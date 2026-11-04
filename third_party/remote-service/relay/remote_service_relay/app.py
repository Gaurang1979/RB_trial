"""support.sundaramtech.com: the remote service relay.

Machines dial in on /link (one outbound WebSocket each; no port is opened at the plant, no remote-desktop software).
A signed-in Sundaram engineer opens https://support.sundaramtech.com/<machine>/ and gets that machine's service
console and pages through the link: the relay forwards the requests and streams the answers back, it never decides
what the engineer may do there (the agent on the machine does). The machine reports every remote event; the relay
keeps them as the session log and sends each finished session to ERPNext.

Design: docs/DESIGN.md; protocol: docs/PROTOCOL.md.
"""

from __future__ import annotations

import asyncio
import base64
from contextlib import asynccontextmanager
import json
import secrets
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from fastapi import FastAPI, Request, WebSocket, WebSocketDisconnect
from starlette.concurrency import run_in_threadpool
from fastapi.responses import FileResponse, JSONResponse, RedirectResponse, Response, StreamingResponse

from .erpnext import ErpNext, outbox_worker
from .store import MACHINE_ID, RESERVED, Account, SessionLog, Store

COOKIE = "sr_session"
LOGIN_TTL = 8 * 3600
LOGIN_IDLE = 30 * 60
MAX_FAILURES = 5
FAILURE_WINDOW = 15 * 60
MAX_BODY = 1024 * 1024 + 4096   # one upload chunk (PROTOCOL.md section 2)
HEAD_TIMEOUT = 20.0
STATIC = Path(__file__).parent / "static"
MAX_FAILURES_PER_ADDRESS = 20   # wrong passwords from one address in FAILURE_WINDOW, whatever the names tried
QUEUE_FRAMES = 64               # chunks waiting for one browser (64 x 32 KB); a browser that stops reading is cut off
# what goes down to the line PC and what comes back; nothing else (no Host, Origin, relay cookie, forwarding headers)
FORWARD = ("accept", "accept-language", "content-type", "last-event-id", "if-none-match")
RETURN = ("content-type", "content-disposition", "cache-control", "etag")
# any other X- header the machine names passes too, except these, which the relay sets itself
OWN_X = ("x-frame-options", "x-content-type-options", "x-forwarded-for", "x-forwarded-proto", "x-forwarded-host")
# set by the relay on every forwarded answer, never taken from the line PC: a line PC cannot loosen them
DASHBOARD_HEADERS = {
    "Content-Security-Policy": "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; "
                               "connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'",
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
    "Referrer-Policy": "no-referrer",
}
SECURITY_HEADERS = {
    "Content-Security-Policy": "default-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'",
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
    "Referrer-Policy": "no-referrer",
}


@dataclass
class Login:
    account: str
    created: float
    seen: float
    opened: set[str] = field(default_factory=set)   # machines opened in this sign-in (audited once each)


class Link:
    """One line PC's WebSocket: requests go down, answers come back as head / data / end frames by request id."""

    def __init__(self, machine_id: str, ws: WebSocket):
        self.machine_id = machine_id
        self.ws = ws
        self.since = time.time()
        self.pending: dict[str, asyncio.Queue] = {}
        self._send = asyncio.Lock()

    async def send(self, frame: dict) -> None:
        async with self._send:
            await self.ws.send_text(json.dumps(frame))

    def deliver(self, frame: dict) -> str | None:
        """Hands a frame to its waiting browser request. Returns the request id to cancel on the line PC when that
        browser is not reading fast enough (its queue is full), else None."""
        rid = str(frame.get("id", ""))
        q = self.pending.get(rid)
        if q is None:
            return None
        try:
            q.put_nowait(frame)
            return None
        except asyncio.QueueFull:
            self.pending.pop(rid, None)
            finish(q, "the browser is not reading")
            return rid

    def close_all(self) -> None:
        for q in list(self.pending.values()):
            finish(q, "link closed")


