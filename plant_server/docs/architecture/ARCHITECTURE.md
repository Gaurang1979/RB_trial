# Architecture (v0.1, Milestone 1)

Status legend: **[done]** implemented + tested, **[next]** planned milestone, **[later]** backlog.
Baseline requirements: `docs/CLAUDE_MASTER_DEVELOPMENT_PROMPT.md`. Nothing is claimed to exist unless marked [done].

## 1. Data flow
```
Machine/sensor -> Edge gateway -> Ingestion -> Event engine -> DB -> KPI engine -> API -> Dashboard / Mobile
   [simulated]     [simulated]     [next M2]    [next M2]    [M2]     [M2]       [M2]     [M3/M4]
                                                                   \-> Alert engine -> Notification [M4]
```
Real-time path (spec s43): edge -> MQTT -> ingestion -> DB -> WebSocket -> dashboard. No polling for live data.

## 2. Key design rule: raw at the edge, meaning in the backend
The edge sends **raw facts** (signals, cycle pulses, counters, sensor readings, queue levels, heartbeats).
STARVED / BLOCKED / BYPASSED / IDLE / SENSOR_ERROR are **derived in the backend** from line relationships
(neighbour states, queue levels, counter agreement). Reason: derivation needs the whole line, rules must be
changeable without touching gateways, and results must be reproducible from stored raw events.
The simulator additionally writes a separate **truth log** (what really happened) so the derivation engine can be
scored against it in M2. Truth never travels in the event stream.

## 3. Technology choices (default; change only with a stated reason)
| Layer | Choice | Why / trade-off |
|---|---|---|
| Simulator + edge agent | Python 3.10+, stdlib only | Runs on Raspberry Pi/x86/ARM unchanged; no hardware lock-in |
| Transport | MQTT (Mosquitto) | Standard in plants, store-and-forward friendly. Adds a broker to operate |
| Backend | Python + FastAPI | Same language as edge, fast to build, typed. Not the fastest at very high event rates - fine for hundreds of machines |
| Database | PostgreSQL (+ TimescaleDB later) | One engine for master data and time-series. M2a uses SQLite behind the same SQL so it runs anywhere; Postgres adapter in M2b; Timescale only when volumes need it (plain Postgres handles pilot volumes) |
| Web | Single self-contained HTML/JS dashboard (M3); React later if UI complexity needs it | No build tooling or internet needed, so it works on plant networks. Harder to grow than a component framework |
| Mobile | PWA + push first, native later | Cheapest path to plant-manager alerts; native only if PWA push proves insufficient |
| Deploy | Docker Compose (single plant) | Simple pilot install. Kubernetes only for multi-plant scale |

## 4. Failure handling (spec s59) - status
| Situation | Behaviour | Status |
|---|---|---|
| Network down between gateway and backend | Buffer locally, flush in order, keep original `ts`, mark `buffered` | simulated + end-to-end result identical to no-outage run [done]; real edge buffer [M2b] |
| Duplicate message | Same `event_id` (`<gateway>:<seq>`); ingestion dedupes | simulated + ingestion tested [done] |
| Equipment unreachable | `comm_status ok=false` -> COMMUNICATION_ERROR interval; no fake production; counters reconcile on recovery | derivation + KPI tested [done]; alert [M4] |
| Stuck sensor | Reading frozen with quality still `good` (edge cannot know); backend must detect | simulated + tested [done]; detector [M4] |
| Invalid/skewed timestamp | Flag data | [later] (clock-skew scenario not yet simulated) |
| Database unavailable | Ingestion must not lose data | [M2b] |
| Silent gateway (network/gateway down) | Derivation never extrapolates past the gateway's last event: previous state carried 90 s, then `NO_DATA`; buffered data repairs history on arrival | tested, incl. regression for false bypass [done] |
| MQTT loss / backend down | QoS 1 + persistent session queues events at the broker; broker queue limit raised (`deploy/mosquitto.conf`); loss shows as sequence gaps | tested [done] |
| Invalid event | Quarantined with reason in `rejected_event`, never silently dropped | tested [done] |
| Sequence gap | Reported per gateway, closed by late arrival | tested [done] |

## 5. Simulator model limits (be aware)
- 1 s ticks: each cycle is rounded up to whole seconds (about +0.5 s per cycle on average).
- Truth states generated: PRODUCING, STARVED, BLOCKED, FAULT, BYPASSED. Not yet: OFF, IDLE, WAITING, MAINTENANCE, quality-related stops.
- Faults are random (MTBF/MTTR, tail capped at 4x MTTR) or scripted scenarios. Not calibrated to any real plant.
- All demo data is synthetic. **No accuracy claim is valid until validated against plant data** (spec s51).

