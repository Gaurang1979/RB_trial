# Mobile app vision floor on the plant server's WebSocket, with polling fallback

- Status: done
- Session: claude mobile-vision-ws, 2026-11-04
- Pull request: #338
- Section 5 item: new (Pending in docs/changes/2026-11-03-claude-vision-websocket-839996.md)
- Components/files: mobile/plant_core/lib/src/live_socket.dart (new), plant_core.dart (export), mobile/plant_app/lib/modules/vision/floor_screen.dart, vision_api.dart, vision_module.dart, mobile/plant_app/lib/src/app.dart (VisionModule gets the socket connector), tests plant_core/test/live_socket_test.dart and plant_app/test/vision_live_test.dart (new), docs/REQUIREMENTS-VISION-SYSTEM.md (17.3 LV-05)
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #338: the floor screen takes `WS /vision/ws` pushes, polls `/vision/floor` while the socket is closed, reconnects after 2, 4 .. 60 s, reconnects at once on a sign-in, sign-out or lock, pins the certificate like the HTTP client) Mobile app vision floor on the plant server's WebSocket, with polling as its fallback (Req 17.3 LV-05; pending from PR #316).

## Timeline (section 3)
| 2026-11-04 | Mobile app: the vision floor takes the plant server's `WS /vision/ws` pushes (session token or key, `wss` with the pinned certificate, ping every 10 s) and polls `/vision/floor` while the socket is closed; back-off 2 .. 60 s; a credential change reconnects at once; a 4401 on a session ends that sign-in; a read overtaken by a push is dropped. 12 new tests | 17.3 LV-05 |

## Decisions (section 4)
| Mobile live floor | The socket client (`PcLiveSocket`, `pcSocketConnect`) sits in plant_core next to the certificate trust it reuses, so Production Measurement can take its own live feed the same way; the vision screen keeps one code path for pushes and reads. The credential goes in the query (`?token=`, `?api_key=`) because that is all `/vision/ws` reads (#316); over `wss` it is encrypted like a header. While the socket is open the shown floor counts as current, since the server sends every change within 5 s and the ping closes a dead link | 17.3 LV-05 |

## Update log (section 8)
| 2026-11-04 | Requirements 17.3 LV-05 registered (mobile app on the live socket); the LV block no longer says the mobile app is out of scope | Claude (Mac, branch claude/mobile-vision-ws-1b593f) |

## Pending
- Plant server `WS /vision/ws` (and `/stream/lines`) read the credential only from the query string, so uvicorn's access log can keep a session token: also accept it as a header or subprotocol and leave it out of logs; then the app and the dashboard pages send it there (Req 17.3 LV-01).
