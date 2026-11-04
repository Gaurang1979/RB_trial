"""NG review (docs/vision-system/NG-REVIEW.md): vision_result rows, the server asking for images, uploads, verdicts, export."""
import csv
import io
import json
import os
import time

import pytest
from fastapi.testclient import TestClient

from backend import auth
from backend.api import create_app
from backend.live import LiveEngine
from backend.master import issue_token, sync_site
from backend.topology import topology_from_dict
from backend.vision import ingest
from conftest import new_store

NOW = int(time.time() * 1000)
JPEG = b"\xff\xd8\xff\xe0" + b"x" * 200
NG = [{"tool": "pin", "reason": "missing"}]


def ev(seq=1, queue="q1", station="S1", line="L", plant="P", **kw):
    e = {"kind": "inspection.result", "schema": 1, "plant": plant, "line": line, "station": station, "ts": NOW - 100_000 + seq, "seq": seq,
         "queue_id": queue, "ok": True, "cycle_ms": 40}
    return {**e, **kw}


def ng(seq, image_id=None, reasons=NG, **kw):
    return ev(seq, ok=False, reasons=reasons, **({"image_id": image_id} if image_id else {}), **kw)


def bearer(t):
    return {"Authorization": f"Bearer {t}"}


@pytest.fixture
def env(base_cfg, tmp_path):
    store = new_store()
    sync_site(store, base_cfg)
    stations = {"S1": ("P", "L"), "S2": ("P", "L"), "S3": ("P", "L2"), "S4": ("P2", "L3")}
    for d, (p, ln) in stations.items():
        store.device_create({"id": d, "kind": "vision_station", "name": d, "plant_id": p, "line_id": ln, "enabled": 1, "created_at": 0})
    tok = {d: issue_token(store, d) for d in stations}

    class E:
        pass
    e = E()
    e.store, e.tok, e.dir = store, tok, str(tmp_path / "images")

    def make(limit=None):
        return TestClient(create_app(LiveEngine(store, topology_from_dict(base_cfg)), vision_image_dir=e.dir, vision_image_limit_bytes=limit))
    e.make = make
    e.c = make()

    def person(name, role, level="org", sid=None):
        uid = store.user_create(name, name, "x", None, "test", 0)
        store.role_add(uid, role, level, sid, 0)
        t = auth.new_token()
        store.session_create(t, uid, int(time.time()), int(time.time()) + 3600)
        return bearer(t)
    e.person = person

    def send(station, *events):
        p, ln = stations[station]
        body = [{**x, "station": station, "plant": p, "line": ln} for x in events]
        r = e.c.post("/vision/ingest/v1/events", content=json.dumps(body), headers=bearer(tok[station]))
        assert r.status_code == 200, r.text
        return r.json()
    e.send = send

    def hb(station="S1"):
        p, ln = stations[station]
        body = {"kind": "heartbeat", "schema": 1, "plant": p, "line": ln, "station": station, "ts": NOW, "seq": 0, "agent_version": "1"}
        r = e.c.post("/vision/ingest/v1/heartbeat", content=json.dumps(body), headers=bearer(tok[station]))
        assert r.status_code == 200, r.text
        return r.json()
    e.hb = hb

    def up(station, image_id, camera="cam1", data=JPEG, **kw):
        return e.c.post(f"/vision/ingest/v1/images/{image_id}", params={"camera": camera}, content=data, headers=bearer(tok[station]), **kw)
    e.up = up

    def rows(sql, args=()):
        with store._lock:
            return store._x(sql, args).fetchall()
    e.rows = rows
    return e


# ---------------- vision_result rows and image requests ----------------
def test_ok_and_ng_results_are_kept_and_only_an_ng_with_an_image_id_is_asked_for(env):
    env.send("S1", ev(1, part_id="A1", camera="cam1", job="J", job_version=3), ng(2, "img-2", camera="cam2"), ng(3), ev(4, image_id="img-4"))
    got = env.rows("SELECT seq, ok, camera, part_id, image_id, job, job_version, reasons, plant_id, line_id FROM vision_result ORDER BY seq")
    assert [g[:2] for g in got] == [(1, 1), (2, 0), (3, 0), (4, 1)]
    assert got[0][2:7] == ("cam1", "A1", None, "J", 3)
    assert got[1][4] == "img-2" and json.loads(got[1][7]) == NG and got[1][8:] == ("P", "L")
    assert env.rows("SELECT device_id, image_id, state FROM vision_image_request") == [("S1", "img-2", "wanted")]


def test_duplicates_and_late_events_do_not_double_rows(env):
    env.send("S1", ng(1, "a"), ng(2, "b"), ng(5, "e"))
    env.send("S1", ng(1, "a"), ng(2, "b"), ng(3, "c"))     # a resent batch with one new event
    env.send("S1", ng(4, "d"))                              # fills the gap
    env.send("S1", ng(4, "d"))
    assert env.rows("SELECT COUNT(*) FROM vision_result") == [(5,)]
    assert env.rows("SELECT COUNT(*) FROM vision_image_request") == [(5,)]


