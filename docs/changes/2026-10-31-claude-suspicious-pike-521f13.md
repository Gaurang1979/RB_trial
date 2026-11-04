# Modbus TCP server hardened against anything on the PLC network (fuzz and specific tests)

- Status: done
- Session: modbus-robust (suspicious-pike-521f13), 2026-10-31
- Pull request: #179
- Section 5 item: new
- Components/files: dotnet/VisionCore/Plc/ModbusServer.cs, dotnet/VisionCore.Tests/ModbusRobustnessTests.cs (new), docs/vision-system/PLC-INTERFACE.md (new section 2.1)
- Depends on: none; #167 (PLC follow-ups) touches the same area (PlcService.cs binds the server) and merges first

## Section 5
- New item: `[x] (done 2026-10-31, PR #179: fuzz and specific tests; exact PDU lengths, protocol id 0, connection cap with eviction, idle and frame times, accept loop that never stops) The vision PC's Modbus TCP server survives anything on the PLC network: malformed or non-Modbus frames, unknown functions, partial frames, slow senders, many and idle clients, fast polling; answers with exceptions or closes that connection only, never writes a block from a bad frame, keeps serving the PLC`

## Timeline (section 3)
| 2026-10-31 | Modbus TCP server hardened (PR #179): a write whose PDU is not exactly the length its function needs gets exception 3 and writes nothing (it was accepted with trailing bytes); a protocol id other than 0 or an MBAP length outside 2-254 closes that connection; at most 8 connections, a newcomer always let in and the connection idle longest closed (one that never wrote first); 60 s idle time, 2 s frame time (also for a client that never reads its replies); the accept loop keeps going after an accept error (it stopped for good before); `Connected` means a connection that wrote is open, so an HMI or a scan no longer shows the PLC as connected. 12 new tests in ModbusRobustnessTests, including a seeded fuzz of 1500 bad frames and the runtime staying Ready through a storm on the network | 12, PLC-INTERFACE 2.1, 5.6 |

## Decisions (section 4)
| Modbus server limits | At the connection cap the newcomer is let in and the connection idle longest is closed, one that never wrote first, rather than refusing the newcomer: a PLC reconnecting after a cable pull (its old socket still open) always gets in, and scans never push it out. Defaults 8 connections, 60 s idle, 2 s frame: a PLC polls every 10-20 ms. Frames that are not Modbus TCP close the connection because the stream can no longer be trusted (Modbus TCP has no checksum) | 12, PLC-INTERFACE 2.1 |

## Update log (section 8)
| 2026-10-31 | PLC-INTERFACE.md section 2.1: Modbus TCP server on a shared network (exceptions, frames that close a connection, limits, which connection counts as the PLC) | Claude (Mac, branch claude/suspicious-pike-521f13) |

## Pending
- Show the Modbus server limits (connections, idle time, frame time) in the PLC settings page once #167's interface binding has merged (PlcService.cs, Plc.razor); the defaults apply until then
