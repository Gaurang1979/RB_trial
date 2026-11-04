# Vision alarms as core alerts in the plant server

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #59
- Section 5 item: Vision alarms as core alerts in the plant server
- Components/files: plant_server/backend/alerts.py (vision rule types, one lifecycle for two clocks), plant_server/backend/vision/alerts.py (new), plant_server/backend/vision/api.py (evaluate after batches and heartbeats, 5 s ticker), plant_server/backend/api.py (alert list and user roles for lines from the master tables), plant_server/tests/test_vision_alerts.py (new), plant_server/tests/test_api_alerts.py, plant_server/docs/architecture/ALERTS.md, docs/vision-system/PLANT-PLATFORM.md
- Depends on: none

## Section 5
- [x] (done 2026-10-29, PR #59: rule types vision_alarm and vision_station_offline in the core alert engine, on by default; escalation, notifications, push and audit as for Production; acknowledged at the station acknowledges the alert; /alerts and user roles accept vision-only lines) Vision alarms as core alerts in the plant server (escalation, push and acknowledge like Production's).

## Timeline (section 3)
| 2026-10-29 | Vision alarms and silent vision stations are core alerts in the plant server (PL2 follow-up) | PLANT-PLATFORM.md 3 |

## Decisions (section 4)
| Vision alerts | A station alarm is an alert with the alarm's priority as severity; it resolves when the station clears it and is acknowledged when the station acknowledges it. Each raising of an alarm is its own alert. A station without a heartbeat for 60 s is a high alert (after a server start, a station first gets those 60 s to call in); disabled stations are left out. The default rule of each vision type is added to any rule list that does not name that type, so both are on by default; `"enabled": false` is the only way to turn one off | PLANT-PLATFORM.md 3; requirements 12 |
| Two clocks | Vision alerts run on the server's wall clock, production alerts on production data time; each evaluation touches only its own rule types | ALERTS.md |
| No downlink | Acknowledging or resolving a vision alert on the plant server does not acknowledge the alarm on the station: the server never commands a line PC | PLANT-PLATFORM.md 3 |

## Update log (section 8)
| 2026-10-29 | PLANT-PLATFORM.md and plant_server ALERTS.md: vision alerts | Claude (cloud, branch claude/vision-alerts) |

## Pending
- The production default rule equipment-stopped escalates to "plant_head", which is not a role (backend/auth.py ROLES), so its escalation reaches no account by web push; use plant_manager or add the role.
- Line names in vision alert titles come from the master tables; station names are the device ids. Show the station's device name and the alarm's localised text (alarm code catalogue) in alert titles.
