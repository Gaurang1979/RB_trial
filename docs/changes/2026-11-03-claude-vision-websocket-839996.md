# Live vision updates over the plant server's WebSocket

- Status: done
- Session: vision-websocket, 2026-11-03
- Pull request: #316
- Section 5 item: new
- Components/files: plant_server/backend/vision/live.py, vision/api.py, backend/api.py (the vision mount call only), plant_server/dashboard/ng-review.html, grades.html, changeover.html, plant_server/requirements.txt and requirements.lock (websockets), plant_server/tests/test_vision_ws.py (new), docs/REQUIREMENTS-VISION-SYSTEM.md (17.3 LV-01..04), docs/vision-system/PLANT-PLATFORM.md, NG-REVIEW.md
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #316: `WS /vision/ws` pushes the floor when it changes, token or service key checked and rechecked every 5 s, scoped by role, plant and line; the NG review, print quality and changeover pages use it and poll while it is closed; uvicorn gets its WebSocket library) Live vision updates over the plant server's WebSocket (Req 17.3 LV-01..04; pending from PR #57).

## Timeline (section 3)
| 2026-11-04 | Plant server: `WS /vision/ws` pushes the vision floor on connect and when it changes (well under a second after a batch or heartbeat, within 5 s for time-only changes, never an unchanged copy), checked like `/stream/lines` and scoped like `GET /vision/floor`; NG review, print quality and changeover pages take it and fall back to polling; `websockets` added so uvicorn accepts WebSockets at all. 5 new tests | 17.3 LV-01..04 |

## Decisions (section 4)
| Vision floor push | One socket per page sends the whole scoped floor (same JSON as `GET /vision/floor`) only when it differs from what that socket was last sent, rather than per-event deltas: clients keep one code path for socket and polling, and a missed message is never a wrong state. Refusals close after the accept with 4401/4403/4400, since a refused handshake reaches a browser only as HTTP 403 | 17.3 LV-01..04 |
| uvicorn WebSockets | `websockets` is a runtime requirement: without it uvicorn refuses every WebSocket upgrade, so `/stream/lines` on the main dashboard has only ever polled on an installed server | 17.3 LV-01 |

## Update log (section 8)
| 2026-11-04 | Requirements 17.3 LV-01..04 registered; PLANT-PLATFORM.md and NG-REVIEW.md name `WS /vision/ws` | Claude (Mac, branch claude/vision-websocket-839996) |

## Pending
- Mobile app vision floor on `WS /vision/ws` (Req 17.3 LV-01..03), with polling of `/vision/floor` as its fallback.
- Plant server alarm catalogue lacks the station agent's `ledger_tampered` and `licence_content_key` codes: `tests/test_vision_alarm_catalogue.py` fails on main (found 2026-11-04).
