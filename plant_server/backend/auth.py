"""User accounts and role-based access control (M4b).

Passwords: PBKDF2-HMAC-SHA256, 200k iterations, random salt (stdlib only, no extra native dependency).
Sessions: opaque random tokens, stored server-side so they can be revoked; wall-clock expiry (real time,
not simulated data time - login should behave the same whether the demo simulator is running fast or not at all):
at most SESSION_TTL_S, and ended after SESSION_IDLE_S without a call (Part 11 PT11-06, PT11-08), except for accounts
that only hold the factory_display role (view-only wall screens).
Sign-ins, failed sign-ins, locks, sign-outs (idle ones too) and the mobile app's PIN events are audit entries
(backend/audit.py; PT11-05, PT11-08, PT11-21). A session's entries name it by session_ref(), never by its token.
A session is ended when its token is next used, or by sweep_sessions() (every minute on a running server, PT11-09), which
also audits the end, so an abandoned sign-in does not wait for a call that never comes.

Scope: each role assignment is scoped to 'org' (everywhere), 'plant', or 'line'. A user can hold several
assignments (e.g. maintenance_manager on one plant, viewer on another). Roles mirror the presentation's
roles slide and docs/architecture/ARCHITECTURE.md:
  system_administrator   manages users and roles; full view+act everywhere
  corporate_management   view only, typically org-scoped
  plant_manager, production_manager, maintenance_manager, quality_manager, it_ot
                          view + act (acknowledge/resolve alerts) within their scope
  factory_display         view only (kiosk / large-screen display)

This layer is purely additive: a server with no users configured keeps the M1-M3 behaviour (the shared
API key, or fully open access if no key is set either) unchanged. RBAC only applies once users exist.
"""
from __future__ import annotations

import hashlib
import hmac
import ipaddress
import os
import secrets
import threading
import time
from dataclasses import dataclass, field

from . import audit

ADMIN, CORPORATE, PLANT_MANAGER, PRODUCTION_MANAGER, MAINTENANCE_MANAGER, QUALITY_MANAGER, IT_OT, DISPLAY = (
    "system_administrator", "corporate_management", "plant_manager", "production_manager",
    "maintenance_manager", "quality_manager", "it_ot", "factory_display")
ROLES = (ADMIN, CORPORATE, PLANT_MANAGER, PRODUCTION_MANAGER, MAINTENANCE_MANAGER, QUALITY_MANAGER, IT_OT, DISPLAY)
ACT_ROLES = {ADMIN, PLANT_MANAGER, PRODUCTION_MANAGER, MAINTENANCE_MANAGER, QUALITY_MANAGER, IT_OT}
SCOPE_LEVELS = ("org", "plant", "line")
SESSION_TTL_S = 12 * 3600      # PART11-DESIGN 3: 15 min idle and 12 h at most
SESSION_IDLE_S = 15 * 60       # PT11-06: 15 minutes on the server and mobile
PIN_MAX_WRONG = 5              # the phone's PcServers.maxWrongPins: this many wrong PINs in a row end the session
PIN_EVENTS = ("pin_unlock", "pin_wrong", "pin_lockout")   # reported by the phone; audited as unlock, unlock.failed, lock
PIN_AT_SKEW_S = 5 * 60         # PT11-08b: a phone time this far ahead of the server, or before the sign-in, is still taken
PBKDF2_ITERATIONS = 200_000


def hash_password(password: str) -> str:
    salt = os.urandom(16)
    dk = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, PBKDF2_ITERATIONS)
    return f"pbkdf2${PBKDF2_ITERATIONS}${salt.hex()}${dk.hex()}"


def verify_password(password: str, stored: str) -> bool:
    try:
        scheme, iters, salt_hex, hash_hex = stored.split("$")
        dk = hashlib.pbkdf2_hmac("sha256", password.encode(), bytes.fromhex(salt_hex), int(iters))
        return scheme == "pbkdf2" and hmac.compare_digest(dk.hex(), hash_hex)
    except (ValueError, AttributeError):
        return False


def new_token() -> str:
    return secrets.token_urlsafe(32)


def normalize_role(role: str, scope_level: str, scope_id: str | None) -> tuple[str, str, str | None]:
    if role not in ROLES:
        raise ValueError(f"unknown role {role!r}; expected one of {ROLES}")
    if scope_level not in SCOPE_LEVELS:
        raise ValueError(f"scope_level must be one of {SCOPE_LEVELS}")
    if scope_level == "org" and scope_id is not None:
        raise ValueError("org scope must not have a scope_id")
    if scope_level != "org" and not scope_id:
        raise ValueError(f"{scope_level} scope needs a scope_id")
    return role, scope_level, scope_id


