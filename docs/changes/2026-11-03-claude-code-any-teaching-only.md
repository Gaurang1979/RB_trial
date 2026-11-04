# Read code tool: refuse "any type" outside teaching

- Status: done
- Session: Claude code-any-teaching-only, 2026-11-03
- Pull request: #277
- Section 5 item: Read code tool: refuse "any type" outside teaching (Pending in docs/changes/2026-10-31-claude-wave0-engine-fixes.md)
- Components/files: dotnet/VisionCore/Tools/CodeTool.cs, AutoFind.cs, dotnet/Runtime.Host/ToolTeach.cs, dotnet/VisionStudio Teach.razor (the teaching probe), VisionCore.Tests, docs/REQUIREMENTS-VISION-SYSTEM.md (AS-12a)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #277: CodeTool.SetupProblem refuses "any type", 0xFFFF and 0x3FFFF included, unless teaching; Job.Validate therefore refuses to save it or load it for running; the probes of Auto find, Teach and the job designer's Teach pass teaching: true) Read code tool: refuse "any type" outside teaching, so a line job never counts a stray code of a type nobody chose (a saved "Any" job now also reads rMQR)

## Timeline (section 3)
<!-- | YYYY-MM-DD | what was built and verified | requirement sections | -->
| 2026-11-03 | Read code tool on "any type" refused outside teaching (setup problem, job save and run load); teaching probes keep it; CodeAnyTypeTests | 4, 12, AS-12a |

## Decisions (section 4)
<!-- | Topic | Decision and reason | requirement sections | -->
| "Any type" only while teaching | Any is refused when its 16 older bits are all set (0xFFFF), so an older job's "any" (0xFFFF, 0x3FFFF) is refused like 0x7FFFF; a chosen mix of types (QR and Data Matrix, all 1D) still runs. Only an explicit teaching flag on the probe lets it through; the editor still opens such a job with the problem shown, to teach it again | 12, AS-12a |

## Update log (section 8)
<!-- | YYYY-MM-DD | what this pull request changed in the docs | Claude (<machine>, branch <branch>) | -->
| 2026-11-03 | REQUIREMENTS-VISION-SYSTEM.md: AS-12a "Any type" is for teaching only | Claude (Mac, branch claude/code-any-teaching-only) |
