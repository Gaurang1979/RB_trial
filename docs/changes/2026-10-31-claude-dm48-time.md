# Whole-image pass in time-boxed tiles: 48 DM cans case within 500 ms

- Status: done
- Session: dm48-read, 2026-10-31
- Pull request: #156
- Section 5 item: new (Pending of PR #153: "48 DM cans (Image_20230731102244944): 755 ms carried against 500 ms")
- Components/files: engine/src/decode.cpp (whole-image pass)
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-31, PR #156: the whole-image pass reads 2 x 2 tiles, each started only when it fits the time left; 48 DM slowest case 755 -> 456 ms, every batch at or above its baseline) Whole-image pass in time-boxed tiles: 48 DM cans case within 500 ms`

## Timeline (section 3)
| 2026-10-31 | The whole-image zxing pass could not stop part-way and was costed at 7 ms per megapixel; on 20 MP Data Matrix cases it took 250-470 ms, so a fresh reader ran the cans case to 708 ms. It now reads 2 x 2 overlapping tiles, brightened then raw, tiles with unread regions first, each job only when it fits the time left; the first guess for Data Matrix is 20 ms per megapixel. Bench: 48 DM slowest 755 -> 456 ms, 45 QR 403 -> 327 ms, 48 QR 416 -> 370 ms, reads at or above baseline | Requirements 17.1 |

## Decisions (section 4)
| Time budget over one more read | A part of the whole-image pass now starts only when it fits the case's time budget. On a busy Mac main read 1-2 more crate caps by running to 600 ms against 500; this reads 15-16 within 422-488 ms (quiet Mac: both 20-21). The owner's time per case is a target the line depends on, so the budget wins | Requirements 17.1 |

## Update log (section 8)
| 2026-10-31 | engine/src/decode.cpp: tiled whole-image pass with per-job time check; Data Matrix cost guess 20 ms/MP | Claude (Mac, branch claude/dm48-time) |

## Pending
- 48 DM crate (Image_20220312160710251): 26-27 of 47 dark caps unread at the line target. A re-shoot with about 4x exposure is the main fix (RESEARCH-NEXT-GENERATION R0).
- Refresh truth/batch-baseline.csv with the faster times (48 DM slowest about 456 ms) on a quiet Mac: the --update run of 2026-10-31 came out lower than the passing check (24 QR 360 against 373, the Mac busy), so it was not committed.
