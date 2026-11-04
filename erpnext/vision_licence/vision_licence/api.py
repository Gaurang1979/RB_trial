"""Licence check called by Vision Studio:
POST https://<licence subdomain>/api/method/vision_licence.api.verify
form fields: licence_key, installation, version, nonce, devices (JSON list of {kind, serial}).
No login: the answer is signed with the private key in site_config.json ("vision_licence_private_key").
"""
import json
from datetime import datetime, timezone

import frappe
from frappe.rate_limiter import rate_limit
from frappe.utils import getdate, now_datetime

from vision_licence.core import (
    AUDIT_WARNING_NOTICE,
    LicenceRecord,
    SerialInfo,
    calculate_daily_otp,
    calculate_factory_otp,
    sign,
    verify as verify_core,
)


def _licence(key: str):
    if not key or not frappe.db.exists("Vision Licence", key):
        return None
    doc = frappe.get_doc("Vision Licence", key)
    return doc, LicenceRecord(
        licence_key=doc.name, customer=doc.customer, status=doc.status, valid_until=getdate(doc.valid_until) if doc.valid_until else None,
        installation=doc.installation or None,
        tools=[t.strip() for t in (doc.tools or "").split(",") if t.strip()],
        max_cameras=doc.max_cameras or 0, check_interval_hours=doc.check_interval_hours or 24,
        grace_days=doc.grace_days if doc.grace_days is not None else 7,
        devices=[(d.kind, d.serial_no) for d in doc.devices],
        allow_customer_serials=bool(doc.allow_customer_serials),
    )


def _serial(serial: str):
    meta = frappe.get_meta("Serial No")
    fields = ["name", "item_code", "status"] + (["customer"] if meta.has_field("customer") else [])
    row = frappe.db.get_value("Serial No", serial, fields, as_dict=True)
    if not row:
        return None
    item_name = frappe.db.get_value("Item", row.item_code, "item_name") or ""
    return SerialInfo(row.name, row.item_code, item_name, row.status, row.get("customer"))


@frappe.whitelist(allow_guest=True, methods=["POST"])
@rate_limit(limit=60, seconds=60 * 60)
def verify(licence_key: str = "", installation: str = "", version: str = "", nonce: str = "", devices: str = "[]"):
    key = frappe.conf.get("vision_licence_private_key")
    if not key:
        frappe.throw("Licence signing key not configured", frappe.ValidationError)
    try:
        devs = json.loads(devices or "[]")
        if not isinstance(devs, list):
            devs = []
    except ValueError:
        devs = []
    found = _licence(licence_key.strip())
    doc, lic = found if found else (None, None)
    payload, bind = verify_core(lic, licence_key.strip(), installation.strip()[:64], nonce.strip()[:64], devs[:50],
                                _serial, now_datetime().astimezone())
    if doc and bind:
        doc.db_set("installation", bind)
    if doc:
        doc.db_set("last_check", now_datetime())
    frappe.get_doc({
        "doctype": "Vision Licence Check", "licence": doc.name if doc else None, "licence_key": licence_key[:140],
        "installation": installation[:64], "version": version[:32], "status": payload["status"],
        "ip_address": frappe.local.request_ip,
        "unlicensed_parts": ", ".join(f"{d['kind']} {d['serial']} ({d['reason']})" for d in payload["devices"] if not d["ok"]),
    }).insert(ignore_permissions=True)
    frappe.db.commit()
    return sign(payload, key)


SERVICE_OTP_ROLES = {"OEM Service", "OEM Factory Admin", "System Manager"}
FACTORY_OTP_ROLES = {"OEM Factory Admin", "System Manager"}


def _issue_otp(tier_name: str, roles: set, conf_key: str, calc, licence_key: str, date_str: str) -> dict:
    """Issues one OTP to a signed-in user holding one of the roles (Req 17.10 REQ-14, REQ-21; PROT-29, PROT-36).
    The secret comes from site_config.json (conf_key), never from the repository; without it no code is issued.
    Dates are UTC, as on the line PC."""
    if not roles.intersection(frappe.get_roles()):
        frappe.throw(f"Access denied: your role cannot issue the {tier_name} OTP.", frappe.PermissionError)
    clean_lic = (licence_key or "").strip()
    if not clean_lic:
        frappe.throw("licence_key is required", frappe.ValidationError)
    day = (date_str or "").strip() or datetime.now(timezone.utc).strftime("%Y-%m-%d")
    otp = calc(clean_lic, day, frappe.conf.get(conf_key))
    if otp is None:
        frappe.throw(f"The {tier_name} OTP is not set up on this server (or the date is wrong).", frappe.ValidationError)
    frappe.logger("vision_licence").info(f"{tier_name} OTP issued for {clean_lic} on {day} to user {frappe.session.user}")
    return {
        "licence_key": clean_lic,
        "date": day,
        "otp": otp,
        "valid_until": f"{day}T23:59:59Z",
        "warning_notice": AUDIT_WARNING_NOTICE,
        "issued_to": frappe.session.user,
    }


@frappe.whitelist(methods=["POST"])
def get_oem_daily_otp(licence_key: str = "", date_str: str = ""):
    """Today's Service (Tier 3) OTP for a line, for OEM service engineers (REQ-14 / PROT-29)."""
    return _issue_otp("Service", SERVICE_OTP_ROLES, "vision_service_otp_secret", calculate_daily_otp, licence_key, date_str)


@frappe.whitelist(methods=["POST"])
def get_factory_setting_otp(licence_key: str = "", date_str: str = ""):
    """Today's OEM Factory Setting (Tier 4) OTP, for 'OEM Factory Admin' only (REQ-21 / PROT-36)."""
    return _issue_otp("Factory Setting", FACTORY_OTP_ROLES, "vision_factory_otp_secret", calculate_factory_otp, licence_key, date_str)
