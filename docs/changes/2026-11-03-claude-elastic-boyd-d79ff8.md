# Measure ellipse: final fit kept to the searched ring (partial arcs)

- Status: done
- Session: elastic-boyd-d79ff8, 2026-11-03
- Pull request: #283
- Section 5 item: Measure tool: a partial-arc ellipse's final fit is not kept to the searched ring
- Components/files: engine/src/measure/measure_tool.cpp, engine/tests/measure_tests.cpp
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #283: the ellipse's final fit must lie in the searched ring, arc or full: a fit whose rim leaves the ring by more than 1 px is not found) Measure tool: a partial-arc ellipse's final fit is not kept to the searched ring

## Timeline (section 3)
| 2026-11-03 | Measure ellipse: after RANSAC and the geometric refine, the whole fitted rim must lie in [r_inner, r_outer] (1 px slack) about the ring centre, else not found (NG, no wrong oval). Short arcs of a far larger circle used to give a 147 x 52 oval; now not found. Tests: partial arcs of 100, 140 and 220 deg, and the large-circle arc. measure_tests pass | Req 2.4 (Measure tool) |

## Decisions (section 4)
| Ellipse ring limit | The ring is where the oval is expected, so the whole fitted rim must stay in it for partial arcs too; a fit that cannot is not found rather than a wrong oval | Req 2.4 |

## Update log (section 8)
| 2026-11-03 | change note only | Claude (branch claude/elastic-boyd-d79ff8) |

## Verified
- Engine: `measure_tests` built (Release, macOS) and all checks pass, including the new partial-arc ones. Only the `ellipse under 2 ms` timing check flakes when the Mac is busy (load 20-96 from other sessions); passed at 0.365 ms on a quieter run. Other engine tests not run: only `measure_tool.cpp` changed and only `measure_tests` and `vs_measure` use it.
- Bench: not run, the reader does not use the Measure tool; Mac load far above the cores (would be "not judged").
- CI: not run (billing).
