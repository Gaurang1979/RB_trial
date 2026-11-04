# WhoAmI may_test_plc; Vision Studio PLC page: BindAddress field, Modbus limits, OK/NG test signal buttons gated on may_test_plc

- Status: done
- Session: plc-page-test-signal, 2026-11-03
- Pull request: #276
- Section 5 item: Runtime contract: WhoAmI tells a client whether the user may send PLC test signals
- Components/files: proto/visionstudio/remote/v1/runtime.proto (WhoAmIResponse.may_test_plc), dotnet/Runtime.Host (Access.cs), dotnet/RuntimeContract stub, dotnet/Studio (Run screen Batch and PLC card, IRuntimeClient.PlcTestSignalAsync), dotnet/VisionStudio (PlcService.cs, Plc.razor), requirements section 12 (PLC-TS-01, PLC-TS-02), PLC-INTERFACE.md section 7
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #276: WhoAmIResponse.may_test_plc = 7, Engineer and Admin in Vision Studio, filled by the runtime and the stub) Runtime contract: WhoAmI tells a client whether the user may send PLC test signals (may_test_plc; today the same rule as may_design_jobs).
- Done 2026-11-03, PR #276: Vision Studio (Avalonia) OK and NG test signal buttons calling PlcTestSignal on the Run screen's Batch and PLC card (there is no separate PLC screen; the layout is unchanged), shown only when may_test_plc, usable only in Teach with the link live and RunEnable off, "Is the machine safe?" first, refusal texts runtime.refused.plcOff, plcRunEnable, plcBusy, plcLatchedNg in English.
- Done 2026-11-03, PR #276: Vision Studio PLC page: BindAddress field (checked before saving), the Modbus server limits (connections, idle time, frame time) shown, and the OK/NG test buttons gated (Engineer, Teach, RunEnable off, the PLC connected, "Is the machine safe?" first) through PlcService.TestSignal, with the runtime's refusals; a test signal is not counted as a part.

## Timeline (section 3)
| 2026-11-03 | WhoAmI may_test_plc; PLC test signal buttons in Vision Studio (Avalonia Run screen, PLC page) gated on it and on Teach, with the "Is the machine safe?" question; PLC page BindAddress field and Modbus limits; tested: a test signal is not counted or logged as a part | 12 (PLC-TS-01, PLC-TS-02), PLC-INTERFACE 2.1, 7 |

## Decisions (section 4)
| Avalonia PLC test buttons on the Batch and PLC card | The Avalonia app has no PLC screen and the Workbench layout is fixed (AGR-08): the OK and NG buttons go into the existing Batch and PLC card, hidden for anyone without may_test_plc. may_test_plc only shows or hides them; the runtime still refuses the call for other roles | 12, 17.9 AGR-08 |

## Update log (section 8)
| 2026-11-03 | Requirements section 12: PLC-TS-01 (test signal buttons, may_test_plc), PLC-TS-02 (BindAddress, Modbus limits on the PLC page); PLC-INTERFACE.md section 7: may_test_plc, the safety question, a test signal is no part | Claude (Mac, branch claude/plc-page-test-signal) |

## Pending
- Localisation: the new Studio strings (run.plc.test_*, run.plc.tip.test, runtime.refused.plcOff, plcRunEnable, plcBusy, plcLatchedNg) and VisionStudio strings (plc.bind, plc.bind_hint, plc.limits*, plc.test_*, plc.refused.*) in the 15 languages; English only today.
