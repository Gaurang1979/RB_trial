# Locator to 0.05 px / 0.05 deg with a geometry truth set

- Status: done
- Session: locate-precision, 2026-10-31
- Pull request: #170
- Section 5 item: new
- Components/files: engine (src/fixture/locate_tool.cpp, include/vs_locate.h comment only, tests/locate_tests.cpp, new tests/geometry_truth.hpp)
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-31, PR #170: edge least-squares refinement after the correlation search; geometry truth set generator; tests at 0.05 px / 0.05 deg) Locator (Find the part, vs_locate) to 0.05 px / 0.05 deg with a test at the requirement instead of 1.0 px / 0.5 deg, with the geometry truth set (study 04a, 1.1 and T)`

## Timeline (section 3)
| 2026-10-31 | Locate tool: edge-based least-squares pose refinement (taught edge points with direction, sub-pixel edge search along each normal, Tukey-weighted point-to-line fit of shift and turn). Geometry truth set generator (label drawn 8x, turned and averaged down: known sub-pixel pose; blur, noise, light, gradient, occlusion). On the study set (100 renders, 5 MP, +-15 deg, blur 0.8-1.5, noise 2) the mean and max position error went from 0.373 / 1.250 px to 0.012 / 0.031 px, turn from 0.276 / 0.650 deg to 0.003 / 0.010 deg; repeatability 3 sigma 0.005 px / 0.002 deg; time per locate unchanged (median 55.2 to 55.4 ms, 5 MP, this Mac) | 6, 12 |

## Decisions (section 4)
| Locate: the edge fit only refines | When the fit cannot settle (too few edges agree, edges all one way, a move over 5 px at the rim) the correlation pose stands, and found stays the correlation's decision. A gate on the edge match was tried and dropped: it rejected good parts whose taught pattern holds variable print (a code, a lot number), which the teach advice allows | 12 |
| Locate: one edge estimator for teach and run | Edge points are taught at the fixed point of the same sub-pixel estimator the run-time fit settles on; with two different estimators a part that had not moved was off by 0.09 px | 6 |
| Truth set: light change kept off the camera's limits | A white clipped at 255 moves edges by about 0.1 px for any edge or correlation method; that case is reported (max 0.126 px), not tested. The line's exposure must keep the label white below saturation | 6 |

## Update log (section 8)
| 2026-10-31 | vs_locate.h method comment: the edge refinement | Claude (Mac, branch claude/locate-precision) |

## Pending
- Locate speed to the study target (15 ms for +-15 deg, 200 x 200 px model, 5 MP, T1): the correlation search takes about 55 ms on 5 MP on the development Mac, before and after this change; search at 1/4 resolution first (study 04a, 1.1)
- Locate with up to 30% of the pattern hidden (existing behaviour, before and after this change): on the truth set the correlation finds 40/50, of them 35 to the requirement, 2 off by 0.05-2 px (correlation turn over 1 deg, beyond the edge fit) and 3 "found" 18-120 px from their place (the label's bars repeat every 40 px). A wrong place passed as found is a line-safety risk: keep several coarse candidates and choose by the edge fit, plus the study's margin to the second peak (ambiguous = NG), or the geometric match (study 04a, 1.1 L4 and 1.2)
- Locate edge refinement on large patterns (about 1000 px, 20 MP camera): the fine correlation step is then about 5 px, so the 2 px edge search may not reach and the result falls back to the correlation pose; test on a large pattern and widen the first search if needed
- Locate on the owner's real images: still-part repeatability (100 captures) and the 100% find run (`LOC_IMAGES`, `LOC_ROI` in locate_tests) on a real locate job
