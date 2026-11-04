import json

from backend.__main__ import main as backend_main
from simulator.__main__ import main as sim_main
from conftest import DEMO


def make_events(tmp_path, duration=3600):
    out = tmp_path / "events.jsonl"
    assert sim_main(["--config", DEMO, "--duration", str(duration), "--out", str(out)]) == 0
    return out


def test_pipeline_cli_reports_kpis_and_accuracy(tmp_path, capsys):
    out = make_events(tmp_path)
    capsys.readouterr()
    assert backend_main(["--config", DEMO, "--events", str(out), "--truth", str(out) + ".truth.jsonl", "--db", str(tmp_path / "spi.db")]) == 0
    rep = json.loads(capsys.readouterr().out)
    assert rep["ingest"]["rejected"] == 0 and rep["ingest"]["sequence_gaps"] == {}
    assert rep["accuracy_vs_simulator_truth"]["agreement"] >= 0.98
    assert set(rep["lines"]) == {"LINE-VAD-01", "LINE-VAD-02", "LINE-PUN-01"}
    assert (tmp_path / "spi.db").exists()


def test_pipeline_cli_survives_corrupt_lines_and_reports_them(tmp_path, capsys):
    out = make_events(tmp_path, 600)
    with open(out, "a") as fh:
        fh.write("{this is not json\n")
    capsys.readouterr()
    assert backend_main(["--config", DEMO, "--events", str(out)]) == 0
    rep = json.loads(capsys.readouterr().out)
    assert rep["ingest"]["rejected"] == 1


def test_pipeline_cli_missing_file_returns_2(tmp_path, capsys):
    assert backend_main(["--config", DEMO, "--events", str(tmp_path / "nope.jsonl")]) == 2
