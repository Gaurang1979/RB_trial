# AS-03 auto-tune of a job on saved good (OK) and bad (NG) images: widest margin, never lets a known bad image pass, applied only when the user accepts

- Status: done
- Session: claude/auto-tune-ok-ng (priceless-brattain), 2026-10-30
- Pull request: #154
- Section 5 item: new
- Components/files: dotnet/VisionCore (new Jobs/AutoTune.cs; ToolResult.MatchScore; LocateTool fills it), dotnet/VisionStudio (new Tune page, nav link, English strings), dotnet/VisionCore.Tests/AutoTuneTests.cs
- Depends on: none (requirement text AS-03 is in PR #145)

## Section 5
- New item: `[x] (done 2026-10-30, PR #154: VisionCore AutoTune and the Vision Studio Auto-tune page; tunes Find the part's lowest match and Check text's strictness) Auto-tune of a job on saved good and bad images (AS-03): the job runs on a good-parts folder and a bad-parts folder, the settings with the widest margin are proposed, checked by running the job again, and saved as a new job version only when the user accepts`

## Timeline (section 3)
| 2026-10-30 | Auto-tune on good and bad images (AS-03, first part): Vision Studio's new Auto-tune page runs the job three times on a good-parts and a bad-parts folder (as it is, with the tuned checks relaxed to measure scores, with the proposal) and proposes Find the part's lowest match and Check text's strictness half-way between the highest bad score and the lowest good score, never less strict than now without need. Offered only when the real run with it fails every bad image and no good image that passed before fails; saved as a new job version on acceptance, never while the line runs. Tested with the real OCR engine on rendered print with a wrong and a smudged character. | 16 (AS-03, AS-04, AS-12), 6 |

## Decisions (section 4)
| Auto-tune works out settings from scores, then proves them by a run | One run with the tuned thresholds relaxed gives every tool's score; a tool passes at threshold t when it passed relaxed and its score is at least t, so every setting's verdict is known without more runs. The scores are a model of the engine, so the proposal is run again on every image and offered only when that real run agrees: every bad image fails, no good image that passed before fails. | 16 (AS-03, AS-12) |
| Which settings auto-tune moves first | Only thresholds that decide on a score: Find the part's lowest match and Check text's strictness (Check mode). Check text with learned print limits is left out (the engine scales ink, contrast and pitch by the strictness, so a relaxed run does not show the score at another setting), and so is Check text whose expected text comes from the PLC (the tune has no PLC values). A bad image the other checks already fail (wrong code, wrong text) does not move a threshold. | 16 (AS-03, AS-12) |
| Auto-tune makes checks stricter, rarely less strict | A setting is raised to fail a bad image (half-way to the good ones) but never made less strict than now, unless now fails a good image; then it is lowered half-way to a floor (match 0.40, strictness 20), never below it, and shown as "less strict than now". A less strict proposal is offered only when the run with it passes more good images than now. Also refused: a bad image failed only by a tool error, and a run slower than now (a quarter or 10 ms of noise allowed). Tuning is refused while the line runs (the runs take the PC's time and timing makes reads fail). | 16 (AS-03, AS-04, AS-12), 12 |
| A bad image that looks good is never tuned around | When a bad image scores as well as the good ones on every tuned tool, nothing is proposed and the image is named: either it is not really a bad part or the job needs a tool that sees its fault. | 16 (AS-12), 12 |
| Tuning is guarded like learning | The tune runs on the job as saved on disk (the station keeps the version it opened while a line ran). Applying is refused while the line runs and when the job on disk was saved again since the tune; the result is a new job version, so the previous one stays in the job history. | 16 (AS-04) |

## Pending
- Auto-tune: Read code tool's lowest print grade (MinGrade) and the tools' time budgets (lowest budget that keeps every good part passing within the line time); camera exposure, gain and light on the good and bad images (AS-03)
- Auto-tune page in the Windows application (Avalonia Studio) once its setup screens exist; the 14 other languages for the tune.* strings and for the English sentences AutoTune builds in VisionCore (problem and verdict texts)
- Auto-tune: compare the slowest time with the line's time target (today only with the job as it is), and tune Check text with learned print limits once the engine reports recognition and print deviation apart from the strictness
- Auto-tune: Check text with expected text from the PLC (needs PLC values saved with each tune image)
- Auto-tune: when good and bad images overlap on every tuned tool, offer the best split with the good images it would fail, for the user to decide (today nothing is proposed)
