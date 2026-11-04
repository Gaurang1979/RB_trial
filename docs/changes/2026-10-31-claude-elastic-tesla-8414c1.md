# VisionCore.Tests green on main: Read text agreement setup, Blob defaults in job load, bad UTF-8 refused, Modbus handshake test timing

- Status: done
- Session: elastic-tesla-8414c1, 2026-10-31
- Pull request: #193
- Section 5 item: new
- Components/files: dotnet/VisionCore/Jobs/JobFile.cs; dotnet/VisionCore.Tests (ReadAgreementTests.cs, JobLoadTests.cs, PlcTests.cs)
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #193) VisionCore.Tests green on main again after #162, #177, #180 and #181 met: the Read text agreement tests set a format (#181's rule unchanged), a Blob tool with all defaults loads (the test refuses a wrong-typed Blob field instead), a job file that is not UTF-8 is refused with a reason, and the Modbus handshake and camera channel tests keep their PLC heartbeat running while they wait

## Timeline (section 3)
| 2026-10-31 | Five VisionCore.Tests failures on main fixed: ReadAgreementTests (2) gave their Read text tool no format, which #181 now refuses: they set the format `LOT A{D:5}`, and a blank picture, still NOT OK, is now explained as the format's line not found (explain.ocv.mismatch); JobLoadTests refused `{"kind":"blob","Name":"B"}`, written before the Blob tool (#180) existed: the case now has a wrong-typed Connectivity, and an unknown kind is its own case; JobLoadTests' mutation test met a byte that is not UTF-8 and got a transcoding error: JobFile.Parse checks UTF-8 first and refuses with JobLoadException (new test); PlcTests.HandshakeOverModbus lost its heartbeat (watchdog 400 ms) when a wait ran long on a busy Mac: the test's PLC now beats every 100 ms while it waits, watchdog 1000 ms, waits 5 s; CameraChannelTests had the same fault (heartbeat sent only at the start, watchdog 2000 ms) and got the same fix. VisionCore.Tests 557/557 | 12, 15 |

## Decisions (section 4)
| A Blob tool with all defaults is a valid job entry | JOB-FILE-FORMAT.md documents presence `Any` as the default, the Blob tool's own tests run it as a counting tool, and it still fails a dark or white frame and an area outside the picture; so the job load test, not the job reader, changed. Whether a Blob tool with no pass rule should be refused unless report only, as #181 does for Read text, is left to the owner (Pending) | 12 |
| Bad UTF-8 is refused when the job is read | A damaged byte in a name or text surfaced later as InvalidOperationException ("Cannot transcode invalid UTF-8"); the whole file is checked first, so it is refused with a reason like any damaged file | 12 |

## Pending
- Owner decision: refuse a Blob tool with no pass rule (presence Any, no count or area limit) unless it is report only, as #181 does for Read text (pending from PR #193, 2026-10-31)
