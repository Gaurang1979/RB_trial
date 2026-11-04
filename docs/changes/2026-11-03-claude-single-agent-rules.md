# One rule file for every agent (AGENTS.md), branch prefix per agent

- Status: done
- Session: Claude cloud session, 2026-11-03
- Pull request: #265
- Section 5 item: new
- Components/files: AGENTS.md (all rules), CLAUDE.md and GEMINI.md (import AGENTS.md), scripts/coord.sh (AGENT_PREFIXES), scripts/coord_test.sh, docs/REQUIREMENTS-VISION-SYSTEM.md (17.9), README.md and the files that cited "CLAUDE.md rule N" (now AGENTS.md, same numbers)
- Depends on: #261 (merge first). #262 merges after this one, reworked: its rule-file edits dropped, its product items in the requirements only, no OTP secret

## Section 5
- [x] (done 2026-11-03, PR #265: Req 17.9 AGR-01..07) One rule file for every agent: AGENTS.md holds every rule for Claude Code, Antigravity, Gemini, Codex and others; CLAUDE.md and GEMINI.md import it; branches are named <agent>/<topic> and coord.sh treats all agents alike.

## Timeline (section 3)
| 2026-11-03 | The agent rules are one file, AGENTS.md, read by every agent; CLAUDE.md and GEMINI.md import it; coord.sh cleans up and checks the branches of claude, antigravity, gemini and codex alike | Req 17.9 |

## Decisions (section 4)
| One rule file | Owner, 2026-11-03: "make single file which must be followed by claude, antigravity and others". AGENTS.md (the cross-tool standard) holds every rule; CLAUDE.md and GEMINI.md are one-line imports (`@AGENTS.md`). Rule numbers 0-14 are kept, so "rule N" references stay right | Req 17.9 AGR-01 |
| Owner-only merge | #261's rule (REQ-05) is carried over unchanged into rule 0; #261 merges first | Req 17.6 REQ-05; 17.9 AGR-02 |
| Claude-only part | The Opus / Sonnet / Haiku helper table and the .claude hooks are in a marked "Claude Code only" section; other agents use their own strongest model for the same work | Req 17.9 AGR-03 |
| Branch per agent | Branches are `<agent>/<topic>` with the agent one of claude, antigravity, gemini, codex (`AGENT_PREFIXES` in coord.sh); a branch of no agent is never cleaned up | Req 17.9 AGR-04 |
| Testing for all | Every agent tests once, scoped to the changed and affected parts; the old AGENTS.md fixed suite list is dropped | Req 17.9 AGR-05 |
| Shared Mac | The load limit, the heavy-test lock and archiving a session once merged hold for every agent | Req 17.9 AGR-06 |
| Rules from #262 | One common requirement document and the Vision Studio Workbench as the one UI baseline go into AGENTS.md rule 0; #262's product items (PROT-21..37) go into the requirements, not the rule files; no secret in the repository | Req 17.9 AGR-08 |
| Citations | Commits, pull requests and notes cite the requirement item they serve (e.g. "Req 17.7 ENV-05"), not REQ-01..04 | Req 17.9 AGR-07 |

## Update log (section 8)
| 2026-11-03 | AGENTS.md is the one rule file; CLAUDE.md, GEMINI.md import it; README, requirements and the docs and scripts that cited CLAUDE.md now cite AGENTS.md | Claude (cloud, branch claude/single-agent-rules) |

## Pending
- PR #262: rework after #265 merges: drop its CLAUDE.md, AGENTS.md and GEMINI.md edits, keep its PROT-21..37 items in the requirements only, and take the factory OTP master value out of the repository (and rotate it if it is real).
- docs/PROJECT-HISTORY.md section 7 still describes the process as CLAUDE.md's: point it at AGENTS.md in the next history edit (agents may not edit that file).
- Antigravity, Gemini and Codex have no hook that runs `scripts/coord.sh cleanup` or `scripts/merge-guard.sh`; they follow rules 0 and 9 by hand. Add their hooks where the tool supports them.
- secure_me keeps its own CLAUDE.md; rule 14 says both repositories hold it. Make secure_me's rules one AGENTS.md too if the owner wants the same there.
