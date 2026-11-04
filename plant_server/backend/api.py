"""HTTP + WebSocket API over the live engine (spec s42).

Security: two layers, independent and combinable.
  - Service credential: a shared API key (header X-API-Key, or ?api_key= for WebSocket) grants full access,
    unchanged since M3 - convenient for gateways/ops scripts and for a demo with no accounts configured.
  - User accounts (M4b): sign in via /auth/login for a bearer session token (Authorization: Bearer <token>,
    or ?token= for WebSocket). Access is then scoped by the user's role assignments (backend/auth.py) - a
    plant_manager assigned to one plant cannot see another plant's lines or alerts.
A server with no API key and no user accounts behaves exactly as in M1-M3: fully open (local/dev use).
Bind to localhost or put behind a TLS reverse proxy for anything beyond that; do not expose it to the
internet as is.
"""
from __future__ import annotations

import asyncio
import hmac
import threading
import time
from collections import defaultdict
from pathlib import Path

from fastapi import Depends, FastAPI, Header, HTTPException, Query, Request, WebSocket, WebSocketDisconnect
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import FileResponse
from pydantic import AwareDatetime, BaseModel, Field

from spi_common.schema import format_ts, parse_ts

from . import audit
from .auth import (ADMIN, PIN_EVENTS, SESSION_IDLE_S, LoginThrottle, Principal, hash_password, idle_exempt, login, logout,
                   normalize_role, pin_event, resolve_token, sweep_sessions, verify_password)
from .ingest import gaps
from .master import DEVICE_KINDS, check_id, issue_token, public_device
from .kpi import counter_at, downtime_events, line_kpi

DASHBOARD = Path(__file__).resolve().parent.parent / "dashboard" / "index.html"
BUCKETS = {"minute": 60, "hour": 3600, "day": 86400}
STATES = {"OPEN", "ACKNOWLEDGED", "RESOLVED"}
STATIC = {"manifest.webmanifest": "application/manifest+json", "sw.js": "application/javascript", "icon-192.png": "image/png", "icon-512.png": "image/png"}


class AckBody(BaseModel):
    user: str = Field(default="", max_length=80)   # ignored when signed in: the session's username is used instead
    note: str = Field(default="", max_length=500)


class ResolveBody(BaseModel):
    user: str = Field(default="", max_length=80)
    reason: str = Field(min_length=3, max_length=500)


class LoginBody(BaseModel):
    username: str = Field(min_length=1, max_length=80)
    password: str = Field(min_length=1, max_length=200)
    device: str | None = Field(default=None, max_length=200)   # for the audit trail: phone or screen name and ID


class PinEventBody(BaseModel):
    event: str = Field(pattern="^(" + "|".join(PIN_EVENTS) + ")$")
    device: str | None = Field(default=None, max_length=200)
    at: AwareDatetime | None = None   # PT11-08b: the phone's time of the event, ISO 8601 with a zone; kept in the audit entry


class DisableBody(BaseModel):
    reason: str = Field(min_length=3, max_length=500)   # PT11-22: stored with the audit entry


class ReasonBody(DisableBody):
    """Activate and role removal (PT11-09a a)."""


class ReanchorBody(DisableBody):
    password: str = Field(min_length=1, max_length=200)  # the e-signature asks for the secret again (PT11-31)


class ChangePasswordBody(BaseModel):
    old_password: str = Field(min_length=1, max_length=200)
    new_password: str = Field(min_length=8, max_length=200)


class RoleBody(BaseModel):
    role: str
    scope_level: str
    scope_id: str | None = None


class AddRoleBody(RoleBody):
    reason: str = Field(min_length=3, max_length=500)   # PT11-09a a: stored with the audit entry


class CreateUserBody(BaseModel):
    username: str = Field(min_length=3, max_length=40, pattern=r"^[a-zA-Z0-9_.-]+$")
    name: str = Field(min_length=1, max_length=120)
    password: str = Field(min_length=8, max_length=200)
    email: str | None = Field(default=None, max_length=200)
    roles: list[RoleBody] = Field(default_factory=list)


class PlantBody(BaseModel):
    id: str
    name: str = Field(min_length=1, max_length=200)


class AreaBody(BaseModel):
    id: str
    plant_id: str
    name: str = Field(min_length=1, max_length=200)


class LineBody(BaseModel):
    id: str
    plant_id: str
    area_id: str | None = None
    name: str = Field(min_length=1, max_length=200)
    target_per_hour: float | None = Field(default=None, ge=0)


class DeviceBody(BaseModel):
    id: str
    kind: str
    name: str = Field(min_length=1, max_length=200)
    plant_id: str
    line_id: str | None = None
    maker: str | None = Field(default=None, max_length=100)
    model: str | None = Field(default=None, max_length=100)
    serial: str | None = Field(default=None, max_length=100)


class SubscribeBody(BaseModel):
    endpoint: str = Field(min_length=1, max_length=2000)
    keys: dict
    device: str | None = Field(default=None, max_length=200)


class UnsubscribeBody(BaseModel):
    endpoint: str = Field(min_length=1, max_length=2000)


