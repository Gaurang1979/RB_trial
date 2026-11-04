import json

import pytest

from edge.config import ConfigError, parse_config
from edge.gateway import EdgeGateway


def base_cfg():
    return {
        "organization": {"id": "ORG1"},
        "mqtt": {"host": "127.0.0.1", "port": 1883, "prefix": "spi/v1"},
        "lines": [{
            "id": "L", "plant_id": "P", "area_id": "A", "gateway_id": "GW", "heartbeat_interval_s": 30,
            "equipment": [{
                "id": "PRESS-01", "protocol": "modbus_tcp", "connection": {"host": "10.0.0.1", "port": 502},
                "poll_interval_s": 1,
                "points": [
                    {"kind": "digital", "signal": "machine_on", "address": {"type": "coil", "addr": 0}},
                    {"kind": "counter", "role": "total", "address": {"type": "holding", "addr": 100, "words": 2, "encoding": "u32"}},
                    {"kind": "analog", "sensor_id": "PRESS-01.TEMP", "name": "temperature", "unit": "C",
                     "address": {"type": "holding", "addr": 10}, "scale": 0.1, "sample_interval_s": 10, "valid_range": [-20, 150]},
                ],
                "queue_after": {"id": "QUEUE-A", "capacity": 10, "level_point": {"kind": "analog", "address": {"type": "holding", "addr": 20}}},
            }],
        }],
    }


def test_valid_config_parses():
    cfg = parse_config(base_cfg())
    assert cfg.org_id == "ORG1" and cfg.mqtt_host == "127.0.0.1" and cfg.mqtt_prefix == "spi/v1"
    ln = cfg.lines[0]
    assert ln.id == "L" and ln.gateway_id == "GW" and len(ln.equipment) == 1
    eq = ln.equipment[0]
    assert eq.protocol == "modbus_tcp" and len(eq.points) == 3 and eq.queue_after.queue_id == "QUEUE-A"


def test_missing_required_field_named_in_error():
    raw = base_cfg()
    del raw["lines"][0]["gateway_id"]
    with pytest.raises(ConfigError, match="gateway_id"):
        parse_config(raw)


def test_unknown_point_kind_rejected():
    raw = base_cfg()
    raw["lines"][0]["equipment"][0]["points"][0]["kind"] = "smoke_signal"
    with pytest.raises(ConfigError, match="unknown point kind"):
        parse_config(raw)


def test_digital_point_needs_a_signal_name():
    raw = base_cfg()
    del raw["lines"][0]["equipment"][0]["points"][0]["signal"]
    with pytest.raises(ConfigError, match="digital point needs 'signal'"):
        parse_config(raw)


def test_counter_point_needs_a_valid_role():
    raw = base_cfg()
    raw["lines"][0]["equipment"][0]["points"][1]["role"] = "bogus"
    with pytest.raises(ConfigError, match="counter point needs 'role'"):
        parse_config(raw)


def test_analog_point_needs_a_sensor_id():
    raw = base_cfg()
    del raw["lines"][0]["equipment"][0]["points"][2]["sensor_id"]
    with pytest.raises(ConfigError, match="analog point needs 'sensor_id'"):
        parse_config(raw)


def test_duplicate_equipment_and_line_and_gateway_ids_rejected():
    raw = base_cfg()
    raw["lines"].append(json.loads(json.dumps(raw["lines"][0])))  # exact duplicate: same line/gateway/equipment ids
    with pytest.raises(ConfigError, match="duplicate line id"):
        parse_config(raw)


def test_equipment_needs_at_least_one_point():
    raw = base_cfg()
    raw["lines"][0]["equipment"][0]["points"] = []
    with pytest.raises(ConfigError, match="needs at least one point"):
        parse_config(raw)


# ---------------- EdgeGateway: sequencing, buffering, disk persistence ----------------
class FakeSink:
    def __init__(self):
        self.events = []

    def emit(self, ev):
        self.events.append(ev)


CTX = {"plant_id": "P", "area_id": "A", "line_id": "L", "equipment_id": "PRESS-01"}


def test_events_sent_immediately_while_online():
    sink = FakeSink()
    gw = EdgeGateway("GW", sink)
    gw.create("2026-01-01T00:00:00Z", "signal", CTX, {"name": "machine_on", "value": 1})
    assert len(sink.events) == 1 and sink.events[0]["buffered"] is False and sink.events[0]["event_id"] == "GW:1"


