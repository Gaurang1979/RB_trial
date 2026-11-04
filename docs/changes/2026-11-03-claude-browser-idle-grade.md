# Browser dashboard: idle sign-out in the browser, print-quality trend and grade drift status

- Status: done
- Session: browser-idle-grade, 2026-11-03
- Pull request: #299
- Section 5 item: Browser dashboard: sign out after 15 min without use; print-quality (grade) trend and grade drift (from the Pending lists of PR #279 and #264); Req 6.4 WEB-04, WEB-05
- Components/files: dotnet/Runtime.Host (WebDashboard.cs, new GradeTrail.cs, LineRuntime.cs, RuntimeHost.cs, Web/), dotnet/Runtime.Host.Tests, docs/REQUIREMENTS-VISION-SYSTEM.md 6.4
- Depends on: none. Overlap: other sessions touch runtime start in Runtime.Host (ledger verify, changeover digest); this change only adds a hook in LineRuntime's result loop

## Section 5
- [x] (done 2026-11-03, PR #299: the browser dashboard signs out after 15 min without the person's input, its polling never counts; Trends page shows each graded Read code tool's grade over time and its drift status, read only, with the line and role limits of WEB-01..03) Browser dashboard: sign out after 15 min without use in the browser itself; print-quality (grade) trend and grade drift status
<!-- When done (forms in the README's roll-up rules): "- [x] (done YYYY-MM-DD, PR #n: ...) <the item's text>" replaces the item named above; "- [ ] ..." adds a new item; any other line is added under the item as an update. -->

## Timeline (section 3)
| 2026-11-03 | Browser dashboard: idle sign-out after 15 min of no key, pointer, touch or scroll (`/web/api/me` gives `idleSignOutS`); `GET /web/api/quality` and a Print quality card on Trends with each tool's hourly grade (samples, mean, worst, ungraded) and drift status by the plant server's rule; 11 new tests | 6.4, 17.3 |
<!-- | YYYY-MM-DD | what was built and verified | requirement sections | -->

## Decisions (section 4)
| Quality on the Trends page | The print-quality view is a card on Trends (same rights and line limit), not a new tab; the route is /web/api/quality and a new route stays closed until it names a page | 6.4 WEB-02, WEB-05 |
| Grades kept in memory | Last 2000 graded parts per tool since the runtime started, so a restart empties the trend; the plant server holds the long history (PQ-01). No change to the results ledger | 17.3 PQ-01, 6.4 WEB-05 |
<!-- | Topic | Decision and reason | requirement sections | -->

## Update log (section 8)
| 2026-11-03 | REQUIREMENTS 6.4 WEB-04, WEB-05 (new) | Claude (Mac, branch claude/relaxed-bell-704600) |
<!-- | YYYY-MM-DD | what this pull request changed in the docs | Claude (<machine>, branch <branch>) | -->

## Pending
<!-- Follow-ups this session leaves, one per line: "- Unit tests for ...". Each becomes an open section 5 item. Leave the section out if none. -->
- Browser dashboard: keep the grades across a runtime restart (needs a table in the results log, after the ledger work lands) and show the plant server's drift alarm state.
- Owner: may a wall display (Factory display role) skip the 15 min browser idle sign-out, as the plant server lets it (PT11-08)? Built so every browser signs out.
- Browser idle sign-out: a browser test of the timer (no Node on the build Mac); today the test checks the served script's rules.

## Verified
- dotnet build Runtime.Host.Tests: 0 errors, 0 warnings. Tests: WebQualityTests, WebDashboardTests, WebAccessTests, ToolMeasuresTests, LineTests: 32 passed, 0 failed. Skipped: PlcLineTests and OneClickJobTests fail here because the native codereader library is not built in this worktree (not touched by this change). Timer not run in a browser. CI: not run (billing).
