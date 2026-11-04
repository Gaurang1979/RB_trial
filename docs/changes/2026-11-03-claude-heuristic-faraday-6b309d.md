# Changeover verification: the job file's digest checked again at Start (CV-12)

- Status: done
- Session: job-digest-start, 2026-11-03
- Pull request: #302
- Section 5 item: new
- Components/files: dotnet VisionCore (JobStore.ChangedOnDisk), VisionStudio Station.Start, requirements CV-12. Nearby: results-ledger verify at runtime start (another session; its hook is separate, any merge order, small text conflict at most in Station.Start)
- Depends on: none

## What and why
Pending item of PR #292: the digest (CV-11) was taken when the job was loaded, so a file swapped while the job was loaded was not seen until reload. `JobStore.ChangedOnDisk(job)` hashes the file on disk again; `Station.Start` (camera and PLC runs, not stored pictures) throws `ChangeoverException` with a plain reason and "Open the job again so the supervisor checks it" when the bytes differ, the file is gone or cannot be read. A job without a digest is not checked.

## Verified
- dotnet: VisionCore.Tests (JobLoadTests) 107 passed (2 new); VisionStudio builds (Station.Start change).
- Not run: whole solutions (no shared core contract changed), engine/bench (untouched), Flutter, plant_server. CI: not run (billing).

## Section 5
- [x] (done 2026-11-03, PR #302: Start hashes the job file again; a swapped, missing or unreadable file refuses Start and asks to open the job again) Changeover verification: the job file's digest checked again at Start

## Timeline (section 3)
| 2026-11-03 | Start re-checks the job file digest against the disk | 12 CV-12 |

## Decisions (section 4)
| Unreadable file at Start | Counts as changed (refuse): in doubt the line does not start | 12 CV-12 |

## Update log (section 8)
| 2026-11-03 | Requirements CV-12 | Claude (Mac, branch claude/heuristic-faraday-6b309d) |