def test_a_refused_or_conflicting_batch_leaves_no_result_rows(env):
    env.send("S1", ng(1, "a"), ng(3, "c"))
    # names another station: refused whole
    r = env.c.post("/vision/ingest/v1/events", content=json.dumps([ng(2, "b"), ng(4, "d", station="S2")]), headers=bearer(env.tok["S1"]))
    assert r.status_code == 403
    # seq 3 again with other content: a conflict, the batch is undone with the new event (the late seq 2) before it
    r = env.c.post("/vision/ingest/v1/events", content=json.dumps([ng(2, "b"), ng(3, "other")]), headers=bearer(env.tok["S1"]))
    assert r.status_code == 409
    assert env.rows("SELECT seq, image_id FROM vision_result ORDER BY seq") == [(1, "a"), (3, "c")]
    assert sorted(r[0] for r in env.rows("SELECT image_id FROM vision_image_request")) == ["a", "c"]


def test_an_invalid_event_makes_no_result_row(env):
    env.send("S1", ev(1, ok=False, reasons=[]), ev(2, ok="yes"), ng(3, "x"))
    assert env.rows("SELECT seq FROM vision_result") == [(3,)]


def test_an_image_id_a_station_cannot_upload_is_never_asked_for(env):
    env.send("S1", ng(1, "bad id/../x"), ng(2, "x" * 65), ng(3, "good"))
    assert env.hb()["image_requests"] == ["good"]


# ---------------- the heartbeat answer ----------------
def test_the_heartbeat_asks_newest_first_at_most_ten_of_its_own_station(env):
    env.send("S1", *[ng(i, f"i{i:02d}") for i in range(1, 13)])
    env.send("S2", ng(1, "other"))
    r = env.hb("S1")
    assert r["ok"] is True
    assert r["image_requests"] == [f"i{i:02d}" for i in range(12, 2, -1)]
    assert env.hb("S2")["image_requests"] == ["other"]
    assert env.hb("S3")["image_requests"] == []


def test_a_request_older_than_seven_days_expires_and_is_not_asked(env):
    env.send("S1", ng(1, "old"), ng(2, "new"))
    env.store.db.execute("UPDATE vision_image_request SET requested = ? WHERE image_id = 'old'", (NOW - 8 * 86400_000,))
    env.store.db.commit()
    assert env.hb()["image_requests"] == ["new"]
    assert env.rows("SELECT state FROM vision_image_request WHERE image_id = 'old'") == [("expired",)]
    env.store.db.execute("UPDATE vision_image_request SET requested = ? WHERE image_id = 'new'", (NOW - 6 * 86400_000,))
    env.store.db.commit()
    assert env.hb()["image_requests"] == ["new"]


# ---------------- station image endpoints ----------------
def test_upload_keeps_the_jpeg_and_done_closes_the_request(env):
    env.send("S1", ng(1, "a"))
    r = env.up("S1", "a")
    assert r.status_code == 200 and r.json() == {"ok": True}
    [(f, size)] = env.rows("SELECT file, size FROM vision_image")
    assert size == len(JPEG) and open(os.path.join(env.dir, f), "rb").read() == JPEG
    assert env.hb()["image_requests"] == ["a"]       # asked until the station says done
    assert env.c.post("/vision/ingest/v1/images/a/done", headers=bearer(env.tok["S1"])).json() == {"ok": True}
    assert env.hb()["image_requests"] == []
    assert env.rows("SELECT state FROM vision_image_request") == [("done",)]


def test_an_image_sent_again_replaces_the_earlier_one(env):
    env.send("S1", ng(1, "a"))
    env.up("S1", "a")
    env.up("S1", "a", data=b"\xff\xd8\xff" + b"y" * 50)
    assert env.rows("SELECT size FROM vision_image") == [(53,)]
    assert len(os.listdir(env.dir)) == 1
    assert env.up("S1", "a", camera="cam 2").status_code == 200
    assert env.rows("SELECT COUNT(*) FROM vision_image") == [(2,)]


def test_upload_size_magic_and_camera_rules(env):
    env.send("S1", ng(1, "a"))
    assert env.up("S1", "a", data=b"\xff\xd8\xff" + b"z" * (307_200 - 3)).status_code == 200      # exactly the limit
    assert env.up("S1", "a", data=b"\xff\xd8\xff" + b"z" * (307_200 - 2)).status_code == 413
    assert env.up("S1", "a", data=b"\x89PNG" + b"z" * 20).status_code == 400
    assert env.up("S1", "a", data=b"").status_code == 400
    for cam in ("", " ", "\t", ".", "..", "x" * 65, "a\nb", "a\x00b", "a\x7fb"):
        assert env.up("S1", "a", camera=cam).status_code == 400, repr(cam)
    assert env.c.post("/vision/ingest/v1/images/a", content=JPEG, headers=bearer(env.tok["S1"])).status_code == 400   # no camera
    assert env.rows("SELECT size FROM vision_image") == [(307_200,)]


def test_an_oversize_body_without_a_length_is_stopped_while_reading(env):
    env.send("S1", ng(1, "a"))
    read = []

    def chunks():
        yield b"\xff\xd8\xff"
        for _ in range(100):
            read.append(1)
            yield b"q" * 65_536
    r = env.c.post("/vision/ingest/v1/images/a", params={"camera": "c"}, content=chunks(), headers=bearer(env.tok["S1"]))
    assert r.status_code == 413
    assert env.rows("SELECT COUNT(*) FROM vision_image") == [(0,)]


def test_upload_for_another_stations_or_an_unknown_image_is_404_and_needs_the_token(env):
    env.send("S1", ng(1, "a"))
    assert env.up("S2", "a").status_code == 404          # a result of S1, not S2
    assert env.up("S1", "nope").status_code == 404
    assert env.c.post("/vision/ingest/v1/images/a", params={"camera": "c"}, content=JPEG).status_code == 401
    assert env.up("S1", "a").status_code == 200
    assert env.rows("SELECT device_id FROM vision_image") == [("S1",)]


