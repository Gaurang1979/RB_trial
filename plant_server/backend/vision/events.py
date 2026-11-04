"""Plant events v1, as dotnet/PlantEvents defines them (docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md 3).

parse() reads one event object and returns the problems that make it unusable (empty when valid). The
server keeps an unusable event as a reject; it never blocks the station's queue.
"""
from __future__ import annotations

import math
import re

SCHEMA = 1
MAX_TS = 253_402_300_799_999  # 9999-12-31 in milliseconds; a micro- or nanosecond time is past it
KINDS = ("inspection.result", "line.state", "counter.snapshot", "alarm.raised", "alarm.cleared", "alarm.acknowledged",
         "job.changed", "model.deployed", "health.sample", "heartbeat")
LINE_STATES = ("running", "stopped", "starved", "blocked", "changeover", "fault")
PRIORITIES = ("high", "medium", "low")


def _str(v) -> bool:
    return isinstance(v, str) and v.strip() != ""


INT64 = 2**63 - 1
INT32 = 2**31 - 1


def _int(v, top: int = INT64) -> bool:
    """A whole number the station's C# side can hold (long by default)."""
    return isinstance(v, int) and not isinstance(v, bool) and -top - 1 <= v <= top


def _num(v) -> bool:
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        return False
    try:
        return math.isfinite(float(v))
    except OverflowError:   # an integer beyond any double
        return False


def _opt_str(v) -> bool:
    return v is None or isinstance(v, str)


CODE = re.compile(r"[0-9a-f]{64}")


def _text_ok(v, lo: int, hi: int) -> bool:
    """Text of lo..hi characters that UTF-8 and the database can hold (no NUL, no lone surrogate)."""
    if not isinstance(v, str) or not lo <= len(v) <= hi or "\x00" in v:
        return False
    try:
        v.encode("utf-8")
    except UnicodeEncodeError:
        return False
    return True


def changeover_problems(c) -> list[str]:
    """The heartbeat's `changeover` object (docs/vision-system/CHANGEOVER-VERIFY.md section 2). The code is checked against the
    record in vision.changeover; this checks the types and the lengths."""
    if not isinstance(c, dict):
        return ["changeover is not an object"]
    p = []
    if not isinstance(c.get("code"), str) or not CODE.fullmatch(c["code"]):
        p.append("changeover code is not 64 lower-case hex characters")
    if not _int(c.get("ask")) or c["ask"] < 1:
        p.append("changeover ask is not a whole number of 1 or more")
    if not _text_ok(c.get("job"), 1, 128):
        p.append("changeover job is not 1 to 128 characters")
    if not _int(c.get("job_version"), INT32) or c["job_version"] < 0:
        p.append("changeover job_version is not a whole number of 0 or more")
    if "job_digest" in c and (not isinstance(c["job_digest"], str) or (c["job_digest"] and not CODE.fullmatch(c["job_digest"]))):
        p.append("changeover job_digest is not empty or 64 lower-case hex characters")
    if "variant" in c and not _text_ok(c["variant"], 0, 256):
        p.append("changeover variant is not text of at most 256 characters")
    if "operator" in c and not _text_ok(c["operator"], 0, 64):
        p.append("changeover operator is not text of at most 64 characters")
    if "changed_at" in c and (not _int(c["changed_at"]) or c["changed_at"] < 0):
        p.append("changeover changed_at is not a time in milliseconds")
    la = c.get("accepted_at_line")
    if la is not None and (not isinstance(la, dict) or not _text_ok(la.get("by"), 1, 64) or not _text_ok(la.get("name", ""), 0, 128)
                           or not _int(la.get("at")) or la["at"] <= 0):
        p.append("changeover accepted_at_line needs a user of 1 to 64, a name of at most 128 characters and a time")
    batch = c.get("batch")
    if not isinstance(batch, list) or len(batch) > 32:
        p.append("changeover batch is not a list of at most 32 fields")
    elif any(not isinstance(f, dict) or not _text_ok(f.get("key"), 1, 64) or not _text_ok(f.get("label"), 0, 128)
             or not _text_ok(f.get("value"), 0, 256) for f in batch):
        p.append("a changeover batch field needs a key of 1 to 64, a label of at most 128 and a value of at most 256 characters")
    return p


def queue_id_problem(q) -> str | None:
    """The queue id is a key in the database: a short string without NUL."""
    if not isinstance(q, str) or not q.strip():
        return "an element has no queue_id"
    if len(q) > 200 or "\x00" in q:
        return "queue_id is too long or holds a NUL"
    return None


