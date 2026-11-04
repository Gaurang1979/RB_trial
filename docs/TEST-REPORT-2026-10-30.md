# Full system test report (2026-10-30)

Owner, 2026-10-30: "test full module for QR, Datamatrix, 1D and ocv/ocr with laser print. Full
vision application like Cognex Vision Pro and basic PLC module for sensor trigger, camera trigger,
led light trigger ... test report also required now."

- **Code:** `main` at `56e7735` (2026-10-30), no product code changed for this test.
- **Machine:** macOS 27.0.1, Apple M3, 8 cores; engine Release build (CMake 4.4.4, Unix Makefiles);
  .NET 10.0.403. Development sessions were paused so the timed runs ran on a quiet machine
  (the bench waits for a load below 4 and refuses to judge a run that goes over 12).
- **Not available on this Mac:** a camera, a PLC, real laser OCR images (the Harpic HWN lines are
  not on this machine), real 1D images, real dot-peen/DPM parts. Those parts were tested with
  simulated or drawn input; see "Not covered" at the end.

## Summary

| Module | Test | Result | Verdict |
| --- | --- | --- | --- |
| QR + Data Matrix (real cases) | `batch_check.py` on 53 sample images, 6 batches | 1733 / 1868 codes (92.8 %), no batch below baseline | **pass** (48 DM weak, see below) |
| 1D (10 types) | Drawn symbols × 10 conditions × 5 = 500 reads, + 40 images with no code | 500 / 500 right with the job's rules (see ITF), 0 reads from the no-code images | **pass** |
| OCR / OCV, laser (AI reader) | 400 synthetic laser lines | 92.6 % of characters; OCV: 0 / 400 changed texts and 0 / 396 look-alikes accepted | **pass for safety**, read rate open |
| OCR / OCV, laser (whole image) | 200 synthetic laser images, 2–3 lines up to 12° | OCV: 0 / 200 misprints accepted; 101 / 200 good prints pass | **safe, too many false rejects** |
| Engine unit tests | `engine_tests`, `dpm_tests`, `ocr_tests`, `locate_tests` | 456 / 456 checks | **pass** |
| Vision application (.NET) | 9 test projects | 692 / 694 on a busy Mac; the 2 failures pass 3 / 3 when quiet | **pass** (2 tests sensitive to load) |
| PLC: sensor → camera trigger → result → reject | `plc/queue_sim.py` (copy of the Delta DVP program) | 9 / 9 scenarios | **pass** |
| PLC: handshake over Modbus TCP | `VisionCore.Tests` PlcTests (17 tests) | all pass | **pass** |
| Application end to end (runtime + simulated PLC) | vs-runtime with sample images as camera, Modbus TCP PLC simulator, 52 parts | QR48: 18 parts match the baseline exactly; not-ready, no-frame and watchdog cases safe; **one finding**: OK written after a link loss mid-part | **pass with one safety finding** |

No test let a bad part pass when the job's rules were set. One line-safety finding: the
runtime writes OK for a part whose PLC link was lost while it was inspected (section 6). The PLC
still makes that part NG, but the runtime should not. The two open weaknesses are read rate, not
safety: Data Matrix on 48-can cases, and laser OCR on whole images with turned lines.

## 1. QR and Data Matrix (real images)

`python3 tools/bench/batch_check.py --force --bench engine/build/bench`: every sample batch from
`samples/Barcode Images`, compared with `truth/batch-baseline.csv`.

| Batch | Codes read / visible | Slowest image | Time target | Note |
| --- | --- | --- | --- | --- |
| 24 DM | 48 / 48 (100 %) | 57 ms | 250–300 ms | |
| 24 QR | 372 / 384 (96.9 %) | 230 ms | 250–300 ms | |
| 45 QR | 270 / 270 (100 %) | 394 ms | 500 ms | |
| 48 QR | 848 / 857 (98.9 %) | 412 ms | 500 ms | |
| 48 DM | 61 / 95 (64.2 %) | **738 ms** | 500 ms | weak: read rate and time (2 images) |
| other QR (shrink-wrapped 5/11/28/40) | 134 / 214 (62.6 %) | 210 ms | per count | not a product-master size |
| **All** | **1733 / 1868 (92.8 %)** | | | 4 more than the last write-up (1729) |

