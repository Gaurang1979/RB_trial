"""Tests the full Web Push send path (VAPID signing + aes128gcm encryption + HTTP POST) against a throwaway
local HTTP server standing in for a browser vendor's push service, decrypting what was actually sent with an
independently generated 'browser' key pair. This proves protocol correctness; it cannot prove delivery to a
real phone, which needs an HTTPS deployment and a real device - see backend/webpush.py's module docstring.
"""
import base64
import http.server
import json
import os
import threading
import time

import http_ece
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

from backend.webpush import VapidKeys, WebPushNotifier


def b64u(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).decode().rstrip("=")


class FakeBrowser:
    """Stands in for a real browser's push subscription: generates its own EC key pair and auth secret,
    exposes them the way navigator.pushManager.subscribe() would, and can decrypt what was sent to it."""

    def __init__(self):
        self.private_key = ec.generate_private_key(ec.SECP256R1())
        self.auth_secret = os.urandom(16)
        raw_pub = self.private_key.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)
        self.p256dh, self.auth = b64u(raw_pub), b64u(self.auth_secret)

    def subscription(self, endpoint: str) -> dict:
        return {"endpoint": endpoint, "p256dh": self.p256dh, "auth": self.auth}

    def decrypt(self, ciphertext: bytes) -> dict:
        return json.loads(http_ece.decrypt(ciphertext, private_key=self.private_key, auth_secret=self.auth_secret, version="aes128gcm"))


class _CapturingPushService(http.server.BaseHTTPRequestHandler):
    captured: list = []
    status_to_return = 201

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        _CapturingPushService.captured.append({"path": self.path, "headers": dict(self.headers), "body": body})
        self.send_response(_CapturingPushService.status_to_return)
        self.end_headers()

    def log_message(self, *a):
        pass


@pytest.fixture
def push_service():
    _CapturingPushService.captured = []
    _CapturingPushService.status_to_return = 201
    srv = http.server.HTTPServer(("127.0.0.1", 0), _CapturingPushService)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    yield srv, f"http://127.0.0.1:{srv.server_port}/push/sub-1"
    srv.shutdown()


def wait_for(cond, timeout=10):
    end = time.time() + timeout
    while time.time() < end:
        if cond():
            return True
        time.sleep(0.02)
    return False


def test_vapid_keys_generate_a_browser_ready_public_key():
    keys = VapidKeys.generate()
    raw = base64.urlsafe_b64decode(keys.public_key_b64 + "==")
    assert len(raw) == 65 and raw[0:1] == b"\x04"          # uncompressed P-256 point: 0x04 + 32 + 32 bytes


def test_keys_persisted_to_disk_are_stable_across_reloads(tmp_path):
    path = str(tmp_path / "vapid.pem")
    a = VapidKeys.load_or_create(path)
    b = VapidKeys.load_or_create(path)
    assert a.public_key_b64 == b.public_key_b64                # same key on reload: existing subscriptions stay valid


def test_payload_round_trips_through_real_encryption_and_send(push_service):
    srv, endpoint = push_service
    browser = FakeBrowser()
    notifier = WebPushNotifier(VapidKeys.generate(), contact="mailto:ops@example.com")
    payload = {"title": "Press 01 stopped", "body": "fault signal active for 3 min", "tag": "spi-alert-1", "severity": "high"}
    results = []
    notifier.send_to_subscription(browser.subscription(endpoint), payload, lambda *a: results.append(a))
    assert wait_for(lambda: results)
    assert results[0][0] is True
    assert len(_CapturingPushService.captured) == 1
    got = browser.decrypt(_CapturingPushService.captured[0]["body"])
    assert got == payload


def test_vapid_authorization_header_is_present_and_signed_for_the_right_origin(push_service):
    srv, endpoint = push_service
    browser = FakeBrowser()
    notifier = WebPushNotifier(VapidKeys.generate())
    results = []
    notifier.send_to_subscription(browser.subscription(endpoint), {"title": "t", "body": "b", "tag": "x"}, lambda *a: results.append(a))
    assert wait_for(lambda: results)
    hdrs = {k.lower(): v for k, v in _CapturingPushService.captured[0]["headers"].items()}
    assert hdrs.get("authorization", "").startswith("vapid ")
    assert hdrs.get("content-encoding") == "aes128gcm"


def test_gone_subscription_is_reported_as_gone_for_cleanup(push_service):
    srv, endpoint = push_service
    _CapturingPushService.status_to_return = 410                # browser revoked the subscription
    browser = FakeBrowser()
    notifier = WebPushNotifier(VapidKeys.generate())
    results = []
    notifier.send_to_subscription(browser.subscription(endpoint), {"title": "t", "body": "b", "tag": "x"}, lambda *a: results.append(a))
    assert wait_for(lambda: results)
    ok, detail, gone = results[0]
    assert ok is False and gone is True


def test_unreachable_push_service_fails_without_raising():
    notifier = WebPushNotifier(VapidKeys.generate(), timeout=0.5)
    browser = FakeBrowser()
    results = []
    notifier.send_to_subscription(browser.subscription("http://127.0.0.1:9/dead"), {"title": "t", "body": "b", "tag": "x"},
                                  lambda *a: results.append(a))
    assert wait_for(lambda: results, timeout=15)
    ok, detail, gone = results[0]
    assert ok is False and gone is False and detail


def test_each_subscriber_gets_a_separately_encrypted_payload_only_they_can_read(push_service):
    srv, endpoint = push_service
    alice, bob = FakeBrowser(), FakeBrowser()
    notifier = WebPushNotifier(VapidKeys.generate())
    results = []
    notifier.send_to_subscription(alice.subscription(endpoint), {"title": "for alice", "body": "b", "tag": "x"}, lambda *a: results.append(("alice", a)))
    notifier.send_to_subscription(bob.subscription(endpoint), {"title": "for bob", "body": "b", "tag": "x"}, lambda *a: results.append(("bob", a)))
    assert wait_for(lambda: len(results) == 2)
    assert len(_CapturingPushService.captured) == 2
    decrypted_titles = set()
    for cap in _CapturingPushService.captured:
        for browser in (alice, bob):
            try:
                decrypted_titles.add(browser.decrypt(cap["body"])["title"])
                break
            except Exception:
                continue
    assert decrypted_titles == {"for alice", "for bob"}
    with pytest.raises(Exception):
        alice.decrypt(b"not a valid aes128gcm record")
