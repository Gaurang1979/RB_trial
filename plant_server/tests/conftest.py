import copy
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest  # noqa: E402

from simulator.config import parse_config  # noqa: E402
from simulator.engine import Simulation  # noqa: E402
from simulator.sinks import MemorySink  # noqa: E402

DEMO = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "configs", "demo_factory.json")

BASE = {
    "organization": {"id": "T", "name": "test"},
    "simulation": {"seed": 7, "start_time": "2026-01-01T00:00:00Z", "queue_sample_interval_s": 10, "heartbeat_interval_s": 30},
    "plants": [{"id": "P", "name": "P", "areas": [{"id": "AR", "name": "AR", "lines": [{
        "id": "L", "name": "L", "gateway_id": "GW", "target_per_hour": 180,
        "equipment": [
            {"id": "A", "name": "A", "standard_cycle_time_s": 10, "reject_rate": 0.0},
            {"id": "B", "name": "B", "standard_cycle_time_s": 20, "reject_rate": 0.0,
             "sensors": [{"id": "B.T", "name": "temperature", "unit": "C", "base": 50, "noise": 0.5,
                          "producing_delta": 10, "sample_interval_s": 10, "valid_range": [0, 200]}]},
            {"id": "C", "name": "C", "standard_cycle_time_s": 15, "reject_rate": 0.0}],
        "queues": [{"id": "QA", "name": "QA", "capacity": 5}, {"id": "QB", "name": "QB", "capacity": 5}],
    }]}]}],
    "scenarios": [],
}


@pytest.fixture
def base_cfg():
    return copy.deepcopy(BASE)


def run(raw, duration, cfg_hook=None):
    cfg = parse_config(raw)
    sink = MemorySink()
    sim = Simulation(cfg, sink)
    sim.run(duration)
    return sim, sink.events


_PG_STORES = []


def new_store():
    """SQLite by default; set SPI_TEST_PG_DSN to run the whole suite against PostgreSQL (fresh schema per store)."""
    dsn = os.environ.get("SPI_TEST_PG_DSN")
    if dsn:
        from backend.store import temp_postgres_store
        st = temp_postgres_store(dsn)
        _PG_STORES.append(st)
        return st
    from backend.store import SqliteStore
    return SqliteStore()


@pytest.fixture(autouse=True)
def _drop_pg_schemas():
    """Function-scoped (not session-scoped): a PostgreSQL connection per store, left open until the whole
    session ends, exhausts the server's connection limit well before a suite this size finishes. Closing
    each test's stores as soon as it ends keeps at most a handful of connections open at once."""
    yield
    while _PG_STORES:
        st = _PG_STORES.pop()
        try:
            st.drop_schema()
            st.close()
        except Exception:
            pass


def pipeline(raw, duration, topo_raw=None):
    """simulate -> ingest -> derive. Returns a namespace with everything tests need."""
    from types import SimpleNamespace

    from backend.derive import derive_all
    from backend.ingest import ingest_events
    from backend.topology import topology_from_dict
    from spi_common.schema import parse_ts

    sim, events = run(raw, duration)
    store = new_store()
    ingest_events(store, events)
    topo = topology_from_dict(topo_raw or raw)
    t0 = parse_ts(sim.iso(0))
    t1 = t0 + duration
    return SimpleNamespace(sim=sim, events=events, store=store, topo=topo, t0=t0, t1=t1,
                           derived=derive_all(store, topo, t0, t1))


def sc(kind, start, dur, **kw):
    return {"kind": kind, "start_s": start, "duration_s": dur, **kw}