def test_path_tricks_never_reach_the_folder(env):
    env.send("S1", ev(1, ok=False, reasons=NG, image_id="../../evil"), ng(2, "a"))
    for bad in ("..", ".", "%2e%2e%2fevil", "..%2Fevil", "a%00b", "x" * 65, "a b"):
        assert env.up("S1", bad).status_code in (400, 404), bad
    assert env.c.post("/vision/ingest/v1/images/../done", headers=bearer(env.tok["S1"])).status_code in (400, 404, 405)
    assert not os.path.exists(env.dir) or os.listdir(env.dir) == []
    for cam in ("../x", "a/b", "...", "c\\d"):                       # printable text is a name; the file's name is a hash
        assert env.up("S1", "a", camera=cam).status_code == 200, cam
    files = {f for (f,) in env.rows("SELECT file FROM vision_image")}
    assert len(files) == 4 and set(os.listdir(env.dir)) == files and all(len(f) == 44 and f.endswith(".jpg") for f in files)
    assert not os.path.exists(os.path.join(os.path.dirname(env.dir), "x"))


def test_camera_names_are_any_printable_text_and_at_most_eight_per_image(env):
    env.send("S1", ng(1, "a"), ng(2, "b"))
    names = ["cam 1", "caf\u00e9", "a/b", "\u76f8\u673a", "  padded  ", "x" * 64, "-lead", "+plus"]
    for n in names:
        assert env.up("S1", "a", camera=n).status_code == 200, n
    assert env.up("S1", "a", camera="ninth").status_code == 400
    assert env.up("S1", "a", camera="cam 1").status_code == 200       # a name already kept is a resend, not a ninth
    assert env.up("S1", "b", camera="ninth").status_code == 200       # the cap is per image id
    assert env.rows("SELECT COUNT(*) FROM vision_image WHERE image_id = 'a'") == [(8,)]
    admin = env.person("adm", auth.ADMIN)
    item = env.c.get("/vision/ng/1", headers=admin).json()
    assert sorted(item["cameras"]) == sorted(names) and len(item["images"]) == 8
    for im in item["images"]:
        r = env.c.get(im["url"], headers=admin)
        assert r.status_code == 200 and r.content == JPEG, im       # names with / or spaces or accents round-trip through the url


def test_done_and_missing(env):
    env.send("S1", ng(1, "a"), ng(2, "b"), ev(3, image_id="ok-part"))
    h = bearer(env.tok["S1"])
    assert env.c.post("/vision/ingest/v1/images/a/missing", headers=h).json() == {"ok": True}
    assert env.hb()["image_requests"] == ["b"]
    assert env.rows("SELECT image_id, state FROM vision_image_request ORDER BY image_id") == [("a", "missing"), ("b", "wanted")]
    assert env.c.post("/vision/ingest/v1/images/ok-part/done", headers=h).status_code == 200     # an OK part: nothing to change
    assert env.c.post("/vision/ingest/v1/images/zzz/done", headers=h).status_code == 404
    assert env.c.post("/vision/ingest/v1/images/b/done", headers=bearer(env.tok["S2"])).status_code == 404
    assert env.c.post("/vision/ingest/v1/images/b/done").status_code == 401


def test_the_image_folder_keeps_to_its_limit_oldest_first(env):
    c = env.make(limit=700)
    env.c = c
    env.send("S1", ng(1, "a"), ng(2, "b"), ng(3, "c"), ng(4, "d"))
    for i in "abc":
        assert env.up("S1", i, data=b"\xff\xd8\xff" + b"p" * 297).status_code == 200      # 300 bytes each
    assert env.rows("SELECT image_id FROM vision_image ORDER BY id") == [("b",), ("c",)]
    assert len(os.listdir(env.dir)) == 2
    env.up("S1", "b")                                    # resent: now the newest, so the next to go is c
    env.up("S1", "d", data=b"\xff\xd8\xff" + b"p" * 297)
    assert sorted(r[0] for r in env.rows("SELECT image_id FROM vision_image")) == ["b", "d"]
    assert len(os.listdir(env.dir)) == 2
    admin = env.person("adm", auth.ADMIN)
    # newest first: d and b are kept; a and c are gone from the disk, and the station never said done, so they still read as asked
    assert [x["image_state"] for x in env.c.get("/vision/ng", headers=admin).json()["items"]] == ["ready", "waiting", "ready", "waiting"]
    for i in "ac":
        env.c.post(f"/vision/ingest/v1/images/{i}/done", headers=bearer(env.tok["S1"]))
    assert [x["image_state"] for x in env.c.get("/vision/ng", headers=admin).json()["items"]] == ["ready", "removed", "ready", "removed"]


