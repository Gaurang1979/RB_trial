# Plant Insight: live plant dashboard and mobile app

2026-10-29 · design, not yet built · owner ask: "Proper development, UI
Design live dashboard and industry leading features required. I want
mobile app also for multiple lines for plant operators and admin."

> **2026-10-29 (owner):** one plant server and one database for every product, in Python —
> see [PLANT-PLATFORM.md](PLANT-PLATFORM.md). The "plant server" below is now the Vision
> module of that server; everything else here still holds.

This document adds a plant layer above the vision stations: one server per
plant that collects every station's results, a live web dashboard for all
lines, and one mobile app for operators, supervisors, maintenance and
admins. It extends [the requirements](../REQUIREMENTS-VISION-SYSTEM.md)
sections 6.4 (remote web HMI) and 12 (results and data), and the existing
single-station dashboard in `dotnet/Dashboard`. Where this document and the
requirements disagree, the requirements win until they are updated.
The clickable UI design is in `design/prototypes/plant-insight/`
(https://claude.ai/artifact/Py9Qexish3fDHqKZYDJvXr).

## 1. What makes it better than what plants have today

Vision vendors sell the inspection; the plant then builds its own reports
from CSV files. Plant Insight ships the plant view with the vision system,
so a plant gets these on day one, with no MES project:

| # | Feature | Why it matters on the shop floor |
| --- | --- | --- |
| F1 | **Live floor map** of every line: state by colour and shape, yield, rate, top defect, open alarm | The supervisor sees the whole plant in one look, on a TV, a PC or a phone |
| F2 | **OEE from the vision data**: availability, performance and quality per line, shift and product, with the loss tree | Most plants calculate OEE by hand at the end of the day |
| F3 | **SPC before rejects**: X̄-R and I-MR charts, live Cp/Cpk, Western Electric rules raise a warning while parts are still good | Drift is fixed before the first bad part, not after a batch is lost |
| F4 | **Defect Pareto and defect map**: where on the part the NGs are, per camera | Points maintenance at the right station (a dirty nozzle, a worn guide) |
| F5 | **NG review loop**: supervisors confirm or overrule NG images on the phone with one swipe; overruled images feed the next training round | False rejects fall every week; the AI learns from the plant's own decisions |
| F6 | **AI health**: score distribution, drift against the training set, confidence trend, "retrain suggested" | AI tools stay explainable and trusted (requirements section 5) |
| F7 | **Camera and lighting health**: focus score, brightness drift, frame drops, PC CPU, GPU and disk, with predictive alerts | A failing light is replaced at the next stop, not after a night of NGs |
| F8 | **Alarm management** after ISA-18.2: priorities, shelving, escalation to the next person when nobody acknowledges | The right person is called; nobody is flooded |
| F9 | **Guided fixes** in the operator's language, with pictures and spoken steps ("What do I do?") | Unskilled operators clear most alarms without calling anyone |
| F10 | **Traceability in seconds**: search by code, lot or time, see the image, the result, the job and model version | Customer complaints and recalls are answered the same day |
| F11 | **Shift handover report**, made automatically, PDF and e-mail | No manual report at shift change |
| F12 | **Changeover tracking**: time from last good part of one product to first good part of the next | SMED work has a number to improve |
| F13 | **Golden-part check at shift start**: the operator shows a known good and a known bad part; the station must pass and fail them | Proves the inspection works before production starts (pharma, food, auto) |
| F14 | **21 CFR Part 11 mode**: e-signatures on job, tolerance and model changes, full audit trail | Pharma and medical customers can use it as is |
| F15 | **Open integration**: OPC UA, MQTT (Sparkplug B), REST, webhooks, CSV and Excel export, MES and ERP connectors | Fits any plant's existing systems |
| F16 | **Multi-plant roll-up** for management: compare plants, lines, shifts and products | Group-level quality view |

## 2. Topology

```
 Line PC (per line)                       Plant server (one per plant)              Clients
 ┌─────────────────────────┐   gRPC/TLS   ┌──────────────────────────────┐  HTTPS   ┌──────────────┐
 │ Vision Studio / Line-   │─────────────▶│ Ingest  ─▶ PostgreSQL +      │◀────────▶│ Web dashboard│
 │ Runner + Station agent  │  events,     │            TimescaleDB       │ SignalR  │ (PC, TV)     │
 │ store-and-forward queue │  thumbnails  │ Live hub (SignalR)           │          ├──────────────┤
 │ PLC ◀──▶ (unchanged)    │◀─────────────│ Rules: OEE, SPC, alarms      │◀────────▶│ Mobile app   │
 └─────────────────────────┘  commands    │ Reports, NG review, AI health│  HTTPS + │ (Android,    │
                              (signed)    │ OPC UA / MQTT / REST out     │  push    │  iOS)        │
                                          └──────────────────────────────┘          └──────────────┘
                                                        │ outbound TLS only, the plant can turn it off
                                                        ▼
                                          Cloud relay (from PI2: remote access, phone push,
                                          later multi-plant roll-up)
```

Rules that do not bend:

1. **The line never depends on the plant server.** The station decides OK
   or NG and talks to the PLC as today. If the server is down, the station
   agent queues events on disk (7 days by default) and sends them later.
2. **Commands to a line are few, signed and checked twice:** change
   product, start, stop, acknowledge alarm, approve a tolerance or model.
   The server checks the user's role (and e-signature in Part 11 mode); the
   station checks the server's signature and its own state, and refuses a
   command that would change a running inspection mid-part. Nothing on the
   phone can make a bad part pass.
3. **The plant server is the master; the cloud relay only relays**
   (owner, 2026-10-29: cloud relay from PI2). Everything runs on the plant
   server and keeps working without internet. The relay carries phone
   push, remote access and the app's traffic over one outbound TLS
   connection from the plant server; no port is opened into the plant,
   and a plant on an isolated network turns it off and uses the app on
   the plant Wi-Fi.

## 3. Plant event schema (v1)

Every event carries `schema: 1`, `plant`, `line`, `station`, `camera`
(when it applies), `ts` (UTC, ms), `seq` (per station, gap-free, so the
server sees lost events), `queue_id` (the station's queue file; a new file
starts `seq` again at 1, so the server keys events by station, `queue_id`
and `seq`) and `job` + `job_version`.

| Kind | Extra fields | Sent |
| --- | --- | --- |
| `inspection.result` | part id or code, OK/NG, NG reasons (tool, reason key), measured values, scores, cycle ms, model versions, image id | every part (thumbnail for NG, every Nth OK) |
| `line.state` | running, stopped, starved, blocked, changeover, fault; reason code | on change |
| `counter.snapshot` | good, bad, total since shift start | every 10 s |
| `alarm.raised`, `alarm.cleared`, `alarm.acknowledged` | alarm code (requirements alarm catalogue), priority, who | on change |
| `job.changed` | from, to, who, e-signature id | on change |
| `model.deployed` | tool, model version, who, validation result | on change |
| `health.sample` | focus score, mean brightness, exposure, frame drops, CPU, GPU, RAM, disk, temperature | every 60 s |
| `heartbeat` | agent version, queue depth, events dropped | every 5 s, never stored |

Wire form (built in `dotnet/PlantEvents`): one JSON object per event,
snake_case names, `kind` and `schema` first; a reader does not depend on
the order. A reader refuses an unknown kind and any other schema number.
An NG result must carry at least one reason and an OK result none.
Required fields (`ok`, `state`, the counters) must be present; enums are
names, never numbers; every number is finite: a measurement that failed is
left out of `values` and its tool's NG reason says why.

Print grades (Read code tool, one part in N; requirements 17.3 PQ-01..05)
travel in `values` under the tool's name: `<tool>.grade` (worst overall
grade of the part's codes, 4 = A .. 0 = F), `<tool>.grade.<parameter>`,
`<tool>.symbol_contrast_pct`, `<tool>.modulation`,
`<tool>.grade.ungraded` and, when the station sends it, `<tool>.grade_min`
(the tool's lowest grade). The server keeps them per station and tool
(`vision_grade`), serves `GET /vision/grades/trend` (time buckets and the
drift status) for the dashboard and the mobile app, and raises the
`vision_grade_drift` alert before the lowest grade is reached. No schema
change.

Sequence and delivery (built in `dotnet/StationAgent`): the station agent
gives each stored event the next number of the station's sequence when it
writes it to its SQLite queue, in the same transaction, so numbers are
gap-free and survive restarts. The server answers each batch with the
highest number N such that N and every earlier event of the batch are
stored; only those events are marked sent, and an answer above the batch
is refused as a protocol error. Every call to the server has a time-out. A
heartbeat is not stored and carries the last number given out, so the
server knows how far behind it is. Unsent events are dropped only after the
agent itself has seen the server away for the whole retention time (7
days, on the monotonic clock, so a wrong or jumping PC clock never deletes
them); dropped events are counted in the heartbeat and the server sees them
as a gap. Storing an event takes only a short lock; old rows are pruned in
small pieces, so the line never waits behind the send loop.

The schema is a shared core: a change to it changes the station agent,
the server, the web dashboard and the mobile app in one pull request, with
a new `schema` number when a field changes meaning.

## 4. Who sees what

Scope is plant → area → line → station → camera. A user is given roles on
a scope (for example Operator on Line 3, Supervisor on the Packing area).

| Role | Web dashboard | Mobile app |
| --- | --- | --- |
| Operator | Own lines: live view, counters, NG strip, alarms | Own lines: status, alarms with guided fixes, acknowledge, call for help, downtime reason, change product (if the plant allows), golden-part check |
| Supervisor | Area: floor map, OEE, SPC, Pareto, reports | Area: all of the operator's view for every line, NG review, approve tolerance changes, shift report |
| Quality | Plant: SPC, traceability, audit, Part 11 | Traceability search, NG review, SPC alerts |
| Maintenance | Health of cameras, lights and PCs | Health alerts, scan a station's QR code to open it, log a fix |
| Plant admin | Users, roles, lines, integrations, licences, retention | Users and roles, pair a new line by QR code, approve model deploys, notification rules, audit |
| Group management | Multi-plant roll-up | Multi-plant summary |

## 5. Web dashboard screens

1. **Floor map** (home): a tile per line in plant layout order: state (✓ ✗ !
   plus colour), OEE, yield, parts per minute against target, top defect,
   open alarm with age. TV mode cycles areas full-screen.
2. **Line view**: live image per camera (thumbnail stream, last NG held),
   OK/NG, counters and yield, cycle-time bar against target, last 20 NG
   strip with plain reasons, state timeline for the shift.
3. **OEE**: availability × performance × quality per line, shift, product;
   loss tree (downtime reasons, slow cycles, rejects); trend by day.
4. **Quality (SPC)**: control charts for each measured value, Cp/Cpk,
   rule breaks marked; defect Pareto; defect map over the part image.
5. **NG review**: queue of NG images, filters by line and reason; confirm,
   overrule (false reject) or mark "unsure"; export to training.
6. **AI health**: per AI tool, score histogram now against training,
   drift index, overrule rate from NG review, "retrain suggested".
7. **Station health**: camera, light and PC charts; predicted failures.
8. **Alarms**: live list by priority, shelving, escalation history,
   alarm Pareto and mean time to acknowledge and to clear.
9. **Traceability**: search box (code, lot, time), result with image and
   versions, export.
10. **Reports**: shift, day, week; PDF and Excel; scheduled e-mail.
11. **Admin**: plant structure, users and roles, integrations, retention,
    licences, audit trail.

Same look and rules as the operator screens (requirements section 2):
pass/fail first, colour plus shape, light and dark themes, 100–200% text,
15 languages.

## 6. Mobile app

One Flutter app for Android and iOS, the screens set by the user's roles.

- **Home: My lines.** A card per line: state, yield, rate, open alarm.
  Tap for the line view. Admins and supervisors see every line in scope,
  grouped by area, with a search.
- **Line view.** Live thumbnail, OK/NG counters, NG strip, state for the
  shift, OEE today. Big buttons, at most 5 actions.
- **Alarms.** Push notification by priority and role; open it to see the
  picture, the plain-words cause and the guided fix (spoken in the user's
  language); acknowledge; "Call supervisor" escalates at once.
- **NG review.** Swipe right to confirm NG, left to overrule, up for
  unsure. Works one-handed on the floor.
- **Change product.** Pick by product photo; PIN or e-signature; the
  station confirms when the new job runs.
- **Downtime reason.** When a line stops, the operator picks the reason
  from pictures; it feeds OEE.
- **Golden-part check.** Guided start-of-shift check with the result
  signed and stored.
- **Scan.** The camera reads a station's QR code (open it, or pair it as
  admin) or a product code (traceability).
- **Admin.** Users and roles, notification rules, approvals waiting
  (tolerance, model, job), audit.

Offline: the app shows the last known state, clearly marked with its age.
Commands are never queued offline; they need a live connection.
Sign-in (owner, 2026-10-29: local accounts only for now): user name and
password held by the plant server, a PIN that unlocks shared floor
phones, and roles per scope as in section 4. Company SSO (OpenID
Connect) waits until a customer asks for it. Tokens in secure storage.
PIN on a shared floor phone (built 2026-10-31): each person signs in once
with user name and password and chooses a PIN of 4 to 8 digits (not one
digit repeated, not a straight run); the app keeps their session with a
salted PBKDF2-HMAC-SHA256 hash of the PIN, never the digits (PT11-07).
Only one person's sign-in is open on the phone at a time; it locks when
the app is hidden and after 2 minutes untouched, and the person, or the
next one, picks their name and types their PIN. While locked, calls go
with the server's key and no verdict can be given. The PIN never signs
in on its own (PT11-03): 5 wrong PINs in a row, or 15 minutes without use
(PT11-06), end that sign-in on the phone and on the server, and the
password is needed again. E-signatures still ask for the password
(PT11-31).
Plant server side (built 2026-11-03, PT11-08): a session ends after 15
minutes without a call (`serve.py --session-idle`, minutes, 0 for none)
and after 12 hours at most; an account that holds only Factory display
keeps only the 12 hours. `POST /auth/login` takes an optional `device`
(phone name and ID, for the audit trail) and answers `idle_sign_out_s`
(null: no idle limit). The phone reports each PIN event to
`POST /auth/pin-events` with that sign-in's own bearer token (never the
plant key) and `{"event": "pin_unlock" | "pin_wrong" | "pin_lockout",
"device": ...}`; the answer is `{"ok", "session": "open" | "ended",
"wrong_in_a_row"}`. `pin_unlock` counts as use; `pin_wrong` does not, so
wrong PINs never keep a session alive; the 5th `pin_wrong` in a row, or
`pin_lockout`, ends the session on the server. A 401 means the session
has already ended (idle, lockout or sign-out): the phone drops that
sign-in and asks for the password. The server measures idle time between
calls it receives, so it may end a session a little before the phone
does. A report may carry `"at"`, the phone's time when the PIN was typed
(ISO 8601 with `Z` or an offset; PT11-08b): it is kept, in UTC, as
`phone_at` in the audit entry next to the server's time of arrival. An
`at` more than 5 minutes ahead of the server, more than 5 minutes before
that sign-in began, or without a time zone is refused with 422 and not
recorded; a report without `at` is recorded as before.
Phone side (built 2026-11-03, PT11-08a): the phone sends `device` (system and
a random id, "Android a1b2c3d4") at sign-in and with each report; the 5th
wrong PIN is sent as `pin_lockout`. Reports that cannot be sent are
queued in the secure store, retried every 10 s and at start, and never
hold up the unlock; the lockout is sent before that sign-in is logged out. These events are audit entries (PART11-DESIGN 5: `sign-in`,
`sign-in.failed`, `lock`, `sign-out`, `unlock`, `unlock.failed`), which
Admin reads at `GET /admin/audit` and checks at `GET /admin/audit/check`;
an entry names the session by a short hash, never the token. The browser
dashboard drops a sign-in the server has ended and carries on without it.
Platforms (owner, 2026-10-29): Android and iOS together from PI2.
Push: FCM (Android) and APNs (iOS) through the cloud relay; on an
isolated plant network with the relay off, Android uses a local push
service on the plant server and iOS shows alarms while the app is open.