@dataclass
class Principal:
    """kind: 'service' (shared API key), 'user' (logged-in account), or 'anon' (no auth configured at all)."""
    kind: str
    user_id: int | None = None
    username: str | None = None
    name: str | None = None
    assignments: list = field(default_factory=list)  # [{"id","role","scope_level","scope_id"}]

    def is_admin(self) -> bool:
        return self.kind == "service" or any(a["role"] == ADMIN for a in self.assignments)

    def _covers(self, a: dict, plant_id, line_id) -> bool:
        if a["scope_level"] == "org":
            return True
        if a["scope_level"] == "plant":
            return plant_id is not None and a["scope_id"] == plant_id
        return line_id is not None and a["scope_id"] == line_id

    def can_view(self, plant_id: str | None = None, line_id: str | None = None) -> bool:
        if self.kind in ("service", "anon") or self.is_admin():
            return True
        return any(self._covers(a, plant_id, line_id) for a in self.assignments)

    def can_act(self, plant_id: str | None = None, line_id: str | None = None) -> bool:
        if self.kind in ("service", "anon") or self.is_admin():
            return True
        return any(a["role"] in ACT_ROLES and self._covers(a, plant_id, line_id) for a in self.assignments)

    def visible_plant_ids(self, all_plant_ids) -> set:
        if self.kind in ("service", "anon") or self.is_admin():
            return set(all_plant_ids)
        out = set()
        for a in self.assignments:
            if a["scope_level"] == "org":
                return set(all_plant_ids)
            if a["scope_level"] == "plant":
                out.add(a["scope_id"])
        return out

    def visible_line_ids(self, topo) -> set:
        if self.kind in ("service", "anon") or self.is_admin():
            return set(topo.lines)
        out = set()
        for a in self.assignments:
            if a["scope_level"] == "org":
                return set(topo.lines)
            if a["scope_level"] == "plant":
                out |= {lid for lid, ln in topo.lines.items() if ln.plant_id == a["scope_id"]}
            if a["scope_level"] == "line":
                out.add(a["scope_id"])
        return out

    def actor_name(self, fallback: str) -> str:
        """The name recorded in an audit trail: the authenticated username when logged in (trustworthy),
        otherwise whatever the caller supplied (kept for the shared-API-key / no-accounts path)."""
        return self.username if self.kind == "user" else fallback

    def public(self) -> dict:
        return {"user_id": self.user_id, "username": self.username, "name": self.name,
                "roles": [{k: a[k] for k in ("role", "scope_level", "scope_id")} for a in self.assignments],
                "is_admin": self.is_admin()}


def principal_for_user(store, u: dict) -> Principal:
    return Principal(kind="user", user_id=u["id"], username=u["username"], name=u["name"], assignments=store.role_list(u["id"]))


def session_ref(token: str) -> str:
    """Ties a session's audit entries together without writing the token itself (a live secret) into the trail."""
    return hashlib.sha256(token.encode()).hexdigest()[:16]


def _session(token: str) -> dict:
    return {"type": "session", "id": session_ref(token), "name": None, "version": None}


def _audit(store, action: str, u: dict | None, *, token: str | None = None, device: str | None = None,
           address: str | None = None, outcome: str = "ok", note: str | None = None, typed: str | None = None,
           phone_at: float | None = None) -> None:
    detail = {"address": audit.clean(address), "note": note}
    if phone_at is not None:
        detail["phone_at"] = audit._iso(phone_at)[0]
    if u is None and typed is not None:
        detail["typed_username"] = audit.mask_username(typed)
    audit.write(store, action, user=audit.user_of(store, u), user_id=u["id"] if u else None, client=device,
                obj=_session(token) if token else None, outcome=outcome, detail=detail)


def login(store, username: str, password: str, *, device: str | None = None, address: str | None = None) -> tuple[Principal, str, int] | None:
    u = store.user_by_username(username)
    if u is None or not u["active"] or not verify_password(password, u["password_hash"]):
        why = "unknown user" if u is None else ("account disabled" if not u["active"] else "wrong password")
        _audit(store, "sign-in.failed", u, device=device, address=address, outcome="failed", note=why, typed=username)
        return None
    now = int(time.time())
    expires = now + SESSION_TTL_S
    token = new_token()
    store.session_create(token, u["id"], now, expires)
    _audit(store, "sign-in", u, token=token, device=device, address=address)
    return principal_for_user(store, u), token, expires


