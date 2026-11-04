# Laser OCV per-letter freedom (scale and shear per letter) and a verification margin score

- Status: done
- Session: ocv-letter-freedom, 2026-10-31
- Pull request: #166
- Section 5 item: new (INDUSTRY-METHODS.md section 17 rank 11)
- Components/files: engine/src/ocr (verify.cpp, ocr.hpp, c_api_ocr.cpp), engine/include/vs_ocr.h, engine/tests/ocr_tests.cpp, tools/bench/ocr_bench.cpp, dotnet/VisionCore (OcrNative, OcrEngine, Tools/TextTool) and its tests, dotnet/VisionStudio/Services/Station.cs (lowest margin over cameras), ml/ocr/eval_ocr.py, docs/vision-system (study 04c, INDUSTRY-METHODS, TOOL-SPECIFICATIONS 7.3)
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #166: verification margin per character and line reported, `eval_ocr --every`; per-letter freedom measured on the AI read and not built: unsafe per letter, +0.5 point per line) Laser OCV per-letter freedom (scale and shear per letter) and a verification margin score (INDUSTRY-METHODS rank 11, study 04c 7.3.1 and 7.3.3)

## Timeline (section 3)
| 2026-10-31 | Check text verification margin: each checked character's lead in nats over its best rival (any other character within a column of it and short of its neighbours, not space; or nothing printed), each line's lowest, in the C API (`vs_ocr_char_quality.margin`, `vs_ocr_line_ext.margin`, older struct sizes still served), VisionCore (`CharQuality.Margin`, `OcrLine.Margin`) and the Check text tool (detail line, value `margin` kept with the part's result). Reported only; OK / NG unchanged. `eval_ocr --every N`: every look-alike at every place and N random characters per place. Per-letter scale and shear freedom on the AI read measured on about 330,000 changed texts and not built. Verdicts identical to main on real Harpic (469 / 475, 0 / 475 changed accepted) and five synthetic sets; real good prints have a lowest-line margin of median 5.0, at least 0.53 nats; changed texts at most -4.17 | 7.3, 12 |

## Decisions (section 4)
| No per-letter freedom on the AI read | Measured with every look-alike at every place (PR #166): each letter taking its best of 7 warps mixes alignments and passes random changed texts (real Harpic lines 0 -> 11 of 31,374); the whole line in one warp passes look-alikes 9 -> 25 of 9,320 (synthetic laser); made safe (the rival given the same freedom must lose by 1 nat) it adds 0.5 point of laser true text. Line safety first: not built. Freedom stays planned for checks against a reference glyph (taught or printer font) | 7.3, 12 |
| Margin reported, not judged | As a second pass route the margin gained at most 1 point of laser true text and let look-alike accepts creep up; it is shown and kept for trends (how far a good print is from being read as another text), and the pass rule is unchanged | 7.3, 12 |

## Update log (section 8)
| 2026-10-31 | study 04c 7.3.1, 7.3.3 and 7.12 measured results; INDUSTRY-METHODS rank 11 measured; TOOL-SPECIFICATIONS 7.3 verification margin; vs_ocr.h margin fields | Claude (Mac, branch claude/ocv-letter-freedom) |

## Pending
- Check text accepts look-alikes that the synthetic fonts draw alike (eval_ocr --every 6, test sets: laser 8 / 8,782, TTO 6 / 9,376, CIJ 3 / 9,275, embossed 5 / 9,027; mostly O / 0 and I / l / 1; real Harpic 0 / 10,182): decide per job whether such pairs may differ only in that character (a setup check that refuses expected texts whose look-alike twin the font cannot tell apart), and make `--every` part of the model gate
- Laser true-text pass (69.0 % on eval_ocr seed 900) is limited by the recogniser, not the geometry: 44 % of failing laser characters have another character scoring above the expected one; next step is the laser model on real data (study 04c 7.1.1, 7.10)
- Per-letter scale and shear freedom against a reference glyph (taught font, printer font) with the rival under the same freedom (study 04c 7.3.1, 7.3.2)
