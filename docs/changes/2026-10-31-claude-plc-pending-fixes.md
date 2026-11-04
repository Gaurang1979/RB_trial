# PLC runtime and station: link-loss NG logged as reason 100, error 100 kept, alarm 300 cleared on Stop, JobActive only while running, text part id, Modbus bind interface, TestResult keeps a latched NG

- Status: done
- Session: plc-pending-fixes, 2026-10-31
- Pull request: #167
- Section 5 item: PLC runtime: keep error 100 when a trigger arrives during a comms loss
- Components/files: dotnet/VisionCore/Plc (PlcRuntime, PlcOptions), dotnet/VisionStudio (Station, Recorder, PlcService, Plc and Line pages), dotnet/VisionReports (ResultLog, ExcelReport), dotnet/Runtime.Host (LineRuntime), docs/vision-system/PLC-INTERFACE.md
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #167: a refused trigger keeps error 100; the runtime's camera alarm 300 ends with Stop; JobActive 0 while the line is stopped; the PLC's part id kept as text in results.db (inspections.part_text) and sent to the plant server; the Modbus TCP server listens on `BindAddress` from data/plc.json, every card when empty) PLC runtime: keep error 100 when a trigger arrives during a comms loss; clear alarm 300 on Stop; JobActive only while running; store a non-numeric part id; Modbus server bound to a chosen interface.
- [x] (done 2026-10-31, PR #167: PlcRuntime hands each part to the station (IPlcHost.Delivered) with the verdict the PLC got, when the PLC drops its trigger, when the result comes after the PLC gave up (NG, plc_not_taken), at a link or heartbeat loss or when the runtime closes (also for a part still being inspected); the station counts and logs it then, in order off the PLC thread, with the job and batch it was inspected with, so an OK turned into NG 100 is NG with failing tool "PLC link" in results.db, reason plc_link_lost in the runtime's results and counters, and NG ("PLC", "link_lost") at the plant server) Station (VisionStudio Station.cs CountPart, plant report): record a part the PLC runtime turned NG after a link loss as NG reason 100, not the inspection's OK (traceability, requirements 12); needs an event from PlcRuntime.
- [x] (done 2026-10-31, PR #167: the OK test signal never replaces a latched NG) PlcRuntime.TestResult (PLC screen test signal) can overwrite a latched NG with OK while not Busy.

## Timeline (section 3)
| 2026-10-31 | PLC runtime and station: a part is counted and logged with the verdict the PLC got (NG 100 after a link or heartbeat loss, not the inspection's OK) in the results log, the runtime's results and counters and the plant report; error 100 kept over a refused trigger; alarm 300 cleared on Stop; JobActive only while running; text part id stored; Modbus server bind address; test signal never over a latched NG. Tested over real Modbus TCP (VisionCore) and end to end in vs-runtime with a simulated PLC (Runtime.Host) | Requirements 12; PLC-INTERFACE 2, 3.2, 5.6, 7, 9 |

## Decisions (section 4)
| Station counts a PLC part when the PLC took it | A part triggered by the PLC runtime (PlcTrigger.Held) is counted, logged and reported when the runtime calls IPlcHost.Delivered: when the PLC drops its trigger, at a link or heartbeat loss, or when the runtime closes. Counting at inspection and correcting later would send the plant server two results for one part; holding it until the PLC's verdict is known gives one record with the verdict the PLC acted on. Callers without Held (software triggers, tests) are counted at once as before | Requirements 12; PLC-INTERFACE 5.6 |
| A part's verdict comes from its own results | The station decides OK from the cycle's own results and errors, never from the cameras' shared last result or error, which Stop (it clears alarm 300) or the next part may change: a camera with no picture is NG even if Stop cleared its error meanwhile | Requirements 12 |
| Error 100 is the first cause | A trigger refused during a comms loss sets TriggerRejected but leaves ErrorCode 100, so the PLC logs why the line stopped and the error still clears when the heartbeat is back | PLC-INTERFACE 5.6, 9 |
| Part id as text beside the number | inspections.part_id stays the PLC's part number (camera channels: the trigger id, matched with the part log); the new part_text column keeps the part id string the PLC sent; a log made before gets the column added on open | Requirements 12 |

## Update log (section 8)
| 2026-10-31 | PLC-INTERFACE.md: Modbus BindAddress (section 2), JobActive 0 while stopped (3.2), error 100 kept and the station's record of a part the PLC got as NG 100 (5.6), test signal never over a latched NG (7), code 100 row (9) | Claude (Mac, branch claude/plc-pending-fixes) |

## Pending
- Vision Studio PLC page: a field for the Modbus network card address (BindAddress; today set in data/plc.json), with its label in the 15 languages.
- Localisation: the plant server and mobile app NG reason key result.PLC.reason.link_lost (tool "PLC", reason "link_lost") in the 15 languages.
- PLC page test signal: gate it as section 7 says (engineer role, Teach mode, never while RunEnable = 1, 2 s pulse); today the buttons are not gated and the signal latches (before this PR).
- Tests: the station's camera-channel deferral and the plant server's NG ("PLC", ...) report have no end-to-end test yet (the runtime side is tested with a fake host).
- NG images: a part the PLC got as NG 100 after an OK inspection is not kept as an NG image (the image was judged OK when it was taken).
