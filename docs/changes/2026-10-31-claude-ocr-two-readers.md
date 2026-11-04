# OCR Read text: two readers must agree, and a calibrated line confidence

- Status: done
- Session: ocr-two-readers, 2026-10-31
- Pull request: #162
- Section 5 item: Two independent text readers must agree, plus word-level confidence
- Components/files: engine OCR (`engine/include/vs_ocr.h`, `engine/src/ocr/ocr.hpp`, `recognize.cpp`, `c_api_ocr.cpp`, `engine/tests/ocr_tests.cpp`), `tools/bench/ocr_bench.cpp`, `ml/ocr/eval_ocr.py`, .NET (`OcrNative.cs`, `OcrEngine.cs`, `Jobs/Job.cs`, `Tools/TextTool.cs`, `VisionCore.Tests/ReadAgreementTests.cs`), README, TOOL-SPECIFICATIONS 7.1, INDUSTRY-METHODS 14.1
- Depends on: none (PR #166, merged first, put `margin` at the end of `vs_ocr_line_ext`; the three new fields follow it). PR #160 (layout reads) overlaps: merge it after this one and give layout reads the same second reader

## Section 5
- [x] (done 2026-10-31, PR #162: in Read text a second reader must read the same text and the line confidence, the CTC probability of the whole read calibrated on validation lines, caps the score; misread lines scoring >= 60 on whole images laser 9 → 0, TTO 10 → 3, CIJ 1 → 0, embossed 5 → 1, real Harpic 1 → 0; what is left is O/0 and l/1 in mixed letter-digit strings, which needs an independently trained second model) Two independent text readers must agree, plus word-level confidence calibrated on our OK / NG set (rank 14)

## Results

Thresholds: min quality 60 (the tool's default). Model `ml/models/ocrnet.onnx` as on main. Synthetic test sets `eval_ocr --make --n 400 --seed 900`; calibration on separate validation sets (`--seed 901`); "real" = the 389 held-out Harpic HWN laser lines (never trained on). The second reader on these sets is the shifted re-read (no second model exists yet; the old ocrnet of b57949f is shown as a stand-in).

Calibration (`eval_ocr --fit-gamma`, validation sets, second reader the shifted re-read): line confidence = 100 · P^(1/γ). Wrong reads the print decides / wrong reads whose glyphs the font draws alike / right reads, at or above 60:

| γ | laser | TTO | CIJ | embossed |
| ---: | ---: | ---: | ---: | ---: |
| 0.75 | 0 / 0 / 206 | 0 / 0 / 243 | 0 / 0 / 5 | 0 / 0 / 124 |
| **0.9 (chosen)** | 0 / 1 / 217 | 0 / 1 / 257 | 0 / 0 / 7 | 0 / 0 / 131 |
| 1.0 | 1 / 1 / 220 | 1 / 1 / 266 | 0 / 0 / 8 | 0 / 0 / 139 |
| 1.5 | 1 / 1 / 230 | 1 / 5 / 289 | 0 / 0 / 12 | 2 / 0 / 160 |

Straightened lines (test sets), wrong reads at or above 60 and right reads at or above 60:

| Set | main | line confidence only | line confidence + shifted re-read (built default) | of them glyph-identical | old ocrnet as second reader | Right reads >= 60: main → built default |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| laser | 22 / 103 | 3 | **1** | 0 | 1 | 275 → 215 |
| TTO | 16 / 57 | 4 | **2** | 1 | 2 | 308 → 253 |
| CIJ dot print | 4 / 330 | 1 | **1** | 1 | 0 | 20 → 6 |
| embossed | 13 / 158 | 2 | **2** | 1 | 0 | 204 → 136 |
| real laser (389) | 0 / 0 | 0 | **0** | 0 | 0 | 389 → 388 |

Check text (OCV) is unchanged: one character changed accepted 0 / 400 (random) and 0 / 396–400 (look-alike) on every set, 0 / 389 on the real lines.

Whole images (`ocr_bench --read-only`, 150 images per type from `synth_scene --seed 31`, budget 5000 ms; real = the 148 held-out Harpic images, `--roi 400,600,1250,750 --char-h 38 --polarity light`), main → this branch. A read line is "misread" when it is neither a printed line of the image nor a piece of one (pieces come from the line finder splitting a line, an open item since PR #77):

| Set | Printed lines | Right reads >= 60 | Misread lines >= 60 | Pieces of a line >= 60 | One character changed accepted (`--mutate`) |
| --- | ---: | ---: | ---: | ---: | ---: |
| laser | 311 | 227 → 183 | 9 / 78 → **0** / 78 | 5 → 4 | 0 / 150 → 0 / 150 |
| TTO | 319 | 244 → 206 | 10 / 63 → **3** / 64 | 0 → 1 | 0 / 150 → 0 / 150 |
| CIJ dot print | 287 | 19 → 6 | 1 / 333 → **0** / 339 | 0 → 0 | 0 / 150 → 0 / 150 |
| embossed | 299 | 46 → 34 | 5 / 268 → **1** / 270 | 13 → 10 | 0 / 150 → 0 / 150 |
| real Harpic | 444 | 404 → 402 | 1 / 172 → **0** / 172 | 9 → 9 | 0 / 148 → 0 / 148 |

Left at or above 60 (line and whole-image sets together): O read as 0 or 0 as O in mixed letter-digit strings (5, two of them in fonts that draw O and 0 alike), l read as 1 (1), a dropped character or mark ("Wt." as "Wt", "B.NO:IR9VE" as "B.NO:R9VE" at exactly 60), "," read as "." (1). The shifted re-read is the same model, so it shares the model's blind spots; these need an independently trained second reader, or a format where letters and digits have fixed places.

Time at the tool's default budget (100 ms), Apple M3, other sessions running. Same load, main → branch before the review fixes: laser scenes (1–3 lines) median 27.3 → 31.5 ms, p95 51.9 → 54.0, max 59.6 → 64.0; real Harpic (3 lines and a label edge) median 13.1 → 16.4 ms, p95 14.4 → 17.8, max 15.4 → 19.1: about 1 ms more per line read. Final head (busier Mac): laser median 33.7, p95 60.2, max 83.4 ms; real median 17.7, p95 21.5, max 48.0 ms. Within the 100 ms budget; a second read the budget leaves no room for fails the line.

The taught font as the second reader on the real set was tried in `ocr_bench --teach-font`: teaching from whole images refused 116 of 120 lines (the line does not cut into as many characters as its text), so it is not measured here.

## Timeline (section 3)
| 2026-10-31 | OCR Read text safety: a second reader must read the same text (second model, taught font or AI, else the shifted re-read; `vs_ocr_set_agreement`, `vs_ocr_load_second_model`), the line score capped by a calibrated line confidence (CTC probability of the whole read, γ 0.9 fitted on validation lines), Read text without formats fails a part on any unclear line; misread lines >= 60 on whole images laser 9 → 0, TTO 10 → 3, CIJ 1 → 0, embossed 5 → 1, real 1 → 0, 0 changed texts accepted; `eval_ocr --line-conf --agree --fit-gamma`, `ocr_bench --agree --second-model --teach-font` | Requirements 7.1, 12 |

## Decisions (section 4)
| Read text safety | In Read text (no expected text) a line passes only when a second reader reads the same text, each at or above min quality, and the line confidence is at or above it; Off is refused in Read text. With no independent second reader the same reader reads the line moved by half a step. Read text without formats fails a part on any unclear line read (before, any text read passed): line safety first, at a cost of right reads on hard synthetic print (laser 227 → 183 of 311 lines on whole images) and 2 of 444 on the real line | Requirements 7.1, 12 |
| Teaching proposals | Teaching (`TextTeach`, Auto-find) proposes lines by their lowest character score with no second reader, as before: a proposal decides nothing, the Read or Check text tool built from it judges every part with both readers | Requirements 7.1 |
| Line confidence calibration | 100 · P^(1/γ), P the CTC probability of the read text with spaces as blanks; γ the most lenient value at which no wrong read the print decides scores >= 60 on the validation sets (`eval_ocr --fit-gamma`), 0.9. Change it only by a new fit, never on the test sets | Requirements 7.1, 12 |

## Update log (section 8)
| 2026-10-31 | README OCR model: Read text safety commands; TOOL-SPECIFICATIONS 7.1: built note (agreement, line confidence); INDUSTRY-METHODS 14.1: second reader and confidence rows | Claude (ocr-two-readers, branch claude/ocr-two-readers) |

## Pending
- Reader B for Read text: train a second OCR model of another kind (study 04c 7.1: an attention or transformer recogniser, other augmentation seeds), install it as `ocrnet_b.onnx`, and measure; the O/0 and l/1 confident wrong reads left by PR #162 need it
- Taught font as the second reader on the real Harpic line: teach it on the Teach page flow and measure agreement with the AI on the held-out images (bench teaching from whole images refused 116 of 120 lines)
