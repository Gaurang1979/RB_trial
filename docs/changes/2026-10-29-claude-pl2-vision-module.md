# PL2 Vision module in the Python plant server: station ingest and live line state

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #57
- Section 5 item: PL2 Vision module
- Components/files: plant_server/backend/vision/ (new: events, ingest, live, api), plant_server/backend/store.py (vision tables, vision_latest), plant_server/backend/api.py (mount), plant_server/tests/test_vision_ingest.py and test_vision_live.py (new), dotnet/StationAgent/HttpPlantLink.cs (new here, from #49), dotnet/StationAgent.Tests/LinkTests.cs, mobile/plant_app (path /vision/floor), docs/vision-system/PLANT-PLATFORM.md
- Depends on: #56

## Section 5
- [x] (done 2026-10-29, PR #57: plant_server/backend/vision — ingest with device tokens, duplicates, gaps, rejects that never block a queue, conflicts answered 409, partial batches, heartbeats, live line state rebuilt at start from a small latest-state table, /vision/floor and /vision/lines scoped by role; the station agent's HTTPS link) PL2 Vision module in the Python plant server: ingest and live line state ported from #49 with its tests. Vision alarms as core alerts, and closing #49, are their own items below.
- "PI1 Station agent in Vision Studio and LineRunner": the HTTPS link to the plant server is built (HttpPlantLink to /vision/ingest/v1, https only, at most 4 MB per request); calling the agent from Vision Studio and LineRunner is still to do.

## Timeline (section 3)
| 2026-10-29 | Vision module in the Python plant server (PL2): station ingest, live line state, read API; replacing PR #49's .NET server | PLANT-PLATFORM.md 3 |

## Decisions (section 4)
| Vision on the one server | The .NET plant server of #49 is ported to plant_server/backend/vision with the same contract (the station agent is unchanged but for the path) and the same rules and tests; #49 is closed | PLANT-PLATFORM.md 3, 5 |
| Vision tables | The Vision module keeps the station agent's events as they came (vision_event, with vision_queue, vision_gap, vision_reject, vision_conflict), the last heartbeat of each station (vision_station_status) and what the live state needs from any time (vision_latest: last line state and job, open alarms). A per-part vision_result table and NG review (ng_review) come with the NG review screens, read from vision_event | PLANT-PLATFORM.md 2 |
| Station identity | A vision station is a device of kind vision_station on a line; its token names it, and every event must carry that device's plant, line and station id (403 otherwise) | PLANT-PLATFORM.md 2 |

## Update log (section 8)
| 2026-10-29 | PLANT-PLATFORM.md: the Vision module is built | Claude (cloud, branch claude/pl2-vision-module) |

## Pending
- Close PR #49 (the .NET plant server) once this pull request is merged.
- Per-part vision_result table and NG review (ng_review), with the NG review screens.
- Vision alarms as core alerts in the plant server (escalation, push and acknowledge like Production's).
- Live vision updates over the plant server's WebSocket (the dashboard and the app poll /vision/floor today).
- Vision Studio and LineRunner publish their results, line state, counters, alarms and health through the station agent.
