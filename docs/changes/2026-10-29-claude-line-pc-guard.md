# Line PC: resource guard (memory, leaks, step-by-step clean-up) and NG image saving with counts

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #51
- Section 5 item: new
- Components/files: dotnet/StationGuard (new), dotnet/StationGuard.Tests (new), dotnet/VisionCore/Cameras.cs (ICamera.ReuseBuffers), dotnet/VisionStudio (Station, StationHistory, LiveCamera, Render, Options, Program, Test/Teach pages), dotnet/Dashboard (Station, Live page), dotnet/LineRunner, .github/workflows/windows-build.yml, README.md
- Depends on: none (#47 merged)

## Section 5
- [x] (done 2026-10-29, PR #51: ResourceGuard and NgImageStore in dotnet/StationGuard, wired into Vision Studio; leak and stability fixes in Vision Studio, the Dashboard and LineRunner; 35 tests) Line PC resource guard (memory, leaks, step-by-step clean-up) and NG image saving with counts.

## Timeline (section 3)
| 2026-10-29 | Line PC resource guard: memory, PC memory and disk watched every 5 s, leak check, one clean-up step at a time for the resource that is short, only between parts; NG images kept with counts per part and per reason, under file, byte and pressure limits; leak and stability fixes (20 MB allocation per frame, Dashboard stop deadlock and Stop/Start race, image cache growth, unlimited hard-case folder); 35 tests | Requirements 13 (reliability) |

## Decisions (section 4)
| Line PC performance first | Owner, 2026-10-29: "there must be option to save bad images with counts (monitor working memory and give priority to system performance). Check for memory leakage and other issues which can affect the system performance and auto clear the same if any step by step during running as such it doesn't affect system performance and stability." The guard runs below normal priority; a clean-up step runs only when no part is being inspected and the gap fits it; a step that may pause threads (heap compaction) runs only when the line has paused; each step relieves memory or disk and runs only when that is short; the guard never restarts anything, it recommends a restart between batches when every step has been tried | Requirements 13 |
| NG images with counts | Every part is counted once (all cameras together; a camera without an image or with an error counts as NG with that reason); images are kept by mode (NG only by default, all, every Nth NG, none), at most 64 MB waiting, 5000 files and 5 GB on disk, oldest first; under High pressure at most 20 per reason per hour, under Critical none; counts per batch survive restarts | Requirements 12 (results and data) |
| Review before ready | Opus review found thirteen problems, among them a missing parent code that could pass as OK during Stop, a Start/Stop race freeing the reader mid-decode, memory pressure deleting saved images, a heap compaction that could pause inspection, a bad frame stopping image saving, per-camera counts; all fixed with tests | Requirements 13 |

## Update log (section 8)
| 2026-10-29 | README layout row for dotnet/StationGuard; Windows build runs its tests | Claude (cloud, branch claude/line-pc-guard) |

## Pending
- Dashboard (aggregation station): use the resource guard and NgImageStore; it still saves hard cases on the inspection thread.
- Act on the guard's restart recommendation: restart the station between batches (with the operator's confirmation) and raise a station alarm through the station agent.
- Free-run or hardware-triggered cameras: check that three reused frame buffers are enough when frames arrive faster than inspection.
- Soak test on a line PC: 24 hours with real cameras, memory flat, no step during a part (the testing session).
