"""The relay's session record into a Remote Service Log and a draft Timesheet (no ERPNext needed)."""
from datetime import datetime, timedelta

import pytest

from remote_service.core import BadRecord, log_doc, parse, timesheet_doc

RECORD = {
    "session_id": "a1b2c3d4e5f60718", "machine": "M-1001", "machine_name": "Barcode Line 1", "site": "Pune", "licence": "LIC-1",
    "engineer": "anita", "engineer_name": "Anita", "employee": "HR-EMP-0007", "role": "service", "reason": "Call 4711",
    "started": "2026-11-04T10:00:00Z", "ended": "2026-11-04T10:45:00Z", "end_reason": "sign-out", "ledger_head": "ab12" * 16,
    "actions": [{"time": "2026-11-04T10:05:00Z", "kind": "file_read", "detail": "Downloaded logs/today.log"}],
}
same = lambda t: t


def test_a_session_becomes_a_log_with_its_actions():
    s = parse(RECORD)
    assert s.hours == 0.75
    d = log_doc(s, "Customer A", same)
    assert d["doctype"] == "Remote Service Log" and d["session_id"] == "a1b2c3d4e5f60718" and d["customer"] == "Customer A"
    assert d["started"] == datetime(2026, 11, 4, 10, 0) and d["duration_hours"] == 0.75
    assert d["ledger_head"] == "ab12" * 16
    assert d["actions"] == [{"time": datetime(2026, 11, 4, 10, 5), "kind": "file_read", "detail": "Downloaded logs/today.log"}]


def test_the_timesheet_is_one_time_log_for_the_engineer():
    t = timesheet_doc(parse(RECORD), "Customer A", "Remote Service", same)
    assert t["doctype"] == "Timesheet" and t["employee"] == "HR-EMP-0007"
    (log,) = t["time_logs"]
    assert log["hours"] == 0.75 and log["activity_type"] == "Remote Service" and log["from_time"] < log["to_time"]
    assert "M-1001" in log["description"] and "Call 4711" in log["description"]


def test_times_are_converted_to_the_system_zone():
    plus_5_30 = lambda t: t + timedelta(hours=5, minutes=30)
    assert log_doc(parse(RECORD), None, plus_5_30)["started"] == datetime(2026, 11, 4, 15, 30)


@pytest.mark.parametrize("change, why", [
    ({"session_id": "../x"}, "session id"),
    ({"machine": "a b"}, "machine number"),
    ({"role": "admin"}, "role"),
    ({"started": "yesterday"}, "not a time"),
    ({"ended": "2026-11-04T09:00:00Z"}, "before started"),
    ({"engineer": ""}, "engineer is missing"),
    ({"reason": 5}, "must be text"),
])
def test_bad_records_are_refused_with_a_reason(change, why):
    with pytest.raises(BadRecord, match=why):
        parse(RECORD | change)


def test_text_is_trimmed_and_limited():
    s = parse(RECORD | {"reason": "  line\x00 two " + "x" * 500, "actions": [{"time": "bad", "kind": "k", "detail": "d"}] * 3000})
    assert s.reason.startswith("line two") and len(s.reason) == 200
    assert len(s.actions) == 2000 and s.actions[0][0] == s.started   # a bad action time falls back to the start
