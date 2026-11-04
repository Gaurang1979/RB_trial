# Runtime frame path: frame wait from the job, pooled simulator and per-part frame buffers

- Status: done
- Session: Claude (Mac, runtime-frame-wait-pool), 2026-11-03
- Pull request: #293
- Section 5 item: Runtime: the frame wait is 2 s
- Components/files: dotnet/Runtime.Host/Cameras.cs (IntakeCamera, SimulatorDriver.SimSource), dotnet/VisionCore/Cameras.cs (ICamera.FrameWaitMs, default 2 s), dotnet/VisionStudio/Services/Station.cs (grabs wait FrameWaitMs), dotnet/Runtime.Host.Tests/LineTests.cs, docs/REQUIREMENTS-VISION-SYSTEM.md (section 12: RT-FW-01, RT-FP-01)
- Depends on: none

Requirements section 12, RT-FW-01 and RT-FP-01 (both registered in this pull request, from the Pending lists of #148 and #178).

**Frame wait (RT-FW-01).** The station waited a fixed 2 s for every frame. It now waits `IntakeCamera.FrameWaitMs`: the job's exposure, the last frame's transfer over GigE (100 MB/s, the slower link) and a 100 ms margin, never less than the slowest of the camera's last 16 trigger-to-frame times plus the margin, at most 2 s; 2 s before the camera's first frame. A 1920 × 1200 frame at 2 ms exposure gets 126 ms. A part whose frame does not come in that time is NG ("No picture from the camera"), as before.

A shorter wait makes a late frame more likely, so a late frame must never be inspected as the next part:
- after a timeout the camera's next part first waits out twice the frame wait from the missed trigger, throwing away anything that arrives;
- a frame later still is known by the camera's frame number (the one after the last frame seen) and thrown away whenever it comes. A lost frame of that number (the intake's `frame_lost`) or a number that starts afresh ends the search.
- A camera that skips a trigger altogether sends its next frame under the late frame's number. That frame is thrown away, so that part is NG too; the part after it is in step again. It costs two parts, never a chain.
- The first full-suite run showed why the number check is needed: on a Mac at load 35 a frame 300 ms late arrived after a 400 ms window. The window alone was not enough.

**Pooled frames (RT-FP-01).** The 30-minute soak allocated about 4.7 MB per part. Measured here: the simulated camera re-read and re-decoded its BMP file for every frame, a 2.3 MB file buffer plus a 2.3 MB pixel array, 4.6 MB per frame with 98 gen2 collections in 200 frames of 1920 × 1200. `SimSource` now decodes each picture once and plays it from memory, up to 256 MB a simulated camera (pictures beyond that are still decoded as they play). The same 200 frames allocate 0.2 MB in all and cause no gen2 collection. The station's own copies already avoid new arrays per part: frames stay in the intake's reused buffers (`ReuseBuffers`), held-part copies are rented from the shared pool and given back at the PLC's verdict (PR #200, unchanged). NG images kept for the disk stay plain arrays on purpose (NgImageStore: freed memory really goes back).

## Section 5
- [x] (done 2026-11-03, PR #293: the frame wait follows the job, exposure + GigE transfer + 100 ms, at least the camera's slowest recent frame + 100 ms, 2 s at most and before the first frame; a missed frame is still NG; a late frame is thrown away by a late window and by its frame number, never given to the next part; requirements 12 RT-FW-01) Runtime: the frame wait is 2 s; at 225/min a camera that sends no frame costs that part plus the next 7 (all NG). Make the wait follow the job (exposure + transfer + margin, about 150 ms here).
- "Simulator and station: each frame" `[x] (done 2026-11-03, PR #293: the simulated camera decodes each picture once, up to 256 MB, 4.6 MB → 0.001 MB allocated per 1920 × 1200 frame and no gen2 collection in 200 frames; the station already keeps frames in the intake's buffers and held copies in pooled ones; requirements 12 RT-FP-01)`

## Timeline (section 3)
| 2026-11-03 | Runtime frame path: the frame wait follows the job (126 ms for a 1920 × 1200 frame at 2 ms exposure instead of 2 s), a late frame is thrown away by a late window and by its frame number; the simulated camera decodes each picture once (4.6 MB → 0.001 MB allocated per frame, no gen2 collection) | Requirements 12 (RT-FW-01, RT-FP-01) |

## Decisions (section 4)
| Frame wait | Exposure + transfer over GigE at 100 MB/s + 100 ms margin, at least the slowest of the last 16 frames + 100 ms, at most 2 s, 2 s before the first frame. The link type is not in the job, so the slower link is assumed; the recent-frames floor covers a slow PC or a simulator | Requirements 12 RT-FW-01 |
| A late frame never reaches the next part | After a timeout: a blocking late window of twice the wait, then the camera's frame number. A camera that skips a trigger costs two parts, not one: its next frame cannot be told apart from the late one, so it is thrown away (line safety before throughput) | Requirements 12 RT-FW-01; section 12 line safety |
| Simulator cache | Decoded pictures kept up to 256 MB per simulated camera; beyond that decoded on each play, so a folder of 20 MP pictures does not hold gigabytes | Requirements 12 RT-FP-01 |

## Update log (section 8)
| 2026-11-03 | Requirements section 12: RT-FW-01 (frame wait from the job) and RT-FP-01 (no new frame-size arrays per part) registered | Claude (Mac, branch claude/runtime-frame-wait-pool) |

## Pending
- Soak: run tools/soak again (two simulated cameras, 225/min) and check the gen2 rate is near zero and that no part is missed with the shorter frame wait; then on the Windows line PC with real cameras, check FrameWaitMs against the measured trigger-to-frame times (USB3 is faster than the GigE rate assumed).
