# Grade report for 1D codes after ISO/IEC 15416 verifier reports: overall grade with aperture, wavelength and angle, ten scan profiles with their grades, per-scan parameter table, symbol means, X-dimension, growth, quiet zones, decoded data, GS1 checks and diagnosis

- Status: done
- Session: grade-report-1d, 2026-10-30
- Pull request: #87
- Section 5 item: new
- Components/files: dotnet/VisionCore (Reports/LinearAnalysis.cs new, Reports/GradeReport.cs), dotnet/VisionCore.Tests (LinearReportTests.cs new, GradeReportTests.cs), docs/vision-system/TOOL-SPECIFICATIONS.md (6.5)
- Depends on: #78 (merged: CodeQuality.ScanLines)

## Section 5
- [x] (done 2026-10-30, PR #87: per-scan table with the symbol means, ten scan reflectance profiles with their grades, Rmin and quiet zone rows, measured X, bar width growth, symbol width and bar height, quiet zones against the type's minimum, check digit, per-scan diagnosis, formal grade from the scan mean) Grade report for 1D codes after ISO/IEC 15416 verifier reports, as detailed as the 2D one

## Timeline (section 3)
| 2026-10-30 | 1D grade report after ISO/IEC 15416 verifier reports. Ten scan lines measured from the image as the engine measures them, with the bar height found from the image. A per-scan table (Rmin, Rmax, SC, ECmin, MOD, defects, decodability, decode, quiet zones in X, grade) with the symbol means and their grades. Ten profile charts, each with its grade and lowest parameter. X, bar width growth, symbol width and height, quiet zones against the type's minimum, the check digit, and a diagnosis of which scans are weak and where. The formal grade comes from the mean of the engine's scan grades (CodeQuality.ScanLines, PR #78). Verified on a synthetic EAN-13 (clean, grown, a mark in the quiet zone, a spot at the top). | 6.5, 12 |

## Decisions (section 4)
| 1D scan figures | The report measures the ten scans from the image with a C# port of the engine's per-scan measurement (grade1d.cpp, without decodability and decode). This gives the profiles, which the engine does not hand over, and the quiet zones in X. Decodability, decode and the scan grades are the engine's alone: the report takes them from CodeQuality.ScanLines (#78). Without them each grade is printed as "at most", because decodability and decode are left out. A grade in doubt never looks better than it is. | 6.5, 12 |
| Review before ready | An Opus review found the port matches grade1d.cpp line for line. It also found problems, all fixed. The bar height ran past the image's edge. A quiet zone cut off by the image's border passed: it is now measured only to the edge. Decodability that a scan could not measure was left out of the mean: it now counts 0, as in the engine. The diagnosis gave the wrong scan numbers. A partial or inconsistent engine scan list was trusted: it is now used only when it has one scan per line and its mean is the overall grade. Growth was wrong for two-width codes. The scan table could run into the footer. Engine scans were dropped when the image analysis failed. | 6.5, 12 |
| 1D formal grade | The mean of the engine's scan grades, truncated to one decimal as a verifier does, then aperture in mil / wavelength / angle (e.g. 3.5/10/660/45Q). Only with a reflectance calibration, a mm scale and the setup recorded, and only when the engine's scans are handed over. | 6.5 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS 6.5: the 1D report's content; the formal grade for 1D from the scan mean; "the 1D individual scan results" removed from "Not yet" | Claude (Mac, branch claude/grade-report-1d) |

## Pending
- 1D report for GS1 DataBar, once the engine grades it (PR #82): its scan lines per row and its finder patterns.
- Engine 1D quiet zone at the image's border: grade1d.cpp samples with BORDER_REPLICATE, so a quiet zone cut off by the image's edge can grade clear. Measure it only inside the image and grade it F when it is too short (line safety). The report already does this.