# ---------------- people: list, detail, image, summary ----------------
def test_the_list_filters_and_pages(env):
    env.send("S1", ng(1, "a", reasons=[{"tool": "pin", "reason": "missing"}]), ev(2), ng(3, "c", reasons=[{"tool": "ocr", "reason": "unreadable"}]),
             ng(4, "d", reasons=[{"tool": "pin", "reason": "Missing_x"}]))
    env.send("S3", ng(1, "e"))
    admin = env.person("adm", auth.ADMIN)
    g = lambda **q: env.c.get("/vision/ng", params=q, headers=admin).json()["items"]      # noqa: E731
    all_ = g()
    assert [x["station"] + str(x["id"]) for x in all_] == ["S35", "S14", "S13", "S11"] and all(x["verdict"] == "pending" for x in all_)
    assert {"id", "plant", "line", "station", "ts", "reasons", "verdict", "image_state", "cameras", "image_id", "part_id"} <= set(all_[0])
    assert [x["id"] for x in g(line="L")] == [4, 3, 1] and [x["id"] for x in g(line="L2")] == [5]
    assert [x["id"] for x in g(station="S3")] == [5] and g(plant="P2") == []
    assert [x["id"] for x in g(reason="missing")] == [5, 1]       # exact, not 'Missing_x' and not a pattern
    assert [x["id"] for x in g(reason="unreadable")] == [3] and g(reason="%") == [] and g(reason="Missing_") == []
    assert [x["id"] for x in g(limit=2)] == [5, 4] and [x["id"] for x in g(limit=2, before_id=4)] == [3, 1]
    assert [x["id"] for x in g(since=NOW - 100_000 + 3, until=NOW - 100_000 + 4, line="L")] == [4, 3]
    assert env.c.get("/vision/ng", params={"verdict": "bogus"}, headers=admin).status_code == 400
    assert env.c.get("/vision/ng", params={"limit": 201}, headers=admin).status_code == 422
    assert all(x["image_state"] == "waiting" for x in all_) and all_[0]["cameras"] == []


def test_the_reason_filter_pages_past_rows_that_only_look_alike(env):
    env.send("S1", *[ng(i, f"i{i}", reasons=[{"tool": "pin", "reason": "Missing"}]) for i in range(2, 8)], ng(8, "m", reasons=[{"tool": "pin", "reason": "missing"}]))
    admin = env.person("adm", auth.ADMIN)
    got = env.c.get("/vision/ng", params={"reason": "missing", "limit": 2}, headers=admin).json()["items"]
    assert [x["image_id"] for x in got] == ["m"]



def test_a_page_says_where_the_next_starts_and_links_each_camera_image(env):
    env.send("S1", *[ng(i, f"p{i}") for i in range(1, 4)])
    admin = env.person("adm", auth.ADMIN)
    first = env.c.get("/vision/ng", params={"limit": 2}, headers=admin).json()
    assert [x["id"] for x in first["items"]] == [3, 2] and first["next_before_id"] == 2
    last = env.c.get("/vision/ng", params={"limit": 2, "before_id": 2}, headers=admin).json()
    assert [x["id"] for x in last["items"]] == [1] and last["next_before_id"] is None
    assert all(x["images"] == [] for x in first["items"])

def test_scoping_by_plant_and_line(env):
    env.send("S1", ng(1, "a"))
    env.send("S3", ng(1, "b"))
    env.send("S4", ng(1, "c"))
    g = lambda who, path="/vision/ng": env.c.get(path, headers=who)      # noqa: E731
    line = env.person("line-l2", auth.QUALITY_MANAGER, "line", "L2")
    plant = env.person("plant-p", auth.PLANT_MANAGER, "plant", "P")
    org = env.person("org", auth.CORPORATE)
    nobody = env.person("nobody-here", auth.QUALITY_MANAGER, "line", "nowhere")
    assert [x["line"] for x in g(line).json()["items"]] == ["L2"]
    assert sorted(x["line"] for x in g(plant).json()["items"]) == ["L", "L2"]
    assert len(g(org).json()["items"]) == 3 and g(nobody).json()["items"] == []
    assert g(line, "/vision/ng/1").status_code == 404 and g(line, "/vision/ng/2").status_code == 200
    assert g(line, "/vision/ng/999").status_code == 404
    assert [(s["line"], s["ng"]) for s in g(plant, "/vision/ng/summary").json()] == [("L", 1), ("L2", 1)]
    assert g(nobody, "/vision/ng/summary").json() == []


def test_an_ok_part_is_not_an_ng_part(env):
    env.send("S1", ev(1), ng(2, "a"))
    admin = env.person("adm", auth.ADMIN)
    assert env.c.get("/vision/ng/1", headers=admin).status_code == 404
    assert env.c.post("/vision/ng/1/review", json={"verdict": "confirmed"}, headers=admin).status_code == 404


def test_the_image_of_a_part_is_served_to_those_who_may_view_it(env):
    env.send("S1", ng(1, "a"))
    env.up("S1", "a", camera="cam1")
    admin, other = env.person("adm", auth.ADMIN), env.person("other", auth.QUALITY_MANAGER, "line", "L2")
    r = env.c.get("/vision/ng/1/image/cam1", headers=admin)
    assert r.status_code == 200 and r.content == JPEG and r.headers["content-type"] == "image/jpeg"
    assert env.c.get("/vision/ng/1/image/cam9", headers=admin).status_code == 404
    assert env.c.get("/vision/ng/1/image/..%2f..", headers=admin).status_code == 404
    assert env.c.get("/vision/ng/1/image/cam1", headers=other).status_code == 404
    item = env.c.get("/vision/ng/1", headers=admin).json()
    assert item["cameras"] == ["cam1"] and item["image_state"] == "ready" and item["history"] == []
    assert item["images"] == [{"camera": "cam1", "url": "/vision/ng/1/image/cam1"}] and env.c.get(item["images"][0]["url"], headers=admin).content == JPEG
    os.unlink(os.path.join(env.dir, os.listdir(env.dir)[0]))      # a file lost from the disk is a 404, not a failure
    assert env.c.get("/vision/ng/1/image/cam1", headers=admin).status_code == 404