### 6.1 One app for every plant product

Owner, 2026-10-29: "Use light / dark colour mode, sound effects, charts and
graphs like secureme. In future I am going to add Production measurement
project also in same mobile app."

The app is one Flutter app (`mobile/plant_app`) built from a shared core
package (`mobile/plant_core`) and one module per product:

| Module | Server | State |
| --- | --- | --- |
| Vision (Plant Insight) | `dotnet/PlantServer` (`/api/v1`, `/hub/live`) | Built first |
| Production Measurement | the `production_measurement` backend (`/auth/login`, `/plants`, `/lines/{id}/status`, `/alerts`) | Later; the core is made ready for it |

- **Shared core:** theme, sounds, charts, strings in 15 languages, sign-in
  to each server, the server list, push registration and the app's own
  settings. A module only adds its screens and its API client.
- **Sign-in per server, one look:** a user adds a server (by QR code or
  address), signs in, and the module for that server's product appears.
  Plant Insight's local accounts follow Production Measurement's model
  (opaque, revocable session tokens; roles scoped to org, plant or line),
  so one sign-in screen and one token store serve both.
- **One line, both products:** when a line is known to both servers (a
  line id mapped once by an admin), the line card shows vision yield and
  NG next to production output and OEE.

### 6.2 Look, sound and charts (as in secureme)

