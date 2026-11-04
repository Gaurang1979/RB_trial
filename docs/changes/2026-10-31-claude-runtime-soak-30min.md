# vs-runtime 30-minute soak on the Mac: two simulated cameras, simulated Modbus PLC at 225 parts/min, per-minute resource record

- Status: done
- Session: runtime-soak-30min, 2026-10-31
- Pull request: #178
- Section 5 item: new
- Components/files: tools/soak/ (new: run.sh, plcsim.py, sample.py, report.py, analyze.py, ctl/ soakctl, results/), this note. No product code changed: no leak was found.
- Depends on: none

First step of the 24-hour line PC soak, which needs hardware. The real vs-runtime (Release) ran on this Mac with two simulated cameras (`Runtime:SimulatorFolder` and a test folder per job camera, 25 ms frame delay) and a Python Modbus TCP PLC running the camera-channel handshake (PLC-INTERFACE 3.5, CamStation logic from #148) at 225 parts/min, both cameras triggered in the same scan with ±20 ms jitter. A Vision Studio client stayed connected to the result stream all run. The setup and the PLC simulator come from PR #148, whose scripts were scratch only; they are now in `tools/soak/`.

**Frames.** The owner's sample images (`samples/Barcode Images`, 58 files) are 20 MP case photos (5472 × 3648, up to 58 MB per BMP). Played whole, one inspection plus file decode takes 200–400 ms, more than the 267 ms part period, so a 150 s try missed 576 of 1,126 parts. A 225/min line camera is 1.6–3 MP (#148). Each frame is therefore the 1920 × 1200 centre of a sample image, as 8-bit grey (`soakctl crop`, kept outside the repository), 29 frames per camera. Job SOAK2CAM: one QR tool per camera, at least one code, 100 ms budget.

## Run 1: quiet Mac (5-minute load 5–9 for most of the run), 30 minutes, heavy-test lock held

| | Camera 1 | Camera 2 |
| --- | --- | --- |
| Parts at the PLC | 6,750 | 6,750 |
| OK / NG | 5,585 / 1,163 | 5,816 / 931 |
| Missed, timed out, later than 267 ms | 2, 0, 0 | 3, 0, 1 |
| Trigger to Done median / p99 / max | 140.8 / 178.9 / 243.1 ms | 142.3 / 180.6 / 352.6 ms |
| No OK that should be NG (each inspection against a fresh reader on its frame) | 6,748 inspections: 0 | 6,747 inspections: 0 |

- **Minutes 3–30:** 12,600 parts with none missed, timed out or late. Trigger to Done p99 was 171–198 ms per minute. Both cameras run in parallel: 106 ms median vision cycle.
- **Minutes 1–2 (warm-up):** 5 parts missed and 1 late.
  - The PLC simulator itself stalled about 400 ms at 35 s (max scan gap 473 ms). Part 134 was triggered late and finished 100–170 ms after its trigger, but parts 135 and 136 then found both channels still busy (4 missed).
  - At 106 s one camera-2 cycle took 351 ms in the runtime, when the Mac's 5-minute load was about 15 from other sessions just before the lock. That cost 1 late part and 1 missed part.
  - A missed part is "not inspected", which is NG at the PLC: safe.
- **Resources after warm-up:**
  - flat: file descriptors 557 in every minute; threads 55–58 (one 71); managed heap 37–64 MB (+3 MB/h in the second half); GC committed 73–93 MB (−0.6 MB/h in the second half).
  - recorded growth, as designed: results.db +2.8 MB/h. The NG image store grew 141 MB/min with each NG frame saved (4,187 files and 4.2 GB at 30 min, under the 5,000-file and 5 GB caps), so the trim at the cap was not reached in 30 minutes.
  - Resource guard: Normal, no restart recommended, no alarm in every minute. 0 frames missed.
- **Working set** 353–404 MB, rising 73 ± 32 MB/h over minutes 3–30 while the managed heap stayed flat. Checked whether that is a native leak:
  - **Engine alone** (`soakctl loop`, both cameras' readers on two threads, the same frames loaded per part): 21,422 parts in 20 minutes (2.4 × the soak's rate). Working set fell from 280 to 250 MB. macOS `leaks`: 0 leaks. `vmmap`: 10 MB of live malloc in 182 MB of malloc regions (91 % freed and kept by the allocator).
  - **Runtime, run 2 below, vmmap during the run:** live malloc (ALLOCATED) was 44.8, 45.7, 73.3, 55.4 and 49.1 MB at minutes 3, 10, 17, 24 and the end, back to where it started. `leaks`: one 128-byte block.
  - **Conclusion:** no leak found. The working set on macOS includes freed memory the allocator keeps; it moves with load and drops when the system compresses the process (run 2: as low as 42 MB). The 24-hour soak should judge memory by the managed heap, live malloc and physical footprint, not the working set.
- **GC:** almost every collection is gen2, about 56 a minute (gen0 18, gen2 1,671 in 30 minutes). Each frame is a 2.3 MB array on the large-object heap, and its allocation budget triggers the collections. Pause time was 7.4 s in 30 minutes (0.4 %) with 63 GB allocated. It is not a leak, and the camera path pools its buffers (FrameIntake). The simulator and the per-part copies do not (Pending).

Per minute, run 1 (`tools/soak/results/2026-10-31-mac-run1-samples.csv`; GC counts are cumulative):

| Min | Working set MB | Managed heap MB | GC committed MB | GC gen0 | gen1 | gen2 | Threads | FDs | results.db MB | NG store MB | Parts | Missed | Timed out | Late | p50 ms | p99 ms | Load |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 292.5 | 51.5 | 80.0 | 1 | 0 | 27 | 58 | 557 | 4.04 | 138.7 | 430 | 4 | 0 | 0 | 143.2 | 190.1 | 6.26 |
| 2 | 279.3 | 51.0 | 73.8 | 1 | 0 | 83 | 55 | 557 | 4.07 | 273.6 | 449 | 1 | 0 | 1 | 142.5 | 196.9 | 9.52 |
| 3 | 371.2 | 36.5 | 73.0 | 1 | 1 | 149 | 55 | 557 | 4.14 | 419.5 | 450 | 0 | 0 | 0 | 142.5 | 184.9 | 5.82 |
| 4 | 366.7 | 49.5 | 77.0 | 1 | 1 | 178 | 55 | 557 | 4.17 | 560.3 | 450 | 0 | 0 | 0 | 142.3 | 179.9 | 4.98 |
| 5 | 355.1 | 49.7 | 73.8 | 1 | 1 | 242 | 55 | 557 | 4.23 | 704.1 | 450 | 0 | 0 | 0 | 142.9 | 183.6 | 5.42 |
| 6 | 356.8 | 56.7 | 83.7 | 2 | 1 | 299 | 55 | 557 | 4.27 | 839.0 | 450 | 0 | 0 | 0 | 142.3 | 184.7 | 4.87 |
| 7 | 376.5 | 46.8 | 78.0 | 2 | 2 | 365 | 56 | 557 | 4.33 | 984.9 | 450 | 0 | 0 | 0 | 142.5 | 183.5 | 7.32 |
| 8 | 352.7 | 51.2 | 77.3 | 3 | 2 | 423 | 56 | 557 | 4.36 | 1127.9 | 450 | 0 | 0 | 0 | 141.8 | 184.2 | 6.69 |
| 9 | 358.3 | 37.9 | 73.2 | 4 | 3 | 491 | 56 | 557 | 4.39 | 1269.4 | 450 | 0 | 0 | 0 | 142.4 | 180.3 | 5.21 |
| 10 | 371.6 | 48.7 | 73.4 | 4 | 3 | 520 | 56 | 557 | 4.46 | 1404.4 | 450 | 0 | 0 | 0 | 145.0 | 198.3 | 4.3 |
| 11 | 382.9 | 47.4 | 75.3 | 6 | 3 | 587 | 55 | 557 | 4.49 | 1550.3 | 450 | 0 | 0 | 0 | 141.8 | 175.5 | 4.98 |
| 12 | 385.2 | 49.9 | 77.6 | 6 | 4 | 645 | 56 | 557 | 4.55 | 1693.3 | 450 | 0 | 0 | 0 | 141.1 | 172.8 | 5.02 |
| 13 | 403.4 | 50.8 | 89.0 | 7 | 5 | 713 | 71 | 557 | 4.59 | 1834.8 | 450 | 0 | 0 | 0 | 140.1 | 175.6 | 4.45 |
| 14 | 368.8 | 55.8 | 79.8 | 7 | 5 | 742 | 55 | 557 | 4.65 | 1969.8 | 450 | 0 | 0 | 0 | 141.1 | 176.3 | 5.5 |
| 15 | 383.2 | 56.1 | 86.4 | 9 | 5 | 809 | 55 | 557 | 4.68 | 2115.7 | 450 | 0 | 0 | 0 | 141.4 | 175.0 | 4.5 |
| 16 | 358.3 | 48.3 | 74.2 | 10 | 5 | 865 | 55 | 557 | 4.75 | 2261.6 | 450 | 0 | 0 | 0 | 140.5 | 182.9 | 2.9 |
| 17 | 375.0 | 50.5 | 77.5 | 10 | 6 | 931 | 55 | 557 | 4.78 | 2402.4 | 450 | 0 | 0 | 0 | 140.7 | 171.0 | 4.4 |
| 18 | 400.8 | 64.2 | 92.7 | 10 | 8 | 997 | 60 | 557 | 4.81 | 2541.7 | 450 | 0 | 0 | 0 | 140.8 | 176.3 | 5.5 |
| 19 | 380.2 | 48.3 | 75.3 | 10 | 9 | 1055 | 57 | 557 | 4.88 | 2681.1 | 450 | 0 | 0 | 0 | 140.8 | 174.7 | 4.12 |
| 20 | 398.2 | 53.2 | 82.9 | 11 | 10 | 1122 | 55 | 557 | 4.91 | 2827.0 | 450 | 0 | 0 | 0 | 141.4 | 171.5 | 8.44 |
| 21 | 388.8 | 53.9 | 79.4 | 11 | 10 | 1151 | 56 | 557 | 4.98 | 2967.8 | 450 | 0 | 0 | 0 | 140.6 | 174.1 | 6.28 |
| 22 | 385.4 | 48.6 | 75.8 | 12 | 11 | 1217 | 55 | 557 | 5.0 | 3109.3 | 450 | 0 | 0 | 0 | 140.8 | 176.6 | 5.55 |
| 23 | 379.8 | 61.4 | 86.2 | 13 | 11 | 1274 | 55 | 557 | 5.07 | 3246.5 | 450 | 0 | 0 | 0 | 141.1 | 173.4 | 4.64 |
| 24 | 404.3 | 51.6 | 79.9 | 14 | 12 | 1342 | 55 | 557 | 5.1 | 3389.5 | 450 | 0 | 0 | 0 | 140.9 | 177.0 | 3.3 |
| 25 | 390.9 | 55.7 | 80.5 | 15 | 12 | 1400 | 55 | 557 | 5.13 | 3535.4 | 450 | 0 | 0 | 0 | 140.9 | 174.9 | 5.01 |
| 26 | 394.7 | 61.9 | 84.5 | 16 | 12 | 1458 | 56 | 557 | 5.2 | 3676.9 | 450 | 0 | 0 | 0 | 141.7 | 177.4 | 5.26 |
| 27 | 381.5 | 50.2 | 78.2 | 17 | 13 | 1526 | 55 | 557 | 5.23 | 3811.8 | 450 | 0 | 0 | 0 | 141.5 | 172.7 | 4.83 |
| 28 | 387.4 | 52.8 | 77.2 | 18 | 13 | 1585 | 55 | 557 | 5.3 | 3957.8 | 450 | 0 | 0 | 0 | 141.2 | 173.2 | 5.15 |
| 29 | 390.3 | 50.5 | 80.1 | 18 | 14 | 1614 | 56 | 557 | 5.33 | 4100.8 | 450 | 0 | 0 | 0 | 140.4 | 173.4 | 5.91 |
| 30 | 396.7 | 52.9 | 79.4 | 18 | 15 | 1671 | 55 | 557 | 5.39 | 4242.3 | 450 | 0 | 0 | 0 | 140.9 | 175.0 | 8.39 |

## Run 2: busy Mac (load 13–40), 30 minutes, vmmap snapshots: timing not judged

The other sessions had resumed, so the load was above half the cores all run (CLAUDE.md rule 11). 4,305 parts were missed, 27 timed out, 685 were late and p99 reached 979 ms per minute. Every missed or timed-out part is NG at the PLC.

- **Safety:** no OK that should be NG. 4,623 and 4,572 inspections were checked against the reference; 57 NG where the reference is OK came from reads cut by the budget, on the safe side.
- **Memory:** it serves the leak check above. File descriptors were 557 all run, the guard stayed at Normal and 0 frames were missed. The record is in `tools/soak/results/2026-10-31-mac-run2-busy-samples.csv` and `-run2-vmmap.csv`.
- **Analysis fix:** #148's analyze.py counted frames from the PLC's part log. On this busy run, timed-out parts shifted that count, and it reported 803 OK-where-NG parts that were not. The runtime's own inspections (results.db in order) match the reference frame for frame. `tools/soak/analyze.py` now reads results.db.

## Verdict

- **On the quiet Mac:** pass after a 2-minute warm-up.
  - No leak: file descriptors and threads flat; managed heap, GC committed and live malloc flat; engine `leaks` 0 and runtime `leaks` 128 bytes.
  - No part lost or late after warm-up, and no OK that should be NG in 22,690 inspections over both runs.
  - The resource guard never alarmed.
- **Warm-up:** 5 missed and 1 late, explained above (a simulator stall and one slow cycle on a loaded Mac), all NG at the PLC.
- **Not covered here:** the 24-hour line PC soak with real cameras and a real PLC.

## Section 5
- New item: `[x] (done 2026-10-31, PR #178: tools/soak; quiet Mac, 13,500 parts at 225/min on two simulated cameras with a Modbus PLC simulator: no leak (descriptors, threads, managed heap, live malloc flat; leaks 0 / 128 B), no part lost or late after a 2-minute warm-up, no OK that should be NG, resource guard quiet) vs-runtime 30-minute soak on the Mac with a per-minute resource record`

## Timeline (section 3)
| 2026-10-31 | vs-runtime 30-minute soak on the Mac: two simulated cameras (1920 × 1200 crops of the sample images) and a Modbus PLC simulator at 225/min, per-minute record of working set, managed heap, GC, threads, file descriptors, results.db, NG store and trigger to Done; quiet run: 13,495 inspections, p99 179/181 ms, no part lost or late after warm-up, no OK that should be NG; no leak (engine-only loop and vmmap/leaks on the runtime); tools/soak with the PLC simulator, sampler, report and verdict check | Requirements 12; PLC-INTERFACE 3.5, 6 |

## Decisions (section 4)
| Soak frames at line-camera size | The sample case photos are 20 MP and cannot be read in a 267 ms part period on any PC. The soak plays their 1920 × 1200 centres, the size of a 225/min camera (#148); full images remain an option (`full`). | PLC-INTERFACE 6 |
| Leak judged on live memory, not the working set | On macOS the working set includes freed memory the allocator keeps, and drops under memory pressure. A leak is judged on the managed heap, live malloc (vmmap ALLOCATED) and `leaks`; the working set is recorded. | Requirements 12 |

## Update log (section 8)
| 2026-10-31 | tools/soak/README.md: how to run the vs-runtime soak and judge it; this note: the 30-minute numbers per minute | Claude (Mac, branch claude/runtime-soak-30min) |

## Pending
- Soak on a Windows line PC: 24 hours with two real cameras, the PLC and the light at 225/min, using tools/soak's sampler and verdict check. Judge memory by managed heap, private bytes and live native memory (not the working set), and let the NG image store reach its 5 GB / 5,000-file cap to exercise the trim.
- Simulator and station: each frame and its per-part copies are new 2.3 MB large-object-heap arrays, so almost every GC is gen2 (about 56 a minute at 2 × 225/min, 0.4 % pause). Pool them as FrameIntake does, then check the gen2 rate in the soak.
- Runtime: one 351 ms cycle in the first two minutes on a loaded Mac (1 late, 1 missed part). Check on a quiet line PC whether the first minutes after Start are slower (JIT, first allocations), and warm the readers at Start if so.
- tools/soak/plcsim.py: the Python PLC stalled about 400 ms once (4 parts missed in the simulator, not the runtime). Raise its priority or write it in C before the 24-hour Mac run.
