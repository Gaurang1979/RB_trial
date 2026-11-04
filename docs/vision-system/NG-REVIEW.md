# NG review: per-part results, NG images and verdicts

Status: built in PR #71 (2026-10-30). Plant Insight F5 (PLANT-DASHBOARD-AND-MOBILE.md
sections 5 and 6); PL2 follow-up in PLANT-PLATFORM.md.

Supervisors and quality staff look at the parts a station rejected and give
each one a verdict: **confirmed** (really NG), **overruled** (a good part,
a false reject) or **unsure**. Overruled parts are the training set of the
next model round. A verdict never changes what the line did: the part was
rejected when it was inspected (line safety, requirements section 12).

## 1. How the images travel (owner, 2026-10-30)

The owner's rule: "the station sends when the server asks, reduced image of
300 kb". The full-resolution image stays on the line PC for training export.

The station only makes outgoing connections, so the server asks in its
answer to the station's heartbeat (every 5 s):

```
station                                   plant server
  inspection.result {image_id: "…"}  ──▶  vision_result row, and for an NG with
                                          an image_id a vision_image_request (wanted)
  heartbeat                          ──▶
                                     ◀──  {"ok": true, "image_requests": ["…", …]}
  POST images/{image_id}?camera=cam1 ──▶  JPEG ≤ 300 KB kept on disk (vision_image)
  POST images/{image_id}/done        ──▶  request done
  (or POST images/{image_id}/missing ──▶  the station no longer has it)
```

The server asks for every new NG image at once, newest first, so the images
are on the server before anyone opens them and review works while the line
PC is off or busy.

## 2. Station side (dotnet)

- **Image id.** The image's name (owner, 2026-10-31; `StationGuard.ImageName`):
  `L01_C2_J045_261031-104512_000123` = line (station setting `LineNumber`,
  default 1), camera on the line (camera index + 1), the job's PLC number
  (J000 when it has none), local yymmdd-hhmmss, and the part serial (the
  station's part count for the day, 1-999999, back to 1 at local midnight,
  kept in `data/part-serial.json`). 32 characters, only `[A-Z0-9_-]`. The
  cameras of one part differ only in `C<n>`. `inspection.result.image_id` is
  the name of the part's first camera whose image was kept; a part with no
  kept image sends none. The same string is the image id in results.db
  (`inspections.image_id`, `parts.image_id`), GetImage and the reports' Image
  column. With camera channels (each camera fired when the part reaches it)
  each camera counts its own serial, so the names of one part match while the
  line skips no part; the PLC part id in the `.json` links them for sure.
- **NgImageStore** saves a named image as
  `<ng-images>/L01/2026-10-31/J045/C1/NG/L01_C1_J045_261031-104512_000123.bmp`
  (`OK/` for an OK image in save mode All) with a `.json` beside it: `image_id`
  (the name), `camera` (its display name), `job` ("job-camera" label),
  `product`, `ok`, `reasons`, `plc_trigger_id`, `part_id`, `time` (local, with
  milliseconds and offset), `time_zone`, `batch`, `batch_fields`, `line`,
  `job_number`, `serial`. Any camera's name of a part finds all its files
  (the store indexes by the name without the camera). Images saved before
  2026-10-31 (`<image_id>_<camera>.bmp` under `<ng-images>/yyyy-MM-dd/<job-camera>/`,
  ids `yyyyMMddTHHmmssfffZ-nnnnnn`) are never moved or renamed: the store,
  NG review, the Run page's NG list and Teach still find and open them
  (`ImageName.ParseAny` reads the old forms). Teach grabs are named the same
  way in `data/teach/`, with the day's grab count as the serial.
  An image still waiting for the writer is "not yet": the station skips it
  this round and the server asks again.
- **Reduction** (Vision Studio, SkiaSharp): long side at most 1600 px, JPEG
  quality 80, then 70, 60, 50, 40, then the size × 0.75 again from 80,
  until the file is at most 300 KB (307,200 bytes).