def test_the_image_state_when_there_is_no_image_yet(env):
    env.send("S1", ng(1, "a"), ng(2, "b"), ng(3), ng(4, "d"))
    h = bearer(env.tok["S1"])
    env.c.post("/vision/ingest/v1/images/b/missing", headers=h)
    env.store.db.execute("UPDATE vision_image_request SET state = 'expired' WHERE image_id = 'd'")
    env.store.db.commit()
    admin = env.person("adm", auth.ADMIN)
    got = {x["image_id"]: x["image_state"] for x in env.c.get("/vision/ng", headers=admin).json()["items"]}
    assert got == {"a": "waiting", "b": "missing", None: "none", "d": "expired"}


# ---------------- verdicts ----------------
def test_who_may_give_a_verdict(env):
    env.send("S1", ng(1, "a"))
    post = lambda who: env.c.post("/vision/ng/1/review", json={"verdict": "confirmed"}, headers=who)      # noqa: E731
    for role in (auth.QUALITY_MANAGER, auth.PRODUCTION_MANAGER, auth.PLANT_MANAGER, auth.ADMIN):
        assert post(env.person(f"u-{role}", role, "line" if role != auth.ADMIN else "org", "L" if role != auth.ADMIN else None)).status_code == 200, role
    assert post(env.person("viewer", auth.DISPLAY, "line", "L")).status_code == 403
    assert post(env.person("corp", auth.CORPORATE)).status_code == 403
    assert post(env.person("maint", auth.MAINTENANCE_MANAGER, "line", "L")).status_code == 403
    assert post(env.person("other-line", auth.QUALITY_MANAGER, "line", "L2")).status_code == 404      # cannot even see the part
    assert post(env.person("other-plant", auth.PLANT_MANAGER, "plant", "P2")).status_code == 404
    assert env.rows("SELECT COUNT(*) FROM ng_review") == [(4,)]


def test_a_service_key_cannot_give_a_verdict_and_anon_is_named_anonymous(base_cfg, tmp_path):
    store = new_store()
    sync_site(store, base_cfg)
    store.device_create({"id": "S1", "kind": "vision_station", "name": "S1", "plant_id": "P", "line_id": "L", "enabled": 1, "created_at": 0})
    tok = issue_token(store, "S1")
    mk = lambda key: TestClient(create_app(LiveEngine(store, topology_from_dict(base_cfg)), api_key=key, vision_image_dir=str(tmp_path)))      # noqa: E731
    c = mk("k")
    assert c.post("/vision/ingest/v1/events", content=json.dumps([ng(1, "a")]), headers=bearer(tok)).status_code == 200
    svc = {"X-API-Key": "k"}
    assert len(c.get("/vision/ng", headers=svc).json()["items"]) == 1
    r = c.post("/vision/ng/1/review", json={"verdict": "confirmed"}, headers=svc)
    assert r.status_code == 403 and "person" in r.json()["detail"]
    assert c.get("/vision/ng").status_code == 401
    r = mk(None).post("/vision/ng/1/review", json={"verdict": "unsure"})        # no authentication at all
    assert r.status_code == 200 and r.json()["reviewed_by"] == "anonymous"


def test_verdicts_are_validated_and_kept_as_a_history(env):
    env.send("S1", ng(1, "a"), ng(2, "b"))
    qm = env.person("quality", auth.QUALITY_MANAGER, "line", "L")
    pm = env.person("boss", auth.PLANT_MANAGER, "plant", "P")
    post = lambda who, body, n=1: env.c.post(f"/vision/ng/{n}/review", json=body, headers=who)      # noqa: E731
    assert post(qm, {"verdict": "maybe"}).status_code == 400
    assert post(qm, {}).status_code == 422
    assert post(qm, {"verdict": "confirmed", "note": "x" * 501}).status_code == 422
    assert env.rows("SELECT COUNT(*) FROM ng_review") == [(0,)]
    assert post(qm, {"verdict": "confirmed", "note": "x" * 500}).json()["verdict"] == "confirmed"
    r = post(pm, {"verdict": "overruled", "note": "good part"})
    assert r.status_code == 200 and (r.json()["verdict"], r.json()["note"], r.json()["reviewed_by"]) == ("overruled", "good part", "boss")
    assert [(h["verdict"], h["by"]) for h in r.json()["history"]] == [("overruled", "boss"), ("confirmed", "quality")]
    assert post(qm, {"verdict": "unsure"}).json()["verdict"] == "unsure"
    assert env.rows("SELECT COUNT(*) FROM ng_review WHERE result_id = 1") == [(3,)]      # append only
    g = lambda **q: [x["id"] for x in env.c.get("/vision/ng", params=q, headers=qm).json()["items"]]      # noqa: E731
    assert g(verdict="pending") == [2] and g(verdict="unsure") == [1] and g(verdict="confirmed") == [] and g(verdict="all") == [2, 1]
    assert env.c.get("/vision/ng/1", headers=qm).json()["history"][0]["verdict"] == "unsure"


def test_the_summary_counts_the_newest_verdict_per_part(env):
    env.send("S1", ng(1, "a"), ng(2, "b"), ng(3, "c"), ng(4, "d"), ev(5))
    qm = env.person("quality", auth.QUALITY_MANAGER, "line", "L")
    for n, v in ((1, "confirmed"), (1, "overruled"), (2, "overruled"), (3, "confirmed")):
        assert env.c.post(f"/vision/ng/{n}/review", json={"verdict": v}, headers=qm).status_code == 200
    [s] = env.c.get("/vision/ng/summary", headers=qm).json()
    assert s == {"plant": "P", "line": "L", "ng": 4, "reviewed": 3, "pending": 1, "confirmed": 1, "overruled": 2, "unsure": 0, "overrule_rate": 0.6667}


