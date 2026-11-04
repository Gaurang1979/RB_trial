# OCV (Check text, tool spec 7.3) and dot-matrix / inkjet mode (7.5) up to the spec: per-character print quality and auto-learned tolerances

- Status: done
- Session: ocv-print-quality, 2026-10-30
- Pull request: #81
- Section 5 item: new
- Components/files: engine/src/ocr/quality.cpp (new), c_api_ocr.cpp, ocr.hpp, lines.cpp, engine/include/vs_ocr.h, engine/CMakeLists.txt, engine/tests/ocr_tests.cpp; dotnet/VisionCore (OcrNative, OcrEngine, Tools/TextTool.cs), dotnet/VisionCore.Tests/PrintQualityTests.cs
- Depends on: none

Not in this item: fixed-pitch cutting (ocr-fixed-pitch), teaching UX (ocr-teach-ux), format-constrained decoding (ocr-format-decode).

## Gap list (spec 7.3 / 7.5 against main on 2026-10-30)

Compared `docs/vision-system/TOOL-SPECIFICATIONS.md` 7.3 and 7.5 with `engine/src/ocr/verify.cpp`,
`c_api_ocr.cpp`, `engine/include/vs_ocr.h` and `dotnet/VisionCore/Tools/TextTool.cs`.

| Spec | Today | Gap |
| --- | --- | --- |
| 7.3 per-character quality "from correlation plus stroke coverage and contrast" | score = the recogniser's best probability at the aligned columns only | no contrast, no stroke / dot completeness, no position check: a faint or half-printed character the recogniser still knows passes at 90+ |
| 1, auto-tolerance mean ± k·σ over 10–30 good parts (k = 4) | nothing learned from good parts | no learned limits for print quality |
| 7.3 status OK / Missing / Broken / Wrong / Extra | OK / Unsure / Wrong / Missing; Extra only defined for free reads, never set on a checked line | extra print after the expected text is not detected (pass/fail says "no extra characters") |
| 7.3 explain keys `explain.ocv.missing / broken / wrong / extra`, 7.5 `explain.ocr.dots_missing` | one key, `explain.ocv.mismatch`, for every failed check | no per-character reason for the operator |
| 7.3 `PrintQuality` output (lowest char) | line `quality` per line; no tool-level output | small: TextTool has no PrintQuality value |
| 7.3 Allow shift (± px) | the line finder finds the line anywhere in the region | no limit on where the text may sit |
| 7.3 Font: rendered system font | AI recogniser or taught font | no rendered-font templates (needs no teaching) |
| 7.3 Auto-learn: take the per-character images as reference | the check needs no reference images (CTC alignment) | reference images replaced by learned print-quality statistics (this item) |
| 7.3 expected text from PLC or another tool (a decoded code) | job lines, batch fields, clock fields | no PLC / tool wiring of the expected text |
| 7.5 Dot size (px, learned), Join strength (auto) | dots joined by a closing sized from the character height | no learned dot size or join strength parameter |
| 7.5 Print type Inkjet | Auto / Solid / Dot | Inkjet (TIJ, solid-looking but grainy) not a separate type |
| 7.3 target ≤ 10 ms per 20-character line on T1 | not measured on T1 | measure on T1 |

## What was built