A case with a code missing is NG at the line (count check), so a miss costs yield, not safety.
No images of 12- or 96-can cases exist yet.

## 2. 1D codes

Script (scratch, not in the repository): each type drawn by zxing-cpp, put on an 800 × 600 page,
read by the engine through `ml/engine.py` with a 300 ms budget, 5 reads per condition. Truth is
the text zxing-cpp reads back from the clean drawing.

Conditions: 3 px/module, 2 px, 6 px, blur σ 1.0, noise sd 20, contrast 35 %, turned 15°, 90°,
180°, and turned 37° with blur and noise.

| Type | Right / 50 | Wrong | Note |
| --- | --- | --- | --- |
| EAN-13, EAN-8, UPC-A, UPC-E | 50 each | 0 | |
| Code 128, Code 39, Code 93, Codabar | 50 each | 0 | |
| GS1 DataBar | 50 | 0 | engine returns `0109501101530010`, zxing `(01)09501101530010`: same data |
| ITF (no rule set) | 45 | **5** | turned 37° + blur: reads `12` and `1234` from a 14-digit code (zxing too) |
| ITF with length 14 or check digit | 10 / 10 at 0–180° and 37° | 0 | as the job requires |

Read time: median 3.7 ms, slowest 14.5 ms. 40 images with no code (noise, bar-like stripes),
all 1D types enabled: 0 reads.

ITF can be read in part, and the part is a valid shorter ITF. Job setup already refuses an ITF
tool without a length lock, a check digit or an expected text (`CodeTool.SetupProblem`), so a
line job cannot run in the unsafe way. Keep that rule.

## 3. OCR and OCV, laser print

