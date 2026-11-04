# Show why a line failed as cut short ("text may go on past this line") in Vision Studio

- Status: done
- Session: ocr-cut-short-reason (Claude, Mac), 2026-10-30
- Pull request: #131
- Section 5 item: Show why a line failed as cut short
- Components/files: engine C API (engine/include/vs_ocr.h: vs_ocr_line_ext.cut_short, additive), engine/src/ocr/c_api_ocr.cpp, engine/src/ocr/lines.cpp (cut-short edge rule), engine/tests/ocr_tests.cpp, dotnet/VisionCore (OcrNative.cs, OcrEngine.cs, Tools/TextTool.cs), dotnet/VisionCore.Tests, dotnet/VisionStudio/i18n/en.json
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #131: `vs_ocr_line_ext.cut_short` (added at the end; the older struct size is still served); `OcrLine.CutShort`; Vision Studio gives a cut-short line the NG reason "The text may go on past …" and the explain key `explain.ocr.cut_short`, in Check text and in Read text format checks; the line finder now also marks a line cut short when what is left of a letter the region's edge cuts touches the edge) Show why a line failed as cut short ("text may go on past this line") in Vision Studio: needs a field in vs_ocr_line_ext and the .NET mirror.

## Timeline (section 3)
| 2026-10-30 | OCR: a line cut short (PR #107) now says so. The engine sets `vs_ocr_line_ext.cut_short` (a new last field; `vs_ocr_get_line_ext` still serves callers built with the older struct size). VisionCore mirrors it as `OcrLine.CutShort`. Text tool NG reason: "The text may go on past "…": the area's edge cuts it, or more print is in line with it. Check the area and the print", explain key `explain.ocr.cut_short` (English text in Vision Studio). Line finder fix found by the new real-engine test: a region edge through the last letter left a piece too small to count as a letter, which joined the line's end unchecked; "LOT A123" with the area cut through the 3 read "LOT A12E" and passed a check for LOT A12. Every part of a line on the region's edge now marks it cut short. Verified with ocr_tests (testCutShort) and the VisionCore and VisionStudio tests. | 9, 12 |

## Decisions (section 4)
| OCR: cut short is the reason given first | For a found line cut short, `explain.ocr.cut_short` is given before missing, wrong or extra characters (only "line not found" and a character the font does not know come first). A missing, wrong or extra character on such a line is often only the cut, so the operator fixes the area or the print first. The line is NG either way. | 12 |
| OCR: any part of a line on the region's edge cuts it short | Not only letters: what is left of a letter the edge cuts is often too small to count as one and joins the line as a faint end, so the line could pass a shorter expected text (a wrong lot). Price: a speck in line with the text at the region's edge makes the line NG. | 12 |
| C API: an added struct field keeps older callers | `vs_ocr_get_line_ext` accepts `struct_size` = sizeof or the size before `cut_short`, and copies only that much. Any other size is refused. A host built against the older header keeps working with a newer engine. | 9 |

## Update log (section 8)
| 2026-10-30 | Change note for the OCR cut-short reason in Vision Studio. | Claude (Mac, branch claude/ocr-cut-short-reason) |

## Pending
- Explain text for `explain.ocr.cut_short` in the other 14 languages (owner 2026-10-30: English only for now).