- **Print quality per character (engine, `quality.cpp`).** Every aligned character of a checked line is measured in the straightened line image: contrast (grey levels between ink and paper, in the camera image), ink (pixels in its cell: missing strokes or dots lower it, a smear raises it) and pitch (distance from the previous character's ink centre). `vs_ocr_get_char_quality` gives them with a score for each and a reason (`vs_print_reason`: unclear, faint, broken, smeared, position).
- **Limits learned from good parts.** `vs_ocr_learn_print` adds a run that passed as one good part (a part that failed is refused); limits are mean ± 4σ per character (ink, contrast) and per line position (pitch), applied from 10 good parts, with spread floors so a nearly identical print does not give razor-thin limits. A measure scores 100 at the mean and `min_quality` at its limit, so `vs_ocr_char.score` (now the lowest of recognition and print) keeps one rule: every character ≥ min quality. The reference is text kept in the job (`vs_ocr_get_print` / `vs_ocr_set_print`, `TextToolConfig.PrintReference`); a damaged one fails every part as a setup error. Before learning nothing changes for existing jobs.
- **Extra print.** A character read with confidence beyond either end of the expected text is `VS_CHAR_EXTRA` (codepoint 0, found = what was read) and fails the line (spec 7.3: "no extra characters").
- **VisionCore.** `TextTool.LearnPrint(image, batch)` / `ClearPrint()`; `OcrChar.Quality`; explain key per fault (`explain.ocv.missing`, `.wrong`, `.extra`, `.broken`, `explain.ocr.dots_missing` on dot print) and a sentence naming the character and its fault ("\"3\" in \"LOT A12345\" has dots missing (31)").

## Verified

- Engine: `ocr_tests` built (Release, macOS) after merging main with #75, #80 and #83: 17 new checks pass: measured but not scored before learning, a failed part never learned, 10 good parts learned and 10 other good parts OK, a third of one character's dots missing NOT OK (broken), one character faint NOT OK (faint), the dot print slipping two pitches NOT OK (the fixed-pitch dot reader from #80 rejects it first), the line moved on 7 px before one character NOT OK as position (scorePrint on a made-up line), extra character after and before the text NOT OK, reference saved, loaded, damaged one refused, cleared. Over 30 noise seeds, 11,220 good characters: none failed on print quality alone, after pitch scoring across word gaps was dropped (it caused 1 such failure). All earlier checks unchanged, except "the word gap reads as a space", which fails on main too.
- .NET: `dotnet build` of VisionCore.Tests and VisionStudio, 0 warnings; `PrintQualityTests` 4 new tests pass; with FontTeachTests, TextFormatDecodingTests, TextFormatTests and TaughtFontTests, 63 pass.
- Full suites: the testing session.

## Section 5
- New item: `[x] (done 2026-10-30, PR #81: per-character contrast, ink and pitch against limits learned from good parts, extra print fails, explain key per fault) OCV (Check text) print quality per character, tool spec 7.3 and 7.5`

## Timeline (section 3)
| 2026-10-30 | Check text (OCV) print quality: contrast, ink and pitch per character scored against limits learned from 10+ good parts; extra print beyond the expected text fails; an explain key and sentence per character fault | Tool spec 7.3, 7.5; requirements 5, 12 |

## Decisions (section 4)
| Check text: print quality | A character's score is the lowest of the recogniser's score and its contrast, ink and pitch scores; each print score is 100 at the learned mean and the tool's minimum quality at mean ± 4σ, so one rule (every character ≥ minimum) decides. Limits only from 10 good parts that passed; until then only the recogniser decides, so existing jobs are unchanged | Tool spec 1 (auto-tolerance), 7.3 |
| Check text: extra print fails | A character read with confidence beyond either end of an expected line makes the line NOT OK (spec 7.3), also on existing jobs: an expected line that covers only part of a printed line must now hold all of it | Tool spec 7.3; requirements 12 |

## Pending
- Vision Studio: a "Learn good part" button and print quality per character on the Check text screens (Teach page; teaching UX session), using `TextTool.LearnPrint` and `OcrChar.Quality`.
- Explain key texts for `explain.ocv.missing / wrong / extra / broken` and `explain.ocr.dots_missing` in the 15 languages (LOCALISATION.md resource files).
- Owner: should Check text fail on very low contrast before any good parts are learned (an absolute floor)? Today only the recogniser decides until 10 good parts are learned.
- Print quality limits on real CIJ, TTO and laser samples from VS_IMAGES: check the spread floors (ink 6 %, contrast 5 %, pitch 1 px) and that a one-dot-pitch slip is caught (on drawn print it scores just above the limit).
- Tool spec 7.3 parts not built: Allow shift (± px), rendered system font, expected text from the PLC or another tool, a tool-level PrintQuality output; 7.5: learned dot size and join strength, an Inkjet print type.
- Measure Check text with print quality on T1 (target ≤ 10 ms per 20-character line).
- `ocr_tests` "the word gap reads as a space" fails on main (taught-font reader), not from this change.

## Update log (section 8)
| 2026-10-30 | Change note for Check text print quality, gap list of tool spec 7.3 / 7.5 against the code | Claude (Mac, branch claude/ocv-print-quality) |
