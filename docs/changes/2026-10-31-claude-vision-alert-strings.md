# Vision alert escalation role, alert titles and camera/station/pressure alarm strings

- Status: done
- Session: vision-alert-strings, 2026-10-31
- Pull request: #201
- Section 5 item: new
- Components/files: plant_server/backend/alerts.py, plant_server/backend/vision/{alerts,api,alarms}.py, plant_server/backend/vision/lang/en/alarm.json, mobile/plant_app/lib/modules/vision/{vision_api,line_screen}.dart, their tests
- Depends on: none

## Section 5
- "The production default rule equipment-stopped": `[x] (done 2026-10-31, PR #201: escalates to plant_manager; normalize_rule refuses a rule whose recipients or escalation recipients name a role not in auth.ROLES, so a typo can no longer leave an escalation silent)`
- "Line names in vision alert titles": `[x] (done 2026-10-31, PR #201: titles read "<line name> <station device name>: <catalogue title>", the detail carries the catalogue's fix and the code; the evidence keeps the ids)`
- "Alarm catalogue entries and alarm.<code>.* strings": `[x] (done 2026-10-31, PR #201: backend/vision/alarms.py and lang/en/alarm.json hold alarm.<code>.title and .fix in English for the five codes; /vision/floor and /vision/lines add station_name, title and fix to each open alarm (?lang=, English fallback); the mobile app's line view shows them; the dashboard shows them through the alert titles; a test fails if Vision Studio or LineRunner raise a code the catalogue lacks)`

## Timeline (section 3)
| 2026-10-31 | Vision alerts: equipment-stopped escalation goes to plant_manager and every rule's roles are checked; vision alert titles and the floor API name stations by device name and alarms by the new English alarm catalogue (camera_error, child_camera_error, parent_camera_error, station_error, pc_pressure); mobile line view shows them | 12, 14 |

## Decisions (section 4)
| Alert rule recipients | Recipients and escalation recipients must be roles of auth.ROLES; an unknown name is refused when the rules load, not left to reach no one. "Plant head" is the plant_manager role (no separate role: the design documents define none) | 12 |
| Alarm strings on the plant server | The server owns the alarm catalogue (lang/<tag>/alarm.json, LOCALISATION.md 2.1) and sends title and fix with each alarm, so the mobile app and the dashboard need no string table of their own for alarms; unknown codes still alert as "Alarm <code>" | 12, 14 |

## Update log (section 8)
| 2026-10-31 | change note only | Claude (Mac, branch claude/vision-alert-strings) |

## Pending
- Translations of the ten alarm.<code>.title and .fix strings (plant_server/backend/vision/lang/en/alarm.json) into the other 14 languages: the plant server, dashboard and mobile app carry no language tables yet.
- The mobile app asks /vision/floor in English only; pass the user's language (?lang=) once the app has a language setting.
- Vision alert titles are stored in English; a per-user language for push notifications needs the alert title built per recipient.
