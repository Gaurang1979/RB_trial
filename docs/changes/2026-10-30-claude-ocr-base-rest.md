# Vision Studio work left on feature/ocr-base after PR #46: Find the part, PLC (Modbus TCP, Delta DVP), line and wiring, product length, reports, panel wiring, ERPNext licence check

- Status: done
- Session: Claude desktop (hybrid-app), 2026-10-30
- Pull request: #90
- Section 5 item: new
- Components/files: engine (vs_locate: include, src/fixture, tests, CMakeLists), dotnet/VisionCore (Plc, Tools, Jobs, licence), dotnet/VisionReports (new), dotnet/VisionStudio (Station, PLC, reports, licence, wiring pages), erpnext/vision_licence (new), docs/vision-system (PLC-INTERFACE, PLC-DELTA-DVP, LICENCE-SERVER-ERPNEXT, LICENSING, README)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #90: seven commits made on feature/ocr-base after #46 merged, merged with main) Vision Studio: Find the part (vs_locate, tools follow the part), PLC interface (Modbus TCP server and handshake, Delta DVP profiles and programs, cameras matched by encoder position, multi-camera tracking, bypass), line and wiring diagrams with instrument health, product length, gap and touching products, results log with PDF / Excel reports, panel wiring (supply, start / stop, contactor and star-delta drives), licence verification with the ERPNext licence server.

## Timeline (section 3)
| 2026-10-30 | The seven Vision Studio commits left on feature/ocr-base after #46 (Find the part, PLC Modbus TCP and Delta DVP, line and wiring, product length and reports, panel wiring, ERPNext licence) merged with main: Station keeps the timed loop, PLC trigger paths and licence checks and gains main's resource guard, NG image store, plant events and history on every inspection path; VisionReports on .NET 10 | 4, 8, 9, 12 |

## Decisions (section 4)
| Station after the merge | One CountPart for a full cycle and for a single-camera PLC trigger: NG counts with reasons, plant-server result and camera alarm, history; plant events only for camera runs (free run, PLC), not for stored pictures; the operator's Stop sends Stopped | 8, 12 |
| System.IO.Packaging | Pinned to 10.0.14 in VisionReports: ClosedXML 0.104.1 brings 8.0.0, which has two high-severity advisories (GHSA-f32c-w444-8ppv, GHSA-qj66-m88j-hmgj) | 13 |

## Update log (section 8)
| 2026-10-30 | PLC-INTERFACE.md, new PLC-DELTA-DVP.md and LICENCE-SERVER-ERPNEXT.md, LICENSING.md and the design README, from the feature/ocr-base commits | Claude (Mac, branch claude/ocr-base-rest) |

## Pending
- Try the PLC paths on hardware: Modbus TCP handshake with a real PLC, the Delta DVP programs, encoder-matched multi-camera tracking.
- Try the licence check against a real ERPNext site with the vision_licence app installed.
- Remove the feature/ocr-base branch and its worktree (.claude/worktrees/ocr-base) after this pull request merges.
