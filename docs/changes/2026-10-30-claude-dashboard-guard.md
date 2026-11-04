# Dashboard (aggregation station): use the resource guard and NgImageStore

- Status: done
- Session: dashboard-guard, 2026-10-30
- Pull request: #129
- Section 5 item: Dashboard (aggregation station): use the resource guard and NgImageStore
- Components/files: dotnet/Dashboard (Station.cs, HardCases.cs new, Options.cs, Program.cs, Live.razor, csproj, appsettings.json), dotnet/Dashboard.Tests (new), dotnet/StationGuard/NgImageStore.cs (DateFolders option, caller fields in the .json), dotnet/StationGuard.Tests/NgImageStoreTests.cs, README.md, .github/workflows/windows-build.yml
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #129: ResourceGuard and NgImageStore in the Dashboard; hard cases written off the inspection thread to data/hard-cases/<product>/ with the retraining sidecar; PC warning on the Live page and GET /api/health; verdicts never changed) Dashboard (aggregation station): use the resource guard and NgImageStore; it still saves hard cases on the inspection thread.

## Timeline (section 3)
| 2026-10-30 | Aggregation station Dashboard on the line PC guard: memory, PC memory and disk watched (MemoryBudgetMb); every case counted by reason (codes_missing, no_parent, parent_invalid, duplicate); short cases kept through NgImageStore off the inspection thread, a few or none under pressure; the Live page, the audit trail and GET /api/health tell the station; a case not saved stays NG. NgImageStore: DateFolders option and caller fields in the .json. Dashboard.Tests (9) new | 12 |

## Decisions (section 4)
| Dashboard hard-case folder | NgImageStore writes the Dashboard's hard cases to data/hard-cases/<product>/ (DateFolders off) with format and expected count in each .json, so ml/retrain.py --hard and the bench's --frames naming keep working unchanged; HardCaseLimit now counts all products together, with HardCasesMaxGb (10 GB) as a second limit | 12 |
| Dashboard under pressure | The verdict is decided before an image is offered and never depends on it: under High or Critical pressure, or with an unusable folder, the case is still counted (NG stays NG), the image is skipped, and the Live page says so | 12 |

## Update log (section 8)
| 2026-10-30 | README: Dashboard hard cases off the inspection thread, the guard's settings and GET /api/health; Dashboard.Tests in the layout row | Claude (Mac, branch claude/dashboard-guard) |

## Pending
- Dashboard: soak on a line PC (memory and disk over a long run with real cameras; hard cases still reach the retraining agent).
