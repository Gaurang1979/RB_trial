# Check text (tool spec 7.3) Allow shift, expected text from the PLC or another tool, tool-level PrintQuality; dot-matrix / inkjet mode (7.5) learned dot size and join strength, Inkjet print type

- Status: done
- Session: ocv-shift-plc-inkjet, 2026-10-30
- Pull request: #93
- Section 5 item: new (Pending in docs/changes/2026-10-30-claude-ocv-print-quality.md: "Tool spec 7.3 parts not built")
- Components/files: engine/include/vs_ocr.h, engine/src/ocr (c_api_ocr.cpp, quality.cpp, lines.cpp, font.cpp, ocr.hpp), engine/tests/ocr_tests.cpp, tools/bench/ocr_bench.cpp; dotnet/VisionCore (OcrNative, OcrEngine, Tools/TextTool.cs, Tools/ToolModel.cs, Jobs/Job.cs), dotnet/VisionStudio/Services/Station.cs (PLC values only), dotnet/VisionCore.Tests/ExpectedTextSourceTests.cs; docs/vision-system/TOOL-SPECIFICATIONS.md 7.3, 7.5
- Depends on: none

## What was built

- **Expected text from the PLC or another tool.** A Check text line takes its expected text from the job (as before), from the PLC (`PartId` or `ProductId` of the trigger) or from a tool that ran before it on the same camera (the one code a Read code tool read, or the one line a Read text tool read). `JobRunner.Run(camera, image, batch, plc)` passes the PLC values and the earlier results (`RunContext`). Station passes the trigger's values; a free run passes none, so such lines fail there. A value that is missing or empty, a source tool that failed, or one with no text or several texts makes the part NG (`explain.ocv.no_expected`). Checked at setup: the source is named, the PLC field exists, a tool does not check its own text, and these sources work in Check text mode only.
- **PrintQuality.** `ToolResult.PrintQuality` is the lowest character score over every expected line, 0 when a line is not found or the tool could not check.
- **Allow shift.** `vs_ocr_params.allow_shift_px` / `AllowShiftPx`. Each line's first character position, measured from the region's corner, is learned with the print quality of good parts (print reference tags X and Y). Beyond the allowed distance the line is NOT OK (`explain.ocv.shift`). Set with nothing learned, the line is NOT OK (`explain.ocv.shift_unlearned`); learning accepts a part whose only fault is that. New: `vs_ocr_get_line_ext` (struct_size; shift state, distance, offset, position).
- **Dot size and join strength (7.5).** `vs_ocr_measure_dots` returns the median dot diameter and pitch in the region, and refuses solid print. `dot_size_px` sizes the line finder's closing (1.2 dot diameters) and the taught font's melt (about half a dot); `join_strength` (0.5 to 3, 0 = normal) scales both. `TextTool.LearnDots` keeps the dot size in the job.
- **Inkjet print type.** `VS_PRINT_INKJET` / `PrintType.Inkjet`: a grey opening fills the grain inside the strokes of each straightened line. Auto never picks it.
- `vs_ocr_params` grew by three floats at the end. The .NET mirror is updated, and `ocr_bench` gains `--print inkjet`, `--dot-size` and `--join`.

## Verified

- Engine: `ocr_tests` builds (Release, macOS). 23 new checks pass: allow shift unlearned NOT OK, then learned; same print OK; moved 3, 2 px OK; moved 12 px NOT OK (offset 11.9, 0) and not learnable; place saved and loaded; allow shift 0 not checked; line ext without struct_size refused; empty and all-space expected lines NOT OK; dots measured 4.7 px / pitch 6.0 px (drawn 5 / 6); solid refused; dot print read with the learned dot size at join 0, 0.7 and 1.2 (5 of 5 each, a wrong character NOT OK); inkjet grain 4 of 5 OK as Inkjet against 0 as Solid. A run of the full file on an idle machine passed everything except "the word gap reads as a space", which fails on main too (PR #92). On a run with a load average near 100, several print-quality checks failed on the 500 ms time budget. The new tests use a 3 s budget.
- .NET: `dotnet build` of VisionCore.Tests and VisionStudio, 0 warnings. ExpectedTextSourceTests: 13 new tests pass, including the engine settings struct size against the engine and a PLC line with no value failing before reading. With PrintQualityTests, TextFormat*, FontTeach, TaughtFont, JobTests and PlcTests, 83 pass.
- Bench (`batch_check.py`, run once because engine/ changed): FAIL on 48 DM, 60 of 95 read against a baseline of 61 (one image, Image_20220312160710251.bmp, 18 of 19), and 35--Raw.bmp at 511 ms against a 500 ms budget; 48 QR and other QR ok; 1720 of 1868 codes overall. The bench binary links only the code reader (`codereader`), which this pull request does not change, so this is not from this change. It ran with a 15-minute load average near 40. Not rerun (rule 11).
- CI: not run (billing).
- Full suites: the testing session.

## Section 5
- New item: `[x] (done 2026-10-30, PR #93: expected text from the PLC or a tool, missing or empty = NG; PrintQuality; Allow shift learned from good parts; dot size measured, join strength, Inkjet print type) Check text (OCV) tool spec 7.3 and 7.5 parts left after PR #81`

## Timeline (section 3)
| 2026-10-30 | Check text: expected text from the PLC or another tool (missing or empty fails), tool-level PrintQuality, Allow shift against the place learned from good parts; dot print joined by a measured dot size and join strength; Inkjet print type | Tool spec 7.3, 7.5; requirements 12 |

## Decisions (section 4)
| Check text: expected text from outside the job | The PLC value or the other tool's text is checked as printed. When it is missing, empty, from a tool that failed, or ambiguous (several codes or lines), the part fails (`explain.ocv.no_expected`); nothing is guessed. A source tool must run before the text tool on the same camera | Tool spec 7.3; requirements 12 |
| Check text: Allow shift | The place is learned from good parts, not drawn. With Allow shift set and no place learned every part fails until a good part is learned (learning accepts that one fault), so a setting that looks on never checks nothing | Tool spec 7.3; requirements 12 |
| Inkjet print type | Never chosen by Auto. It helps heavy grain and hurts light grain on drawn print, so it is picked on good parts (auto-tune or the engineer) | Tool spec 7.1, 7.5 |

## Update log (section 8)
| 2026-10-30 | Tool spec 7.3 and 7.5: "Built" notes for expected text sources, PrintQuality, Allow shift, dot size, join strength and Inkjet | Claude (Mac, branch claude/ocv-shift-plc-inkjet) |

## Pending
- Wire the new settings into the Hybrid Studio app (`dotnet/Studio`): expected text source per line, Allow shift, Learn dots, print type Inkjet, join strength, and the PrintQuality output on the result. There was no UI work here.
- Expected text from PLC user registers (`UserIn`) or a string block longer than PartId; today only PartId and ProductId of the trigger.
- Explain texts for `explain.ocv.no_expected`, `explain.ocv.shift` and `explain.ocv.shift_unlearned` in the 15 languages.
- Rendered system font for Check text (no teaching): not built.
- Inkjet, dot size and join strength on real TIJ and CIJ samples from VS_IMAGES (`ocr_bench --print inkjet --dot-size --join`); auto-tune to pick the print type.
- Allow shift under a fixture that turns the part: the position is measured from the region's corner, so a rotation shows up as shift.
