# Auto-tune: Read code tool's lowest print grade (MinGrade) and the tools' time budgets; NG review learning from accepted code pictures

- Status: done
- Session: claude/autotune-code-grade-0d48b8, 2026-11-04
- Pull request: #331
- Section 5 item: Auto-tune: Read code tool's lowest print grade (MinGrade) and the tools' time budgets
- Components/files: dotnet/VisionCore/Jobs/AutoTune.cs, dotnet/VisionCore.Tests/AutoTuneTests.cs, docs/REQUIREMENTS-VISION-SYSTEM.md (AS-03a..c). Runtime.Host/Reviews.cs is not changed: Learn from reviews calls AutoTune and lists every changed setting, so it takes the new ones as they are
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #331: AutoTune proposes Read code's lowest print grade and the time budgets of Read code and Read/Check text; Learn from reviews uses them on accepted and confirmed code pictures) Auto-tune: Read code tool's lowest print grade (MinGrade) and the tools' time budgets (requirements AS-03a, AS-03b, AS-03c)

## Timeline (section 3)
| 2026-11-04 | Auto-tune tunes Read code's lowest print grade (A..D) where every part is graded, and proposes the lowest time budget of Read code and Read/Check text that keeps every good image passing; NG review learning and the near-limit good pictures now include code pictures. VisionCore AutoTune tests 26 of 26 | AS-03a..c, AS-04, AS-12 |

## Decisions (section 4)
| Lowest print grade tuned only where every part is graded | A tool with no lowest grade, or one grading one part in N, is left as it is: between samples a bad part passes whatever its grade, so the run could not prove a grade. The relaxed run grades and records without failing on the grade; a code that cannot be graded counts as failing at every grade (a grade in doubt is NG). The grade goes half-way up between the highest bad and the lowest good grade, as the other thresholds. | AS-03a, AS-12 |
| Time budget from the time taken, never above now | The slowest time the tool took times 1.5 plus 10 ms, in 5 ms steps, proposed only when a whole step below now. A shorter budget reads less, and reading less could hide an extra code or extra text (the engine reports, but does not judge, code-sized regions left unread), so the budget also covers every bad image, except one the tool fails for finding too little (no code, too few codes, no text, a missing line). A bad image the tool passes is covered too: the relaxed run does not judge the grade, and a shorter budget could leave a badly graded code unread. The run with the proposal proves it. Not tuned: Count / spot (fewer blobs counted could pass a most-blobs limit). | AS-03b, AS-12, section 12 |
| No change to Learn from reviews | It runs AutoTune and lists every changed setting; a shorter budget is not "less strict", so it needs no extra proof, and a lower grade needs a confirmed bad picture as any looser setting. | AS-03c, AS-04 |

## Update log (section 8)
| 2026-11-04 | Requirements AS-03a..c: auto-tune of Read code's lowest print grade and of time budgets, and learning from code pictures | Claude (Mac, branch claude/autotune-code-grade-0d48b8) |

## Pending
- Auto-tune: compare the proposed time budgets and the slowest part with the line's time target per product (no time target in the job yet)
- Auto-tune: time budget of Count / spot once the blob tool reports that its budget ran out (fewer blobs could pass a most-blobs limit)
