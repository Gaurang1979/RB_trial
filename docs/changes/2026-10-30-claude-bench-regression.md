# Engine on main reads 3 fewer sample codes than the anomaly baseline: not a regression (machine load)

- Status: done
- Session: claude/bench-regression, 2026-10-30
- Pull request: #102
- Section 5 item: Engine merged on main 2026-10-30 reads 3 fewer sample codes
- Components/files: docs/changes only (no engine change, baseline not refreshed)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #102: no engine regression; with an unlimited time budget main 5652c1b and the anomaly baseline a4c4edd read the same codes per image, 24 QR 377/384 and 48 DM 65/95, at the same CPU time; the 370/60 was machine load on the time-budgeted reads) Engine merged on main 2026-10-30 reads 3 fewer sample codes than the anomaly baseline (24 QR 370 vs 372, 48 DM 60 vs 61): find which PR and fix, then refresh truth/batch-baseline.csv

## Timeline (section 3)
| 2026-10-30 | Read "regression" on main checked without the time budget: main (5652c1b) and the anomaly baseline (a4c4edd) read identical per-image counts on the 24 QR (377/384) and 48 DM (65/95) batches, total CPU 52.5 s vs 51.0 s. The 370 vs 372 and 60 vs 61 came from the load average of 55-80 on 8 cores (16 sessions) starving the time-budgeted reader; at that load main read as few as 49/384 with `--budget auto`. No engine change; truth/batch-baseline.csv kept | 12 |

## Decisions (section 4)
| Batch check under load | Read counts with `--budget auto` measure CPU contention when the Mac is busy, so a read-count drop is checked with an unlimited budget (`--budget 60000`) before any bisect, and the baseline is never refreshed under load. The load guard in batch_check.py is claude/batch-check-load's item. | 12 |

## Update log (section 8)
| 2026-10-30 | docs/changes note: the 2026-10-30 read drop was machine load, not engine code | Claude (Gaurang's MacBook Air, branch claude/bench-regression) |

## Evidence

Same images, the batch check's batches and carried reader (`bench --carry --runs 1`), with the
budget raised from `auto` to 60 000 ms so time no longer limits the reads. Load average 70-80
during both runs.

| Batch | a4c4edd (anomaly baseline) | 5652c1b (main) | Baseline CSV (`--budget auto`) |
| --- | --- | --- | --- |
| 24 QR | 377 / 384 | 377 / 384 | 372 |
| 48 DM | 65 / 95 | 65 / 95 | 61 |

Short images, identical on both builds: Image_20250311101601937 23/24, Image_20250311101614724
23/24, Image_20250311152948003 19/24, Image_20220312160710251 22/47, Image_20230731102244944
43/48. CPU time for both batches (user): a4c4edd 51.0 s, main 52.5 s (within run-to-run noise).

With `--budget auto` at the same load, main read 49/384 and 2/95, so wall-clock read counts
cannot be judged while other sessions build. The batch-check author's own reruns
(claude/batch-check-load) agree: a4c4edd's successor e12ee64 read 369-371 of the 24 QR codes
under load, and main ranged from 1714 to 334 of 1868 in total.
