# Classical OCR teaching: ask only about unsure characters, "Teach this character" from a run image, explain.ocr.unknown_char

- Status: done
- Session: ocr-teach-ux, 2026-10-30
- Pull request: #75
- Section 5 item: Classical OCR: ask only about the characters it is unsure of
- Components/files: engine/include/vs_ocr.h (VS_CHAR_UNKNOWN, vs_ocr_teach_char), engine/src/ocr (font.cpp, ocr.hpp, c_api_ocr.cpp), engine/tests/ocr_tests.cpp, dotnet/VisionCore (OcrNative, OcrEngine, Tools/TextTool.cs, Tools/FontTeach.cs new), dotnet/VisionCore.Tests (FontTeachTests.cs new), dotnet/VisionStudio (Teach page, i18n/en.json, app.css), docs/vision-system/TOOL-SPECIFICATIONS.md 7.2
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #75: the Teach page reads a line with the font taught so far and asks only about the characters it is unsure of, with every character still editable; "Teach this character" adds one character from a tried picture, NG or grabbed; characters never taught get the status Unknown and explain.ocr.unknown_char; font character boxes cover the whole character; 9 engine checks, 5 .NET tests) Classical OCR: ask only about the characters it is unsure of while teaching, "Teach this character" from a run image, and explain.ocr.unknown_char.

## Timeline (section 3)
| 2026-10-30 | Classical OCR teaching: the font reads a new line and the engineer types only the characters it is unsure of; "Teach this character" adds a character from a tried NG or live picture; characters never taught are named as such (explain.ocr.unknown_char) | TOOL-SPECIFICATIONS.md 7.2 |

## Decisions (section 4)
| Every character stays editable while teaching | A character never taught can read as a taught one with confidence (the test font's 8 read as B, sure), so the line read by the font is shown whole; only the unsure characters are left empty, but the engineer checks and can change all of them | Requirements 12 |
| Teach this character is never pre-filled | The expected character of a failed check is shown, not filled in: a misprint taught as the expected character would let that misprint pass from then on. A character that clearly looks like another taught one is refused | Requirements 12 |
| Unknown is a reason, not a verdict | VS_CHAR_UNKNOWN only relabels characters already below OK (expected character never taught; or read, unsure and like no taught character); pass and fail are unchanged | TOOL-SPECIFICATIONS.md 7.2 |

## Pending
- Classical OCR: "the word gap reads as a space" engine check fails on origin/main on the macOS development PC (OpenCV 5); found while testing PR #75, not caused by it.
- Classical OCR: a link from the Run page's NG image list to the Teach page with that picture loaded (today the engineer loads the NG image's path on the Teach page).
- Translations of the new text.font_* strings and explain.ocr.unknown_char once more languages than English exist in dotnet/VisionStudio/i18n.