def finish(q: asyncio.Queue, error: str) -> None:
    """Ends a waiting answer at once: what was queued is dropped, the end is put in its place."""
    while not q.empty():
        q.get_nowait()
    q.put_nowait({"t": "end", "error": error})


async def read_limited(request: Request, limit: int) -> bytes | None:
    """The request body, or None once it is longer than `limit` (never read whole first)."""
    size = request.headers.get("content-length")
    if size is not None and (not size.isdigit() or int(size) > limit):
        return None
    body = bytearray()
    async for chunk in request.stream():
        body += chunk
        if len(body) > limit:
            return None
    return bytes(body)


def create_app(store: Store, secure: bool = True, erp: ErpNext | None = None, outbox: bool = True,
               link_grace: float = 300.0) -> FastAPI:
    """`secure=False` only for tests and a developer's PC: cookies without Secure, plain ws:// accepted."""
    sessions = SessionLog(store)
    wake = asyncio.Event()

    @asynccontextmanager
    async def lifespan(_app: FastAPI):
        task = asyncio.create_task(outbox_worker(store, sessions, erp, wake)) if outbox else None
        try:
            yield
        finally:
            if task:
                task.cancel()

    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)
    app.state.sessions = sessions
    logins: dict[str, Login] = {}
    failures: dict[str, list[float]] = {}
    links: dict[str, Link] = {}
    app.state.links = links
    app.state.store = store

    # ---- helpers ----------------------------------------------------------------------------

    def current(request: Request) -> tuple[str, Login, Account] | None:
        token = request.cookies.get(COOKIE, "")
        s = logins.get(token)
        now = time.time()
        if not s or now - s.created > LOGIN_TTL or now - s.seen > LOGIN_IDLE:
            logins.pop(token, None)
            return None
        account = store.account(s.account)
        if account is None:   # an account taken out is signed out at once
            logins.pop(token, None)
            return None
        s.seen = now
        return token, s, account

    def same_origin(request: Request) -> bool:
        origin = request.headers.get("origin", "")
        if not origin:
            return False   # every browser sends Origin on a POST
        o = urlsplit(origin)
        return o.netloc.lower() == (request.headers.get("host") or "").lower() and (o.scheme == "https" or not secure)

    def page(name: str) -> Response:
        return FileResponse(STATIC / name, headers=SECURITY_HEADERS | {"Cache-Control": "no-store"})

    def locked(key: str, limit: int) -> bool:
        now = time.time()
        recent = [t for t in failures.get(key, []) if now - t < FAILURE_WINDOW]
        if recent:
            failures[key] = recent
        else:
            failures.pop(key, None)
        return len(recent) >= limit

    def sweep() -> None:
        """Drops ended sign-ins and old failure counts, so neither table only grows."""
        now = time.time()
        for token, s in list(logins.items()):
            if now - s.created > LOGIN_TTL or now - s.seen > LOGIN_IDLE:
                logins.pop(token, None)
        for key in list(failures):
            locked(key, 0)

    # ---- the relay's own pages --------------------------------------------------------------

    @app.get("/login")
    def login_page() -> Response:
        return page("login.html")

    @app.get("/static/{name}")
    def static(name: str) -> Response:
        if name not in {"relay.css", "relay.js"}:
            return Response(status_code=404)
        return FileResponse(STATIC / name, headers=SECURITY_HEADERS)

    @app.post("/login")
    async def login(request: Request) -> Response:
        if not same_origin(request):
            return JSONResponse({"error": "Refused."}, status_code=403)
        body = await read_limited(request, 4096)
        if body is None:
            return JSONResponse({"error": "Too large."}, status_code=413)
        form = parse_qs(body.decode("utf-8", "replace"))   # a plain form; no multipart parser needed
        name, password = form.get("name", [""])[0], form.get("password", [""])[0]
        key = name.strip().lower()[:64]
        address = "address:" + (request.client.host if request.client else "")
        if locked(key, MAX_FAILURES) or locked(address, MAX_FAILURES_PER_ADDRESS):
            store.audit(key, "login_locked", detail=address)
            return JSONResponse({"error": "Too many wrong tries. Wait 15 minutes."}, status_code=429)
        # the slow password check off the event loop: it would hold every line PC's link meanwhile
        account = await run_in_threadpool(store.login, key, password)
        if account is None:
            failures.setdefault(key, []).append(time.time())
            failures.setdefault(address, []).append(time.time())
            store.audit(key, "login_failed", detail=address)
            return JSONResponse({"error": "Wrong name or password."}, status_code=401)
        failures.pop(key, None)
        sweep()
        token = secrets.token_urlsafe(32)
        now = time.time()
        logins[token] = Login(account.name, now, now)
        store.audit(account.name, "login")
        res = JSONResponse({"ok": True})
        res.set_cookie(COOKIE, token, max_age=LOGIN_TTL, path="/", secure=secure, httponly=True, samesite="strict")
        return res

    @app.post("/logout")
    def logout(request: Request) -> Response:
        if not same_origin(request):
            return JSONResponse({"error": "Refused."}, status_code=403)
        c = current(request)
        if c:
            logins.pop(c[0], None)
            store.audit(c[2].name, "logout")
        res = JSONResponse({"ok": True})
        res.delete_cookie(COOKIE, path="/")
        return res

    @app.get("/")
    def home(request: Request) -> Response:
        return page("index.html") if current(request) else RedirectResponse("/login", status_code=303)

    @app.get("/api/machines")
    def machines(request: Request) -> Response:
        c = current(request)
        if not c:
            return JSONResponse({"error": "Please sign in."}, status_code=401)
        account = c[2]
        rows = [{"id": m.id, "name": m.name, "site": m.site, "online": m.id in links}
                for m in store.machines() if account.may_open(m.id)]
        return JSONResponse({"user": account.full_name, "machines": rows}, headers={"Cache-Control": "no-store"})

    # ---- line PCs ---------------------------------------------------------------------------

    @app.websocket("/link")
    async def link(ws: WebSocket) -> None:
        # behind the TLS proxy uvicorn takes the scheme from X-Forwarded-Proto, from that proxy only (forwarded_allow_ips)
        if secure and ws.url.scheme != "wss":
            await ws.close(code=1008)
            return
        auth = ws.headers.get("authorization", "")
        token = auth[7:].strip() if auth.lower().startswith("bearer ") else ""
        machine = store.machine_by_token(ws.headers.get("x-machine-id", ""), token)
        if machine is None:
            store.audit("relay", "link_refused", ws.headers.get("x-machine-id", "")[:41])
            await ws.close(code=1008)
            return
        await ws.accept()
        mine = Link(machine.id, ws)
        old = links.get(machine.id)
        links[machine.id] = mine
        if old is not None:   # the line PC dialled again (network change): the newer link wins
            old.close_all()
            try:
                await old.ws.close()
            except (RuntimeError, WebSocketDisconnect):
                pass
        store.audit("relay", "link_up", machine.id)
        try:
            while True:
                text = await ws.receive_text()
                try:
                    frame = json.loads(text)
                except ValueError:
                    continue
                if not isinstance(frame, dict):
                    continue
                if frame.get("t") == "log" and isinstance(frame.get("entry"), dict):
                    # the machine's user log (PROTOCOL.md section 5): kept per session, a finished one goes to ERPNext. A
                    # numbered entry is saved once and confirmed, so the machine can drop it; one sent again is skipped.
                    epoch, seq = str(frame.get("epoch", ""))[:32], frame.get("seq")
                    numbered = isinstance(seq, int) and not isinstance(seq, bool) and seq > 0 and epoch != ""
                    if not numbered or not sessions.already_have(machine.id, epoch, seq):
                        if sessions.add(machine.id, frame["entry"]) is not None:
                            wake.set()
                        if numbered:
                            sessions.saved(machine.id, epoch, seq)
                    if numbered:
                        await mine.send({"t": "ack", "epoch": epoch, "seq": seq})
                elif (slow := mine.deliver(frame)) is not None:
                    await mine.send({"t": "cancel", "id": slow})
        except WebSocketDisconnect:
            pass
        finally:
            mine.close_all()
            if links.get(machine.id) is mine:
                del links[machine.id]
                store.audit("relay", "link_down", machine.id)
                # the machine ends every session when its link drops and sends that end (with its log's chain head) when the
                # link is back; only what is still open after the grace period is ended here, without a head
                asyncio.create_task(end_after_grace(machine.id))

    async def end_after_grace(machine_id: str) -> None:
        await asyncio.sleep(link_grace)
        for open_session in sessions.open_sessions(machine_id):
            if sessions.finish(machine_id, open_session, "link") is not None:
                wake.set()

    # ---- engineers to a line PC's dashboard --------------------------------------------------

    @app.get("/{machine_id}")
    def machine_root(machine_id: str) -> Response:
        if machine_id in RESERVED or not MACHINE_ID.match(machine_id):
            return Response(status_code=404)
        return RedirectResponse(f"/{machine_id}/", status_code=308)

    @app.api_route("/{machine_id}/{path:path}", methods=["GET", "POST"])
    async def forward(machine_id: str, path: str, request: Request) -> Response:
        if machine_id in RESERVED or not MACHINE_ID.match(machine_id):
            return Response(status_code=404)
        c = current(request)
        if not c:
            if request.method == "GET" and wants_page(request):
                return RedirectResponse("/login?next=/" + machine_id + "/", status_code=303)
            return JSONResponse({"error": "Please sign in."}, status_code=401)
        _, login_, account = c
        # a machine the account may not open is answered as one that does not exist
        if not account.may_open(machine_id) or store.machine(machine_id) is None:
            return JSONResponse({"error": "Unknown machine."}, status_code=404)
        if request.method == "POST" and not same_origin(request):
            return JSONResponse({"error": "Refused."}, status_code=403)
        link_ = links.get(machine_id)
        if link_ is None:
            if request.method == "GET" and wants_page(request):
                return page("offline.html")
            return JSONResponse({"error": "This machine is not connected."}, status_code=503)
        body = await read_limited(request, MAX_BODY)
        if body is None:
            return JSONResponse({"error": "Too large."}, status_code=413)
        if machine_id not in login_.opened:
            login_.opened.add(machine_id)
            store.audit(account.name, "machine_opened", machine_id)

        headers = {k: v for k, v in request.headers.items() if k in FORWARD}
        # the machine's own cookies (kept to /<machine>/ by the browser), never the relay's
        line_cookies = "; ".join(f"{k}={v}" for k, v in request.cookies.items() if k != COOKIE)
        if line_cookies:
            headers["cookie"] = line_cookies
        rid = uuid.uuid4().hex
        q: asyncio.Queue = asyncio.Queue(maxsize=QUEUE_FRAMES)
        link_.pending[rid] = q

        async def cancel_on_line() -> None:
            try:
                await link_.send({"t": "cancel", "id": rid})
            except (RuntimeError, WebSocketDisconnect):
                pass

        try:
            await link_.send({"t": "req", "id": rid, "method": request.method, "path": line_path(request, machine_id),
                              "query": ("?" + request.url.query) if request.url.query else "", "headers": headers,
                              "user": account.name, "role": account.role, "body": base64.b64encode(body).decode()})
            head = await asyncio.wait_for(q.get(), HEAD_TIMEOUT)
        except asyncio.CancelledError:
            # the browser left before the answer began: the line PC stops too
            link_.pending.pop(rid, None)
            await asyncio.shield(cancel_on_line())
            raise
        except (asyncio.TimeoutError, RuntimeError, WebSocketDisconnect):
            link_.pending.pop(rid, None)
            await cancel_on_line()
            return JSONResponse({"error": "The machine did not answer."}, status_code=504)
        if head.get("t") != "head":
            link_.pending.pop(rid, None)
            return JSONResponse({"error": head.get("error") or "The machine could not answer."}, status_code=502)

        status = int(head.get("status", 502))
        if path == "_rs/sign-in" and request.method == "POST":
            store.audit(account.name, "service_sign_in" if status == 200 else "service_sign_in_refused", machine_id)
        elif path == "_rs/sign-out" and request.method == "POST":
            store.audit(account.name, "service_sign_out", machine_id)

        res_headers: list[tuple[str, str]] = []
        for name, values in (head.get("headers") or {}).items():
            n = name.lower()
            for v in values if isinstance(values, list) else [values]:
                if n == "set-cookie":
                    cookie = rewrite_cookie(str(v), machine_id, secure)
                    if cookie:
                        res_headers.append(("set-cookie", cookie))
                elif n == "location":
                    # a redirect inside this machine only (a relative address); never to another site or machine
                    if relative_location(str(v)):
                        res_headers.append((n, str(v)))
                elif n in RETURN or (n.startswith("x-") and n not in OWN_X and n not in (k.lower() for k in DASHBOARD_HEADERS)):
                    res_headers.append((n, str(v)))

        async def body_stream():
            ended = False
            try:
                while True:
                    frame = await q.get()
                    if frame.get("t") == "data":
                        yield base64.b64decode(frame.get("b64", ""))
                    elif frame.get("t") == "end":
                        ended = True
                        return
            finally:
                # the browser went away before the end (a closed live page): stop the line PC's side too
                link_.pending.pop(rid, None)
                if not ended:
                    await cancel_on_line()

        res = StreamingResponse(body_stream(), status_code=status)
        for n, v in res_headers:
            res.raw_headers.append((n.encode("latin-1"), v.encode("latin-1")))
        for n, v in DASHBOARD_HEADERS.items():
            res.raw_headers.append((n.lower().encode(), v.encode()))
        if not any(n == "cache-control" for n, _ in res_headers):
            res.raw_headers.append((b"cache-control", b"no-store"))
        return res

    return app


