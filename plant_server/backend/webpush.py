"""Web Push notifications (M4b): deliver alerts to a subscribed browser/phone even when the dashboard
tab is closed, via the browser vendor's push service (FCM, Mozilla autopush, ...), using VAPID (RFC 8292)
for sender identification and aes128gcm (RFC 8291) for payload encryption. No third-party server of ours
sits in the path; the browser vendor's push service does.

What is proven in this sandbox: the full encrypt-and-send path is exercised end to end against a throwaway
local HTTP server standing in for the push service, and the resulting ciphertext is independently decrypted
with a locally generated "browser" key pair to confirm it round-trips correctly (see tests/test_webpush.py).
What is NOT proven here, for lack of a real device: delivery through an actual push service to an actual
phone. That needs an HTTPS deployment and a real subscription; verify it there before relying on it.
"""
from __future__ import annotations

import base64
import json
import threading
from dataclasses import dataclass
from pathlib import Path

from py_vapid import Vapid02
from pywebpush import WebPushException, webpush


def _b64u(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).decode().rstrip("=")


@dataclass
class VapidKeys:
    vapid: Vapid02
    public_key_b64: str  # raw uncompressed EC point, base64url - what the browser's pushManager.subscribe() needs

    @classmethod
    def generate(cls) -> "VapidKeys":
        v = Vapid02()
        v.generate_keys()
        return cls.from_vapid(v)

    @classmethod
    def from_vapid(cls, v: Vapid02) -> "VapidKeys":
        from cryptography.hazmat.primitives import serialization
        raw = v.public_key.public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)
        return cls(vapid=v, public_key_b64=_b64u(raw))

    @classmethod
    def load_or_create(cls, path: str) -> "VapidKeys":
        """Keys must stay stable across restarts, or every browser subscription made against the old key
        becomes invalid (the browser ties a subscription to the applicationServerKey it was created with)."""
        p = Path(path)
        if p.exists():
            v = Vapid02.from_file(str(p))          # a classmethod: returns a new instance, does not mutate one
        else:
            v = Vapid02()
            v.generate_keys()
            p.parent.mkdir(parents=True, exist_ok=True)
            v.save_key(str(p))
        return cls.from_vapid(v)


class WebPushNotifier:
    """Implements the same `.send(payload, done)` interface as the other alert-engine notifiers
    (see backend/alerts.py), but for a single subscription rather than a bare recipient string.
    `send_to_subscription` is what the alert engine actually calls (see AlertEngine._notify_webpush)."""

    def __init__(self, keys: VapidKeys, contact: str = "mailto:ops@example.com", ttl: int = 3600, timeout: float = 5.0):
        self.keys, self.contact, self.ttl, self.timeout = keys, contact, ttl, timeout
        self._pool = threading.BoundedSemaphore(8)  # cap concurrent sends; a slow/dead endpoint never blocks alerting

    def send_to_subscription(self, subscription: dict, payload: dict, done) -> None:
        threading.Thread(target=self._send, args=(subscription, payload, done), daemon=True).start()

    def _send(self, subscription: dict, payload: dict, done) -> None:
        with self._pool:
            info = {"endpoint": subscription["endpoint"], "keys": {"p256dh": subscription["p256dh"], "auth": subscription["auth"]}}
            try:
                webpush(subscription_info=info, data=json.dumps(payload), vapid_private_key=self.keys.vapid,
                        vapid_claims={"sub": self.contact}, ttl=self.ttl, timeout=self.timeout)
                done(True, "delivered to push service", False)
            except WebPushException as exc:
                status = getattr(exc.response, "status_code", None)
                gone = status in (404, 410)  # subscription expired or was revoked by the browser
                done(False, f"HTTP {status}: {exc}"[:300], gone)
            except Exception as exc:  # network errors etc. - never raised into the alert engine
                done(False, repr(exc)[:300], False)
