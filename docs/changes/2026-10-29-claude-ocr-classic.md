# OCR: classical OCR with a taught font (tool spec 7.2): read text without the AI model, teach characters from sample print

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #62
- Section 5 item: new
- Components/files: engine/include/vs_ocr.h (vs_reader, vs_ocr_params.reader, font teach/get/set/clear), engine/src/ocr (font.cpp new, ocr.hpp, c_api_ocr.cpp), engine/CMakeLists.txt, engine/tests/ocr_tests.cpp, dotnet/VisionCore (OcrNative, OcrEngine, Tools/TextTool.cs), dotnet/VisionCore.Tests (TaughtFontTests.cs new), dotnet/VisionStudio (Teach page, i18n/en.json), docs/vision-system/TOOL-SPECIFICATIONS.md 7.2
- Depends on: #61 (merged)

## Section 5
- [x] (done 2026-10-29, PR #62: second OCR reader from a font taught from sample print, sharing reading, checking and date fields with the AI; solid and dot print; wrong teaching texts refused; font kept in the job; Teach page; 21 engine checks, 3 .NET tests) OCR: classical OCR with a taught font (tool spec 7.2): read text without the AI model, teach characters from sample print.

## Timeline (section 3)
| 2026-10-29 | Classical OCR from a taught font: read and check text without the AI model (fixed fonts, very fast lines, PCs without AI acceleration), taught line by line on the Teach page | TOOL-SPECIFICATIONS.md 7.2 |

## Decisions (section 4)
| Taught font shares the AI's back end | The taught-font reader writes the same per-column character probabilities as the AI recogniser, so the expected-text check, TIME / DATE fields, character boxes and Read-mode formats work the same with either reader | TOOL-SPECIFICATIONS.md 7.2 |
| Close characters fail | A character scores by its fit times its lead over the next-best character (0.10 correlation for full marks): characters as alike as a font's O and 0 score low and a check fails, rather than passing on noise; no character is ever dropped from the read; texture and the time budget stop reading with nothing read | Requirements 12 |
| Review before ready | Opus review found O read for 0 at a passing score, an untaught m passing as r n, characters dropped at the line's end in Read mode, a texture taking seconds per line, teaching accepting a text with a character too many or shifted, and exceptions able to cross the C API; all fixed with tests | TOOL-SPECIFICATIONS.md 7.2 |

## Pending
- Classical OCR: measure the time per line on the T1 PC (5 to 10 ms per 10-character line on the development PC).
- Classical OCR: ask only about the characters it is unsure of while teaching, "Teach this character" from a run image, and explain.ocr.unknown_char.
- Classical OCR on dot print with several missing dots per character: fixed-pitch cutting (CIJ prints at a fixed pitch); today such characters score low and the check fails.
- Line finder: a line of mostly diagonal letters (V W X Y Z /) makes the slant estimate run to max_skew_deg.
