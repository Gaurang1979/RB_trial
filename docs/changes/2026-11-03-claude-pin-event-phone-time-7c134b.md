# Plant server: POST /auth/pin-events accepts an optional phone time, so a queued report keeps when the PIN was typed

- Status: done
- Session: pin-event-phone-time, 2026-11-03
- Pull request: #312
- Section 5 item: new (from the ## Pending list of docs/changes/2026-11-03-claude-mobile-pin-events.md)
- Components/files: plant_server/backend/auth.py (pin_event), plant_server/backend/api.py (/auth/pin-events), plant_server/tests/test_session_idle_pin_audit.py, docs/REQUIREMENTS-VISION-SYSTEM.md (Req 12 PT11-08b), docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #312: optional `at` on POST /auth/pin-events, kept as `phone_at` next to the server time; refused beyond 5 min ahead or 5 min before the sign-in, or without a time zone) Plant server: accept an optional phone time on POST /auth/pin-events so a queued report keeps when the PIN was typed (PT11-08b)

## Timeline (section 3)
| 2026-11-03 | Plant server keeps the phone time of a PIN event: `POST /auth/pin-events` takes an optional `at` (ISO 8601 with `Z` or an offset), stored in UTC as `phone_at` in the audit entry's detail next to the entry's server time; an `at` more than 5 min ahead of the server, more than 5 min before that sign-in began (so at most 12 h 5 min old), or without a zone is refused with 422 and nothing is recorded; without `at` nothing changes. 3 new tests | Req 12 PT11-08b, PT11-08a, PT11-08 |

## Decisions (section 4)
| Phone time of a PIN event | The server time stays the audit entry's time and the chain order; the phone time is an extra `phone_at` field, since a phone clock can be wrong. Idle time and the wrong-PIN count still go by arrival. Bounds tie to the session: a report can only be for a sign-in that exists, so a time before that sign-in (5 min allowed for drift, also ahead of the server) cannot be right | Req 12 PT11-08b |

## Update log (section 8)
| 2026-11-03 | REQUIREMENTS PT11-08b (new); PLANT-DASHBOARD-AND-MOBILE 6: `at` on POST /auth/pin-events | Claude (Mac, branch claude/pin-event-phone-time-7c134b) |

## Pending
- Mobile app: send `at` (the time the PIN was typed) with each queued PIN report, and on a 422 about `at` (phone clock off by more than 5 min) resend the report without it, never drop it, so a lockout still ends the session on the server (PT11-08b).