def test_events_buffered_while_offline_and_flushed_in_order_on_reconnect():
    sink = FakeSink()
    gw = EdgeGateway("GW", sink)
    gw.set_online(False, "2026-01-01T00:00:00Z")
    for i in range(5):
        gw.create(f"2026-01-01T00:0{i}:00Z", "signal", CTX, {"name": "machine_on", "value": i % 2})
    assert sink.events == [] and gw.buffered_events == 5
    gw.set_online(True, "2026-01-01T00:10:00Z")
    assert len(sink.events) == 5
    assert [e["seq"] for e in sink.events] == [1, 2, 3, 4, 5]                 # contiguous, ordered
    assert all(e["buffered"] and e["emitted_ts"] == "2026-01-01T00:10:00Z" for e in sink.events)
    assert sink.events[0]["ts"] == "2026-01-01T00:00:00Z"                     # original event time is kept
    assert gw.buffered_events == 0


def test_buffer_survives_a_process_restart_via_disk_persistence(tmp_path):
    path = str(tmp_path / "GW.jsonl")
    sink1 = FakeSink()
    gw1 = EdgeGateway("GW", sink1, buffer_file=path)
    gw1.set_online(False, "2026-01-01T00:00:00Z")
    gw1.create("2026-01-01T00:00:01Z", "signal", CTX, {"name": "machine_on", "value": 1})
    gw1.create("2026-01-01T00:00:02Z", "signal", CTX, {"name": "fault", "value": 1})
    assert gw1.buffered_events == 2

    # process "restarts": a brand new EdgeGateway reads the same buffer file
    sink2 = FakeSink()
    gw2 = EdgeGateway("GW", sink2, buffer_file=path)
    assert gw2.buffered_events == 2 and gw2.online is False and sink2.events == []
    gw2.set_online(True, "2026-01-01T00:05:00Z")
    assert len(sink2.events) == 2 and gw2.buffered_events == 0
    assert [e["payload"]["name"] for e in sink2.events] == ["machine_on", "fault"]


def test_disk_buffer_file_is_cleared_once_flushed(tmp_path):
    path = tmp_path / "GW.jsonl"
    gw = EdgeGateway("GW", FakeSink(), buffer_file=str(path))
    gw.set_online(False, "2026-01-01T00:00:00Z")
    gw.create("2026-01-01T00:00:01Z", "signal", CTX, {"name": "machine_on", "value": 1})
    assert path.read_text().strip() != ""
    gw.set_online(True, "2026-01-01T00:01:00Z")
    assert path.read_text().strip() == ""


def test_concurrent_creates_from_multiple_threads_do_not_corrupt_sequencing():
    import threading
    sink = FakeSink()
    gw = EdgeGateway("GW", sink)

    def worker(n):
        for i in range(50):
            gw.create("2026-01-01T00:00:00Z", "signal", CTX, {"name": f"s{n}", "value": i % 2})

    threads = [threading.Thread(target=worker, args=(n,)) for n in range(8)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    seqs = [e["seq"] for e in sink.events]
    assert len(sink.events) == 400
    assert sorted(seqs) == list(range(1, 401))                                # no duplicate/dropped sequence numbers
    assert len({e["event_id"] for e in sink.events}) == 400


def test_concurrent_creates_while_buffering_to_disk_do_not_race(tmp_path):
    """Regression: disk persistence does real file I/O (releases the GIL), so without the lock, concurrent
    equipment-polling threads writing the same buffer file race and can crash or corrupt it."""
    import threading
    errors = []
    orig_hook = threading.excepthook
    threading.excepthook = lambda args: errors.append(args)
    try:
        gw = EdgeGateway("GW", FakeSink(), buffer_file=str(tmp_path / "GW.jsonl"))
        gw.set_online(False, "2026-01-01T00:00:00Z")

        def worker(n):
            for i in range(60):
                gw.create("2026-01-01T00:00:00Z", "signal", CTX, {"name": f"s{n}", "value": i % 2})

        threads = [threading.Thread(target=worker, args=(n,)) for n in range(12)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
    finally:
        threading.excepthook = orig_hook
    assert errors == []                              # no worker thread crashed
    assert gw.buffered_events == 720
    lines = (tmp_path / "GW.jsonl").read_text().splitlines()
    assert len(lines) == 720
    assert len({json.loads(l)["event_id"] for l in lines}) == 720            # no duplicate/dropped events on disk
