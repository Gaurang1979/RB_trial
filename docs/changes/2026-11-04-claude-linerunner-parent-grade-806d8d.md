# LineRunner: grade the parent (case) label on the sampled cases too, with the parent reader switched per case in step with the child camera

- Status: done
- Session: Claude desktop (linerunner-parent-grade), 2026-11-04
- Pull request: #337
- Section 5 item: LineRunner: grade the parent (case) label on the sampled cases too
- Components/files: dotnet/LineRunner (CaseGrading.cs: parent tool, CameraSampling, JudgeCase; Program.cs: parent reader switched per case, parent grades in the plant result), dotnet/LineRunner.Tests (CaseGradingTests.cs), docs/REQUIREMENTS-VISION-SYSTEM.md (17.3 PQ-07 wording, PQ-08 new)
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #337: each camera counts its own frames, decoded and missed, with the same one-case-in-N sampling and switches its own reader on its own thread before the next decode, so the k-th child and parent frames (one case) are graded together; each parent frame carries whether it was graded; the parent grades go in the same values map under the tool name "parent" (parent.grade, parent.grade.<parameter>, ...); with --min-grade a read parent label below it or not gradable is NG (parent_code grade_low / not_graded); no plant event schema change; 6 new tests) LineRunner: grade the parent (case) label on the sampled cases too, with the parent reader switched per case in step with the child camera.

## Timeline (section 3)
| 2026-11-04 | LineRunner grades the parent (case) label of the sampled cases too, in step with the child codes, and sends its grades with the case's plant result under the tool name "parent" | 17.3 PQ-08 |

## Decisions (section 4)
| Parent label graded per camera count | Supersedes "Parent label not graded" (PR #322). Each camera's frame events run on the thread that decodes its next frame, so each reader can be switched there; counting each camera's own frames (missed ones included, now for the child too) keeps the sampled case the same on both, as frames are paired by arrival. A parent frame is judged only when it carries that it was decoded with grading on, so a pairing slip never fails a case as not graded | 17.3 PQ-08 |

## Update log (section 8)
| 2026-11-04 | Requirements 17.3: PQ-08 LineRunner parent label grading registered; PQ-07's "parent label is not graded" replaced by a pointer to PQ-08 | Claude (Mac, branch claude/linerunner-parent-grade-806d8d) |
