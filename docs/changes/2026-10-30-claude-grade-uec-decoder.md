# Grading: unused error correction from the decoder (QR, Data Matrix, Micro QR M1) and PDF417 codeword validity against the ISO/IEC 15438 table

- Status: done
- Session: grade-uec-decoder (claude/grade-uec-decoder), 2026-10-30
- Pull request: #100
- Section 5 item: Grading: QR and Data Matrix unused error correction from the decoder
- Components/files: engine (engine/src/quality.cpp, engine/src/grade_pdf417.cpp, a comment in engine/include/codereader.h, engine/tests/engine_tests.cpp)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #100: zxing's count of corrected errors less ISO's misdecode protection, for every type zxing reports it for) Grading: QR and Data Matrix unused error correction from the decoder (zxing's UEC) instead of the module-count approximation.
- "Grading: Micro QR M1 unused error correction": `[x] (done 2026-10-30, PR #100: its two check codewords recomputed on the decoder's sampled grid, A when intact, F when not, not measured when its format cannot be read)`
- "Grading: PDF417 codeword validity": `[x] (done 2026-10-30, PR #100: the full table (zint's, in pdf417.cpp since PR #76), looked up by the reference decode's edge-to-similar-edge values)`

## Timeline (section 3)
| 2026-10-30 | Grading takes unused error correction from the decoder for QR, Micro QR, Data Matrix and Aztec, after ISO/IEC 15415 (less the misdecode protection codewords zxing ignores); Micro QR M1 from its own check codewords; PDF417 codewords checked against the full ISO/IEC 15438 codeword table (the one pdf417.cpp reads with; independently recovered from the local encoder with computed Reed-Solomon codewords and found identical). Tests on zint symbols with 0 to 3 codewords damaged: QR 1-L, 1-M, 5-Q, Micro QR M2-L, M4-L, M1, Data Matrix 10 x 10 and 14 x 14, read and graded through the C API; a PDF417 pattern of the right cluster that is no codeword | 6.2, 12 |

## Decisions (section 4)
| UEC after ISO/IEC 15415, not zxing's | zxing 3.1 reports 1 - 2t/d of its worst Reed-Solomon block (cut to two decimals) and ignores the misdecode protection codewords p: it corrects 3 errors in a QR 1-L where ISO allows 2, and 2 in a Micro QR M2-L where ISO allows 1. The grade uses 1 - 2t/(d - p), 0 when 2t > d - p, with t recovered from zxing's value; p is not 0 only in QR 1-L/M/Q/H, 2-L, 3-L, Micro QR M2-L/M, M3-L, M4-L and Data Matrix 10 x 10, 12 x 12, 8 x 18 and 8 x 32 (odd check codeword counts). It never grades higher than zxing's value. A read by the engine's own grid decoders (no zxing count) keeps the module-count approximation. Data Matrix rectangular extension sizes take zxing's value as it is | 6.2, 12 |
| Micro QR M1 UEC | zxing reports 0.0 even for a clean M1 and reads no M1 with a wrong codeword. Its zxing grid is the sampled one (damage kept), so the engine reads the format, unmasks and recomputes the two check codewords: intact A, otherwise F; not measured when the format cannot be read | 6.2, 12 |
| PDF417 codeword table | Grading uses the table the PDF417 reader already has (pdf417Pattern, zint's table, PR #76) rather than a second copy; it was checked against a table recovered independently from the local encoder (numeric codewords chosen, error correction codewords computed over GF(929)): identical in all 2787 entries. The six rounded edge-to-similar-edge values are unique in each cluster, so the grader looks codewords up by them, as the ISO/IEC 15438 reference decode does | 6.2 |

## Update log (section 8)
| 2026-10-30 | Change note for the decoder's UEC, M1 UEC and the PDF417 codeword table | Claude (Mac, branch claude/grade-uec-decoder) |
