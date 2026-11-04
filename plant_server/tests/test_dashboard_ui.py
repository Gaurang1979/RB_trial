import json
import os
import shutil
import subprocess

import pytest

from backend.live import LiveEngine
from backend.topology import topology_from_dict
from conftest import new_store, run, sc
from test_live import feed

UI = os.path.join(os.path.dirname(__file__), "ui")
HTML = os.path.join(os.path.dirname(os.path.dirname(__file__)), "dashboard", "index.html")

needs_node = pytest.mark.skipif(not shutil.which("node") or not os.path.isdir(os.path.join(UI, "node_modules", "jsdom")),
                                reason="needs node and 'npm install' in tests/ui")


@needs_node
def test_dashboard_renders_live_status_and_escapes_untrusted_text(base_cfg, tmp_path):
    base_cfg["scenarios"] = [sc("fault", 300, 1500, equipment_id="A")]      # still stopped at the snapshot, so an alert is open
    sim, ev = run(base_cfg, 1200)
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg), alerts=True)
    feed(eng, ev)
    fixture = tmp_path / "status.json"
    fixture.write_text(json.dumps(eng.line_status("L")))
    r = subprocess.run(["node", os.path.join(UI, "smoke.js"), str(fixture), HTML], capture_output=True, text=True, timeout=60)
    assert r.returncode == 0, r.stdout + r.stderr