- **Colour mode:** dark (default, the floor look), light, or the phone's
  own setting; switching fades instead of jumping. One set of colour roles
  (page, panel, ink, muted, brand, OK, warning, NG, two extra chart colours)
  defined for both modes; every text at least 4.5:1 and every border at
  least 3:1 on its panel in both. OK, warning and NG always also carry a
  shape (✓ ! ✗).
- **Sounds:** short synthesised clips (no licence to carry), none over
  0.35 s and none looping: `tick` (tap), `confirm`, `undo`, `scan`, and
  three alert levels — `notice` (one soft note), `attention` (two rising
  notes, double vibration), `critical` (three short pulses, three firm
  vibrations). On from install, a switch in Settings, and quiet whenever
  the phone is on silent or vibrate (ringtone volume on Android, ambient on
  iOS). An NG part does not make a sound on the phone; alarms do, by
  priority.
- **Charts (drawn by the app, no chart library):** yield ring, OEE gauge
  with availability, performance and quality arcs, trend line with limits
  (control chart), NG reason bars (Pareto), shift timeline (state bands),
  sparkline on each line card. Every chart has a text equivalent for
  screen readers, draws NaN or missing values as gaps, and animates only
  when the person has not asked the phone for reduced motion.

## 7. Targets

| Area | Target |
| --- | --- |
| Live latency | Result on the line to the dashboard and app in ≤ 1 s on the plant network |
| Scale | One plant server: 50 lines, 200 cameras, 20 parts/s per line |
| Line independence | No change in line cycle time or results with the server on, off or overloaded |
| Store-and-forward | 7 days of events on the line PC; no gap after the server returns (checked by `seq`) |
| Availability | Plant server 99.9%; restart and catch-up in ≤ 60 s |
| Retention | Raw results 90 days, NG images by quota, aggregates 5 years (configurable) |
| Mobile | Cold start ≤ 2 s, alarm push to phone ≤ 5 s, works on 5-year-old Android phones |