There are no real laser images on this Mac (the 389 real Harpic lines read 100 % in PR #77). So
these results use the synthetic sets, which are harder than the real line.

**Line level** (`python -m ocr.eval_ocr --make <dir> --n 400 --seed 900`, then `--sets`, model
`ml/models/ocrnet.onnx`, min quality 60):

| Print | Characters right | Lines exact | Wrong reads scoring ≥ 60 | OCV: true text passes | OCV: one character changed accepted | OCV: look-alike accepted |
| --- | --- | --- | --- | --- | --- | --- |
| **laser** | **92.55 %** | 74.2 % | 22 / 103 | 69.0 % | **0 / 400** | **0 / 396** |
| TTO | 98.00 % | 85.8 % | 16 / 57 | 77.5 % | 0 / 400 | 0 / 399 |
| CIJ dot | 72.87 % | 17.5 % | 4 / 330 | 5.0 % | 0 / 400 | 0 / 400 |
| embossed | 89.36 % | 60.5 % | 13 / 158 | 51.8 % | 0 / 400 | 0 / 398 |

**Whole image, laser** (`ocr.synth_scene --kind laser --n 200 --seed 31`, then `ocr_bench ... --mutate`):

- OCV against the truth: 101 / 200 pass.
- Every image with one character changed: **0 / 200 accepted**.
- Free read: 74.6 % of characters, 277 / 411 lines exact.
- Wrong reads: highest score 59, so 0 / 15 reach the pass level of 60.
- Time per image: median 80.6 ms, p95 136.3 ms, max 270.5 ms.

Check text (OCV) never passed a changed or look-alike text, on any print type. That is the line
safety rule. In free reading (Read text, no expected text), 22 of 103 wrong laser lines still
scored ≥ 60. A Read text tool without a format or expected text can therefore pass a misread line
on hard print. Use Check text, or a format (date, lot), for any laser text that decides OK/NG.

The false rejects on whole images (99 of 200) come mostly from the line finder on 2–3 turned
lines (open item from PR #77) and from the hard synthetic print. They are yield, not safety.

## 4. Engine unit tests

`ctest` on the Release build, then each executable run on its own for the counts:

| Executable | Checks | Covers |
| --- | --- | --- |
| `engine_tests` | 264 ok, 0 failed | QR 37, Data Matrix 23, 1D 61, PDF417/Aztec/Micro QR/DotCode 64, grading 72 (overlapping tags) |
| `dpm_tests` | 30 ok, 0 failed | dot-peen and laser DPM cell filters, ISO/IEC TR 29158 |
| `ocr_tests` | 154 ok, 0 failed | line finder (laser light-on-dark among them), taught font, AI reader, OCV print quality |
| `locate_tests` | 8 ok, 0 failed | Find the part |

## 5. Vision application (.NET) and PLC

`dotnet test` per project, with the engine's libraries on `DYLD_LIBRARY_PATH`:

| Project | Passed / total |
| --- | --- |
| CodeReader.Tests | 145 / 145 |
| PlantEvents.Tests | 38 / 38 |
| Runtime.Host.Tests (vs-runtime) | 18 / 18 |
| RuntimeContract.Tests | 18 / 18 |
| StationAgent.Tests | 62 / 62 |
| StationGuard.Tests | 57 / 57 |
| Studio.Tests (Avalonia Vision Studio) | 93 / 94 |
| VisionCore.Tests (tools, jobs, PLC, reports) | 241 / 242 |
| VisionStudio.Tests | 20 / 20 |
| **All** | **692 / 694** |

The two failures happened while the machine was still loaded (load about 25 on 8 cores). Rerun
alone, 3 times each on a quieter machine, both passed 3 / 3:

- `LineGradingTests.ToolGradesOnlyTheSampledParts` failed with "No code read" after 338 ms, which
  is a time budget being exceeded.
- `RuntimeLinkTests.A_runtime_that_stops_answering_while_streams_hang_is_dropped_by_the_probe`
  failed with TimeoutException where IOException was expected.

Both tests should be made robust to a busy machine (pending).

**PLC.** The line's design (PLC-INTERFACE.md, ARCHITECTURE.md 2.x): the PLC reads the sensors,
software-triggers the camera through the handshake, and switches the light. Camera hardware
trigger and strobe lines are not used.

- `plc/queue_sim.py`, the scan-by-scan copy of `plc/delta-dvp/VisionLineDVP.st`: 9 / 9 scenarios
  pass. Front/back cameras on one sensor, three camera places, a camera sensor missing a part,
  camera busy (parts every 6 scans), an extra sensor edge, two missed parts, touching and short
  parts. In every scenario the part that should be rejected was rejected, and no other part.
- Handshake over Modbus TCP (`PlcTests.HandshakeOverModbus`) and 16 more PLC tests all pass:
  cameras at different places triggering on their own, the settings block round trip (which
  carries light delay and light on-time), jam state, bypass, verify sensor.
- The light (`q_VisionLight`: on after LightDelay for LightOn) runs in the PLC program only. The
  simulator does not model it, so its timing was checked only as the settings value sent. It needs
  a try on the real PLC.

## 6. Application end to end (runtime + simulated PLC)

The real `vs-runtime` (`dotnet/Runtime.Host`, Release, `--console`) ran on macOS with
`Runtime:Source=camera` and `Runtime:SimulatorFolder` playing case images as the camera. A
scratch gRPC client signed in (Operator, PIN) and started the line in PLC mode, which opened the
runtime's Modbus TCP server (`data/plc.json`: port 15502, watchdog 1000 ms). A scratch Python
PLC (raw Modbus FC3/FC16) did what the PLC does in handshake mode (PLC-INTERFACE 4.1, 5.1): a
sensor edge, then a trigger with a new part id, wait for Done with the echoed id, read OK/NG,
drop the trigger. It also kept the two-way heartbeat. Jobs: QR48 (48 codes, 500 ms budget) and
QR24 (24 codes, 275 ms budget). 52 parts in all. The scripts are in the session's scratch folder,
not in the repository.

| Run | Parts | Result | Trigger → Done |
| --- | --- | --- | --- |
| QR48, quiet Mac (load about 1.7) | 18 | 11 OK, 7 NG. Every NG had a code missing in the baseline (42–47 of 48 read). Two images read 48 / 48 here (46 and 47 in the baseline) and were OK. | median 376 ms, max 457 ms |
| QR24, Mac loaded by 10 building sessions (load 28–56) | 16 | **not judged**: 1 OK, 15 NG, reads cut at 150–200 ms (5–23 of 24). All on the safe side. | median 253 ms, max 467 ms |
| Trigger with no job, or job loaded and line stopped | — | trigger rejected, error 111, Ready 0 | — |
| Trigger with no frame from the camera | — | NG, error 302 after 2.0 s; alarm 300 | — |
| PLC heartbeat stops (watchdog) | — | Ready 0 and error 100 after 919 ms; `plc_connected` false, alarm 100; the vision heartbeat keeps counting; a trigger during the loss is rejected (111); after the heartbeat returns, Reset alarm gives Ready and the next part runs | — |
| PLC socket closed 50 ms after a trigger | 1 | **finding**: the runtime still wrote Done 1, **OK 1**, echo 8000 (with error 100) | — |

**No part that should have been NG came out OK.**

**Finding (line safety, against PLC-INTERFACE 5.6).** "The runtime never sets OK for a part whose
trigger arrived before the link loss and whose result could not be delivered." `PlcRuntime.Finish`
(`dotnet/VisionCore/Plc/PlcRuntime.cs`) writes `Ok` and `Done` from the inspection without
checking whether the link or the heartbeat was lost meanwhile. A PLC that reconnects reads Done 1 /
OK 1 for that part. Today the PLC still decides safely: its function block makes the part NG on
CommsFault, and it accepts a result only when the echo matches its current trigger id. But the
runtime half of the rule is missing. Fix: write NG (with error 100) for a result finished after a
link or heartbeat loss since its trigger, and clear Done/OK on reconnect until the next trigger.

**Smaller findings**

- 29 of the 34 `.bmp` sample files are really PNG. The runtime's simulator loader reads BMP only,
  so each such part was NG 302 after the 2 s frame timeout (safe). The run above used gray BMP
  copies made in scratch. The loader should read by content, or refuse the folder at start.
- A trigger during a comms loss overwrites error 100 with 111, so the PLC loses the first cause.
- Alarm 300 (no frame) stays across Stop/Start until a good frame arrives.
- `JobActive` is 1 while the line is stopped; PLC-INTERFACE 3.2 says loaded and running.
- A non-numeric PLC part id is stored as NULL in `results.db`.
- The Modbus server listens on all interfaces (0.0.0.0); it should be the PLC network only, or
  configurable.

## Not covered (needs hardware or images)

- A camera on a real line: GenICam/Hikrobot acquisition, exposure, frames missed.
- A real PLC (Delta DVP / AS, Siemens, Rockwell, Mitsubishi): handshake timing and jitter, the
  light output timing, the encoder, the reject chute.
- Real laser OCR images on this Mac (copy the Harpic set or new line images to `VS_IMAGES`),
  real 1D, Pharmacode, DotCode and DPM part images, and 12- and 96-can cases.
- The Avalonia Vision Studio window by eye, on a line PC at 1366 × 768 with touch (Windows).
- 24-hour soak on a line PC.

## Pending

- PLC runtime: never write OK for a part whose result finished after a PLC link or heartbeat loss since its trigger (PLC-INTERFACE 5.6); write NG with error 100 and clear Done/OK on reconnect (line safety).
- Runtime simulator folder: read images by content (29 of 34 sample `.bmp` files are PNG), or refuse the folder at start.
- PLC runtime: keep error 100 when a trigger arrives during a comms loss; clear alarm 300 on Stop; JobActive only while running; store a non-numeric part id; Modbus server bound to a chosen interface.
- End-to-end QR24 run on a quiet Mac (not judged on 2026-10-30: load 28–56).
- Make `LineGradingTests.ToolGradesOnlyTheSampledParts` and `RuntimeLinkTests.A_runtime_that_stops_answering_while_streams_hang_is_dropped_by_the_probe` pass on a loaded machine.
- Data Matrix 48-can batch: 61 / 95 read and 738 ms on the slowest image (target 500 ms).
- Laser OCR on whole images with 2–3 turned lines: 101 / 200 good prints pass Check text.
- Read text without a format or expected text can pass a misread line on hard print (22 of 103 wrong laser lines scored ≥ 60): warn at setup or require a format when the tool decides OK/NG.
- Hardware run: camera, PLC handshake timing, light output timing and reject on the line.
