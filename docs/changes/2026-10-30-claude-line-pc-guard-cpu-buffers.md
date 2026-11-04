# Line PC guard: whole-PC CPU, exposed guard thresholds, frame-buffer overrun as a missed trigger

- Status: done
- Session: line-pc-guard-cpu-buffers, 2026-10-30
- Pull request: #97
- Section 5 item: new (three Pending items of the 2026-10-29 notes line-pc-guard, pi1-agent-wiring and line-pc-live-charts)
- Components/files: dotnet/StationGuard (Sampling.cs, ResourceGuard.cs, Frames.cs new), dotnet/StationGuard.Tests (CpuAndOptionsTests.cs, FrameTests.cs new), dotnet/HikCamera (HikCamera.cs, .csproj references StationGuard), dotnet/LineRunner/Program.cs
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #97: SystemCpu in the guard sample; LineRunner sends cpu_pct) Whole-PC CPU in the resource guard's sample, so health.sample can carry cpu_pct.
- [x] (done 2026-10-30, PR #97: ResourceGuard.Options; the gauges themselves move to the new app) PC health gauges should read the running guard's thresholds (expose GuardOptions) instead of the defaults.
- [x] (done 2026-10-30, PR #97: FrameIntake, missed part = NG; HikCodeCamera.FramesMissed) Free-run or hardware-triggered cameras: check that three reused frame buffers are enough when frames arrive faster than inspection.

## Timeline (section 3)
| 2026-10-30 | Line PC guard: whole-PC CPU in every sample (Windows GetSystemTimes, macOS CPU ticks and memory for development, Linux /proc/stat) and sent as cpu_pct by LineRunner; ResourceGuard.Options exposes the running limits; FrameIntake hands three reused buffers to free-run or hardware-triggered cameras, never overwrites one being inspected, and passes a dropped or lost frame on as a missed part (NG) in its place; HikCodeCamera reports frame-number gaps (FramesMissed), LineRunner makes each one NG and keeps child and parent paired; 11 tests | Requirements 12, 13 |

## Decisions (section 4)
| Frames faster than inspection | Three buffers are enough only while each frame is inspected before the next is taken (software trigger, as now). With free-run or hardware trigger no number of buffers is enough when inspection is slower on average, so a frame that finds no free buffer is dropped and its part is NG, in its place in the part order; frames the camera numbered but never delivered count the same way. A buffer being inspected is never written to | Requirements 12 (line safety), 13 |
| Missed child or parent frame in LineRunner | Each missed child frame is an NG case and sets one parent frame aside; each missed parent frame leaves a gap in the parent queue, so that case is NO READ. Without this, a lost frame would pair a case with the previous case's parent code | Requirements 12 |

## Update log (section 8)

## Pending
- The new runtime's camera: take frames through FrameIntake instead of the round-robin ring in VisionStudio's LiveCamera (that ring can overwrite a buffer under free-run or hardware trigger), and send cpu_pct and the guard's limits (ResourceGuard.Options) from its health.sample and gauges.
