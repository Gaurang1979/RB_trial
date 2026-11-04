# Full system test and report: QR, Data Matrix, 1D, OCR/OCV (laser), the vision application and the PLC trigger, camera and light logic

- Status: done
- Session: full-system-test, 2026-10-30
- Pull request: #140
- Section 5 item: new (owner, 2026-10-30: "test full module ... test report also required now")
- Components/files: docs/TEST-REPORT-2026-10-30.md (new); no product code
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #140: docs/TEST-REPORT-2026-10-30.md; QR/DM 1733/1868 on 53 real images, 1D 500/500 with job rules, laser OCV 0 misprints accepted, engine 456/456, .NET 692/694 (2 load-sensitive), PLC sim 9/9, vs-runtime + simulated PLC 52 parts with one 5.6 finding) Full system test and report: QR, Data Matrix, 1D, OCR/OCV (laser), the vision application and the PLC trigger, camera and light logic

## Timeline (section 3)
| 2026-10-30 | Full system test on main 56e7735 (macOS, Apple M3): engine unit tests, .NET test projects, QR/DM bench, 1D sweep, laser OCR/OCV, PLC simulator, vs-runtime end to end with a simulated Modbus PLC; report in docs/TEST-REPORT-2026-10-30.md | Requirements 7, 12, 13; PLC-INTERFACE 4-5 |

## Decisions (section 4)

## Update log (section 8)
| 2026-10-30 | docs/TEST-REPORT-2026-10-30.md added | Claude (Mac, branch claude/full-system-test) |

## Pending
- PLC runtime: never write OK for a part whose result finished after a PLC link or heartbeat loss since its trigger (PLC-INTERFACE 5.6); write NG with error 100 and clear Done/OK on reconnect (line safety).
- Runtime simulator folder: read images by content (29 of 34 sample `.bmp` files are PNG), or refuse the folder at start.
- PLC runtime: keep error 100 when a trigger arrives during a comms loss; clear alarm 300 on Stop; JobActive only while running; store a non-numeric part id; Modbus server bound to a chosen interface.
- End-to-end QR24 run on a quiet Mac (not judged on 2026-10-30: load 28–56).
- Make `LineGradingTests.ToolGradesOnlyTheSampledParts` and `RuntimeLinkTests.A_runtime_that_stops_answering_while_streams_hang_is_dropped_by_the_probe` pass on a loaded machine.
- Data Matrix 48-can batch: 61 / 95 read and 738 ms on the slowest image (target 500 ms).
- Laser OCR on whole images with 2–3 turned lines: 101 / 200 good prints pass Check text.
- Read text without a format or expected text can pass a misread line on hard print (22 of 103 wrong laser lines scored ≥ 60): warn at setup or require a format when the tool decides OK/NG.
- Hardware run: camera, PLC handshake timing, light output timing and reject on the line.
