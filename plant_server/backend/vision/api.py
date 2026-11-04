"""HTTP endpoints of the Vision module (PL2).

Stations (device token, kind vision_station):
  POST /vision/ingest/v1/events     a batch of stored events (the station agent's contract)
  POST /vision/ingest/v1/heartbeat  the station is alive; never stored as an event
People (sign-in or service key, scoped by plant and line like every other endpoint):
  GET /vision/floor                 every line the caller may see, live
  GET /vision/lines/{plant}/{line}  one line
  WS  /vision/ws                    the floor pushed on connect and when it changes (Req 17.3 LV-01..04): ?token= or ?api_key=,
                                    ?plant= (and ?line=) to narrow it, ?lang=; {"type": "floor", "lines": [...]} as /vision/floor
                                    each open alarm also carries station_name (the device's name) and the
                                    catalogue's title and fix (vision/alarms.py) in ?lang= (default English)
Station images (docs/vision-system/NG-REVIEW.md): the heartbeat answer lists the image ids the server wants
  POST /vision/ingest/v1/images/{image_id}?camera=<name>   a JPEG of at most 300 KB (then .../done, or .../missing)
NG review (sign-in; view, and the reviewer roles for a verdict):
  GET /vision/ng, /vision/ng/summary, /vision/ng/export.csv, /vision/ng/{id}, /vision/ng/{id}/image/{camera}
  POST /vision/ng/{id}/review       GET /vision/review  the web page
Changeover verification (docs/vision-system/CHANGEOVER-VERIFY.md): the heartbeat's `changeover` record is kept as a request
  while the check is on for the line, and the heartbeat answer carries its state (pending, accepted, refused, not_needed)
  GET /vision/changeover, /vision/changeover/{id}       (sign-in; view)
  POST /vision/changeover/{id}/decide                   (the reviewer roles of the line, never the operator or a service key)
  GET /vision/changeover/page       the web page
  GET, PUT /admin/vision/settings   (admin) the check on or off for a plant or a line; on when nothing is set
Print quality (Req 17.3 PQ-02; sign-in, scoped like the floor):
  GET /vision/grades/trend          each station and tool's print grade in time buckets, and the drift status of its latest samples
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import threading
import time
from pathlib import Path
from typing import Any

from anyio import to_thread

from fastapi import Depends, FastAPI, Header, HTTPException, Query, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, JSONResponse, StreamingResponse
from pydantic import BaseModel, Field

from ..auth import SESSION_IDLE_S, Principal, resolve_token
from ..master import device_for_token
from . import alarms as catalogue
from . import changeover, events, grades, images, ingest, review
from .alerts import VisionAlerts
from .live import VisionFloor

REVIEW_PAGE = Path(__file__).resolve().parent.parent.parent / "dashboard" / "ng-review.html"
GRADES_PAGE = Path(__file__).resolve().parent.parent.parent / "dashboard" / "grades.html"
CHANGEOVER_PAGE = Path(__file__).resolve().parent.parent.parent / "dashboard" / "changeover.html"


class ReviewBody(BaseModel):
    verdict: str
    note: str = Field(default="", max_length=review.MAX_NOTE)


class DecideBody(BaseModel):
    decision: str
    note: str = Field(default="", max_length=review.MAX_NOTE)


class SettingBody(BaseModel):
    key: str
    plant: str
    line: str | None = None
    value: Any = None


log = logging.getLogger("vision")


def _now_ms() -> int:
    return int(time.time() * 1000)


def mount(app: FastAPI, store, get_principal, replay_hours: float = 24, clock=_now_ms, alerts=None, alert_tick_s: float = 5,
          image_dir: str | None = None, image_limit_bytes: int | None = None, session_idle_s: int | None = SESSION_IDLE_S,
          ws_tick_s: float = 0.5, ws_refresh_s: float = 5) -> VisionFloor:
    """`image_dir` is the folder of the stations' NG images (default: env VISION_IMAGE_DIR, else ./vision_images, made on the first
    upload) and `image_limit_bytes` its size limit (env VISION_IMAGE_LIMIT_BYTES, default 2 GB; the oldest go first).
    `alerts` is the core AlertEngine (None: no vision alerts). Alerts are evaluated after every batch and heartbeat,
    and every `alert_tick_s` while the server runs, so a station that falls silent is noticed with nothing arriving.
    A batch or heartbeat only wakes the evaluating thread; it never waits on alerting or fails because of it.
    `session_idle_s` is the server's idle limit, for the open socket's recheck of its session; `/vision/ws` looks at the floor
    every `ws_tick_s` and builds the snapshot again when an event or heartbeat came in, or `ws_refresh_s` passed."""
    floor = VisionFloor(clock)
    imgs = images.ImageStore(store, image_dir or os.environ.get("VISION_IMAGE_DIR") or "vision_images",
                             image_limit_bytes if image_limit_bytes is not None else int(os.environ.get("VISION_IMAGE_LIMIT_BYTES") or images.DEFAULT_LIMIT))
    rev = review.Review(store, imgs, clock)
    cov = changeover.Changeover(store, clock)
    for e in ingest.replay(store, clock() - int(replay_hours * 3600_000)):
        floor.apply(e)
    # Each station as last heard, so a line whose stations went quiet before a restart shows offline.
    for hb in ingest.station_status(store):
        floor.heartbeat(hb, hb["last_seen"])
    app.state.vision_floor = floor
    # Events stored before vision_result existed are turned into results in the background, a small batch at a time, so the
    # server serves at once and ingest and heartbeats are never held up; it resumes after a stop and runs once.
    bf_stop = threading.Event()

    def run_backfill():
        try:
            ingest.backfill_results(store, stop=bf_stop)
        except Exception:  # noqa: BLE001
            log.exception("vision: the backfill of results stopped; it resumes at the next start")
    app.router.on_startup.append(lambda: threading.Thread(target=run_backfill, name="vision-backfill", daemon=True).start())
    app.router.on_shutdown.append(bf_stop.set)
    valerts = None
    if alerts is not None:
        alerts.wall_clock = lambda: clock() // 1000
        valerts = VisionAlerts(alerts, floor, store)
        app.state.vision_alerts = valerts
        stop = threading.Event()
        if alert_tick_s > 0:
            app.router.on_startup.append(lambda: threading.Thread(target=valerts.run, args=(stop, alert_tick_s), name="vision-alerts",
                                                                  daemon=True).start())
            app.router.on_shutdown.append(lambda: (stop.set(), valerts.poke()))

    async def evaluate_alerts():
        if valerts is not None:
            await to_thread.run_sync(valerts.poke)

    def station(authorization: str | None) -> dict:
        token = authorization.split(None, 1)[1].strip() if authorization and authorization.lower().startswith("bearer ") else None
        d = device_for_token(store, token)
        if d is None or d["kind"] != "vision_station":
            raise HTTPException(401, "unknown, disabled or non-station device token")
        if not d["line_id"]:
            raise HTTPException(403, "this station is not on a line: give it one in /admin/devices")
        return d

    async def body_json(request: Request):
        try:
            return json.loads(await request.body())
        except ValueError:
            raise HTTPException(400, "the body is not JSON")

    @app.post("/vision/ingest/v1/events")
    async def ingest_events(request: Request, authorization: str | None = Header(default=None)):
        dev = await to_thread.run_sync(station, authorization)
        batch = await body_json(request)
        try:
            # The database work runs off the event loop, so one big batch never stalls other requests.
            r = await to_thread.run_sync(ingest.ingest, store, dev, batch, clock())
        except ingest.Refused as e:
            return JSONResponse({"error": str(e)}, status_code=e.status)
        except ingest.Conflict as e:
            return JSONResponse({"error": str(e)}, status_code=409)
        for ev in r.pop("fresh"):
            floor.apply(ev)
        await evaluate_alerts()
        return r

    @app.post("/vision/ingest/v1/heartbeat")
    async def heartbeat(request: Request, authorization: str | None = Header(default=None)):
        dev = await to_thread.run_sync(station, authorization)
        hb = await body_json(request)
        if not isinstance(hb, dict) or hb.get("kind") != "heartbeat":
            raise HTTPException(400, "not a heartbeat")
        if (hb.get("plant"), hb.get("line"), hb.get("station")) != (dev["plant_id"], dev["line_id"], dev["id"]):
            raise HTTPException(403, "the heartbeat is not this station's")
        # A changeover record the server cannot take is left out, never the heartbeat: a refused heartbeat would stop the
        # station's events too. Without an answer the station's check stays undone and its line does not start.
        co = hb.pop("changeover", None)
        if co is not None and (p := changeover.code_problem(co)):
            log.warning("vision: the changeover record of %s was left out: %s", dev["id"], p)
            co = None
        if p := events.problems(hb):
            raise HTTPException(400, "; ".join(p))
        now = clock()
        await to_thread.run_sync(ingest.save_heartbeat, store, dev, hb, now)
        floor.heartbeat(hb, now)
        await evaluate_alerts()
        # The server asks for the NG images it wants in the same answer; a station only makes outgoing connections.
        # The heartbeat itself is stored by now: asking for images must never make it fail.
        try:
            wanted = await to_thread.run_sync(imgs.wanted, dev["id"], now)
        except Exception:  # noqa: BLE001
            log.exception("vision: could not work out the image requests of %s", dev["id"])
            wanted = []
        answer = {"ok": True, "image_requests": wanted}
        # The same for the changeover check: a failure here leaves the field out (the station keeps its last known state, and
        # without an accepted answer it does not run) and never fails the heartbeat.
        if co is not None:
            try:
                answer["changeover"] = await to_thread.run_sync(cov.on_heartbeat, dev, co, now)
            except Exception:  # noqa: BLE001
                log.exception("vision: could not work out the changeover check of %s", dev["id"])
        return answer

    async def image_call(fn, *args):
        try:
            return await to_thread.run_sync(fn, *args)
        except images.Bad as e:
            return JSONResponse({"error": str(e)}, status_code=e.status)

    @app.post("/vision/ingest/v1/images/{image_id}")
    async def upload_image(image_id: str, request: Request, camera: str | None = None, authorization: str | None = Header(default=None)):
        dev = await to_thread.run_sync(station, authorization)
        try:
            await to_thread.run_sync(imgs.check_upload, dev["id"], image_id, camera)
            declared = request.headers.get("content-length", "")
            if declared.isdigit() and int(declared) > images.MAX_IMAGE:
                raise images.Bad(413, f"the image is over {images.MAX_IMAGE} bytes")
            # Read in pieces and stop at the limit: an oversize body is never held whole in memory.
            body = bytearray()
            async for chunk in request.stream():
                body += chunk
                if len(body) > images.MAX_IMAGE:
                    raise images.Bad(413, f"the image is over {images.MAX_IMAGE} bytes")
            await to_thread.run_sync(imgs.save, dev["id"], image_id, camera, bytes(body), clock())
        except images.Bad as e:
            return JSONResponse({"error": str(e)}, status_code=e.status)
        return {"ok": True}

    @app.post("/vision/ingest/v1/images/{image_id}/done")
    async def image_done(image_id: str, authorization: str | None = Header(default=None)):
        dev = await to_thread.run_sync(station, authorization)
        return await image_call(imgs.mark, dev["id"], image_id, "done", clock()) or {"ok": True}

    @app.post("/vision/ingest/v1/images/{image_id}/missing")
    async def image_missing(image_id: str, authorization: str | None = Header(default=None)):
        dev = await to_thread.run_sync(station, authorization)
        return await image_call(imgs.mark, dev["id"], image_id, "missing", clock()) or {"ok": True}

    def described(lines: list[dict], lang: str) -> list[dict]:
        """Copies (the floor's own alarm dicts are never changed) with each alarm's station name and catalogue text."""
        names = {d["id"]: d["name"] for d in store.device_list() if d["kind"] == "vision_station"}
        return [{**ln, "alarms": [{**a, "station_name": names.get(a["station"]) or a["station"], **catalogue.text(a["code"], lang)}
                                  for a in ln["alarms"]]} for ln in lines]

    def scoped(principal: Principal, lang: str, plant: str | None = None, line: str | None = None) -> list[dict]:
        return described([ln for ln in floor.floor() if principal.can_view(ln["plant"], ln["line"])
                          and plant in (None, ln["plant"]) and line in (None, ln["line"])], lang)

    @app.get("/vision/floor")
    def vision_floor(lang: str = "en", principal: Principal = Depends(get_principal)):
        return scoped(principal, lang)

    @app.websocket("/vision/ws")
    async def vision_ws(ws: WebSocket, plant: str | None = None, line: str | None = None, lang: str = "en",
                        api_key_q: str | None = Query(None, alias="api_key"), token_q: str | None = Query(None, alias="token")):
        """The caller is checked as for GET /vision/floor (a token, else the service key when the server has one, else open),
        and the session again every 5 s without counting as use: an ended session closes the socket (4401), and a change of
        roles changes what it is sent. A line is named by plant and line; one the caller may not view is refused (4403).
        A refusal is a close after the accept, so a browser sees its code (a refused handshake is only HTTP 403 to it)."""
        def check():
            return get_principal(x_api_key=api_key_q, authorization=f"Bearer {token_q}" if token_q else None)
        await ws.accept()
        try:
            principal = await to_thread.run_sync(check)
        except HTTPException:
            await ws.close(code=4401)
            return
        if line is not None and plant is None:
            await ws.close(code=4400)
            return
        if line is not None and not principal.can_view(plant, line):
            await ws.close(code=4403)
            return
        sent, seen, built, checked = None, None, 0.0, time.monotonic()
        try:
            while True:
                now = time.monotonic()
                if token_q and now - checked >= 5:
                    principal = await to_thread.run_sync(resolve_token, store, token_q, session_idle_s, False)
                    if principal is None:
                        await ws.close(code=4401)
                        return
                    checked, seen = now, None   # roles may have changed: build it again
                if floor.version != seen or now - built >= ws_refresh_s:
                    seen, built = floor.version, now
                    snap = {"type": "floor", "lines": await to_thread.run_sync(scoped, principal, lang, plant, line)}
                    text = json.dumps(snap, separators=(",", ":"))
                    if text != sent:
                        await ws.send_text(text)
                        sent = text
                await asyncio.sleep(ws_tick_s)
        except (WebSocketDisconnect, RuntimeError):
            pass

    @app.get("/vision/lines/{plant}/{line}")
    def vision_line(plant: str, line: str, lang: str = "en", principal: Principal = Depends(get_principal)):
        if not principal.can_view(plant, line):
            raise HTTPException(403, "you do not have view access to this plant/line")
        ln = floor.line(plant, line)
        if ln is None:
            raise HTTPException(404, f"no vision data for line '{line}' of plant '{plant}'")
        return described([ln], lang)[0]

    # ---- print quality: the trend for the dashboard and the mobile app ----
    @app.get("/vision/grades/trend")
    def grade_trend(plant: str | None = None, line: str | None = None, station: str | None = None, tool: str | None = None,
                    since: int | None = None, until: int | None = None, bucket_s: int = Query(3600, ge=60, le=7 * 86400),
                    principal: Principal = Depends(get_principal)):
        """`since`, `until` in ms (default: the last 24 hours); at most grades.MAX_BUCKETS buckets."""
        if plant is not None and line is not None and not principal.can_view(plant, line):
            raise HTTPException(403, "you do not have view access to this plant/line")
        until = clock() if until is None else until
        since = until - 86400_000 if since is None else since
        if since >= until:
            raise HTTPException(400, "since must be before until")
        if (until - since) // (bucket_s * 1000) >= grades.MAX_BUCKETS:
            raise HTTPException(400, f"at most {grades.MAX_BUCKETS} buckets: widen bucket_s or narrow since..until")
        rule = next((r for r in alerts.rules if r["type"] == "vision_grade_drift"), None) if alerts is not None else None
        drift = {(d["plant"], d["line"], d["station"], d["tool"]): d
                 for d in grades.status(store, clock(), rule, principal.can_view)}
        series = grades.trend(store, since, until, bucket_s, plant, line, station, tool, principal.can_view)
        for s in series:
            s["drift"] = drift.get((s["plant"], s["line"], s["station"], s["tool"]))
        return {"since": since, "until": until, "bucket_s": bucket_s, "rule": grades.rule_params(rule), "series": series}

    # ---- NG review: the people's side ----
    def denied(fn, *args):
        try:
            return fn(*args)
        except review.Denied as e:
            raise HTTPException(e.status, str(e))

    def filters(plant, line, station, reason, verdict, since, until, before_id) -> dict:
        if verdict not in review.FILTERS:
            raise HTTPException(400, f"verdict must be one of {', '.join(review.FILTERS)}")
        return {"plant": plant, "line": line, "station": station, "reason": reason, "verdict": verdict, "since": since, "until": until,
                "before_id": before_id}

    @app.get("/vision/grades/page", include_in_schema=False)
    def grades_page():
        return FileResponse(GRADES_PAGE)

    @app.get("/vision/review", include_in_schema=False)
    def review_page():
        return FileResponse(REVIEW_PAGE)

    @app.get("/vision/ng")
    def ng_list(plant: str | None = None, line: str | None = None, station: str | None = None, reason: str | None = None, verdict: str = "all",
                since: int | None = None, until: int | None = None, before_id: int | None = None,
                limit: int = Query(50, ge=1, le=review.MAX_LIMIT), principal: Principal = Depends(get_principal)):
        items = rev.rows(principal, filters(plant, line, station, reason, verdict, since, until, before_id), limit)
        # next_before_id is null on the last page, so a client knows when to stop asking.
        return {"items": items, "next_before_id": items[-1]["id"] if len(items) == limit else None}

    @app.get("/vision/ng/summary")
    def ng_summary(plant: str | None = None, line: str | None = None, station: str | None = None, since: int | None = None, until: int | None = None,
                   principal: Principal = Depends(get_principal)):
        return rev.summary(principal, {"plant": plant, "line": line, "station": station, "since": since, "until": until})

    @app.get("/vision/ng/export.csv")
    def ng_export(plant: str | None = None, line: str | None = None, station: str | None = None, verdict: str = "all", since: int | None = None,
                  until: int | None = None, principal: Principal = Depends(get_principal)):
        f = filters(plant, line, station, None, verdict, since, until, None)
        return StreamingResponse(denied(rev.export, principal, f), media_type="text/csv; charset=utf-8",
                                 headers={"Content-Disposition": 'attachment; filename="ng-verdicts.csv"'})

    @app.get("/vision/ng/{result_id}")
    def ng_one(result_id: int, principal: Principal = Depends(get_principal)):
        return denied(rev.one, principal, result_id)

    @app.get("/vision/ng/{result_id}/image/{camera:path}")
    def ng_image(result_id: int, camera: str, principal: Principal = Depends(get_principal)):
        return FileResponse(denied(rev.image_path, principal, result_id, camera), media_type="image/jpeg", headers={"Cache-Control": "private, max-age=3600"})

    @app.post("/vision/ng/{result_id}/review")
    def ng_review(result_id: int, body: ReviewBody, principal: Principal = Depends(get_principal)):
        if body.verdict not in review.VERDICTS:
            raise HTTPException(400, f"verdict must be one of {', '.join(review.VERDICTS)}")
        return denied(rev.give, principal, result_id, body.verdict, body.note)

    # ---- changeover verification: the supervisor's side ----
    def cov_denied(fn, *args):
        try:
            return fn(*args)
        except changeover.Denied as e:
            raise HTTPException(e.status, str(e))

    @app.get("/vision/changeover/page", include_in_schema=False)
    def changeover_page():
        return FileResponse(CHANGEOVER_PAGE)

    @app.get("/vision/changeover")
    def changeover_list(state: str = "pending", plant: str | None = None, line: str | None = None,
                        limit: int = Query(50, ge=1, le=changeover.MAX_LIMIT), principal: Principal = Depends(get_principal)):
        if state not in changeover.FILTERS:
            raise HTTPException(400, f"state must be one of {', '.join(changeover.FILTERS)}")
        return {"items": cov.rows(principal, state, plant, line, limit)}

    @app.get("/vision/changeover/{cid}")
    def changeover_one(cid: int, principal: Principal = Depends(get_principal)):
        return cov_denied(cov.one, principal, cid)

    @app.post("/vision/changeover/{cid}/decide")
    def changeover_decide(cid: int, body: DecideBody, principal: Principal = Depends(get_principal)):
        return cov_denied(cov.decide, principal, cid, body.decision, body.note)

    def require_admin(principal: Principal) -> None:
        if not principal.is_admin():
            raise HTTPException(403, "system_administrator role required")

    @app.get("/admin/vision/settings")
    def vision_settings(principal: Principal = Depends(get_principal)):
        require_admin(principal)
        return {"items": cov.settings()}

    @app.put("/admin/vision/settings")
    def vision_setting_put(body: SettingBody, principal: Principal = Depends(get_principal)):
        # A named Admin only: the shared service key is on every phone, and a change must say who made it (CV-02).
        require_admin(principal)
        if principal.kind != "user":
            raise HTTPException(403, "sign in as an administrator to change this setting")
        if body.key != changeover.KEY:
            raise HTTPException(400, f"key must be {changeover.KEY}")
        if not isinstance(body.value, bool):
            raise HTTPException(400, "value must be true or false")
        if store.master_get("plant", body.plant) is None:
            raise HTTPException(404, f"unknown plant '{body.plant}'")
        if body.line is not None:
            ln = store.master_get("line", body.line)
            if ln is None or ln["plant_id"] != body.plant:
                raise HTTPException(400, f"line '{body.line}' is not in plant '{body.plant}'")
        cov.set_setting(body.plant, body.line, body.value, principal.actor_name("service"))
        return {"items": [s for s in cov.settings() if s["key"] == body.key and s["plant"] == body.plant and s["line"] == body.line]}

    return floor
