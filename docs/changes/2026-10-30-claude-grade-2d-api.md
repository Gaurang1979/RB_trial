# Engine C API and .NET for the 2D grades: PDF417 codeword yield, UEC and quiet zone in the grade list, ISO/IEC 15415 report for PDF417, Aztec cell map

- Status: done
- Session: grade-2d-api (claude/grade-2d-api), 2026-10-30
- Pull request: #98
- Section 5 item: new (the first Pending line of docs/changes/2026-10-30-claude-grade-2d-types.md, "Engine C API and .NET: PDF417's codeword yield")
- Components/files: engine/include/codereader.h, engine/src/c_api.cpp, engine/tests/engine_tests.cpp, dotnet/CodeReader (Native.cs, CodeReader.cs), dotnet/CodeReader.Tests/CodeFormatTests.cs, dotnet/VisionCore/Reports (GradeReport.cs, SymbolAnalysis.cs), dotnet/VisionCore.Tests/GradeReportTests.cs
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #98: cr_code_ext gains codeword_yield, grade_codeword_yield, grid_rows, grid_cols; PDF417's .NET grade list has codeword yield, UEC and quiet zone; the grade report names ISO/IEC 15415 for PDF417; Aztec gets a cell map and modulation table without a quiet zone ring) Engine C API and .NET: PDF417's codeword yield and its grade, its UEC and quiet zone grades in the .NET grade list, the grade report naming ISO/IEC 15415 for PDF417, and a cell map for Aztec

## Timeline (section 3)
| 2026-10-30 | C API and .NET for the 2D grades: cr_code_ext grows at its end (struct_size, PR #78) with PDF417's codeword yield and its grade, and the module grid the engine graded (rows, columns) for QR, Micro QR, Data Matrix and Aztec. .NET: PDF417's grade list is symbol contrast, modulation, edge contrast, defects, decodability, codeword yield, unused error correction and quiet zone (the 1D Decode-per-scan and Rmin rows left out); CodeQuality.GridRows / GridCols. Grade report: PDF417 laid out as an ISO/IEC 15415 symbol (rows scanned with start and stop, the overall rule stated, codeword-yield advice), no 1D scan analysis; the cell map takes the engine's grid first, Aztec sizes added to the estimate (compact 15-27, full-range with reference grid lines), and the modulation table leaves out the quiet zone ring for Aztec (ISO/IEC 24778 needs none). Engine test decodes real PDF417 and Aztec symbols (zxing-cpp's writer) through the C API | 6.2 |

## Decisions (section 4)
| Topic | Decision and reason | requirement sections |
| --- | --- | --- |
| Codeword yield in cr_code_ext, not cr_quality | cr_quality is fixed since PR #78; the yield and its grade go at the end of cr_code_ext, -1 for every type but PDF417. The engine's overall grade already counts it, so nothing on the line changes | 6.2 |
| Grid from the engine | The engine hands over the decoder's module grid (zxing's sampled symbol) instead of the report guessing it from corners and module size; checked with zxing-cpp that Aztec's grid is the whole symbol between its outer corners. PDF417 has none (zxing gives no grid), so it has no cell map | 6.2 |
| PDF417 report as 2D | PDF417 comes in the 1D fields of cr_quality, but its report follows ISO/IEC 15415 (multi-row): the 1D ten-scan analysis, Rmin and per-scan quiet zone rows do not apply. Decode is the symbol's (it decoded); a row without start and stop is already in the row scans' mean grade | 6.2 |

## Pending
- Code tool: allow a lowest print grade for Micro QR, PDF417 and Aztec (CodeTool.SetupProblem still says "not graded yet", although the engine grades them since PR #74); a line-safety decision for the owner.
- Grade report for PDF417: codeword-level modulation map (the engine grades per codeword but does not hand the codewords over).