def line_path(request: Request, machine_id: str) -> str:
    """The path on the line PC, still encoded as the browser sent it (a camera name may hold a space or a '/')."""
    raw = (request.scope.get("raw_path") or request.url.path.encode()).decode("latin-1")
    parts = raw.split("/", 2)   # "", the machine (as sent, maybe percent-encoded), the rest
    return "/" + (parts[2] if len(parts) > 2 else "")


def relative_location(v: str) -> bool:
    """A Location that stays under /<machine>/: no scheme, no host, not rooted, never "..", which could climb to another
    machine ("_rs/ui/" yes; "../../M-2/" no)."""
    v = v.strip()
    head = v.split("?", 1)[0].split("#", 1)[0]
    return bool(v) and not v.startswith(("/", "\\")) and ":" not in head.split("/", 1)[0] and "\\" not in v \
        and not any(c in v for c in "\r\n") and ".." not in head.split("/") and "%2e" not in head.lower()


def wants_page(request: Request) -> bool:
    """A browser opening a page (sign-in redirect, offline page) rather than a script calling an API (a JSON error)."""
    return "text/html" in request.headers.get("accept", "")


def rewrite_cookie(cookie: str, machine_id: str, secure: bool) -> str | None:
    """A machine's cookie, kept to that machine's path on the relay: two machines open in one browser never share a
    cookie. A machine can never set the relay's own cookie."""
    parts = [p.strip() for p in cookie.split(";") if p.strip()]
    if not parts or "=" not in parts[0] or parts[0].split("=", 1)[0].strip() in ("", COOKIE):
        return None
    attrs = [p for p in parts[1:] if p.split("=", 1)[0].strip().lower() not in ("path", "domain", "secure", "samesite")]
    out = [parts[0], f"path=/{machine_id}/", "samesite=strict"] + attrs
    if secure:
        out.append("secure")
    return "; ".join(out)
