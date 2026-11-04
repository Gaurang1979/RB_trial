"""Deterministic tests of EquipmentPoller's translation logic (edge.agent), driven by the MockSource so
every read is exactly what the test says it is - no real protocol, no timing flakiness. Real-protocol
correctness (Modbus, OPC-UA) is covered separately in test_edge_modbus.py / test_edge_opcua.py.
"""
import time

from edge.adapters.base import ReadError
from edge.adapters.mock import MockSource
from edge.agent import EquipmentPoller
from edge.config import EquipmentEdge, Point
from edge.gateway import EdgeGateway


class FakeSink:
    def __init__(self):
        self.events = []

    def emit(self, ev):
        self.events.append(ev)


CTX = {"plant_id": "P", "area_id": "A", "line_id": "L", "equipment_id": "PRESS-01"}


def digital(name, key):
    return Point(kind="digital", signal=name, address={"key": key})


def counter(role, key):
    return Point(kind="counter", role=role, address={"key": key})


def analog(sensor_id, key, scale=1.0, offset=0.0, sample_interval_s=10, name="temperature", unit="C"):
    return Point(kind="analog", sensor_id=sensor_id, name=name, unit=unit, address={"key": key}, scale=scale, offset=offset, sample_interval_s=sample_interval_s)


def make_poller(points, values, comm_fail_threshold=3, monkeypatch_source=None):
    cfg = EquipmentEdge(id="PRESS-01", protocol="mock", connection={}, poll_interval_s=1, points=tuple(points), comm_fail_threshold=comm_fail_threshold)
    gw = EdgeGateway("GW", FakeSink())
    clock = _FakeClock()
    poller = EquipmentPoller(cfg, gw, CTX, clock=clock.now, sleep=lambda s: None)
    poller.source = monkeypatch_source or MockSource(values)
    return poller, gw, clock


class _FakeClock:
    def __init__(self):
        self.t = 0.0

    def now(self):
        return self.t

    def advance(self, s):
        self.t += s


def events(gw):
    return gw._inner.sink.events if hasattr(gw._inner.sink, "events") else gw._inner.sink.events


# ---------------- digital signals ----------------
def test_first_poll_emits_every_digital_signal_as_a_full_resync():
    poller, gw, clk = make_poller([digital("machine_on", "on"), digital("running", "run"), digital("fault", "flt")],
                                  {"on": True, "run": False, "flt": False})
    poller.poll_once()
    sigs = [e["payload"] for e in events(gw) if e["type"] == "signal"]
    assert {(s["name"], s["value"]) for s in sigs} == {("machine_on", 1), ("running", 0), ("fault", 0)}


def test_unchanged_digital_signals_are_not_resent():
    poller, gw, clk = make_poller([digital("machine_on", "on")], {"on": True})
    poller.poll_once()
    poller.poll_once()
    poller.poll_once()
    assert len([e for e in events(gw) if e["type"] == "signal"]) == 1     # only the first-poll resync


def test_digital_signal_change_is_emitted_immediately():
    poller, gw, clk = make_poller([digital("running", "run")], {"run": False})
    poller.poll_once()
    poller.source.set("run", True)
    poller.poll_once()
    sigs = [e["payload"] for e in events(gw) if e["type"] == "signal"]
    assert [s["value"] for s in sigs] == [0, 1]


# ---------------- counters -> cycle events ----------------
def test_first_counter_reading_only_baselines_no_cycle_events_yet():
    poller, gw, clk = make_poller([counter("total", "cnt")], {"cnt": 100})
    poller.poll_once()
    assert [e for e in events(gw) if e["type"] == "cycle"] == []
    assert [e["payload"] for e in events(gw) if e["type"] == "counter"] == [{"total": 100, "good": 100, "reject": 0}]


def test_counter_increase_emits_that_many_cycle_events_with_averaged_cycle_time():
    poller, gw, clk = make_poller([counter("total", "cnt")], {"cnt": 100})
    poller.poll_once()
    clk.advance(30)
    poller.source.set("cnt", 103)
    poller.poll_once()
    cyc = [e["payload"] for e in events(gw) if e["type"] == "cycle"]
    assert len(cyc) == 3
    assert all(c["cycle_time_s"] == 10.0 for c in cyc)               # 30 s elapsed / 3 parts
    assert all(c["result"] == "good" for c in cyc)
    assert cyc[-1]["counters"] == {"total": 103, "good": 103, "reject": 0}


def test_reject_counter_reduces_good_and_is_reported():
    poller, gw, clk = make_poller([counter("total", "cnt"), counter("reject", "rej")], {"cnt": 100, "rej": 2})
    poller.poll_once()
    clk.advance(10)
    poller.source.set("cnt", 105)
    poller.source.set("rej", 3)
    poller.poll_once()
    cyc = [e["payload"] for e in events(gw) if e["type"] == "cycle"]
    assert len(cyc) == 5 and cyc[-1]["counters"] == {"total": 105, "good": 102, "reject": 3}


def test_counter_going_backwards_rebaselines_without_negative_or_fabricated_events():
    poller, gw, clk = make_poller([counter("total", "cnt")], {"cnt": 500})
    poller.poll_once()
    clk.advance(10)
    poller.source.set("cnt", 3)     # register reset / rollover
    poller.poll_once()
    assert [e for e in events(gw) if e["type"] == "cycle"] == []
    clk.advance(10)
    poller.source.set("cnt", 8)
    poller.poll_once()
    cyc = [e["payload"] for e in events(gw) if e["type"] == "cycle"]
    assert len(cyc) == 5 and all(c["cycle_time_s"] > 0 for c in cyc)


