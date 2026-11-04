# OCR: format-constrained decoding in the engine and month names in other languages for MMM dates

- Status: done
- Session: Claude desktop side session ocr-format-decoding, 2026-10-30
- Pull request: #83
- Section 5 item: Month names in other languages for MMM dates
- Components/files: engine/src/ocr (format.cpp new, ocr.hpp, c_api_ocr.cpp), engine/include/vs_ocr.h (vs_ocr_set_format, vs_ocr_clear_formats, vs_ocr_line.format), engine/CMakeLists.txt, engine/tests/ocr_tests.cpp, dotnet/VisionCore (Tools/TextFormat.cs, Tools/TextTool.cs, OcrEngine.cs, OcrNative.cs), dotnet/VisionCore.Tests (TextFormatDecodingTests.cs new), docs/vision-system/TOOL-SPECIFICATIONS.md 7.4
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #83: MMM accepts month names in English, Spanish, French, German, Portuguese and Italian per tool, English by default; each Read text format is given to the OCR engine, which reads its line only with the characters the format allows at each place, for the AI and the taught-font reader; a weak allowed character, print the format has no place for and a doubled character with no gap seen all still fail; 33 engine checks and 20 .NET tests) Month names in other languages for MMM dates; format-constrained decoding in the OCR engine (lexicon-limited reading from the format).

## Timeline (section 3)
| 2026-10-30 | OCR reads each Read text line under its format (digits, letters, printed literals, day 01-31, month 01-12 or month names), for both readers, without forcing a pass; MMM dates in six languages | TOOL-SPECIFICATIONS.md 7.1, 7.4 |

## Decisions (section 4)
| Constrained reading never vouches | Under a format the engine chooses the best allowed character but scores it with its own probability, not renormalised over the allowed set: an O printed where a digit belongs reads as 0 with the 0's low score and the part is NG. Print the format has no place for (a free-read character no format character owns, or two on one) is NG as extra; two equal neighbours need a gap the recogniser saw (one wide 0 never reads as 00) | TOOL-SPECIFICATIONS.md 7.4; requirements section 12 |
| Month names by language | MMM accepts the month names of the tool's MonthLanguages (en default; es, fr, de, pt, it, with and without accents; no spelling names two months). Hindi and other Indian languages are left out: Indian packs print months in English, Hindi has no fixed short month names, and the recogniser reads Latin characters only | TOOL-SPECIFICATIONS.md 7.4; LOCALISATION.md 1 |
| A format keeps its own line | Each format takes the first unused line, top to bottom, whose free read already fits it (pass or not) or that passes under it: a weak print of the right line is never skipped for a later stray line the format happens to fit. Under a format a character passes only at 50 or more, whatever the tool's minimum quality, so it is also the recogniser's own first choice there | TOOL-SPECIFICATIONS.md 7.4; requirements section 12 |
| Review before ready | Opus review found formats skipping a weak right line for a later line that passed (a misprint unchecked), and O then o read from one wide O (equal letters compared by class, not case-folded); both fixed with tests, with the 50 floor and the time budget in the format loop | TOOL-SPECIFICATIONS.md 7.4 |
| {re:} lines stay free reads | A format with a {re:} part or a count above 64 is not given to the engine; its line is read freely and matched by its pattern on the lines the engine left, as before | TOOL-SPECIFICATIONS.md 7.4 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md 7.4: month names in other languages and format-constrained reading built (PR #83) | Claude (Mac, branch claude/ocr-format-decoding) |

## Pending
- Refuse at setup a format that can never read (a literal the reader's charset lacks, or so many {?} places that the line exceeds the decoder's size): today it is NG on every part.
- Vision Studio: a setting for the Read text tool's month-name languages (TextToolConfig.MonthLanguages, today set in the job file only) and Teach passing them to TextFormat.Propose.
- Measure format-constrained reading on real dot-print and laser dates from VS_IMAGES (ocr_bench with formats): how many lines the format rescues and that no misprint passes.
- ocr_tests "the word gap reads as a space" fails on main on this Mac (taught-font free read), unrelated to this change.
