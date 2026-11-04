# Test and acceptance plan

This document says how VisionStudio is verified: the test levels from unit tests to a 7-day soak
on the line, how the benchmark against Cognex VisionPro is run and kept fair, which test proves
each target in [section 13 of the requirements](../REQUIREMENTS-VISION-SYSTEM.md#13-non-functional-requirements-and-better-than-cognex-targets),
the acceptance checklist for each phase in [section 15](../REQUIREMENTS-VISION-SYSTEM.md#15-roadmap-acceptance-risks-and-open-questions),
the CI plan, and the rules for test images. It builds on what the repository already does:
xUnit tests in `dotnet/CodeReader.Tests/`, the engine self-tests in `engine/tests/`, the
benchmark in `tools/bench/` with expected counts in `truth/samples.csv`, and the Windows build
in `.github/workflows/windows-build.yml`. All numbers in this document are targets or pass
criteria, not measured results.

## 1. Test levels

| Level | ID prefix | What | Tooling | Where it runs | When |
| --- | --- | --- | --- | --- | --- |
| Unit, native | `UT-C-` | Engine internals per tool: fitting, edge detection, Reed-Solomon, licence parsing, CRC-10 | GoogleTest for new code. `engine/tests/engine_tests.cpp` (own runner, exit code) keeps working and is moved to GoogleTest when it is next extended | CI, every push | P1 on |
| Unit, .NET | `UT-N-` | Wrappers, helpers, UI view-models, PLC block codec | xUnit, as `dotnet/CodeReader.Tests` today | CI, every push | P1 on |
| Unit, Python | `UT-P-` | `visionstudio` wheel: array checks, dataclasses | pytest | CI | P4 |
| ABI | `ABI-` | Exported symbols, struct sizes and offsets against the last release | Symbol dump plus `vs_struct_size` checks | CI | P1 on |
| Golden-image regression | `GLD-` | Each tool's result on a fixed image set equals the stored result | `vsbench` (the grown `tools/bench`) with result JSON | CI (synthetic set), nightly (private set) | P1 on |
| Benchmark against VisionPro | `BEN-` | Accuracy and speed on the shared image library | `vsbench` plus a VisionPro harness | Benchmark PC | Each phase gate |
| SDK parity | `SDK-PAR-` | SDK result equals vision-application result | Result JSON comparison | Nightly | P1 on |
| Integration, cameras | `INT-CAM-` | GigE and USB3 cameras: acquisition, settings, packet loss, action commands, PTP | Camera rig | Lab, weekly and per release | P1 on |
| Integration, PLC | `PLC-` | Handshake, job change, light steps, watchdog, every protocol | PLC simulators and real PLCs | CI (simulators), lab (real) | P1 on |
| Performance | `PERF-` | Cycle time per tool and job, latency, memory | `vsbench`, built-in benchmark | Benchmark PC | Nightly |
| Soak | `SOAK-` | 7 days continuous, crash recovery | Line or lab rig | Lab, line | Per release |
| Usability | `USE-` | Operators build and run jobs | Moderated sessions | Customer sites | P1 (Run), P2, P4 |
| Localisation | `LOC-` | Strings, shaping, RTL, native review | Screenshot tests plus reviewers | CI plus reviewers | P2, P4 |
| Licence | `LIC-` | Every flow and state in [LICENSING.md](LICENSING.md) | Test licence server, VMs, dongles | CI (logic), lab (flows) | P1 on |
| Installer and upgrade | `INS-` | Fresh install, upgrade, rollback, old jobs | Clean VMs plus one industrial PC | Per release | P1 on |
| Security | `SEC-` | Remote API auth and TLS, signed updates, job encryption | Scripts, scanner | Per release | P1 on |

### 1.1 Unit tests

- New C++ code uses GoogleTest, built by CMake as one test executable per DLL
  (`vs_core_tests`, `vs_measure_tests`, ...), run with CTest.
- Unit tests use synthetic images generated in the test (as `CalibrationTests.cs` builds frames
  today and `engine_tests.cpp` builds QR codes with qrcodegen). No image files are needed, so
  unit tests run in public CI.
- Each tool has tests for: default parameters, every status code it can return, a known-answer
  synthetic image (for example a caliper on a rendered bar of known sub-pixel width), ROI
  clipping at image edges, all pixel formats it accepts, and thread safety (two instances on two
  threads give the same results as one).
- Coverage target for tool DLLs: 80% of lines, reported, not gating in P1; gating from P2.

### 1.2 Golden-image regression

- Each tool has a set of images with its stored result (`<image>.<tool>.json`, the
  `vs_tool_get_result_json` output). A run passes when every result matches exactly (CPU) or
  within the tool's stated tolerance (GPU).
- Two sets per tool: a **public synthetic set** in the repository (rendered parts, generated
  codes, printed text from open fonts) and a **private set** on the test-data store (section 9).
- A change that alters a golden result needs the new result committed with a reason in the pull
  request ("caliper: edge at 0.3 px sub-pixel bias fixed"). CI shows the diff as a table.

### 1.3 Benchmark against VisionPro

| Item | Plan |
| --- | --- |
| Licence | One VisionPro licence and dongle bought for benchmarking (requirements section 15). Check the VisionPro licence terms on publishing comparisons before any result leaves the company |
| PC | One fixed benchmark PC per tier (T1 first, T2 for AI), same OS image, power plan "High performance", no other load. Both products run on the same PC |
| Library size | ≥ 5,000 images in total by the end of P2, built from P1 on. ≥ 1,000 images for code reading (damaged and DPM), per requirements section 13 |
| Sets | One set per target: codes (1D, 2D, DPM, damaged), OCR (dates, lot codes, dot-matrix, inkjet), locate (with known poses), measure (static repeats), defects (customer OK/NG), colour |
| Sources | Customer images under agreement, own lab captures with the camera rig, public sets where the licence allows commercial benchmarking, and rendered images with exact truth (poses, sizes) |
| Truth | Hand-verified, two people for each labelled image. Rendered images have exact truth. Measure truth comes from a calibrated reference (gauge blocks, CMM report) |
| Tuning rule | Each product is tuned by an engineer who knows it, with the same time limit per set (default 2 hours), on a tuning split. Results are reported on a separate held-out split that neither engineer saw |
| VisionPro harness | A small C# console program using the VisionPro .NET API reads the same folder and writes the same result CSV columns. It runs on the benchmark PC only and is kept outside the product source tree (it needs the VisionPro SDK) |
| Repeats | Speed: 5 runs, median reported. Accuracy: deterministic, 1 run |
| Reporting | One table per target: our result, VisionPro result, difference, pass/fail. Images where only one product succeeds are listed by file name for review |

### 1.4 Truth CSV format

`truth/samples.csv` today has `file,format,expected,note` and `tools/bench` reads the first
three columns. The extended format keeps those three first, so the current bench still reads it,
and adds columns before `note`, which stays last because it is free text.

```
file,format,expected,set,tool,label,value,tolerance,source,split,note
14--Raw.bmp,qr,24,agg-cans,code,ok,,,cust-A,test,by eye: 24 cans; each label carries the same QR twice
lot-0001.png,,1,ocr-dates,ocr,ok,EXP 11/27 LOT A1234,,lab,test,inkjet on PET
gap-0007.png,,1,measure-gap,caliper,ok,2.500,0.005,lab,test,gauge block 2.500 mm
pcb-0102.png,,0,defect-pcb,anomaly,ng,scratch,,cust-B,holdout,scratch near J3
```

| Column | Meaning |
| --- | --- |
| `file` | Path relative to the set's root folder on the test-data store |
| `format` | Code format as today (`qr`, `dm`, `code128`, `ean13`, plus the new symbologies); empty for other tools |
| `expected` | Expected count (codes, blobs, found instances); 1 for single-result tools; 0 for "nothing should be found" |
| `set` | Set name; one set = one folder |
| `tool` | Tool kind (`code`, `ocr`, `caliper`, `locate`, `blob`, `anomaly`, `classify`, `colour`, ...) |
| `label` | `ok` or `ng` for the part, for defect and pass/fail sets |
| `value` | Expected value: decoded text, OCR string, measured size, pose (`x;y;angle`), class name |
| `tolerance` | Allowed difference for numeric values, in the value's unit |
| `source` | Anonymised source key (`cust-A`, `lab`, `render`); the key-to-customer list is on the store, not in the repo |
| `split` | `tune`, `test` or `holdout` |
| `note` | Free text, last column |

Decoded texts of customer codes can be serial numbers of real products. For customer sets the
`value` column stays in a private copy of the CSV on the store; the repository copy keeps counts
only, as today.

### 1.5 Integration with cameras and PLCs

| Rig | Content |
| --- | --- |
| Camera rig | At least: two GigE area cameras from different vendors (one with action commands and PTP), one GigE PoE, one USB3, one colour, one 20+ MP, one GigE line-scan. Dedicated NIC per GigE camera, a PTP grandmaster, a managed switch for packet-loss tests |
| PLC simulators (CI) | Snap7 server (S7), a Modbus TCP client, an EtherNet/IP scanner simulation, an MC protocol simulator, an OPC UA client script. Each drives the input block from a scripted sequence and checks the output block |
| Real PLCs (lab) | Siemens S7-1500 (PROFINET and PUT/GET), Rockwell CompactLogix (EtherNet/IP), Mitsubishi iQ-R (MC). Omron NX at P4 |
| Scripts | Every sequence in [PLC-INTERFACE.md](PLC-INTERFACE.md) section 5 is a script: normal cycle, job change, light steps, multi-camera, overrun, comms loss |

### 1.6 Soak test

- 7 days, 24 hours a day, on the target industrial PC tier, with a real job and real cameras
  (or the line itself at the P1 gate), triggered by a PLC at the job's rated rate.
- Image saving set to "NG only" with the disk quota at its default, so clean-up runs during the
  test.
- Logged every minute: private bytes, working set, handle count, thread count and GDI objects
  for every process; GPU memory; cycle-time percentiles; disk use.
- Fault injection on day 3 and day 5: kill the runtime process, pull a camera cable for 60 s,
  pull the PLC cable for 60 s, fill the disk to the quota.
- Pass: see `NFR-REL-*` in section 2.

### 1.7 Usability tests

- 10 shop-floor operators per round who have never used a vision system, in at least 3
  languages (English, Hindi and one more P2 language; then the P4 languages).
- Moderated, at a real machine or a bench with the camera rig. One observer notes time, errors,
  help requests and the step where each help request happened.
- Tasks: (1) start the job and read OK/NG, (2) change product by picture, (3) clear an alarm
  with "What do I do?", (4) build a label, date and code check from a blank job with the Teach
  wizard, (5) re-teach a part after a label change.
- No training is given before tasks 1–3. Task 4 and 5 follow the Teach-mode training (≤ 1 day).
- Screens and wording come from [UI-UX-SPEC.md](UI-UX-SPEC.md); language checks from
  [LOCALISATION.md](LOCALISATION.md).

### 1.8 Licence tests

Run against a test licence server with its own signing key (never the production key) and
clean VMs plus one industrial PC and two dongles.

| ID | Case |
| --- | --- |
| `LIC-01` | Serial checksum: every single-character error and every adjacent swap on 1,000 random serials is rejected |
| `LIC-02` | Install with serials, online |
| `LIC-03` | Offline activation by request file and by QR from a phone |
| `LIC-04` | Add a licence later; no restart; read-only job reopens |
| `LIC-05` | Transfer online and by release file; old PC loses the tools before release |
| `LIC-06` | Trial: start, watermark on saved images and PDFs only, results identical to full licence, expiry at day 30, second trial refused |
| `LIC-07` | Subscription: warning at 14 days to supervisor only, expiry during a run keeps the job running, read-only after restart, PLC error 411 |
| `LIC-08` | Dongle: activate, move to a second PC, remove during a run (job continues, new tools refused) |
| `LIC-09` | One fingerprint component changed: still licensed; re-anchored at next refresh. Two changed: binding mismatch |
| `LIC-10` | Clock set back 3 days: time-limited tools `ClockProblem`, perpetual tools unaffected; cleared by online refresh |
| `LIC-11` | Edited licence file (one byte), wrong key ID, corrupt file with good `.bak` |
| `LIC-12` | SDK process and vision application use the same licence; SDK create returns `VS_E_NOT_LICENSED` with the serial prefix |
| `LIC-13` | Unlicensed tool in a job: read-only, never crash, never skipped (job with each tool in turn) |
| `LIC-14` | DL model cannot be decrypted without `VS-DL` |
| `LIC-15` | VM detection: production activation in a VM refused without dongle |

### 1.9 SDK parity tests

- For every tool, the golden-image sets run through (a) the runtime service with the job, (b)
  the C API, (c) the .NET wrapper, (d) the Python wheel (from P4), and (e) a `.vstool` exported
  from the designer and loaded in the SDK.
- Pass on CPU: the `vs_tool_get_result_json` strings are byte-identical across (a)–(e).
- Pass on GPU: every numeric field within the tool's stated GPU tolerance, every pass/fail equal.
- `SDK-PAR-CR`: `truth/samples.csv` sets through `codereader.dll` (shim) and `vs_code.dll` give
  identical codes, steps and corners (see [SDK-API.md](SDK-API.md) section 5.1).

### 1.10 Installer and upgrade tests

| ID | Case | Pass |
| --- | --- | --- |
| `INS-01` | Offline install on clean Windows 10 IoT Enterprise LTSC 2021 and Windows 11 IoT Enterprise LTSC 2024, drivers bundled | Installs with no network; camera and PLC found |
| `INS-02` | Install to running job on an industrial PC with one camera and a PLC, by a field engineer following the quick-start guide | ≤ 30 minutes (target, section 13) |
| `INS-03` | Upgrade N-1 → N and N-2 → N with jobs, results database, users and licence | All jobs open and give the same results; licence kept |
| `INS-04` | Rollback N → N-1 | Previous version runs with its jobs |
| `INS-05` | Signatures | MSI and every DLL Authenticode-signed; a tampered update is refused |
| `INS-06` | Jobs from every released version (kept in a job archive) open in the new version | 100% open; results equal within tolerance |
| `INS-07` | Uninstall | Leaves jobs, results and licence unless the user asks to remove them |

## 2. Traceability: section 13 targets

Every target in requirements section 13 maps to at least one test. "Same PC" means the
benchmark PC of the tier stated.

| Target (section 13) | Test ID | Method | Pass criterion |
| --- | --- | --- | --- |
| Time to first job: untrained operator builds a label, date and code check in ≤ 15 min | `USE-01` | Usability task 4 with 10 shop-floor users, ≥ 3 languages, Teach wizard, template "Read code and verify text" | ≥ 8 of 10 users finish in ≤ 15 min with no moderator help; median ≤ 15 min |
| Learning: no training for Run mode | `USE-02` | Usability tasks 1–3 with no training | ≥ 9 of 10 users complete all three without help |
| Learning: ≤ 1 day training for Teach mode | `USE-03` | Users trained for ≤ 1 day, then tasks 4–5 | ≥ 8 of 10 complete both |
| Code read rate ≥ Cognex on damaged and DPM set | `BEN-COD-01` | ≥ 1,000-image code set, held-out split, both products tuned per 1.3 | Our read rate ≥ VisionPro's on the whole set and on each subset (1D, 2D printed, DPM) with no subset more than 0.5 points below |
| Code read rate ≥ 99.9% on graded samples (section 4) | `BEN-COD-02` | Graded subset (ISO grade C or better) | ≥ 99.9% of codes read |
| OCR ≥ 99.5% character accuracy, no training | `BEN-OCR-01` | OCR set of industrial dates and lot codes, pretrained AI engine, default settings | Character accuracy (1 − edit distance / characters) ≥ 99.5% |
| Measurement repeatability ≤ 0.1 px (3σ) | `BEN-MEA-01` | 100 captures of a static part, caliper and find-line/circle, camera fixed, light stable | 3σ of each measured value ≤ 0.1 px |
| Locator ≤ 0.05 px and 0.05° repeatability | `BEN-LOC-01` | 100 captures of a static part, geometric and correlation match | 3σ of x, y ≤ 0.05 px; 3σ of angle ≤ 0.05° (3σ as for measurement; confirm with requirements owner) |
| Locator 100% find rate | `BEN-LOC-02` | Locate set with rotations ±180°, scale 0.8–1.2, up to 30% occlusion | 100% found within 1 px and 0.5° of truth; 0 false finds |
| Defect detection: false reject ≤ 0.5%, escape ≤ 0.1% after auto-tolerance | `BEN-DEF-01` | 3 customer OK/NG sets (P3 gate), auto-tolerance from 30 good parts, held-out split | False reject ≤ 0.5% and escape ≤ 0.1% on each set; also ≥ VisionPro on each |
| Speed: within 10% of VisionPro on classical tools, same CPU | `PERF-01` | Each classical tool family on its benchmark set, T1 PC, 5 runs, median | Median time ≤ 1.10 × VisionPro median for each tool family |
| Speed: GPU AI < 20 ms on T2 for 5 MP | `PERF-02` | Anomaly, classification, detection, segmentation on 5 MP images, T2 PC, warm | 99th-percentile inference time < 20 ms per image for each model type |
| Reliability: no memory growth over 7 days | `NFR-REL-01` | Soak test 1.6 | After a 24 h warm-up, the linear trend of private bytes and handle count of every process is below 0.1% per day and no single step above 1% stays for more than 1 h; no crash |
| Reliability: automatic restart and job reload within 10 s after a crash | `NFR-REL-02` | Kill the runtime process 20 times during the soak and on the bench | `Ready` back on the PLC within 10 s every time, with the same job and variant |
| Install: one offline installer, drivers bundled, install to running job < 30 min | `INS-01`, `INS-02` | Field test on an industrial PC | As in 1.10 |
| Updates: signed, rollback, old jobs open | `INS-03` to `INS-06` | Upgrade tests | As in 1.10 |
| Section 15: SDK results identical to the vision application | `SDK-PAR-*` | 1.9 | Byte-identical on CPU |
| Section 15: licence activation online, offline, dongle, transfer, expiry | `LIC-02` to `LIC-08` | 1.8 | All pass |
| Section 15: 10 operators, ≥ 3 languages | `USE-01` to `USE-03` | 1.7 | As above |
| Section 15: 7-day soak, no crash, no memory growth | `NFR-REL-01` | 1.6 | As above |
| Section 12: watchdog, PLC treats part as NG and stops line | `PLC-WD-01` | Pull the PLC cable during inspections, each protocol | Part NG at the PLC, `CommsFault` within `WatchdogTime` + 1 cycle; "PLC not connected" shown |
| Section 8: trigger latency and jitter measured in P1 | `PLC-LAT-01` | [PLC-INTERFACE.md](PLC-INTERFACE.md) 6.1, 10,000 triggers per protocol | Measured and published; meets the targets there or the targets are revised with the requirements owner |

## 3. Phase acceptance checklists

Each phase also re-runs every earlier phase's automated tests.

### P1 Foundation

- [ ] Tool framework and `vs_core` C API frozen as ABI 1 (ABI check in CI from this point).
- [ ] Base, Locate, Measure, Calibrate and Blob tools pass unit and golden-image tests.
- [ ] `BEN-MEA-01`, `BEN-LOC-01`, `BEN-LOC-02` pass on the benchmark sets.
- [ ] `PERF-01` for Locate, Measure, Blob within 10% of VisionPro on T1.
- [ ] GigE and USB3 acquisition pass `INT-CAM-*` on the camera rig, including packet-loss handling.
- [ ] S7, EtherNet/IP and Modbus TCP pass `PLC-*` with simulators and real PLCs; PROFINET and MC
      as decided in [PLC-INTERFACE.md](PLC-INTERFACE.md) open questions.
- [ ] `PLC-LAT-01` measured and reported; targets confirmed or revised.
- [ ] Licence service: `LIC-01` to `LIC-05`, `LIC-09` to `LIC-13` pass.
- [ ] English Run screen and job designer; `USE-02` (Run mode, no training) passes with 10 operators.
- [ ] `SDK-PAR-*` pass for every P1 tool; `SDK-PAR-CR` passes.
- [ ] `INS-01`, `INS-02`, `INS-05` pass.
- [ ] Exit gate: one single-camera measuring job runs 7 days on the line (`NFR-REL-01`,
      `NFR-REL-02`).

### P2 Identify

- [ ] Code Reader (`vs_code.dll`), Code Verify, OCR/OCV (classical and AI) pass unit and
      golden-image tests.
- [ ] `BEN-COD-01`, `BEN-COD-02`, `BEN-OCR-01` pass on the held-out splits.
- [ ] Image library at ≥ 5,000 images with truth in the extended CSV format.
- [ ] Teach wizard and auto-find v1 complete.
- [ ] Hindi plus 4 more languages pass `LOC-*` and native shop-floor review.
- [ ] Exit gate: `USE-01` (label, date and code in ≤ 15 min) passes with 10 operators in ≥ 3
      languages; `USE-03` passes.
- [ ] `LIC-06` (trial) and `LIC-07` (subscription) pass.
- [ ] `INS-03`, `INS-04`, `INS-06` pass from the P1 release.

### P3 AI

- [ ] Anomaly, classification, detection, segmentation, AI OCR and AI locator pass unit and
      golden-image tests on CPU and GPU (GPU within tolerance).
- [ ] `PERF-02` (< 20 ms on T2 for 5 MP) passes for each model type.
- [ ] Training workflow: typical datasets train in < 10 minutes on T2/T4 (target from
      requirements section 4), measured on 5 datasets.
- [ ] Auto-tolerance, auto-label and drift monitor complete, with tests from
      [AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md).
- [ ] `LIC-14` passes.
- [ ] Exit gate: `BEN-DEF-01` beats the VisionPro baseline on 3 customer defect sets.

### P4 Scale

- [ ] Multi-Camera & Enterprise: 4-camera synchronised job passes `INT-CAM-SYNC` (skew target
      in [PLC-INTERFACE.md](PLC-INTERFACE.md) 5.4) and `PLC-*` multi-camera scripts.
- [ ] OPC UA, MQTT, SQL/MES outputs pass integration tests.
- [ ] Inspect, Colour and Scripting tools pass unit, golden-image and `PERF-01` tests.
- [ ] All 15 languages pass `LOC-*` and native review; Urdu RTL checked on every screen.
- [ ] .NET (`VisionStudio.Sdk`) and Python (`visionstudio`) packages published to an internal
      feed; every sample in [SDK-API.md](SDK-API.md) section 13 builds and runs in CI.
- [ ] `SDK-PAR-*` pass through C, .NET and Python.
- [ ] Remote API: `SEC-*` (TLS, tokens, roles, audit) pass.
- [ ] Web HMI read-only by default, checked.
- [ ] `LIC-08` (dongle) and `LIC-15` pass.
- [ ] Exit gate: 4-camera synchronised job runs; SDK sample applications pass.

### P5 Edge (optional)

- [ ] Linux x64 and Jetson builds pass unit and golden-image tests.
- [ ] `SDK-PAR-*` between Windows and Linux CPU builds: identical, or differences documented per
      tool with a tolerance.
- [ ] T5 cycle-time targets (30–150 ms) measured on 3 typical jobs.
- [ ] Natural-language job hint: drafts reviewed on a fixed prompt set (criteria in
      [AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md)).
- [ ] 7-day soak on a Jetson.

### Every release

- [ ] All section 13 targets met on the benchmark sets (section 2 table).
- [ ] Usability test with 10 shop-floor operators in at least 3 languages (when the UI changed).
- [ ] 7-day soak, no crash, no memory growth.
- [ ] Licence activation tested online, offline, by dongle, by transfer and on expiry.
- [ ] SDK results identical to vision-application results for every tool.
- [ ] Release notes list every golden-result change and every ABI addition.

## 4. CI plan

Today `.github/workflows/windows-build.yml` builds the engine with vcpkg on `windows-latest`,
runs `dotnet test dotnet/CodeReader.Tests`, publishes the demo, station runner and dashboard,
runs a bench smoke test and uploads a zip. `engine_tests` is built by that workflow but not run.

| Step | Change | Phase |
| --- | --- | --- |
| Run native tests | Add `engine\build\Release\engine_tests.exe` (and later `ctest`) after the build. Small change, closes today's gap | Now / P1 |
| Split workflows | `ci.yml` (every push and PR: build, unit, ABI, synthetic golden set, packaging), `nightly.yml` (private golden set, parity, performance), `release.yml` (signing, installer, upgrade tests) | P1 |
| Matrix | Windows x64 Release and Debug; Linux x64 build-only from P4 to keep the code portable; Jetson in P5 | P1, P4, P5 |
| ABI check | Dump exported symbols of every DLL and the `vs_struct_size` table; compare with the last release tag; fail on removal or change within a major version | P1 |
| P/Invoke layout | xUnit test that compares `Marshal.SizeOf` of every wrapper struct with `vs_struct_size` (would have caught any `Native.cs` drift from `codereader.h`) | P1 |
| Synthetic golden set | Runs on hosted runners in a few minutes; images are generated or stored in the repository (no customer data) | P1 |
| Private golden and benchmark sets | Run on a self-hosted Windows runner on the test-data network, labelled `vs-bench`, which mounts the store read-only. Results upload as artifacts without images | P1 |
| PLC simulators | Snap7 server, Modbus client and EIP scanner simulators run as services in the CI job; scripted handshake tests | P1 |
| Performance trend | Nightly `PERF-*` on the benchmark PC; a slowdown above 5% against the 7-day median opens an issue | P1 |
| Coverage | OpenCppCoverage and Coverlet reports on PRs; gating from P2 | P1, P2 |
| Static checks | MSVC `/W4 /WX` for new code, clang-tidy on PRs, `dotnet format --verify-no-changes` | P1 |
| Licence check of dependencies | vcpkg and NuGet dependency list compared with an allow-list (no GPL/AGPL in shipped DLLs, requirements section 14) | P1 |
| Signing | Authenticode signing in `release.yml` only, with the key in a cloud HSM; never on PR builds | P1 |
| Installer tests | `release.yml` installs on clean Windows IoT VMs (self-hosted Hyper-V) and runs `INS-01`, `INS-03`, `INS-04` | P1 |
| Python | Build and test wheels with cibuildwheel | P4 |

Pull requests from forks run only the hosted jobs; self-hosted runners with test data run only
on branches of the main repository.

## 5. Test data governance

The repository README already states that sample images are customer data and are not stored in
the repository. That rule extends to every test image from a customer.

| Rule | Detail |
| --- | --- |
| No customer images in Git | Not in this repository, not in a submodule, not in Git LFS, not in CI artifacts or logs. Pull requests are checked for image files above 100 kB outside `tests/synthetic/` |
| Where they live | A test-data store on the company network (or a private cloud bucket in the hosting region), encrypted at rest, one folder per set, read-only for CI |
| Access | Named people only, listed per set. The self-hosted runner has read-only access |
| Agreement | Every customer set has a written permission (purpose: product testing and benchmarking; who may see it; retention). No agreement, no import |
| Anonymisation | Source keys (`cust-A`) in truth files; the key list is on the store. Decoded texts and OCR values of customer sets stay on the store (1.4) |
| Truth files | Counts and public-safe columns may be in the repository (as `truth/samples.csv` today). Full truth with values stays with the images |
| Manifest | Each set has a manifest (file name, SHA-256, size, source key, date added). The repository may hold manifests so that tests can check they run on the right images |
| Retention | As in the agreement; default: delete within 30 days of the end of the agreement, and remove the set's results from reports |
| Generated data | Synthetic and rendered images are owned by the company and may be in the repository |
| Images in bug reports | A customer image attached to an issue is moved to the store and the issue keeps only the link |
| AI training | Customer images train models only for that customer unless the agreement allows general use; the training record lists the sets used ([AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md)) |

## 6. Open questions

- [ ] VisionPro licence terms on benchmarking and on publishing comparisons; legal review before
      P1 results are shared outside the company.
- [ ] Repeatability statistic: section 13 says "≤ 0.05 px and 0.05° repeatability" for the
      locator without stating 3σ; this plan assumes 3σ as for measurement.
- [ ] Where the test-data store is hosted (company NAS or cloud) and who administers access.
- [ ] Which customers provide the 3 defect sets for the P3 gate.
- [ ] Usability sites and languages for P1 and P2 rounds.
- [ ] Is a hosted Windows runner enough for PLC simulator tests, or is a self-hosted runner with
      a real PLC on its network needed for PROFINET?
