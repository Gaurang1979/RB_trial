# Read code tool: allow a lowest print grade on Micro QR, PDF417, Aztec, Pharmacode and DotCode

- Status: done
- Session: Claude desktop session grade-pharma-dotcode, 2026-10-30
- Pull request: #150
- Section 5 item: new (Pending of docs/changes/2026-10-30-claude-grade-2d-api.md, grade-databar-stacked.md and grade-pharma-dotcode.md)
- Components/files: dotnet/VisionCore/Tools/CodeTool.cs (SetupProblem), dotnet/VisionCore.Tests (CodeGradeTests, CodePharmacodeTests, LineGradingTests), docs/vision-system/TOOL-SPECIFICATIONS.md (5.x wording, 6.1)
- Depends on: #117 (engine grading of Pharmacode and DotCode)

## Section 5
- New item: `[x] (done 2026-10-30, PR #150: the setup refusal is gone; a code that cannot be graded while grading is on still fails the part; part mark grading stays Data Matrix and QR) Read code tool: a lowest print grade and grade recording on Micro QR, PDF417, Aztec, Pharmacode and DotCode`
- "Code tool: allow a lowest print grade for Micro QR, PDF417 and Aztec": [x] (done 2026-10-30, PR #150)
- "Read code tool: allow a lowest print grade on Micro QR, PDF417 and Aztec": [x] (done 2026-10-30, PR #150)

## Timeline (section 3)
| 2026-10-30 | The Read code tool takes a lowest print grade and grade recording on every type the engine reads (Micro QR, PDF417, Aztec, Pharmacode, DotCode no longer refused at setup) | 6.2, 12 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md: every type read is graded; 6.1 built line for PR #150 | Claude (Mac, branch claude/grade-tool-types) |

## Pending
- Runtime.Host.Tests `CardSignInTests.A_broken_or_cut_short_hash_matches_nothing` failed once on 2026-10-31 (good card not found again after the broken hashes were put back; the test sets random file times, so the store may not see the last write). This PR does not touch it: make the test or the store's reload sturdy.
