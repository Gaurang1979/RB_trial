"""Relay tests: sign-in, which machines an engineer may open, the line PC link and the forwarding through it.

A fake line PC connects on /link exactly as the runtime's SupportLink does and answers the forwarded requests.
"""

from __future__ import annotations

import base64
import json
import threading

import pytest
from fastapi.testclient import TestClient

from remote_service_relay.app import DASHBOARD_HEADERS, Link, create_app, relative_location, rewrite_cookie
from remote_service_relay.store import Store

ORIGIN = {"origin": "http://testserver"}
PAGE = {"accept": "text/html,application/xhtml+xml"}
PASSWORD = "correct-horse-1"


@pytest.fixture()
def env(tmp_path):
    store = Store(tmp_path)
    tokens = {"M-1001": store.add_machine("M-1001", "Line 1", "Pune"), "M-1002": store.add_machine("M-1002", "Line 2", "Chennai")}
    store.save_account("anita", "Anita", PASSWORD, ["M-1001"], employee="HR-EMP-0007")
    store.save_account("ravi", "Ravi", PASSWORD, ["*"], role="oem")
    with TestClient(create_app(store, secure=False, outbox=False, link_grace=0)) as client:
        yield client, store, tokens


def sign_in(client, name="anita", password=PASSWORD):
    return client.post("/login", data={"name": name, "password": password}, headers=ORIGIN)


class FakeLine:
    """Answers each forwarded request with `answer(frame) -> list of frames`, in its own thread."""

    def __init__(self, client, machine, token, answer):
        self.frames: list[dict] = []
        self._cm = client.websocket_connect("/link", headers={"x-machine-id": machine, "authorization": "Bearer " + token})
        self.ws = self._cm.__enter__()
        self.answer = answer
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self):
        try:
            while True:
                frame = self.ws.receive_json()
                self.frames.append(frame)
                for out in self.answer(frame):
                    self.ws.send_json(out)
        except Exception:
            pass

    def close(self):
        self._cm.__exit__(None, None, None)


def ok_page(frame):
    if frame["t"] != "req":
        return []
    rid = frame["id"]
    return [
        {"t": "head", "id": rid, "status": 200, "headers": {"Content-Type": ["text/plain"],
                                                            "Set-Cookie": ["vs_web=ABC; path=/; httponly; samesite=strict"],
                                                            "Server": ["Kestrel"]}},
        {"t": "data", "id": rid, "b64": base64.b64encode(b"hello ").decode()},
        {"t": "data", "id": rid, "b64": base64.b64encode(b"line").decode()},
        {"t": "end", "id": rid},
    ]


def test_wrong_password_and_lockout(env):
    client, store, _ = env
    for _ in range(5):
        assert sign_in(client, password="wrong-password").status_code == 401
    assert sign_in(client).status_code == 429   # locked even with the right password
    assert [e["event"] for e in store.audit_entries()].count("login_failed") == 5


def test_login_needs_same_origin(env):
    client, _, _ = env
    r = client.post("/login", data={"name": "anita", "password": PASSWORD})
    assert r.status_code == 403


def test_machine_list_shows_only_assigned_machines(env):
    client, _, tokens = env
    assert client.get("/api/machines").status_code == 401
    sign_in(client)
    line = FakeLine(client, "M-1001", tokens["M-1001"], ok_page)
    try:
        d = client.get("/api/machines").json()
        assert [(m["id"], m["online"]) for m in d["machines"]] == [("M-1001", True)]
    finally:
        line.close()


def test_link_refused_with_wrong_token(env):
    client, store, _ = env
    with pytest.raises(Exception):
        with client.websocket_connect("/link", headers={"x-machine-id": "M-1001", "authorization": "Bearer nope"}) as ws:
            ws.receive_text()
    assert store.audit_entries()[-1]["event"] == "link_refused"


def test_secure_relay_refuses_plain_ws(tmp_path):
    store = Store(tmp_path)
    token = store.add_machine("M-1001", "Line 1", "")
    with TestClient(create_app(store, secure=True)) as client:
        with pytest.raises(Exception):
            with client.websocket_connect("/link", headers={"x-machine-id": "M-1001", "authorization": "Bearer " + token}) as ws:
                ws.receive_text()