def logout(store, token: str, *, address: str | None = None, idle_s: int | None = SESSION_IDLE_S) -> None:
    s = store.session_get(token)
    if s is not None and store.session_delete(token):
        u = store.user_by_id(s["user_id"])
        idle = int(time.time()) - s["last_used_at"]
        late = idle_s and idle > idle_s and u is not None and not idle_exempt(principal_for_user(store, u))
        _audit(store, "sign-out", u, token=token, address=address,
               note=f"idle: no call for {idle} s (limit {idle_s} s)" if late else "signed out")


def idle_exempt(p: Principal) -> bool:
    """A view-only wall screen (factory_display and nothing else) can change nothing, so it is not signed out when idle."""
    return bool(p.assignments) and all(a["role"] == DISPLAY for a in p.assignments)


def resolve_token(store, token: str, idle_s: int | None = SESSION_IDLE_S, touch: bool = True) -> Principal | None:
    """The signed-in user of a session token, or None. A session past its 12 h, or idle longer than idle_s (None or
    0: no idle limit), is ended here and the end is audited. touch=False reads it without counting as use."""
    now = int(time.time())
    s = store.session_get(token)
    if s is None:
        return None
    if s["expires_at"] < now:
        if store.session_delete(token):
            _audit(store, "sign-out", store.user_by_id(s["user_id"]), token=token, note=_expired(s))
        return None
    u = store.user_by_id(s["user_id"])
    if u is None or not u["active"]:
        return None
    p = principal_for_user(store, u)
    idle = now - s["last_used_at"]
    if idle_s and idle > idle_s and not idle_exempt(p):
        if store.session_delete(token):
            _audit(store, "sign-out", u, token=token, note=f"idle: no call for {idle} s (limit {idle_s} s)")
        return None
    if touch:
        store.session_touch(token, now)
    return p


def _expired(s: dict) -> str:
    return f"expired: {(s['expires_at'] - s['created_at']) // 3600} h limit"


def sweep_sessions(store, idle_s: int | None = SESSION_IDLE_S, now: int | None = None) -> int:
    """Ends every session past its limit or idle longer than idle_s (the wall-screen exception as in resolve_token) and
    audits each end (PT11-09 d). A session used while the sweep runs is left alone. Returns how many it ended."""
    now = int(time.time()) if now is None else now
    ended = 0
    for s in store.session_list():
        u = store.user_by_id(s["user_id"])
        idle = now - s["last_used_at"]
        if s["expires_at"] < now:
            note = _expired(s)
        elif idle_s and idle > idle_s and (u is None or not idle_exempt(principal_for_user(store, u))):
            note = f"idle: no call for {idle} s (limit {idle_s} s)"
        else:
            continue
        if store.session_delete(s["token"], last_used_at=s["last_used_at"]):
            _audit(store, "sign-out", u, token=s["token"], note=note + "; ended by the session sweep")
            ended += 1
    return ended


def pin_event(store, token: str, kind: str, *, device: str | None = None, address: str | None = None,
              at: float | None = None, idle_s: int | None = SESSION_IDLE_S, on_lock=None) -> dict | None:
    """Records a PIN event the phone reports with the sign-in's own token (the PIN is checked on the phone, offline;
    the server never sees it). pin_unlock counts as use; pin_wrong does not, so wrong PINs never keep a session
    alive. pin_lockout, or PIN_MAX_WRONG pin_wrong in a row, ends the session here too, and on_lock(user) is called
    after the lock is audited. None: no such live session.
    at: the phone's time of the event (epoch seconds; PT11-08b), stored as phone_at next to the entry's server time. It
    may be up to PIN_AT_SKEW_S ahead of the server or before the sign-in (a drifted phone clock); further out it raises
    ValueError and nothing is recorded. Idle time and the wrong-PIN count still go by arrival."""
    if kind not in PIN_EVENTS:
        raise ValueError(f"event must be one of {PIN_EVENTS}")
    p = resolve_token(store, token, idle_s, touch=False)
    if p is None:
        return None
    if at is not None:
        now, began = time.time(), store.session_get(token)["created_at"]
        if at > now + PIN_AT_SKEW_S:
            raise ValueError(f"at is more than {PIN_AT_SKEW_S} s ahead of the server clock")
        if at < began - PIN_AT_SKEW_S:
            raise ValueError(f"at is more than {PIN_AT_SKEW_S} s before this sign-in began")
    u = store.user_by_id(p.user_id)
    kw = {"token": token, "device": device, "address": address, "phone_at": at}
    if kind == "pin_unlock":
        store.session_touch(token, int(time.time()))
        _audit(store, "unlock", u, **kw, note="PIN")
        return {"session": "open", "wrong_in_a_row": 0}
    if kind == "pin_wrong":
        _audit(store, "unlock.failed", u, **kw, outcome="failed", note="wrong PIN")
        wrong = 0
        for e in audit.entries(store, object_type="session", object_id=session_ref(token), limit=PIN_MAX_WRONG):
            if e["action"] != "unlock.failed":
                break
            wrong += 1
        if wrong < PIN_MAX_WRONG:
            return {"session": "open", "wrong_in_a_row": wrong}
        note = f"{wrong} wrong PINs in a row reported"
    else:
        wrong, note = None, "lockout reported by the phone"
    if store.session_delete(token):
        _audit(store, "lock", u, **kw, outcome="refused", note=note + "; session ended")
        if on_lock is not None:
            on_lock(u)
    return {"session": "ended", "wrong_in_a_row": wrong}


