"""Accounts, machines, session logs, the ERPNext outbox and the audit trail of the relay, kept as files in one data folder.

Secrets are stored only as hashes: engineer passwords with PBKDF2, machine link tokens with SHA-256.
Files are read again when they change, so `add-user` and `add-machine` count without a restart.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import secrets
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path

MACHINE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{1,40}$")
ACCOUNT_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._@-]{1,63}$")
PBKDF2_ROUNDS = 200_000
MIN_PASSWORD = 10
# never machine numbers: the relay's own pages
RESERVED = {"login", "logout", "link", "static", "api", "favicon.ico"}


def hash_password(password: str, salt: str | None = None, rounds: int = PBKDF2_ROUNDS) -> str:
    salt = salt or secrets.token_hex(16)
    digest = hashlib.pbkdf2_hmac("sha256", password.encode(), salt.encode(), rounds)
    return f"pbkdf2${rounds}${salt}${digest.hex()}"


def verify_password(password: str, stored: str | None) -> bool:
    try:
        _, rounds, salt, _ = (stored or "").split("$")
        return hmac.compare_digest(hash_password(password, salt, int(rounds)), stored or "")
    except ValueError:
        return False


def hash_token(token: str) -> str:
    return hashlib.sha256(token.encode()).hexdigest()


ROLES = ("service", "oem")


@dataclass
class Account:
    name: str
    full_name: str
    password: str
    machines: list[str] = field(default_factory=list)   # "*" = every machine
    role: str = "service"                                # service | oem (DESIGN.md section 3)
    employee: str = ""                                   # the ERPNext Employee the work time goes to

    def may_open(self, machine_id: str) -> bool:
        return "*" in self.machines or machine_id in self.machines


@dataclass
class Machine:
    id: str
    name: str
    site: str
    token: str         # SHA-256 of the link token
    licence: str = ""  # the customer's licence key in ERPNext (Vision Licence), for the service log


class Store:
    def __init__(self, folder: str | Path):
        self.folder = Path(folder)
        self.folder.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._cache: dict[str, tuple[float, object]] = {}

    # ---- files ------------------------------------------------------------------------------

    def _read(self, name: str) -> dict:
        path = self.folder / name
        with self._lock:
            at = path.stat().st_mtime if path.exists() else 0.0
            cached = self._cache.get(name)
            if cached and cached[0] == at:
                return cached[1]   # type: ignore[return-value]
            try:
                data = json.loads(path.read_text()) if at else {}
            except (OSError, ValueError):
                data = {}   # a broken file means nobody gets in, never everybody
            self._cache[name] = (at, data)
            return data

    def _write(self, name: str, data: dict) -> None:
        path = self.folder / name
        tmp = path.with_suffix(".tmp")
        with self._lock:
            tmp.write_text(json.dumps(data, indent=2))
            os.chmod(tmp, 0o600)
            os.replace(tmp, path)
            self._cache.pop(name, None)

    # ---- accounts ---------------------------------------------------------------------------

    def account(self, name: str) -> Account | None:
        a = self._read("accounts.json").get(name.lower())
        return Account(name.lower(), a["full_name"], a["password"], list(a.get("machines", [])), a.get("role", "service"),
                       a.get("employee", "")) if a else None

    def save_account(self, name: str, full_name: str, password: str, machines: list[str], role: str = "service",
                     employee: str = "") -> None:
        if not ACCOUNT_NAME.match(name):
            raise ValueError("an account name is 2-64 letters, digits, '.', '_', '-' or '@'")
        if len(password) < MIN_PASSWORD:
            raise ValueError(f"a password is at least {MIN_PASSWORD} characters")
        if role not in ROLES:
            raise ValueError("the role is service or oem")
        data = dict(self._read("accounts.json"))
        data[name.lower()] = {"full_name": full_name, "password": hash_password(password), "machines": machines,
                              "role": role, "employee": employee.strip()}
        self._write("accounts.json", data)

    def login(self, name: str, password: str) -> Account | None:
        a = self.account(name)
        # the same work for an unknown name, so the answer time does not tell which names exist
        ok = verify_password(password, a.password if a else hash_password("x" * MIN_PASSWORD, "0" * 32))
        return a if a and ok else None

    # ---- machines ---------------------------------------------------------------------------

    def machine(self, machine_id: str) -> Machine | None:
        m = self._read("machines.json").get(machine_id)
        return Machine(machine_id, m["name"], m.get("site", ""), m["token"], m.get("licence", "")) if m else None

    def machines(self) -> list[Machine]:
        return [Machine(k, v["name"], v.get("site", ""), v["token"], v.get("licence", ""))
                for k, v in sorted(self._read("machines.json").items())]

    def add_machine(self, machine_id: str, name: str, site: str, licence: str = "") -> str:
        """Registers a machine (or gives it a new token) and returns its link token, shown once."""
        if not MACHINE_ID.match(machine_id) or machine_id.lower() in RESERVED:
            raise ValueError("a machine number is 2-41 letters, digits, '_' or '-', and not one of the relay's own names")
        token = secrets.token_urlsafe(32)
        data = dict(self._read("machines.json"))
        data[machine_id] = {"name": name, "site": site, "token": hash_token(token), "licence": licence.strip()}
        self._write("machines.json", data)
        return token

    def machine_by_token(self, machine_id: str, token: str) -> Machine | None:
        m = self.machine(machine_id)
        return m if m and token and hmac.compare_digest(m.token, hash_token(token)) else None

    # ---- audit ------------------------------------------------------------------------------

    def audit(self, who: str, event: str, machine: str = "", detail: str = "") -> dict:
        entry = {"time": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "who": who, "event": event,
                 "machine": machine, "detail": detail}
        with self._lock, open(self.folder / f"audit-{entry['time'][:7]}.jsonl", "a") as f:
            f.write(json.dumps(entry) + "\n")
        return entry

    def audit_entries(self, limit: int = 200) -> list[dict]:
        files = sorted(self.folder.glob("audit-*.jsonl"))
        lines: list[str] = []
        for path in reversed(files):
            lines = path.read_text().splitlines() + lines
            if len(lines) >= limit:
                break
        return [json.loads(line) for line in lines[-limit:]]


# ---- session logs and the ERPNext outbox (DESIGN.md sections 4 and 5) ------------------------------------------------------

SESSION_ID = re.compile(r"^[0-9a-f]{8,32}$")


class SessionLog:
    """What each remote session did, as the machine reported it (PROTOCOL.md section 5). One JSON-lines file per session
    (sessions/<machine>/<id>.jsonl); at the session's end one record goes to the outbox for ERPNext."""

    def __init__(self, store: Store):
        self.store = store
        self.folder = store.folder / "sessions"
        self.outbox = store.folder / "outbox"
        self.sent = store.folder / "sent"
        self.anchors = store.folder / "anchors"
        for f in (self.folder, self.outbox, self.sent, self.anchors):
            f.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._highest: dict[str, dict[str, int]] = {}   # machine -> epoch -> highest number saved

    def _seq_file(self, machine: str) -> Path:
        return self.folder / machine / "seq.json"

    def already_have(self, machine: str, epoch: str, seq: int) -> bool:
        """True when this numbered entry was saved before (the machine sent it again after a link drop)."""
        if not MACHINE_ID.match(machine):
            return True
        with self._lock:
            if machine not in self._highest:
                try:
                    self._highest[machine] = json.loads(self._seq_file(machine).read_text())
                except (OSError, ValueError):
                    self._highest[machine] = {}
            return seq <= self._highest[machine].get(epoch, 0)

    def saved(self, machine: str, epoch: str, seq: int) -> None:
        with self._lock:
            known = self._highest.setdefault(machine, {})
            known[epoch] = max(seq, known.get(epoch, 0))
            # a machine's runs come and go: the newest 50 are enough to recognise any resent entry
            if len(known) > 50:
                for old in sorted(known, key=known.get)[:len(known) - 50]:
                    del known[old]
            path = self._seq_file(machine)
            path.parent.mkdir(parents=True, exist_ok=True)
            tmp = path.with_suffix(".tmp")
            tmp.write_text(json.dumps(known))
            os.replace(tmp, path)

    def _file(self, machine: str, session: str) -> Path:
        return self.folder / machine / f"{session}.jsonl"

    def add(self, machine: str, entry: dict) -> dict | None:
        """Keeps one entry. Returns the finished session record when this entry ended it, else None."""
        session = str(entry.get("session", ""))
        if not MACHINE_ID.match(machine) or not SESSION_ID.match(session):
            return None
        clean = {k: entry.get(k) for k in ("time", "session", "user", "role", "kind", "detail", "data")}
        if not isinstance(clean.get("data"), dict):
            clean["data"] = {}
        path = self._file(machine, session)
        with self._lock:
            path.parent.mkdir(parents=True, exist_ok=True)
            with open(path, "a") as f:
                f.write(json.dumps(clean) + "\n")
        if clean["kind"] == "session_end":
            return self.finish(machine, session, str(clean["data"].get("endReason", "")))
        return None

    def entries(self, machine: str, session: str) -> list[dict]:
        path = self._file(machine, session)
        if not path.exists():
            return []
        return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]

    def open_sessions(self, machine: str) -> list[str]:
        """Sessions of this machine that started and are not finished yet."""
        folder = self.folder / machine
        if not folder.exists():
            return []
        done = {p.stem for p in self.outbox.glob(f"{machine}--*.json")} | {p.stem for p in self.sent.glob(f"{machine}--*.json")}
        return [p.stem for p in folder.glob("*.jsonl") if f"{machine}--{p.stem}" not in done]

    def finish(self, machine: str, session: str, end_reason: str) -> dict | None:
        """Builds the session's record for ERPNext and puts it in the outbox, once."""
        name = f"{machine}--{session}"
        with self._lock:
            if (self.outbox / f"{name}.json").exists() or (self.sent / f"{name}.json").exists():
                return None
            entries = self.entries(machine, session)
            start = next((e for e in entries if e.get("kind") == "session_start"), None)
            if start is None:
                return None
            m = self.store.machine(machine)
            account = self.store.account(str(start.get("user", "")))
            end = next((e for e in reversed(entries) if e.get("kind") == "session_end"), None)
            head = str(((end or {}).get("data") or {}).get("chainHead", ""))[:128]
            record = {
                "session_id": session,
                "machine": machine,
                "machine_name": m.name if m else "",
                "site": m.site if m else "",
                "licence": m.licence if m else "",
                "engineer": start.get("user", ""),
                "engineer_name": account.full_name if account else "",
                "employee": account.employee if account else "",
                "role": start.get("role", ""),
                "reason": (start.get("data") or {}).get("reason", ""),
                "started": start.get("time", ""),
                "ended": entries[-1].get("time", ""),
                "end_reason": end_reason,
                "ledger_head": head,
                "actions": [{"time": e.get("time", ""), "kind": e.get("kind", ""), "detail": e.get("detail", "")}
                            for e in entries if e.get("kind") not in ("session_start", "session_end")],
            }
            if head:
                # the machine's log as it stood at the session's end, kept outside the machine
                with open(self.anchors / f"{machine}.jsonl", "a") as f:
                    f.write(json.dumps({"time": record["ended"], "session": session, "head": head}) + "\n")
            tmp = self.outbox / f"{name}.tmp"
            tmp.write_text(json.dumps(record, indent=1))
            os.replace(tmp, self.outbox / f"{name}.json")
            return record

    def pending(self) -> list[Path]:
        return sorted(self.outbox.glob("*.json"))

    def mark_sent(self, path: Path) -> None:
        os.replace(path, self.sent / path.name)
