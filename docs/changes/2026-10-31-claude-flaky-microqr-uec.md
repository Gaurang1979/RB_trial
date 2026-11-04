# Flaky engine test: UEC from the decoder after ISO 15415, Micro QR M4-L 2 errors (UEC 0.00 instead of 0.33)

- Status: done
- Session: flaky-microqr-uec, 2026-10-31
- Pull request: #207
- Section 5 item: new (Pending of 2026-10-31-claude-quiet-zone-same-verdict, psf-deblur, 2026-10-30-claude-grade-1d-large-x)
- Components/files: engine (`engine/src/decode.cpp` Collector::addCode and the whole-image job order; `engine/tests/engine_tests.cpp` UEC test budget)
- Depends on: none

## Section 5
- [x] (done 2026-11-01, PR #207: the code's grade comes from the read nearest the image as captured, not from the thread that added it first; raw whole-image job first) Engine test "UEC from the decoder after ISO 15415: Micro QR M4-L 2 errors" flaky on main (UEC 0.00 instead of 0.33 in 4 of 6 runs)

## Timeline (section 3)
| 2026-11-01 | Grading made independent of thread order. Cause: the whole-image pass reads the raw and the brightened image on two threads, and the first read of a text kept its module grid and decoder UEC; the brightened read of the damaged Micro QR M4-L saw one codeword more wrong (zxing 0.25, ISO 0.00; raw 0.50, ISO 0.33). Under load the late worker also found the code read and skipped the raw job (brightened ran first since PR #156). Now a repeat read of the same symbol and type gives the grade when it is nearer the captured image (raw whole-image read, then reads with grid and UEC, then grid only), or, at equal standing, has the lower UEC; the raw job goes first per tile. UEC test 30/30 at load 50-76 (before: 24/30 at load 40 with only the swap, 2-4/6 without); engine_tests 349 ok | 4 (Tool 6), 13 |

## Decisions (section 4)
| Grade source when a code is read more than once | The grade (module grid, decoder UEC, EC level and corners) comes from the read on the image as captured (the raw whole-image pass) before reads of enhanced images; between reads of equal standing the lower UEC, so the result is the same in any thread order and errs low. Only reads of the same type and place swap. Reason: ISO/IEC 15415 grades the image as captured; an enhanced image's read is the engine's own processing, not the print | 4 (Tool 6), 13 |

## Update log (section 8)

## Pending
- Engine: locate_tests fails under heavy load (load 70-90: "truth, light change" found 49/50, "repeatability on a still part" found 73/100, both against a time budget); give its truth cases a load-proof budget like the UEC test
