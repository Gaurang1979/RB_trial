# Line safety of the tools added by Antigravity PRs #245–#259 (Req 17.10 AUD-01..05, AUD-08)

- Status: done
- Session: claude audit-fix-tool-line-safety, 2026-11-03
- Pull request: #268
- Section 5 item: new
- Components/files: dotnet/VisionCore (Tools/ToolArea.cs new; the 14 tools of #245–#259; Jobs/Job.cs Validate, JobRunner.Run, JobStore references, save and clone; InspectEngine.cs, InspectNative.cs), dotnet/VisionCore.Tests (ToolLineSafetyTests.cs new, AutoTuneTests.cs restored, AutotuneToolTests.cs, DeformTests, PhaseTests, DriftTests), dotnet/VisionStudio (Teach.razor teach buttons, i18n/en.json)
- Depends on: #267 (registers Req 17.10). Merges before the recipe/Run PR (claude/audit-recipe-run-labels): both change dotnet/VisionCore/Jobs/Job.cs, in different parts (this one: Validate, References, SavePatterns, Clone, JobRunner.Run; that one: ImportBundle)

## Section 5
- [x] (done 2026-11-03, PR #268: taught references, camera image never changed, only checking tools decide, areas follow the part, AS-03 tests restored, Inspect model load fixed) Line safety of the tools added by Antigravity PRs #245–#259 (requirements 17.10 AUD-01..05, AUD-08)

## Timeline (section 3)
| 2026-11-03 | Line safety of the Antigravity tools: Pouch Alignment, phase correlation and drift use only a reference or baseline taught in Teach and saved with the job (NG "not taught" otherwise, refused by Validate); every new tool works on a copy of its area, mapped through the Locate fixture (outside the picture = NG); inpaint, rectify, auto-tune, photometric virtual light, and drift / metamorphic with their fail option off no longer decide OK; unknown modes and auto-tune engine errors are NG; the stability map has no made-up fallback; the 14 AS-03 auto-tune tests are back; Inspect models load with their size after a restart. VisionCore.Tests and VisionStudio.Tests pass | 12, 17.10 AUD-01..05, AUD-08 |

## Decisions (section 4)
| Correction and advisory tools in a job | One rule, `ToolArea.Decides`, says which tools decide OK/NG; `TextTool.Decides` uses it. A tool that does not decide has its result marked report-only by `JobRunner.Run`, so a part passes only with a result from a tool that checks it; a failing advisory tool still fails the part (in doubt, NG). Rectify and the mesh matcher no longer hand a corrected picture to later tools: no tool reads another tool's output image, so writing back only changed what later tools and the saved NG image saw | 12, 17.10 AUD-02, AUD-03 |
| Where a taught reference lives | As the Find the part pattern: a BMP next to the job, `assets/<job>/<tool>.reference.v<version>.bmp`, named in the job by `ReferenceFile`; the drift baseline is at least 5 good-part measure sets in the job file itself. `TemplatePath` stays in the file format so older jobs load, but is not used | 17.10 AUD-01 |
| Areas of tools compared with a reference | Inspect, Pouch Alignment and phase correlation keep the taught size and centre it on where the taught area's centre is now; the other new tools use the upright box round the turned corners, as Blob does. Any area not wholly in the picture is NG, never clamped | 17.10 AUD-04 |

## Update log (section 8)
| 2026-11-03 | docs/changes note for the tool line-safety fixes (Req 17.10 AUD-01..05, AUD-08) | Claude (Mac, branch claude/audit-tool-line-safety) |

## Pending
- The `.vsrecipe` bundle does not carry taught reference pictures (as for Locate patterns): an imported job with these tools is refused until they are taught again
- No unit test forces an auto-tune engine error (the engine only fails on an area under 32 px, which the tool refuses first); the NG path was checked by reading the code
