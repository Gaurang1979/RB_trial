"""The relay's integration user gets the role "Remote Service Integration" (and nothing else it does not need)."""
import frappe

ROLE = "Remote Service Integration"


def after_install():
    if not frappe.db.exists("Role", ROLE):
        frappe.get_doc({"doctype": "Role", "role_name": ROLE, "desk_access": 0}).insert(ignore_permissions=True)