## 6. Roadmap
| Milestone | Scope | Exit test |
|---|---|---|
| **M1 [done]** | Docs, config schema, event model, simulator, tests | 23 tests |
| **M2a [done]** | Ingestion (validate, dedupe, quarantine, gap detection), SQLite store, state derivation, KPI (counts, target vs actual, downtime, OEE, bottleneck), scoring vs truth, pipeline CLI | 54 tests; accuracy vs truth log; outage/duplicate/out-of-order end-to-end |
| **M2b [done]** | MQTT sink + subscriber (durable session), PostgreSQL store, live (incremental) derivation, gateway data horizon | Whole suite passes on SQLite and PostgreSQL; live result equals batch; end-to-end MQTT -> PostgreSQL -> API identical to batch CLI |
| **M3 [done]** | FastAPI (REST + WebSocket, optional API key), live dashboard (line flow, queues, KPIs, attention list), demo server | 75 tests; DOM smoke test in jsdom. **Not yet checked visually in a real browser** |
| **M4 [done]** | Sensor-health detectors, alert engine (rules, lifecycle, escalation, audit), in-app + webhook channels, alerts API, dashboard alerts with acknowledge, installable PWA shell | Alerts fire on the demo factory's scripted incidents; 106 tests on SQLite and PostgreSQL |
| **M4b [done]** | User accounts + RBAC (org/plant/line scope), background Web Push (VAPID/aes128gcm), per-user recipients, dashboard sign-in and push opt-in | 148 tests on SQLite and PostgreSQL; real end-to-end run (real server, real login, real encrypted push, independently decrypted) |
| **M5 [done]** | Real-hardware edge agent: Modbus TCP and OPC-UA adapters (verified against real local servers), Raspberry Pi GPIO adapter (untested - needs real hardware), store-and-forward with disk persistence, counter-to-cycle-event translation | Real Modbus registers -> real edge agent -> real MQTT broker -> real backend ingestion -> correct derived states, end to end |
| Later | Analytics/AI, AMC/SLA workflow, admin UI for user management, reports, shifts/planned stops, password reset, background Web Push on a real device | Per spec s39 |

## 7. Decisions log
| # | Decision | Date |
|---|---|---|
| D1 | Stack defaults per section 3 (user did not override) | 2026-09-21 |
| D2 | First goal: sales demo on simulated factory, then real-plant pilot | 2026-09-21 |
| D3 | Derived states computed in backend; edge sends raw signals only | 2026-09-21 |
| D4 | M2a stores in SQLite (stdlib, testable anywhere); PostgreSQL adapter in M2b; TimescaleDB deferred until volume needs it. Reason: cannot run Postgres/MQTT daemons in the build sandbox, and pilot volumes are small | 2026-09-21 |
| D5 | KPI definitions as in DERIVATION.md (24x7 planned time; starved/blocked counted as availability loss with a separate `availability_own`) | 2026-09-21 |
| D7 | Dashboard is one self-contained HTML file (no build step, no CDN) served by the API; React deferred | 2026-09-21 |
| D8 | Silence is not evidence: per-gateway data horizon, `NO_DATA` state after 90 s of gateway silence | 2026-09-21 |
| D9 | Alerts open on a threshold but stay open while the underlying state persists, resolve after `clear_after_s`, and are held (not cleared) while the machine cannot be seen | 2026-09-21 |
| D11 | Sessions are opaque server-side tokens (revocable), not JWT; PBKDF2 password hashing (stdlib, no native dependency) | 2026-09-21 |
| D12 | RBAC scope is org/plant/line (no area level); `system_administrator` bypasses scope entirely; the shared API key remains a full-access "service" credential, unchanged, for backward compatibility | 2026-09-21 |
| D14 | Edge agent config is a separate file from the site topology (different concerns: physical wiring vs. what exists and its properties); IDs are kept in sync by the plant survey process, not auto-generated | 2026-09-21 |
| D15 | Counter-based cycle-event inference (no dedicated pulse input) trades precise per-cycle timing for simplicity; documented as an average, not a measurement | 2026-09-21 |
| D13 | Web Push proven correct via a local push-service stand-in and independent decryption (see AUTH.md); real-device delivery needs an HTTPS deployment to confirm | 2026-09-21 |
| D10 | M4 ships in-app + webhook notifications; background Web Push and accounts come next (need HTTPS, VAPID keys, real devices to test) | 2026-09-21 |
| D6 | Proposed accuracy gate vs simulator truth: >= 99% overall, >= 95% per-state recall (BYPASSED >= 90%). Awaiting user confirmation | 2026-09-21 |
