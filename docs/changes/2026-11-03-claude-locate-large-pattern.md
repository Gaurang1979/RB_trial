# Locate edge refinement on large patterns (about 1000 px, 20 MP camera)

- Status: done
- Session: locate-large-pattern, 2026-11-03
- Pull request: #273
- Section 5 item: new (Pending in docs/changes/2026-10-31-claude-locate-precision.md; requirements section 13 Locator row, section 3 tool 1)
- Components/files: engine (src/fixture/locate_tool.cpp, tests/locate_tests.cpp, tests/geometry_truth.hpp), docs/REQUIREMENTS-VISION-SYSTEM.md (section 13 Locator row)
- Depends on: none

## Section 5
- New item: `[x] (done 2026-11-03, PR #273: edge fit coarse to fine; 20 MP truth test with a 960 x 606 px pattern) Locate edge refinement on large patterns (about 1000 px, 20 MP camera) to 0.05 px / 0.05 deg`

## Timeline (section 3)
| 2026-11-03 | Locate on large patterns: a 960 x 606 px pattern on 20 MP (5472 x 3648) is searched on a copy at 0.19 scale, so the correlation pose was 1-3 px and 0.5-0.7 deg off, beyond the 2 px edge search: 10 of 30 truth poses kept the correlation pose (max 3.27 px, 0.74 deg). The edge fit now runs coarse to fine (at the fine search's scale, then about 2.5 times larger, then full size); found poses on the 20 MP truth test (blur 0.8-1.5, noise 2): max 0.013 px, 0.002 deg. The extra levels cost about 1-2 ms, the full-size fit about 5 ms; the whole locate on 20 MP takes median 33.7 ms, max 34.1 ms (budget 50 ms, this Mac, load 3.9). Patterns whose fine search runs at half size or more (up to about 380 px) are unchanged | 6, 13 |

## Decisions (section 4)
| Locate: edge fit coarse to fine on large patterns | When the fine correlation runs below half size (a fine pixel over 2 image pixels), the edge fit first runs on copies of the image and pattern at the fine search's scale and about 2.5 times larger each, each pose within reach of the next, then at full size. Widening the full-size search instead would let the fit lock onto a neighbouring bar of a repeating print. Each level only refines: one that cannot settle leaves the pose as it was, and found stays the correlation's decision | 12, 13 |
| Locate timing in tests judged only on a quiet machine | The large-pattern test checks the median time against the budget only when the load is at most half the cores (as the bench); on a busier machine it prints "not judged" | 11 |

## Update log (section 8)
| 2026-11-03 | Requirements section 13, Locator row: the 0.05 px / 0.05 deg target holds for large patterns (up to about 1000 px on 20 MP) within the time budget | Claude (Mac, branch claude/locate-large-pattern) |
