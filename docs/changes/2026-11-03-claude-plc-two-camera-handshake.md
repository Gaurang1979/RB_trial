# Two-camera PLC trigger on VisionLineDVP.st's handshake: no result in time is NG, matched by part ID, reject (X4) checked (Req 17.10 MRG-05)

- Status: done (marked ready on the owner's word, 2026-11-03: "Yes, mark ready"; the ISPSoft compile and the line try are under Pending)
- Session: plc-two-camera-handshake, 2026-11-03
- Pull request: #287
- Section 5 item: Build the two-camera trigger on VisionLineDVP.st's handshake (Pending of 2026-11-03-claude-unified-teach-studio-preview.md)
- Components/files: plc/delta-dvp/VisionLineDVP.st, plc/delta-as/VisionLine.st (same logic), plc/queue_sim.py, plc/st_check.sh (new), dotnet/VisionCore.Tests/PlcTwoCameraTriggerTests.cs (new), docs/vision-system/PLC-INTERFACE.md (3.5), docs/vision-system/PLC-DELTA-DVP.md
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #287: camera channel result only with the part's echo and OK, refused or no answer in 1 s = NG, one X4 edge per reject, time-mode reject per part; queue_sim.py 16 scenarios, PlcTwoCameraTriggerTests, rusty check clean; ISPSoft compile and line try under Pending) Build the two-camera trigger on VisionLineDVP.st's handshake: no result in time is NG, match by part ID, check the reject (X4) (Req 17.10 MRG-05).

## Timeline (section 3)
| 2026-11-03 | Two-camera trigger on the DVP / AS handshake (camera channels, PLC-INTERFACE 3.5): a channel's result counts only with Done, that part's number in the echo, OK set and NG clear; a refused trigger (TriggerRejected) is NG at once instead of after the 1 s timeout; a channel takes its next part only after Done and TriggerRejected dropped; no answer, refused, busy or not ready leave the part "not inspected" in the part log; every reject needs its own X4 edge within the window (a ring of fire times, touching products as one), where one edge used to clear any number of rejects; time mode (reject sensor + delay) gives every NOT OK part its own firing (a ring of 8 due times) where one delay timer used to fire once for two parts inside one delay. Verified with queue_sim.py (16 scenarios) and a .NET test running a C# copy of CamStation against the real PlcRuntime; checked clean with the open-source rusty compiler (plc/st_check.sh: both programs, no errors, the same 8 integer-width warnings as main); not compiled in ISPSoft | 17.10 MRG-05, 12, PLC-INTERFACE 3.5 |

## Decisions (section 4)
| Two-camera trigger | Built on the existing camera channels of VisionLineDVP.st (and VisionLine.st for AS), not a separate program and not hardware Y4 / Y5 triggers: the DVP12SE11T has only Y0-Y3 on the CPU and the cameras are triggered over Modbus with the part number, which is what lets the PC match results by part ID | 17.10 MRG-05, REQ-22 item 3 |
| Time-mode reject | Each NOT OK part reaching the reject sensor gets its own due time in a ring of 8; a 9th waiting is a reject failure alarm whatever the "stop when a reject is not seen" setting, because that part would pass | 12, 17.10 MRG-05 |
| Reject verification | One X4 edge per reject, each within its own window from its firing; a touching product rejected right after its group's previous product needs none of its own (the sensor sees one block). More than 8 rejects waiting for X4 count as misses | 17.10 MRG-05, 12 |

## Update log (section 8)
| 2026-11-03 | PLC-INTERFACE.md 3.5: how the PLC takes a channel result (echo, Ok and not Ng, refused = NG, one X4 edge per reject); PLC-DELTA-DVP.md: rejection rules, reject delay per part (time mode), verification window wording, two commissioning checks (camera with no answer, two rejects close together) | Claude (Mac, branch claude/plc-two-camera-handshake) |

## Pending
- Compile VisionLineDVP.st and VisionLine.st in ISPSoft (rusty's check passes; still to see in ISPSoft: TIME_TO_DINT, VAR_GLOBAL inside the PROGRAM, the AT addresses) and run the two new commissioning checks of PLC-DELTA-DVP.md on the DVP line (Req 17.10 MRG-05).
