# production_measurement - Sundaram Smart Production Intelligence

Know what is happening on every production line, understand why production is being lost, and act early.
Baseline spec: [`docs/CLAUDE_MASTER_DEVELOPMENT_PROMPT.md`](docs/CLAUDE_MASTER_DEVELOPMENT_PROMPT.md).
History and status: [`PROJECT_HISTORY.md`](PROJECT_HISTORY.md).

## Status
Done: M1 simulator, M2a backend core, **M2b** (MQTT, PostgreSQL, live derivation), **M3** (API + live dashboard), **M4** (sensor health, alert engine, notifications), **M4b** (accounts + RBAC, Web Push) and **M5** (real-hardware edge agent: Modbus TCP, OPC-UA, Raspberry Pi GPIO).
Not started: admin UI, password reset, AMC/SLA workflow, background Web Push confirmed on a real device.
Not yet done: dashboard has not been checked visually in a real browser (DOM-tested only); Docker files are untested.

## Run the simulator (Python 3.12+, no dependencies)
```
python -m simulator --config configs/demo_factory.json --duration 7200 --out out/events.jsonl
```
Writes `out/events.jsonl` (what the edge gateway would send) and `out/events.jsonl.truth.jsonl` (what really happened; for validation only).
Add `--speed 60` for a live feed (1 simulated minute per second). A production summary prints to stderr.

## Live demo (one command, then open the printed URL)
```
pip install --require-hashes -r requirements.lock
python -m backend.serve --config configs/demo_factory.json --demo --speed 120
```
Runs the synthetic factory in-process; scripted events (fault, starvation, bypass, sensor stuck, comm loss, network outage) occur in the first 2 simulated hours.

## Full stack (simulator -> MQTT broker -> backend -> PostgreSQL -> dashboard)
```
mosquitto -c deploy/mosquitto.conf                     # or docker compose up (untested; see "Deploy at a site")
python -m backend.serve --config configs/demo_factory.json --db postgresql://user:pw@localhost/spi --mqtt localhost --mqtt-no-token
python -m simulator --config configs/demo_factory.json --duration 7200 --mqtt localhost
```
`--mqtt-no-token` is for this local demo only: without it the backend stores only messages that carry an active production gateway's device token (issue one per gateway with `POST /admin/devices/{id}/token`, then pass them to the simulator with `--mqtt-tokens tokens.json`, or to a real gateway with `token_file`; see "Gateway token" in `docs/architecture/EDGE.md`).
Set `SPI_API_KEY` to require an API key (service credential; see `docs/architecture/AUTH.md` for user accounts/RBAC). Alerts: rules come from `alert_rules` in the site JSON (defaults built in, see `docs/architecture/ALERTS.md`); set `SPI_ALERT_WEBHOOK` for a webhook channel, and `--vapid-key <path>` to enable real Web Push. Create the first account (becomes admin) with `POST /admin/users {username, name, password}`. Use a fresh database for demo data (event ids repeat between runs).

## Deploy at a site
Every site installs the tested set: `requirements.lock` holds the exact version and hashes of every package (Python 3.14, Linux, Windows and macOS), so pip refuses anything else.
```
python3.14 -m venv .venv && . .venv/bin/activate        # Windows: .venv\Scripts\activate
pip install --require-hashes -r requirements.lock
```
`requirements.txt` is the list of what the server needs, with version ranges; after changing it, make the lock again with `deploy/make-lock.sh` (needs `uv`) and run the tests on an install from the new lock.
The Docker image (`deploy/Dockerfile`) installs from the lock too.

NG images from the stations are stored as files under `VISION_IMAGE_DIR` (default `./vision_images`), up to `VISION_IMAGE_LIMIT_BYTES` (default 2 GB; the oldest go first). Put it on a disk that is backed up and outlives the server: a native install sets `VISION_IMAGE_DIR` to such a folder, and `docker-compose.yml` mounts the volume `visionimages` at `/data/vision_images` in the backend, so the images survive a rebuilt or replaced container (`docker compose down -v` deletes them).

Production gateways send their data over MQTT with a device token each: issue one per gateway (`POST /admin/devices/{id}/token`), put it in the gateway's `token_file`, run the server without `--mqtt-no-token`, and lock the broker down (password file, ACL as in `deploy/mosquitto-acl.example`, TLS). At start the server lists gateways that still have no token. See "Gateway token" in `docs/architecture/EDGE.md`.

## Run the real-hardware edge agent
```
python -m edge --config configs/edge_example.json --buffer-dir ./buffer
```
Polls real equipment (Modbus TCP / OPC-UA today; Raspberry Pi GPIO code exists but is unverified without real hardware - see `docs/architecture/EDGE.md`) and publishes the same events the simulator does, so the rest of the stack (ingestion, derivation, dashboard, alerts) needs no changes to work with a real pilot line instead of the demo.

## Run the backend pipeline (ingest -> derive -> KPIs -> accuracy vs truth)
```
python -m backend --config configs/demo_factory.json --events out/events.jsonl \
  --truth out/events.jsonl.truth.jsonl --db out/spi.db
```
Prints a JSON report: per line good output vs target, bottleneck candidate, per machine availability/performance/quality/OEE, downtime events, ingestion stats (duplicates, rejected, sequence gaps) and simulator-vs-truth accuracy. Definitions: `docs/architecture/DERIVATION.md`.

## Customer presentation
Sources, graphics and generators are in `docs/presentation/` (see its README). Screens use synthetic demo data.

## Test
```
pip install --require-hashes -r requirements.lock && python -m pytest -q
```
Optional: `SPI_TEST_PG_DSN=postgresql://user:pw@localhost/db` runs the whole suite on PostgreSQL; `SPI_TEST_MQTT=host:1883` points the MQTT tests at a broker
(skipped if none); `cd tests/ui && npm install` enables the dashboard DOM test.

## Layout
`simulator/` edge + factory simulator - `backend/` ingest, store (SQLite/PostgreSQL), derive, live, kpi, insight, api, auth, alerts, sensor_health, webpush, mqtt_ingest, serve, scoring - `edge/` real-hardware agent (Modbus/OPC-UA/GPIO adapters, config, gateway) - `dashboard/` live UI - `deploy/` broker config, Dockerfile - `spi_common/` event contract - `configs/` demo factory - `tests/` - `docs/architecture/` design docs - `docs/api/` API draft
