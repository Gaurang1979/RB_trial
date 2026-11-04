# PI1 Station agent in Vision Studio and LineRunner

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #58
- Section 5 item: PI1 Station agent in Vision Studio
- Components/files: dotnet/StationAgent/StationPublisher.cs (new), Agent.cs and EventQueue.cs (many events per disk flush), dotnet/StationAgent.Tests/PublisherTests.cs (new), dotnet/VisionStudio (Station, Program, Run screen, appsettings, en strings), dotnet/LineRunner/Program.cs
- Depends on: none

## Section 5
- [x] (done 2026-10-29, PR #58: StationPublisher hands events to the agent through a bounded memory queue and a writer thread that stores up to 500 per disk flush, so a part never waits for the disk or the network; Vision Studio (camera runs only, not test folders) and LineRunner (not in --software bench mode) send every part's result with its NG reasons and job version, line state, job changes, counters every 10 s, memory and disk health every minute and camera, station and PC alarms; Vision Studio's Run screen and LineRunner's 50-case line show the plant link; LineRunner now rejects a case whose parent camera sent no frame within a second) PI1 Station agent in Vision Studio and LineRunner: events with store-and-forward on disk, gap-free sequence numbers.

## Timeline (section 3)
| 2026-10-29 | Vision Studio and LineRunner publish to the plant server through the station agent; the line runs as before when the link is off or the server is away | PLANT-DASHBOARD-AND-MOBILE.md 3 |

## Decisions (section 4)
| Station token on the line PC | Read from a file (Plant:TokenFile, --plant-token-file) or VS_PLANT_TOKEN, never from appsettings.json or the command line, so it stays out of backups of settings and out of process lists | PLANT-PLATFORM.md 2 |
| Publishing never slows a part | Events go into a bounded memory queue (20,000) and a writer thread stores them; a full queue loses and counts the event (shown on the Run screen and LineRunner's status line) instead of making the inspection wait | REQUIREMENTS section 12 |
| Only production goes to the plant server | Vision Studio runs from test folders and LineRunner's --software bench mode publish nothing, so test images never enter the plant's counts and yield | PLANT-PLATFORM.md 3 |
| Plant counters | A station's counter.snapshot counts camera parts since the program started and is not reset by a job change, because the server keeps each station's latest snapshot; restarting at each shift is an owner decision (Pending) | PLANT-DASHBOARD-AND-MOBILE.md 3 |
| LineRunner parent timeout | With a parent camera, no parent frame within a second is a parent not read and the case is rejected (it could pass before) | REQUIREMENTS section 12 |

## Pending
- Vision Studio: an image id on NG results, linking each NG to its saved image (NgImageStore) for NG review.
- Vision Studio and LineRunner: the PLC's line states (starved, blocked, changeover) and operator acknowledgements, once the PLC handshake reports them.
- Translations of the five run.plant.* strings of Vision Studio into the other languages.
- Owner decision: should a station's plant counters restart at each shift (and from which shift calendar), and survive a restart of the program?
- Alarm catalogue entries and alarm.<code>.* strings (server, dashboard, mobile app) for camera_error, child_camera_error, parent_camera_error, station_error and pc_pressure.
- Whole-PC CPU in the resource guard's sample, so health.sample can carry cpu_pct.