def test_forwards_to_line_and_keeps_cookie_per_machine(env):
    client, store, tokens = env
    sign_in(client)
    line = FakeLine(client, "M-1001", tokens["M-1001"], ok_page)
    try:
        client.cookies.set("vs_web", "LINE-SESSION", path="/M-1001/")
        client.cookies.set("other", "x")
        r = client.get("/M-1001/web/api/frame/Top%20cam?x=1")
        assert r.status_code == 200 and r.text == "hello line"
        req = line.frames[-1]
        assert req["path"] == "/web/api/frame/Top%20cam"   # still encoded as the browser sent it
        assert req["query"] == "?x=1"
        assert req["user"] == "anita"
        assert req["role"] == "service"
        assert "vs_web=LINE-SESSION" in req["headers"]["cookie"]
        assert "sr_session" not in req["headers"]["cookie"]   # never the relay's own cookie
        assert "host" not in req["headers"] and "origin" not in req["headers"]
        cookie = r.headers["set-cookie"]
        assert "path=/M-1001/" in cookie and "vs_web=ABC" in cookie
        assert "server" not in r.headers
        assert any(e["event"] == "machine_opened" and e["machine"] == "M-1001" for e in store.audit_entries())
    finally:
        line.close()


def test_machine_not_assigned_is_unknown(env):
    client, _, tokens = env
    sign_in(client)
    line = FakeLine(client, "M-1002", tokens["M-1002"], ok_page)
    try:
        assert client.get("/M-1002/").status_code == 404
        assert line.frames == []
    finally:
        line.close()


def test_offline_machine(env):
    client, _, _ = env
    sign_in(client)
    assert "not connected" in client.get("/M-1001/", headers=PAGE).text
    assert client.get("/M-1001/web/api/me").status_code == 503


def test_not_signed_in_goes_to_login(env):
    client, _, _ = env
    r = client.get("/M-1001/", headers=PAGE, follow_redirects=False)
    assert r.status_code == 303 and r.headers["location"].startswith("/login")
    assert client.get("/M-1001/web/api/me").status_code == 401
    assert client.get("/M-1001", follow_redirects=False).headers["location"] == "/M-1001/"


def test_post_needs_same_origin_and_service_sign_in_is_audited(env):
    client, store, tokens = env
    sign_in(client)
    line = FakeLine(client, "M-1001", tokens["M-1001"], ok_page)
    try:
        assert client.post("/M-1001/_rs/sign-in", data={"code": "123456"}).status_code == 403
        r = client.post("/M-1001/_rs/sign-in", data={"code": "123456"}, headers=ORIGIN)
        assert r.status_code == 200
        assert base64.b64decode(line.frames[-1]["body"]) == b"code=123456"
        assert store.audit_entries()[-1]["event"] == "service_sign_in"
    finally:
        line.close()


def test_closed_stream_cancels_on_line(tmp_path):
    """A browser that closes a live page: the relay tells the line PC to stop that answer (real server and sockets)."""
    import http.client
    import json
    import socket
    import time
    import urllib.parse

    import uvicorn
    from websockets.sync.client import connect

    store = Store(tmp_path)
    token = store.add_machine("M-1001", "Line 1", "")
    store.save_account("anita", "Anita", PASSWORD, ["M-1001"])
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    server = uvicorn.Server(uvicorn.Config(create_app(store, secure=False), host="127.0.0.1", port=port, log_level="warning"))
    threading.Thread(target=server.run, daemon=True).start()
    for _ in range(200):
        if server.started:
            break
        time.sleep(0.02)
    frames: list[dict] = []
    line = connect(f"ws://127.0.0.1:{port}/link", additional_headers={"x-machine-id": "M-1001", "authorization": "Bearer " + token})

    def answer():
        try:
            for text in line:
                f = json.loads(text)
                frames.append(f)
                if f["t"] == "req":
                    line.send(json.dumps({"t": "head", "id": f["id"], "status": 200, "headers": {"Content-Type": ["text/event-stream"]}}))
                    line.send(json.dumps({"t": "data", "id": f["id"], "b64": base64.b64encode(b"data: 1\n\n").decode()}))
        except Exception:
            pass

    threading.Thread(target=answer, daemon=True).start()
    try:
        c = http.client.HTTPConnection("127.0.0.1", port)
        c.request("POST", "/login", urllib.parse.urlencode({"name": "anita", "password": PASSWORD}),
                  {"Origin": f"http://127.0.0.1:{port}", "Content-Type": "application/x-www-form-urlencoded"})
        r = c.getresponse()
        cookie = r.getheader("set-cookie").split(";")[0]
        r.read()
        c.request("GET", "/M-1001/web/api/live", headers={"Cookie": cookie})
        r = c.getresponse()
        assert r.status == 200 and r.read1(100) == b"data: 1\n\n"
        c.sock.shutdown(socket.SHUT_RDWR)   # the browser closes the page
        c.close()
        for _ in range(150):
            if any(f["t"] == "cancel" for f in frames):
                break
            time.sleep(0.02)
        assert any(f["t"] == "cancel" for f in frames)
    finally:
        line.close()
        server.should_exit = True


