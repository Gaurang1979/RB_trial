# Sensor health and alerts (M4; `backend/sensor_health.py`, `backend/alerts.py`)

## Sensor health (basis: detected)
| Status | Meaning | Alert-worthy |
|---|---|---|
| OK | Readings arrive and look plausible. A constant reading while the *process is also stable* is not flagged | no |
| STUCK | Identical readings for >= 10 min, and during that flat run the machine changed between producing and not producing | medium |
| IMPOSSIBLE | Readings outside the sensor's configured `valid_range` (2 of the last 3) | high |
| OFFLINE | No reading for > 5 sample intervals (min 60 s) although the gateway is reporting | medium |
| GATEWAY_SILENT / UNREACHABLE | The line gateway or the machine cannot be seen: state unknown, **not** a sensor fault | no |

Each result carries last valid reading, current reading, since, duration, reason and confidence. The detector follows a flat run back to its real start (up to 24 h),
so the verdict does not flap when the process change scrolls out of a short window. Tunables: `SensorParams`; per sensor `valid_range`, `sample_interval_s`, `stuck_epsilon`.

## Alert rules (site JSON `alert_rules`; defaults in `DEFAULT_RULES`)
Types: `equipment_stopped`, `bypass_detected`, `equipment_unreachable`, `gateway_silent`, `sensor_unhealthy`, `production_below_target`, `queue_level` (full/empty).
A rule has: `id`, `severity` (high/medium/low), `threshold_s` (or `threshold`), `recipients` (role names of `auth.ROLES`; a rule naming any other, in recipients or escalations, is refused at load), `channels`, `clear_after_s` (default 60), `escalations` (`[{after_s, recipients}]`).
A rule with `"enabled": false` is checked and then left out.

Vision types (PL2, `backend/vision/alerts.py`): `vision_alarm` (an alarm a vision station raised and has not cleared; the alert takes the alarm's
priority as its severity, is acknowledged when the station acknowledges the alarm, and resolves as soon as the station clears it) and
`vision_station_offline` (no heartbeat from a station for `threshold_s`, never less than 15 s). A rule list gets the rule of `DEFAULT_VISION_RULES` for each vision type it does not name (`vision-alarm`: production and maintenance managers, plant manager after 15 min unacknowledged;
`vision-station-offline`: high, 60 s, IT/OT and maintenance manager, resolves 30 s after the station is back; `vision-grade-drift`: medium, quality and production managers, plant manager after 30 min unacknowledged, resolves 300 s after the grade recovers). `vision_grade_drift` (Req 17.3 PQ-03, `backend/vision/grades.py`): one alert per station and tool whose print grades drift toward the lowest grade: the mean of the latest `window` (20) samples within `margin` (0.5) of `lowest_grade` (2 = C; the station's own `<tool>.grade_min` wins when sent), or a least-squares fall that reaches the lowest grade within the next `window` samples; at least `min_samples` (10) samples from the last `max_age_s` (8 h); an ungraded sample counts as F. An open alert stays open while the mean stays within 1.5 x `margin`, so it does not flap. It only warns: the line judges each graded part. Vision alerts run on the server's
wall clock, not on production data time, and are evaluated after every station batch and heartbeat and every 5 s; production evaluation never
opens, clears or escalates them, nor the other way round. Acknowledging a vision alert on the server does not acknowledge the alarm on the station. Each raising of an alarm is its own alert, so an
alarm cleared and raised again opens, notifies and escalates afresh. A vision alert's title names the line, the station by its device name and the alarm by its
catalogue title (`backend/vision/alarms.py`, strings in `backend/vision/lang/<tag>/alarm.json`); an alarm code not in the catalogue is titled "Alarm <code>". Disabled stations, and stations no longer on that line, are left out. After a server
start a station gets `threshold_s` to call in before it is reported offline. One thread evaluates; a batch or heartbeat only wakes it, so an alerting
failure never fails a station's batch.
Not yet: cycle-time threshold and SLA-exceeded rules (need SLA data), per-user recipients (no accounts yet).

## Lifecycle
`OPEN -> ACKNOWLEDGED -> RESOLVED`. An alert **opens** when its condition has held for `threshold_s`; it **stays open** while the underlying state persists
(a 20 s recovery between two faults is one incident), and **resolves itself** after the state has been clear for `clear_after_s`. While the machine cannot be seen
(silent gateway, unreachable machine) an open alert is held, not cleared. Unacknowledged alerts **escalate** per `escalations`; acknowledging stops escalation.
Manual resolve needs a reason. Time is *data time*, so replays and tests are deterministic.

## Audit and notifications
Every step is stored (`alert_event`: opened, escalated, acknowledged, resolved: who, when, why) and every send attempt (`notification`: channel, recipient, status).
Today the actor of an acknowledgement is the free-text name typed in the dashboard (no user accounts yet): treat it as a note, not identity.

## Channels
- `in_app`: the stored alert list (API, dashboard). Built in.
- `webhook`: JSON POST to `SPI_ALERT_WEBHOOK` (bridge to Slack, WhatsApp/SMS gateways, ticketing). Runs on its own thread; failures are recorded, never raised. List `"webhook"` in a rule's `channels`.
- Dashboard notifications: while the dashboard is open (also installed as an app on a phone) it shows browser notifications for new high/medium alerts.
- **Not built:** background Web Push (VAPID keys, subscriptions), email, SMS. The service worker already handles `push` events for when it is added.

## Verified
Sensor-health and alert tests (SQLite and PostgreSQL) including: no alert on a healthy line, single alert per incident, escalation only when unacknowledged,
webhook success and failure, gateway outage during a stop, and two regressions found by running the demo (flapping stuck-sensor verdict, alert cleared while blind).