# ---------------- export ----------------
def test_the_export_is_csv_with_quoting_and_formula_safety(env):
    env.send("S1", ng(1, "a", part_id="=1+1", reasons=[{"tool": "t", "reason": '=HYPERLINK("x")'}]), ng(2, "b", part_id="-5"), ng(3, "c"))
    env.send("S3", ng(1, "z"))
    env.up("S1", "a", camera="cam1")
    qm = env.person("quality", auth.QUALITY_MANAGER, "line", "L")
    env.c.post("/vision/ng/1/review", json={"verdict": "overruled", "note": '+cmd, "quoted"\nsecond line'}, headers=qm)
    env.c.post("/vision/ng/2/review", json={"verdict": "confirmed", "note": "@sum"}, headers=qm)
    r = env.c.get("/vision/ng/export.csv", headers=qm)
    assert r.status_code == 200 and r.headers["content-type"].startswith("text/csv") and "attachment" in r.headers["content-disposition"]
    rows = list(csv.reader(io.StringIO(r.text)))
    assert rows[0][:3] == ["id", "plant", "line"] and len(rows) == 3        # only reviewed parts of the caller's lines
    by_id = {x[0]: dict(zip(rows[0], x)) for x in rows[1:]}
    assert by_id["1"]["note"] == "'+cmd, \"quoted\"\nsecond line"
    assert by_id["1"]["reasons"] == 't: =HYPERLINK("x")'      # starts with a letter: kept as it is
    assert by_id["1"]["image_id"] == "a" and by_id["1"]["cameras"] == "cam1" and by_id["1"]["verdict"] == "overruled" and by_id["1"]["reviewed_by"] == "quality"
    assert by_id["2"]["note"] == "'@sum" and by_id["1"]["part_id"] == "'=1+1" and by_id["2"]["part_id"] == "'-5"
    assert not any(c.startswith(("=", "+", "-", "@")) for x in rows[1:] for c in x if isinstance(c, str))
    assert [x[0] for x in csv.reader(io.StringIO(env.c.get("/vision/ng/export.csv", params={"verdict": "confirmed"}, headers=qm).text))][1:] == ["2"]


def test_the_export_is_for_reviewers_only(env):
    env.send("S1", ng(1, "a"))
    admin = env.person("adm", auth.ADMIN)
    env.c.post("/vision/ng/1/review", json={"verdict": "confirmed"}, headers=admin)
    assert env.c.get("/vision/ng/export.csv", headers=env.person("viewer", auth.DISPLAY, "line", "L")).status_code == 403
    assert env.c.get("/vision/ng/export.csv", headers=env.person("maint", auth.MAINTENANCE_MANAGER, "line", "L")).status_code == 403
    r = env.c.get("/vision/ng/export.csv", headers=env.person("other", auth.QUALITY_MANAGER, "line", "L2"))
    assert r.status_code == 200 and len(r.text.strip().splitlines()) == 1        # a reviewer, but of another line: only the header
    assert len(env.c.get("/vision/ng/export.csv", headers=admin).text.strip().splitlines()) == 2


def test_the_review_page_is_served(env):
    r = env.c.get("/vision/review")
    assert r.status_code == 200 and "NG review" in r.text and "/vision/ng" in r.text


# ---------------- review fixes: speed, ingest limits, heartbeat, folder, backfill ----------------
def _plan(store, sql, args):
    with store._lock:
        return [r[-1] for r in store._x("EXPLAIN QUERY PLAN " + sql, args).fetchall()]


def _no_full_scan(plan):
    # every table access names an index: SCAN ... USING INDEX walks one in order (fine with LIMIT), a bare SCAN reads the whole table
    return [d for d in plan if d.split()[0] in ("SCAN", "SEARCH") and "USING" not in d]


def test_the_list_and_summary_use_indexes_not_a_scan_of_all_results(env):
    import sqlite3
    from backend.vision import review
    if not isinstance(env.store.db, sqlite3.Connection):
        pytest.skip("the query plan is read from SQLite")
    rows = []
    for i in range(1, 4001):
        ng_ = i % 3 == 0
        verdict = ("confirmed", "overruled", None, None)[i % 4] if ng_ else None
        rows.append(("S1" if i % 2 else "S3", "q", i, "P", "L" if i % 2 else "L2", NOW - i * 1000, NOW, int(not ng_), None, None, f"i{i}", None, None,
                     "[]", 1.0, verdict, None, "x" if verdict else None, NOW if verdict else None))
    env.store.db.executemany("INSERT INTO vision_result(device_id, queue_id, seq, plant_id, line_id, ts, received, ok, camera, part_id, image_id, job, "
                             "job_version, reasons, cycle_ms, verdict, note, reviewed_by, reviewed_at) VALUES (" + ",".join("?" * 19) + ")", rows)
    env.store.db.commit()
    rv = review.Review(env.store, None, lambda: NOW)
    admin = auth.Principal(kind="anon")
    scoped = auth.Principal(kind="user", user_id=1, username="u", assignments=[{"id": 1, "role": auth.QUALITY_MANAGER, "scope_level": "line", "scope_id": "L"}])
    for who in (admin, scoped):
        for f in ({"verdict": "pending"}, {"verdict": "all"}, {"verdict": "pending", "line": "L"}, {"verdict": "confirmed"}, {"verdict": "pending", "plant": "P"},
                  {"verdict": "pending", "station": "S1"}, {"verdict": "pending", "before_id": 3000}):
            w, a = rv._where(who, f)
            plan = _plan(env.store, *rv.list_sql(w, a, f.get("before_id"), 50))
            assert not _no_full_scan(plan), (f, plan)
        plan = _plan(env.store, *rv.summary_sql(who, {}))
        assert not _no_full_scan(plan), plan
    # the pending page really is the newest pending ones, and the verdict filter agrees with the rows
    w, a = rv._where(admin, {"verdict": "pending"})
    got = [r[0] for r in env.store._x(*rv.list_sql(w, a, None, 5)).fetchall()]
    assert got == [i for i in range(4000, 0, -1) if i % 3 == 0 and i % 4 in (2, 3)][:5]
    assert sum(s["ng"] for s in rv.summary(admin, {"since": 0})) == 1333


