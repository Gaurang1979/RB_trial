# Job load reasons: Station.LoadJob answers JobNotFound with the reason; JobDesigner.SaveJob reads the draft with the strict job reader

- Status: done
- Session: job-load-reasons, 2026-11-03
- Pull request: #271
- Section 5 item: new (Pending in docs/changes/2026-10-31-claude-job-load-hardening.md)
- Components/files: dotnet/VisionStudio (Services/Station.cs), dotnet/Runtime.Host (JobDesigner.cs), their test projects, docs/REQUIREMENTS-VISION-SYSTEM.md (section 8, Job lifecycle)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #271: Station.LoadJob answers JobNotFound with the job and reason in LastError, CameraNotFound only for cameras that do not open; JobDesigner reads drafts for save and try with JobFile.Parse; Runtime.Host.Tests JobLoadReasonTests and a JobDesignerTests case) Job load reasons: a PLC job change to a job that cannot be loaded answers JobNotFound with the reason, and Studio's job drafts are read with the strict job reader.

## Timeline (section 3)
| 2026-11-03 | A PLC job change by number to a job that cannot be loaded answers JobNotFound (200) with the reason (no job has the number, the job named and why it cannot run, or which files cannot be read); CameraNotFound (300) only when a loaded job's cameras do not open; the line keeps its job. Drafts for SaveJob, TryJob and the other designer calls are read with JobFile.Parse (unknown fields, integer named values, duplicate fields, newer schema refused). Tested in Runtime.Host.Tests. | 8, 12, 18.5 |

## Decisions (section 4)
| Drafts read strictly for a try too | Studio sends the same draft to try and to save, so both read it with the job file reader: what is tried is what would be saved, and a setting Studio wrote that this runtime does not know is refused rather than dropped. | 8, JOB-FILE-FORMAT 2.2.1 |

## Update log (section 8)
| 2026-11-03 | Requirements section 8 (Job lifecycle): job load reasons and strict drafts | Claude (Mac, branch claude/job-load-reasons) |
