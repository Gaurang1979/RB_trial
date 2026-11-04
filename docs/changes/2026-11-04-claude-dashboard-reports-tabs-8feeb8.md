# Browser dashboard Reports: the read-only tabs of UI-UX-SPEC.md 4.11 (Shift, Trends, Find a part, Operator flags) with Print, Save as PDF and Save as Excel

- Status: done
- Session: dashboard-reports-tabs, 2026-11-04
- Pull request: #340
- Section 5 item: Browser dashboard Reports: the read-only tabs
- Components/files: dotnet/Runtime.Host (WebDashboard.cs, Reviews.cs ReviewStore.Recent, Web/index.html, app.js, app.css), dotnet/VisionReports/ResultLog.cs (FindInspections), dotnet/Runtime.Host.Tests/WebReportsTests.cs, docs/REQUIREMENTS-VISION-SYSTEM.md 6.4 WEB-07, docs/vision-system/UI-UX-SPEC.md 4.17
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #340: Reports page tabs Shift (current and last 3 shifts, top 5 reasons, Print, Save as PDF, Save as Excel), Find a part (by code, part id, image name or time, with the image), Operator flags (the reviewed bad parts) and Files; Find a part and Operator flags only for Supervisor and above; the Trends tab of 4.11 is Pending) Browser dashboard Reports: the read-only tabs of UI-UX-SPEC.md 4.11 (Shift, Trends, Find a part, Operator flags) with Print, Save as PDF and Save as Excel; today it lists and downloads the report files.

## Timeline (section 3)
| 2026-11-04 | Browser dashboard Reports tabs (WEB-07): Shift (good, bad, total, good % of the current and last 3 shifts counted as the shift report counts them, top 5 NG reasons chart; Print without the menus, Save as PDF / Excel of any of the 4 shifts: the line PC's own file for a finished shift, else made on demand, one at a time, "busy" answered 429), Find a part (code, text, PLC part id or image name in 1, 7 or 31 days, or within a minute of a time; 50 newest with image and results), Operator flags (the 200 newest NG review decisions: Was good, Confirmed bad, Accepted record only, who, note), Files (as before). Shift for every role with Reports; Find a part and Operator flags for Supervisor and above, 403 for the others; 7 tests | 6.4 WEB-07, UI-UX-SPEC 4.11, 4.17 |

## Decisions (section 4)
| Browser Reports tabs | Shift counts reuse the results log's hourly aggregate (as the Trends page and the shift report count: the PLC's part log when it has parts, else inspections), worked out at most once a minute. Save as PDF / Excel gives the same report the shift reporter writes, by its file name, so the Operator rule of WEB-02 applies unchanged; one report is made at a time and a second request is told "busy" rather than queued, so the line PC never builds several shift reports at once. Find a part matches text with LIKE inside a time window on the time index (no new index on the ledgered table); % and _ are literal. Operator flags show the review store's decisions; agreeing or disagreeing stays in Vision Studio (the browser only watches) | 6.4 WEB-02, WEB-07 |

## Update log (section 8)
| 2026-11-04 | REQUIREMENTS-VISION-SYSTEM.md 6.4 WEB-07 (Reports tabs in the browser); UI-UX-SPEC.md 4.17 Reports row | Claude (MacBook Air, branch claude/dashboard-reports-tabs-8feeb8) |

## Pending
- Browser dashboard Reports "Trends" tab of UI-UX-SPEC.md 4.11: measured values over time with limits, Cp/Cpk colour verdict and drift warnings, Shift / Day / Week and a check picker (needs per-hour measure statistics in the results log; the Trends page today shows parts, yield, cycle time, NG reasons and print quality).
- Browser dashboard Operator flags: also list the operator's "Was OK" flags still waiting for the supervisor once PR #330 (AS-04) has built them; today the tab lists the supervisor's review decisions.
- Browser dashboard Find a part: "Scan code" with the tablet's camera (UI-UX-SPEC.md 4.11); today the code is typed or pasted.
