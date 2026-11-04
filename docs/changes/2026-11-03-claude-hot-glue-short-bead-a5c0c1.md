# Hot-glue bead: only gaps between hot stretches count (no false NG on a short bead in a longer ROI)

- Status: done
- Session: claude hot-glue-short-bead, 2026-11-03
- Pull request: #310
- Section 5 item: new (pending from docs/changes/2026-11-03-claude-test-checks-tsan.md)
- Components/files: engine/src/thermal.cpp, engine/tests/thermal_tests.cpp, docs/REQUIREMENTS-VISION-SYSTEM.md (section 4 item 5)
- Depends on: none

## Section 5
- New: [x] (done 2026-11-03, PR #310) Hot-glue bead: only gaps between hot stretches count, so a short good bead in a longer ROI is no longer a false NG_BROKEN_BEAD; a real break still fails on the gap and a bead that stops early fails on min_bead_length_px (requirements section 4 item 5, from the pending item in docs/changes/2026-11-03-claude-test-checks-tsan.md)

## Timeline (section 3)
| 2026-11-03 | `vs_thermal_inspect_hot_glue` counts a cold stretch as a gap only when hot glue follows it (cold after the bead's end inside the ROI no longer gives NG_BROKEN_BEAD); good-beads test back on the full 150 px ROI, new tests for a 20 px break (NG_BROKEN_BEAD, gap 20) and a 30 px bead that stops early (NG_BROKEN_BEAD through min_bead_length_px, gap 0); thermal_tests 17/17 pass | 4 (item 5), 12 |

## Decisions (section 4)
| Hot-glue bead gap | A gap is cold between two hot stretches of one bead; cold before the start or after the end is the bead's length, judged by min_bead_length_px. Reason: the trailing cold made every bead shorter than its ROI a false NG, while a bead that stops early is still caught by the length (zero false accepts kept) | 4 (item 5), 12 |

## Update log (section 8)
| 2026-11-03 | Requirements section 4 item 5: hot-melt glue bead gap rule | Claude (Mac, branch claude/hot-glue-short-bead-a5c0c1) |
