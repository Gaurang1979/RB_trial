# Plant platform: one Python plant server and one common database for every product (Vision, Production Measurement, EuroCod TIJ printers)

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #54
- Section 5 item: new
- Components/files: docs/vision-system/PLANT-PLATFORM.md (new), docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md, docs/vision-system/README.md
- Depends on: none

## Section 5
- [ ] PL1 Plant server core: plants, areas, lines and devices as tables in the Python server (production_measurement), device tokens, the site JSON as an import.
- [ ] PL2 Vision module in the Python plant server: ingest and live line state ported from #49 with its tests; vision alarms as core alerts; then close #49.
- [ ] PL3 One web dashboard: vision floor map and line view beside the production views, one sign-in.
- [ ] PL4 Mobile app on one plant server per plant, modules by licence, push for every module.
- [ ] PL5 Printing module for EuroCod TIJ printers with print and verify (needs the owner's protocol documents and first models).
- [ ] Owner: where the plant server's code lives (production_measurement as is, renamed, or a new repository) and where the mobile app lives.

## Timeline (section 3)
| 2026-10-29 | Plant platform designed: one Python plant server and one PostgreSQL database for Vision, Production Measurement and EuroCod TIJ printers; one dashboard, one app (PLANT-PLATFORM.md) | PLANT-PLATFORM.md |

## Decisions (section 4)
| One plant server | Owner, 2026-10-29: "Plant level single server will be installed for all kind of systems. Keep common database." One install per plant, one PostgreSQL database with core tables (plants, lines, devices, users and roles, events, alerts, audit) and one module per product | PLANT-PLATFORM.md 1-2 |
| Server language | Owner, 2026-10-29: the one server is Python — the production_measurement backend grows into it; the .NET plant server of #49 is ported into its Vision module and closed | PLANT-PLATFORM.md 3, 5 |
| EuroCod TIJ printers | Owner, 2026-10-29: "In future I will also sell TIJ printer EuroCod. I will share integration protocols also." A printing module, with print and verify: batch values sent once to the printer and to the vision job's text check | PLANT-PLATFORM.md 3 |
| Single application | Owner, 2026-10-29: "Multiple systems and single application." One dashboard and one mobile app per plant server, modules by licence | PLANT-PLATFORM.md 4 |

## Update log (section 8)
| 2026-10-29 | PLANT-PLATFORM.md added; PLANT-DASHBOARD-AND-MOBILE.md points to it | Claude (cloud, branch claude/plant-platform) |
