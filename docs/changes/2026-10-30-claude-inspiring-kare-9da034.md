# Test once: batch check against a saved baseline (CLAUDE.md step 11)

- Status: done
- Session: claude/inspiring-kare-9da034 (batch testing), 2026-10-30
- Pull request: #89
- Section 5 item: new
- Components/files: CLAUDE.md, .claude/agents/tester.md, tools/bench/batch_check.py, truth/batch-baseline.csv, docs/BATCH-TEST-RESULTS.md
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #89) Test once: batch check against a saved baseline (CLAUDE.md step 11)

## Timeline (section 3)
| 2026-10-30 | Batch test of 53 sample images by case size and code type: 1729 / 1868 visible codes read (92.6 %), 48 DM fails on reads and time. tools/bench/batch_check.py runs it in one command against truth/batch-baseline.csv and skips when the code already passed | 12 |

## Decisions (section 4)
| Test once | Owner 2026-10-30: "automate one time testing and avoid second duplicate testing if not required to save token and time". CLAUDE.md step 11; the same rule is in secure_me (#1487). | CLAUDE.md 10, 11 |

## Update log (section 8)
| 2026-10-30 | CLAUDE.md step 11 "Test once"; docs/BATCH-TEST-RESULTS.md | Claude (Gaurang's MacBook Air, branch claude/inspiring-kare-9da034) |

