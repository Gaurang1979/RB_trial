# Event model v1 (`spi.event.v1`)

One JSON object per event. Implemented in `simulator/gateway.py`; validated by `simulator/schema.py`.

## Envelope
| Field | Meaning |
|---|---|
| `schema` | `spi.event.v1` |
| `event_id` | `<gateway_id>:<seq>` - dedupe key |
| `seq` | Per-gateway counter assigned at creation, starts at 1, never reused |
| `gateway_id` | Source gateway (one per line in the demo) |
| `type` | `signal`, `cycle`, `counter`, `sensor`, `queue`, `heartbeat`, `comm_status` |
| `ts` | When it happened (UTC, second resolution). Never changed |
| `emitted_ts` | When it left the gateway. Equals `ts` unless buffered |
| `buffered` | `true` if held during a network outage and flushed later |
| `plant_id`, `area_id`, `line_id` | Hierarchy (spec s7) |
| `equipment_id` | Set for equipment events; `null` for `queue` and `heartbeat` |
| `payload` | Type-specific, below |

## Payloads
| type | payload | Emitted |
|---|---|---|
| `signal` | `name` (`machine_on`, `running`, `fault`), `value` (0/1) | On change, plus full snapshot at start and after comm recovery |
| `cycle` | `cycle_time_s`, `result` (`good`/`reject`), `counters{total,good,reject}` | Each completed cycle |
| `counter` | `total`, `good`, `reject` (cumulative) | Snapshot at start and after comm recovery |
| `sensor` | `sensor_id`, `name`, `value`, `unit`, `quality` | Every `sample_interval_s`. `quality` is always `good` from the simulator |
| `queue` | `queue_id`, `level`, `capacity` | Every `queue_sample_interval_s` |
| `heartbeat` | `uptime_s`, `buffered_events` | Every `heartbeat_interval_s` |
| `comm_status` | `ok` | On equipment comm loss / recovery |

## Two producers, one contract
The simulator (`simulator/`) and the real edge agent (`edge/`, see `docs/architecture/EDGE.md`) both emit
exactly this event schema, so the backend cannot tell - and does not need to tell - whether it is looking at
synthetic or real data. That is the whole point of the contract.

## Rules ingestion must follow
1. **Dedupe** on `event_id`. 2. **Order** by `(gateway_id, seq)` for delivery, by `ts` for analysis.
3. `seq` gaps mean lost events. Gaps never occur from outages (buffer keeps `seq`), only from real loss.
4. `running=1` with no `cycle` events for X seconds is a *possible bypass* (neutral wording: "signal inconsistency").
5. Counters are cumulative: after a comm gap, the counter jump reconciles production that was not observed.
6. Never create production from missing data.

## Truth log (`<events>.truth.jsonl`, simulator only)
`{"kind":"state","ts","equipment_id","state"}` on every real state change, and
`{"kind":"scenario","scenario","key","start_ts","end_ts"}` for each injected/random condition.
Used only to score the derivation engine. Not part of the production event stream.

## Transport (MQTT)
Topic `<prefix>/<plant_id>/<line_id>/<gateway_id>/events` (default prefix `spi/v1`), one event JSON per message, QoS 1 (at-least-once, so
duplicates are expected and removed by `event_id`). MQTT 5: each message carries its gateway's device token as the user property
`spi-token`, outside the event; the backend stores only events whose token is an active production gateway's and whose `gateway_id`,
`plant_id` and `line_id` are that gateway's (`EDGE.md`, "Gateway token"). The backend uses a persistent session (never expiring)
so the broker queues events while it is down.
Raise the broker queue limit (`deploy/mosquitto.conf`): the default of 1000 drops messages when a gateway flushes a long outage buffer.
