import json

from simulator.__main__ import main
from conftest import DEMO


def test_cli_writes_events_and_truth(tmp_path):
    out = tmp_path / "events.jsonl"
    assert main(["--config", DEMO, "--duration", "300", "--out", str(out)]) == 0
    events = [json.loads(line) for line in out.read_text().splitlines()]
    truth = [json.loads(line) for line in (tmp_path / "events.jsonl.truth.jsonl").read_text().splitlines()]
    assert len(events) > 500 and truth


def test_cli_bad_config_returns_2(tmp_path, capsys):
    bad = tmp_path / "bad.json"
    bad.write_text("{}")
    assert main(["--config", str(bad)]) == 2
    assert "config error" in capsys.readouterr().err
