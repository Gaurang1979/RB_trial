# OCR line finder: rotated multi-line blocks (2-3 lines up to 12°) found whole; a line cut short fails

- Status: done
- Session: ocr-line-finder, 2026-10-30
- Pull request: #107
- Section 5 item: new
- Components/files: engine/src/ocr/lines.cpp (line finder, capital height estimate), engine/src/ocr/ocr.hpp (Line::cutShort), engine/src/ocr/c_api_ocr.cpp (a cut-short line scores below min quality; a read of spaces only scores 0). Not font.cpp or recognize.cpp (PR #91). The C API and the job format are unchanged.
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-30, PR #107: lines followed letter by letter instead of projection bands; capital height from letters standing beside a letter; a line cut short (letters in line past a gap too wide to join, a letter on the region's edge, ink just past its ends) scores below min quality and fails; on synth_scene whole images, characters right: laser 40.5 → 89.1%, TTO 58.0 → 94.6%, CIJ 35.4 → 55.3%, embossed 34.5 → 44.1%; wrong reads at or above min quality 73 → 11, 28 → 10, 4 → 3, 70 → 19; check passes 35 → 93, 50 → 94, 4 → 4, 14 → 12 of 150; one character changed accepted 0 of 150 on every set) OCR line finder for rotated multi-line blocks; a line cut short fails`

## Timeline (section 3)
| 2026-10-30 | OCR line finder rebuilt for whole images of 2–3 lines turned up to 12°, bowed or wide-tracked: lines are chains of letters (each letter joins the line whose last letters' band it sits in, the band carried along the line's slope; the join gap is about 3.5 times the line's own letter gap, 2.2 to 4.5 capitals); punctuation, dashes and faint end letters in the band join the nearest end; pieces split at a stop before a word gap are joined; a line takes its own angle only when clearly sharper than the region's. Capital height estimate counts only parts with a like part beside them and none on top (dots of dot print and texture specks no longer win); unjoined dot print is also tried joined. A line cut short is scored below min quality in read, check and format modes, so it fails; a read of spaces only scores 0 (it scored 100). Verified on synth_scene (seed 31, 150 images per print type), ocr_tests and ctest. | 9, 12 |

## Decisions (section 4)
| OCR: a line cut short never passes | A line with letters of its size in line with it just past a gap too wide to join, a letter touching the region's edge, or stroke ink (a third of a letter column's, twice the ground) within 1.3 capitals past an end may be only part of the print. Its read is scored at min quality − 1 and fails, in read, check and format modes: a cut-short part reads confidently and could pass a format or show as a good read. Price: a false NG when another field is printed within about 1.5 capitals past the join gap of a line. | 12 |

## Update log (section 8)
| 2026-10-30 | Change note for the OCR line finder (rotated multi-line blocks, cut-short lines fail). | Claude (Mac, branch claude/ocr-line-finder-1dd5f4) |

## Measured
`ocr_bench` on `ml/ocr/synth_scene.py --seed 31 --n 150` per print type, budget 5000 ms, print auto, no capital height given (the tool estimates it), main → this branch:

| Print type | Characters right (read only) | Lines exact | Wrong reads ≥ 60 | Check passes | One character changed accepted | Median ms per image |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| laser | 40.49% → 89.10% | 90 → 235 / 311 | 73 / 153 → 11 / 66 | 35 → 93 / 150 | 0 → 0 / 150 | 46.9 → 36.1 |
| thermal transfer | 58.00% → 94.62% | 152 → 264 / 319 | 28 / 124 → 10 / 50 | 50 → 94 / 150 | 0 → 0 / 150 | 45.4 → 33.6 |
| CIJ dot print | 35.43% → 55.32% | 23 → 35 / 287 | 4 / 226 → 3 / 228 | 4 → 4 / 150 | 0 → 0 / 150 | 53.5 → 41.6 |
| embossed | 34.53% → 44.14% | 47 → 56 / 299 | 70 / 201 → 19 / 185 | 14 → 12 / 150 | 0 → 0 / 150 | 42.1 → 35.9 |

Times are per image of 1–3 lines with other sessions running (load 7–15 on the M3), so only a rough guide; the line finder is not slower.

With the true capital height given per image, characters right were (main → this branch): laser 80.4 → 90.8%, TTO 89.6 → 94.5%, embossed 49.4 → 49.8% (check passes 15). The rest of the gap on embossed and CIJ print without a taught height is the height estimate: an embossed letter shows as a light half and a dark half (estimate about half the height in 58 of 150 images), and CIJ is estimated 1.3–1.5 times high (the dots add their size) or too low in about a third of images.

## Pending
- OCR capital height estimate on embossed print (each letter a light and a dark half; 58 of 150 synth_scene images estimated about half high) and CIJ dot print without a taught height (53 of 150 off by more than 30%); a job that sets the capital height reads them at 49.8% (embossed) and 57% (CIJ).
- Show why a line failed as cut short ("text may go on past this line") in Vision Studio: needs a field in vs_ocr_line_ext and the .NET mirror.
