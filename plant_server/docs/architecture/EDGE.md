# Real-hardware edge agent (M5; `edge/`)

Polls real equipment over a real industrial protocol and publishes the exact `spi.event.v1` events the
simulator produces, so everything downstream - ingestion, derivation, KPIs, alerts - is unchanged between a
demo and a real pilot line. Run it with:

```
python -m edge --config configs/edge_example.json --buffer-dir ./buffer --client-id GW-VAD-01
```

`--buffer-dir` persists each line's store-and-forward buffer to disk so an outage survives a process
restart, not just an in-process reconnect. `--client-id` gives the MQTT connection a persistent session, the
same durability mechanism already used by the backend side (`backend/mqtt_ingest.py`).

## Gateway token
The plant server stores a production gateway's MQTT messages only when they carry that gateway's device
token, so a device on the plant network cannot write production data for a line it is not. Set-up, once per
gateway (one gateway per line, `gateway_id` in the edge config):

1. The gateway is a `production_gateway` device in the plant server's registry. Gateways named in the site
   JSON (`gateway_id` of each line) are added at server start; others are added by an administrator with
   `POST /admin/devices` `{"id": "GW-VAD-01", "kind": "production_gateway", "name": ..., "plant_id": ..., "line_id": ...}`.
2. An administrator issues its token: `POST /admin/devices/GW-VAD-01/token` (or the token returned by
   `POST /admin/devices`). The token is shown once; the server keeps only its SHA-256.
3. Put the token, alone, in a file on the gateway that only the agent's user can read
   (`chmod 600`, e.g. `/etc/spi/GW-VAD-01.token`), and name it in that line's `token_file` in the edge
   config. Never put the token itself in the config or in the repository.
4. Start the agent. It sends the token on every message as the MQTT 5 user property `spi-token` (the event
   itself does not change). A line without `token_file` is warned about at start; the server refuses its
   messages.

The server refuses, logs (gateway id, topic and reason; never the token or the event) and does not store any
message whose token is missing, unknown, replaced or belongs to a disabled device or one that is not a
production gateway, or whose event names another gateway, plant or line than the token's device. Refused
messages are not quarantined as rejected events either: nothing from an unknown sender is kept.
**Revoking:** `POST /admin/devices/{id}/disable` stops the token at once (within one ingest batch);
`POST /admin/devices/{id}/token` replaces it (the old one stops at once). A refused gateway's events stay in
its store-and-forward buffer only until the broker accepts them, so after fixing a token, check the
line's sequence gaps.

**The broker must be locked down before the token means anything.** MQTT 5 passes user properties to every
subscriber, so on an open broker (`allow_anonymous true`, as in `deploy/mosquitto.conf` for local evaluation)
anyone who subscribes to `spi/v1/#` sees every gateway's token, and port 1883 sends it in clear text. On a real
line: a password file, an ACL that lets only the plant server's user read `spi/v1/#` and each gateway write
only its own topic (`deploy/mosquitto-acl.example`), and a TLS listener. A production gateway in the
registry needs its plant and line; the server also refuses any event naming equipment, a sensor or a queue
that the site JSON gives to another gateway.

The broker and every client speak MQTT 5 (Mosquitto 2 does). `python -m backend.serve --mqtt-no-token` turns the
check off for the local docker-compose demo only, whose simulator has no tokens; the simulator can also send
tokens with `--mqtt-tokens tokens.json` (`{"GW-VAD-01": "<token>", ...}`).

## Two separate configs, kept in sync by ID
- **Site topology** (`configs/*.json`, existing format) - what equipment exists, its standard cycle time,
  queue capacities, sensor valid ranges. Used by the backend for derivation/KPIs/sensor-health.
- **Edge config** (`edge/config.py`, new) - how to physically read that same equipment from real hardware:
  protocol, connection details, register/tag addresses.

The equipment, sensor and queue IDs must match between the two files; this is produced during the plant
survey described in the presentation's pilot roadmap. The two are never auto-generated from each other.

