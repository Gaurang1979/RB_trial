# Browser dashboard: the role limits which lines and reports a user sees

- Status: done
- Session: claude dashboard-role-scope, 2026-11-03
- Pull request: #278
- Section 5 item: Browser dashboard: the role limits which lines and reports a user sees
- Components/files: dotnet/Runtime.Host (WebDashboard.cs, Access.cs, RuntimeOptions.cs, Program.cs add-user, Web/app.js, README.md), Runtime.Host.Tests (WebAccessTests.cs, WebDashboardTests.cs), docs/REQUIREMENTS-VISION-SYSTEM.md 6.4 WEB-01..03, UI-UX-SPEC.md 4.17
- Depends on: none. Overlaps #276 in dotnet/Runtime.Host/Access.cs (different parts: #276 WhoAmI, this RuntimeUser and SaveUser); either merge order, the second merges origin/main.

## Section 5
- [x] (done 2026-11-03, PR #278: vs-runtime checks every dashboard call: Live and Trends for every role, Reports for Supervisor and above (Operator only shift reports when Runtime:WebOperatorShiftReports is on), Audit trail for QA and Admin; per-user line list (add-user --lines); rights read from the users file on every call; requirements 6.4 WEB-01..03) Browser dashboard: the role limits which lines and reports a user sees (UI-UX-SPEC.md 4.17); today every signed-in user sees every report.

## Timeline (section 3)
| 2026-11-03 | Browser dashboard scope enforced in vs-runtime: each /web/api route names its page and a route without one is refused; pages by role from PART11-DESIGN's default rights; a user's line list (plant id or report name) closes every page of another line (403, "You may not watch this line"); an Operator's report list and download keep only shift reports, and a refused report is "not found"; a changed role or line counts from the next call and ends a live stream at its next beat; a user removed from the users file is signed out; the page shows only the allowed tabs; 7 new tests, 97 Runtime.Host tests pass | 6.4, 12, PT11-11, PT11-12, PT11-14 |

## Decisions (section 4)
| Dashboard pages by role | Owner's spec (UI-UX-SPEC.md 4.17: "the role only limits which lines and reports they see") with the default rights of PART11-DESIGN.md section 4: Live and Trends for every role (run.view, result.view); Reports for Supervisor, QA, Engineer and Admin; Operator "My shift" only when the plant turns it on (UI-UX-SPEC.md 4.11), never batch reports; Audit trail for QA and Admin (audit.view). A user without a role sees nothing | 6.4 WEB-02, PT11-11 |
| Line scope on the line PC | A user may carry a line list; empty means every line, so existing users keep working. The line is named by its plant id (Plant:Line) or its report line name. A user whose list names neither sees no page of this line | 6.4 WEB-01, PT11-14 |
| Closed by default | Rights are checked by the runtime on every call, not by hiding tabs; a web route that does not name its page is refused, so a new route stays closed until it says who may see it | 6.4 WEB-02, PT11-12 |
| Rights follow the user's record | The session keeps only who signed in; roles and lines come from runtime-users.json on every call, and a removed user's session ends (putting the user back does not revive it) | 6.4 WEB-03 |

## Update log (section 8)
| 2026-11-03 | REQUIREMENTS-VISION-SYSTEM.md 6.4: WEB-01..03 browser dashboard scope; UI-UX-SPEC.md 4.17: what a user sees; dotnet/Runtime.Host/README.md: dashboard scope and add-user --lines | Claude (Mac, branch claude/dashboard-role-scope) |

## Pending
- Browser dashboard scope from the plant server's role sets (PT11-10, roles changed by Admin) and station scope; today vs-runtime uses PART11-DESIGN's default role sets and a per-user line list set with `vs-runtime add-user --lines`.