def test_the_summary_covers_the_last_seven_days_unless_told_otherwise(env):
    day = 86400_000
    env.send("S1", ng(1, "a"), ng(2, "b", ts=NOW - 10 * day), ng(3, "c", ts=NOW - 20 * day))
    admin = env.person("adm", auth.ADMIN)
    n = lambda **q: sum(s["ng"] for s in env.c.get("/vision/ng/summary", params=q, headers=admin).json())      # noqa: E731
    assert n() == 1
    assert n(since=NOW - 15 * day) == 2 and n(since=0) == 3
    assert n(until=NOW - 9 * day) == 1            # only `until`: the 7 days before it (the 10-day-old part)
    assert n(since=NOW - 25 * day, until=NOW - 15 * day) == 1


def test_a_verdict_is_kept_on_the_result_and_in_the_history(env):
    env.send("S1", ng(1, "a"))
    qm = env.person("quality", auth.QUALITY_MANAGER, "line", "L")
    env.c.post("/vision/ng/1/review", json={"verdict": "confirmed", "note": "n1"}, headers=qm)
    env.c.post("/vision/ng/1/review", json={"verdict": "overruled", "note": "n2"}, headers=qm)
    assert env.rows("SELECT verdict, note, reviewed_by FROM vision_result") == [("overruled", "n2", "quality")]
    assert env.rows("SELECT verdict FROM ng_review ORDER BY id") == [("confirmed",), ("overruled",)]


def test_a_huge_cycle_time_is_stored_and_never_blocks_the_queue(env):
    r = env.send("S1", ev(1, cycle_ms=2**64), ev(2, cycle_ms=10**400), ev(3))
    assert (r["stored_up_to"], r["stored"], r["rejected"]) == (3, 2, 1)       # 10**400 is no number at all: a reject
    assert env.rows("SELECT seq, cycle_ms FROM vision_result ORDER BY seq") == [(1, float(2**64)), (3, 40.0)]
    for e in (OverflowError("int too big"), ValueError("bad"), __import__("sqlite3").DataError("x")):
        assert ingest._data_error(e)
    assert not ingest._data_error(RuntimeError("real failure"))


def test_a_heartbeat_succeeds_when_the_image_requests_cannot_be_worked_out(env, monkeypatch, caplog):
    from backend.vision import images
    env.send("S1", ng(1, "a"))

    def boom(self, device_id, now_ms):
        raise RuntimeError("disk on fire")
    with monkeypatch.context() as m:
        m.setattr(images.ImageStore, "wanted", boom)
        with caplog.at_level("ERROR", logger="vision"):
            assert env.hb() == {"ok": True, "image_requests": []}
        assert "image requests" in caplog.text
    assert env.hb()["image_requests"] == ["a"]
    # a failing statement inside wanted() leaves the shared connection usable
    env.store.db.execute("ALTER TABLE vision_image_request RENAME TO vir_gone")
    assert env.hb() == {"ok": True, "image_requests": []}
    env.store.db.execute("ALTER TABLE vir_gone RENAME TO vision_image_request")
    assert env.hb()["image_requests"] == ["a"]


def test_uploads_racing_with_the_size_limit_never_leave_a_row_without_its_file(env):
    import random
    import threading
    from backend.vision import images
    env.send("S1", *[ng(i, f"i{i}") for i in range(1, 41)])
    store = images.ImageStore(env.store, env.dir, 5 * 250)
    errors = []

    def work(seed):
        rnd = random.Random(seed)
        try:
            for _ in range(60):
                i = rnd.randint(1, 40)
                store.save("S1", f"i{i}", rnd.choice(["c1", "c2"]), b"\xff\xd8\xff" + b"d" * 247, NOW)
        except Exception as e:  # noqa: BLE001
            errors.append(e)
    ts = [threading.Thread(target=work, args=(k,)) for k in range(6)]
    [t.start() for t in ts]
    [t.join() for t in ts]
    assert not errors
    rows = env.rows("SELECT file FROM vision_image")
    assert 0 < len(rows) <= 5
    assert {f for (f,) in rows} == set(os.listdir(env.dir))        # every row has its file, and no file lacks a row


