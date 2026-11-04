# Two-camera line at 225 parts/min: timing budget, run at rate, 10-minute soak

- Status: done
- Session: line-225-soak, 2026-10-30
- Pull request: #148
- Section 5 item: new
- Components/files: engine/src/decode.cpp (taken verbatim from #147), engine/tests/engine_tests.cpp, engine/CMakeLists.txt, dotnet/VisionStudio/Services/Station.cs (PartCounted), dotnet/Runtime.Host/LineRuntime.cs, dotnet/Runtime.Host/Cameras.cs and RuntimeOptions.cs (verbatim from #147), dotnet/VisionCore/Tools/ToolModel.cs (GrayImage.Load), dotnet/VisionCore/Jobs/Job.cs (FolderSource), dotnet/Runtime.Host.Tests/LineTests.cs
- Depends on: none (#147 carries the same decode.cpp and simulator lines; either order merges cleanly, #147 first suggested)

Owner, 2026-10-30: "test on windows machine with camera in live mode at speed of 225 products per minute with 2 gig-e/USB cameras, PLC, Lighting. camera 1. small qr code, 2. 1D code + batch code 3-4 lines." This pull request is the Mac part: the runtime at 225/min with two simulated cameras and a simulated PLC. The Windows, camera and PLC try is under Pending.

## What was found and fixed

1. **Crash with two cameras inspecting at once (line safety).** The engine changed OpenCV's process-wide thread count during every decode (`cv::setNumThreads`). With OpenCV's TBB backend that rebuilds the thread pool while the other camera's reader is inside an OpenCV parallel loop (the AI locator's or the OCR's forward pass), and vs-runtime died with SIGBUS. The test with two camera channels firing together crashed in about half the runs. The studio-two-camera-product session found the same crash and fixed it in #147 (a held-open OpenCV region instead of changing the thread count). This pull request takes that `decode.cpp` byte for byte, and adds an engine test: two readers with the AI locator decode on two threads. The old code crashed it 3 of 3; with the fix it passes.
2. **Results merged across cameras (counts and verdicts on screen).** `LineRuntime` built each contract result from the station's last-cycle values. With camera channels, two cameras that finish together became one result, whose Pass was the last camera's (an NG camera could be shown OK), and the counters lost a part (21 for 22 in the first try). The station now raises `PartCounted` once per counted part, with each camera's result, and `LineRuntime` builds the result from that. The PLC's verdicts were always per channel and right; this was the operator's and the plant contract's view.
3. **Simulator frames read by content.** `GrayImage.Load` reads BMP, PNG or JPEG by content (29 of the 34 sample `.bmp` files are PNG), and stored-picture folders list `.png` files too. Each simulated camera plays its own test folder (the same lines as #147).
4. **Engine test bug.** A DataBar check built a vector from `begin()` of one temporary and `end()` of another (undefined; it aborted the engine tests about one run in three).

## Timing budget per camera, trigger to result (225/min = one part every 267 ms)

PLC handshake per camera channel, Delta `CamStation` (plc/delta-as): a part that arrives while the channel is not idle and Ready is not inspected (NG); no Done within 1 s is NG.

| Segment | Camera 1: small QR, ROI, 1.6–2.3 MP | Camera 2: EAN-13 + 3 laser lines, 3 MP | Source |
| --- | --- | --- | --- |
| Sensor edge to Trigger (PLC scan) | 1–4 ms | 1–4 ms | PLC-INTERFACE 6 |
| PLC to PC (Modbus poll) | ≤ 5 ms (2 ms poll) | ≤ 5 ms | PLC-INTERFACE 6 |
| Runtime edge to software trigger, trigger to exposure | ≤ 1 ms | ≤ 1 ms | PLC-INTERFACE 6 |
| Exposure (light switched by the PLC) | 0.1–1 ms | 0.1–1 ms | job |
| Transfer GigE (1 Gb/s) / USB3 | 20 / 7 ms (2.3 MB) | 27 / 9 ms (3.1 MB) | frame size |
| Inspection, per tool, Mac M3 (median / p99, in the runtime at rate) | QR 8 / 27 ms | EAN 5 / 11 ms, laser OCR 22 / 48 ms | soak |
| Inspection, line PC estimate (× 1.2 for a 12th–13th gen desktop i5/i7, × 2.5 for an older or fanless i5) | QR 10–20 / 32–67 ms | EAN 6–13 / 13–28 ms, OCR 26–55 / 58–120 ms | estimate, not measured |
| Result write, PLC read, Trigger and Done drop (two scans and a poll) | ≤ 15 ms | ≤ 15 ms | PLC-INTERFACE 4.1, 6 |
| **Total p99 on a line PC** | **about 75–115 ms** | **about 120–200 ms** | **budget 267 ms** |

Engine alone on this Mac (`engine/build/bench --budget 100`, whole frame, no ROI): small QR 1920 × 1200, 5 px modules: median 5 ms, slowest 38 ms (the frames with no code are the slow ones); Data Matrix on the same frames: median 6 ms, slowest 74 ms; EAN-13 on the 59 real Harpic frames 2048 × 1536: median 12 ms, slowest 15 ms, 59/59 read. `ocr_bench --budget 150`, 3 laser lines in the taught area: median 13.5 ms, p95 18.9, max 21.2 ms. In the runtime the tool times are higher (the OCR tool checks the lines, the QR tool grades the code) and come from the soak. Camera 2 is the tight one: on a slow fanless PC its p99 is about 200 ms of the 267, so choose at least a current desktop i5 for this line.

## Soak on this Mac (Apple M3, 8 cores, quiet: load 3.1 at start, heavy-test lock held)

vs-runtime (Release) with two simulated cameras through the frame intake (a 25 ms frame delay standing in for exposure and transfer, plus the file decode), PLC mode, Modbus TCP. A Python PLC ran the camera-channel handshake the way `CamStation` does, with both channels fired in the same scan (two cameras on one sensor, the worst case for CPU), 225/min with ±20 ms jitter, for 600 s. Camera 1 played 40 synthetic frames with one small QR, every 10th with no code (must be NG); camera 2 played 59 real Harpic frames (EAN-13 + 3 laser lines; customer data, kept outside the repository).

| | Camera 1 | Camera 2 |
| --- | --- | --- |
| Parts | 2,251 | 2,251 |
| OK / NG | 2,026 / 225 | 1,641 / 610 |
| Missed (arrived while busy), timed out, later than 267 ms | 0, 0, 0 | 0, 0, 0 |
| Trigger to Done at the PLC: median / p99 / max | 49.7 / 70.1 / 193.7 ms | 78.3 / 109.1 / 193.7 ms |
| Runtime CPU (one core = 100 %) | 60 % mean, both cameras | |
| Runtime memory | 179 MB mean in the first half, 171 MB in the second: flat | |

Contract counters: 4,502 parts = 2,251 + 2,251 (before the fix they lost parts). Every part's verdict was compared with the verdict of a fresh reader on the same frame. Camera 1: 2,026 OK and 225 NG, all as expected (every no-code frame NG). Camera 2: 1,640 OK and 609 NG as expected, 1 NG where the reference was OK, and **1 OK where the reference was NG** (finding below).

**Finding (engine, line safety): the EAN-13 quiet-zone verdict depends on what the reader read before.** Frame 20261023_154745501 fails the quiet-zone check ("Something is printed too close to the barcode ends") on a fresh reader every time (10 of 10), and on 37 of its 38 parts in the soak; once it passed. It is not the rate or the two cameras: one reader that runs the 59 frames in line order, with no runtime and no time pressure, passes it in 2 of 40 passes. The reader's learned step order changes which pass finds the code, and with it the corners where the quiet zone is measured. A check that decides OK or NG must give the same verdict for the same pixels. Under Pending.

## Run at rate: what the runtime does (reviewed, no change needed)

- Camera channels run in parallel: each trigger is its own task, each camera has its own lock and its own tools, and a channel never waits for the other (PLC-INTERFACE 3.5).
- Frame intake: 3 buffers per camera; with the four-phase handshake at most one frame per camera is in flight, so jitter cannot queue frames. A lost frame or one dropped for want of a buffer makes that part NG ("No picture from the camera").
- A late result is never OK for the wrong part: the PLC matches the echo with its own part number, does not trigger a camera that is not Ready (that part is NG, not inspected), and gives NG after 1 s. A trigger that still reaches a busy channel is rejected with error 110, which stops Ready until Reset alarm (PLC-INTERFACE 5.5).
- A camera that sends no frame keeps its channel busy for the 2 s frame wait: that part is NG at the PLC's 1 s timeout and the next 7 parts are NG as not inspected. Safe, but more parts lost than needed (Pending).

## Timeline (section 3)
| 2026-10-30 | Two-camera line at 225/min on the Mac: 10-minute soak, 2,251 parts per camera, no part missed, late or timed out, trigger to Done p99 70 ms (QR) and 109 ms (EAN + laser OCR), memory flat; two cameras at once no longer crash the runtime (engine fix shared with #147, engine test); one contract result per part; simulator reads frames by content; timing budget per tool with a line-PC estimate | Requirements 12; PLC-INTERFACE 3.5, 5.5, 6 |

## Decisions (section 4)
| One fix for the two-camera crash | #147 and this pull request found the same SIGBUS (cv::setNumThreads during decode). This pull request takes #147's decode.cpp verbatim instead of a second fix, and adds the engine test both fixes must pass | PLC-INTERFACE 3.5; requirements 12 |
| Line PC for the two-camera 225/min line | Camera 2 (EAN + 3 laser lines) needs about 120–200 ms of the 267 ms at p99 depending on the PC; recommend a current desktop i5 or better, not an older fanless box, until measured on site | PLC-INTERFACE 6 |

## Update log (section 8)
| 2026-10-30 | Runtime.Host README: the simulator reads BMP or PNG by content; this note: the timing budget per camera and the soak numbers | Claude (Mac, branch claude/line-225-soak) |

## Pending
- Engine (line safety): the EAN-13 quiet-zone verdict depends on the reader's history; the same Harpic frame (20261023_154745501) passes in 2 of 40 passes of one reader over the line's images and fails on a fresh reader. Measure the quiet zone where it does not depend on which pass found the code, and add a test that the verdict is the same for the same pixels.
- On site, Windows line PC: two GigE or USB3 cameras, the PLC and the light, 225/min for at least 10 minutes; record trigger to Done median, p99 and max per camera at the PLC, missed and timed-out parts, the runtime's memory and CPU; redo the budget above with the measured transfer and inspection times. Add it to docs/WINDOWS-INSTALL-AND-TEST.md when #146 merges.
- Runtime: the frame wait is 2 s; at 225/min a camera that sends no frame costs that part plus the next 7 (all NG). Make the wait follow the job (exposure + transfer + margin, about 150 ms here).
- Simulator: the 25 ms frame delay stands in for exposure and transfer; set it from the camera's size and link (GigE / USB3) so the Mac soak matches the line.
- Runtime.Host.Tests: `CardSignInTests.A_broken_or_cut_short_hash_matches_nothing` fails about 1 run in 5 (it sets the users file's time up to 1,000 s ahead to force a reload; the last restore writes the time now, which can be older than the cached one, so the store keeps the broken copy). Set the restore's time later than every earlier one.
