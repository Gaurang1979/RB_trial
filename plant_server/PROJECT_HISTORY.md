# Project history (read this first in every new chat)

> **2026-10-29: merged into Vision_System** (owner: "Merge full repo production measurement into
> vision system. I will delete production measurement repo after that. Make single project.").
> This file is kept as the history up to the merge. New entries go into the Vision_System process:
> a note in `docs/changes/` per pull request, rolled up into `docs/PROJECT-HISTORY.md` (see `CLAUDE.md`).

**Product:** Sundaram Smart Production Intelligence (Sundaram Technologies). Repo: github.com/Gaurang1979/production_measurement
**Source of truth:** `docs/CLAUDE_MASTER_DEVELOPMENT_PROMPT.md`. Design: `docs/architecture/ARCHITECTURE.md`.

## Decisions
- Stack: Python (edge + FastAPI), MQTT (Mosquitto), PostgreSQL (Timescale later), single-file HTML dashboard (React later), PWA push first, Docker Compose.
- First goal: sales demo on a simulated factory, then real-plant pilot (Raspberry Pi + sensors).
- Edge sends raw signals; backend derives STARVED/BLOCKED/BYPASSED/etc.
- M2a storage is SQLite; PostgreSQL in M2b; TimescaleDB deferred (D4). KPI definitions in DERIVATION.md (D5).
- Proposed accuracy gate vs simulator truth: >= 99% overall, >= 95% per-state recall (D6) - awaiting user confirmation.

## Log
| Date | Milestone | Result |
|---|---|---|
| 2026-09-21 | Repo reviewed | Only README + master spec existed; no code |
| 2026-09-21 | M1 | Docs + simulator (10 machines, 3 lines, 2 plants) + 23 passing tests, mutation-checked; pushed to GitHub |
| 2026-09-21 | M2b + M3 | MQTT (durable QoS1), PostgreSQL store, live derivation, REST/WebSocket API, live dashboard. 75 tests on SQLite and PostgreSQL; MQTT->PostgreSQL->API identical to batch. Found+fixed: broker default queue limit dropped events (config + gap detection caught it); network outage was misread as bypass (data horizon / NO_DATA fix + regression test) |
| 2026-09-21 | M2a | Backend core: ingestion, derivation, KPIs, scoring, CLI. 54 tests pass; derived states match simulator truth at 99.9-100% (clean synthetic data, not plant accuracy) |

## Daily log (update every working day; newest first)
| Date | What happened |
|---|---|
| 2026-09-21 (M5) | Real-hardware edge agent built: Modbus TCP and OPC-UA adapters (each verified against a real local server over a real socket, including live value changes), a Raspberry Pi GPIO adapter (code only - not testable here, needs real hardware), store-and-forward with optional disk persistence, and counter/digital/analog -> spi.event.v1 translation. Ran the full real chain end to end: a real Modbus server standing in for a press -> real edge agent -> real Mosquitto broker -> real backend ingestion -> correct derived states (FAULT/IDLE) with real counter increments becoming the matching number of real cycle events. Four real bugs found and fixed along the way, each with a regression test: a dict-vs-dataclass mismatch across every adapter, a wrongly-required field on queue-level points, a genuine thread-safety gap in the disk-persisted buffer (reproduced as a real crash under load), and an init-order race where the MQTT connect callback could fire before the agent's gateway dict existed. 188 tests pass on SQLite and PostgreSQL |
| 2026-09-21 (M4b) | User accounts + RBAC (org/plant/line scope, roles matching the presentation's roles slide) and background Web Push (VAPID/aes128gcm) built, tested and verified live: real server, real bootstrap admin, real login, a real alert from the demo simulator delivered as a real encrypted push to a stand-in device and independently decrypted. Dashboard gained sign-in and a real push-subscribe flow. Found and fixed a real bug in the *test harness*: PostgreSQL test connections were only closed at the very end of the session, so a suite this size (148 tests) exhausted the server's connection limit around test 100 - now closed per test. 148 tests pass on SQLite and PostgreSQL |
| 2026-09-21 (later) | Deck layout fixed (cover overlap, questions overflow, narrow hierarchy boxes, long titles, double border) and republished. **M4 built**: sensor-health detectors (stuck/impossible/offline; gateway silence is not a sensor fault), alert engine (rules, open/ack/resolve, escalation, audit trail, hold while blind), in-app + webhook channels, alerts API, dashboard alerts with acknowledge, PWA shell. Two more bugs found by running the demo and fixed with regression tests (stuck-sensor verdict flapping; alert cleared while machine unseen). Web Push and accounts are next (M4b). |
| 2026-09-21 | M1, M2a, M2b, M3 built, tested and pushed (75 tests, SQLite + PostgreSQL, MQTT). Two real bugs found and fixed (broker queue limit dropped events; network outage misread as bypass). Customer presentation built (25 slides, 13 graphics from real product data), sources in `docs/presentation/`. Deck: https://claude.ai/artifact/QKVbBWBbbWGbohg1aayJtE |

Working rule until 2026-10-29 (asked by the owner): at the end of every working session/day, update this file and push all generated content to GitHub. Since the merge, work follows Vision_System's CLAUDE.md (pull requests, change notes).

## Next
Real-device Web Push delivery and real-PLC/real-Pi confirmation (both need actual hardware/deployment - the protocols themselves are verified). Admin UI for user management. Presentation: add logo and contacts, review in the viewer. Also: user accounts/RBAC, visual check of dashboard in a real browser, Docker validation, real edge agent (Raspberry Pi).

## Open items
- Real machine/PLC/protocol details for the pilot plant (needed before edge agent work).
- Target customers' shift patterns and downtime categories (to seed config).
- Simulator not calibrated to any real plant.