def problems(e) -> list[str]:
    """What is wrong with one event (empty when it is valid). Mirrors PlantEvent.Validate in C#."""
    if not isinstance(e, dict):
        return ["not a JSON object"]
    kind = e.get("kind")
    if kind not in KINDS:
        return [f"unknown kind {kind!r}"]
    if e.get("schema") != SCHEMA:
        return [f"schema {e.get('schema')!r} is not supported (this reads {SCHEMA})"]
    p = []
    for f in ("plant", "line", "station"):
        if not _str(e.get(f)):
            p.append(f"{f} is empty")
    ts, seq = e.get("ts"), e.get("seq")
    if not _int(ts) or ts <= 0:
        p.append("ts is not set")
    elif ts > MAX_TS:
        p.append("ts is not in milliseconds (past year 9999)")
    if not _int(seq) or seq < 0:
        p.append("seq is not a whole number of 0 or more")
    elif seq == 0 and kind != "heartbeat":
        p.append("seq is not set")
    elif seq > 0 and kind != "heartbeat" and not _str(e.get("queue_id")):
        p.append("a stored event has no queue_id")
    if e.get("job_version") is not None and (not _int(e["job_version"], INT32) or e["job_version"] < 0):
        p.append("job_version is not a whole number of 0 or more")
    for f in ("camera", "job"):
        if not _opt_str(e.get(f)):
            p.append(f"{f} is not text")

    if kind == "inspection.result":
        ok, reasons = e.get("ok"), e.get("reasons", [])
        for f in ("part_id", "image_id"):
            if not _opt_str(e.get(f)):
                p.append(f"{f} is not text")
        models = e.get("models", {})
        if not isinstance(models, dict) or any(not isinstance(v, str) for v in models.values()):
            p.append("models must map tools to version text")
        if not isinstance(ok, bool):
            p.append("ok is missing")
        if not isinstance(reasons, list) or any(not isinstance(r, dict) or not _str(r.get("tool")) or not _str(r.get("reason")) for r in reasons):
            p.append("reasons must be a list of {tool, reason}")
        elif ok is False and not reasons:
            p.append("an NG result has no reason")
        elif ok is True and reasons:
            p.append("an OK result has NG reasons")
        if not _num(e.get("cycle_ms", 0)) or e.get("cycle_ms", 0) < 0:
            p.append("cycle_ms is not a number of 0 or more")
        for field in ("values", "scores"):
            m = e.get(field, {})
            if not isinstance(m, dict) or any(not _num(v) for v in m.values()):
                p.append(f"{field} must hold finite numbers")
    elif kind == "line.state":
        if e.get("state") not in LINE_STATES:
            p.append(f"state {e.get('state')!r} is not a known value")
        if not _opt_str(e.get("reason")):
            p.append("reason is not text")
    elif kind == "counter.snapshot":
        if not all(_int(e.get(f)) for f in ("good", "bad", "total", "shift_start")):
            p.append("good, bad, total and shift_start are required whole numbers")
        elif e["good"] < 0 or e["bad"] < 0:
            p.append("a counter is negative")
        elif e["total"] != e["good"] + e["bad"]:
            p.append("total is not good + bad")
    elif kind.startswith("alarm."):
        if not _str(e.get("code")):
            p.append("alarm code is empty")
        if e.get("priority", "medium") not in PRIORITIES:
            p.append("priority is not a known value")
        if not _opt_str(e.get("by")):
            p.append("by is not text")
        elif kind == "alarm.acknowledged" and not _str(e.get("by")):
            p.append("an acknowledgement has no user")
    elif kind == "job.changed":
        if not _str(e.get("to")):
            p.append("job.changed has no new job")
        for f in ("from", "by", "signature_id"):
            if not _opt_str(e.get(f)):
                p.append(f"{f} is not text")
    elif kind == "model.deployed":
        if not _str(e.get("tool")) or not _str(e.get("model_version")):
            p.append("model.deployed has no tool or model version")
        for f in ("by", "validation"):
            if not _opt_str(e.get(f)):
                p.append(f"{f} is not text")
    elif kind == "health.sample":
        for f in ("cpu_pct", "gpu_pct", "ram_pct", "disk_pct"):
            v = e.get(f)
            if v is not None and (not _num(v) or not 0 <= v <= 100):
                p.append(f"{f} is not 0-100")
        for f in ("focus_score", "brightness", "exposure_us", "temp_c"):
            v = e.get(f)
            if v is not None and not _num(v):
                p.append(f"{f} is not a finite number")
        fd = e.get("frame_drops")
        if fd is not None and (not _int(fd) or fd < 0):
            p.append("frame_drops is negative")
    elif kind == "heartbeat":
        if not isinstance(e.get("agent_version", ""), str):
            p.append("agent_version is not text")
        if e.get("queue_id") is not None and queue_id_problem(e["queue_id"]):
            p.append("queue_id is not a short text")
        for f in ("queue_depth", "dropped"):
            v = e.get(f, 0)
            if not _int(v) or v < 0:
                p.append(f"{f} is not a whole number of 0 or more")
        if e.get("changeover") is not None:
            p += changeover_problems(e["changeover"])
    return p
