# Mobile app: report PIN unlock, wrong PIN and lockout to the plant server (PT11-08a)

- Status: done
- Session: claude/mobile-pin-events, 2026-11-03
- Pull request: #304
- Section 5 item: new (from the ## Pending list of docs/changes/2026-11-03-claude-plant-session-idle-pin-audit.md: "Mobile app: call POST /auth/pin-events ..."); Req 12 PT11-08a
- Components/files: mobile/plant_core (new pin_events.dart, modules.dart hook), mobile/plant_app (vision/api_auth.dart), tests, docs/REQUIREMENTS-VISION-SYSTEM.md PT11-08a. Not the vision grade-trend chart files
- Depends on: none (server side is #279, merged)

## Section 5
- [x] (done 2026-11-03, PR #304: the phone reports pin_unlock, pin_wrong and pin_lockout to POST /auth/pin-events with the sign-in's own token, sends `device` at sign-in, a 401 ends the sign-in on the phone, reports that cannot be sent are queued and sent later) Mobile app: call POST /auth/pin-events on each PIN unlock, wrong PIN and lockout (PT11-08a)

## Timeline (section 3)
| 2026-11-03 | Mobile app reports PIN events to the plant server: `PcPinReports` (plant_core, new `pin_events.dart`) queues each unlock, wrong PIN and lockout (the 5th wrong PIN is `pin_lockout`) and sends them in order with that sign-in's own token and the phone's `device`; the queue is kept in the secure store, retried every 10 s tick and at start, bounded to 200, and never delays an unlock or lockout. 401: the sign-in is dropped on the phone, its queued reports discarded, the ended note shows. A lockout is sent before the logout of the dropped sign-in. `device` is sent at sign-in ("Android a1b2c3d4": system and a random id made once). 10 core and 3 app tests, all passing | PT11-08a, PT11-05, PT11-08 |

## Decisions (section 4)
| Report queue | Reports wait in the secure store (they hold tokens), in order, up to 200 (oldest dropped), and are retried by the 10 s tick; a 5xx, 408, 429 or no answer retries, 401 ends the sign-in, any other 4xx drops that report. The server stamps the time it receives a report, so a late report shows its arrival time; owner may want the phone's time added to the API later | PT11-08a |
| Lockout wording | The 5th wrong PIN is sent as `pin_lockout` only (not also `pin_wrong`), since the server ends the session on either | PT11-08a |
| Device name | System name plus a random 8-hex id made once on the phone, not a hardware number, so no device identifier leaves the phone | PT11-08a |

## Update log (section 8)
| 2026-11-03 | REQUIREMENTS PT11-08a (new) | Claude (Mac, branch claude/mobile-pin-events) |

## Verified
- `flutter analyze`: plant_core and plant_app, no issues. Tests, run once on the final code: plant_core pin_events_test, pin_test, modules_test, 28 passed; plant_app pin_events_test, sign_in_test, app_test, 61 passed. The first run found two bugs in the new code (the 401 hook was set only in `load()`), fixed before this run. CI: not run (billing).

## Pending
- Plant server: accept an optional phone time on POST /auth/pin-events so a queued report keeps when the PIN was typed.
