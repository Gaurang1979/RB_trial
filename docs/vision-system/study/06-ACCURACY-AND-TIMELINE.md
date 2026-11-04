# Accuracy targets and timeline

What "almost 100 %" means for each tool, how it is proved, where we are today, and the order and
time to get there. The tool details are in chapters [04a](04a-TOOLS-BASE-GEOMETRY.md),
[04b](04b-TOOLS-CODES.md), [04c](04c-TOOLS-TEXT.md) and [04d](04d-TOOLS-INSPECT-AI-PLATFORM.md); the
purchases in [05](05-HARDWARE.md).

## 1. What "almost 100 %" means, per tool

A target is met only on the owner's own images, with the stated number of samples, at the line time
target, on the line PC (not the development Mac). Line safety is part of every target: no tool may
reach its number by letting a bad part pass.

| Tool family | Target | Today | Proof (image set, pass rule) |
| --- | --- | --- | --- |
| Code Reader, every batch (QR, Data Matrix, 1D) | **≥ 99.9 % of visible codes read, 0 wrong reads**, false accept ≤ 1e-9 per code; 12 codes 100 ms, 24 codes 250-300 ms, 45/48 codes 500 ms, 96 codes 600 ms | 92.2 % overall (1,723 / 1,868), 0 wrong reads inferred from counts (texts not yet in truth); 48 DM 64.2 % and over time; shrink-wrapped QR 60.3 %; 12- and 96-code cases, real 1D, DPM never measured | `batch_check.py` on every batch; wrong-read counter and adversarial set (04b step 2); 1,000+ codes per batch before a claim |
| Code Verify | Grade within ± 0.5 grade of an ISO 15426-conformant verifier | Grades built, camera-relative, never compared | Same 200 codes on both, per parameter (04b 6.7) |
| OCR | **≥ 99.5 % characters without training, ≥ 99.95 % after product teach** | Synthetic: laser 92.6 %, TTO 98.0 %, CIJ 72.9 %, embossed 89.4 %; real laser 100 % (389 lines, one product) | ≥ 20,000 real characters per print type (04c section 6) |
| OCV | **0 wrong texts accepted**; false reject ≤ 0.5 % | 0 of every altered set accepted; true laser text passes 69 % (synthetic) | 30,000 altered lines (7 kinds); 600 good parts per print type |
| Locate | 0.05 px and 0.05° repeatability, 100 % find | Tests pass at < 1.0 px and 0.5° (`engine/tests/locate_tests.cpp:80`): 20× and 10× short | Truth set and rig T (04a): synthetic renders, 100 still captures, micrometer stage |
| Measure | ≤ 0.1 px (3σ) on static parts; gauge R&R in mm after calibration | Not built | Rig T, 100 repeats; gauge blocks |
| Calibrate | Reprojection RMS ≤ 0.1 px for one camera | Accepts up to 1.0 px (`engine/src/calibration.cpp:111`); no undistort | Calibration plate, held-out views |
| Blob and Count | 100 % exact count on the owner's sets | Inside the reader only | Count sets per product |
| Inspect and AI defect tools | **0 escapes** on real and staged bad sets (synthetic sets stress-test only and are reported apart); false reject ≤ 0.5 % | Not built (`ml/experiments/anomaly.py` caught 0 / 56 torn labels) | 600 good parts with 0 false rejects (or ≤ 1 in 949) for ≤ 0.5 %; 3,000 real and staged bad parts with 0 escapes for ≤ 0.1 % (04d section 0.1) |
| Colour | ΔE2000 repeatability and match against a chart, per 04d 9.x | Not built; cameras are mono | Colour chart, colour camera |
| Auto-find (AS-01) | ≥ 99 % of codes and text blocks found and typed | First version merged (PR #152) | Owner's sample images |
| Job setup | Label, date and code job in ≤ 15 minutes by an untrained operator | Not measured | Usability test, requirements section 13 |

Why 99.9 % and not 100 %: 100 % can never be proved by testing. The bench can show "0 misses in N",
and N sets the claim. A reader that never reports a wrong code and makes every unread code NG is
safe at any read rate; the read rate decides how many good cases are rejected.

## 2. Effort, per chapter

Person-weeks (pw) for one experienced developer with an AI assistant, as estimated in each chapter's
build plan. One pw is one developer-week.

| Chapter | Scope | Total |
| --- | --- | ---: |
| 04b Codes | Tools 5 and 6, 19 steps | ~100 |
| 04c Text | Tool 7, build plan steps 1-14 (+ 8 for Devanagari, + 2 per further script) | ~52 + 8 |
| 04a Base, Locate, Measure, Calibrate, Blob | Tools 0-4 | ~89 |
| 04d Inspect, Colour, AI, platform | Tools 8-12 and platform (platform ~64 of which P1/P2 ~40, AI ~56, Inspect and Colour ~44) | ~164 |
| 03 Standards and compliance | QR quiet zone, verification rig, regulatory profiles, GS1 presets, OPC UA, camera characterisation (Part 11 is inside 04d) | ~15 |
| **All** | | **~430** |

The Sun Teknovation machine families (blister, tablet, 360°, thermal seal, serialisation chain,
chapter [01c](01c-SOFTWARE-SUNTEKNOVATION.md)) are partly inside 04d (blister and foil) and
otherwise extra, about 30-40 pw, only if the owner wants to sell those machines.

The project has so far moved faster than one developer per pw by running several AI sessions in
parallel with the owner reviewing. That can shorten coding, but not the things that gate this plan:
line time, real images and bad parts, hardware delivery, and the owner's review and merge. The
calendar below therefore counts developers.

## 3. The plan, in waves

Start: Monday 2026-11-02. Weeks = pw ÷ developers, rounded up; a wave also cannot finish before
its longest single track (shown).

| Wave | Work | pw | 3 developers: ends | 6 developers: ends | Longest track |
| --- | --- | ---: | --- | --- | --- |
| 0 | Measure, order hardware, cheap fixes, safety counters | ~8 | 2026-11-22 | 2026-11-15 | - |
| 1 | Reading and text to target; locator and calibration to their numbers | ~76 | 2027-05-23 | 2027-02-14 | Codes ~23 pw (split over two developers for the 6-developer date) |
| 2 | P1 tool kit, Studio platform, Code Verify, remaining P2 code and text work | ~130 | 2028-03-26 | 2027-07-18 | Platform ~40 pw |
| 3 | AI, Inspect, Colour, coverage, enterprise | ~216 | 2029-08-12 | 2028-03-26 | AI ~56 pw |

So: the reading and text targets in about 7 months with three developers or about 3.5 months with
six; requirements section 13 for every tool in about 2.8 years with three developers or about 1.4
years with six.

### Wave 0: measure and fix the cheap things (~8 pw)

| Work | Effort | Owner provides |
| --- | --- | --- |
| One hour on the line: re-shoot shrink-wrap, dark-cap, faint and hologram cases on the MV-CS200 with 3-frame bursts; 12- and 96-code cases; 30-50 side-camera 1D frames; expected texts in `truth/` | 1 pw | Line time, a stopped case, a ruler on module size |
| Record lens, f-number, exposure, conveyor speed and part pitch; try f/4 against f/8, a polariser sheet, Mono12 (RESEARCH-NEXT-GENERATION R13, R14) | 0.5 pw | Polariser sheets (₹5-20k) |
| Order purchase list items 1-5 of chapter 05 (shroud, filter, polariser; strobe with overdrive; dark-field ring; dome or coaxial light; T2 line PC) | - | ₹3.5-8 lakh per station (estimates) |
| Quick fixes: QR quiet zone graded on 4 modules for QR (Micro QR stays 2) (`quality.cpp:457`); rMQR enabled behind its own `CR_RMQR` flag so it is never counted in a QR job (`zxformats.cpp:42`); a calibration over 0.1 px RMS refused (dependent tools Error, so NG) instead of today's silent fall-back to scale only (`calibration.cpp:111`); the 0.05 px / 0.05° locator check added to the geometry truth harness (04a T), outside the default ctest until the locator passes it | ~1.5 pw | - |
| Wrong-read counter and adversarial set (04b step 2) | 2.5 pw | - |
| Real text images per print type labelled into `truth/` (04c step 2) | 2 pw | CIJ, TTO, TIJ, embossed, foil and cap samples |

### Wave 1: reading and text to target (~76 pw)

| Track | Work | Effort |
| --- | --- | --- |
| Codes | Evidence budget and read-back before any new candidate source; module-scaled contrast; lighting recipes per product; PSF deblur; ERP print list (accepted only after read-back); scheduler on the pool; Data Matrix after #153 (04b steps 3-9) | ~23 pw |
| Text | Verification margin score against the full character set; real-data training with length-normalised CTC and print-type models; OCV per-character freedom; second recogniser; finder and layout prior; CIJ / TIJ / laser / TTO settings; product teach and printer fonts; formats incl. Indian month names and MRP (04c steps 1-9) | ~33 pw |
| Geometry | Truth set and rig T; sub-pixel edges and robust fits; correction service and calibration; locator to 0.05 px; fixture; frame gate (04a steps 1-3) | ~20 pw |
| Hardware | Install and prove lights, strobe, PC (chapter 05 section 12 tests) | line time |

**Exit:** every code batch ≥ 99.9 % with 0 wrong at line targets on the line PC (T2 once bought);
OCR ≥ 99.5 % on real characters; OCV 0 wrong accepted and ≤ 0.5 % false reject; locator at 0.05 px /
0.05° on rig T.

### Wave 2: P1 tool kit, Studio platform and verification (~130 pw)

| Track | Work | Effort |
| --- | --- | --- |
| Codes | Bracket per trigger and multi-frame super-resolution; QR soft v7-40 and cylinders; 1D segment stitching and DataBar; locator recall; tool outputs and Studio pages; Code Verify set-up, reports and SPC (04b steps 10-15) | ~37 pw |
| Text | Known-text auto-tune and teach UX; OpenVINO path; print-quality grade; embossed photometric stereo; foil mode; curved text on caps; OCR-A/B (04c steps 10-14) | ~17 pw |
| Geometry | Geometric match and multi-instance; Measure tools; Blob and Count; Base wrappers (04a steps 4-6) | ~28 pw |
| Platform | Hardening, safety layer, in-app benchmark, Teach wizard on the merged auto-find (#152) and auto-tune (#154), Part 11 pack (04d build order 1) | ~40 pw |
| Compliance | Verification rig and verifier correlation, regulatory profiles, GS1 presets (chapter 03) | ~8 pw |

**Exit:** a P1 measuring job and a P2 label, date and code job run on the line; grades correlate with
a bought verifier; Part 11 mode available.

### Wave 3: AI, Inspect, Colour, coverage (~216 pw)

AI runtime and acceptance gate first (ONNX Runtime / OpenVINO, INT8 with gate, calibration, OOD,
drift, ~17 pw), then anomaly, classification, detection, segmentation and counting; classical
Inspect (pharma first: labels, caps, blister and foil) and Colour once a colour camera is bought;
the remaining geometry (HDR, cylinder unwrap, fast and anisotropic matching, deformable matching,
photometric stereo); the code reader's data engine, remaining symbologies and DPM mode; Devanagari
OCR; enterprise outputs (OPC UA, MES / SQL, sync and stitching).

**Exit:** each defect tool at 0 escapes on real and staged bad sets and ≤ 0.5 % false reject;
requirements section 13 met for every tool.

## 4. What decides the calendar

| Item | Why it gates | When needed |
| --- | --- | --- |
| Line time with a stopped case (1-2 hours per wave) | Every lighting and camera change is proved on real cases | Weeks 1, 6, 14 |
| Real images and expected texts per product and print type | Text accuracy cannot be claimed on synthetic lines | Week 2, then with each new product |
| Real and staged bad parts (600 good, 3,000 bad per defect family) | Escape rates cannot be proved without them | Wave 3 |
| ERP export of printed serials per roll | The print-list method (04b step 6) | Week 6 |
| Purchases (chapter 05) | Light and PC first; cameras last unless TOO_SMALL still dominates | Order in week 1 |
| A conformant verifier and calibration card; ISO standards (chapter 03 section 13) | Verify correlation; exact grading numbers | Wave 2 |
| Owner review and merge of pull requests | Only the owner merges | Throughout |

## 5. Decisions the owner must take

| # | Decision | Raised in | Proposal |
| --- | --- | --- | --- |
| 1 | Hardware trigger and strobe wired to the camera (Line0 / Line1) and GigE scheduled action commands with PTP for the 225 ppm line. Today's fixed decision: every trigger through the PLC, nothing wired to the camera | 05 section 7 | Allow it for moving lines: a software trigger over Modbus may jitter by about 10 ms, 3.75 mm at 375 mm/s [estimate, not measured; example speed]. Each hardware-triggered frame must match the PLC's trigger ID (frame counter or encoder stamp checked against the PLC count); a missing, extra or mismatched frame is NG, and the PLC result time-out (PLC-INTERFACE.md) still applies. The PLC still decides OK / NG and reject |
| 2 | Dot-print jobs with characters never taught: today one dot short of a taught character passes about 1 time in 20 (`docs/changes/2026-10-30-claude-ocr-dot-grid.md`) | 04c | The setup check refuses a dot-print job unless every character its formats can print is taught |
| 3 | Requirement AS-08 ("used only after a design-around or a licence") against the owner's statement of 2026-10-30 that his lawyer cleared use of all patents | 00-INDEX | Confirm the scope (which territories, export, active US / EU / JP patents) and reword AS-08 to record it; keep "never copy code, models, files or screenshots". Until then the earlier territory notes in PATENTS.md stay as written |
| 4 | DL-07 (labelling in diversity order only, a patent-driven rule) | 04d | Drop it under the clearance; keep "operator-started, offline, with a gate" |
| 5 | False-accept budget per code | 04b evidence-budget step | 1e-9 per accepted code, summed per case |
| 6 | Team size | this chapter | Six developers to reach the reading and text targets by about 2027-02 and everything by about 2028-03; three developers take about twice as long |
| 7 | Whether to build the Sun Teknovation machine families (blister, tablet, 360°, thermal seal, serialisation chain) | 01c | Blister and foil inside Inspect; the rest only with a customer order |
| 8 | Requirement DL-02 allows a "not sure" AI result to pass, which conflicts with AS-12 (in doubt, NG) | 04d 10.3 | Remove the "pass" option: "not sure" is NG, with its own PLC bit |

## 6. Engine facts found by this study (checked in the code)

| Finding | Where | Effect |
| --- | --- | --- |
| QR quiet zone graded on a 2-module ring; ISO/IEC 18004 needs 4 modules | `engine/src/quality.cpp:457` | A QR with a 2-3 module margin grades clean; the fix only makes grades stricter |
| rMQR read by zxing-cpp but dropped | `engine/src/zxformats.cpp:42` | rMQR jobs read nothing |
| Locator test tolerance 1.0 px / 0.5° against a 0.05 px / 0.05° requirement | `engine/tests/locate_tests.cpp:80` | The locator is not yet proved to the requirement |
| Calibration accepts a lens model up to 1.0 px RMS | `engine/src/calibration.cpp:111` | Grading in mm can carry a poor lens model |
| Under-resolved QR solver is on by default (`CR_UNDERRES` ON) | `engine/CMakeLists.txt:22` | INDUSTRY-METHODS.md corrected in this pull request |
| Own Data Matrix decoders cover square 10×10 to 26×26 only | `engine/src/dmmatch.cpp` | Rectangular, DMRE and larger squares rely on zxing-cpp alone |
| QR soft decoding stops at version 6 | `engine/src/qrsoft.cpp` | Larger QR codes have no soft path |
| Every time target was measured on an Apple M3, not on the line PC | `docs/BATCH-TEST-RESULTS.md` | Times on the DDR3 i7 line PC are unknown; the T2 PC purchase settles it |
| Default exposure 2,000 µs | `dotnet/HikCamera/HikCamera.cs:14` | About 4.5-7.5 px of motion blur at 225 ppm for a 60-100 mm part pitch at 0.1 mm/px [estimate: pitch and scale are not recorded in the repo]: a strobe is needed on moving lines |