def create_app(engine, api_key: str | None = None, demo: bool = False, vapid_public_key: str | None = None,
               vision_replay_hours: float = 24, vision_image_dir: str | None = None, vision_image_limit_bytes: int | None = None,
               session_idle_s: int | None = SESSION_IDLE_S, mqtt_ingest=None, audit_anchor: str | None = None,
               session_sweep_s: float = 60) -> FastAPI:
    app = FastAPI(title="Sundaram Smart Production Intelligence API", version="0.4.0")
    store, topo = engine.store, engine.topo

    def chain_alarm(got: dict, when: str) -> None:
        """PT11-09 b: a broken chain is a log line and a high alert to Admin and IT/OT that stays open until resolved."""
        where = [f"first bad entry at seq {got['first_bad_seq']}"] if got.get("first_bad_seq") else []
        if got.get("anchor") and not got["anchor"]["ok"]:
            where.append(got["anchor"]["note"])
        what = "; ".join(where) or "broken"
        print(f"WARNING: audit chain check ({when}): {what}", flush=True)
        if engine.alerts is None:
            return
        try:
            engine.alerts.raise_security(
                "audit_chain_broken", "plant-server", "Audit trail broken on the plant server",
                f"The audit chain check ({when}) found: {what}. Entries were changed or removed outside the software. Nothing is deleted "
                "or repaired; keep a copy of the database and the anchor file, and find who had access.",
                "audit chain check failed", {"check": got, "when": when})
        except Exception as exc:  # noqa: BLE001 - the log line above and the failed check entry stay
            print(f"WARNING: the audit chain alert could not be raised: {exc!r}", flush=True)

    # PT11-23: the audit chain (and its anchor, PT11-09 c) is checked at start; the result is itself an entry. A break deletes nothing.
    if audit_anchor:
        audit.use_anchor(store, audit_anchor)
    app.state.audit_check = audit.verify(store)
    audit.write(store, "chain.check", outcome="ok" if app.state.audit_check["ok"] else "failed", detail=app.state.audit_check)
    if not app.state.audit_check["ok"]:
        chain_alarm(app.state.audit_check, "at start")

    def sweep() -> None:
        """Every session_sweep_s: ends abandoned sessions (PT11-09 d) and compares the anchor with the table, so entries
        removed while the server runs are found within a minute."""
        sweep_sessions(store, session_idle_s)
        anchor = getattr(store, "audit_anchor", None)
        if anchor is not None and not anchor.frozen:
            a = anchor.check(store)
            if not a["ok"]:
                got = {"ok": False, "entries": None, "first_bad_seq": None, "anchor": a}
                audit.write(store, "chain.check", outcome="failed", detail=got)
                chain_alarm(got, "anchor check while running")
    app.state.sweep = sweep
    if session_sweep_s > 0:
        sweep_stop = threading.Event()

        def run_sweep():
            while not sweep_stop.wait(session_sweep_s):
                try:
                    sweep()
                except Exception as exc:  # noqa: BLE001 - the next round tries again
                    print(f"session sweep: {exc!r}", flush=True)
        app.router.on_startup.append(lambda: threading.Thread(target=run_sweep, name="session-sweep", daemon=True).start())
        app.router.on_shutdown.append(sweep_stop.set)
    sensor_owner = {s.id: e for e in topo.equipment.values() for s in e.sensors}

    def eq_scope(eq) -> tuple[str, str]:
        return topo.lines[eq.line_id].plant_id, eq.line_id

    def get_principal(x_api_key: str | None = Header(default=None), authorization: str | None = Header(default=None)) -> Principal:
        if authorization and authorization.lower().startswith("bearer "):
            p = resolve_token(store, authorization.split(None, 1)[1].strip(), session_idle_s)
            if p is None:
                raise HTTPException(401, "invalid or expired session token")
            return p
        if api_key:
            if x_api_key and hmac.compare_digest(x_api_key, api_key):
                return Principal(kind="service")
            raise HTTPException(401, "missing or invalid API key")
        return Principal(kind="anon")

    def require_view(principal: Principal, plant_id: str | None = None, line_id: str | None = None) -> None:
        if not principal.can_view(plant_id, line_id):
            raise HTTPException(403, "you do not have view access to this plant/line")

    def require_act(principal: Principal, plant_id: str | None = None, line_id: str | None = None) -> None:
        if not principal.can_act(plant_id, line_id):
            raise HTTPException(403, "you do not have permission to act on this plant/line")

    def require_admin(principal: Principal) -> None:
        if not principal.is_admin():
            raise HTTPException(403, "system_administrator role required")

    def require_user(principal: Principal) -> Principal:
        if principal.kind != "user":
            raise HTTPException(401, "sign in first (Authorization: Bearer <token> from /auth/login)")
        return principal

    def user_public(uid: int) -> dict:
        u = store.user_by_id(uid)
        return {"id": u["id"], "username": u["username"], "name": u["name"], "email": u["email"], "active": bool(u["active"]),
                "created_at": format_ts(u["created_at"]), "created_by": u["created_by"],
                "roles": [{k: r[k] for k in ("id", "role", "scope_level", "scope_id")} for r in store.role_list(uid)]}

    def window(frm: str | None, to: str | None) -> tuple[int, int]:
        if engine.now is None:
            raise HTTPException(404, "no data yet")
        try:
            t1 = parse_ts(to) if to else engine.now + 1
            t0 = parse_ts(frm) if frm else max(store.time_range()[0], t1 - engine.kpi_window_s)
        except ValueError:
            raise HTTPException(400, "from/to must look like 2026-01-01T00:00:00Z")
        if t1 <= t0:
            raise HTTPException(400, "'to' must be after 'from'")
        return t0, t1

    def eq_or_404(eid: str):
        if eid not in topo.equipment:
            raise HTTPException(404, f"unknown equipment '{eid}'")
        return topo.equipment[eid]

    def line_or_404(lid: str):
        if lid not in topo.lines:
            raise HTTPException(404, f"unknown line '{lid}'")
        return topo.lines[lid]

    def alerts_or_404():
        if engine.alerts is None:
            raise HTTPException(404, "alerts are not enabled on this server")
        return engine.alerts

    # ---------------- public / unauthenticated ----------------
    @app.get("/health")
    def health():
        return {"status": "ok", "demo": demo, "data_time": format_ts(engine.now) if engine.now is not None else None, **store.counts()}

    @app.get("/", include_in_schema=False)
    def dashboard():
        return FileResponse(DASHBOARD)

    @app.get("/static-app/{name}", include_in_schema=False)
    def static_app(name: str):
        if name not in STATIC:
            raise HTTPException(404)
        return FileResponse(DASHBOARD.parent / name, media_type=STATIC[name])

    for public_path, name in (("/manifest.webmanifest", "manifest.webmanifest"), ("/sw.js", "sw.js")):
        app.add_api_route(public_path, (lambda n=name: FileResponse(DASHBOARD.parent / n, media_type=STATIC[n], headers={"Service-Worker-Allowed": "/"})),
                          methods=["GET"], include_in_schema=False)

    @app.get("/webpush/public-key")
    def webpush_public_key():
        # not secret - a browser needs this to create a subscription at all, so it cannot require login first
        if vapid_public_key is None:
            raise HTTPException(404, "web push is not configured on this server")
        return {"key": vapid_public_key}

    # ---------------- auth ----------------
    throttle = app.state.login_throttle = LoginThrottle()

    def after_lock(u: dict | None) -> None:
        if engine.alerts is None:
            return
        try:
            engine.alerts.repeated_locks(u)   # PT11-05: three locks of one user in an hour alert Admin
        except Exception as exc:  # noqa: BLE001 - alerting never fails a sign-in or a PIN report; the lock entry stays
            print(f"WARNING: the repeated-lock alert could not be raised: {exc!r}", flush=True)

    def refuse_locked(username: str, address: str | None, device: str | None = None):
        """The throttle refused this name and device: the lock is audited once per window, then 429."""
        if throttle.first_refusal(username, address):
            u = store.user_by_username(username)
            audit.write(store, "lock", user=audit.user_of(store, u), user_id=u["id"] if u else None, client=device,
                        outcome="refused", detail={"address": address, "note": "too many wrong passwords from this device",
                                                   **({} if u else {"typed_username": audit.mask_username(username)})})
            after_lock(u)
        raise HTTPException(429, "too many wrong passwords: try again in 15 minutes")

    def bearer(authorization: str | None) -> str | None:
        return authorization.split(None, 1)[1].strip() if authorization and authorization.lower().startswith("bearer ") else None

    def client_address(request: Request) -> str | None:
        return request.client.host if request.client else None

    @app.post("/auth/login")
    def auth_login(body: LoginBody, request: Request):
        address = client_address(request)
        if not throttle.attempt(body.username, address):
            refuse_locked(body.username, address, body.device)
        got = login(store, body.username, body.password, device=body.device, address=address)
        if got is None:
            raise HTTPException(401, "invalid username or password")
        throttle.succeeded(body.username, address)
        p, token, expires = got
        # idle_sign_out_s: the server ends the session after this long without a call (None: no idle limit)
        return {"token": token, "expires_at": format_ts(expires), "user": p.public(),
                "idle_sign_out_s": None if idle_exempt(p) else (session_idle_s or None)}

    @app.post("/auth/logout")
    def auth_logout(request: Request, authorization: str | None = Header(default=None)):
        token = bearer(authorization)
        if token:
            logout(store, token, address=client_address(request), idle_s=session_idle_s)
        return {"ok": True}

    @app.post("/auth/pin-events")
    def auth_pin_event(body: PinEventBody, request: Request, authorization: str | None = Header(default=None)):
        """The mobile app reports a PIN unlock, a wrong PIN or a lockout with the sign-in's own bearer token
        (PT11-05, PT11-08). The PIN itself never leaves the phone."""
        token = bearer(authorization)
        if not token:
            raise HTTPException(401, "send the sign-in's own token (Authorization: Bearer <token>)")
        try:
            got = pin_event(store, token, body.event, device=body.device, address=client_address(request),
                            at=body.at.timestamp() if body.at else None, idle_s=session_idle_s, on_lock=after_lock)
        except ValueError as e:
            raise HTTPException(422, str(e))
        if got is None:
            raise HTTPException(401, "invalid or expired session token")
        return {"ok": True, **got}

    @app.get("/auth/me")
    def auth_me(principal: Principal = Depends(get_principal)):
        return principal.public()

    @app.post("/auth/change-password")
    def change_password(body: ChangePasswordBody, request: Request, principal: Principal = Depends(get_principal)):
        require_user(principal)
        u = store.user_by_id(principal.user_id)
        if not verify_password(body.old_password, u["password_hash"]):
            raise HTTPException(403, "current password is incorrect")
        store.user_set_password(u["id"], hash_password(body.new_password))
        store.session_delete_all_for_user(u["id"])  # forces re-login everywhere, including this session
        audit.write(store, "edit", user=audit.user_of(store, u), user_id=u["id"],
                    obj={"type": "user", "id": str(u["id"]), "name": u["username"], "version": None},
                    changes=[{"path": "/password", "old": "***", "new": "***"}], reason="own password changed",
                    detail={"address": client_address(request), "note": "all sessions ended"})
        return {"ok": True}

    # ---------------- admin: users and roles ----------------
    def actor(principal: Principal) -> dict | None:
        return audit.user_of(store, store.user_by_id(principal.user_id)) if principal.kind == "user" else None

    def given_reason(text: str, what: str) -> str:
        reason = audit.clean(text, 500).strip()
        if len(reason) < 3:
            raise HTTPException(422, f"give a reason for {what} (PT11-22)")
        return reason

    def role_text(r: dict) -> str:
        return f"{r['role']}@{r['scope_level']}" + (f":{r['scope_id']}" if r["scope_id"] else "")

    def user_obj(u: dict) -> dict:
        return {"type": "user", "id": str(u["id"]), "name": u["username"], "version": None}

    def _validated_roles(roles: list[RoleBody]) -> list[tuple[str, str, str | None]]:
        out = []
        for r in roles:
            try:
                role, level, sid = normalize_role(r.role, r.scope_level, r.scope_id)
            except ValueError as exc:
                raise HTTPException(400, str(exc))
            # the site JSON's plants and lines, or ones added in the master tables (vision-only lines, PL1)
            if level == "plant" and sid not in topo.plants and store.master_get("plant", sid) is None:
                raise HTTPException(400, f"unknown plant '{sid}'")
            if level == "line" and sid not in topo.lines and store.master_get("line", sid) is None:
                raise HTTPException(400, f"unknown line '{sid}'")
            out.append((role, level, sid))
        return out

    @app.post("/admin/users")
    def create_user(body: CreateUserBody, principal: Principal = Depends(get_principal)):
        bootstrap = store.user_count() == 0
        if not bootstrap:
            require_admin(principal)
        if store.user_by_username(body.username) is not None:
            raise HTTPException(409, f"username '{body.username}' already exists")
        roles = _validated_roles(body.roles)
        now = int(time.time())
        created_by = principal.username if principal.kind == "user" else ("service" if principal.kind == "service" else "bootstrap")
        uid = store.user_create(body.username, body.name, hash_password(body.password), body.email, created_by, now)
        for role, level, sid in roles:
            store.role_add(uid, role, level, sid, now)
        if bootstrap and not any(r == (ADMIN, "org", None) for r in roles):
            store.role_add(uid, ADMIN, "org", None, now)   # the system is never left without an administrator
        return user_public(uid)

    @app.get("/admin/audit")
    def list_audit(user_id: int | None = None, action: str | None = None, limit: int = Query(200, ge=1, le=5000),
                   principal: Principal = Depends(get_principal)):
        """The audit trail, newest first (PT11-21; PART11-DESIGN 5 entries)."""
        require_admin(principal)
        return audit.entries(store, user_id=user_id, action=action, limit=limit)

    @app.get("/admin/audit/check")
    def check_audit(principal: Principal = Depends(get_principal)):
        """Walks the audit hash chain (PT11-23); the check is itself an entry."""
        require_admin(principal)
        got = audit.verify(store)
        audit.write(store, "chain.check", user=audit.user_of(store, store.user_by_id(principal.user_id)) if principal.kind == "user" else None,
                    user_id=principal.user_id, outcome="ok" if got["ok"] else "failed", detail=got)
        if not got["ok"]:
            chain_alarm(got, "on demand")
        return got

    @app.post("/admin/audit/reanchor")
    def reanchor_audit(body: ReanchorBody, request: Request, principal: Principal = Depends(get_principal)):
        """PT11-09a b: after a break was investigated, an Admin re-anchors the chain with a reason and a signature
        (the password again). The break stays in the history; later checks list it as investigated."""
        address = client_address(request)
        if principal.kind != "user" or not principal.is_admin():
            audit.write(store, "refused", user=actor(principal), user_id=principal.user_id, obj=audit.CHAIN, outcome="refused",
                        detail={"address": address, "note": "audit chain re-anchor needs a signed-in Admin"
                                + ("" if principal.kind == "user" else f" (not a {principal.kind} key)")})
            raise HTTPException(403, "a signed-in system_administrator must sign the re-anchor")
        reason = given_reason(body.reason, "re-anchoring the audit chain")
        u = store.user_by_id(principal.user_id)
        if not throttle.attempt(u["username"], address):
            refuse_locked(u["username"], address)
        if not verify_password(body.password, u["password_hash"]):
            audit.write(store, "sign.failed", user=audit.user_of(store, u), user_id=u["id"], obj=audit.CHAIN, outcome="failed",
                        reason=reason, detail={"address": address, "note": "wrong password for the re-anchor signature"})
            raise HTTPException(403, "wrong password: the re-anchor was not signed")
        throttle.succeeded(u["username"], address)
        try:
            entry = audit.reanchor(store, u, reason)
        except ValueError as exc:
            raise HTTPException(409, str(exc))
        got = audit.verify(store)
        audit.write(store, "chain.check", user=audit.user_of(store, u), user_id=u["id"], outcome="ok" if got["ok"] else "failed",
                    detail=got)
        if not got["ok"]:
            chain_alarm(got, "after the re-anchor")
        return {"reanchor": entry, "check": got}

    @app.get("/admin/users")
    def list_users(principal: Principal = Depends(get_principal)):
        require_admin(principal)
        return [user_public(u["id"]) for u in store.user_list()]

    @app.post("/admin/users/{uid}/roles")
    def add_role(uid: int, body: AddRoleBody, principal: Principal = Depends(get_principal)):
        require_admin(principal)
        u = store.user_by_id(uid)
        if u is None:
            raise HTTPException(404, f"unknown user {uid}")
        (role, level, sid), = _validated_roles([body])
        reason = given_reason(body.reason, "adding a role")
        aid = store.role_add(uid, role, level, sid, int(time.time()))
        audit.write(store, "edit", user=actor(principal), user_id=principal.user_id, obj=user_obj(u), reason=reason,
                    changes=[{"path": f"/roles/{aid}", "new": role_text({"role": role, "scope_level": level, "scope_id": sid})}])
        return user_public(uid)

    @app.delete("/admin/users/{uid}/roles/{assignment_id}")
    def remove_role(uid: int, assignment_id: int, body: ReasonBody, principal: Principal = Depends(get_principal)):
        require_admin(principal)
        u = store.user_by_id(uid)
        if u is None:
            raise HTTPException(404, f"unknown user {uid}")
        old = next((r for r in store.role_list(uid) if r["id"] == assignment_id), None)
        if old is None:
            raise HTTPException(404, f"unknown role assignment {assignment_id} for user {uid}")
        reason = given_reason(body.reason, "removing a role")
        store.role_remove(assignment_id)
        audit.write(store, "edit", user=actor(principal), user_id=principal.user_id, obj=user_obj(u), reason=reason,
                    changes=[{"path": f"/roles/{assignment_id}", "old": role_text(old)}])
        return user_public(uid)

    @app.post("/admin/users/{uid}/deactivate")
    def deactivate_user(uid: int, body: DisableBody, principal: Principal = Depends(get_principal)):
        require_admin(principal)
        if store.user_by_id(uid) is None:
            raise HTTPException(404, f"unknown user {uid}")
        if principal.kind == "user" and principal.user_id == uid:
            raise HTTPException(400, "cannot deactivate your own account")
        reason = given_reason(body.reason, "disabling the account")
        store.user_set_active(uid, False)
        store.session_delete_all_for_user(uid)
        u = store.user_by_id(uid)
        audit.write(store, "disable", user=audit.user_of(store, store.user_by_id(principal.user_id)) if principal.kind == "user" else None,
                    user_id=principal.user_id, obj={"type": "user", "id": str(uid), "name": u["username"], "version": None},
                    changes=[{"path": "/active", "old": True, "new": False}], reason=reason, detail={"note": "all sessions ended"})
        return user_public(uid)

    @app.post("/admin/users/{uid}/activate")
    def activate_user(uid: int, body: ReasonBody, principal: Principal = Depends(get_principal)):
        require_admin(principal)
        u = store.user_by_id(uid)
        if u is None:
            raise HTTPException(404, f"unknown user {uid}")
        reason = given_reason(body.reason, "activating the account")
        store.user_set_active(uid, True)
        audit.write(store, "edit", user=actor(principal), user_id=principal.user_id, obj=user_obj(u), reason=reason,
                    changes=[{"path": "/active", "old": bool(u["active"]), "new": True}])
        return user_public(uid)

    # ---------------- Vision module (PL2): station ingest and live lines ----------------
    from .vision.api import mount as mount_vision
    mount_vision(app, store, get_principal, replay_hours=vision_replay_hours, alerts=engine.alerts,
                 image_dir=vision_image_dir, image_limit_bytes=vision_image_limit_bytes, session_idle_s=session_idle_s)

    # ---------------- plant master data and devices (PL1) ----------------
    def master_write(table: str, row: dict) -> dict:
        try:
            check_id(row["id"], f"{table} id")
        except ValueError as e:
            raise HTTPException(400, str(e))
        old = store.master_get(table, row["id"])
        # Production's plants, areas and lines come from the site JSON: change them there.
        if old is not None and old["source"] == "site_json":
            raise HTTPException(409, f"{table} '{row['id']}' comes from the site JSON; change it there")
        store.master_upsert(table, {**row, "source": "admin"})
        store.commit()
        return store.master_get(table, row["id"])

    @app.get("/admin/master")
    def master_data(principal: Principal = Depends(get_principal)):
        require_admin(principal)
        return {t + "s": store.master_list(t) for t in ("plant", "area", "line")}

    @app.post("/admin/plants")
    def put_plant(body: PlantBody, principal: Principal = Depends(get_principal)):
        require_admin(principal)
        return master_write("plant", {"id": body.id, "name": body.name})

    @app.post("/admin/areas")
    def put_area(body: AreaBody, principal: Principal = Depends(get_principal)):
        require_admin(principal)
        if store.master_get("plant", body.plant_id) is None:
            raise HTTPException(400, f"unknown plant '{body.plant_id}'")
        return master_write("area", {"id": body.id, "plant_id": body.plant_id, "name": body.name})

    @app.post("/admin/lines")
    def put_line(body: LineBody, principal: Principal = Depends(get_principal)):
        require_admin(principal)
        if store.master_get("plant", body.plant_id) is None:
            raise HTTPException(400, f"unknown plant '{body.plant_id}'")
        if body.area_id is not None:
            area = store.master_get("area", body.area_id)
            if area is None or area["plant_id"] != body.plant_id:
                raise HTTPException(400, f"area '{body.area_id}' is not in plant '{body.plant_id}'")
        return master_write("line", {"id": body.id, "plant_id": body.plant_id, "area_id": body.area_id, "name": body.name,
                                     "target_per_hour": body.target_per_hour})

    def device_or_404(device_id: str) -> dict:
        d = store.device_get(device_id)
        if d is None:
            raise HTTPException(404, f"unknown device '{device_id}'")
        return d

    @app.get("/admin/devices")
    def list_devices(principal: Principal = Depends(get_principal)):
        require_admin(principal)
        return [public_device(d) for d in store.device_list()]

    @app.post("/admin/devices")
    def add_device(body: DeviceBody, principal: Principal = Depends(get_principal)):
        """Adds a device and returns its token once; only its SHA-256 is kept."""
        require_admin(principal)
        try:
            check_id(body.id, "device id")
        except ValueError as e:
            raise HTTPException(400, str(e))
        if body.kind not in DEVICE_KINDS:
            raise HTTPException(400, f"kind must be one of {list(DEVICE_KINDS)}")
        if store.master_get("plant", body.plant_id) is None:
            raise HTTPException(400, f"unknown plant '{body.plant_id}'")
        if body.kind == "production_gateway" and body.line_id is None:
            raise HTTPException(400, "a production gateway needs its line_id (one gateway per line)")
        if body.line_id is not None:
            line = store.master_get("line", body.line_id)
            if line is None or line["plant_id"] != body.plant_id:
                raise HTTPException(400, f"line '{body.line_id}' is not in plant '{body.plant_id}'")
        if store.device_get(body.id) is not None:
            raise HTTPException(409, f"device '{body.id}' already exists")
        store.device_create({"id": body.id, "kind": body.kind, "name": body.name, "plant_id": body.plant_id, "line_id": body.line_id,
                             "maker": body.maker, "model": body.model, "serial": body.serial, "enabled": 1, "created_at": int(time.time()),
                             "created_by": principal.actor_name("service"), "source": "admin"})
        token = issue_token(store, body.id)
        return {"device": public_device(store.device_get(body.id)), "token": token}

    @app.post("/admin/devices/{device_id}/token")
    def new_device_token(device_id: str, principal: Principal = Depends(get_principal)):
        """A new token for the device; the old one stops working at once."""
        require_admin(principal)
        device_or_404(device_id)
        return {"device": public_device(store.device_get(device_id)), "token": issue_token(store, device_id)}

    @app.post("/admin/devices/{device_id}/disable")
    def disable_device(device_id: str, principal: Principal = Depends(get_principal)):
        require_admin(principal)
        device_or_404(device_id)
        store.device_update(device_id, enabled=0)
        store.commit()
        return public_device(store.device_get(device_id))

    @app.post("/admin/devices/{device_id}/enable")
    def enable_device(device_id: str, principal: Principal = Depends(get_principal)):
        require_admin(principal)
        device_or_404(device_id)
        store.device_update(device_id, enabled=1)
        store.commit()
        return public_device(store.device_get(device_id))

    # ---------------- web push subscriptions ----------------
    @app.post("/webpush/subscribe")
    def webpush_subscribe(body: SubscribeBody, principal: Principal = Depends(get_principal)):
        require_user(principal)
        p256dh, auth_secret = body.keys.get("p256dh"), body.keys.get("auth")
        if not p256dh or not auth_secret:
            raise HTTPException(400, "keys.p256dh and keys.auth are required")
        sid = store.push_add(principal.user_id, body.endpoint, p256dh, auth_secret, body.device, int(time.time()))
        return {"id": sid, "ok": True}

    @app.post("/webpush/unsubscribe")
    def webpush_unsubscribe(body: UnsubscribeBody, principal: Principal = Depends(get_principal)):
        require_user(principal)
        if not any(s["endpoint"] == body.endpoint for s in store.push_list_for_user(principal.user_id)):
            raise HTTPException(404, "no such subscription for this account")
        store.push_remove(body.endpoint)
        return {"ok": True}

    @app.get("/webpush/subscriptions")
    def webpush_subscriptions(principal: Principal = Depends(get_principal)):
        require_user(principal)
        return [{"id": s["id"], "device": s["device"], "endpoint_tail": s["endpoint"][-12:]} for s in store.push_list_for_user(principal.user_id)]

    # ---------------- production data (RBAC-scoped) ----------------
    def all_plants() -> dict:
        """{plant_id: name}: Production's plants (site JSON), then those only the master tables hold (PL1)."""
        out = dict(topo.plants)
        for p in store.master_list("plant"):
            out.setdefault(p["id"], p["name"])
        return out

    def lines_of(plant_id: str) -> list[dict]:
        """A plant's lines: Production's first, then lines added in the master tables for vision or printing.
        "production" says whether the line has Production's equipment (and so /lines/{id}/status)."""
        out = [{"id": ln.id, "name": ln.name, "target_per_hour": ln.target_per_hour, "equipment": [e.id for e in ln.equipment],
                "production": True} for ln in topo.lines.values() if ln.plant_id == plant_id]
        out += [{"id": ln["id"], "name": ln["name"], "target_per_hour": ln["target_per_hour"], "equipment": [], "production": False}
                for ln in store.master_list("line") if ln["plant_id"] == plant_id and ln["id"] not in topo.lines]
        return out

    @app.get("/plants")
    def plants(principal: Principal = Depends(get_principal)):
        names = all_plants()
        visible = principal.visible_plant_ids(names)
        return [{"id": pid, "name": name, "lines": [{"id": ln["id"], "name": ln["name"], "production": ln["production"]} for ln in lines_of(pid)]}
                for pid, name in names.items() if pid in visible]

    @app.get("/plants/{plant_id}/lines")
    def plant_lines(plant_id: str, principal: Principal = Depends(get_principal)):
        if plant_id not in all_plants():
            raise HTTPException(404, f"unknown plant '{plant_id}'")
        require_view(principal, plant_id=plant_id)
        return lines_of(plant_id)

    @app.get("/lines/{line_id}/equipment")
    def line_equipment(line_id: str, principal: Principal = Depends(get_principal)):
        line = line_or_404(line_id)
        require_view(principal, line.plant_id, line_id)
        return [engine.equipment_status(e.id) for e in line.equipment]

    @app.get("/lines/{line_id}/status")
    def line_status(line_id: str, principal: Principal = Depends(get_principal)):
        line = line_or_404(line_id)
        require_view(principal, line.plant_id, line_id)
        return engine.line_status(line_id)

    @app.get("/lines/{line_id}/bottlenecks")
    def bottlenecks(line_id: str, frm: str | None = Query(None, alias="from"), to: str | None = None, principal: Principal = Depends(get_principal)):
        line = line_or_404(line_id)
        require_view(principal, line.plant_id, line_id)
        t0, t1 = window(frm, to)
        k = line_kpi(store, line, {e.id: store.intervals(e.id, t0, t1) for e in line.equipment}, t0, t1)
        return {"line_id": line_id, "window": [format_ts(t0), format_ts(t1)], **k["bottleneck"]}

    @app.get("/equipment/{eid}/status")
    def equipment_status(eid: str, principal: Principal = Depends(get_principal)):
        eq = eq_or_404(eid)
        require_view(principal, *eq_scope(eq))
        return engine.equipment_status(eid)

    @app.get("/equipment/{eid}/states")
    def equipment_states(eid: str, frm: str | None = Query(None, alias="from"), to: str | None = None, principal: Principal = Depends(get_principal)):
        eq = eq_or_404(eid)
        require_view(principal, *eq_scope(eq))
        t0, t1 = window(frm, to)
        return {"equipment_id": eid, "basis": "detected", "intervals": [
            {"state": i.state, "start": format_ts(i.start), "end": format_ts(i.end), "duration_s": i.duration,
             "confidence": round(i.confidence, 2), "evidence": i.evidence} for i in store.intervals(eid, t0, t1)]}

    @app.get("/equipment/{eid}/production")
    def production(eid: str, frm: str | None = Query(None, alias="from"), to: str | None = None, bucket: str = "hour", principal: Principal = Depends(get_principal)):
        eq = eq_or_404(eid)
        require_view(principal, *eq_scope(eq))
        if bucket not in BUCKETS:
            raise HTTPException(400, f"bucket must be one of {sorted(BUCKETS)} (shift buckets need shift configuration)")
        t0, t1 = window(frm, to)
        size, agg = BUCKETS[bucket], defaultdict(lambda: [0, 0])
        for e in store.events(["cycle"], equipment_id=eid, t_min=t0, t_max=t1 - 1):
            if e["ts"] >= t0:
                agg[e["ts"] - e["ts"] % size][e["payload"]["result"] != "good"] += 1
        end, start = counter_at(store, eid, t1 - 1), counter_at(store, eid, t0 - 1)
        return {"equipment_id": eid, "bucket": bucket, "window": [format_ts(t0), format_ts(t1)],
                "buckets": [{"start": format_ts(b), "good": g, "reject": r, "total": g + r} for b, (g, r) in sorted(agg.items())],
                "observed_total": sum(g + r for g, r in agg.values()), "reconciled_total": end["total"] - start["total"],
                "basis": "measured", "note": "buckets count observed cycles; reconciled_total also includes production made while the machine was unreachable"}

    @app.get("/equipment/{eid}/downtime")
    def downtime(eid: str, frm: str | None = Query(None, alias="from"), to: str | None = None, min_s: int = 30, principal: Principal = Depends(get_principal)):
        eq = eq_or_404(eid)
        require_view(principal, *eq_scope(eq))
        t0, t1 = window(frm, to)
        rows = downtime_events(store.intervals(eid, t0, t1), min_s)
        return {"equipment_id": eid, "basis": "detected", "events": [
            {**r, "start": format_ts(r.pop("start")), "end": format_ts(r.pop("end"))} for r in rows]}

    @app.get("/sensors/{sensor_id}/readings")
    def sensor_readings(sensor_id: str, frm: str | None = Query(None, alias="from"), to: str | None = None, limit: int = Query(500, ge=1, le=5000),
                        principal: Principal = Depends(get_principal)):
        owner = sensor_owner.get(sensor_id)
        if owner is not None:
            require_view(principal, *eq_scope(owner))
        t0, t1 = window(frm, to)
        ev = [e for e in store.events(["sensor"], subject=sensor_id, t_min=t0, t_max=t1 - 1) if e["ts"] >= t0][-limit:]
        if not ev and store.last_before(["sensor"], t1, subject=sensor_id) is None:
            raise HTTPException(404, f"no readings for sensor '{sensor_id}'")
        return {"sensor_id": sensor_id, "basis": "measured",
                "readings": [{"ts": format_ts(e["ts"]), "value": e["payload"]["value"], "unit": e["payload"]["unit"]} for e in ev]}

    @app.get("/sensors/{sensor_id}/health")
    def sensor_health(sensor_id: str, principal: Principal = Depends(get_principal)):
        owner = sensor_owner.get(sensor_id)
        if owner is not None:
            require_view(principal, *eq_scope(owner))
        for h in engine.sensor_health():
            if h["sensor_id"] == sensor_id:
                return {**h, "since": format_ts(h["since"]) if h["since"] else None,
                        "last_valid": {**h["last_valid"], "ts": format_ts(h["last_valid"]["ts"])} if h["last_valid"] else None,
                        "current": {**h["current"], "ts": format_ts(h["current"]["ts"])} if h["current"] else None}
        raise HTTPException(404, f"unknown sensor '{sensor_id}' or no data yet")

    @app.get("/lines/{line_id}/sensors")
    def line_sensors(line_id: str, principal: Principal = Depends(get_principal)):
        line = line_or_404(line_id)
        require_view(principal, line.plant_id, line_id)
        return [{k: (format_ts(v) if k == "since" and v else v) for k, v in h.items() if k not in ("last_valid", "current")}
                for h in engine.sensor_health(line_id)]

    @app.get("/reports/oee")
    def report_oee(line_id: str, frm: str | None = Query(None, alias="from"), to: str | None = None, principal: Principal = Depends(get_principal)):
        line = line_or_404(line_id)
        require_view(principal, line.plant_id, line_id)
        t0, t1 = window(frm, to)
        k = line_kpi(store, line, {e.id: store.intervals(e.id, t0, t1) for e in line.equipment}, t0, t1)
        return {"line_id": line_id, "window": [format_ts(t0), format_ts(t1)], **k}

    # ---------------- alerts (RBAC-scoped) ----------------
    @app.get("/alerts")
    def list_alerts(state: str | None = None, line_id: str | None = None, limit: int = Query(200, ge=1, le=1000), principal: Principal = Depends(get_principal)):
        al = alerts_or_404()
        states = None
        if state:
            states = ["OPEN", "ACKNOWLEDGED"] if state.lower() == "active" else [x.strip().upper() for x in state.split(",")]
            if not set(states) <= STATES:
                raise HTTPException(400, "state must be active or a comma list of open, acknowledged, resolved")
        if line_id:
            # a production line from the site JSON, or a line from the master tables (vision-only lines, PL1)
            line = topo.lines.get(line_id) or store.master_get("line", line_id)
            if line is None:
                raise HTTPException(404, f"unknown line '{line_id}'")
            require_view(principal, line.plant_id if line_id in topo.lines else line["plant_id"], line_id)
            return al.list(states, line_id, limit)
        return [a for a in al.list(states, None, limit) if a["line_id"] is None or principal.can_view(a["plant_id"], a["line_id"])]

    @app.get("/alerts/{alert_id}")
    def alert_detail(alert_id: int, principal: Principal = Depends(get_principal)):
        d = alerts_or_404().detail(alert_id)
        if d is None:
            raise HTTPException(404, f"unknown alert {alert_id}")
        require_view(principal, d["plant_id"], d["line_id"])
        return d

    @app.post("/alerts/{alert_id}/acknowledge")
    def acknowledge(alert_id: int, body: AckBody, principal: Principal = Depends(get_principal)):
        al = alerts_or_404()
        a = store.alert_get(alert_id)
        if a is None:
            raise HTTPException(404, f"unknown alert {alert_id}")
        require_act(principal, a["plant_id"], a["line_id"])
        actor = principal.actor_name(body.user.strip())
        if not actor:
            raise HTTPException(422, "sign in, or provide 'user' in the request body")
        try:
            return al.acknowledge(alert_id, actor, body.note.strip())
        except ValueError as exc:
            raise HTTPException(409, str(exc))

    @app.post("/alerts/{alert_id}/resolve")
    def resolve_alert(alert_id: int, body: ResolveBody, principal: Principal = Depends(get_principal)):
        al = alerts_or_404()
        a = store.alert_get(alert_id)
        if a is None:
            raise HTTPException(404, f"unknown alert {alert_id}")
        require_act(principal, a["plant_id"], a["line_id"])
        actor = principal.actor_name(body.user.strip())
        if not actor:
            raise HTTPException(422, "sign in, or provide 'user' in the request body")
        try:
            return al.resolve(alert_id, actor, body.reason.strip())
        except ValueError as exc:
            raise HTTPException(409, str(exc))

    @app.get("/alert-rules")
    def alert_rules(principal: Principal = Depends(get_principal)):
        return alerts_or_404().rules

    @app.get("/admin/mqtt/refusals")
    def mqtt_refusals(principal: Principal = Depends(get_principal)):
        """Admin only: MQTT messages refused at ingest, by reason, and the last refusal per claimed gateway (no tokens, no bodies)."""
        require_admin(principal)
        if mqtt_ingest is None:
            raise HTTPException(404, "MQTT ingest is not running on this server")
        return mqtt_ingest.refusal_report()

    @app.get("/ingest/stats")
    def ingest_stats(principal: Principal = Depends(get_principal)):
        return {**store.counts(), "sequence_gaps": {g: r for g, r in gaps(store).items()},
                "data_time": format_ts(engine.now) if engine.now is not None else None}

    @app.websocket("/stream/lines/{line_id}")
    async def stream(ws: WebSocket, line_id: str, interval: float = 1.0, api_key_q: str | None = Query(None, alias="api_key"),
                     token_q: str | None = Query(None, alias="token")):
        if token_q:
            principal = await run_in_threadpool(resolve_token, store, token_q, session_idle_s)
            if principal is None:
                await ws.close(code=4401)
                return
        elif api_key:
            if not (api_key_q and hmac.compare_digest(api_key_q, api_key)):
                await ws.close(code=4401)
                return
            principal = Principal(kind="service")
        else:
            principal = Principal(kind="anon")
        if line_id not in topo.lines:
            await ws.close(code=4404)
            return
        line = topo.lines[line_id]
        if not principal.can_view(line.plant_id, line_id):
            await ws.close(code=4403)
            return
        await ws.accept()
        checked = time.monotonic()
        try:
            while True:
                if token_q and time.monotonic() - checked >= 5:
                    # an idle end, lockout, sign-out or disable also ends an open stream (not counted as use)
                    if await run_in_threadpool(resolve_token, store, token_q, session_idle_s, False) is None:
                        await ws.close(code=4401)
                        return
                    checked = time.monotonic()
                await ws.send_json(await run_in_threadpool(engine.line_status, line_id))
                await asyncio.sleep(max(0.2, interval))
        except (WebSocketDisconnect, RuntimeError):
            pass

    return app
