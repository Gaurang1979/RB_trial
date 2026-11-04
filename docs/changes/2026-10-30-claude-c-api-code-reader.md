# Code Reader engine C API: cr_params struct_size, symbology identifier and GS1 flag per result, quiet zone and Rmin per scan with the grade

- Status: done
- Session: c-api-code-reader, 2026-10-30
- Pull request: #78
- Section 5 item: new
- Components/files: engine/include/codereader.h, engine/src/c_api.cpp, engine/src/decode.cpp, engine/src/linear.cpp, engine/src/grade1d.cpp, engine/src/reader.hpp, engine/CMakeLists.txt (engine_tests links c_api.cpp), engine/tests/engine_tests.cpp (testCApi), ml/engine.py, dotnet/CodeReader (Native.cs, CodeReader.cs), dotnet/CodeReader.Tests (CodeFormatTests), dotnet/VisionCore (CodeTool.Judge), dotnet/VisionCore.Tests (CodeGradeTests, Gs1SymbologyTests new)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #78: cr_params struct_size with a tag, cr_code_ext and cr_scan that grow at their end, AIM symbology identifier and GS1 flag per code from zxing and libdmtx, 1D Rmin and quiet zone per scan and as grades; the line's GS1 check fails a symbol without FNC1 first; 20 engine checks, 6 .NET test methods changed or added) Code Reader engine C API: cr_params struct_size, symbology identifier and GS1 flag per result, quiet zone and Rmin per scan with the grade.

## Timeline (section 3)
| 2026-10-30 | Engine C API: cr_params carries a tagged struct_size (an older binary's struct is refused, a newer one's extra fields ignored); cr_get_code_ext gives each code's AIM symbology identifier and GS1 flag; cr_get_scan gives each 1D scan line with its Rmin and quiet-zone results; the line names Rmin or the quiet zone as the worst parameter; the GS1 check fails a symbol without FNC1 first | TOOL-SPECIFICATIONS.md 6.2, 6.4 |

## Decisions (section 4)
| Growing C API structs | New fields only at the end of cr_params, cr_code_ext and cr_scan, each starting with struct_size; cr_code, cr_quality, cr_miss_info, cr_region and cr_stats stay fixed, and new per-code outputs go into cr_code_ext. cr_params.struct_size carries the tag 0x4352 in its upper half, because a struct from before the field starts with the format (at most 0x3FFFF), which a bare size could be mistaken for: an untagged struct makes cr_create return NULL rather than read wrong settings | requirements 12 |
| GS1 needs FNC1 first | With the GS1 check on, the code's AIM identifier goes in front of its text, so ]Q1, ]d1, ]C0 fail as "not GS1". A code read by the engine's own decoders (lattice, grid fit, known content, frames, the fitted SSCC) has no identifier: it takes the worst identifier known for its type on the same image (one printer prints them alike), and with none known it is checked on its data alone with "FNC1 not checked" in its details. A GS1 Digital Link URL (carried in a plain QR) is checked without its identifier. ECI variants (]Q4, ]d5) and GS1 Aztec fail as not GS1, on the safe side | TOOL-SPECIFICATIONS.md 6.4 |
| Review before ready | Opus review: a Digital Link QR would have failed on its ]Q1, and own-decoder reads skipped the FNC1 rule; both fixed with tests. Left as found: libdmtx cannot tell FNC1 from a literal GS as a plain symbol's first byte (a deliberately odd symbol) | requirements 12 |
| libdmtx FNC1 | libdmtx dropped every FNC1 unless told a character for it, so its reads of a GS1 Data Matrix lost the GS separators (a variable-length field ran into the next). It now outputs GS, the leading one is taken off and reported as ]d2 | TOOL-SPECIFICATIONS.md 6.4 |
| Rmin and quiet zone graded | ISO 15416 grades them per scan only; the symbol's Rmin and quiet-zone grades are the means of their scans' grades, formed like the overall grade, so the worst-parameter text names them | TOOL-SPECIFICATIONS.md 6.2 |

## Pending
- Symbology identifier from the engine's own decoders (lattice, grid fit, known content, frames, fitted SSCC), so their GS1 reads are checked for FNC1 too.
- Grade report PDF: check FNC1 with the code's symbology identifier (VisionCore CodeRead does not carry it yet).
