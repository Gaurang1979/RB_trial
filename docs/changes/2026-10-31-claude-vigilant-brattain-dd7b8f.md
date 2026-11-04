# Module-scaled local contrast normalisation in place of fixed-tile CLAHE, in our own form (Cognex US10607047 active) (rank 2)

- Status: done
- Session: vigilant-brattain-dd7b8f, 2026-10-31
- Pull request: #169
- Section 5 item: Module-scaled local contrast normalisation
- Components/files: docs/PATENTS.md, docs/vision-system/INDUSTRY-METHODS.md (no engine change: the step was built, measured and not adopted)
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #169: built and measured, not adopted. The bench showed no gain: 1733 -> 1732 reads, and the LOW_CONTRAST misses it was meant for are background regions, not codes) Module-scaled local contrast normalisation in place of fixed-tile CLAHE, in our own form (Cognex US10607047 active) (rank 2)

## Timeline (section 3)
| 2026-10-31 | Module-scaled local contrast (Gaussian local mean and deviation, window about 2 modules from the region's side over the learned module count, on locator candidates only, in place of 4 x 4-tile CLAHE when the module size is known) built and measured. Synthetic faint, unevenly lit QR: 37/40 read against CLAHE 17/40, 0 wrong. Sample images, same binary with and without it, carried per batch: reads 1733 -> 1732, LOW_CONTRAST misses 193 -> 197. batch_check on it: 1726/1868, below the baseline in 3 batches. Not adopted. Engine tests 4/4 passed on it | 12 |
| 2026-10-31 | The LOW_CONTRAST miss list checked by hand: none of its 193 boxes (shrink wrap 113, 48 QR 38, 48 DM 38, 24 QR 4) is read by any of 27 contrast variants (stretch, CLAHE 4 x 4 and 8 x 8, module-scaled at 3 window sizes and 2 gains, each at 1x, 2x and 3x). Their crops are background: conveyor rail, tape, case print, cap tops, carton flaps. The real unread codes on the shrink-wrap case Image_20250125192045895 are white labels with no locator box at all; 9 of 15 read once a crop is centred on them (8 x 8 CLAHE at 2x 6, module-scaled 5, plain stretch 4) | 12 |

## Decisions (section 4)
| Module-scaled contrast | Not adopted. It reads no more codes on the samples, because the regions it was meant for are not codes. The shrink-wrap misses are locator recall (labels never proposed) and small modules, so ranks 1 (lighting) and 8 (locator recall) come first. Revisit only with real faint codes, for example frames from the line after the lighting work. The tried patch is kept outside the repository; the design-around points are in docs/PATENTS.md for legal review | 12 |
| Miss causes | The `--misses` cause counts include locator false positives. LOW_CONTRAST counts mostly background, so it is not a measure of faint codes. Rank methods by unread codes (visible minus read), not by miss-list causes, until the miss list keeps only regions with code evidence | 12 |

## Update log (section 8)
| 2026-10-31 | docs/PATENTS.md: US10607047 row, design-around points (8-bit in and out; claims 1, 19, 20 need a smaller output bit depth) for legal review. INDUSTRY-METHODS.md: rank 2 measured, no gain, LOW_CONTRAST counts are background | Claude (Mac, branch claude/vigilant-brattain-dd7b8f) |

## Pending
- Miss list (`--misses`, diagnose.cpp): report only regions with code evidence (finder, module texture at the expected module size), so background is not counted as LOW_CONTRAST, and re-count the causes in INDUSTRY-METHODS section 1
