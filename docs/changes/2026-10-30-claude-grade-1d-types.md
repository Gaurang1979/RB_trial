# Code Verify: 1D grading of GS1 DataBar and the EAN/UPC reference-decode bar-width check (characters 1, 2, 7, 8)

- Status: done
- Session: Claude session grade-1d-types, 2026-10-30
- Pull request: #82
- Section 5 item: new
- Components/files: engine/src/decodability.cpp, engine/src/grade1d.cpp, engine/src/reader.hpp, engine/tests/engine_tests.cpp (testGrading), docs/vision-system/TOOL-SPECIFICATIONS.md 6.2
- Depends on: none (PR #78 changes grade1d.cpp too: merge #78 first, then this)

## Section 5
- [x] (done 2026-10-30, PR #82: EAN/UPC characters 1, 2, 7, 8 by their bar total in sets A, B and C; GS1 DataBar Omnidirectional/Truncated, Limited and Expanded graded per ISO/IEC 24724, the variant from each scan's decode, stacked variants not graded; each scan decoded at the global threshold; 14 new engine grading checks with exact expected values) Code Verify: 1D grading of GS1 DataBar and the EAN/UPC reference-decode bar-width check (characters 1, 2, 7, 8)

## Timeline (section 3)
| 2026-10-30 | 1D grading of GS1 DataBar (Omnidirectional, Truncated, Limited, Expanded) and the EAN/UPC bar-width check for characters 1, 2, 7 and 8 | TOOL-SPECIFICATIONS.md 6.2 |

## Decisions (section 4)
| DataBar variant from the decode | Omnidirectional and a two-pair Expanded have the same element count, so each scan's variant comes from zxing's read of that scan; a scan that does not read has no decodability (counts 0, and it fails decode anyway); a symbol no scan reads as a single-row variant (stacked) is not graded, never given a made-up grade | TOOL-SPECIFICATIONS.md 6.2 |
| DataBar finders | A finder's own width runs between a space's and a bar's leading edges, which ink spread moves; it is measured against 15 modules of the characters beside it, so ink spread leaves decodability unchanged as the standard intends | TOOL-SPECIFICATIONS.md 6.2 |
| EAN 1/2/7/8 against the text | The bar total's margin to the threshold over 1 module; when the decoded text names the printed digit, a total on the pair's other side counts 0 (the reference decode would read the other digit) | TOOL-SPECIFICATIONS.md 6.2 |
| Scan decode at the global threshold | Each scan is thresholded at its global threshold before zxing reads it, as ISO 15416 decodes a scan; zxing's own binarizer on grey edge pixels moved DataBar Omni edges by a fraction of a module and failed 9 of 10 clean scans | TOOL-SPECIFICATIONS.md 6.2 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md 6.2: DataBar grading and the EAN/UPC 1, 2, 7, 8 check | Claude (local Mac, branch claude/grade-1d-types) |

## Pending
- Read code tool: allow a lowest print grade on single-row GS1 DataBar now that it is graded (CodeTool.cs still refuses it; stacked DataBar stays not graded).
- Grading: GS1 DataBar Stacked, Stacked Omnidirectional and Expanded Stacked (each row with its separator pattern).
