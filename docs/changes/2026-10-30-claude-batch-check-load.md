# Batch check: refuse to judge a run on a busy machine (load guard)

- Status: done
- Session: claude/batch-check-load, 2026-10-30
- Pull request: #103
- Section 5 item: new
- Components/files: tools/bench/batch_check.py, CLAUDE.md (step 11), .claude/agents/tester.md, docs/changes/2026-10-30-claude-inspiring-kare-9da034.md (own note: false Pending item removed)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #103) Batch check: refuse to judge a run on a busy machine (load guard)

## Timeline (section 3)
| 2026-10-30 | batch_check.py load guard: waits up to 10 min for the load to fall to half the cores, and returns 2 (not judged, no pass or baseline saved) when the Mac is busy before or during the run. At a load of 59-78 on 8 cores, a known-good commit failed 3 times and main's tip read 17.9 % | 12 |

## Decisions (section 4)
| Bench on a busy Mac | A busy Mac fails good code, because reads run against a time budget. A result from one is neither a pass nor a failure, and the baseline is refreshed only on a quiet Mac. The PR #89 Pending item (3 fewer codes on main) was load noise and is removed | CLAUDE.md 11 |

## Update log (section 8)
| 2026-10-30 | CLAUDE.md step 11: exit 2 is not judged | Claude (Gaurang's MacBook Air, branch claude/batch-check-load) |

