"""Plant master data and devices (PL1; docs/vision-system/PLANT-PLATFORM.md section 2).

The plant server keeps plants, areas, lines and devices in its own tables, shared by every module
(Production, Vision, printing). The site JSON stays the description of Production's equipment model;
at start its plants, areas, lines and gateways are copied into the tables (sync_site), so a module
that knows only the tables sees the same plant. Lines that only a vision station or a printer uses
are added by an administrator through the API.

A device is anything that sends data: a vision station, a production gateway, a TIJ printer. It may
hold a token; the server stores only its SHA-256, so a copy of the database gives no working token,
and the token names the device, so a device never writes for another.
"""
from __future__ import annotations

import hashlib
import hmac
import re
import time

from .auth import new_token

DEVICE_KINDS = ("vision_station", "production_gateway", "tij_printer")
ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$")


def token_hash(token: str) -> str:
    return hashlib.sha256(token.encode()).hexdigest()


def check_id(value: str, what: str) -> str:
    if not isinstance(value, str) or not ID_RE.match(value):
        raise ValueError(f"{what} must be 1-64 letters, digits or _ . : - (starting with a letter or digit)")
    return value


def sync_site(store, raw: dict) -> dict:
    """Copies the site JSON's plants, areas, lines and gateways into the master tables. Safe to run at
    every start; returns what it wrote. Gateways become production_gateway devices without a token; MQTT
    ingest refuses their messages until an administrator issues one (POST /admin/devices/{id}/token)."""
    n = {"plants": 0, "areas": 0, "lines": 0, "gateways": 0}
    known = {d["id"] for d in store.device_list()}
    now = int(time.time())
    for p in raw.get("plants", []):
        store.master_upsert("plant", {"id": p["id"], "name": p["name"], "source": "site_json"})
        n["plants"] += 1
        for a in p.get("areas", []):
            store.master_upsert("area", {"id": a["id"], "plant_id": p["id"], "name": a.get("name", a["id"]), "source": "site_json"})
            n["areas"] += 1
            for ln in a.get("lines", []):
                store.master_upsert("line", {"id": ln["id"], "plant_id": p["id"], "area_id": a["id"], "name": ln["name"],
                                             "target_per_hour": float(ln.get("target_per_hour", 0.0)), "source": "site_json"})
                n["lines"] += 1
                gw = ln.get("gateway_id")
                if gw and gw not in known:
                    store.device_create({"id": gw, "kind": "production_gateway", "name": gw, "plant_id": p["id"], "line_id": ln["id"],
                                         "enabled": 1, "created_at": now, "created_by": "site_json", "source": "site_json"})
                    known.add(gw)
                    n["gateways"] += 1
    store.commit()
    return n


def issue_token(store, device_id: str) -> str:
    """A new token for the device (the old one stops working at once); shown once, stored as a hash."""
    token = new_token()
    store.device_update(device_id, token_hash=token_hash(token))
    store.commit()
    return token


def device_for_token(store, token: str | None) -> dict | None:
    """The enabled device a token belongs to, or None. The lookup is by hash, then compared again in
    constant time, so neither the timing nor a database copy gives the token away."""
    if not token:
        return None
    h = token_hash(token)
    d = store.device_by_token_hash(h)
    if d is None or not d["enabled"] or not hmac.compare_digest(d["token_hash"] or "", h):
        return None
    return d


def public_device(d: dict) -> dict:
    """A device as the API shows it: never the token hash, only whether a token is set."""
    out = {k: v for k, v in d.items() if k != "token_hash"}
    out["has_token"] = bool(d.get("token_hash"))
    return out
