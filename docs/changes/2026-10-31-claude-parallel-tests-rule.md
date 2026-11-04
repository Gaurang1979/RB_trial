# CLAUDE.md rule 13: only timed runs take the heavy-test lock; builds and other tests run in parallel by load

- Status: done
- Session: parallel-tests-rule, 2026-10-31
- Pull request: #212
- Section 5 item: new (owner, 2026-10-31: "multiple testing sessions can be done automatically as per mac load. Claude can decide to utilize full capacity of mac.")
- Components/files: CLAUDE.md
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #212: only timed runs take the heavy-test lock; builds and other tests run in parallel by load; sessions start by load, with no session cap) Parallel testing by Mac load (owner, 2026-10-31)

## Timeline (section 3)
| 2026-10-31 | CLAUDE.md rules 11-13: the heavy-test lock covers timed runs only (bench, soak, ocr_bench gates, timed end-to-end runs); builds, unit tests, sanitizers, fuzzers and leak checks run in parallel by load; while the lock is held no new heavy CPU work starts; sessions (development or testing) start while the 5-minute load is under the cores plus 2, with no session cap | Process |

## Decisions (section 4)
| Parallel testing | Tests that do not judge time run side by side, as many as the load allows; only timed runs queue on the lock. Before this, every test queued on one lock and sessions waited hours (#174 behind five others) while the Mac was often idle | CLAUDE.md rules 11, 13 |
| Load target | Start work while the 5-minute load is under the cores plus 2 (10 on the 8-core Mac) and memory pressure is normal; while the lock is held start nothing heavy, so the bench (not judged above half the cores before, cores plus half during) gets a quiet Mac. Replaces "load under 12, fewer than about 15 sessions" | CLAUDE.md rules 12, 13 |

## Update log (section 8)
| 2026-10-31 | CLAUDE.md rules 11, 12, 13 | Claude (Mac, branch claude/parallel-tests-rule) |

## Pending
- scripts/fuzz-engine.sh (PR #174) takes the heavy-test lock for its whole run; fuzzing is not timed, so it should run without the lock (CLAUDE.md rule 13)
- docs/PROJECT-HISTORY.md section 7 item 13 and the process rows in sections 3 and 4 still describe one testing session and `tested-pass` merges, replaced by test once on 2026-10-30

