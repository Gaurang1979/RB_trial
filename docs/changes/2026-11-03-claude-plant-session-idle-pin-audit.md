# Plant server: idle sign-out of sessions and an audit trail of mobile PIN events

- Status: done
- Session: claude/plant-session-idle-pin-audit, 2026-11-03
- Pull request: #279
- Section 5 item: new (from the ## Pending list of docs/changes/2026-10-31-claude-mobile-pin-unlock.md: "Plant server audit of mobile PIN events" and "Plant server sessions last 7 days with no idle limit"); Req 12 PT11-08 (PT11-05, PT11-06, PT11-20, PT11-21, PT11-23)
- Components/files: plant_server/backend/audit.py (new), auth.py, api.py (auth routes, principal, live stream, admin audit routes, user disable), store.py (audit_entry table and its append-only guards, session helpers), serve.py (--session-idle), dashboard/index.html (an ended sign-in is dropped), tests (new test_session_idle_pin_audit.py; session fixtures in test_vision_ng_review.py and test_vision_changeover.py made with the current time); docs/REQUIREMENTS-VISION-SYSTEM.md PT11-08, docs/vision-system/PART11-DESIGN.md 3, 5, 9, docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md 6
- Depends on: none. Overlap: #282 (MQTT refusals) also adds a keyword to `create_app` in plant_server/backend/api.py and to its call in serve.py; any merge order, the second keeps both keywords (`mqtt_ingest=...`, `session_idle_s=...`). #276, #277, #278, #280, #281 touch docs/REQUIREMENTS-VISION-SYSTEM.md in other sections only.

## Section 5
- [x] (done 2026-11-03, PR #279: 15 min idle and 12 h at most, Factory display only keeps the 12 h; POST /auth/pin-events with the sign-in's own token; plant server audit trail in the PART11-DESIGN 5 format, append-only, hash-chained, checked at start and on demand) Plant server: idle sign-out of sessions and an audit trail of mobile PIN events (PT11-08)

## Timeline (section 3)
| 2026-11-03 | Plant server sessions end after 15 min without a call and after 12 h at most (a Factory-display-only wall screen keeps only the 12 h); an open live stream closes within 5 s of its session ending and the browser dashboard drops the ended sign-in. The mobile app can report PIN unlock, wrong PIN and lockout with the sign-in's own token; a wrong PIN never counts as use; 5 wrong in a row or a lockout end the session on the server. First part of `backend/audit.py` (PART11-DESIGN 5 entries in `audit_entry`, append-only by SQLite triggers or PostgreSQL rules, hash-chained, checked at start and on demand): sign-in, sign-in.failed, lock (once per lock), sign-out (idle too), unlock, unlock.failed, password edit, user disable; unknown names typed at sign-in are masked. 19 new tests; whole plant_server suite passes | PT11-05, PT11-06, PT11-08, PT11-20, PT11-21, PT11-23 |

## Decisions (section 4)
| Server idle sign-out | The server ends a session 15 min after the last call it received (any call with the token counts, except PIN reports of a wrong PIN and the stream's own checks) and after 12 h at most, as PART11-DESIGN 3 says. A Factory-display-only account (a view-only wall screen that changes nothing) has no idle limit, only the 12 h, so wall screens do not go blank; owner to confirm | PT11-06, PT11-08 |
| PIN events on the server | The PIN stays on the phone; the phone reports each unlock, wrong PIN and lockout with that sign-in's own bearer token (never the plant key), so only the person's own sign-in can be reported on. The server counts wrong PINs from its own audit entries and ends the session on the 5th in a row, also when the phone never sends the lockout | PT11-03, PT11-05, PT11-08 |
| One audit store | The sign-in entries are written by the plant server's `backend/audit.py` into `audit_entry` in the PART11-DESIGN 5 format (the first part of build order step 2), not a separate sign-in table, so later entries join the same chain. New actions `unlock` and `unlock.failed` were added to the design's list. A lock is written once per window, not per refused try, so nobody can flood the trail through a blocked sign-in | PT11-20, PT11-21, PT11-23, AGR-08 |

## Update log (section 8)
| 2026-11-03 | REQUIREMENTS PT11-08 (new); PART11-DESIGN 3 (what is built), 5 (actions unlock, unlock.failed), 9 (step 2 started); PLANT-DASHBOARD-AND-MOBILE 6: the plant server side of the PIN and the API for the phone | Claude (Mac, branch claude/plant-session-idle-pin-audit) |

## Pending
- Owner: may a Factory-display-only account (view-only wall screen) skip the 15 min idle sign-out and keep only the 12 h limit (built so)?
- Mobile app: call POST /auth/pin-events on each PIN unlock, wrong PIN and lockout, send `device` at sign-in, and treat a 401 there as the sign-in ended (API in PLANT-DASHBOARD-AND-MOBILE.md 6).
- Plant server: Admin alert for repeated locks (PT11-05: three locks of one user in an hour, PART11-DESIGN 3), from the `lock` audit entries.
- Plant server: an alarm when the audit chain check at start or on demand finds a break (PT11-23); today it is an audit entry and a log line.
- Plant server: anchor the head of the server's own audit chain (store the newest seq and hash outside the database), so removing the newest entries with file access is found too.
- Plant server: end abandoned sessions in a periodic sweep and audit each end; today an idle session is ended and audited when its token is next used.
- Browser dashboard: sign out after 15 min without use in the browser itself (its 2 s polling counts as use on the server).
- Plant server: a reason for user disable (PT11-22) and the rest of PART11-DESIGN build order step 2.