def test_relay_sets_its_own_page_headers_never_the_lines(env):
    client, _, tokens = env
    sign_in(client)

    def loose(frame):
        if frame["t"] != "req":
            return []
        return [{"t": "head", "id": frame["id"], "status": 200,
                 "headers": {"Content-Type": ["text/html"], "Content-Security-Policy": ["default-src *"], "X-Frame-Options": ["ALLOWALL"]}},
                {"t": "end", "id": frame["id"]}]

    line = FakeLine(client, "M-1001", tokens["M-1001"], loose)
    try:
        r = client.get("/M-1001/")
        assert r.headers["content-security-policy"] == DASHBOARD_HEADERS["Content-Security-Policy"]
        assert r.headers["x-frame-options"] == "DENY"
    finally:
        line.close()


def test_a_browser_that_stops_reading_is_cut_off_and_the_line_told():
    import asyncio

    link = Link("M-1001", ws=None)   # type: ignore[arg-type]
    q: asyncio.Queue = asyncio.Queue(maxsize=2)
    link.pending["r1"] = q
    assert link.deliver({"t": "data", "id": "r1"}) is None
    assert link.deliver({"t": "data", "id": "r1"}) is None
    assert link.deliver({"t": "data", "id": "r1"}) == "r1"   # full: cancel r1 on the line PC
    assert "r1" not in link.pending
    assert q.get_nowait() == {"t": "end", "error": "the browser is not reading"}


def test_login_limits(env):
    client, store, _ = env
    assert client.post("/login", content=b"name=a&password=" + b"x" * 5000, headers=ORIGIN | {"content-type": "application/x-www-form-urlencoded"}).status_code == 413
    for i in range(20):   # rotating names: the address is still counted
        sign_in(client, name=f"nobody{i}", password="wrong-password")
    assert sign_in(client).status_code == 429


def test_reserved_names_are_never_machines(env):
    _, store, _ = env
    for name in ("api", "login", "link", "Static"):
        with pytest.raises(ValueError):
            store.add_machine(name, "x", "")


def test_rewrite_cookie():
    assert rewrite_cookie("vs_web=T; Path=/; HttpOnly; SameSite=Strict", "M-1", True) == "vs_web=T; path=/M-1/; samesite=strict; HttpOnly; secure"
    assert rewrite_cookie("sr_session=x; path=/", "M-1", True) is None   # never the relay's own
    assert rewrite_cookie("rs_session=T; Path=/; HttpOnly", "M-1", True) == "rs_session=T; path=/M-1/; samesite=strict; HttpOnly; secure"
    assert rewrite_cookie("vs_web=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT", "M-1", False).startswith("vs_web=; path=/M-1/")


def log_frame(session, kind, detail="", data=None, time="2026-11-04T10:00:00Z", user="anita"):
    return {"t": "log", "entry": {"time": time, "session": session, "user": user, "role": "service", "kind": kind,
                                  "detail": detail, "data": data or {}}}


