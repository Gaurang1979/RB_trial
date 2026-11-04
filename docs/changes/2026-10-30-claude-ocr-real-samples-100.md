# OCR / OCV on the real samples: PR #107's cut-short rule no longer fails good lines beside a print box's edge; per-image results in docs/OCR-TEST-RESULTS.md

- Status: done
- Session: ocr-real-samples-100, 2026-10-30
- Pull request: #142
- Section 5 item: new
- Components/files: engine/src/ocr/lines.cpp (inkPastEnds), engine/tests/ocr_tests.cpp (testLineEnds), docs/OCR-TEST-RESULTS.md
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-30, PR #142: real Harpic laser set 452 → 469 of 475 readable images OK, held out 139 → 146 of 148, line job 438 → 450 of 455 HWN881 bottles, 0 misprints accepted; synthetic sets (PR #107's four print types, the full-system laser set) read exactly as on main; per-image table and causes in docs/OCR-TEST-RESULTS.md) OCR / OCV on the real samples stored on the Mac: why 92–93 %, every failed image with its cause`

## Timeline (section 3)
| 2026-10-30 | Real OCR samples on the Mac found (only the Harpic HWN laser line: 489 images, 475 readable, labelled by eye; 10 old time labels were wrong). Owner's "92–93 %" are synthetic sets; on the real set PR #107's cut-short rule had dropped main from 472 to 452 of 475. The rule now tells a region border (the label's bright margin beside the dark print box) from print: 469 of 475, held out 146 of 148, synthetic sets read as on main, 0 misprints accepted | Requirements 7.1, 7.3, 12 |

## Decisions (section 4)
| OCR line ends beside a region border | Ink past a line's end is not the line's when it is a filled region: 80 % of a letter-wide band, twice over, beyond the line's ink level, starting at least the line's usual letter gap + 0.3 capitals (0.5 at least) from the end. Text never fills that much, and a next letter at the line's spacing would have shown before it, so no line that may go on is let through | Requirements 7.1, 12 |
| OCR line boxing changes need the synthetic gate | Taking the band per quarter of a line, the region's angle from text lines only, and widening a line to hold one faint end letter each read 1–3 more real lines but raised wrong reads scoring at or above min quality on synthetic laser sets (the reader ignored a faint "." at 100). Not kept: a change to how lines are boxed must not raise those on any set | Requirements 7.1, 12 |

## Update log (section 8)
| 2026-10-30 | docs/OCR-TEST-RESULTS.md: real OCR samples on the Mac, method, before/after per set, the 6 images still NOT OK with cause, per-image table | Claude (Mac, branch claude/ocr-real-samples-100) |

## Pending
- OCR line boxing on real bowed labels: three Harpic lines that read before PR #107 (a faint 7, a bowed time line clipped at the top, a faint "l" past the line's end) still fail safe; change the band or end-letter handling only with no rise in wrong reads ≥ min quality on the synthetic sets.
- Retrain the AI OCR model on real line crops from the current line finder, with thin lines across the print (a thread over "09/28 15:52" defeats it); keep the images from 15:58 on held out.
- Find the part on the Harpic job: in a quick try (barcode taught on one image) the pattern was not found on 9 of 475 images (score under 0.6 on the curved bottle); the text area should follow the print box.
- Real CIJ, TTO and embossed sample images: none are stored on this Mac.
