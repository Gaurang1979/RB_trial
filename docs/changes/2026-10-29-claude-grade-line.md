# Production grading on the line: grade every Nth code, grades kept with each result and sent to the plant server (no PDF on the line)

- Status: done
- Session: Claude cloud session, 2026-10-29; finished by Claude desktop (next-batch), 2026-10-30, on the owner's request
- Pull request: #70
- Section 5 item: new
- Components/files: dotnet/VisionCore (Tools/CodeTool.cs: GradeEvery, RecordGrades, IsSample, GradeValues, ISO/IEC TR 29158 grading switched with the samples; Tools/ToolModel.cs ToolResult.Values; Jobs/Job.cs JobResult.Values), dotnet/VisionCore.Tests (LineGradingTests.cs new), dotnet/VisionStudio (Services/Station.cs: the part's values through RunCycle/CountPart to the plant result, also for PLC camera triggers; Teach page; i18n/en.json), docs/vision-system/TOOL-SPECIFICATIONS.md 6
- Depends on: none (#69 changes Teach.razor and en.json at other lines)

## Section 5
- [x] (done 2026-10-29, PR #70: the Read code tool grades every part or one in N, keeps the grades with each result and sends them to the plant server with the inspection result; no PDF on the line; recorded grades follow the tool's grade standard (ISO 15415 or ISO/IEC TR 29158); 6 tests, one with the real engine) Production grading on the line: grade every Nth code, grades kept with each result and sent to the plant server (no PDF on the line).

## Timeline (section 3)
| 2026-10-29 | Grading on the line as a measurement: one part in N graded, grades with each result and in the plant server's inspection results; reports stay off the line | TOOL-SPECIFICATIONS.md 6 |

## Decisions (section 4)
| No PDF on the line | Owner, 2026-10-29: "For all barcodes being inspected reports in pdf is not required. Cognex verifier is a separate product for lab and not used for high speed production." The line grades and keeps numbers only; the PDF grade report is made on request, off the line (about 37 ms each, measured) | TOOL-SPECIFICATIONS.md 6, 6.5 |
| Grade one part in N | Opt-in (default: every part). A sampled part below the lowest grade, or not gradable, is NG; the parts between samples are not judged on their grade, while their text, count and GS1 data still are. Print quality drifts slowly, so the trend (plant server) catches it before parts fail | TOOL-SPECIFICATIONS.md 6 |
| Grades to the plant server | Sent in the inspection result's existing values map ("<tool>.grade", "<tool>.grade.<parameter>", symbol contrast %, modulation): no change to the plant event schema | PLANT-DASHBOARD-AND-MOBILE.md 3 |

## Pending
- Plant server: store the grade values per line and tool, show the print-quality trend on the dashboard and the mobile app, and raise a drift alarm before the lowest grade is reached.
- LineRunner (aggregation lines): grade sampling and grades in its plant results.
