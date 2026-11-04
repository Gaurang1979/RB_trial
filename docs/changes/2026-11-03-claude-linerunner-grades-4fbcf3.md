# LineRunner (aggregation lines): grade sampling and grades in its plant results

- Status: done
- Session: Claude desktop (linerunner-grades), 2026-11-03
- Pull request: #322
- Section 5 item: LineRunner (aggregation lines): grade sampling and grades in its plant results.
- Components/files: dotnet/LineRunner (CaseGrading.cs new, Program.cs: --grade-every, --min-grade, grades in the plant result), dotnet/LineRunner.Tests (new), .github/workflows/windows-build.yml (runs the new tests), docs/REQUIREMENTS-VISION-SYSTEM.md (17.3 PQ-07)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #322: LineRunner grades the child codes of one case in N (--grade-every N) with Vision Studio's CodeTool.IsSample and GradeValues, sends them in the plant result's values map as child.grade, child.grade.<parameter>, child.symbol_contrast_pct, child.modulation, child.grade.ungraded and child.grade_min; with --min-grade a sampled case below it or not gradable is NG; no plant event schema change; 5 tests) LineRunner (aggregation lines): grade sampling and grades in its plant results.

## Timeline (section 3)
| 2026-11-03 | LineRunner grades one case in N on aggregation lines and sends the grades with each graded case's plant result under the tool name "child"; an optional lowest grade rejects a sampled case below it or not gradable | 17.3 PQ-07 |

## Decisions (section 4)
| LineRunner grades as Vision Studio | Same sampling (first case, then every Nth) and the same values keys, under the tool name "child", so the plant server's print-quality trend and drift alarm (PQ-01..05) take LineRunner's grades with no change to the plant event schema. Command-line options, not a product master field, so the product file format is unchanged | 17.3 PQ-07 |
| Parent label not graded | The parent camera decodes on its own thread, so its reader cannot be switched to grade the same sampled case; grading it on every case would cost time on every case | 17.3 PQ-07 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.3: PQ-07 LineRunner grade sampling registered | Claude (Mac, branch claude/linerunner-grades-4fbcf3) |

## Pending
- LineRunner: grade the parent (case) label on the sampled cases too, with the parent reader switched per case in step with the child camera.
