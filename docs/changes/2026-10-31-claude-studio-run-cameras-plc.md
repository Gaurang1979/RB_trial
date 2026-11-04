# Vision Studio Run screen: one image tile per camera, line speed and cycle budget from the runtime on the charts, Batch and PLC card

- Status: done
- Session: zen-austin-a9df7b, 2026-10-31
- Pull request: #191
- Section 5 item: new
- Components/files: proto/visionstudio/remote/v1/runtime.proto (additive: Status.line_speed_ppm, cycle_budget_ms, PlcState plc); dotnet/VisionCore/Plc/PlcRuntime.cs (LinkUp, HeartbeatOk, RunEnable, LastTriggerId, read without the PLC lock); dotnet/Runtime.Host (LineRuntime.Status, RuntimeOptions.LineSpeedPpm and CycleBudgetMs, vs-runtime.json, README); dotnet/RuntimeContract/Stub/StubRuntime.cs; dotnet/Studio (ViewModels/CameraTile.cs and Views/CameraTileView new, RunViewModel, RunView, Controls/Charts.cs, Runtime/GrpcRuntimeClient.cs and RuntimeModels.cs, CheckLine.Header, i18n/en.json); tests in Studio.Tests, Runtime.Host.Tests, RuntimeContract.Tests
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #191: one live image tile per camera side by side, each with its own picture, verdict frame, tooltip and heading, a tile without a current picture dimmed and never green; "This picture" lines under each camera's heading; Status.line_speed_ppm, cycle_budget_ms and plc in the runtime contract, from Runtime:LineSpeedPpm and CycleBudgetMs and the PLC runtime; the line speed drawn in the parts-per-minute chart and the budget in the cycle-time chart, each with a tooltip; a Batch and PLC card with the batch values, link, heartbeat, RunEnable and the last part the PLC was given) Vision Studio Run screen: one image tile per camera, line speed and cycle budget on the Overview charts, Batch and PLC card (UI-UX-SPEC.md 4.1, principle 15)

## Timeline (section 3)
| 2026-10-31 | Vision Studio Run screen: one live image tile per camera with its own verdict frame and "This picture" lines per camera; line speed and cycle budget from the runtime (Status.line_speed_ppm, cycle_budget_ms) drawn as limit lines on the Overview charts; Batch and PLC card from Status.batch and the new Status.plc (link, heartbeat, RunEnable, last trigger and result). Rendered at 1366 x 768 with one and two cameras | UI-UX-SPEC 4.1, principle 15; requirements 12 |

## Decisions (section 4)
| Run screen camera tiles | One tile per camera, side by side in job order, replaces the camera chips: an operator sees every camera at once (owner, 2026-10-31: many things on one screen). With one camera the screen is as before. A tile is dimmed and its frame never green when it has no picture, the line is stopped, or (two or more cameras) its camera sent nothing for 5 s while another's pictures came; a bad part keeps its red. While the part is NG (any camera, a camera without a picture, the PLC link) no tile is green, only the failing camera's is red. The result block still counts every camera | UI-UX-SPEC 4.1; requirements 12 |
| Line speed and cycle budget | The runtime sends them in Status (additive fields 18 and 19), from Runtime:LineSpeedPpm and Runtime:CycleBudgetMs (0 = 60000 / line speed); 0 means not set and no line is drawn. Neither the job file nor the PLC settings hold a line speed today | UI-UX-SPEC 4.1, principle 15 |
| Batch and PLC card | PLC signals come from new lock-free reads on PlcRuntime (LinkUp, HeartbeatOk, RunEnable, LastTriggerId): the status is built from station and PLC events, so it never waits for the PLC's lock. A signal is green only while the link (and for RunEnable the heartbeat) is up; the Link lamp is not green while the heartbeat is lost; a PLC set up whose link could not start is sent as a lost link, not as no PLC; the last part's result is in colour only while running over a live link and for 5 s from when the screen first saw it (another PC's clock never keeps it green) | UI-UX-SPEC 4.1; requirements 12 |

## Update log (section 8)
| 2026-10-31 | dotnet/Runtime.Host/README.md: Runtime:LineSpeedPpm, CycleBudgetMs and Status.plc | Claude (Mac, branch claude/studio-run-cameras-plc) |

## Pending
- Line speed and cycle budget per product: take them from the job (or the PLC's line settings) instead of one Runtime:LineSpeedPpm for the line PC.
- Vision Studio Run screen with two real cameras and a PLC on the line PC: tiles, the Batch and PLC card and the chart lines at 225 parts a minute.
