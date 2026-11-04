# Tool methods backlog: what to build and test, tool by tool, to reach almost 100 %

One consolidated, ranked backlog of the world's best methods — vendor techniques and
patented methods — to **develop and test** for every Vision_System tool (0–12), so each tool
reaches almost 100 % results and the AI tools match or beat Cognex ViDi. Development starts one
by one from the ranked list in [section 15](#15-one-ranked-list-across-all-tools).

Owner, 2026-10-30: "for every tools we are going to build check all All possible AI tools and
cognex, halcon, teledyne dalsa, omron, national instruments and worlds best software methods,
patents and tools. prepare list of process, tools, filter and everything applicable to get 100%
results." Owner, 2026-11-01 (patent rule, after his lawyer): build from any patent or vendor,
with or without a twist, cite the source, never check validity or territory, never stop
development for a patent. This document follows that rule: every patent is listed as **the idea's
source**, and the engineering note says the twist we build, not a legal position.

## 0. How this document relates to the others, and how to read it

This is the **action backlog**. It does not repeat the research; it points to it and adds four
things the earlier documents do not have in one place:

1. **All 13 tools**, including Tool 11 (Multi-Camera & Enterprise) and Tool 12 (Scripting), which
   the per-tool survey did not cover.
2. **A test per method** — the bench batch, synthetic test, or metric that will prove the gain —
   because the owner wants every item *developed and tested*.
3. **Current build state with the file or PR**, so work already done or in progress is marked
   Done / In-progress and not re-listed as to-do.
4. **A line-safety note** on every method that decides OK or NG (requirements section 12: in
   doubt, the result is NG and the line is told).

Read alongside, and do not duplicate:
- [INDUSTRY-METHODS.md](INDUSTRY-METHODS.md) (#149) — the per-tool vendor/patent survey (Tools
  0–10) and the measured baseline. This backlog cites its row numbers.
- [ROADMAP.md](ROADMAP.md) (#145) and requirements section 16 (AS-01…AS-14) — auto setup.
- [AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md) — the full design of the six deep-learning tools.
- [ANTIGRAVITY-SUGGESTIONS.md](ANTIGRAVITY-SUGGESTIONS.md) — advanced CPU architectures, procedural 3D twins, rolling-shutter rectification, test harnesses and OOD drift methods proposed for Claude sessions to try and test.
- [GLOBAL-BEST-METHODS-PATENTS-STUDY.md](GLOBAL-BEST-METHODS-PATENTS-STUDY.md) — study of global best machine vision and AI platforms (Cognex, Keyence, HALCON, DALSA, Omron), key global patents with engineering twists, and mathematical formulations for 100% accuracy.
- [RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md](RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md) — ViDi/SuaKIT
  study, the 25-feature plan and requirement bullets DL-01…DL-26.
- [TOOL-SPECIFICATIONS.md](TOOL-SPECIFICATIONS.md) — the tool list and the sub-tool IDs used here.
- [RESEARCH-COGNEX-HALCON.md](../RESEARCH-COGNEX-HALCON.md),
  [RESEARCH-COGNEX-PATENTS.md](../RESEARCH-COGNEX-PATENTS.md),
  [RESEARCH-NEXT-GENERATION.md](../RESEARCH-NEXT-GENERATION.md), [PATENTS.md](../PATENTS.md).

### Column meanings

- **Method / what it buys:** the technique and the failure or accuracy gap it closes toward 100 %.
- **Source:** the vendor who uses it (Cognex, HALCON/MVTec, Keyence, Omron, Teledyne DALSA,
  National Instruments, Dynamsoft, Basler, Zebra…) and the patent, if any, as the idea's source.
- **State:** **Have** (built and tested), **Partial** (part built, or built inside the reader only,
  not as a user tool), **Missing**. The file or PR follows where known.
- **Gain:** **High** addresses a miss we measure today; **Med** likely to help, not yet measured;
  **Low** rare case or comfort. Every gain is a forecast until the test shows it.
- **Effort:** **S** days · **M** one to three weeks · **L** a month or more, or needs hardware or
  images from the owner.
- **Test:** the acceptance test. `bench` = `tools/bench/batch_check.py` against
  `truth/batch-baseline.csv`; `ocr_bench` = `tools/bench/ocr_bench.py`; `ctest` = engine unit
  tests; `synthetic` = a generated set with ground truth; `field` = real line images or hardware
  the owner supplies. A method is accepted only when the test shows the gain with **0 wrong reads
  / 0 missed bad parts** at the line-time target.
- **Safety:** how the method fails safe when it decides OK/NG. `n/a` = it does not decide OK/NG
  (a locate, a measure aid, an acquisition step); its output still feeds a tool that does.

The measured starting point (53 real images, 0 wrong reads: 1,723 / 1,868 codes = 92.2 %) and the
miss causes are in [INDUSTRY-METHODS.md §1](INDUSTRY-METHODS.md). The misses are mostly in the
image: low contrast under shrink wrap and on dark Data Matrix caps, blur on 24/48-code QR, codes
below ~1.8 px/module, covered/missing finders, and the time budget on 48-code Data Matrix.

---

## 1. Tool 0 — Base (acquisition and image preparation)

The biggest single lever on reads is light and optics, not decoders (INDUSTRY-METHODS §1, §3).
Base also supplies the classical image kit every later tool needs.

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **Lighting recipe per product** (dark-field low-angle for dark DM caps and DPM; cross-polarised for shrink-wrap glare; frame-fill for ≥ 2.5 px/module), stored per product | Cognex DM475V 30/45/90° light; Microscan US6352204 low-angle DPM; Keyence LumiTrax | Partial (LineAdvisor suggests exposure; no lighting rule) | **High** (shrink-wrap + dark-cap LOW_CONTRAST, TOO_SMALL) | L (line time) | `field` on real cases before/after light change; `bench` on re-captured sets | n/a (feeds reader) |
| **Bracketed exposures on one trigger, fused** with saturation masks | Cognex US9104932 / US9734376 (own form) | Partial (`multiframe.cpp`, `TestBurst.cs` burst+fusion; no auto bracket per trigger) | **High** (glare + dark in one frame) | M | `synthetic` fused vs single; `field` burst sets | n/a |
| **Module-scaled local contrast** (local mean/contrast over a few modules) replacing fixed-tile CLAHE | HALCON `contrast_tolerance`; Cognex OCRMax local normalisation; Cognex US10607047 (own form) | Partial; **tried, no gain** (PR #169: 1733→1732) — LOW_CONTRAST misses are unlocated labels, not contrast | Med | S–M | `bench` (done, no gain) | n/a |
| **Polariser / Polarsens 4-direction sensor** for foil and glossy cartons | Sony Polarsens; Euresys | Have (`vs_polar`, `polar.cpp`, `PolarTool.cs`, `PolarEngine.cs`, `Teach.razor`, PR #249) | Med (foil/film glare, stress flaw) | M | `ctest` on synthetic & Sony mosaic; `synthetic` | NG on excessive glare / stress flaw |
| **Specular highlight glare inpainting** (anisotropic isophote gradient diffusion for shrink-wrap & glossy tape glare, AG-07) | Keyence; Cognex; AG-07 | Have (`vs_inpaint`, `inpaint.cpp`, `InpaintTool.cs`, `InpaintEngine.cs`, `Teach.razor`, PR #252) | **High** (glare over codes & characters) | M | `ctest` + `dotnet test` (0.25 ms crop, 17.2 ms 20MP) | NG on excessive glare (> 35%) |
| **Rolling-shutter rectification** (line-by-line velocity compensation for moving items under rolling-shutter CMOS sensors, AG-06) | Sony IMX183 line compensation; AG-06 | Have (`vs_rectify`, `rectify.cpp`, `RectifyTool.cs`, `RectifyEngine.cs`, `Teach.razor`, PR #253) | **High** (high-speed conveyor scanning without distortion) | M | `ctest` + `dotnet test` (25.8 ms 20MP, < 1 px unwarp) | NG on excessive shear (> 15°) |
| **Photometric stereo** (3–6 lights) for embossed/engraved text and dents | Keyence LumiTrax; HALCON `photometric_stereo`; Omron FH | Have (`vs_photometric`, `photometric.cpp`, `PhotometricTool.cs`, `PhotometricEngine.cs`, `Teach.razor`, PR #250) | Med (embossed OCR, Tool 8, dent/scratch inspection) | M | `ctest` on synthetic & 3D relief; `synthetic` | NG on missing emboss / surface dent |
| **Shock filter / halo-free sharpening**, dark-frame flat field | HALCON `shock_filter`; Euresys FFC | Partial (flat field inside reader `CR_STEP_FLAT`) | Low | S | `ctest` on synthetic | n/a |
| **User Base tools** (filters, morphology, threshold, polar unwrap, arithmetic) exposed as drag-drop tools, not only inside the reader | All vendors; OpenCV | Partial (inside reader only) | Needed for P1 toolchain | M | `ctest` per op vs OpenCV reference | n/a |

**Build order for Tool 0:** bracketed-exposure fusion (rank 4 overall), then the lighting recipe
(rank 1, needs line time), then the user Base tools for P1.

---

## 2. Tool 1 — Locate

Geometric (edge-based) matching is the headline gap: we have grey-level NCC only.

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **Edge-based geometric match** (model of edge points + direction; pose without resampling; score = visible share of the model) — robust to brightness change, occlusion | Cognex PatMax; HALCON `find_shape_model`; Teledyne DALSA Sapera; expired MVTec US7062093/US7239929 and Cognex US7016539/US7065262 (free to use) | Have (`locate_tool.cpp`, `LocateTool.cs`, `PatternQuality`, sub-pixel least-squares edge refinement, `LocateToolTests.cs`) | **High** (parts that change brightness; fixture for every later tool) | M | `synthetic` pose accuracy (≤ 0.05 px / 0.05°) + occlusion/brightness sweep; `ctest` | n/a (fixture; wrong pose → downstream NG) |
| **Coarse-to-fine pyramid, relative thresholds** (large features first; contrast relative not absolute) | Cognex PatMax RedLine | Have (`locate_tool.cpp` pyramid coarse-to-fine + angle sub-stepping) | Med (lighting drift, speed) | M | `synthetic` speed + recall vs single-scale | n/a |
| **Local deformable match** (returns a deformation field + rectified image) for pouches, labels on bottles, shrink wrap | HALCON `find_local_deformable_model`; Cognex PatFlex | Have (`vs_deform`, `deform.cpp`, `DeformTool.cs`, `DeformEngine.cs`, `Teach.razor`, PR #255) | Med (curved/wrinkled parts, helps reads) | L | `ctest` + `dotnet test` (sinusoidal wrinkle, strain rupture, crumple) | NG on tear/rupture or crumple |
| **Log-polar phase correlator** (Fourier-Mellin sub-0.01px translation, 360° rotation alignment, invariant to 95% contrast fade and lighting shifts, AG-04) | Fourier-Mellin; AG-04 | Have (`vs_phase`, `phase.cpp`, `PhaseTool.cs`, `PhaseEngine.cs`, `Teach.razor`, PR #256) | **High** (extreme contrast fade & low SNR parts) | M | `ctest` + `dotnet test` (sub-pixel accuracy, 95% contrast fade) | NG on low peak/PSR |
| **Anisotropic scale (x≠y)** for oblique cameras | HALCON `create_aniso_shape_model` | Missing | Low | M | `synthetic` tilted views | n/a |
| **AI locator** (centre heatmap + angle/scale, classical sub-pixel polish) for parts where geometric match fails | own CodeNet; see Tool 10 | Partial (`ai_locator.cpp` CodeNet for codes; `locate.ai` spec) | Med | M | `synthetic` + `field`; pose within geometric tolerance | n/a |

**Patent twist (owner rule):** avoid Omron US8422759 edge-code histograms and US9286669
direction-loosening for deformation; use a displacement field / sub-models instead (own form).

---

## 3. Tool 2 — Measure

Caliper, line and circle measurement tools built in `engine/src/measure/` and `MeasureTool.cs`, with full UI setup in `Teach.razor` (PR #244).

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **Caliper** (grey projection → 1D profile; derivative-of-Gaussian edge peaks; σ < half the box) | Cognex Caliper; HALCON `measure_pos` | Have (`MeasureTool.cs`, `Teach.razor`) | Med (P1 measuring jobs) | M | `synthetic` edge position vs ground truth (sub-pixel); `ctest` | NG on no-edge / ambiguous edge |
| **Fuzzy edge scoring** (position/size/contrast/straddle 0–1, geometric mean, reason per edge) | Cognex Caliper; HALCON `fuzzy_measure_pos` | Missing | Low | S | `synthetic` ranking correctness | NG when best score below limit |
| **Sub-pixel edges** (Deriche/Canny + Devernay non-maxima) | HALCON `edges_sub_pix`; IPOL Devernay | Missing | Med | M | `synthetic` accuracy on ramps/steps | n/a |
| **Robust fits** (Tukey/Huber lines, geometric circle, RANSAC) | HALCON `fit_line`/`fit_circle`; RANSAC | Missing | Med | S | `synthetic` fit error with outliers | n/a |
| **Steger ridge** for thin lines/wires | HALCON `lines_gauss` | Missing | Low | M | `synthetic` width/asymmetry | n/a |
| **Measure from local anchors** (small sub-models near each gauge) | expired Cognex US6993177 (free) | Missing | Low | M | `synthetic` on moving parts | n/a |

---

## 4. Tool 3 — Calibrate

The calibration maths exists for grading (mm); it needs promoting to a user tool with a pass rule.

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **Plate calibration** (Zhang: homography → ML refine with distortion) as a user tool | Zhang 2000; OpenCV; HALCON `calibrate_cameras` | Partial (`calibration.cpp`, mm for grading) | Med (P1) | S | `synthetic` known board; RMS report | n/a |
| **Pass rule: back-projection RMS ≤ ~0.1 px**, report intrinsic σ | HALCON | Missing | Med (trust) | S | `synthetic`/`field` RMS gate | **Reject calibration** (and warn) if RMS over limit |
| **Undistort with valid-pixel mask** (model is computed, never applied) | Cognex checkerboard calibration | Missing | Low | S | `ctest` round-trip pixel error | n/a |
| **Richer models** (division/rational, tilt lens, telecentric, line-scan) | HALCON; OpenCV | Partial (radial+tangential) | Low | M | `synthetic` per model | n/a |
| **N-point / hand-eye** (for robots later) | Cognex; HALCON `hand_eye_calibration`; OpenCV Tsai/Park | Missing | Low (no robots today) | M | `synthetic` pose chain | n/a |

---

## 5. Tool 4 — Blob & Count

Connected-components and blob analysis built in `vs_blob` and `BlobTool.cs`, with interactive Teach inspector and area/count filtering in `Teach.razor`.

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **Threshold + labelling as a tool** (8/4-connected, Spaghetti/BBDT; min size; cleanup) | HALCON `connection`; OpenCV `connectedComponentsWithStats`; Cognex blob | Have (`BlobTool.cs`, `Teach.razor`) | Med (P1) | S | `ctest` vs OpenCV counts/areas | count/area limits → NG |
| **Shape features** (convex hull, equivalent ellipse, min circle, rotated rect) | HALCON `shape_trans` | Missing | Low | S | `synthetic` feature values | n/a |
| **Texture segmentation** (Laws masks + classify) | HALCON `texture_laws` | Missing | Low | M | `synthetic` textured regions | n/a |

---

## 6. Tool 5 — Code Reader (all symbologies)

This is the most-built tool. Most headline methods are **Have**; the remaining gaps are listed as
to-do. Full per-process tables are in INDUSTRY-METHODS §8–§13.

### 6.1 Recently built — do not re-list as to-do

| Item | PR | What it bought |
| --- | --- | --- |
| **Data Matrix with a damaged/missing L**: lattice of the learned symbol size placed from the seen L-arm + clock track, modules outside the image or over covered patches as RS erasures, serial-run guard | **#171** | 48 DM 62 → 72 of 95, 0 wrong (closes INDUSTRY-METHODS §10 / §17 rank 5) |
| **Data Matrix PowerGrid warped grid** (masked inverse-FFT module-centre map, 5×5 seed grown 8 ways, polynomial grid refit for wrinkled/curved caps), own form of Cognex US10956697 | **#218** | 48 DM 72 → 75 of 95, 0 wrong |
| **Multi-frame super-resolution** from the burst (register to 1/10 px, reconstruct 2–4× finer) | **#175** | TOO_SMALL QR below 2.6 px/module (0→4 of 10 at 1.0 px/mod); real bursts not yet recorded (closes §17 rank 6) |
| **Deblur with the PSF measured per zone** (not only re-scoring with it) | **#164** | BLUR misses (closes §17 rank 7) |
| **Locator recall measured** per batch + on BarBeR (`tools/bench/locator_recall.py`, `docs/LOCATOR-RECALL.md`) | **#159** | NO_FINDER visibility (closes §17 rank 8) |
| Module-scaled local contrast | #169 | tried, **no gain**, not adopted (§17 rank 2 resolved) |
| **Two OCR readers must agree**, calibrated line confidence | #162 | OCR safety |
| OCV per-letter freedom / check-text margin | #166 | measured; per-letter unsafe, kept for reference glyphs |
| **Text-line layout prior** ("2 2 2-4") + dot-print tight spacing | #160 | CIJ letter cutting (closes §17 rank 15) |
| Under-resolved solver `CR_UNDERRES` ON by default; own decoders report AIM id + GS1 flag | #155/#101 | small codes; symbology id |

Already **Have** (INDUSTRY-METHODS §8–§12): find-all (classical density + CodeNet AI locator),
count-driven search-harder, binarisation variants, RL/Wiener deblur with measured PSF, Lanczos +
AI enhancer, soft decoding (QR v1–6, DM erasures), known-content and serial-run matching, lattice
(inside-out, Fourier pitch), 1D at any angle with check digits, PDF417/Aztec/DotCode/Pharmacode/
Micro QR own decoders, cylinders to 138°, grading (ISO 15415/15416/29158), GS1 validation,
slot-map aggregation, miss diagnosis. **Never skip an image** — unreadable = NG with a reason (the
safe opposite of Code Corp US12210930).

### 6.2 To build or test

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **ERP print list as exact candidates** (load printed serials per roll; candidate set exact from the first code) | R8; Keyence SR-X station linking | Missing | **High** (COVERED, LOW_CONTRAST on serial products) | M | `field`/`synthetic` serial sets; `bench` | read must still pass RS / read-back; no list → normal path |
| **Fewest-error-correction-symbols ranking + two-frame confirmation** for low-evidence reads | NI IMAQ demodulation mode; Scanbot | Partial (2 RS reserved; no cross-candidate rank; no reproduce rule) | Med (**safety** as reads rise) | S–M | `bench` with injected ambiguous codes: 0 wrong | **core safety**: a low-evidence read must reproduce or be NG |
| **Real 1D images** from the side camera; join intact segments of several scan lines for torn bars | Scanbot; classic | Missing (**no real 1D measured**) | High (unknown) | S + owner frames | `field` SSCC/ITF labels; `bench` 1D batch | check digit must pass |
| **Under-resolved below 1.5 px/module**: constraint-graph pruning + routing | Cognex US11151346 (own form) | Partial (`underres.cpp` ~1.3–2.6 px/mod) | Med (TOO_SMALL) | M | `synthetic` px/module sweep | RS gate |
| **Soft QR above version 6** | Cognex family | Missing (have v1–6) | Low today (our QR are small) | M | `synthetic` large QR with erasures | RS gate |
| **rMQR / Han Xin / MaxiCode** enable | zxing-cpp | Missing | Low (until a line uses them) | S | `synthetic`/`ctest` | plausibility gate |

**Patent twist:** enhancer stays strictly *after* normal decode fails (Keyence US11854163/
US12039400 as idea source; own placement). Avoid learned per-column 1D decoders (US11334731,
US9361503) and stored blurred-pattern tables (US8596540); our fit renders candidates through the
measured blur instead.

---

## 7. Tool 6 — Code Verify (grading)

Grading is largely **Have** (ISO/IEC 15415, 15416, TR 29158, GS1). Gaps are about trust.

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **Check against a certified verifier** (same codes graded by both; bias report) | industry practice | Missing | Med (trust in our grades) | L (verifier hardware) | `field` paired grading, bias < 0.5 grade | report, not a line decision |
| **ISO-defined light / calibrated aperture** per grade | Cognex DM475V; GS1 | Partial (camera-relative unless calibrated) | Med | M (optics) | `field` against calibrated setup | grade marked "camera-relative" until calibrated |
| **Per-standard confidence mapping** (fix-step used → 0–100) | own | Missing (open question) | Low | S | `bench` correlation with grade | n/a |

---

## 8. Tool 7 — OCR / OCV

OCR safety (two-reader agreement, calibrated confidence) is built (#162); OCV margin measured
(#166). The remaining High-gain items need **owner images** of real CIJ/TTO/embossed lines.

### 8.1 OCR

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **AI OCR trained on real line images**, length-normalised CTC loss; print-type models (DotPrint, Pharma, Industrial, OCR-A/B, each with a reject class) | HALCON Deep OCR; PARSeq; Cognex ViDi Read; Zebra DL-OCR | Partial (one CRNN; synthetic + 389 real laser lines) | **High** (CIJ 72.9 %, embossed 89.4 % on synthetic) | M + owner images | `ocr_bench` per print type ≥ 99.5 % char (DL-11) | low confidence → NG (abstain) |
| **Second independent reader (reader B)** trained apart, must agree | our design | Partial (#162: agreement rule; independent model missing) | Med (safety) | M | `ocr_bench` disagreement = NG, 0 wrong accepted | disagreement → NG |
| **Text-line layout prior** ("2 2 2-4") + tight-spacing dot-print flag | HALCON `text_line_structure`, `dot_print_*`; expired Kodak US5212741/US5357581 (free) | Partial (#160 layout prior + dot-print spacing built) | Med (CIJ cutting) | S | `ocr_bench` CIJ segmentation | n/a |
| **Per-character box + confidence** so OCV can name the wrong character | CTC alignment peaks / attention | Partial (format automaton under CTC) | Med | S | `ocr_bench` per-char localisation | n/a |
| **Indian-script OCR** (Devanagari P2; Tamil/Telugu/Kannada/Bengali/Gujarati/Gurmukhi/Malayalam/Odia P4) | PaddleOCR multilingual; Noto OFL fonts | Missing | Market need | M + images | `ocr_bench` per script ≥ 98 % (DL-13) | abstain on low confidence |

### 8.2 OCV

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **OCV limits learned on real good prints** (not synthetic) | Keyence IV4 AI OCV; our learning | Partial (learning built; not run on real CIJ/TTO) | **High** (false rejects) | S + owner images | `field` good prints: false-reject < target, 0 bad accepted | over-strict is the safe side |
| **Printer-maker reference fonts** (Domino, Videojet, Markem) as the OCV reference glyphs | Cognex OCVMax | Partial (system font rendered; printer fonts missing) | Med (CIJ, TTO) | S–M | `ocv` on rendered vs real glyphs | n/a |
| **Per-letter freedom** (scale/shear per letter) | Cognex OCVMax | Partial (#166 measured: unsafe per-letter on AI read; kept for reference glyphs only) | — (measured) | M | `ocv` measured | per-letter freedom must not accept a changed char |

Already **Have**: look-alike / confusion-matrix check (0 changed-text accepted), forced alignment,
print-quality limits (mean ± kσ from 10+ good parts), format automaton, calibrated line confidence.

---

## 9. Tool 8 — Inspect

Golden template and variation model surface inspection built in `vs_inspect`, `InspectEngine.cs`, `InspectTool.cs`, and `Teach.razor`.

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **Golden template** (register, average good parts per pose, subtract, blob the difference; bright-only/dark-only modes) | expired Cognex US5850466 (free); Cognex PatInspect | Have (`vs_inspect`, `InspectTool.cs`, `Teach.razor`) | Med (label print, cap) | M | `synthetic` injected defects: recall + 0 good rejected | defect/no-template → NG |
| **Variation model** (ideal + per-pixel σ; fail on absolute AND kσ, separate bright/dark) | HALCON `prepare_variation_model` | Have (`vs_inspect`, `InspectEngine.cs`, `InspectTool.cs`) | Med | M | `synthetic` + `field` | NG on out-of-band |
| **Thermal packaging seal & tooling diagnostics** (bottle induction cap foil, pouch heat seal seam, hot melt glue bead, blister moisture barrier) | Sun Teknovation eSaksham; Cognex In-Sight thermal | Have (`vs_thermal`, `ThermalEngine.cs`, `ThermalTool.cs`, `Teach.razor`) | High (packaging seal leaks) | M | `ctest` + `dotnet test` (0.018 ms CPU, 100% pass) | leak/cold gap/breach → NG |
| **Infrared sub-surface inspection** (see-through fill level, metameric anti-counterfeit ink, SWIR moisture barrier, solar EL cracks) | Basler NIR; Xenics SWIR; Keyence | Have (`vs_infrared`, `InfraredEngine.cs`, `InfraredTool.cs`, `Teach.razor`) | High (liquid fill & packaging breach) | M | `ctest` + `dotnet test` (2.99 ms CPU, 100% pass) | breach/underfill/counterfeit → NG |
| **Wavelet homomorphic scratch & micro-defect filter** (à trous directional shrinkage on brushed metal, knurled packaging, periodic machining grain) | Keyence; Cognex US6983065 twist | Have (`vs_wavelet`, `wavelet.cpp`, `WaveletTool.cs`, `WaveletEngine.cs`, `Teach.razor`, PR #251) | High (scratches hidden under texture) | M | `ctest` + `dotnet test` (5.99 ms CPU, 100% pass) | scratch/pinhole → NG |
| **Presence / absence** (`inspect.presence`) simple, fast | all vendors | Missing (spec 8.1) | Med (assembly) | S | `synthetic` present/absent | missing → NG |
| **Embossed/dented via photometric stereo** (shape from directional lights) | LumiTrax; HALCON; Omron | Have (`vs_photometric`, `photometric.cpp`, `PhotometricTool.cs`, `PhotometricEngine.cs`, `Teach.razor`, PR #250) | Med (embossed/relief) | M | `ctest` + `dotnet test` (5.26 ms CPU) | NG on shape anomaly |
| **AI defect detection** — see Tool 10 (anomaly, segmentation) | Omron FH AI; MVTec Global Context | Missing | Med | L | see §11 | abstain/NG |

**Patent twist:** avoid a learned Siamese master-vs-test comparator (SuaLab US10937141) and
US8971663 layered golden-template pipeline; use many-patch memory-bank anomaly (PatchCore idea) or
the classical non-learned golden template.

---

## 10. Tool 9 — Colour

The engine is grey today; colour is a P4 build with grey cameras in use now, so gains are low until
colour cameras ship. Methods are standard.

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **White-reference flat field** per channel | Euresys | Missing (grey only) | Low | S | `ctest` on synthetic colour | n/a |
| **CIEDE2000 ΔE in Lab** (print limit ~ΔE 2.0, strict 0.5), checked against Sharma test pairs | colour-difference standard | Missing | Low today | S | `ctest` vs Sharma pairs (known ΔE) | ΔE over limit → NG |
| **Colour segmentation / sorting / presence** | Cognex ColorMatch; Keyence; Sherlock | Missing | Low today | M | `synthetic` colour regions | NG on absent/ wrong colour |

---

## 11. Tool 10 — AI Deep Learning, enhanced like Cognex ViDi and beyond

This is the owner's "enhance the AI tools like Cognex ViDi" item. The full design is in
[AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md); the ViDi study and the 25-feature plan are in
[RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md](RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md) (features #1–#25,
requirement bullets DL-01…DL-26). This section makes the plan concrete: **match the three ViDi
tools, then go beyond them**, each tied to a test and a line-safety rule.

**Our structural advantage over ViDi** (keep it): ViDi/VPDL is NVIDIA-only, needs a USB dongle,
High Detail cannot run on CPU, and TensorRT turns off the heatmap and outlier score. Ours runs on
CPU (ONNX Runtime + OpenVINO), needs no dongle, and keeps the heatmap and outlier score on every
tier (DL-14, DL-15, DL-26).

### 11.1 Match the three ViDi tools

| ViDi tool | What it does | Our build (model) | State | Test | Safety |
| --- | --- | --- | --- | --- | --- |
| **Blue** = supervised feature/segmentation + Blue Read (OCR, locate) | find features, pose, segment known defects, read deformed text | Segmentation: U-Net/FPN (`segmentation_models_pytorch`, MIT) + timm encoders, tiled full-res for thin defects. Read: PP-OCR + our CTC (Tool 7). Locate: AI locator (Tool 1/`dl.locate`) | Missing (OCR Partial) | `synthetic` seg mIoU; thin-defect recall ≥ 90 % (DL-04); `ocr_bench` | abstain/NG on low confidence; area/region rules → NG |
| **Red** = anomaly / novelty from good samples only | unknown cosmetic defects, no bad samples needed | **PatchCore** default (kNN on patch features, coreset memory bank) + **EfficientAD** fast option; tiling for small defects; score normalised so 99.9th good pct → 50 | Have (`vs_anomaly.h`, `vs_anomaly`, `AnomalyEngine.cs`, `AnomalyTool.cs`, `Teach.razor`) | train from 20 good in < 2 min T2 (DL-03); scratch 3×50 px recall ≥ 90 %; `synthetic` + `field` | score > limit → NG; **outlier score** on lighting change → NG/abstain |
| **Green** = classification | variant / OK-NG / defect-type after Red | EfficientNet-B0 / ConvNeXt-Atto / MobileNetV3 (timm, Apache) + linear head; **"Not sure"** = below threshold or far from every class centre | Missing | 5-class accuracy ≥ 95 % (DL-05); quick-train 3–10 images < 60 s CPU | "Not sure" → configurable, default NG |

### 11.2 Beyond ViDi — the modern methods

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **Self-supervised / foundation pretraining** (frozen DINOv2 features, Apache) for few-shot anomaly + classify; one backbone across products (Dinomaly-class unified model) | DINOv2; Dinomaly; anomalib | Missing | High (few images, robust) | M | `synthetic` few-shot; cross-product AUROC | abstain if feature far from training |
| **Anomaly: PatchCore / PaDiM** (memory-bank & Gaussian patch models) | PatchCore; PaDiM; anomalib (Apache) | Have (in-memory HNSW PatchCore, `vs_anomaly.h`, `vs_anomaly`) | High (Red tool core) | L | MVTec-style AUROC + `field` | NG on high score |
| **Few-shot / quick-train ("teach in 2 min")** — frozen backbone + prototype/logreg head, CPU | Cognex Edge Learning (EL); DINOv2 | Missing | High (unskilled operators) | M | DL-05 (< 60 s CPU, ≥ 95 %) | abstain when prototypes disagree |
| **Synthetic-data training** (CutPaste, DRAEM, AnomalyDiffusion; procedural scratches/dents) | CutPaste; DRAEM; AnomalyDiffusion | Have for codes (`violations.py`) + text (`synth_*.py`); Missing for DL tools | Med (rare defects, validation) | M | validation recall on synthetic *and* real, reported apart (DL-17) | validation only — never relaxes a limit |
| **Active learning, patent-safe** (pre-label on "Pre-label"; queue by embedding diversity + time, not confidence) | design-around US12315277/US12361100 | Partial (`autolabel.py` from engine reads) | Med (less labelling) | M | `synthetic` label-time reduction; DL-07 | training offline, gated, named approver |
| **Test-time augmentation** (average over small shifts/brightness for a steadier score) | standard | Missing | Low–Med | S | `synthetic` score variance ↓ | must not raise missed-bad rate |
| **Uncertainty / abstention + OOD** (temperature scaling; Mahalanobis/kNN/energy OOD; MC-dropout/ensemble on demand) tied to line safety | Guo (calibration); Liu (energy); SuaKIT uncertainty | Partial (OCR temperature only; no OOD on AI tools) | Med (**safety**) | S–M | DL-01 (95 % of ±40 % light flagged, < 1 % in-dist), DL-02 (ECE < 5 %) | **in doubt → NG + tell the line** |
| **Label-error finder** (confident-learning; re-implement, not cleanlab/AGPL) | confident learning paper | Missing | Med (model quality) | M | DL-10 (find ≥ 60 % of 5 % flipped) | n/a |
| **Click-to-mask / text-prompt pre-labels** (SAM 2, Grounding DINO, Florence-2 — permissive only) | SAM 2; Grounding DINO; OWLv2; Florence-2 | Partial (auto-labels from reads) | Low (labelling speed) | M | DL-09 (one-click mIoU ≥ 0.75), DL-20 | trainer only, never runtime |
| **INT8 / CPU compression with an accuracy gate** | ONNX Runtime; OpenVINO NNCF | Missing (OpenCV DNN only) | Med (T1 time) | M | DL-14 gate: no more missed bad, ≤ 0.1 pt more good rejected | gate blocks a lossy model |
| **AI drift monitor** (CUSUM + KS on scores; MMD on embeddings; rising outlier rate) | Guo/Liu/Rabanser; our design | Partial (OCR temp; monitor missing) | Med (**safety**) | M | DL-24 (warn when outlier rate doubles over 1,000 parts) | warns; never silently changes a limit |

**Licence rule (hard):** no AGPL/GPL or non-commercial weights in the runtime or trainer — no
Ultralytics YOLO, no SegFormer NVIDIA weights, no cleanlab, SAM 2 not SAM 3, DINOv2 not DINOv3
(RDV §7.2, DL-22). **Patent twist:** training is operator-started and offline, the label queue is
diversity-ordered not confidence-ordered, the runtime never suggests a class or updates a model
(design-around US12315277 / US12361100 / US11568629 / US10937141; FTO before export, DL-23).

---

## 12. Tool 11 — Multi-Camera & Enterprise

Job-level integration, not image algorithms; "best methods" here are the right open standards and a
store-and-forward design so a link failure never stops inspection or passes a bad part.

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **GigE Vision action commands + PTP (IEEE 1588)** to fire 2–8 cameras within ~100 µs | Basler scheduled action commands; GigE Vision `ACTION_CMD` | Missing (spec 11.2 `mce.sync`) | High (multi-cam parts) | M | `field` multi-cam rig: exposure timestamp spread < limit | **NG the part** if PTP unlocked or spread over limit |
| **Optical drift & OOD condition monitoring** (10D regularized Mahalanobis baseline condition tracking for LED drop, lens dust, focus drift, AG-12, AS-13) | Antigravity AG-12; AS-13 | Have (`vs_drift`, `drift.cpp`, `DriftTool.cs`, `DriftEngine.cs`, `Teach.razor`, PR #254) | **High** (early warning prevents false reject spikes) | M | `ctest` + `dotnet test` (4.2 ms 20MP, 100% pass) | Advisory warning or NG on physical degradation |
| **One job over 2–8 cameras** (Independent / Synchronised / Sequential / Stitched), results joined by trigger/part ID | HALCON image acquisition; NI; all vendors | Partial (base runs **1–3 cameras** today as parallel channels, `Job.MaxCameras=3`, licence-gated; no 2–8 job type, no sync modes, no stitch) | High | L | `field`/`synthetic` multi-cam; overhead ≤ 2 ms/part | missing camera picture → NG for that part |
| **OPC UA server with the Machine Vision companion spec** (Sign & Encrypt) | VDMA/OPC UA MV companion spec; open62541 | Missing (spec 11.3) | Med (MES integration) | M | integration test vs a UA client; update ≤ 10 ms | refused cert → explain, no silent connect |
| **MQTT store-and-forward** (local queue, never blocks inspection) | IIoT practice | Missing (spec 11.4) | Med | S–M | kill-broker test: results buffered, delivered once | broker down must not block or alter a verdict |
| **MES / SQL at-least-once with de-dup ID** | plant MES practice | Missing (spec 11.5) | Med | M | DB-offline test: buffer + de-dup, no lost/dup record | offline must not stop the line |
| **Read-only web HMI** (≥ 2 fps, role-checked control off) | vendor remote HMI | Missing (spec 11.6) | Low | M | load test; no cycle-time impact | control gated by role |

No patents of concern here (open standards). Safety theme: every enterprise link is off the
inspection path and fails to a buffered, non-blocking state — a dead link never passes a bad part.

---

## 13. Tool 12 — Scripting & Advanced Logic

The "best method" is a **sandbox that cannot crash or slow the runtime** and strong SPC maths.

| Method / what it buys | Source | State | Gain | Effort | Test | Safety |
| --- | --- | --- | --- | --- | --- | --- |
| **Sandboxed C# (Roslyn, restricted load context) / Python (module allow-list)** with watchdog, time + memory limits | .NET Roslyn; embedded CPython; vendor scripting (Cognex, HALCON HDevelop) | Missing (spec 12.1) | Med (custom rules) | L | `ctest`: timeout/OOM/throw → Error, runtime survives; overhead ≤ 0.5 ms C# | script error/timeout → **Error (NG), call engineer** |
| **Formula** (typed expression tree, parsed once) | vendor formula tools | Missing (spec 12.2) | Med | S | `ctest` expression correctness; ≤ 0.02 ms | out-of-range → NG with reason |
| **State machine** (table-driven, consecutive-NG alarm, recipe alternation) | vendor sequence tools | Missing (spec 12.3) | Med | M | `ctest` transition coverage | N bad in a row → alarm/stop |
| **SPC / data analysis** (Welford running stats; Cp/Cpk; Western Electric rules 1–4; trend slope) | Western Electric / Nelson rules; NI, vendor SPC | Missing (spec 12.4) | Med (early warning) | M | `ctest` on known series vs reference Cp/Cpk and rule hits | warns only; never fails a part itself |

No patents of concern. Safety theme: a script or formula that fails, times out or exceeds memory
returns **Error**, which the job treats as NG; SPC never changes a pass/fail, only warns.

---

## 14. Line-safety rules that bind every method above

From requirements section 12 and the owner's software-protection note (2026-11-01). A method that
decides OK/NG ships only if it obeys all of these; the **test must demonstrate each**:

1. **Doubt → NG + tell the line.** Low confidence, OOD, disagreement between two readers, a dead
   camera, a failed PTP sync, a script error, a protection/decrypt failure: the part is NG and the
   operator is told the reason. No silent wrong read, no "best guess" passed as a read.
2. **0 wrong reads / 0 missed bad parts is the gate**, before any gain in reads counts. Every
   `bench`/`ocr_bench`/`synthetic` acceptance checks this first.
3. **Never skip or pre-filter an image** as "undecodable" (the opposite of Code Corp US12210930).
4. **AI never changes itself on the line.** Training is operator-started and offline; every model
   passes the gate (AI-DEEP-LEARNING §5.4) and a named approver; runtime suggests nothing.
5. **Reduced precision passes the accuracy gate** (no more missed bad, ≤ 0.1 pt more good rejected)
   or the next precision up is used.
6. **Enterprise links are off the inspection path** and buffer when down; they never block a cycle
   or alter a verdict.

---

## 15. One ranked list across all tools

Highest **gain-per-effort toward 100 %** first, folding in the misses measured today
(INDUSTRY-METHODS §1) and the owner's product priorities (codes and date/lot text on his lines
first, then the P3 AI tools, then P4 inspect/colour/enterprise). **Each row becomes a future
section-5 item**; development picks from the top down, one at a time, and each is accepted only
when its test passes with 0 wrong / 0 missed bad at the line-time target.

Items already built since the last survey are **not** in this list; they are in §6.1 (DM
damaged/missing L #171, DM PowerGrid #218, multi-frame SR #175, PSF deblur #164, locator recall
#159, OCR two-reader #162, layout prior #160, module-contrast tried #169). INDUSTRY-METHODS §17
ranks 2, 5, 6, 7, 8, 11, 14, 15 are therefore resolved; the rows below are what remains.

| # | Method | Tool | Gain | Effort | Test | Safety note |
| ---: | --- | --- | --- | --- | --- | --- |
| 1 | **Lighting + optics recipe per product** (dark-field dark DM caps, cross-polarised shrink wrap, frame-fill) | 0 | High | L (line time) | `field` + `bench` on re-captured sets | n/a (feeds reader) |
| 2 | **ERP print list as exact candidates** for known-content / serial decoding | 5 | High | M | `field`/`synthetic` serial sets + `bench` | read still RS/read-back checked |
| 3 | **Bracketed exposures on one trigger, fused** with saturation masks | 0 | High | M | `synthetic` + `field` burst sets | n/a |
| 4 | **OCV limits learned on real good prints** | 7 | High | S + owner images | `field`: false-reject ↓, 0 bad accepted | over-strict is safe side |
| 5 | **AI OCR trained on real line images** + print-type models, length-normalised CTC | 7 | High | M + owner images | `ocr_bench` ≥ 99.5 % char per type (DL-11) | low confidence → NG |
| 6 | **Real 1D images measured** + torn-bar segment joining | 5 | High (unknown) | S + owner frames | `field` + `bench` 1D | check digit must pass |
| 7 | **Fewest-EC-symbols ranking + two-frame confirmation** (reproduce low-evidence reads) | 5 | Med (safety) | S–M | `bench` injected ambiguous: 0 wrong | reproduce or NG |
| 8 | **Edge-based geometric match (PatMax/shape-model)** | 1 | High | M | `synthetic` pose ≤ 0.05 px/0.05° + occlusion | wrong pose → downstream NG |
| 9 | **Red = anomaly from good images (PatchCore/EfficientAD)** with outlier score | 10/8 | Have (in-memory PatchCore, `vs_anomaly`, `AnomalyTool.cs`, PR #246) | High | DL-03/DL-04 + `field` | score/outlier → NG/abstain |
| 10 | **Uncertainty + OOD + abstention on every AI tool**, calibrated | 10 | Med (safety) | S–M | DL-01, DL-02 | in doubt → NG + tell line |
| 11 | **Few-shot / quick-train (Edge-Learning style, CPU)** | 10 | High | M | DL-05 (< 60 s CPU, ≥ 95 %) | abstain on disagreement |
| 12 | **Green = classification with "Not sure"** | 10 | Med | M | DL-05 | "Not sure" → default NG |
| 13 | **Second independent OCR reader (reader B)** | 7 | Med (safety) | M | `ocr_bench` disagreement = NG | disagreement → NG |
| 14 | **Caliper + sub-pixel edges + robust fits** (Measure as user tools) | 2 | Med | M | `synthetic` sub-pixel vs ground truth | NG on ambiguous edge |
| 15 | **Blue = supervised segmentation** (thin/line defects, tiled) | 10/8 | Med | L | DL-04 thin-defect recall ≥ 90 % | area/region rule → NG |
| 16 | **Golden template + variation model** (classical Inspect) | 8 | Have (`vs_inspect`, `InspectTool.cs`, PR #245) | Med | `synthetic` injected defects | defect/no-template → NG |
| 17 | **GigE action-command + PTP multi-camera sync** | 11 | High (multi-cam) | M | `field` timestamp spread < limit | unlocked PTP → NG part |
| 18 | **Local deformable match** (pouches, bottle labels, shrink wrap) | 1 | Have (`vs_deform`, `DeformTool.cs`, PR #255) | Med | `synthetic` warped + `field` | tear/crumple → NG |
| 19 | **Synthetic-data training for DL tools** (CutPaste/DRAEM/procedural) | 10 | Med | M | recall on synthetic *and* real, reported apart (DL-17) | validation only |
| 20 | **Active learning + label-error finder (patent-safe)** | 10 | Med | M | DL-07, DL-10 | offline, gated |
| 21 | **INT8 / CPU compression with accuracy gate** | 10 | Med | M | DL-14 gate | gate blocks lossy model |
| 22 | **OPC UA (MV companion spec) + MQTT/MES store-and-forward** | 11 | Med | M | integration + kill-link tests | link down never blocks/alters verdict |
| 23 | **Sandboxed scripting + SPC (Western Electric, Cp/Cpk)** | 12 | Med | L | `ctest` sandbox survival + SPC vs reference | script error → Error/NG |
| 24 | **Under-resolved below 1.5 px/module** (constraint-graph) | 5 | Med | M | `synthetic` px/module sweep | RS gate |
| 25 | **Plate calibration as a user tool + RMS pass rule** | 3 | Med | S | `synthetic` RMS gate | reject calibration over limit |
| 26 | **Blob & Count as a user tool** | 4 | Med | S | `ctest` vs OpenCV | count/area → NG |
| 27 | **Colour (ΔE CIEDE2000, white reference)** | 9 | Low (grey cameras today) | S–M | `ctest` vs Sharma pairs | ΔE over limit → NG |

Items 1, 4, 5, 6 and 9 need **line time or images from the owner** and cannot be finished from the
desk; they are ranked high because the gain is large, but each should be claimed only when the
owner can supply the images or line access (mark the claim "blocked on owner images/line").
Items with patent sources (8, 9, 15, 18, 24) follow the owner's patent rule — build the method with
our own twist, cite the source, do not stop — and the design-around notes in INDUSTRY-METHODS §16
and RDV §5.2 are the *engineering* twist, not a hold.

## 16. Limits of this backlog

- Gains are forecasts until each row's test runs on `VS_IMAGES` or on owner-supplied images; the
  bench on our images is the acceptance number (INDUSTRY-METHODS §2).
- Vendor read rates are marketing unless a test is described; no method here was tried as part of
  writing this document.
- Patent entries are the idea's source per the owner's rule; they are not a legal position and not
  a reason to hold any item.
- Tool 5 (Code Reader) is the most mature; most of its headline methods are already Have, so its
  to-do list is short by design. Tools 1–4 and 8–12 are earlier-phase and have more Missing rows,
  which the ranking reflects by weighting measured misses on today's lines first.
