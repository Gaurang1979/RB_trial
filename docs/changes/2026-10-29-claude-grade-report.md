# Code Verify: grade report (tool spec 6.5): a PDF per graded code with its image, text, overall and parameter grades, aperture, calibration and operator

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #69
- Section 5 item: new
- Components/files: dotnet/VisionCore (Reports/MiniPdf.cs, GradeReport.cs, SymbolAnalysis.cs, ReportFiles.cs new; Tools/ToolModel.cs CodeRead; Tools/CodeTool.cs), dotnet/CodeReader (CodeQuality.Values), dotnet/VisionCore.Tests (GradeReportTests.cs new), dotnet/VisionStudio (Test page, Program.cs route, i18n/en.json), docs/vision-system/TOOL-SPECIFICATIONS.md 6.5
- Depends on: none (#68 merged)

## Section 5
- [x] (done 2026-10-29, PR #69: verifier-style PDF grade report per graded code from Vision Studio's Test page: setup and identity, overall, numeric and formal grade, pass/fail against the lowest print grade, matrix and cell size, aperture as measured, every parameter's grade and value, informative measures, the GS1 data structure; images: symbol, grade map, pixel and cell histograms, 1D scan profile; own small PDF writer; 16 tests and a Data Matrix sample graded by the engine's ISO 15415 grader) Code Verify: grade report (tool spec 6.5).

## Timeline (section 3)
| 2026-10-29 | Grade report: a verifier-style PDF per graded code from the Test page (formal grade, pass/fail, GS1 data structure, grade map, histograms, scan profile), with a sample from a Data Matrix graded by the engine | TOOL-SPECIFICATIONS.md 6.5 |

## Decisions (section 4)
| Uncalibrated grade reports | Requirements row 6 asks for a calibrated setup for grading. As tool specification 6 already says, grading still runs without one: the report is written, headed "Process control grading ... not ISO verification (no reflectance calibration)", and states the aperture actually used (X / 3 for uncalibrated 2D) | TOOL-SPECIFICATIONS.md 6, 6.5 |
| Report layout | Owner, 2026-10-29: "Check real cognex verifier report. Add missing fields and images." the DM475V Verifier Reference Manual and Cognex's white paper "Understanding Barcode Verification Results": laid out after its Verification Report, with the modulation overlay, the quiet-zone ring in the modulation table, the lighting notation (45Q) and a diagnosis per weak parameter in our own words (grade row, acceptance and data checks, numbered parameters with PASS, fixed-pattern segments, general characteristics, data detail, modulation values table, histograms with grade bands, units, sections). The engine's grades are stated as given; anything the report estimates itself (cells, segments, histograms, profile) is labelled so; the formal grade and 1D grade value are withheld until the engine hands over what they need, rather than guessed | TOOL-SPECIFICATIONS.md 6.5 |
| Own PDF writer | A small PDF 1.4 writer (standard fonts, grey and colour images, line graphs) instead of a PDF library: no new dependency for the legal review; English only until fonts are embedded for the 15 languages | TOOL-SPECIFICATIONS.md 6.5; LOCALISATION.md |
| Review before ready | Opus review found the 2D aperture and overall-grade wording wrong, 2D values missing, C1 characters printed as other symbols, long data under the grade box, the wrong job named after another was loaded, uncaught errors, and reports served to every role as downloads; all fixed with tests. No pass/fail change | TOOL-SPECIFICATIONS.md 6.5 |

## Pending
- Grade report like the verifier's in full (needs the engine C API): the fixed pattern damage transition ratios and average grade, print growth per axis, each module's value and decoded colour (so the modulation table is the engine's), the symbology identifier (FNC1 / GS1 symbol), codewords and encodation analysis, and for 1D the unrounded scan mean and the individual scan results (10 scans: edge, Rl/Rd, SC, MinEC, MOD, defects, decode, decodability, quiet zone).
- Grading setup (6.0): record wavelength, light angle and the calibration date with the setup, so every report and its formal grade carry them.
- ISO/IEC TR 29158 (AIM DPM) grading and its report: cell contrast, cell modulation, distributed damage, minimum reflectance, the 50 % and 80 % apertures and the lighting options.
- Grade report in the operator's language (embedded fonts and report.* strings, LOCALISATION.md), and the batch grade report (6.5).
