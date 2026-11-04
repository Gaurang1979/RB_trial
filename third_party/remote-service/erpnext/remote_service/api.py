"""Called by the relay (support.sundaramtech.com) for each finished remote service session:
POST https://<erp>/api/method/remote_service.api.record_session   body: {"session": "<JSON>"}
Authorization: token <api key>:<api secret> of an integration user with the role "Remote Service Integration".

Idempotent by session id: a session sent again answers ok with the log made the first time. The log is always made; the
draft Timesheet only when the settings ask for it and the engineer's Employee exists (otherwise the log says why).
"""
import json

import frappe
from frappe.utils import convert_utc_to_system_timezone

from remote_service.core import BadRecord, log_doc, parse, timesheet_doc
from remote_service.install import ROLE


def _to_local(utc_naive):
    return convert_utc_to_system_timezone(utc_naive).replace(tzinfo=None)


def _customer(licence: str):
    if licence and frappe.db.exists("DocType", "Vision Licence") and frappe.db.exists("Vision Licence", licence):
        return frappe.db.get_value("Vision Licence", licence, "customer")
    return None


@frappe.whitelist(methods=["POST"])
def record_session(session: str = ""):
    roles = frappe.get_roles()
    if ROLE not in roles and "System Manager" not in roles:
        frappe.throw("Not permitted", frappe.PermissionError)
    try:
        s = parse(json.loads(session or "null"))
    except (ValueError, BadRecord) as e:
        return {"ok": False, "error": str(e)}

    if frappe.db.exists("Remote Service Log", s.session_id):
        return {"ok": True, "log": s.session_id, "again": True}

    customer = _customer(s.licence)
    log = frappe.get_doc(log_doc(s, customer, _to_local))
    log.insert(ignore_permissions=True)

    settings = frappe.get_single("Remote Service Settings")
    note = ""
    if not settings.create_timesheet:
        note = "Timesheets are switched off in Remote Service Settings."
    elif not s.employee:
        note = "The relay account has no Employee; no Timesheet was made."
    elif not frappe.db.exists("Employee", s.employee):
        note = f"Employee {s.employee} does not exist; no Timesheet was made."
    else:
        ts = frappe.get_doc(timesheet_doc(s, customer, settings.activity_type, _to_local))
        ts.insert(ignore_permissions=True)   # a draft: the engineer adds the work note and submits it
        log.db_set("timesheet", ts.name, update_modified=False)
    if note:
        log.db_set("timesheet_note", note, update_modified=False)
    frappe.db.commit()
    return {"ok": True, "log": log.name, "timesheet": log.timesheet or None, "note": note}
