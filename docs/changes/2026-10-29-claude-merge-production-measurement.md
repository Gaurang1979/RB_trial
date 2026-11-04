# One project: production_measurement merged into Vision_System as plant_server/

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #55
- Section 5 item: new
- Components/files: plant_server/ (new, the whole production_measurement repository with its history), CLAUDE.md, README.md, .gitignore, .claude/agents/checker.md, scripts/heavy-pick.sh, docs/CODING-STANDARDS.md, docs/vision-system/PLANT-PLATFORM.md, .github/workflows/plant-server.yml (new)
- Depends on: none

## Section 5
- [x] (done 2026-10-29, PR #55: the whole repository with its 35 commits merged into plant_server/; its 183 tests pass on SQLite and PostgreSQL in the new place; one process, test picker, CI and coding standards for it) One project: merge the production_measurement repository into Vision_System.
- "Owner: where the plant server's code lives": `[x] (done 2026-10-29, owner decision: production_measurement is merged into Vision_System as plant_server/, and the mobile app stays in mobile/ of the same single project)`
- [ ] Owner: delete the production_measurement repository on GitHub once this pull request is merged (nothing else lives there: one branch, no pull requests; check its issues and settings first).
- [ ] Mobile app: the Production module talks to plant_server's API (auth/login, plants, lines, status, alerts) in the same project.

## Timeline (section 3)
| 2026-10-29 | production_measurement merged into Vision_System as plant_server/ with its full history (35 commits): one project for the line PC software, the plant server and the mobile app; 183 plant server tests pass on SQLite and PostgreSQL | PLANT-PLATFORM.md |

## Decisions (section 4)
| One project | Owner, 2026-10-29: "Merge full repo production measurement into vision system. I will delete production measurement repo after that. Make single project." Merged with its history (an ours-merge plus read-tree into plant_server/, so every old commit stays reachable) rather than copied, so blame and history survive the deletion of the old repository | PLANT-PLATFORM.md 6 |
| Plant server process | plant_server/ follows Vision_System's process from now on (claims, change notes, the testing session); its PROJECT_HISTORY.md is kept as history up to the merge; its tests run in a Linux CI job on SQLite and PostgreSQL with Mosquitto | CLAUDE.md |

## Links (section 6)
| Plant server (was production_measurement) | plant_server/ in this repository; its history up to the merge in plant_server/PROJECT_HISTORY.md; its customer presentation sources in plant_server/docs/presentation/ | Merged 2026-10-29 |

## Update log (section 8)
| 2026-10-29 | CLAUDE.md, README.md, CODING-STANDARDS.md, checker and test picker cover plant_server/; PLANT-PLATFORM.md open questions 1 and 2 answered | Claude (cloud, branch claude/merge-production-measurement) |