## Protocol adapters (`edge/adapters/`)
| Protocol | Library | Verified here |
|---|---|---|
| `modbus_tcp` | pymodbus | Real TCP server, real socket, live register changes (`tests/test_edge_modbus.py`) |
| `opcua` | asyncua | Real server, real socket, browse-path resolution, live tag changes (`tests/test_edge_opcua.py`) |
| `gpio` | gpiozero | **Not testable here** - needs a real Raspberry Pi GPIO device. Import-guarded so the rest of the package still loads without it; using it off a Pi raises a clear error rather than failing silently. Confirm against real pins before relying on it |
| `mock` | - | In-memory, for testing agent logic in isolation (`tests/test_edge_agent.py`) |

A point's `address` schema differs per protocol (Modbus: `{type, addr, words, encoding}`; OPC-UA: `{node_id}`
or `{browse_path}`; GPIO: `{pin, pull_up}`) - see `edge/config.py` and `configs/edge_example.json`.

## Point kinds -> event translation
| Kind | Becomes | Notes |
|---|---|---|
| `digital` | `signal` events on change | Plus a full resync at start and after a comm recovery, same contract as the simulator |
| `counter` (`role: total\|reject`) | `counter` snapshots + `cycle` events | The PLC's own register is the source of truth - nothing is accumulated locally, so a restart never loses counts. An increase since the last poll becomes that many `cycle` events, each stamped at the poll time with `cycle_time_s` = elapsed / increase - an **average**, not each part's real time (see limits below). A register that goes backwards is treated as a reset: re-baselined, nothing fabricated |
| `analog` | `sensor` events on their own `sample_interval_s` | `value = raw * scale + offset` |
| `queue_after` | `queue` events | Level + capacity, on its own sample interval |

Repeated read failures for an equipment (`comm_fail_threshold` consecutive, default 3) raise `comm_status
ok=false` and that equipment goes silent - no fabricated signals or counts - exactly how the simulator
represents `device_comm_loss`, so the backend's existing derivation handles a real dead link the same way it
already handles a simulated one. A single point failing (e.g. one bad sensor wire) does not mark the whole
equipment unreachable; only that point goes silent, which the backend's sensor-health OFFLINE detector (M4)
already covers.

## Known limits
- **Cycle timing without a pulse input is an average, not a measurement.** A plain counter register read once
  a poll cannot recover each part's individual cycle time - only the count and an average over the interval.
  This is most visible right after an outage: a burst of `cycle` events spread over the *whole* outage gets a
  correspondingly coarse average. The event *count* (and the cumulative totals each event carries) is still
  exact; only the per-event timing estimate is coarse. A dedicated cycle-pulse digital input, read via a
  `digital` point mapped to a custom signal name, would give real-time-resolution timestamps instead - not
  yet wired as its own point kind.
- **Counter rollover** (a register wrapping past its max value) is treated the same as a reset: re-baselined,
  no negative or fabricated cycle count. The parts made right at the rollover are not counted.
- **The full resync after a comm recovery lands one poll later than the simulator's equivalent** (which can
  decide synchronously since it scripts its own outages). Real digital *changes* during the outage are still
  correctly detected and emitted on the recovery poll itself; only the redundant re-announcement of anything
  that happened to stay unchanged is delayed by one poll. Not a correctness gap, just a minor timing nuance.
- **One polling thread per equipment.** Fine at pilot-line scale (tens of machines); would need a different
  design (e.g. batched reads, async I/O) at much larger scale.
- Modbus/OPC-UA server or read errors never fabricate a value - a point simply goes silent, exactly like a
  simulated `device_comm_loss` or `sensor_offline` scenario.

## Verified for real, end to end, in this sandbox
Beyond the per-adapter protocol tests: a real Modbus TCP server standing in for a press, a real edge agent
polling it over a real socket, publishing to the real local Mosquitto broker, ingested by the real backend
(the same code path tested in M2b/M3), and correctly derived into FAULT/IDLE states - with real register
increments turning into the matching number of real `cycle` events. Not verified here, for lack of one: a
real PLC/Raspberry Pi/GPIO device - confirm the GPIO adapter and any protocol quirks of a specific PLC vendor
against real hardware during the plant survey before a pilot goes live.
