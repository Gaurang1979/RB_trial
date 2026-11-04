"""Licence rules, independent of Frappe so they can be tested on their own (tests/test_core.py).

verify() takes the licence record, the devices the PC reported and a function that looks a serial
number up in ERPNext, and returns the payload that api.py signs. The payload fields are read by
Vision Studio (dotnet/VisionCore/Licensing/Licence.cs): keep the names in step.
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import json
from dataclasses import dataclass, field
from datetime import date, datetime, time, timezone
from typing import Callable, Optional


@dataclass
class SerialInfo:
    """A Serial No record of ERPNext."""
    serial_no: str
    item_code: str
    item_name: str
    status: str                     # ERPNext Serial No status: Active, Inactive, Delivered, Expired
    customer: Optional[str] = None  # customer it was delivered to (when the ERPNext version keeps it)


@dataclass
class LicenceRecord:
    """A Vision Licence record."""
    licence_key: str
    customer: str
    status: str = "Active"                       # Active, Suspended, Cancelled
    valid_until: Optional[date] = None
    installation: Optional[str] = None           # bound on first activation
    tools: list[str] = field(default_factory=list)
    max_cameras: int = 0
    check_interval_hours: float = 24
    grace_days: float = 7
    devices: list[tuple[str, str]] = field(default_factory=list)   # (kind, serial) registered for this licence
    allow_customer_serials: bool = False         # also accept any serial delivered to this customer
    delivered_statuses: tuple[str, ...] = ("Delivered",)


def iso(dt: datetime) -> str:
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def device_verdict(lic: LicenceRecord, kind: str, serial: str, lookup: Callable[[str], Optional[SerialInfo]]) -> dict:
    """One device: OK when ERPNext knows the serial, it was delivered to this customer, and it is
    registered on this licence (or the licence accepts any serial delivered to the customer)."""
    info = lookup(serial)
    out = {"serial": serial, "kind": kind, "ok": False, "item": None, "reason": None}
    if info is None:
        out["reason"] = "serial number not registered with the supplier"
        return out
    out["item"] = f"{info.item_code} {info.item_name}".strip()
    registered = any(s.lower() == serial.lower() for _, s in lic.devices)
    if info.status not in lic.delivered_statuses:
        out["reason"] = f"serial number not delivered (status {info.status})"
    elif info.customer and info.customer != lic.customer:
        out["reason"] = "serial number delivered to another customer"
    elif not registered and not lic.allow_customer_serials:
        out["reason"] = "part not registered on this licence"
    else:
        out["ok"] = True
    return out


def verify(lic: Optional[LicenceRecord], licence_key: str, installation: str, nonce: str, devices: list[dict],
           lookup: Callable[[str], Optional[SerialInfo]], now: datetime) -> tuple[dict, Optional[str]]:
    """Returns (payload, installation to bind or None)."""
    payload = {
        "status": "active", "licence": licence_key, "customer": "", "installation": installation,
        "tools": [], "max_cameras": 0, "valid_until": None, "issued_at": iso(now),
        "next_check_hours": 24, "grace_days": 0, "nonce": nonce, "devices": [], "message": None,
    }
    if lic is None:
        payload.update(status="unknown", message="Unknown licence key.")
        return payload, None
    payload.update(customer=lic.customer, tools=list(lic.tools), max_cameras=lic.max_cameras,
                   next_check_hours=lic.check_interval_hours, grace_days=lic.grace_days)
    if lic.valid_until:
        # end of that day in the server's time zone (the ERPNext system time zone)
        payload["valid_until"] = iso(datetime.combine(lic.valid_until, time(23, 59, 59), tzinfo=now.tzinfo or timezone.utc))
    bind = None
    if lic.status != "Active":
        payload.update(status=lic.status.lower(), message=f"Licence {lic.status.lower()}: contact your supplier.")
    elif lic.valid_until and now.date() > lic.valid_until:
        payload.update(status="expired", message=f"Licence ended on {lic.valid_until:%d-%m-%Y}.")
    elif lic.installation and lic.installation != installation:
        payload.update(status="installation_mismatch",
                       message="This licence is in use on another PC: ask your supplier to move it.")
    elif not lic.installation:
        bind = installation
    seen = set()
    for d in devices:
        kind, serial = str(d.get("kind", "other"))[:20], str(d.get("serial", "")).strip()[:64]
        if serial and serial.lower() not in seen:
            seen.add(serial.lower())
            payload["devices"].append(device_verdict(lic, kind, serial, lookup))
    return payload, bind


def sign(payload: dict, private_key_pem: str) -> dict:
    """{"payload": base64 JSON, "signature": base64 DER ECDSA P-256 / SHA-256} as Vision Studio expects."""
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec

    key = serialization.load_pem_private_key(private_key_pem.encode(), password=None)
    data = json.dumps(payload, separators=(",", ":"), ensure_ascii=False).encode()
    sig = key.sign(data, ec.ECDSA(hashes.SHA256()))
    return {"payload": base64.b64encode(data).decode(), "signature": base64.b64encode(sig).decode()}


AUDIT_WARNING_NOTICE = "Do not share OTP with other users as change log is generated with values, It will be audited in your name - Risky."

# Daily rolling OTP for the Service (Tier 3) and OEM Factory (Tier 4) logins (Req 17.10 REQ-14, REQ-21; PROT-29, PROT-36).
# Vision Studio checks the same codes (dotnet/VisionCore/Licensing/Licence.cs, OemOtpVerifier): keep both in step.
# The secrets are never in the repository: api.py reads them from site_config.json. No secret, or a short one,
# gives no code and refuses every code (fail closed).
OTP_SERVICE_TIER = "service"
OTP_FACTORY_TIER = "factory"
OTP_MIN_SECRET_BYTES = 16


def calculate_otp(tier: str, licence_key: str, date_str: Optional[str], secret: Optional[str]) -> Optional[str]:
    """The 6-digit code of one tier, licence and UTC day (YYYY-MM-DD; today when empty), or None without a usable
    secret. HMAC-SHA256 over "vs-otp-v1|<tier>|<licence>|<date>", cut to 6 digits as RFC 4226 does."""
    if not secret or len(secret.encode()) < OTP_MIN_SECRET_BYTES or not licence_key or not licence_key.strip():
        return None
    day = (date_str or "").strip() or datetime.now(timezone.utc).strftime("%Y-%m-%d")
    try:
        datetime.strptime(day, "%Y-%m-%d")
    except ValueError:
        return None
    msg = f"vs-otp-v1|{tier}|{licence_key.strip()}|{day}".encode()
    mac = hmac.new(secret.encode(), msg, hashlib.sha256).digest()
    off = mac[-1] & 0x0F
    num = ((mac[off] & 0x7F) << 24) | (mac[off + 1] << 16) | (mac[off + 2] << 8) | mac[off + 3]
    return f"{num % 1_000_000:06d}"


def verify_otp(tier: str, licence_key: str, candidate_otp: str, date_str: Optional[str], secret: Optional[str]) -> bool:
    expected = calculate_otp(tier, licence_key, date_str, secret)
    candidate = (candidate_otp or "").strip()
    return expected is not None and len(candidate) == 6 and hmac.compare_digest(candidate, expected)


def calculate_daily_otp(licence_key: str, date_str: Optional[str], secret: Optional[str]) -> Optional[str]:
    """Service (Tier 3) code."""
    return calculate_otp(OTP_SERVICE_TIER, licence_key, date_str, secret)


def verify_daily_otp(licence_key: str, candidate_otp: str, date_str: Optional[str], secret: Optional[str]) -> bool:
    return verify_otp(OTP_SERVICE_TIER, licence_key, candidate_otp, date_str, secret)


def calculate_factory_otp(licence_key: str, date_str: Optional[str], secret: Optional[str]) -> Optional[str]:
    """OEM Factory (Tier 4) code, from its own secret so a service engineer cannot open factory settings."""
    return calculate_otp(OTP_FACTORY_TIER, licence_key, date_str, secret)


def verify_factory_otp(licence_key: str, candidate_otp: str, date_str: Optional[str], secret: Optional[str]) -> bool:
    return verify_otp(OTP_FACTORY_TIER, licence_key, candidate_otp, date_str, secret)
