# Grading (Tool 6): ISO/IEC 15415 print grading for Micro QR, PDF417 and Aztec

- Status: done
- Session: grade-2d-types (claude/grade-2d-types), 2026-10-30
- Pull request: #74
- Section 5 item: new (the 2D part of the pending "Grading: DataBar (1D) and Micro QR, PDF417 and Aztec (2D)" from docs/changes/2026-10-29-claude-barcode-grading.md)
- Components/files: engine/include/codereader.h (a comment), engine/src/quality.cpp, engine/src/grade_pdf417.cpp (new), engine/src/reader.hpp, engine/src/decode.cpp, engine/CMakeLists.txt, engine/tests/engine_tests.cpp
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #74: Micro QR and Aztec graded on their module grid with their own fixed patterns, PDF417 by rows with codeword yield and codeword-level modulation, defects and decodability; engine tests on drawn symbols) Grading (2D): ISO/IEC 15415 print grading for Micro QR, PDF417 and Aztec
- "Grading: DataBar (1D) and Micro QR, PDF417 and Aztec (2D)": the 2D part (Micro QR, PDF417, Aztec) done in PR #74; DataBar stays with the grade-1d-types session.

## Timeline (section 3)
| 2026-10-30 | Code Verify grades Micro QR, PDF417 and Aztec (ISO/IEC 15415): Micro QR finder and timing, Aztec bullseye, orientation marks and full-range reference grid as fixed patterns (checked against encoder output for every Micro QR version and Aztec compact and full-range sizes); PDF417 by rows: X from the start pattern, columns from the width, rows from codeword clusters, per-row scans (Rmin, SC, ECmin, start and stop), codeword yield, and modulation, defects and decodability per codeword counted as erasures against the error correction; unused error correction from the decoder for all three. 26 new engine tests on drawn symbols (clean A, faded C, finder and bullseye damage, a timing module 0.6 module off, quiet zone speck, blotted codewords and a lost PDF417 row, turned a quarter and a half) | 6.2 |

## Decisions (section 4)
| Topic | Decision and reason | requirement sections |
| --- | --- | --- |
| UEC from the decoder | Micro QR, Aztec and PDF417 take unused error correction from zxing (from the Reed-Solomon errors it corrected), not the module-count approximation QR and Data Matrix still use. Micro QR M1 has no correction to spare and zxing does not say whether it corrected a codeword, so its UEC is not measured (-1), never assumed 1.0; likewise PDF417 when the decoder gives none | 6.2, 12 |
| PDF417 codeword validity | A codeword is valid when its eight elements are 1 to 6 modules and its ISO/IEC 15438 cluster is the row's; not checked against the codeword table, which zxing does not export on Windows. A misprint into another codeword of the same cluster is not caught by codeword yield, but it is by the decoder's UEC. Wrong row counts only make clusters mismatch, so they lower a grade, never raise it | 6.2, 12 |
| PDF417 in the 1D fields | PDF417's per-scan results go in the 1D fields of cr_quality (linear = 1: contrast, edge contrast, defects, decodability, scans) so the report and the line show them today; codeword yield is in the engine's overall grade but not yet in cr_quality (PR #78's struct_size now lets it grow) | 6.2 |
| Aztec quiet zone | Aztec needs none (ISO/IEC 24778), so it has no quiet zone grade | 6.2 |
| Grid non-uniformity search | The timing-module search looks 1 module either way (was 0.5) and takes the middle of the darkest run, not its first step: a sharp print's flat dark module read as up to 0.4 module off, and a module truly 0.6 off could not grade below B. Applies to QR and Data Matrix too | 6.2 |
| Review before ready | Opus review found the grid search window hiding shifts of 0.5 to 0.8 module, M1 forced to UEC 1.0 and PDF417 reporting UEC 0 when not measured; all fixed with tests. The PDF417 error correction count from zxing's ecLevel was checked against the encoder at levels 0 to 6 | 6.2, 12 |

## Pending
- Engine C API and .NET: PDF417's codeword yield and its grade in cr_quality, its UEC and quiet zone grades in the .NET grade list for codes reported in the 1D fields, the grade report naming ISO/IEC 15415 (not 15416) for PDF417, and a cell map for Aztec.
- Grading: QR and Data Matrix unused error correction from the decoder (zxing's UEC) instead of the module-count approximation.
- Grading: Micro QR M1 unused error correction from its own check codewords (zxing does not report a corrected M1).
- Grading: PDF417 codeword validity against the ISO/IEC 15438 codeword table (now structure and cluster only).
- Grading of Micro QR, PDF417 and Aztec on real prints from VS_IMAGES (the tests use drawn symbols).
