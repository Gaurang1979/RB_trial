# Mobile app: send the phone time `at` with each queued PIN report, and resend without it on a 422 about `at`

- Status: done
- Session: mobile-pin-time, 2026-11-04
- Pull request: #339
- Section 5 item: new (from the ## Pending list of docs/changes/2026-11-03-claude-pin-event-phone-time-7c134b.md)
- Components/files: mobile/plant_core/lib/src/pin_events.dart, mobile/plant_core/lib/src/modules.dart (PcServers._report), mobile/plant_app/lib/src/app.dart (_sendReport), mobile/plant_core/test/pin_events_test.dart, mobile/plant_app/test/pin_events_test.dart, docs/REQUIREMENTS-VISION-SYSTEM.md (Req 12 PT11-08c)
- Depends on: none (server side merged in #312)

## Section 5
- [x] (done 2026-11-04, PR #339: each queued PIN report carries `at`, the phone time in UTC, kept in the offline queue; a 422 to a report with `at` sends it again at once without `at`, never dropped) Mobile app: send `at` (the time the PIN was typed) with each queued PIN report, and on a 422 about `at` resend the report without it, never drop it (PT11-08c)

## Timeline (section 3)
| 2026-11-04 | Mobile app sends the phone time of a PIN event: each report the app queues (unlock, wrong PIN, lockout) carries `at` (the phone clock in UTC, `Z`) when the PIN was typed, kept in the secure-store queue across restarts; on a 422 to a report with `at` the same report goes again at once without it, keeping queue order, so a lockout still ends the sign-in on the server; reports queued by the older app are sent without `at`. plant_core 5 and plant_app 2 new tests | Req 12 PT11-08c, PT11-08b, PT11-08a |

## Decisions (section 4)
| 422 to a PIN report with a phone time | Any 422 to a report that carries `at` is resent without `at` rather than reading the error text: at most one extra request, and a report the server would refuse for another reason is refused again without `at` and handled as before. The report is rewritten in the queue (not dropped), so if the network goes between the two tries it waits without `at` | Req 12 PT11-08c |

## Update log (section 8)
| 2026-11-04 | REQUIREMENTS PT11-08c (new) | Claude (Mac, branch claude/mobile-pin-event-time) |