def test_machine_log_becomes_a_session_record_in_the_outbox(env):
    client, store, tokens = env
    line = FakeLine(client, "M-1001", tokens["M-1001"], lambda f: [])
    try:
        sid = "a1b2c3d4e5f60718"
        line.ws.send_json(log_frame(sid, "session_start", "started", {"reason": "Call 4711"}))
        line.ws.send_json(log_frame(sid, "file_read", "Downloaded logs/today.log", time="2026-11-04T10:05:00Z"))
        line.ws.send_json(log_frame(sid, "session_end", "ended", {"endReason": "sign-out"}, time="2026-11-04T10:42:00Z"))
        sessions = client.app.state.sessions
        for _ in range(100):
            if sessions.pending():
                break
            threading.Event().wait(0.02)
        rec = json.loads(sessions.pending()[0].read_text())
        assert rec["session_id"] == sid and rec["machine"] == "M-1001" and rec["employee"] == "HR-EMP-0007"
        assert rec["reason"] == "Call 4711" and rec["started"] == "2026-11-04T10:00:00Z" and rec["ended"] == "2026-11-04T10:42:00Z"
        assert rec["end_reason"] == "sign-out"
        assert [a["kind"] for a in rec["actions"]] == ["file_read"]
        # a second end never makes a second record
        assert sessions.finish("M-1001", sid, "link") is None
    finally:
        line.close()


def test_link_drop_ends_open_sessions(env):
    client, store, tokens = env
    line = FakeLine(client, "M-1001", tokens["M-1001"], lambda f: [])
    sid = "0011223344556677"
    line.ws.send_json(log_frame(sid, "session_start", "started", {"reason": "x"}))
    sessions = client.app.state.sessions
    for _ in range(100):
        if sessions.open_sessions("M-1001"):
            break
        threading.Event().wait(0.02)
    line.close()
    for _ in range(100):
        if sessions.pending():
            break
        threading.Event().wait(0.02)
    assert json.loads(sessions.pending()[0].read_text())["end_reason"] == "link"


def test_bad_log_entries_are_ignored(tmp_path):
    from remote_service_relay.store import SessionLog

    log = SessionLog(Store(tmp_path))
    assert log.add("M-1001", {"session": "../../etc", "kind": "session_end"}) is None
    assert log.add("../x", {"session": "a1b2c3d4", "kind": "session_start"}) is None
    assert not any((tmp_path / "sessions").rglob("*.jsonl"))


def test_outbox_goes_to_erpnext_once_and_waits_while_it_is_away(tmp_path):
    import asyncio
    import http.server
    import socket

    from remote_service_relay.erpnext import ErpNext, outbox_worker
    from remote_service_relay.store import SessionLog

    got: list[dict] = []
    state = {"up": False}

    class Fake(http.server.BaseHTTPRequestHandler):
        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            assert self.headers["Authorization"] == "token k:s"
            if not state["up"]:
                self.send_response(503)
                self.end_headers()
                return
            got.append(json.loads(body["session"]))
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"message": {"ok": true}}')

        def log_message(self, *a):
            pass

    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    server = http.server.HTTPServer(("127.0.0.1", port), Fake)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    store = Store(tmp_path)
    store.add_machine("M-1001", "Line 1", "Pune", "LIC-1")
    log = SessionLog(store)
    log.add("M-1001", log_frame("aa11bb22", "session_start", data={"reason": "r"})["entry"])
    log.add("M-1001", log_frame("aa11bb22", "session_end", data={"endReason": "idle"})["entry"])
    erp = ErpNext(f"http://127.0.0.1:{port}", "k", "s")

    async def run():
        wake = asyncio.Event()
        task = asyncio.create_task(outbox_worker(store, log, erp, wake, min_wait=0.05, max_wait=0.1))
        await asyncio.sleep(0.3)
        assert got == [] and len(log.pending()) == 1   # ERPNext away: kept
        state["up"] = True
        for _ in range(60):
            await asyncio.sleep(0.05)
            if not log.pending():
                break
        task.cancel()

    try:
        asyncio.run(run())
    finally:
        server.shutdown()
    assert [g["session_id"] for g in got] == ["aa11bb22"] and got[0]["licence"] == "LIC-1"
    assert not log.pending() and list((tmp_path / "sent").glob("*.json"))
    assert "erpnext_failed" in [e["event"] for e in store.audit_entries()]


