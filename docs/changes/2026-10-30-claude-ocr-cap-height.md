# OCR capital height estimate on embossed print and CIJ dot print

- Status: done
- Session: ocr-cap-height, 2026-10-30
- Pull request: #132
- Section 5 item: OCR capital height estimate on embossed print
- Components/files: engine/src/ocr/lines.cpp (estimateCharHeight, a relief mask for embossed print; textMask split into strokeBin and keepParts, unchanged in effect), engine/tests/ocr_tests.cpp (testHeightEstimate). The C API, the job format and findLines are unchanged.
- Depends on: none

## Section 5
- `[x] (done 2026-10-30, PR #132: the estimate scores each trial scale by its letters times their share of the parts of a letter's size, adds the scores of the other scales that found about the same height, and takes the median letter; embossed print also tried as relief (band-pass of either sign, both edges of a stroke joined); a job that names dot print gets the dot-centre height (one dot less, on the dot pitch). On synth_scene (seed 31, 150 images per print type, print auto) estimates off by more than 30%: laser 12 → 0, TTO 10 → 2, CIJ 54 → 15, embossed 58 → 32; characters right 89.1 → 92.5%, 94.6 → 95.7%, 55.3 → 63.1%, 44.1 → 48.2%; check passes 93 → 95, 94 → 99, 4 → 7, 12 → 17 of 150; one character changed accepted 0 of 150 on every set)` OCR capital height estimate on embossed print and CIJ dot print without a taught height

## Timeline (section 3)
| 2026-10-30 | OCR capital height estimate (no height in the job) rebuilt: specks of noise and texture that pass as letters at the finest trial scales no longer win by number; each scale scores its letters times their share of the parts of a letter's size, and a height wins with the scores of the other scales that found about the same height (median letter). Embossed print is also tried as relief: a band-pass of stroke size, either sign, keeps a stroke's light and dark edges and drops uneven light. A job that names dot print gets one dot less (the dot-centre height), on the dot pitch where measureDots finds it. Verified on synth_scene (seed 31, 600 images), ocr_tests (new testHeightEstimate) and ctest. | 9 |

## Decisions (section 4)
| OCR height estimate: no dot correction under print auto | Under print auto the estimate of CIJ print stays about 15% high (a letter's box is one dot higher than the dot-centre height). Dot print could not be told from embossed or textured print reliably here: joined-dot masks agree with the winning height on laser and embossed images as often as on CIJ, measureDots refuses most synthetic CIJ (touching, jittered dots), and the dot lattice did not show in the autocorrelation. A wrong one-dot correction would shrink solid print, so the correction is made only when the job says dot print. | 9 |

## Update log (section 8)
| 2026-10-30 | Change note for the OCR capital height estimate (embossed and CIJ print). | Claude (Mac, branch claude/ocr-cap-height) |

## Measured
Estimate ÷ true capital height on `ml/ocr/synth_scene.py` scenes (seed 31, 150 images per print type, print auto), main → this branch:

| Print type | Median | Off by > 30% (low / high) | Within 15% |
| --- | ---: | ---: | ---: |
| laser | 1.06 → 1.04 | 12 (12/0) → 0 | 135 → 144 |
| thermal transfer | 1.11 → 1.07 | 10 (9/1) → 2 (1/1) | 99 → 132 |
| CIJ dot print | 1.17 → 1.16 | 54 (33/21) → 15 (2/13) | 21 → 60 |
| embossed | 0.91 → 1.01 | 58 (57/1) → 32 (28/4) | 71 → 89 |

`ocr_bench` on the same scenes, budget 5000 ms, no capital height given, main → this branch:

| Print type | Characters right (read only) | Wrong reads ≥ 60 (read only) | Check passes | One character changed accepted |
| --- | ---: | ---: | ---: | ---: |
| laser | 89.10% → 92.46% | 11 / 66 → 14 / 66 | 93 → 95 / 150 | 0 → 0 / 150 |
| thermal transfer | 94.62% → 95.70% | 10 / 50 → 10 / 56 | 94 → 99 / 150 | 0 → 0 / 150 |
| CIJ dot print | 55.32% → 63.07% | 3 / 228 → 2 / 228 | 4 → 7 / 150 | 0 → 0 / 150 |
| embossed | 44.14% → 48.17% | 19 / 185 → 16 / 185 | 12 → 17 / 150 | 0 → 0 / 150 |

Finding and estimating takes about 4–5 ms more per image (find-only mean 16.6 → 21.0 ms laser, 17 → 20–24 ms embossed), only when the job sets no capital height. Of the 32 embossed images still off, 16 have no trial scale near the true height at all (the relief is too faint to see; emboss_0011 shows no text by eye).

## Pending
- OCR capital height estimate on CIJ dot print under print auto stays about 15% high (a dot more than the dot-centre height): needs a dot-print test that tells CIJ from embossed and textured print (touching, jittered dots); a job that names dot print already gets the correction.
- OCR line finder on embossed print: a line is followed in one polarity's mask, so an embossed line is cut from its light or its dark edges only (characters right about 50% even with the true height given, PR #107); follow lines in the relief mask too.
