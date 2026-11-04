# Plant platform: one server, one database, every product

2026-10-29 · design · owner decisions of 2026-10-29:

- "Plant level single server will be installed for all kind of systems. Keep
  common database."
- "In future I will also sell TIJ printer EuroCod. I will share integration
  protocols also. Multiple systems and single application."
- The one server is **Python**: the `production_measurement` backend (now
  `plant_server/` in this repository) grows into it (owner's choice over a shared core with separate services, or one
  .NET server).

This document says how. It changes [PLANT-DASHBOARD-AND-MOBILE.md](PLANT-DASHBOARD-AND-MOBILE.md)
(Plant Insight) where they differ: there is no separate Plant Insight server
any more; Plant Insight is the Vision module of the plant server.

## 1. The picture

```
 Line PCs and machines                      One plant server (Python, FastAPI)                 People
 ┌──────────────────────────┐               ┌─────────────────────────────────────────┐       ┌──────────────┐
 │ Vision station (.NET):    │  HTTPS JSON   │ core: plants, areas, lines, devices,     │ HTTPS │ One web      │
 │  Vision Studio, LineRunner│──────────────▶│  users and roles, sessions, alarms and   │ WS    │ dashboard    │
 │  + station agent          │               │  alerts, audit, push, settings, licences │◀─────▶│ (all modules)│
 ├──────────────────────────┤               │ module: production (OEE, states, KPIs)   │       ├──────────────┤
 │ Production edge (Python): │  MQTT         │ module: vision (ingest, live, NG review) │       │ One mobile   │
 │  Modbus / OPC UA / GPIO   │──────────────▶│ module: printing (EuroCod TIJ, later)    │       │ app (Android,│
 ├──────────────────────────┤               │ one PostgreSQL database (+ TimescaleDB)  │       │ iOS)         │
 │ EuroCod TIJ printers      │  protocol     └─────────────────────────────────────────┘       └──────────────┘
 │  (later; owner shares it) │──────────────▶ printing adapter          │ outbound TLS, optional
 └──────────────────────────┘                                          ▼
                                                              Cloud relay (push, remote access)
```

Rules that do not bend (carried over from Plant Insight):

1. **No line depends on the server.** Each line PC, edge gateway and printer
   keeps working, and keeps its own queue, while the server is away.
2. **One sign-in, one set of roles** for every module: a user's roles are
   scoped to org, plant or line, as Production Measurement already does.
3. **One install per plant**, working without internet; the cloud relay is
   an outbound-only add-on.

## 2. One database

One PostgreSQL database per plant. Shared tables live in the core; each
module owns its own tables and reads the core ones, never another module's.

| Part | Tables | From |
| --- | --- | --- |
| Core: where | `plant`, `area`, `line`, `device` (kind, maker, model, serial, line, token hash, enabled) | Today the topology comes from the site JSON; it moves into tables, and the JSON becomes an import |
| Core: who | `spi_user`, `role_assignment`, `user_session`, `push_subscription` | Production Measurement, as they are |
| Core: what happened | `raw_event` (every device's events, deduplicated by device and sequence), `rejected_event`, `sequence_gap`, `alert`, `alert_event`, `notification`, `audit_log` | Production Measurement; `gateway_id` is the device id for every kind |
| Production | `equipment_state_interval`, `downtime_event`, KPIs | Production Measurement, as they are |
| Vision | Built: `vision_event` (every stored event of a station as it came, with `vision_queue`, `vision_gap`, `vision_reject`, `vision_conflict`), `vision_station_status` (last heartbeat), `vision_latest` (last line state and job, open alarms: the start-up replay). Later, with NG review: `vision_result` (one row per inspected part) and `ng_review` | Ported from the .NET plant server of PR #49 |
| Printing | `print_job`, `printer_status` (ink, cartridge, faults, counts) | When EuroCod's protocol arrives |

Device kinds: `vision_station`, `production_gateway`, `tij_printer`. A device
gets a token when an admin adds it (the server stores only its SHA-256); the
token says which device it is, so a device never writes for another.

## 3. Modules

**Production** — today's Production Measurement: MQTT ingest, state
derivation, OEE and KPIs, sensor health, alerts. Unchanged, now reading the
topology from the core tables.

**Vision** — the station agent's contract stays exactly as built in
`dotnet/StationAgent` (HTTPS JSON batches of plant events v1, station token,
`stored_up_to` answer). Built 2026-10-29 in `plant_server/backend/vision`
(`POST /vision/ingest/v1/events` and `/heartbeat` with a device token;
`GET /vision/floor` and `/vision/lines/{plant}/{line}` scoped by role, and
`WS /vision/ws` pushing the same floor when it changes, Req 17.3 LV-01..04): the
.NET plant server of #49 ported to Python with the same rules and tests:
duplicates, gaps, rejects without blocking the queue, conflicts answered 409,
partial batches, live line state, replay at start. Vision alarms are
core alerts (built 2026-10-29, `backend/vision/alerts.py`), so escalation,
push and acknowledge work the same for every module; a station that sends
no heartbeat for 60 s is an alert too. An alarm acknowledged at the station
acknowledges its alert; the server never acknowledges on the station.

**Printing (EuroCod TIJ, later)** — an adapter per printer that speaks the
maker's protocol (to be shared by the owner) and turns it into core events:
job loaded, message printed (count), ink level, cartridge changed, fault.
The feature that sets the platform apart: **print and verify in one loop**.
The batch values (date, lot, MRP …) are entered once; the server sends them
to the printer and to the vision job's batch fields together, and the Check
text tool verifies every print against exactly what was sent. A mismatch is
an NG with the reason "printed text differs from the batch".

## 4. One application

- **Web dashboard:** the plant server's dashboard gains a module per product
  (floor map, line view, quality, NG review for Vision; the existing
  production views; printer status later), with one menu and one sign-in.
- **Mobile app:** the Flutter app (`mobile/plant_app`) connects to **one
  server per plant** and shows the modules that plant has. The shared core
  (theme light and dark, sounds, charts) stays; the "server per product"
  list becomes "plant server", with modules enabled by licence.
- **Line PC:** Vision Studio and LineRunner stay .NET on the line PC; their
  station agent sends to the plant server. The line PC's own screen is the
  operator's first screen and works alone.

## 5. Steps

| Step | Content | Exit |
| --- | --- | --- |
| PL1 | Core tables for plants, areas, lines and devices in the Python server, with device tokens; the site JSON imported into them | Production Measurement's tests pass unchanged on the new topology |
| PL2 | Vision module: ingest and live line state ported from #49 with its tests; vision alarms as core alerts | The .NET station agent delivers to the Python server end to end; #49 closed |
| PL3 | One dashboard: vision floor map and line view beside the production views | One sign-in shows both on a plant with both |
| PL4 | Mobile app on one plant server, modules by licence; push for every module | Operators get vision and production alarms in one app |
| PL5 | Printing module for EuroCod TIJ, with print and verify | One printer and one vision station on a line, closed loop |

## 6. Open questions for the owner

1. ~~Where the plant server's code lives~~ — decided 2026-10-29: the whole
   `production_measurement` repository is merged into Vision_System, with its
   history, as `plant_server/`; one project.
2. ~~Where the mobile app lives~~ — in the same single project (`mobile/`).
3. EuroCod TIJ: printer models first, and the protocol documents.
4. Licence per module and per line (Vision per line is decided; Production
   and Printing to be priced).
