# Data model (proposal for M2 - not yet implemented)

Hierarchy (spec s7/s22): Organization -> Plant -> Area -> Line -> Equipment -> Sensor.
Master data in plain PostgreSQL tables; high-frequency data in TimescaleDB hypertables.

## Master data
`organization`, `plant`, `area`, `line`(gateway_id, target_per_hour), `equipment`(type, standard_cycle_time_s, criticality, supplier, amc, protocol),
`queue`(line_id, position, capacity), `sensor`(equipment_id, name, unit, expected_range), `gateway`, `shift`, `production_target`,
`user`, `role`, `permission`(scope: org/plant/area/line), `downtime_reason`, `alert_rule`, `audit_log`.

## Time-series / events (hypertables, keyed by `event_id` for idempotent insert)
| Table | Source | Retention (configurable, spec s44) |
|---|---|---|
| `raw_event` (all envelope fields + payload JSON) | ingestion | short (weeks) |
| `signal_sample`, `sensor_sample`, `queue_sample` | split from raw | short-medium |
| `cycle_event` (equipment, ts, cycle_time, result, counters) | cycle | long |
| `equipment_state_interval` (equipment, state, start, end, confidence, evidence) | derivation engine | long |
| `downtime_event` (reason, category, auto/manual, acknowledgement) | derived + manual | long |
| `sensor_health_event`, `alert`, `notification` | detectors / alert engine | long |
| `kpi_snapshot` (line/equipment, window, availability, performance, quality, OEE, counts) | KPI engine | long |

Every derived row stores `basis` = measured | calculated | detected | ai_inference | user_entered (spec s53).
