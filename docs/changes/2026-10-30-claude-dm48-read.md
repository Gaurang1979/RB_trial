# Data Matrix 48-can batch: deblur second try from the blurred start

- Status: done
- Session: dm48-read, 2026-10-30
- Pull request: #153
- Section 5 item: new (Pending of docs/changes/2026-10-30-claude-full-system-test.md: "Data Matrix 48-can batch: 61 / 95 read and 738 ms on the slowest image (target 500 ms).")
- Components/files: engine/src/decode.cpp (deblur steps); truth/batch-baseline.csv
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-30, PR #153: Data Matrix deblur steps try the blurred start after the Wiener start; 48 DM 62/95 at the line targets (61 before) and 71/95 at a 20 s budget (65 before); baseline refreshed) Data Matrix 48-can batch: deblur second try from the blurred start`

## Timeline (section 3)
| 2026-10-30 | 48 DM crate: "46 now read" in truth/samples.csv came from repeated bench runs learning from the same image; the honest read was 27 on 2026-10-23 and 22 since b4f6e4a (Wiener deblur start, 2026-10-27), found by bisect. The deblur steps now try the old blurred start when the Wiener start reads nothing: crate 28/47, cans 43/48 at 20 s, every read in the serial run; 62/95 at the line targets. Data Matrix only: on QR it cost one code on each of 3 images of the 24 QR batch | Requirements 17.1 |

## Decisions (section 4)
| Two deblur starts | The Wiener start and the blurred start read different codes (crate: 6 only from the blurred start; cans: 2 only from Wiener). Both are tried for Data Matrix, the second only when the first reads nothing and the first pass has 15 ms left. Not for QR, where the time is worth more to the later passes | Requirements 17.1 |

## Update log (section 8)
| 2026-10-30 | engine/src/decode.cpp: deblurSigma helper, deblur(…, fromBlurred), second deblur try for Data Matrix; truth/batch-baseline.csv refreshed | Claude (Mac, branch claude/dm48-read) |

## Pending
- 48 DM crate (Image_20220312160710251): 19 of 47 dark caps still unread at 20 s. The picture is badly underexposed and the labels bend over the caps: a re-shoot with about 4x exposure on the MV-CS200 is the main fix (RESEARCH-NEXT-GENERATION R0).
- 48 DM cans (Image_20230731102244944): 755 ms carried against 500 ms; the carried reader is slower than a fresh one (258 ms median) on this image.
- truth/samples.csv: the crate note "46 now read" is wrong; honest is 28 (bench --runs 1).
- engine_tests aborts about 1 run in 3 in the DataBar decodability test (begin and end of two temporary vectors); the one-line fix is in an open grading pull request, not on main yet.
