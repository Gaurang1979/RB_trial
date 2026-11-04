import json
import os
import socket
import time
import uuid

import pytest

from backend.derive import derive_all
from backend.ingest import ingest_events
from backend.live import LiveEngine
from backend.master import issue_token, sync_site
from backend.mqtt_ingest import MqttIngest
from backend.topology import topology_from_dict
from conftest import new_store, run
from simulator.mqtt_sink import MqttSink
from spi_common.schema import parse_ts

HOST, PORT = os.environ.get("SPI_TEST_MQTT", "127.0.0.1:1883").split(":")
PORT = int(PORT)


def _broker_up():
    try:
        socket.create_connection((HOST, PORT), timeout=1).close()
        return True
    except OSError:
        return False


pytestmark = pytest.mark.skipif(not _broker_up(), reason=f"no MQTT broker at {HOST}:{PORT} (set SPI_TEST_MQTT)")


def wait_for(cond, timeout=30):
    end = time.time() + timeout
    while time.time() < end:
        if cond():
            return True
        time.sleep(0.1)
    return False


def publish_all(events, prefix, tokens, twice=False, garbage=False):
    sink = MqttSink(HOST, PORT, prefix, tokens=tokens)
    for e in events:
        sink.emit(e)
        if twice:
            sink.emit(e)
    if garbage:
        sink.emit_raw("junk/events", b"{not json", token=next(iter(tokens.values())))
    sink.close()


def engine_for(cfg):
    """A live engine whose device registry has the site's gateways, each with a token: (engine, {gateway_id: token})."""
    store = new_store()
    sync_site(store, cfg)
    tokens = {d["id"]: issue_token(store, d["id"]) for d in store.device_list() if d["kind"] == "production_gateway"}
    return LiveEngine(store, topology_from_dict(cfg)), tokens


def test_events_flow_through_broker_end_to_end(base_cfg):
    sim, ev = run(base_cfg, 900)
    prefix = f"spi/test/{uuid.uuid4().hex}"
    eng, tokens = engine_for(base_cfg)
    ing = MqttIngest(eng, HOST, PORT, prefix, client_id=None).start()
    time.sleep(0.5)
    publish_all(ev, prefix, tokens)
    assert wait_for(lambda: eng.store.counts()["raw_events"] == len(ev)), eng.store.counts()
    ing.stop()
    assert ing.errors == []
    ref = new_store()
    ingest_events(ref, ev)
    t0 = parse_ts(sim.iso(0))
    a = derive_all(eng.store, eng.topo, t0, t0 + 900)
    b = derive_all(ref, eng.topo, t0, t0 + 900)
    assert a == b
    assert eng.line_status("L")["equipment"]          # live read model is populated


def test_broker_redelivery_and_garbage_are_handled(base_cfg):
    sim, ev = run(base_cfg, 300)
    prefix = f"spi/test/{uuid.uuid4().hex}"
    eng, tokens = engine_for(base_cfg)
    ing = MqttIngest(eng, HOST, PORT, prefix, client_id=None).start()
    time.sleep(0.5)
    publish_all(ev, prefix, tokens, twice=True, garbage=True)
    assert wait_for(lambda: eng.store.counts() == {"raw_events": len(ev), "rejected": 1})
    ing.stop()
    assert ing.errors == []


def test_events_published_while_backend_is_down_are_not_lost(base_cfg):
    sim, ev = run(base_cfg, 300)
    prefix = f"spi/test/{uuid.uuid4().hex}"
    cid = f"spi-test-{uuid.uuid4().hex[:8]}"
    eng, tokens = engine_for(base_cfg)
    first = MqttIngest(eng, HOST, PORT, prefix, client_id=cid).start()
    time.sleep(0.5)
    first.stop()                                           # backend goes down (persistent session stays on the broker)
    publish_all(ev, prefix, tokens)                                # gateway keeps publishing
    assert eng.store.counts()["raw_events"] == 0
    second = MqttIngest(eng, HOST, PORT, prefix, client_id=cid).start()   # backend comes back
    assert wait_for(lambda: eng.store.counts()["raw_events"] == len(ev)), eng.store.counts()
    second.stop()


def test_large_burst_after_an_outage_loses_nothing():
    """A gateway flushing a long buffer publishes thousands of events at once. Needs the broker settings in deploy/mosquitto.conf
    (Mosquitto's default queue limit of 1000 silently drops messages; sequence-gap detection is what reveals that)."""
    import json as _json
    from backend.ingest import gaps
    from conftest import DEMO
    raw = _json.load(open(DEMO))
    sim, ev = run(raw, 3600)
    assert len(ev) > 9000
    prefix = f"spi/test/{uuid.uuid4().hex}"
    eng, tokens = engine_for(raw)
    ing = MqttIngest(eng, HOST, PORT, prefix, client_id=None).start()
    time.sleep(0.5)
    publish_all(ev, prefix, tokens)
    assert wait_for(lambda: eng.store.counts()["raw_events"] >= len(ev), timeout=60), \
        f"only {eng.store.counts()['raw_events']} of {len(ev)} stored; gaps={gaps(eng.store)}; is deploy/mosquitto.conf applied to the broker?"
    ing.stop()
    assert gaps(eng.store) == {}


def test_messages_without_a_valid_token_are_refused_over_the_broker(base_cfg):
    sim, ev = run(base_cfg, 300)
    prefix = f"spi/test/{uuid.uuid4().hex}"
    eng, tokens = engine_for(base_cfg)
    ing = MqttIngest(eng, HOST, PORT, prefix, client_id=None).start()
    time.sleep(0.5)
    publish_all(ev[:50], prefix, {})                                   # no token
    publish_all(ev[:50], prefix, {g: "wrong" for g in tokens})          # wrong token
    publish_all(ev, prefix, tokens)                                    # the gateway's own token
    assert wait_for(lambda: eng.store.counts()["raw_events"] == len(ev)), eng.store.counts()
    assert wait_for(lambda: ing.refused == 100), ing.refused_reasons
    ing.stop()
    assert eng.store.counts() == {"raw_events": len(ev), "rejected": 0} and ing.errors == []
