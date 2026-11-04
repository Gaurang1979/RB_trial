# scripts/fuzz-engine.sh runs without the heavy-test lock

- Status: done
- Session: claude/fuzz-no-lock-4875f4, 2026-11-03
- Pull request: #320
- Section 5 item: new
- Components/files: scripts/fuzz-engine.sh, this note
- Depends on: none
- Requirement: Req 17.9 AGR-06 (heavy-test lock for timed runs only; AGENTS.md rule 13)

## Section 5
- [x] (done 2026-11-03, PR #320: the lock, its refresher and its trap removed; the script only waits to start while a fresh lock of a timed run exists, never takes or removes it) scripts/fuzz-engine.sh (PR #174) takes the heavy-test lock for its whole run; fuzzing is not timed, so it should run without the lock (CLAUDE.md rule 13)

## Timeline (section 3)
| 2026-11-03 | scripts/fuzz-engine.sh no longer takes the heavy-test lock (fuzzing is not timed); it waits to start while a timed run holds a fresh lock, so its builds and fuzzers do not spoil that run's timings | 17.9 AGR-06 |
