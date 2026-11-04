# PLC runtime: never write OK for a part whose result finished after a PLC link or heartbeat loss since its trigger (PLC-INTERFACE 5.6)

- Status: done
- Session: plc-link-loss-ng, 2026-10-30
- Pull request: #141
- Section 5 item: PLC runtime: never write OK for a part whose result finished after a PLC link or heartbeat loss
- Components/files: dotnet/VisionCore/Plc/PlcRuntime.cs, dotnet/VisionCore/Plc/ModbusServer.cs (IPlcLink.Disconnects, DisconnectsAtInputWrite), dotnet/VisionCore.Tests/PlcLinkLossTests.cs (new), docs/vision-system/PLC-INTERFACE.md 5.6
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #141: a part across a PLC link or heartbeat loss is NG 100, main trigger and camera channels; a latched OK is taken back when the loss shows after the result; only connections that wrote count) PLC runtime: never write OK for a part whose result finished after a PLC link or heartbeat loss since its trigger (PLC-INTERFACE 5.6); write NG with error 100 and clear Done/OK on reconnect (line safety).

## Timeline (section 3)
| 2026-10-30 | PLC runtime: NG 100 instead of OK for a part across a PLC link or heartbeat loss (found by the full system test, #140); a latched OK taken back on a later loss; reading clients do not count; 7 link-loss tests | PLC-INTERFACE 5.6; requirements 12 |

## Decisions (section 4)
| Which disconnects count | Only a connection that wrote to the vision blocks (the PLC); an HMI or poll tool that only reads does not, so it cannot stop the line. A PLC that opens a new connection per request would NG every part: none known (Delta DVP/AS keep one); ask the owner before supporting one | PLC-INTERFACE 5.6 |

## Update log (section 8)
| 2026-10-30 | PLC-INTERFACE.md 5.6: what the PLC gets for a part across a link loss, which connections count, how error 100 clears | Claude (Mac, branch claude/plc-link-loss-ng) |

## Pending
- Station (VisionStudio Station.cs CountPart, plant report): record a part the PLC runtime turned NG after a link loss as NG reason 100, not the inspection's OK (traceability, requirements 12); needs an event from PlcRuntime.
- Owner: does any PLC brand planned (Siemens, Rockwell, Mitsubishi) open a new Modbus connection per request? Such a PLC would NG every part under the 5.6 rule.
- PlcRuntime.TestResult (PLC screen test signal, no callers today) can overwrite a latched NG with OK while not Busy.
