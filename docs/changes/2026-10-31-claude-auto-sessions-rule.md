# CLAUDE.md rules 12 and 13: sessions start themselves for development work; heavy-test lock

- Status: done
- Session: auto-sessions-rule, 2026-10-31
- Pull request: #172
- Section 5 item: new (owner, 2026-10-31: "For any development requirement claude can start nested session automatically.")
- Components/files: CLAUDE.md
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #172: CLAUDE.md rules 12 and 13) Sessions start themselves for any development requirement (owner, 2026-10-31); heavy-test lock for bench, OCR gate, training and soak.

## Timeline (section 3)
| 2026-10-31 | CLAUDE.md rule 12 (the main session starts sessions for development work by itself, by load, and archives merged ones) and rule 13 (heavy-test lock, used by every session since 2026-10-30 night) | Process |

## Decisions (section 4)
| Starting sessions | The owner allowed the main session to start a nested session for any development requirement without asking (2026-10-31); it starts them by Mac load and only for items free of owner input | Process |
| Heavy-test lock | Timed tests run one at a time under /tmp/vs-heavy-test.lock; other sessions pause builds meanwhile. Without it the load stayed 30-130 on 8 cores and benches came back not judged for hours (2026-10-30) | CLAUDE.md rules 11, 13 |

## Update log (section 8)
| 2026-10-31 | CLAUDE.md rules 12 and 13 | Claude (Mac, branch claude/auto-sessions-rule) |

