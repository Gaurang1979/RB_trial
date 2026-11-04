# Code Reader: PDF417 row fusion for damaged rows, Micro QR grid fit

- Status: done
- Session: pdf417-microqr-robust, 2026-10-30
- Pull request: #76
- Section 5 item: Code Reader: Pharmacode and DotCode decoders, PDF417 row fusion for damaged rows, Micro QR grid fit (the PDF417 and Micro QR parts only; Pharmacode/DotCode and EAN/UPC add-ons are other sessions)
- Components/files: engine/src/pdf417.cpp, pdf417_patterns.inc, microqr.cpp (new); decode.cpp (two fallback passes), reader.hpp, CMakeLists.txt; engine/tests/engine_tests.cpp
- Depends on: none

## Section 5
- [ ] Code Reader: a zxing PDF417 misread passes on a heavily scratched level-4 symbol (whole-image bright pass, CR_STEP_FULL_BRIGHT: the right text plus 8 extra characters; 1 of 20 synthetic labels, 2 scratches along and 2 across). zxing corrects to full capacity with no reserve; cross-check PDF417 reads (e.g. with the fused reader's 2-codeword reserve) so a misread is NG.
- [ ] Engine tests: "linear reader: tilted, defocused, moving and small SSCCs read" failed once (4 of 5 at 1.3 px modules) on a loaded machine; linear.cpp is unchanged by PR #76: check whether it is flaky.

## Timeline (section 3)
| 2026-10-30 | PDF417 row fusion (codewords fused per row over the scan lines, scratched module columns masked, unclear codewords erased, GF(929) Reed-Solomon with a 2-codeword reserve, text from zxing on the redrawn clean symbol) and Micro QR grid fit (M2-M4: finder/separator/timing fit, corners refined, blur unmixed in module space, zxing's RS and BCH decide). Engine, 20 synthetic labels per set, before -> after, wrong reads unchanged: PDF417 3 px modules, scratches along the bars, level 2: 1 scratch 14 -> 19, 2: 5 -> 18, 3: 5 -> 15; level 4: 4 scratches 10 -> 20; 2 along + 2 across 18 -> 19. Micro QR 4 px modules: blur 1.8 px 6 -> 20, blur 1.8 px slanted 8 -> 20, blur 2.2 px 1 -> 20, blur 2.6 px 0 -> 11, blur 1.2 px slanted 17 -> 20. No wrong read from either new pass. | 12 |

## Decisions (section 4)
| PDF417 never guesses | A codeword whose best pattern is not clearly ahead of the next is an erasure; Reed-Solomon must succeed with 2 check codewords to spare, the length descriptor must match, and zxing re-checks the redrawn symbol. | 12 |
| Micro QR M1 not grid-fitted | M1 has error detection only; a threshold guess would be checked by too little. M2-M4 rely on Reed-Solomon plus the format BCH. | 12 |
| Step ids reused | CR_STEP_LINEAR for fused PDF417, CR_STEP_GRIDFIT for Micro QR: no C API change while other sessions edit codereader.h. | - |
| Pattern table | PDF417 codeword patterns taken from libzint's table (BSD-3-Clause), checked in engine_tests (shape, clusters, zero syndromes on zint symbols). | - |

## Update log (section 8)
| 2026-10-30 | change note for PR #76 | Claude (Mac, branch claude/pdf417-microqr-robust) |

## Pending
- Speed: the fused PDF417 pass takes about 150-500 ms when it runs (only after zxing missed); profile and trim (row-indicator matching per scan line, several bands and turns tried).
- Cross-check zxing's PDF417 reads so a miscorrected read is NG (see Section 5).
