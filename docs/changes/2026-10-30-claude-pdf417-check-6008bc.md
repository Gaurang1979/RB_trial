# PDF417: cross-check zxing reads (miscorrection is NG) and trim the row-fusion pass

- Status: done
- Session: pdf417-check, 2026-10-30
- Pull request: #111
- Section 5 item: new
- Components/files: engine/src/pdf417.cpp (cross-check, fusion speed), engine/src/decode.cpp (small: Collector::add calls the cross-check on zxing PDF417 reads), engine/src/reader.hpp, engine/tests/engine_tests.cpp
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #111: zxing PDF417 reads cross-checked, a miscorrection gives no read; fused pass 225 -> 44 ms on the 58 sample frames) Code Reader: PDF417 cross-check of zxing reads (a miscorrected read is NG) and the fused row-fusion pass trimmed (pending from PR #76)

## Timeline (section 3)
| 2026-10-30 | PDF417 cross-check: a zxing PDF417 read that used check codewords (UEC < 1) is read again at zxing's corners by the row-fusion reader (Reed-Solomon with 2 check codewords to spare, text from zxing on the redrawn symbol); a different text gives no read; when it cannot be read again, zxing's own count must leave 2 check codewords (half when the row indicators are not read). Engine test with a deliberate miscorrection (symbol A with one data codeword and half the check codewords of B, one codeword apart): zxing alone reads B at levels 2 and 4, the engine reads nothing; A with nCheck/2-1 wrong codewords still reads. Fused pass trimmed: start/stop patterns on every 4th row first (and read the other way round, so a band without them either way is not turned and scanned again), one affine warp of the band's box per band instead of a full-frame float copy and two remaps, structure tensor smoothed at quarter size. 58 sample frames (20 MP, no PDF417), same busy Mac: mean 225 -> 44 ms; synthetic damaged labels 37/40 read before and after, no wrong read. | 12 |

## Decisions (section 4)
| PDF417 zxing reads keep a reserve | zxing corrects a PDF417 to its last check codeword and tries up to 100 guesses for unclear codewords (zxing-cpp 3.1.1), with no misdecode reserve. A read that used check codewords must be confirmed by a second, independent read with 2 to spare, or zxing's own count must leave 2 (ISO/IEC 15438 keeps 2 for detection). A symbol at full correction capacity is no read (NG), even when zxing's answer happens to be right. | 12 |

## Update log (section 8)
| 2026-10-30 | change note for PR #111 | Claude (Mac, branch claude/pdf417-check-6008bc) |
