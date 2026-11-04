# PI1 Plant event schema v1 and the station agent with store-and-forward

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #48
- Section 5 item: PI1 Plant event schema v1
- Components/files: dotnet/PlantEvents, dotnet/PlantEvents.Tests, dotnet/StationAgent, dotnet/StationAgent.Tests, .github/workflows/windows-build.yml, README.md, docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md
- Depends on: the project kit pull request (branch claude/startup-kit)

## Section 5
- [x] (done 2026-10-29, PR #48: dotnet/PlantEvents, ten kinds, queue_id with seq, snake_case JSON with kind first, order-free reading, unknown kinds, other schemas, numeric enums and missing required fields refused, finite numbers only; 37 tests) PI1 Plant event schema v1 (inspection.result, line.state, counter.snapshot, alarms, job.changed, model.deployed, health.sample, heartbeat) as a shared contract with tests.
- "PI1 Station agent in Vision Studio and LineRunner": the agent library is built (dotnet/StationAgent: SQLite queue, gap-free sequence kept across restarts, batches acknowledged by the server, retry from 1 s doubling to 60 s, 7-day retention with dropped events counted, heartbeat with the last sequence; acknowledgement above the batch refused, a time-out on every call, unsent events dropped only after the server was seen away for 7 days on the monotonic clock, pruning in small pieces, queue file bound to one station; Publish never throws; 21 tests). Still to do: call it from Vision Studio and LineRunner, and the gRPC link to the plant server.

## Timeline (section 3)
| 2026-10-29 | Plant event schema v1 (dotnet/PlantEvents) and the station agent library with store-and-forward (dotnet/StationAgent); 58 unit tests | PLANT-DASHBOARD-AND-MOBILE.md 3 |

## Decisions (section 4)
| Heartbeat and sequence | A heartbeat is not stored and carries the last sequence number given out, so the queue does not fill with heartbeats during an outage and the server still knows how far behind it is; unsent events older than 7 days are dropped, counted and seen by the server as a gap | PLANT-DASHBOARD-AND-MOBILE.md 3 |
| Agent never stops the line | Publish only writes to the local queue and returns false instead of throwing; the caller shows a station alarm when PublishFailures grows | PLANT-DASHBOARD-AND-MOBILE.md 2 |

| Review before ready | Opus review found eight problems (Publish could wait seconds behind a prune; an acknowledgement above the batch marked events sent; retention used the event's own time; NaN values lost events; no call time-out; numeric enums, nulls and missing required fields read; weak tests; no station binding); all fixed with tests | PLANT-DASHBOARD-AND-MOBILE.md 3 |

## Update log (section 8)
| 2026-10-29 | PLANT-DASHBOARD-AND-MOBILE.md section 3: wire form, sequence and delivery rules; README layout rows | Claude (cloud, branch claude/pi1-event-schema) |

## Pending
- gRPC link from the station agent to the plant server (TLS, station certificate), the IPlantLink implementation.
