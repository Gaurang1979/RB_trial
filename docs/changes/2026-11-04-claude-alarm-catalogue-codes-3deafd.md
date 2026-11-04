# Plant server alarm catalogue lacks the station agent's `ledger_tampered` and `licence_content_key` codes (Req 17.10 MRG-08 item 6, ENC-HOST-01)

- Status: done
- Session: alarm-catalogue-codes, 2026-11-04
- Pull request: #335
- Section 5 item: Plant server alarm catalogue lacks
- Components/files: plant_server/backend/vision/alarms.py, plant_server/backend/vision/lang/en/alarm.json
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #335: both codes in backend/vision/alarms.py CODES with English title and fix in lang/en/alarm.json; tests/test_vision_alarm_catalogue.py passes) Plant server alarm catalogue lacks the station agent's `ledger_tampered` and `licence_content_key` codes: `tests/test_vision_alarm_catalogue.py` fails on main (found 2026-11-04).

## Timeline (section 3)
| 2026-11-04 | Plant server alarm catalogue (PR #335): `ledger_tampered` (Vision Studio, results ledger check failed, alarm 801, production refused) and `licence_content_key` (LineRunner refused to start: no licence content key, or an encrypted product or model could not be opened; line state Fault) added to backend/vision/alarms.py with English title and fix, so their alerts and the floor API show plain text instead of "Alarm <code>". Tests: plant_server suite 435 passed, 6 skipped | 17.10 MRG-08, ENC-HOST-01 |

## Decisions (section 4)
| Alarm catalogue severity | The catalogue holds only title and fix; severity stays with the station, which raises both codes as High (Station.cs, LineRunner Program.cs). The fix texts tell the operator not to run the line and to call the vision engineer, since both alarms refuse production (line safety, section 12) | 17.10 MRG-08, ENC-HOST-01 |

## Update log (section 8)
| 2026-11-04 | Change note for the alarm catalogue codes ledger_tampered and licence_content_key | Claude (Mac, branch claude/alarm-catalogue-codes-3deafd) |

## Pending
- `ledger_tampered` and `licence_content_key` alarm title and fix in the other 14 languages (plant server lang/<tag>/alarm.json; Req 17.10 REQ-16, LOCALISATION.md 2.1).
