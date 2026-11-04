# Changeover verification: the job file digest in the record (CV-11)

- Status: done
- Session: changeover-digest, 2026-11-03
- Pull request: #292
- Section 5 item: Changeover verification: the job file's digest in the record
- Components/files: dotnet VisionCore (Job.Digest), PlantEvents (ChangeoverAsk), VisionStudio ChangeoverGate, plant_server vision changeover.py + events.py, dashboard changeover.html, mobile changeover screen, CHANGEOVER-VERIFY.md
- Depends on: none. Nearby: #271 (job loading, Job.cs/JobFile.cs) and #279 (plant_server auth): any merge order, small text conflicts only

## What and why
Pending item of PR #213: a job file copied by hand with the same name and version counted as the same changeover. The record now carries `job_digest` (SHA-256 of the job file bytes as read: `Job.Digest`, set in `JobFile.Parse`), and the code covers it. Additive: with no digest the code and the stored record are unchanged. Line side: `ChangeoverAsk.JobDigest/CodeOf/Of/CheckFields`, `ChangeoverGate` passes `job.Digest`. Plant server: `changeover.canonical/code_of/_record/changes`, `events.changeover_problems`, web page row. The mobile screen needs no change: an unmatched change is listed under its server label.

## Verified
- dotnet: PlantEvents.Tests 45 passed; VisionStudio.Tests (Changeover) 16 passed; VisionCore.Tests (JobLoad) 105 passed; StationAgent.Tests builds.
- plant_server: tests/test_vision_changeover.py 59 passed.
- Not run: Flutter (no Dart change), whole solutions (no shared core changed), bench (engine untouched). CI: not run (billing).

## Section 5
- [x] (done 2026-11-03, PR #292: the job file's SHA-256 is in the changeover record and its code; a copy with other bytes asks again) Changeover verification: the job file's digest in the record
<!-- When done (forms in the README's roll-up rules): "- [x] (done YYYY-MM-DD, PR #n: ...) <the item's text>" replaces the item named above; "- [ ] ..." adds a new item; any other line is added under the item as an update. -->

## Timeline (section 3)
| 2026-11-03 | Changeover record carries the job file digest, covered by the code; server reads old records unchanged | 12 CV-11 |

## Decisions (section 4)
| Digest of what | SHA-256 of the file bytes as read, not a re-serialised canonical JSON: any byte difference asks again (safe side), no second serialiser to keep equal in C# and Python | 12 CV-11 |

## Update log (section 8)
| 2026-11-03 | Requirements CV-11; CHANGEOVER-VERIFY.md section 2 job_digest | Claude (Mac, branch claude/sad-snyder-9525f5) |

## Pending
- Changeover verification: the job file's digest checked again at Start against the file on disk (a file swapped while the job is loaded is not seen until it is reloaded).
