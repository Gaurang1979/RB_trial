# Check text with no lines passes any part

- Status: done
- Session: claude check-text-no-lines, 2026-11-03
- Pull request: #274
- Section 5 item: VisionCore: a Check text tool with no lines
- Components/files: dotnet/VisionCore/Tools/TextTool.cs, dotnet/VisionCore.Tests (TextDecidesTests, RenderedFontTests, PrintQualityTests), docs/REQUIREMENTS-VISION-SYSTEM.md 17.2 OCV-01
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #274: `TextTool.SetupProblem` refuses a Check text tool with no line, so the tool fails every part on the line, Req 17.2 OCV-01) VisionCore: a Check text tool with no lines must be refused like a Read text tool without a rule (`TextTool.SetupProblem`); today it passes any part whose print reads cleanly (found in the PR #195 review; tap-to-add makes such a tool too)

## Timeline (section 3)
| 2026-11-03 | Line safety: a Check text tool with no expected line is a setup problem (`TextTool.NoLines`). `Job.Validate` already refused it at save and load; now the tool itself fails every part NG ("explain.format.setup") and the Teach page shows the problem, so a tool built outside a validated job (tap-to-add, a try) never passes clean print. Tests: setup problem, job refused, on-the-line NG with a rendered font | 12, 17.2 OCV-01 |

## Decisions (section 4)
| Check text with no line | A setup problem like Read text without a format: never OK, every part NG on the line. `Job.Validate` keeps its own "add the lines to check" message (it runs first), so saved-job wording is unchanged | 12, 17.2 OCV-01 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.2: OCV-01 (a Check text tool needs a line) | Claude (Mac, branch claude/check-text-no-lines) |