## 8. Technology

| Part | Choice | Why |
| --- | --- | --- |
| Station agent | .NET library inside Vision Studio and LineRunner, disk queue (SQLite) | Same process as the result; no extra install |
| Plant server | ASP.NET Core 8, gRPC in, SignalR and REST out | Same language as the station code |
| Data | PostgreSQL 16 + TimescaleDB | Time series and relational in one; free |
| Web dashboard | Blazor (grows from `dotnet/Dashboard`) | Reuses today's pages, services and sign-in |
| Mobile app | Flutter (Android and iOS), built in `mobile/plant_app` on the shared core `mobile/plant_core`; plain InheritedWidget and Navigator instead of Riverpod and go_router while the app is small (no extra packages; revisit when it grows); strings to move to ARB files in the 15 languages | One code base for Android and iOS; the owner's team already ships Flutter in secure_me |
| Push | FCM, APNs; ntfy-style local push for isolated plants | Works with and without internet |
| Integration | OPC UA (open62541 or .NET stack), MQTT Sparkplug B, webhooks | Plant standards |

## 9. Delivery

| Phase | Content | Exit gate |
| --- | --- | --- |
| PI1 | Event schema v1, station agent with store-and-forward, plant server ingest, floor map and line view on the web | Two real lines live on the floor map for 7 days with no lost event |
| PI2 | Mobile app on Android and iOS: sign-in, my lines, line view, alarms with push and guided fixes, NG review; cloud relay for push and remote access | Operators on two lines use it for a week; alarm to acknowledge time measured; app works through the relay from outside the plant |
| PI3 | OEE, SPC, Pareto, defect map, shift reports, traceability | Plant's own OEE sheet matches within 1% |
| PI4 | AI health, NG review to training, camera and light health with predictive alerts, alarm escalation | One false-reject reduction cycle shown on a customer line |
| PI5 | Part 11 mode, OPC UA and MQTT out, multi-plant roll-up over the relay, company SSO when asked | Pharma customer validation |

## 10. Owner decisions (2026-10-29)

1. **Hosting:** cloud relay from PI2 (for push, remote access and the
   app); the plant server stays the master and works offline.
2. **Mobile platforms:** Android and iOS together.
3. **Licence:** Plant Insight per connected line, web dashboard and
   mobile app included; the cloud relay as a yearly subscription.
4. **Sign-in:** local accounts (user name and password, PIN for shared
   phones) only for now; company SSO later, when a customer asks.

Still open: is the golden-part check (F13) on by default for every line?
