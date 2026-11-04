# .NET CodeReader: refused EAN / add-on counts in DecodeStats, SetLens reports a refused lens

- Status: done
- Session: claude dotnet-refused-lens, 2026-11-03
- Pull request: #290
- Section 5 item: new (Pending of docs/changes/2026-11-03-claude-engine-stats-lens-threads.md)
- Components/files: dotnet/CodeReader (Native.StatsExt, CodeReader.cs DecodeStats, SetLens, GradingCalibration.ApplyTo), dotnet/CodeReader.Tests; docs/REQUIREMENTS-VISION-SYSTEM.md 17.1 (ENG-06)
- Depends on: none (engine PR #280 merged)

## Section 5
- [x] (done 2026-11-03, PR #290: DecodeStats.RefusedEan / RefusedAddon from the grown Native.StatsExt; SetLens throws InvalidOperationException for a refused lens; GradingCalibration.ApplyTo returns the reason) .NET CodeReader: mirror cr_stats_ext.refused_ean and refused_addon, and make SetLens report the -1 of a refused lens
- Dashboard Station (Station.cs, two ApplyTo calls) ignores the string ApplyTo now returns: show a refused stored lens to the operator
- A test with a real misread EAN / add-on image that gives RefusedEan / RefusedAddon above 0 through the .NET reader (the engine test covers the counts)