class LoginThrottle:
    """Slows password guessing without letting anyone lock a colleague out (owner, 2026-10-30: per user and
    device). Wrong passwords are counted per user name and address together (`per_user`) and per address
    (`per_address`) within `window_s`; at either limit that device's sign-in is refused for the rest of the
    window, also for the right password, so a guesser learns nothing. The real user signs in from their own
    device. IPv6 addresses count by their /64, the share one network usually holds. attempt() checks and
    counts in one locked step, so parallel guesses cannot all slip past the check while their passwords are
    being verified; a right password then takes its count back. Kept in memory: a restart forgets it."""

    def __init__(self, per_user: int = 5, per_address: int = 20, window_s: int = 900, clock=time.time, keep: int = 10_000):
        self.per_user, self.per_address, self.window_s, self._clock, self._keep = per_user, per_address, window_s, clock, keep
        self._fails: dict[str, list[float]] = {}
        self._noted: dict[str, float] = {}
        self._lock = threading.Lock()

    @staticmethod
    def _keys(username: str, address: str | None) -> tuple[str, str]:
        addr = address or "?"
        if ":" in addr:
            try:
                addr = str(ipaddress.ip_network(addr.split("%")[0] + "/64", strict=False))
            except ValueError:
                pass
        return f"u:{username.strip().lower()}|{addr}", "a:" + addr

    def _recent(self, key: str, now: float) -> list[float]:
        got = [t for t in self._fails.get(key, ()) if now - t < self.window_s]
        if got:
            self._fails[key] = got
        else:
            self._fails.pop(key, None)
        return got

    def attempt(self, username: str, address: str | None) -> bool:
        """False when this device is blocked for this name; otherwise counts the attempt as wrong until
        succeeded() takes it back."""
        now = self._clock()
        user, addr = self._keys(username, address)
        with self._lock:
            if len(self._recent(user, now)) >= self.per_user or len(self._recent(addr, now)) >= self.per_address:
                return False
            if len(self._fails) >= self._keep:
                # Forget what has run out first, then the keys idle longest: never every block at once.
                for k in [k for k in self._fails if not self._recent(k, now)]:
                    del self._fails[k]
                for k in sorted(self._fails, key=lambda k: self._fails[k][-1])[:max(0, len(self._fails) - self._keep + 2)]:
                    del self._fails[k]
            for k in (user, addr):
                self._fails.setdefault(k, []).append(now)
            return True

    def first_refusal(self, username: str, address: str | None) -> bool:
        """True once per window for a refused name and device: the lock is audited once, not every refused try,
        so nobody can flood the audit trail by hammering a blocked sign-in."""
        now = self._clock()
        key = "|".join(self._keys(username, address))
        with self._lock:
            if now - self._noted.get(key, -self.window_s) < self.window_s:
                return False
            if len(self._noted) >= self._keep:
                self._noted = {k: t for k, t in self._noted.items() if now - t < self.window_s}
            self._noted[key] = now
            return True

    def succeeded(self, username: str, address: str | None) -> None:
        """A right password: this name's wrong tries from this device are forgiven, and the attempt is
        taken off the address's count."""
        user, addr = self._keys(username, address)
        with self._lock:
            self._fails.pop(user, None)
            if self._fails.get(addr):
                self._fails[addr].pop()
                if not self._fails[addr]:
                    del self._fails[addr]


def users_with_role(store, role: str, plant_id: str | None, line_id: str | None) -> list[dict]:
    """Active users holding `role` whose assignment scope covers (plant_id, line_id) - for resolving an
    alert rule's role-name recipients to real accounts (M4b notification targeting)."""
    out = []
    for u in store.user_list():
        if not u["active"]:
            continue
        p = Principal(kind="user", assignments=store.role_list(u["id"]))
        if any(a["role"] == role and p._covers(a, plant_id, line_id) for a in p.assignments):
            out.append(u)
    return out
