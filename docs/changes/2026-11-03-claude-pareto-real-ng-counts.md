# Run screen Pareto from real NG counts, and the job bundle round-trip test (Req 6.1 RUN-PAR-01, RUN-PAR-02)

- Status: done
- Session: claude (pareto-real-ng-counts), 2026-11-03
- Pull request: #266
- Section 5 item: new
- Components/files: dotnet/VisionStudio (Run.razor, Services/ToolingDiagnostics.cs, i18n/en.json), dotnet/VisionStudio.Tests, dotnet/VisionCore.Tests/JobTests.cs, docs/REQUIREMENTS-VISION-SYSTEM.md (6.1)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #266: Run screen Pareto card fed from the NG image store's per-reason counts, empty state with no NG, sample numbers removed; JobStore bundle round-trip test) Req 6.1 RUN-PAR-01 and RUN-PAR-02: shift defect Pareto from real counts, and the job bundle round-trip test (follow-up to the closed PR #239).

## Timeline (section 3)
| 2026-11-03 | Run screen "Shift Defect Modes (Pareto)" shows the real NG counts per reason with % of NG parts and the NG total, or "No NG parts in this batch yet."; `ToolingDiagnosticsCalculator.CreateSamplePareto` (fixed sample numbers with engine names) replaced by `ParetoFromCounts`; `JobStoreBundleTests` round trip added from PR #239. VisionStudio.Tests 85/85, JobStoreBundleTests and JobTests pass. | 6.1 RUN-PAR-01, RUN-PAR-02 |

## Decisions (section 4)
| Pareto share | Each reason's share is of the NG parts (`NgCounts.Ng`), not of the sum of reasons: a part NG for two reasons counts under both, so shares can add up to more than 100%; a share never passes 100%. | 6.1 RUN-PAR-01 |

## Update log (section 8)
| 2026-11-03 | Requirements 6.1: added RUN-PAR-01 and RUN-PAR-02. | Claude (Mac, branch claude/pareto-real-ng-counts) |

## Pending
- Run screen "Live NG Filmstrip" card still shows four fixed sample entries ("Void", "Burr", "OCR", "Skew"); feed it from the latest saved NG images (Req 6.1) or show an empty state.
- VisionCore.Tests `SecureAssetVaultTests.SecureAssetVault_TamperedPayload_ThrowsCryptographicException` fails on main on this Mac (the exception type does not match exactly); look into it.
