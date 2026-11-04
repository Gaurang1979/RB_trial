# 1D grading: band finding at large X dimensions (stacked DataBar at 10 px per module found no band)

- Status: done
- Session: grade-1d-large-x, 2026-10-30
- Pull request: #128
- Section 5 item: 1D grading: findLinearBands finds no band for a stacked DataBar
- Components/files: engine (grade1d.cpp gradeLinear, linear.cpp findLinearBandsAt, reader.hpp, engine_tests.cpp)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #128: gradeLinear finds the whole symbol at any X dimension: the band with bars nearest the code, a quarter and an eighth of the size when half and full size show only edges, and at X above 5 px a piece of the band grown along its bars; engine tests at 3 to 16 px per module for Code 39, ITF-14 and the three stacked DataBar variants) 1D grading: findLinearBands finds no band for a stacked DataBar at 10 px per module in the engine test image; check large-X 1D grading on real images.

## Timeline (section 3)
| 2026-10-30 | 1D grading at large X dimensions: Code 39 and ITF-14 at 3-16 px per module and DataBar Stacked, Stacked Omni and Expanded Stacked at 6-16 px per module graded A with X measured (stacked DataBar was not graded from 6, 8 or 12 px per module before); reading (findLinearBands) unchanged | 6.2, 12 |

## Decisions (section 4)
| 1D grading band at large X | At half size a large X leaves the band of bars in pieces (the tensor window and closing are a few px, wide bars tens of px) or only the label's and a stacked symbol's row edges as bands. Grading takes the band nearest the code that has bars across it; when none has, it looks at a quarter and an eighth of the size; at X above 5 px it grows the band along its middle scan line while edges follow within 9 X (DataBar's widest element is 8 modules), never shorter than the piece. No band with bars: not graded, never a grade without bars. The reader's findLinearBands keeps its two scales, so reading does not change | 6.2, 12 |

## Pending
- 1D grading at large X dimensions on real images (no large-X 1D sample in VS_IMAGES yet; the engine tests use drawn symbols).
- engine_tests "UEC from the decoder after ISO 15415: Micro QR M4-L 2 errors" passes in some runs and fails in others on the same code (seen on this branch and before its engine change); find what varies.