def test_stale_part_files_are_swept_and_count_for_nothing(env):
    from backend.vision import images
    os.makedirs(env.dir)
    old, fresh = os.path.join(env.dir, "old.part"), os.path.join(env.dir, "fresh.part")
    for p in (old, fresh):
        open(p, "wb").write(b"x" * 1000)
    os.utime(old, (time.time() - 7200,) * 2)
    store = images.ImageStore(env.store, env.dir, 600)          # the 1000-byte junk files are over the limit, but are not images
    assert not os.path.exists(old) and os.path.exists(fresh)
    env.send("S1", ng(1, "a"))
    store.save("S1", "a", "c", JPEG, NOW)
    assert env.rows("SELECT COUNT(*) FROM vision_image") == [(1,)]      # kept: the .part files were not counted
    os.utime(fresh, (time.time() - 7200,) * 2)
    store._swept = 0
    store.save("S1", "a", "c", JPEG, NOW)
    assert not os.path.exists(fresh)


def _old_events(env):
    """Results as the server stored them before vision_result existed: the events are there, the table and the meta mark are not."""
    env.send("S1", ng(1, "a", part_id="P1"), ev(2), ng(3), ev(4, cycle_ms=7))
    env.send("S3", ng(1, "z"))
    before = env.rows("SELECT device_id, seq, ts, received, ok, part_id, image_id, reasons, cycle_ms FROM vision_result ORDER BY device_id, seq")
    env.store.db.execute("CREATE TABLE IF NOT EXISTS vision_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)")
    for t in ("vision_result", "vision_image_request", "vision_meta"):
        env.store.db.execute(f"DELETE FROM {t}")
    env.store.db.commit()
    return before


COLS = "SELECT device_id, seq, ts, received, ok, part_id, image_id, reasons, cycle_ms FROM vision_result ORDER BY device_id, seq"


def _wait(cond, seconds=10):
    end = time.time() + seconds
    while time.time() < end:
        if cond():
            return True
        time.sleep(0.02)
    return False


def test_results_are_filled_from_old_events_in_batches(env):
    before = _old_events(env)
    assert ingest.backfill_results(env.store, batch=2) == 5
    assert env.rows(COLS) == before
    assert env.rows("SELECT COUNT(*) FROM vision_image_request") == [(0,)]       # no image is asked for an old part
    assert env.rows("SELECT value FROM vision_meta WHERE key = 'results_backfill'") == [("done",)]
    env.store.db.execute("DELETE FROM vision_result WHERE seq = 1")
    env.store.db.commit()
    assert ingest.backfill_results(env.store) == 0                                # done flag: it runs once
    assert env.rows("SELECT COUNT(*) FROM vision_result") == [(3,)]


def test_an_interrupted_backfill_resumes_at_the_next_start_and_a_finished_one_does_not_run_again(env):
    before = _old_events(env)
    assert ingest.backfill_results(env.store, batch=2, max_batches=1) == 2         # stopped after one batch
    assert env.rows("SELECT COUNT(*) FROM vision_result") == [(2,)]
    assert env.rows("SELECT value FROM vision_meta WHERE key = 'results_backfill'")[0][0] != "done"
    # a result a station sends live meanwhile is never made twice
    env.send("S1", ng(5, "live"))
    with env.make():                                                                 # the app starts: the backfill resumes in its thread
        assert _wait(lambda: env.rows("SELECT value FROM vision_meta WHERE key = 'results_backfill'") == [("done",)])
    got = env.rows(COLS)
    assert [g for g in got if g[6] != "live"] == before and sum(g[6] == "live" for g in got) == 1 and len(got) == len(before) + 1
    env.store.db.execute("DELETE FROM vision_result WHERE seq = 1")
    env.store.db.commit()
    with env.make():
        time.sleep(0.3)
    assert env.rows("SELECT COUNT(*) FROM vision_result WHERE seq = 1") == [(0,)]    # finished once, never again


def test_mounting_does_not_wait_for_the_backfill_and_a_stop_ends_it(env, monkeypatch):
    import threading
    _old_events(env)
    made = threading.Event()
    real = ingest.backfill_step

    def slow(store, batch=500):
        made.set()
        time.sleep(0.2)
        return real(store, batch)
    monkeypatch.setattr(ingest, "backfill_step", slow)
    t0 = time.time()
    c = env.make()                                         # create_app and the mount return at once, nothing ran yet
    assert time.time() - t0 < 0.15 and not made.is_set() and env.rows("SELECT COUNT(*) FROM vision_result") == [(0,)]
    with c:
        assert made.wait(5)
    ev_ = threading.Event()
    ev_.set()
    assert ingest.backfill_results(env.store, stop=ev_) == 0                          # a stop that is already set does no batch


def test_the_backfill_batch_is_an_index_range_scan_of_the_primary_key(env):
    import sqlite3
    if not isinstance(env.store.db, sqlite3.Connection):
        pytest.skip("the query plan is read from SQLite")
    env.send("S1", *[ev(i) for i in range(1, 51)])
    sql = ("SELECT device_id, queue_id, seq, kind, received, body FROM vision_event "
           "WHERE (device_id, queue_id, seq) > (?,?,?) ORDER BY device_id, queue_id, seq LIMIT ?")
    plan = _plan(env.store, sql, ("S1", "q1", 10, 500))
    assert any("USING" in d and "autoindex" in d.lower() and "vision_event" in d for d in plan), plan
    assert not any("TEMP B-TREE" in d for d in plan) and not _no_full_scan(plan), plan
    # and the step really reads that query: its source is the one planned
    import inspect
    src = inspect.getsource(ingest.backfill_step)
    assert "(device_id, queue_id, seq) > (?,?,?) ORDER BY device_id, queue_id, seq LIMIT ?" in src and "kind =" not in src.split("WHERE")[1].split("LIMIT")[0]
