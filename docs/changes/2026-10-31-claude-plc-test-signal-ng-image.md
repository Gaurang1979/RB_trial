# PLC test signal gated in the runtime (engineer, Teach, never while RunEnable, 2 s pulse); NG image kept for a part the PLC got as NG 100; end-to-end tests for the station's deferral and the plant NG ("PLC") report

- Status: done
- Session: plc-test-signal-ng-image, 2026-10-31
- Pull request: #200
- Section 5 item: PLC page test signal: gate it as section 7 says
- Components/files: dotnet/VisionCore/Plc (PlcRuntime.TestResult, PlcTestRefusal), proto/visionstudio/remote/v1/runtime.proto (PlcTestSignal), dotnet/RuntimeContract (RuntimeAccess.MayTestPlc), dotnet/Runtime.Host (Access, Services, LineRuntime), dotnet/VisionStudio/Services/Station.cs (held images), dotnet/StationGuard (NgImageStore.KeepsNgImages), tests (VisionCore.Tests, Runtime.Host.Tests, RuntimeContract.Tests, plant_server/tests), docs/vision-system/PLC-INTERFACE.md and UI-UX-SPEC.md
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #200: the runtime gates the test signal: `PlcTestSignal` in the runtime contract for Engineer or Admin in Vision Studio only, refused while the line runs, while the PLC sets RunEnable, while a part is inspected or not yet taken, and OK over a latched NG, each with its reason key; PlcRuntime.TestResult sends OK or NG with Done for 2 s and ends it at once when RunEnable or a trigger comes up) PLC page test signal: gate it as section 7 says (engineer role, Teach mode, never while RunEnable = 1, 2 s pulse); today the buttons are not gated and the signal latches (before this PR).
- "NG images: a part the PLC got as NG 100": `[x] (done 2026-10-31, PR #200: the station holds a copy of each OK camera image of a PLC part it did not keep, in a pooled buffer, until the PLC's verdict; an OK the PLC got as NG 100 or not taken is kept as an NG image with reason plc_link_lost or plc_not_taken, and the plant server's result carries its image id)`
- "Tests: the station's camera-channel deferral": `[x] (done 2026-10-31, PR #200: vs-runtime end to end over Modbus TCP with a simulated camera and a fake plant server: a camera channel's part is counted only when the PLC drops its trigger, one it got as NG 100 is NG "PLC link" in the results and counters and reaches the plant server as NG ("PLC", "link_lost") with its part id and image id, and the saved image is that part's frame; plant_server test of the "PLC" NG reasons on ingest, the floor and NG review)`

## Timeline (section 3)
| 2026-10-31 | PLC test signal gated in the runtime (PlcTestSignal: engineer, Teach, never while RunEnable = 1 or a part is handled, 2 s pulse, OK never over a latched NG); a part the PLC got as NG 100 after an OK inspection kept as an NG image and sent to the plant server with its image id; end-to-end tests of a camera channel part's deferral and of the plant server's NG ("PLC", ...) report | Requirements 12; PLC-INTERFACE 5.6, 7 |

## Decisions (section 4)
| Test signal gated in the runtime | The PLC runtime refuses the signal itself (line running, RunEnable, a part busy or not taken, OK over a latched NG) and the runtime contract checks the role (Engineer or Admin in Vision Studio) and Teach, so no client, old or new, can send a test OK onto a running line; the retired Razor PLC page is not changed. The signal is a 2 s pulse and ends at once when RunEnable or a trigger comes up: a test OK never stands while the PLC runs the line | PLC-INTERFACE 7; requirements 12 |
| Images of a held part are copied | A camera's frame buffer is reused for its next frames (the intake's pool hands the last-returned buffer out first), so an OK image whose part the PLC may still get as NG is copied into a pooled buffer (ArrayPool) when the NG image store would keep an NG image at all, given back as soon as the PLC took the part OK, or when the part is counted. Lazy copying at the PLC's verdict could save a later part's frame under this part's id. The copy costs about 1 ms a camera at 5 MP on every OK PLC part | Requirements 12 |

## Update log (section 8)
| 2026-10-31 | PLC-INTERFACE.md: section 7 test signal enforced by the runtime (PlcTestSignal, refusals, 2 s pulse); section 5.6 images of a part the PLC got as NG 100 kept as NG images; UI-UX-SPEC.md PLC screen test signal: 2 s, engineer, Teach only (was 500 ms); runtime.proto: PlcTestSignal | Claude (Mac, branch claude/plc-test-signal-ng-image) |

## Pending
- Vision Studio (Avalonia) PLC screen: OK and NG test signal buttons calling PlcTestSignal, shown only to Engineer and Admin, with the refusal texts (runtime.refused.plcOff, plcRunEnable, plcBusy, plcLatchedNg) in the 15 languages.
- Runtime contract: WhoAmI tells a client whether the user may send PLC test signals (may_test_plc; today the same rule as may_design_jobs).
- Line PC try: the cycle time of a PLC part with the held image copy (about 1 ms a camera at 5 MP) against the latency budget of PLC-INTERFACE section 6, on the real cameras.
