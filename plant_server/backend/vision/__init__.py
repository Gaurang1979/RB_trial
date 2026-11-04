"""Vision module of the plant server (PL2; docs/vision-system/PLANT-PLATFORM.md section 3).

Vision stations (dotnet/StationAgent on the line PC) send plant events v1 as HTTPS JSON batches with
their device token. The rules are the ones the .NET plant server of PR #49 had, ported with its tests:
duplicates, gaps, rejects that never block a station's queue, conflicts answered 409, partial batches,
and the live state of every line rebuilt at start.
"""
