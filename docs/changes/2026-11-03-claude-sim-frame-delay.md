# Simulator frame delay from the camera's size and link (GigE / USB3)

- Status: done
- Session: Claude (Mac, sim-frame-delay), 2026-11-03
- Pull request: #323
- Section 5 item: Simulator: the 25 ms frame delay stands in
- Components/files: dotnet/Runtime.Host/Cameras.cs (CameraLink, SimulatorDriver.SimSource), dotnet/Runtime.Host/RuntimeOptions.cs (SimulatorFrameMs, SimulatorLink), dotnet/Runtime.Host/RuntimeHost.cs, dotnet/Runtime.Host.Tests/LineTests.cs, tools/soak/run.sh and README.md, docs/REQUIREMENTS-VISION-SYSTEM.md (section 12: RT-SF-01)
- Depends on: none

Requirements section 12, RT-SF-01 (registered in this pull request, from the Pending list of #148).

A simulated camera now delivers each frame as late as the line's camera would: the job camera's exposure plus the frame's bytes over the link, GigE (100 MB/s, the rate the frame wait of RT-FW-01 already assumes) or USB3 (380 MB/s), chosen by the runtime option `SimulatorLink` (GigE when not set). A 1920 × 1200 frame at 2 ms exposure comes after 25 ms over GigE and 8 ms over USB3. The delay counts from the trigger, so a first decode of the picture is part of it, not added to it. `SimulatorFrameMs` is now an override: when set it is the fixed delay, as before; it no longer defaults to 5 ms. An unknown link name stops the runtime at start with a clear message. The soak script no longer passes 25 ms: it passes `SimulatorLink` (`SOAK_LINK`, GigE by default), and with its default 1920 × 1200 crops the delay is the same 25 ms it used, now taken from the job.

## Section 5
- [x] (done 2026-11-03, PR #323: the simulated camera's frame delay is the job's exposure plus the frame over the link, GigE 100 MB/s or USB3 380 MB/s from `Runtime:SimulatorLink`; `SimulatorFrameMs` still overrides it; requirements 12 RT-SF-01) Simulator: the 25 ms frame delay stands in for exposure and transfer; set it from the camera's size and link (GigE / USB3) so the Mac soak matches the line.

## Timeline (section 3)
| 2026-11-03 | Simulated camera timed like the line's camera: exposure plus the frame over GigE or USB3 (25 ms / 8 ms for 1920 × 1200 at 2 ms), fixed delay only when set | Requirements 12 (RT-SF-01) |

## Decisions (section 4)
| Simulator link | The link is a runtime option, not part of the job: the job file has no link field and a simulator setting should not change the job format. USB3 at 380 MB/s (USB3 Vision's usual payload rate), GigE at 100 MB/s as in RT-FW-01 | Requirements 12 RT-SF-01 |

## Update log (section 8)
| 2026-11-03 | Requirements 12: RT-SF-01 registered; tools/soak README: frame delay from the link | Claude (Mac, branch claude/sim-frame-delay) |

## Pending
- Runtime.Host.Tests: `ImageNameLineTests.GetImage_opens_a_saved_image_by_its_name` fails from 2026-11-04 with "This image is no longer kept." Its NG image is saved under a fixed date (2026-10-30); date it relative to today. Seen on PR #323's branch, where nothing it uses changed (Replay source, no simulator).
