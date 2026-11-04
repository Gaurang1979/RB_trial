"""PL2: live state of vision lines (a port of the .NET LiveFloor tests)."""
from backend.vision.live import VisionFloor

NOW = 1_793_280_000_000


class Clock:
    def __init__(self):
        self.now = NOW

    def __call__(self):
        return self.now


def at(kind, station, ago_s=0, line="Line 4", **kw):
    return {"kind": kind, "plant": "P1", "line": line, "station": station, "ts": NOW - ago_s * 1000, **kw}


def test_counters_are_summed_over_stations_with_yield():
    f = VisionFloor(Clock())
    f.apply(at("counter.snapshot", "S1", 5, good=90, bad=10, total=100))
    f.apply(at("counter.snapshot", "S2", 5, good=99, bad=1, total=100))
    f.apply(at("counter.snapshot", "S1", 30, good=80, bad=0, total=80))   # older: ignored
    ln = f.line("P1", "Line 4")
    assert (ln["good"], ln["bad"], ln["total"], ln["yield_pct"]) == (189, 11, 200, 94.5)


def test_last_ng_newest_first_kept_to_twenty_with_top_reason():
    f = VisionFloor(Clock())
    for i in range(25):
        f.apply(at("inspection.result", "S1", 100 - i, ok=False, reasons=[{"tool": "caliper", "reason": "too_large" if i % 3 == 0 else "too_small"}],
                   image_id=f"img{i}"))
    ln = f.line("P1", "Line 4")
    assert len(ln["last_ng"]) == 20 and ln["last_ng"][0]["image_id"] == "img24"
    assert ln["top_ng"] == "too_small"


def test_alarms_raise_acknowledge_and_clear_high_first():
    f = VisionFloor(Clock())
    f.apply(at("alarm.raised", "S1", 50, code="focus", priority="low"))
    f.apply(at("alarm.raised", "S1", 40, code="plc_lost", priority="high"))
    f.apply(at("alarm.raised", "S2", 30, code="dark", priority="medium"))
    f.apply(at("alarm.acknowledged", "S2", 20, code="dark", by="ravi"))
    f.apply(at("alarm.cleared", "S1", 10, code="focus"))
    a = f.line("P1", "Line 4")["alarms"]
    assert [x["code"] for x in a] == ["plc_lost", "dark"] and a[1]["acknowledged_by"] == "ravi"


def test_an_older_state_never_replaces_a_newer_one():
    f = VisionFloor(Clock())
    f.apply(at("line.state", "S1", 10, state="stopped", reason="plc"))
    f.apply(at("line.state", "S1", 60, state="running"))
    ln = f.line("P1", "Line 4")
    assert (ln["state"], ln["state_reason"]) == ("stopped", "plc")


def test_a_line_with_no_live_station_shows_offline():
    c = Clock()
    f = VisionFloor(c)
    f.apply(at("line.state", "S1", 1, state="running"))
    f.heartbeat({"plant": "P1", "line": "Line 4", "station": "S1", "agent_version": "1"}, NOW - 1000)
    assert f.line("P1", "Line 4")["state"] == "running"
    c.now += 20_000
    ln = f.line("P1", "Line 4")
    assert ln["state"] == "offline" and ln["stations"][0]["connected"] is False


def test_parts_per_minute_counts_the_last_minute():
    f = VisionFloor(Clock())
    for i in range(30):
        f.apply(at("inspection.result", "S1", i * 3, ok=True))
    assert f.line("P1", "Line 4")["parts_per_minute"] == 21


def test_lines_are_in_natural_order():
    f = VisionFloor(Clock())
    for n in ("Line 10", "Line 2", "Line 1"):
        f.apply(at("inspection.result", "S1", line=n, ok=True))
    assert [x["line"] for x in f.floor()] == ["Line 1", "Line 2", "Line 10"]


def test_a_station_clock_ahead_does_not_freeze_the_line():
    c = Clock()
    f = VisionFloor(c)
    f.apply(at("line.state", "S1", -3600, state="stopped"))   # an hour in the future
    f.apply(at("line.state", "S1", 0, state="running"))
    assert f.line("P1", "Line 4")["state"] == "stopped"   # capped to now + 1 min, still newest
    c.now += 120_000
    f.apply({**at("line.state", "S1", 0, state="running"), "ts": c.now})
    assert f.line("P1", "Line 4")["state"] == "running"


def test_job_follows_job_changes():
    f = VisionFloor(Clock())
    f.apply(at("job.changed", "S1", to="Carton-24"))
    assert f.line("P1", "Line 4")["job"] == "Carton-24"
