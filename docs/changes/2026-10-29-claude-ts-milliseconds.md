# Plant events: a ts must be in milliseconds

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #<n>
- Section 5 item: PI1 Plant event schema v1
- Components/files: dotnet/PlantEvents/PlantEvent.cs, dotnet/PlantEvents.Tests/PlantEventTests.cs
- Depends on: none

## Section 5
- Update 2026-10-29: Validate refuses a ts past 9999-12-31 (a time in micro- or nanoseconds), which made the plant server's batch fail and blocked the station's queue (found in the plant server review). It reached #48's branch after #48 was merged; this carries it to main.

## Timeline (section 3)
| 2026-10-29 | Plant events: a ts must be in milliseconds (38 schema tests) | PLANT-DASHBOARD-AND-MOBILE.md 3 |
