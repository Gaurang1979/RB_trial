# 1D grade report: stacked GS1 DataBar shown row by row, each row's grade and the overall result

- Status: done
- Session: Claude session grade-report-databar-rows, 2026-10-30
- Pull request: #130
- Section 5 item: 1D grade report (GradeReport / LinearAnalysis): stacked DataBar lists ten scans per row
- Components/files: dotnet/VisionCore (Reports/GradeReport.cs), dotnet/VisionCore.Tests (LinearReportTests.cs)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #130: the scan table groups a stacked DataBar's scans by row, ten a row with their height in the row, each row's means and grade, then the lowest row named as the symbol's grade; the overall grade, scan mean, formal grade, Rmin and quiet zone take the lowest row's; diagnosis names weak scans by row; the image's own ten-scan estimate and profiles left out for stacked) 1D grade report (GradeReport / LinearAnalysis): stacked DataBar lists ten scans per row (20 or more); show them row by row.

## Timeline (section 3)
| 2026-10-30 | Grade report: stacked GS1 DataBar row by row (each row's ten scans, means and grade; overall = lowest row, ISO/IEC 24724); the engine's scans accepted when whole rows of ten and the lowest row's mean is the overall grade; VisionCore (244) and VisionStudio (29) tests pass | 6.2, 6.5 |

## Decisions (section 4)
| Stacked DataBar in the grade report | The report follows the engine: rows of ten scans, top row first; a row's grade the mean of its scans, the symbol's the lowest row's (the first such row). The engine's scan list is used only when it is whole rows and that lowest row's mean is the overall grade; otherwise it says the list does not match, as for any 1D code | 6.5 |
| No image estimate for stacked | The report's own ten scans from the image run over the whole height and would cross the rows and separator patterns, so for a stacked DataBar the scan table is the engine's only (no per-scan quiet zones in X, no scan profiles, histogram over the symbol region) | 6.5 |
