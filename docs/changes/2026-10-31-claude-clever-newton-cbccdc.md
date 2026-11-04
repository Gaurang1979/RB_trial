# Runtime contract carries ReportOnly and a code grade score per tool

- Status: done
- Session: clever-newton-cbccdc, 2026-10-31
- Pull request: #199
- Section 5 item: Runtime contract (Runtime.Host LineRuntime, V1.ToolResult): carry ReportOnly
- Components/files: proto/visionstudio/remote/v1/runtime.proto (ToolResult fields 9, 10), dotnet/Runtime.Host (LineRuntime.AddMeasures, JobDesigner.TryJob), dotnet/Studio (runtime models and mapping, ToolMark, Run last part, NG viewer, Engineer workspace tree, results and margin chart, en.json), Runtime.Host.Tests, Studio.Tests
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #199: `ToolResult.report_only = 9` filled by the line and TryJob; Vision Studio shows a report-only tool as ⓘ Info in the Run screen's last part, the NG viewer, the job tree and the results list, and a failed tool stays ✗ NG whatever it says) Runtime contract (Runtime.Host LineRuntime, V1.ToolResult): carry ReportOnly, so the Avalonia app and the browser view show a report-only tool as information, not OK.
- "Runtime: a code grade score per tool in TryJob": `[x] (done 2026-10-31, PR #199: ToolResult.code_grade = 10, the lowest overall grade of a graded Read code, 4 = A .. 0 = F, absent when not graded or any code could not be graded; TryJob grades every picture; the Engineer workspace margin chart shows a code tool with a lowest grade, one band per grade, safe a whole grade above the limit)`

## Timeline (section 3)
| 2026-10-31 | Runtime contract carries report only and a code grade per tool (runtime.proto ToolResult fields 9, 10, added only): the line and TryJob fill them; Vision Studio shows report-only tools as information and gives graded code tools the margin chart over "Try all pictures". Runtime.Host.Tests and Studio.Tests pass | 12, UI-UX-SPEC 4.19 |

## Decisions (section 4)
| Report only in the app | A tool result is ✗ NG when it failed, ⓘ Info when it passed as report only, ✓ OK otherwise: a report-only tool that errs is never hidden as information (line safety). An older runtime leaves report_only false, so its results look as before | 12 |
| Code grade in the contract | code_grade is the lowest overall grade over the tool's codes (the "grade" value of the part's result), 4 = A .. 0 = F; absent, never 0, when the part was not graded or any of its codes could not be graded, so a missing grade is never read as F and counts as not safe on the margin chart | 12, UI-UX-SPEC 4.19 |
| TryJob grades every picture | A try sets GradeEvery to 1 for its run (the line still samples): the engineer's margin needs a grade on every picture, and a tried picture is judged on its grade as the line's sampled parts are. A code tool gets the margin chart only with a lowest grade (A-D); with grading recorded but no limit there is nothing to be close to | 12, UI-UX-SPEC 4.19 |

## Update log (section 8)
| 2026-10-31 | proto/visionstudio/remote/v1/runtime.proto and SDK-API.md 11.1: ToolResult report_only and code_grade | Claude (Mac, branch claude/clever-newton-cbccdc) |

## Pending
- Engineer workspace: the lines read of a report-only Read text tool still show ✓ or ✗ per line; show them as information too.
- Browser view of the runtime (view-only dashboard over the runtime contract): show a tool with report_only as information, never as OK, when that client is built; none reads the runtime contract yet.
