# ISO/IEC TR 29158 cell modulation per codeword from the real Data Matrix / QR codeword placement

- Status: done
- Session: dpm-codeword-placement, 2026-10-30
- Pull request: #135
- Section 5 item: new (Pending of docs/changes/2026-10-30-claude-grade-dpm-29158.md: "the codeword table instead of the one-cell-one-codeword approximation")
- Components/files: engine (new engine/src/codewords.cpp, engine/src/grade_dpm.cpp, engine/src/reader.hpp, engine/CMakeLists.txt, engine/tests/dpm_tests.cpp)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #135: codeword map for all 30 Data Matrix ECC 200 sizes and QR versions 1-40; TR 29158 cell modulation graded per codeword and Reed-Solomon block) ISO/IEC TR 29158 cell modulation per codeword from the real Data Matrix / QR codeword placement, instead of one wrong cell = one codeword.

## Timeline (section 3)
| 2026-10-30 | Engine `codewordMap` (engine/src/codewords.cpp): the modules and Reed-Solomon block of every codeword of a Data Matrix ECC 200 (ISO/IEC 16022 table 7 and annex F placement, all 24 square and 6 rectangular sizes, multi-region, interleaved blocks) and a QR (ISO/IEC 18004, versions 1-40, L/M/Q/H, short and long blocks, misdecode protection p). TR 29158 cell modulation now grades as ISO/IEC 15415: a codeword takes its lowest module grade; at each level the codewords under it are errors in their own block, notional UEC = min over blocks of 1 - 2e/(d - p). Checked against qrcodegen (160 version/level symbols, every block a valid RS word) and libdmtx (all 30 sizes, codewords equal in order), and on drawn marks with damage in known codewords and blocks. | 7, 12 |

## Decisions (section 4)
| Cell modulation per codeword | ISO/IEC 15415 counts codewords under a grade level as errors (2 per codeword) against each block's check codewords minus p; the symbol's notional UEC is its worst block. Data Matrix p = 1 only for the sizes with an odd check count (10 x 10, 12 x 12, 8 x 18, 8 x 32), QR p from ISO/IEC 18004 (1-L 3, 1-M and 2-L 2, 1-Q, 1-H and 3-L 1). DMRE sizes and a QR of unknown EC level keep the old approximation (DpmQuality.perCodeword false). | 7 |

## Update log (section 8)
| 2026-10-30 | change note for TR 29158 cell modulation per codeword | Claude (Mac, branch claude/dpm-codeword-placement) |

## Pending
- ISO/IEC TR 29158 per-codeword cell modulation tried on real dot-peen and laser part images.
- ISO/IEC 15415 grader (quality.cpp): use codewordMap for modulation per codeword and for the UEC approximation when the decoder reports none (it still counts wrong cells against the whole symbol).
