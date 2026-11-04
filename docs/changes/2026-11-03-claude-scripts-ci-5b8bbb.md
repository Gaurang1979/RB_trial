# CI for this repository's scripts

- Status: done
- Session: scripts-ci, 2026-11-03
- Pull request: #324
- Section 5 item: CI for this repository's scripts
- Components/files: .github/workflows/scripts.yml (new), docs/REQUIREMENTS-VISION-SYSTEM.md (section 17.9 AGR-09), scripts/deps_check_test.py (test made independent of the repository's .NET version)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #324: `.github/workflows/scripts.yml` runs `rollup_test.py`, `coord_test.sh` and `deps_check_test.py` on pull requests and main pushes that change `scripts/**`; Req 17.9 AGR-09) CI for this repository's scripts: run `scripts/rollup_test.py` and `scripts/coord_test.sh` on pull requests that change `scripts/`.

## Timeline (section 3)
| 2026-11-03 | Scripts CI: new `scripts.yml` workflow runs the roll-up, coord.sh and dependency-watch tests on `scripts/**` changes; `deps_check_test.py` no longer fails because the repository moved off .NET 8 (13, 15 and 5 tests pass locally) | 17.9 AGR-09 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.9 AGR-09 (scripts tested in CI) | Claude (Mac, branch claude/scripts-ci-5b8bbb) |
