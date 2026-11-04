# Grading: GS1 DataBar Stacked, Stacked Omnidirectional and Expanded Stacked, and a lowest print grade on DataBar in the Read code tool

- Status: done
- Session: Claude session grade-databar-stacked, 2026-10-30
- Pull request: #95
- Section 5 item: new
- Components/files: engine (grade1d.cpp, decodability.cpp, engine_tests.cpp), dotnet/VisionCore (CodeTool.cs, CodeGradeTests.cs), docs/vision-system/TOOL-SPECIFICATIONS.md
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #95: ISO/IEC 24724 row-by-row grading of GS1 DataBar Stacked, Stacked Omnidirectional and Expanded Stacked, the symbol taking the lowest row's grade; one orientation per row for every DataBar; a lowest print grade allowed on DataBar in the Read code tool) Grading: GS1 DataBar Stacked, Stacked Omnidirectional and Expanded Stacked, and a lowest print grade on DataBar in the Read code tool

## Timeline (section 3)
| 2026-10-30 | Stacked GS1 DataBar graded row by row (rows between their separators, ten scans a row, each decoded with the other rows, symbol = lowest row); DataBar orientation chosen per row; Read code tool accepts a lowest grade on DataBar; engine and VisionCore tests pass | 6.2, 12 |

## Decisions (section 4)
| Stacked DataBar grade | Each row graded as a linear symbol, ten scans inside the row and clear of its separator patterns; the symbol's grade is the lowest row's (ISO/IEC 24724). The separator patterns are not graded themselves: they bound the rows | 6.2 |
| Stacked row decode | No row decodes alone: a scan counts as decoded when it reads with the other rows' best scans (highest decodability) as one symbol. A row that never reads fails every scan of the symbol: F. Fewer than two rows found: not graded, so a lowest grade set on it fails the part | 6.2, 12 |
| DataBar orientation | One orientation per row, the one its scans read better in, never per scan: a defect in one scan cannot turn that scan round to a better decodability | 6.2 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md 6.2: stacked DataBar grading, DataBar orientation, lowest grade on DataBar | Claude (local Mac, branch claude/grade-databar-stacked) |

## Pending
- 1D grade report (GradeReport / LinearAnalysis): stacked DataBar lists ten scans per row (20 or more); show them row by row.
- Read code tool: allow a lowest print grade on Micro QR, PDF417 and Aztec now that they are graded (PR #74; CodeTool.cs still refuses them).
- 1D grading: findLinearBands finds no band for a stacked DataBar at 10 px per module in the engine test image; check large-X 1D grading on real images.
