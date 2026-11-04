# Wrong reads on main's own reader (bench fails since #168)

- Status: done
- Session: reverent-euler-ce7fad, 2026-10-31
- Pull request: #189
- Section 5 item: new
- Components/files: truth/codes.csv
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #189: the bench's wrong reads on main were 3 true codes missing from truth/codes.csv, each read by plain zxing on its crop and inside its serial run; hashes added, no decoder change) Wrong reads on main's own reader: the bench failed on texts from single-frame steps (`full`, `full-bright`, `deblur2`) since PR #168

## Timeline (section 3)
| 2026-10-31 | Bench of main b195f95 (`batch_check.py --force`, under the heavy-test lock) found 3 wrong reads, not 2: 48 DM caps Image_20220312160710251 (`full`, hash 04a89a4404eb), 48 DM caps Image_20230731102244944 (`deblur2`, 1da1aaf6ee7f), excise QR Image_20250125194744613 (`full-bright`, 1b464392fd01). All three are true codes that truth lacked, read only on runs with time left for the slower steps: an independent reader (Homebrew ZXingReader, plain zxing-cpp without our soft paths) reads the same text on a crop at the engine's position (the first with no error correction used, the excise QR with 2 of version 1-M's 5 correctable errors, the third after a plain unsharp mask, 14 of 96 settings, always the same text); the two caps texts fill gaps in their image's serial run (between 207 and 211 in the owner's 180-226; between 17404 and 17406). Hashes added to truth/codes.csv; no decoder change | 12 |

## Decisions (section 4)
| Excise label 511 added to truth | The excise case Image_20250125194744613 was written up as the run 499-509 (PR #168 follow-ups), but only 499, 502 and 507 were named by the owner and truth held 3 of its 11 texts. The QR at (2022, 1821) reads as 511 with plain zxing on the crop (version 1-M, 2 of its 5 correctable errors used, same form as the case's other texts); a Reed-Solomon miscorrection landing on a well-formed serial two past the run is not credible, so it is kept as true and the run is not 499-509 exactly. Owner to confirm by eye (Pending) | 12 |

## Pending
- Owner check: excise case Image_20250125194744613 holds label 511 (QR at about x 2022, y 1821), so its 11 distinct codes are not exactly 499-509
