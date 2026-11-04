# Read text without a format, expected text or length and charset rule refused at save unless report only

- Status: done
- Session: text-tool-decides, 2026-10-31
- Pull request: #181
- Section 5 item: new (TEST-REPORT-2026-10-30.md Pending: "Read text without a format or expected text can pass a misread line")
- Components/files: dotnet/VisionCore (TextTool.cs, ToolModel.cs, Job.cs), dotnet/VisionStudio (Teach, Run and Test pages, ToolNotice service, en.json, app.css), VisionCore.Tests, VisionStudio.Tests, docs/vision-system/TOOL-SPECIFICATIONS.md 7.4
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #181: a Read text tool needs a format on every line, not one of only {?} or {X} fields, or is set report only and never decides OK/NG; refused at save, setup NG at run; a camera of only report-only tools and a Check text line taking its text from one refused; Vision Studio Teach box, warning and tool-list tag, ⓘ result rows; explain.text.no_rule and explain.text.report_only) Read text without a format, expected text or length and charset rule refused at save unless report only.

## Timeline (section 3)
| 2026-10-31 | Read text that decides OK/NG must have a format on every line (not only any-character fields), or be report only (shown, never deciding); refused at save, setup NG at run for older jobs; Vision Studio Teach and result tables show it (PR #181) | 12; TOOL-SPECIFICATIONS 7.4 |

## Decisions (section 4)
| Read text that decides | Free reading passes a misread line (22 of 103 wrong laser lines scored at or above strictness 60), so a deciding Read text tool needs a format on every line. A format of only {?} fields and {X} without a count is free reading in disguise and does not count; printed text, {D}, {A}, a counted {X:6}, a date, a price or {re:} does ({re:} is the engineer's own rule). Expected text from the job, PLC or another tool is Check text, which always decides | 12; TOOL-SPECIFICATIONS 7.4 |
| Report only | A Read text tool may be set report only: its result passes, is marked ReportOnly and shown as ⓘ (never OK), and the part is decided by the other tools. A camera whose enabled tools are all report only is refused at save and NG at run (never OK with nothing checked); a Check text line cannot take its expected text from a report-only tool (a free read cannot vouch for another read). Report only is refused in Check text mode | 12 |
| Layout is not a rule | A text layout (PR #160, "3 6") counts characters but not which ones, so a Read text tool with a layout and no format is still free reading and is refused unless report only |  12 |
| Older jobs | A job saved with a free-reading Read text tool fails every part at run with explain.format.setup and the reason (line safety: a job in doubt is NG and the line is told), rather than being turned into report only silently | 12 |

## Update log (section 8)
| 2026-10-31 | TOOL-SPECIFICATIONS 7.4: Read text that decides OK/NG, Report only | Claude (Mac, branch claude/text-tool-decides) |

## Pending
- Runtime contract (Runtime.Host LineRuntime, V1.ToolResult): carry ReportOnly, so the Avalonia app and the browser view show a report-only tool as information, not OK.
