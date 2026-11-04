# Code Verify: 1D print grading for every 1D type (ISO 15416 decodability per symbology) and a minimum grade on the Read code tool

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #68
- Section 5 item: new
- Components/files: engine/src/decodability.cpp (new), engine/src/grade1d.cpp, engine/src/reader.hpp, engine/include/codereader.h (comment), engine/CMakeLists.txt, engine/tests/engine_tests.cpp, dotnet/CodeReader (CodeQuality.Grades), dotnet/VisionCore (CodeTool), dotnet/VisionCore.Tests (CodeGradeTests.cs new), dotnet/VisionStudio (Teach page, i18n/en.json), docs/vision-system/TOOL-SPECIFICATIONS.md 6.2
- Depends on: #67 (merged)

## Section 5
- [x] (done 2026-10-29, PR #68: ISO 15416 grading of every 1D type but DataBar with its own decodability measure, true X under print gain, quiet zones per type, scans limited to the symbol; "Lowest print grade" on the Read code tool, NG naming the worst parameter, NG when not gradable; 15 engine grading checks run against zxing 2.2 here, 7 .NET tests) Code Verify: 1D print grading for every 1D type (ISO 15416 decodability per symbology) and a minimum grade on the Read code tool.

## Timeline (section 3)
| 2026-10-29 | Print grading of every 1D type (EAN-8, UPC-A/E, Code 39, Code 93, ITF, Codabar added to Code 128 and EAN-13) and a lowest print grade on the Read code tool | TOOL-SPECIFICATIONS.md 6.2 |

## Decisions (section 4)
| Grade in doubt is NG | With a lowest print grade set, a code that cannot be graded fails; a scan that cannot measure decodability counts 0; types not graded yet (DataBar, Micro QR, PDF417, Aztec) refuse a lowest grade rather than stop every part | TOOL-SPECIFICATIONS.md 6.2 |
| Review before ready | Opus review found two-width decodability rating an element on the threshold B, X shrinking under print gain (a short quiet zone grading A), unmeasured scans left out of the mean, one quiet zone for all types, the wrong parameter named, a low grade hiding a wrong label, any trailing character accepted as the check digit; all fixed with tests. Running the grader end to end also found the carton beyond a label breaking the per-scan decode and an edge finder that missed crossings landing exactly on the threshold | TOOL-SPECIFICATIONS.md 6.2 |

## Pending
- Grading: DataBar (1D) and Micro QR, PDF417 and Aztec (2D).
- Grading: the EAN/UPC reference-decode bar-width check for characters 1, 2, 7 and 8 (ISO/IEC 15420).
- Engine C API: report each scan's quiet-zone and Rmin results with the grade, so the line names them as the worst parameter.
- Engine tests of the grading (engine_tests testGrading) on the Windows build: run here only against zxing 2.2 with a stand-in type map.
