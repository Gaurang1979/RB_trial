# Browser dashboard: keep the grades across a runtime restart

- Status: done
- Session: grade-trail-persist, 2026-11-03
- Pull request: #314
- Section 5 item: Browser dashboard: keep the grades across a runtime restart
- Components/files: dotnet/Runtime.Host (GradeTrail.cs, LineRuntime.cs, WebDashboard.cs comment), dotnet/Runtime.Host.Tests (new GradeTrailFileTests.cs), docs/REQUIREMENTS-VISION-SYSTEM.md 6.4 WEB-06
- Depends on: none (#303, #284 merged)

## Section 5
- [x] (done 2026-11-04, PR #314: the graded parts of the browser's print-quality trend are kept in the data folder's own grades.jsonl and read back at start within the last 2000 per tool, a week and 64 tools; never in the results ledger; Req 6.4 WEB-06) Browser dashboard: keep the grades across a runtime restart

## Timeline (section 3)
| 2026-11-04 | Browser dashboard grades kept across a runtime restart: `GradeTrail` writes each graded part to `data/grades.jsonl` within a second (off the part's path) and reads it back at start, dropping samples over a week old and keeping the newest 2000 per tool (at most 64 tools); the file is rewritten to those limits at start and at twice their size; torn lines skipped, a bad grade reads as ungraded, a write failure only loses history; 5 new tests | 6.4 WEB-06, 17.3 PQ-01 |

## Decisions (section 4)
| Grades file of its own | The trend's graded parts go to their own JSON-lines file (`grades.jsonl`) in the data folder, not to a table of the chained results ledger (`ResultLog`): it is display data that never decides a part, and the ledger stays untouched. Same limits as in memory (2000 per tool) plus a week, the longest range the dashboard shows | 6.4 WEB-05, WEB-06 |

## Update log (section 8)
| 2026-11-04 | Requirements 6.4: new WEB-06 (grades kept across a restart, their own file, limits, failure handling) | Claude (Mac, branch claude/grade-trail-persist-f93d50) |

## Pending
- Browser dashboard: show the plant server's own grade drift alarm state (needs a call from the line PC to the plant server's alerts; Req 6.4 WEB-06)
- Runtime.Host test `ImageNameLineTests.GetImage_opens_a_saved_image_by_its_name` fails on main as well as on this branch (NotFound "This image is no longer kept"), found 2026-11-04
