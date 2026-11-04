# PI2 Mobile app foundation: one app for Vision and Production Measurement, light and dark, sounds, charts

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #50
- Section 5 item: PI2 Mobile app (Flutter, Android and iOS)
- Components/files: mobile/plant_app (new), mobile/plant_core (new), .github/workflows/mobile.yml (new), docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md sections 6 and 8, README.md, design/prototypes/plant-insight
- Depends on: #49 for the server it reads today (/api/v1/floor); see the platform plan (#54, PL2 and PL4)

## Section 5
- Built 2026-10-29 (PR #50): mobile/plant_core (theme dark by default with light and phone modes and a fade, colour roles with shapes, 7 synthesised sounds quiet on silent, alarm levels with vibration, ring, OEE gauge, trend with limits, bars, timeline and sparkline drawn by the app with text equivalents and gaps for missing values, server list with tokens in secure storage) and mobile/plant_app (Plants, Settings with colour mode, sounds and alarm sound test, add server with https only, change key and remove, Vision module: live floor worst first, alarm sounds by priority on new alarms, line view following the floor with its age, Production listed as coming); 56 tests. Still to do: sign-in with user accounts, push, QR add, the live hub instead of polling, strings in 15 languages.

## Timeline (section 3)
| 2026-10-29 | Mobile app foundation (Flutter): shared core with light and dark theme, sounds and charts as in secure_me, plant app with settings, servers and the Vision module; light-mode and settings screens in the UI prototype; 56 tests | PLANT-DASHBOARD-AND-MOBILE.md 6.1-6.2 |

## Decisions (section 4)
| App look, sound and charts | Owner, 2026-10-29: "Use light / dark colour mode, sound effects, charts and graphs like secureme. In future I am going to add Production measurement project also in same mobile app." Dark by default with light and phone modes; secure_me's sound set (no night rule: plants run night shifts; only alarms sound, never an NG part); charts drawn by the app | PLANT-DASHBOARD-AND-MOBILE.md 6.1-6.2 |
| App structure | Plain InheritedWidget and Navigator instead of Riverpod and go_router while the app is small; a module per product on a shared core | PLANT-DASHBOARD-AND-MOBILE.md 8 |
| Review before ready | Opus review found fourteen problems (no INTERNET permission in release builds, a start that could hang after a backup restore, overlapping polls sounding alarms twice, a frozen line view, NaN in charts, late or lost alarm sounds, large-text overflow, faint card borders, no way to fix a refused key, …); all fixed with tests | PLANT-DASHBOARD-AND-MOBILE.md 6 |

## Update log (section 8)
| 2026-10-29 | PLANT-DASHBOARD-AND-MOBILE.md 6.1-6.2 and 8; README rows; prototype light and settings screens | Claude (cloud, branch claude/pi2-mobile-foundation) |

## Pending
- Mobile app strings in the 15 languages (ARB files), by the translator.
- Mobile app on one plant server per plant (PL4): sign-in with the server's user accounts, modules by licence, the Vision module reading the Python plant server.
- Mobile app: trust for a plant server with a private or self-signed certificate (certificate fingerprint taken with the QR add flow).
- Mobile app: push notifications (FCM, APNs through the relay; local push on isolated networks) and the live hub instead of polling.
