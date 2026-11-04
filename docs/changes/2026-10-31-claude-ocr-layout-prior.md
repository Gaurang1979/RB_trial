# Text-line layout prior ("2 2 2-4") and a tight-spacing setting for dot print (rank 15)

- Status: done
- Session: ocr-layout-prior (Claude, Mac), 2026-10-31
- Pull request: #160
- Section 5 item: Text-line layout prior ("2 2 2-4") and a tight-spacing setting for dot print
- Components/files: engine/src/ocr (layout.cpp new, lines.cpp, font.cpp, c_api_ocr.cpp, ocr.hpp), engine/include/vs_ocr.h, engine/CMakeLists.txt, engine/tests/ocr_tests.cpp, tools/bench/ocr_bench.cpp, dotnet/VisionCore (OcrEngine, OcrNative, Tools/TextTool.cs), dotnet/VisionCore.Tests (TextLayoutTests.cs new), dotnet/VisionStudio/i18n/en.json, docs/vision-system/study/04c-TOOLS-TEXT.md
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #160: layout "2 2 2-4 | 6" set in the job or learned at Teach; finder joins split lines by it; Read text lines read with its count; a block that does not fit is NG with the reason; tight dot spacing for CIJ. Laser OCV 92 → 101 / 150 and 125 → 140 / 200 with the layout, Harpic unchanged 469 / 475, 0 misprints accepted) Text-line layout prior ("2 2 2-4") and a tight-spacing setting for dot print (rank 15)

## Timeline (section 3)
| 2026-10-31 | OCR text-line layout prior and tight dot spacing (PR #160). Layout per line as character groups ("2 2 2-4", lines apart by "\|"), set in the Read/Check text tool (TextToolConfig.Layout) or learned from a good part (TextTool.LearnLayout). The line finder joins two pieces of one line in one band when more lines are found than the layout lists; Read text lines are decoded with the layout's count (a format of any characters) and must fit it (count; each group when every word gap is seen); a layout line not found or not fitting fails the part with the reason (vs_ocr_layout_reason, explain.ocr.layout). Tight spacing (vs_ocr_set_tight_spacing, TextToolConfig.TightSpacing): dots joined along the line only within half a dot; the taught font finds the pitch on unjoined dots and melts less along the line. Measured (ocr_bench, check against truth, layout from each image's truth): synthetic laser 150 OCV 92 → 101, characters 94.14 → 96.62 %; laser 200 OCV 125 → 140, 94.57 → 97.06 %; CIJ 150 OCV 7 → 7, 67.49 → 68.44 % (tight on: 68.85 %); real Harpic 475: 469 → 469, 99.96 %; one character changed accepted 0 everywhere. Without a layout every set reads exactly as main. Engine tests: tight dot print read 5 / 5 against 0 / 5 without | 7.4, 7.5, 12 |

## Decisions (section 4)
| OCR layout prior | Opt-in per tool, never derived on its own from the expected text: with no layout every line is found and read exactly as before (measured identical on all sets). The finder joins only pieces side by side in one band, the narrowest gap first, only while more lines than the layout's are found, and only up to its longest line's letters; so a layout listing as many lines as are found changes nothing. A read that does not fit fails the part (NG with the reason), it never shortens or pads the text to fit. Fewer word gaps seen than the layout's groups check only the count (a missed gap proves nothing); more gaps fail | 7.4, 12 |
| Tight dot spacing | A setting, off by default (normal CIJ has a dot column between characters, and joining across it is what lets missing dots be bridged). Not learned yet: measuring the character gap against the dot gap at Teach is a follow-up | 7.5 |

## Update log (section 8)
| 2026-10-31 | study 04c 7.4.6 status Partial and the tight-spacing row (vs_ocr_set_tight_spacing); vs_ocr.h documents the layout and tight-spacing calls | Claude (Mac, branch claude/ocr-layout-prior) |

## Measured (ocr_bench, AI reader, budget 5000 ms)

Check against truth with `--mutate` (main → branch with no layout → branch with the layout from each image's truth; Harpic with `--layout "1 6 6 | 1 4 3 2 | 1 6 1 5 5"`):

| Set | OCV pass | Characters (free read) | Lines exact | One character changed accepted |
| --- | --- | --- | --- | --- |
| laser 150 (seed 31) | 92 → 92 → 101 | 94.14 → 94.14 → 96.62 % | 257 → 257 → 267 / 311 | 0 / 150 each |
| laser 200 (seed 31) | 125 → 125 → 140 | 94.57 → 94.57 → 97.06 % | 338 → 338 → 354 / 411 | 0 / 200 each |
| CIJ 150 | 7 → 7 → 7 (tight 7) | 67.49 → 67.49 → 68.44 % (tight 68.85 %) | 47 → 47 → 47 / 287 | 0 / 150 each |
| TTO 150, embossed 150 (no layout) | 97, 17 → same | 96.30, 47.33 % → same | same | 0 |
| Harpic real 475 | 469 → 469 → 469 | 99.96 % each | 1420 / 1425 each | 0 / 475 each |

Read text (`--read-only`) with the layout: characters laser 150 92.46 → 95.84 %, laser 200 92.99 → 96.63 %, CIJ 63.07 → 67.96 % (tight 68.85 %), Harpic 98.64 → 99.95 %; lines exact laser 244 → 273, laser 200 322 → 365, CIJ 43 → 48 (tight 51), Harpic 1314 → 1419. Wrong lines that still score OK: main's free read had 14 / 16 / 2 / 9 at or above min quality (laser 150 / laser 200 / CIJ / Harpic); read under the layout 9 / 11 / 1 / 3. Each is a character misread (O for 0, / for 7, b for 6) of the right count, which the layout cannot see; two readers agreeing (rank 14) is the fix.

## Pending
- Tight dot spacing learned at Teach (character gap against the dot gap, with vs_ocr_measure_dots) instead of set by hand
- Layout prior: split two lines the finder joined (tall line against the layout's line count), and line-height and left-alignment roles learned at Teach
- Vision Studio Teach page: a Layout field and a Learn layout button for the Read/Check text tool (TextTool.LearnLayout)