def test_redirects_stay_inside_the_machine():
    assert relative_location("_rs/ui/") and relative_location("ui/?y=1")
    for bad in ("../x", "../../M-2002/", "_rs/%2e%2e/%2e%2e/M-2002/", "https://evil.example/", "//evil.example/x", "/M-2002/", "javascript:alert(1)", "\\\\evil", "a\r\nSet-Cookie: x=1", ""):
        assert not relative_location(bad), bad


def test_numbered_entries_are_saved_once_and_confirmed_and_the_chain_head_is_kept(env):
    client, store, tokens = env
    acks = []
    line = FakeLine(client, "M-1001", tokens["M-1001"], lambda f: (acks.append(f) or []) if f.get("t") == "ack" else [])
    try:
        sid = "c0ffee0011223344"
        frames = [
            log_frame(sid, "session_start", "started", {"reason": "Call 4714"}) | {"epoch": "e1", "seq": 1},
            log_frame(sid, "file_read", "Downloaded logs/today.log") | {"epoch": "e1", "seq": 2},
        ]
        for fr in frames + frames:   # the machine sent them again after a link drop
            line.ws.send_json(fr)
        line.ws.send_json(log_frame(sid, "session_end", "ended", {"endReason": "sign-out", "chainHead": "ab" * 32},
                                    time="2026-11-04T10:30:00Z") | {"epoch": "e1", "seq": 3})
        sessions = client.app.state.sessions
        for _ in range(100):
            if sessions.pending() and len(acks) >= 5:
                break
            threading.Event().wait(0.02)
        assert [a["seq"] for a in acks] == [1, 2, 1, 2, 3]   # every copy confirmed, so the machine drops them
        assert [e["kind"] for e in sessions.entries("M-1001", sid)] == ["session_start", "file_read", "session_end"]   # saved once
        rec = json.loads(sessions.pending()[0].read_text())
        assert rec["ledger_head"] == "ab" * 32
        anchors = (store.folder / "anchors" / "M-1001.jsonl").read_text().splitlines()
        assert json.loads(anchors[-1]) == {"time": "2026-11-04T10:30:00Z", "session": sid, "head": "ab" * 32}
    finally:
        line.close()


def test_numbers_are_remembered_across_a_relay_restart(tmp_path):
    from remote_service_relay.store import SessionLog

    store = Store(tmp_path)
    store.add_machine("M-1001", "Line 1", "")
    a = SessionLog(store)
    a.saved("M-1001", "e1", 7)
    b = SessionLog(Store(tmp_path))
    assert b.already_have("M-1001", "e1", 7) and not b.already_have("M-1001", "e1", 8) and not b.already_have("M-1001", "e2", 1)


def test_after_a_drop_the_machines_own_end_with_its_head_wins_within_the_grace_period(tmp_path):
    store = Store(tmp_path)
    token = store.add_machine("M-1001", "Line 1", "")
    with TestClient(create_app(store, secure=False, outbox=False, link_grace=0.5)) as client:
        sessions = client.app.state.sessions
        sid = "beefbeef00112233"
        line = FakeLine(client, "M-1001", token, lambda f: [])
        line.ws.send_json(log_frame(sid, "session_start", data={"reason": "x"}) | {"epoch": "e1", "seq": 1})
        for _ in range(100):
            if sessions.open_sessions("M-1001"):
                break
            threading.Event().wait(0.02)
        line.close()                                   # the link drops
        line = FakeLine(client, "M-1001", token, lambda f: [])   # and is back
        line.ws.send_json(log_frame(sid, "session_end", data={"endReason": "link", "chainHead": "cd" * 32}) | {"epoch": "e1", "seq": 2})
        for _ in range(100):
            if sessions.pending():
                break
            threading.Event().wait(0.02)
        threading.Event().wait(0.8)                    # past the grace period: no second record
        line.close()
        assert len(sessions.pending()) == 1
        assert json.loads(sessions.pending()[0].read_text())["ledger_head"] == "cd" * 32
