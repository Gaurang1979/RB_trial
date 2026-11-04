# Test once, no tested-pass: a pull request is marked ready only when its own one test passed; the owner merges ready pull requests

- Status: done
- Session: OCV/OCR barcode module development, 2026-10-30
- Pull request: #105
- Section 5 item: new
- Components/files: CLAUDE.md (steps 1, 6, 9, 10, 11), scripts/merge-guard.sh, scripts/coord.sh (cleanup), scripts/heavy-pick.sh (label removed), .claude/agents/tester.md (removed), .claude/agents/checker.md, .github/pull_request_template.md, README.md, docs/CODING-STANDARDS.md, docs/vision-system/DEPENDENCIES.md, scripts/deps-check.py, tools/bench/batch_check.py
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #105: CLAUDE.md steps 1, 6, 9, 10 and 11; merge-guard refuses only drafts and claims; cleanup no longer waits for a label; tester agent and heavy-pick label removed) Test once, no tested-pass: a pull request is marked ready only when its own one test passed; the owner merges ready pull requests.

## Timeline (section 3)
| 2026-10-30 | Test once without a testing session: the session that makes a change tests it once and marks it ready only when nothing more needs testing; no tested-pass label | CLAUDE.md 10, 11 |

## Decisions (section 4)
| No tested-pass label | Owner, 2026-10-30: "tested-pass concept not required as already today testing method defined to test any developed part only once. If further testing required then do not mark PR ready for merge. Keep this rule for both repo Secure_me and Vision_System." Ready now means mergeable; a pull request that still needs a test (bench not judged, hardware, real images, soak) stays a draft. The merge guard refuses drafts and claims; worktrees go once merged or closed | CLAUDE.md 6, 9, 10, 11 |

## Update log (section 8)
| 2026-10-30 | CLAUDE.md steps 1, 6, 9, 10, 11; README, CODING-STANDARDS, DEPENDENCIES, PR template, checker agent: test once by the session that made the change, no testing session or tested-pass label | Claude (Mac, branch claude/no-tested-pass) |

## Pending
- scripts/heavy-pick.sh does not parse under macOS bash 3.2 (a case pattern inside $( ) at the plant_server tests line); on main before this change.