# ---------------- sensors ----------------
def test_sensor_applies_scale_and_offset():
    poller, gw, clk = make_poller([analog("PRESS-01.TEMP", "t", scale=0.1, offset=-2)], {"t": 653})
    poller.poll_once()
    s = [e["payload"] for e in events(gw) if e["type"] == "sensor"][0]
    assert s == {"sensor_id": "PRESS-01.TEMP", "name": "temperature", "value": 63.3, "unit": "C", "quality": "good"}


def test_sensor_only_read_on_its_own_sample_interval():
    poller, gw, clk = make_poller([analog("PRESS-01.TEMP", "t", sample_interval_s=10)], {"t": 50})
    poller.poll_once()                    # forced resync: reads immediately
    clk.advance(3)
    poller.poll_once()
    clk.advance(3)
    poller.poll_once()
    assert len([e for e in events(gw) if e["type"] == "sensor"]) == 1
    clk.advance(5)                        # total 11 s since the first read
    poller.poll_once()
    assert len([e for e in events(gw) if e["type"] == "sensor"]) == 2


# ---------------- comm_status: silence, not fabrication ----------------
def test_no_values_fabricated_on_read_failure():
    poller, gw, clk = make_poller([digital("machine_on", "on")], {"on": ReadError("comms down")})
    poller.poll_once()
    assert [e for e in events(gw) if e["type"] == "signal"] == []


def test_comm_status_false_after_threshold_consecutive_failures_then_true_on_recovery():
    poller, gw, clk = make_poller([digital("machine_on", "on")], {"on": ReadError("down")}, comm_fail_threshold=3)
    for _ in range(3):
        poller.poll_once()
    comm = [e["payload"] for e in events(gw) if e["type"] == "comm_status"]
    assert comm == [{"ok": False}]                                # exactly once, on the transition
    poller.source.set("on", True)
    poller.poll_once()
    comm = [e["payload"] for e in events(gw) if e["type"] == "comm_status"]
    assert comm == [{"ok": False}, {"ok": True}]
    sigs = [e["payload"] for e in events(gw) if e["type"] == "signal"]
    assert sigs and sigs[-1] == {"name": "machine_on", "value": 1}    # resumes reporting once reachable again


def test_transient_single_failure_below_threshold_does_not_raise_comm_status():
    poller, gw, clk = make_poller([digital("machine_on", "on")], {"on": ReadError("blip")}, comm_fail_threshold=3)
    poller.poll_once()
    poller.source.set("on", True)
    poller.poll_once()
    assert [e for e in events(gw) if e["type"] == "comm_status"] == []


def test_partial_failure_one_point_down_others_keep_reporting():
    poller, gw, clk = make_poller([digital("machine_on", "on"), analog("PRESS-01.TEMP", "t")], {"on": True, "t": ReadError("sensor wire cut")})
    poller.poll_once()
    assert [e["payload"] for e in events(gw) if e["type"] == "signal"] == [{"name": "machine_on", "value": 1}]
    assert [e for e in events(gw) if e["type"] == "sensor"] == []
    assert [e for e in events(gw) if e["type"] == "comm_status"] == []   # partial success: not "unreachable"


def test_no_data_at_all_this_poll_is_not_mistaken_for_a_failure():
    """If nothing was even due to be read this poll (e.g. an idle analog-only equipment between sample
    intervals), that must not silently count toward the comm-failure threshold."""
    poller, gw, clk = make_poller([analog("PRESS-01.TEMP", "t", sample_interval_s=100)], {"t": 50}, comm_fail_threshold=2)
    poller.poll_once()   # forced resync: reads
    for _ in range(10):
        clk.advance(1)
        poller.poll_once()   # nothing due; must not accumulate failures
    assert [e for e in events(gw) if e["type"] == "comm_status"] == []


def test_edge_agent_survives_on_connect_firing_before_construction_finishes(monkeypatch):
    """Regression: the MQTT sink's on_connect can fire the instant connect() is called - potentially before
    EdgeAgent has finished building self.gateways. That must never crash."""
    import edge.agent as agent_mod
    from edge.config import EdgeConfig, LineEdge

    class FakeSink:
        """Stands in for _MqttPublishSink: fires on_state_change synchronously inside __init__, exactly the
        race a real network thread could win against the rest of EdgeAgent.__init__ finishing."""

        def __init__(self, host, port, prefix, client_id, on_state_change, tokens=None):
            self.on_state_change = on_state_change
            on_state_change(True)   # simulate on_connect firing immediately, before gateways exist yet

        def emit(self, event):
            pass

        def close(self):
            pass

    monkeypatch.setattr(agent_mod, "_MqttPublishSink", FakeSink)
    cfg = EdgeConfig(org_id="O", mqtt_host="x", mqtt_port=1, mqtt_prefix="p",
                     lines=(LineEdge(id="L", plant_id="P", area_id="A", gateway_id="GW", equipment=()),))
    a = agent_mod.EdgeAgent(cfg)  # must not raise
    assert "L" in a.gateways and a.gateways["L"].online is True
