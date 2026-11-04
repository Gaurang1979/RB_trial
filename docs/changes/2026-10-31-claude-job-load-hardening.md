# Job file load hardening: corrupt, newer, old and over-size job files and missing references are refused or migrated, never half-loaded

- Status: done
- Session: job-load-hardening, 2026-10-31
- Pull request: #177
- Section 5 item: new
- Components/files: dotnet/VisionCore (JobFile, JobStore, Job.Validate), dotnet/VisionCore.Tests, dotnet/Runtime.Host (JobDesigner, LineRuntime job by number), dotnet/Runtime.Host.Tests, dotnet/VisionStudio (Station, Teach, Jobs pages), docs/vision-system/JOB-FILE-FORMAT.md
- Depends on: none (#147 merged first; its JobDesigner now opens jobs with LoadForEdit)

## Section 5
- [x] (done 2026-10-31, PR #177: strict reader with the reason in words; schema 0.x with notes for old files, newer refused; missing, damaged or out-of-folder patterns, fonts and AI models refused at load; 4 MB, 100-tool and 32-line caps; samples, every tool setting, old-version fixtures and 1,200 mutated copies tested) Job file load hardening: a corrupt, truncated, wrong-typed, newer, over-size job file or one with missing references is refused at load with a clear reason, or an old one migrated with a note; the line stays NG and not ready.

## Timeline (section 3)
| 2026-10-31 | Job files load strictly (JobFile.Parse): size, complete JSON, schema version, known fields and types, no null entries, finite numbers, caps; the job's references (patterns, fonts, AI model) and settings (Validate) are checked at load. The line runs only a whole job (JobStore.Load); Studio opens a job that cannot run yet with its problems listed (LoadForEdit); a broken file never hides the others (TryLoad, FindByNumber). Tested in VisionCore.Tests and Runtime.Host.Tests. | 12, JOB-FILE-FORMAT 2.2, 4 |

## Decisions (section 4)
| Job file schema version | The JSON job of today is schema 0.x ("0.1" written on save; a file without a version reads as 0.0 with a note). The full .vsjob format will be 1.0 and migrate these. On the line a job from a newer version, even a newer minor, is refused, not opened with a banner: its added settings would be left out without anyone knowing. | JOB-FILE-FORMAT 2.2.1, 12 |
| Load for running vs for editing | JobStore.Load refuses anything that cannot run (missing pattern, font, model, a setting out of range). LoadForEdit refuses only a file that cannot be read whole and lists the rest in Job.LoadProblems, so the engineer can open the job and fix it. | 12 |
| Strict field names and named values | Unknown fields, integers for named values and a field written twice are refused: each would leave a setting at its default unseen. | JOB-FILE-FORMAT 2.2.1 |

## Update log (section 8)
| 2026-10-31 | JOB-FILE-FORMAT.md 2.2.1: the JSON job of today (schema 0.x), what is refused and what is migrated with a note | Claude (Mac, branch claude/job-load-hardening) |

## Pending
- Station.LoadJob (VisionStudio) answers CameraNotFound when the job of that number cannot be loaded; it should answer JobNotFound with the reason.
- JobDesigner.SaveJob reads the draft from Studio leniently (unknown fields and integer named values pass); read it with the strict job reader.
- Runtime.Host.Tests: LineTests.Replay_run_streams_NG_results_with_full_resolution_images_and_counts_them and CardSignInTests.A_broken_or_cut_short_hash_matches_nothing fail now and then when the whole project runs (they pass alone); make them independent of timing.