- **Agent.** The heartbeat's answer carries `image_requests`. The agent
  hands them to an image source (Vision Studio's NgImageStore and encoder)
  and uploads at most 3 parts per round, after the events and within 3 s,
  so events always go first. A 400 or 413 is final for that camera (`done`
  when another camera went, else `missing`); a timeout, network error or
  5xx ends the round's image work and the server asks again.
- **Image source.** One below-normal worker reduces one image at a time,
  at most 20 parts a minute, and only between parts (never while a part is
  inspected or the PC is under High pressure).
- **Camera names** are cleaned on the line PC to what the server accepts and
  made unique within the job (` (2)`, ` (3)`).

## 3. Station endpoints (device token, kind `vision_station`)

| Call | Body | Answer |
| --- | --- | --- |
| `POST /vision/ingest/v1/heartbeat` | heartbeat (as before) | `{"ok": true, "image_requests": [up to 10 image ids]}` |
| `POST /vision/ingest/v1/images/{image_id}?camera=<name>` | `image/jpeg`, at most 307,200 bytes, starts `FF D8 FF` | `{"ok": true}`; 404 when no result of this station has that image id; 413 over the size; 400 not a JPEG or a bad camera name |
| `POST /vision/ingest/v1/images/{image_id}/done` | none | `{"ok": true}` |
| `POST /vision/ingest/v1/images/{image_id}/missing` | none | `{"ok": true}` |

Camera names: 1 to 64 printable characters (Python's `str.isprintable`),
not only spaces, not `.` or `..`; at most 8 cameras per part. An image sent
again replaces the earlier one. A request not answered within 7 days is
`expired` and no longer asked for.

## 4. Server tables

- `vision_result`: one row per stored `inspection.result` (OK and NG), with
  a surrogate `id`, written in the same transaction as the event. It also
  holds the latest verdict, so the lists read from indexes. Results stored
  before this table existed are filled in from `vision_event` by a
  background job after start-up, which resumes if stopped.
- `vision_image_request`: per station and image id; `wanted`, `done`,
  `missing` or `expired`.
- `vision_image`: per station, image id and camera; the JPEG's file under
  the image folder and its size. The folder has a size limit (default
  2 GB); the oldest images go first.
- `ng_review`: every verdict given (never updated or deleted, so the
  history stays); the newest is copied to the result row in the same
  transaction.

## 5. People endpoints (sign-in, scoped by plant and line)

| Call | Who | What |
| --- | --- | --- |
| `GET /vision/ng` | view | `{"items": [...], "next_before_id"}`, NG parts newest first, each with `images` [{camera, url}]; filters `plant`, `line`, `station`, `reason`, `verdict` (`pending`, `confirmed`, `overruled`, `unsure`, `all`), `since`, `until` (ms), `before_id`, `limit` (≤ 200) |
| `GET /vision/ng/{id}` | view | one NG part with its verdict history |
| `GET /vision/ng/{id}/image/{camera}` | view | the JPEG |
| `POST /vision/ng/{id}/review` | reviewer | `{"verdict": "confirmed" \| "overruled" \| "unsure", "note": "…"}` |
| `GET /vision/ng/summary` | view | per line: NG parts, reviewed, each verdict, overrule rate; the last 7 days unless `since` / `until` |
| `GET /vision/ng/export.csv` | reviewer | the verdicts with image ids, for the line PC's training export |
| `GET /vision/review` | — | the web NG review page |

Reviewers: admin, plant manager, production manager and quality manager
for the line. A verdict names a person, so a service key cannot give one:
the web page and the mobile app sign in with the person's account
(`/auth/login`, Bearer token). The plant server refuses sign-in from a
device after 5 wrong passwords for a user name, or 20 for any names, in
15 minutes (owner, 2026-10-30: per user and device, so nobody can lock a
colleague out from their own device).

## 6. Screens

- **Web** (`/vision/review`): filter bar, the part's camera images, reasons,
  station and time; keys → confirm, ← overrule, ↑ unsure.
- **Mobile app** (Vision module): the queue of parts waiting for review;
  swipe right to confirm NG, left to overrule, up for unsure, with the same
  three buttons for one-handed use. A verdict needs a live connection; it
  is never queued offline. The person signs in per plant server (only the
  session token is kept, in secure storage); without a sign-in the app reads
  with the plant key and shows no verdict buttons. A note of up to 500
  characters can go with each verdict; it is kept in the part's history.
  The line view lists the line's last NG parts (its NG strip); a tap opens
  that part by its `result_id` (below) for a verdict, also when it already
  has one (a new verdict is added to the history). The line view also has
  the account button.

The live floor (`GET /vision/floor`, `GET /vision/lines/{plant}/{line}`, and
`WS /vision/ws`, which pushes it when it changes, Req 17.3 LV-01..04)
lists a line's last 20 NG reasons in `last_ng` (one entry per reason, so a
part with two reasons has two), each with `result_id`, the part's id in
`/vision/ng/{id}`; the app's strip shows one row per part. The server sets it from its own
`vision_result` row, when the event arrives and when the live state is
rebuilt at start; a `result_id` sent by a station is never used. It is
null for a part stored before `vision_result` existed that the backfill
has not reached yet.
