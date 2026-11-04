# 04b. Tools: code reading and verification (Tool 5 Code Reader, Tool 6 Code Verify)

Part of the [Vision System study](00-INDEX.md). Written 2026-10-30 on branch `claude/tool-study`.
This chapter is the complete list of what the Code Reader and Code Verify must do, layer by layer,
for every symbology the owner's lines print or may print, with what is built today, what is
missing, who does it best, and the plan to close each gap.

It extends, and does not repeat:
[INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md) sections 8-13 and 17 (survey and ranked list),
[TOOL-SPECIFICATIONS.md](../TOOL-SPECIFICATIONS.md) Tools 5 and 6 (the specified parameters,
explain keys and built notes), [RESEARCH-COGNEX-PATENTS.md](../../RESEARCH-COGNEX-PATENTS.md)
(disclosed steps and parameters of every Cognex family), and
[RESEARCH-NEXT-GENERATION.md](../../RESEARCH-NEXT-GENERATION.md) (R0-R16 and the reliability plan).

## How to read this chapter

- **Status words** for our engine: **Have** (built and tested), **Partial** (part built, or only
  inside the engine without a user setting), **Missing**. The file or change note follows.
- **Effort** in person-weeks (pw): one experienced developer with an AI assistant.
- **Phases** used in this chapter: **P1** = reading accuracy at line speed on the owner's lines
  (ROADMAP step 1); **P2** = the Read code / Grade code tools in Vision Studio behind the tool
  contract (ROADMAP steps 2-3); **P3** = learned models (ModuleNet, enhancer on real pairs);
  **P4** = symbologies and marks no owner line uses yet (DPM, Han Xin, MaxiCode, composite);
  **P5** = export builds and certification.
- **Hardware**: "line i7" is the owner's fanless i7 line PC with the 20 MP Hikrobot MV-CS200
  (5472 x 3648). T1 and T2 are the tiers of requirements section 11 (T2 = Core i7, 8+ cores, RTX
  4060 class GPU). Speed targets are on the line i7 unless marked T2.
- **Patents**: owner's statement of 2026-10-30 (legal clearance), after advice from his lawyer:
  "we can use all patents and process in our library". This chapter gives no legal opinion; the
  scope (territories, export) is for the owner to confirm (chapter 02). Patented methods below are
  methods to adopt, with the patent cited and its disclosed steps. Status and expiry are Google
  Patents' estimates, kept as information.
- **Sources**: every link was opened, by this chapter's research or by the cited repo research
  (research-codes pass of 2026-10-30, RESEARCH-COGNEX-PATENTS.md), unless marked **[S]** (seen
  only in a search-result summary). Vendor read rates are marketing unless a test is described.
- Copyright: no vendor text, code, model or screenshot is copied; vendor screens are described in
  words with a link.

## 1. Where we stand and what "done" means

Measured on 53 real sample images at the line time targets, 0 wrong reads inferred from counts
(texts not yet in truth)
(`truth/batch-baseline.csv`, main of 2026-10-30; detail in
[INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md) section 1):

| Batch | Read / visible | Max time vs target | Main miss reasons | Who holds the work now |
| --- | ---: | --- | --- | --- |
| 12 codes | no images | target 100 ms, never measured | - | owner frames needed |
| 24 QR (16 images) | 372 / 384 (96.9 %) | 234 ms / 250-300 | BLUR 18, NO_FINDER 12, LOW_CONTRAST 5, GLARE 5 | free (this chapter) |
| 24 DM (2) | 48 / 48 (100 %) | 55 ms / 250-300 | - | - |
| 45 QR (6) | 270 / 270 (100 %) | 403 ms / 500 | - | - |
| 48 QR (18) | 843 / 857 (98.4 %) | 501 ms / 500 | BLUR 15, LOW_CONTRAST 14, NO_FINDER 6, COVERED 4 | free |
| 48 DM (2) | 61 / 95 (64.2 %) | 761 ms / 500 | LOW_CONTRAST 39, COVERED 26, NO_FINDER 16 | **PR #153** (draft claim) |
| 96 codes | no images | target 600 ms, never measured | - | owner frames needed |
| Shrink-wrapped excise QR (9) | 129 / 214 (60.3 %) | - | LOW_CONTRAST 114, TOO_SMALL 19 | re-shoot first (R0) |
| 1D labels | synthetic only: 500 / 500, 0 false from 40 empty images | median 3.7 ms | - | owner frames needed |
| Two-camera line at 225 ppm | not measured | 267 ms per part cycle | - | **PR #148** (draft claim) |

With ample time (20 s) the same reader read 99.7 % of a 1,965-code set
([RESEARCH-NEXT-GENERATION.md](../../RESEARCH-NEXT-GENERATION.md) section 1): most of today's gap
is time and image quality, not missing decoders.

**Targets for this chapter** (owner, 2026-10-30, and requirements 13/17):

| Target | Number | Proof |
| --- | --- | --- |
| Read rate per batch | **>= 99.9 % of visible codes** in every batch (12, 24, 45/48, 96; QR and DM; 1D labels; shrink-wrap after re-shoot) | `tools/bench/batch_check.py` on `VS_IMAGES` with texts in truth, at the line time targets, on a quiet machine; >= 1,000 codes per batch type before the claim |
| Wrong reads | **0** on the bench and on a standing adversarial set; computed false-accept **<= 1e-9 per code** (proposed; owner decision 5 in chapter 06) | Wrong-read counter in the bench (needs texts in truth), adversarial set (5.0.4), Monte-Carlo per soft path |
| Time | 12 codes 100 ms, 24 codes 250-300 ms, 45/48 codes 500 ms, 96 codes 600 ms, per image, max not median | Bench "max ms" per batch on the line i7 (not only the Mac) |
| Against Cognex | >= Cognex read rate on the same >= 1,000-image damaged and DPM set (requirements 13) | Side-by-side with a bought DataMan licence (AS-10) |
| Grading | Grades within +-0.5 grade (one letter at most) of an ISO/IEC 15426-conformant verifier on the same codes | 100 codes per symbology graded by both (6.7) |

## 2. Summary table

| Tool id | Name (operator words) | Layers | Status | Accuracy target | Speed target | Effort (pw) | Phase | Depends on |
| --- | --- | --- | --- | --- | --- | ---: | --- | --- |
| 5 | Code Reader, "Read code" | L0-L6 | Partial (engine Have; tool contract Partial) | >= 99.9 % visible, 0 wrong | 100/300/500/600 ms per case | see rows | P1-P2 | Tool 0 camera, 1 fixture |
| 5.0.0 | Acquire and exposure search (shared) | L0 | Partial | ML 70-86 % of full scale on codes; < 0.5 % saturated module area | probe <= 10 ms; search <= 30 s at setup | 4.5 | P1 | camera driver, PLC strobe |
| 5.0.1 | Correct (all image fixes) | L1 | Partial | each fix adds reads with 0 wrong | each fix <= 8 ms per region | 14 | P1-P3 | 5.0.2 module size |
| 5.0.2 | Locate codes | L2 | Have (classical + CodeNet) | 100 % of visible codes proposed | <= 25 ms per 20 MP image | 5 | P1 | ml/ |
| 5.0.3 | Decode engines (zxing-cpp, libdmtx, own decoders, soft, known, serial, lattice, grid fit, under-resolved, frames) | L3 | Have / Partial | see per symbology | <= 15 ms per clean code | 21 | P1-P4 | 5.0.1, 5.0.2 |
| 5.0.4 | Confirm and decide (evidence budget, read-back, N-version) | L4 | Partial | 0 wrong; false accept <= 1e-9 (proposed, owner decision 5 in chapter 06) | <= 2 ms per low-evidence read | 7 | P1 | 5.0.3 |
| 5.0.5 | Scheduler and time budget | L3-L4 | Partial (pool Have; fixed splits) | no code lost to time below the targets | targets above | 4 | P1 | #148 numbers |
| 5.0.6 | Learn and tune (per product) | L5 | Partial | learned state never lowers reads | load <= 50 ms | 3 | P1-P2 | #152, #154 (both merged; auto-find and auto-tune Have, first version) |
| 5.0.7 | Output and audit | L6 | Partial | every code and miss recorded | <= 1 ms per code | 2 | P2 | Tool 0.15 |
| 5.1 | Read code (parent) | L2-L6 | Partial | as 5 | as 5 | 3 | P2 | tool contract |
| 5.2 | 1D codes (common) | L1-L4 | Have (synthetic) | >= 99.9 % on real labels | <= 15 ms per label | 3 | P1 | owner frames |
| 5.2.1 | GS1-128 / SSCC / Code 128 | L1-L4 | Have | 99.95 % | <= 10 ms | 1.5 | P1 | 5.2 |
| 5.2.2 | EAN-13/8, UPC-A/E, add-ons | L1-L4 | Have | 99.95 % | <= 10 ms | 0.5 | P1 | 5.2 |
| 5.2.3 | ITF-14 / Interleaved 2 of 5 | L1-L4 | Have (rule required) | 99.9 %, 0 partial reads | <= 10 ms | 1 | P1 | 5.2 |
| 5.2.4 | Code 39 (and Code 32 / PZN **NEW**) | L3-L4 | Have / Missing | 99.9 % | <= 10 ms | 0.5 | P4 | 5.2 |
| 5.2.5 | Code 93 | L3-L4 | Have | 99.9 % | <= 10 ms | 0 | - | 5.2 |
| 5.2.6 | Codabar | L3-L4 | Have | 99.9 % | <= 10 ms | 0 | - | 5.2 |
| 5.2.7 | GS1 DataBar family | L3-L4 | Have (zxing) | 99.9 % | <= 15 ms | 1.5 | P1 | 5.2 |
| 5.2.8 | GS1 Composite CC-A / CC-B / CC-C **NEW** | L2-L4 | Missing | 99.9 % both parts, linked | <= 25 ms | 3 | P4 | 5.2.7, 5.3.6 |
| 5.2.9 | Pharmacode one-track | L2-L4 | Have | 99.9 %, value lock | <= 10 ms | 0.5 | P1 | #127 |
| 5.2.10 | Pharmacode two-track **NEW** | L2-L4 | Missing | 99.9 %, value lock | <= 10 ms | 1.5 | P4 | 5.2.9 |
| 5.2.11 | Other 1D (MSI, Telepen, Code 11, DX) **NEW** | L3 | Missing (zxing reads Telepen, DX) | 99.9 % | <= 10 ms | 1 | P4 | - |
| 5.3 | 2D codes (common) | L1-L4 | Have | as 5 | as 5 | 1 | P2 | - |
| 5.3.1 | QR Model 2 | L1-L4 | Have | >= 99.9 % per batch | per case targets | 4 | P1 | 5.0 |
| 5.3.2 | Micro QR | L1-L4 | Have | 99.9 % | <= 15 ms | 0.5 | P4 | - |
| 5.3.3 | rMQR **NEW** (spec omits it) | L1-L4 | Missing (zxing reads it, engine drops it) | 99.9 % | <= 15 ms | 2 | P4 | 5.3.1 |
| 5.3.4 | Data Matrix ECC 200: square, rectangular, DMRE | L1-L4 | Partial (own decoders square 10-26 only) | >= 99.9 % per batch | 48 DM <= 500 ms | 6 | P1 | #153 |
| 5.3.5 | Aztec (compact, full, Rune) | L1-L4 | Partial (zxing only) | 99.9 % | <= 15 ms | 2 | P4 | - |
| 5.3.6 | PDF417, MicroPDF417 | L1-L4 | Have / Partial (Micro: zxing only) | 99.9 % | <= 20 ms | 1.5 | P4 | - |
| 5.3.7 | DotCode | L1-L4 | Have (synthetic only) | 99.9 % | <= 20 ms | 2 | P4 | real images |
| 5.3.8 | Han Xin **NEW** | L3 | Missing | 99.9 % | <= 20 ms | 4 | P4 | - |
| 5.3.9 | MaxiCode **NEW** | L3 | Missing (zxing partial) | 99.5 % | <= 20 ms | 1 | P4 | - |
| 5.4 | DPM: dot peen, laser etch, inkjet dots on metal / plastic / glass | L0-L4 | Partial | 99.9 % on graded C or better marks | <= 60 ms per mark | 6 | P4 | lights, real parts |
| 5.5 | Aggregation / case mode | L2-L6 | Have / Partial | 100 % of cases right (complete or named NG) | per case targets | 6 | P1 | ERP export |
| 5.6 | Read diagnosis | L4-L6 | Have | right reason >= 90 % of misses | <= 20 ms per image | 2 | P1 | truth reasons |
| 5.7 | Reader health and drift **NEW** | L5-L6 | Partial | alarm before the read rate drops 0.1 % | background | 2 | P2 | plant server |
| 6 | Code Verify, "Grade code" | L0-L6 | Have (process control) / Missing (certified) | +-0.5 grade vs verifier | <= 5 ms per code inline | see rows | P2-P5 | 6.0 |
| 6.0 | Verification setup | L0-L1, L5 | Have (engine) / Partial (UI) | card within +-1 step | <= 2 min per setup | 1.5 | P2 | calibration card |
| 6.1 | 2D grade, ISO/IEC 15415 | L3-L4 | Have (QR, Micro QR, DM, Aztec, PDF417) | +-0.5 grade | ~1 ms per code | 1.5 | P2 | #150 |
| 6.2 | 1D grade, ISO/IEC 15416 | L3-L4 | Have (all 1D incl. DataBar stacked) | +-0.5 grade | <= 3 ms per code | 1 | P2 | #128 |
| 6.3 | DPM grade, ISO/IEC 29158 | L3-L4 | Have (DM, QR; synthetic tests) | +-0.5 grade | <= 3 ms per code | 2 | P4 | #135, real parts |
| 6.4 | GS1 data validation | L4 | Have (.NET) | 100 % of rule breaks caught | <= 0.2 ms per code | 1.5 | P2 | - |
| 6.5 | Grade report | L6 | Have (2D, 1D, DataBar rows) / Partial (DPM, batch) | report matches engine grades | <= 1 s | 2 | P2 | - |
| 6.6 | Production grading and trend | L4-L6 | Have (every Nth part) | grade drift seen before a C becomes D | background | 1 | P2 | plant server |
| 6.7 | Verifier conformance and correlation **NEW** | L5 | Missing | +-0.5 grade on 100 codes per type | offline | 3 + verifier | P5 | certified verifier, cards |
| 6.8 | Printer feedback **NEW** | L6 | Missing | alarm on print growth / contrast drift | background | 1.5 | P3 | 6.6 |

Total effort for everything above that is Missing or Partial: about **100 pw**, of which about
**27 pw is P1** (steps 1-9 of section 8, what moves today's batches to 99.9 %). Section 8 orders it.

## 3. The shared reading pipeline at a glance

Every symbology goes through the same pipeline; only the decoders and the checks differ.

```
L0 trigger -> exposure (learned) [-> bracket of 2-3 exposures]  -> 8/10/12-bit grey frame(s)
L1 per region: stretch | upscale 2x/4x | local contrast | flat | glare | deblur (measured PSF) |
              dots melted | polarity | AI enhance | (frames fused) | under-resolved model
L2 find: classical texture density + CodeNet + 1D band finder + slot map (unread slots)
L3 decode: zxing-cpp -> libdmtx -> own: QR grid fit, DM lattice, soft RS (erasures), known content,
           serial-run match, ERP print list, under-resolved solver, multi-frame, 1D generative fit,
           PDF417 rows, Micro QR, DotCode, Pharmacode
L4 decide: RS reserve symbols, plausibility among the case, check digits, length lock, GS1 rules,
           read-back, N-version rule, evidence budget -> OK / NG on doubt
L5 learn: step order per zone, sizes, encoding, PSF, polarity, exposure, tuning bandits
L6 out:   codes, corners, step, grades, misses with reasons and slots, PLC bits, NG images, audit
```

The scheduler (5.0.5) decides which step runs on which region next and stops at the expected
count or the time budget.

---

## 4. Tool 5 shared pipeline, sub-tool by sub-tool

### 5.0.0 Acquire and exposure search — "Camera"

- **Purpose**: a frame in which every code has usable contrast, no clipped modules and enough
  pixels per module. **Status: Partial.** Have: hardware trigger, strobe, exposure and gain
  (`dotnet/HikCamera/HikCamera.cs`), exposure and light-pulse search with a test burst
  (`dotnet/CodeReader/Calibration.cs`, `CalibrationSearch.cs`, `TestBurst.cs`), line advice
  (`LineAdvisor.cs`). Missing: bracket of exposures on every trigger, 10/12-bit pixel path into the
  engine, a brightness probe frame, lighting recipe per product.
- **Best in class**: Keyence SR-X tunes exposure, gain and focus at one button and uses an AI
  chip trained on over 100,000 images ([SR-X page](https://www.keyence.com/products/barcode/barcode-readers/sr-x/));
  Cognex captures four interleaved gains in one 4 ms sub-sampled frame before choosing exposure
  ([US9413981 family, US20140111656](https://patents.google.com/patent/US20140111656)) and
  retries bright field / dark field from the last good setting
  ([US7604174](https://patents.google.com/patent/US7604174B2/en), expired); Cognex stitches
  frames taken with different settings ([US9104932](https://patents.google.com/patent/US9104932B2/en)).

| Layer | Step | Method (source) | Options (name, values, default, auto-learned, level) | Output |
| --- | --- | --- | --- | --- |
| L0 | Trigger | Hardware edge from PLC, strobe pre-delay (subject to owner decision 1 in chapter 06; each frame must match the PLC's trigger ID, chapter 05) | Trigger delay us (0-10,000, 0, no, Advanced); strobe width (learned with exposure, yes, Advanced) | frame timestamp |
| L0 | Brightness probe **NEW** | Short binned ROI frame, black level and 5/50/99.5 percentiles give the start exposure; interleaved-gain form of US9413981 where the sensor allows (owner's statement of 2026-10-30, legal clearance) | Probe on (on, Advanced) | start exposure |
| L0 | Exposure search | Have: `CalibrationSearch.cs`; target mean light of read codes 70-86 % of full scale (ISO/IEC 29158 practice), saturated module area < 0.5 % | Target ML % (70-86, 78, yes, Advanced) | exposure, gain per product |
| L0 | Bracket per trigger **NEW** | 2-3 exposures (0.6x / 1x / 1.6x) on one trigger, sequencer mode; fused by 5.0.3 frames step with saturation masks (US9734376, US9104932) | Frames per trigger (1-3, 1, no, Advanced); only on stopped or strobed parts | 1-3 frames |
| L0 | Bit depth **NEW** | Mono10/12 transfer, engine takes 16-bit input and maps per region (R13: 4-16x grey levels on faint labels) | Pixel format (Mono8 / Mono10 / Mono12, Mono8, no, Advanced) | 16-bit frame |
| L0 | Lighting recipe **NEW** | Per product: diffuse / 45 deg for labels, cross-polarised for film, dark field for dark caps and DPM ([Cognex polariser guidance](https://www.cognex.com/en-hu/what-is/industrial-barcode-reading/barcode-reader-lighting): crossed at 90 deg, camera tilted 15 deg) | Light (bright / dark / polarised / dome, product master, set at teach, Essential) | light channel to PLC |
| L5 | Re-check | Daily auto-tune on a good part; drift of ML and saturation per zone | Interval (shift / day / week, day) | new exposure, alarm |

- **Corrections**: illumination drift (re-search when ML leaves 65-90 %), glare (bracket +
  polariser), dark print (longer exposure, Mono12), timing jitter (strobe locked to trigger, subject to owner decision 1 in chapter 06),
  temperature (LED output drift seen as ML drift). Automatic: probe, search, drift re-check.
- **Outputs**: exposure us, gain dB, ML %, saturated %, frames per trigger; PLC "light fault" bit.
- **Decide and safety**: a frame with > 2 % saturated code area or ML < 40 % on a part that must
  carry codes is not judged on reads alone: the case is NG with `explain.code.glare` /
  `low_contrast`; a missing frame or timeout is NG (requirements section 12).
- **Learn and tune**: exposure per product and camera kept with the learned state; the bandit
  (`TuningAgent.cs`) may move exposure only inside the brightness and saturation guards (R14).
- **Accuracy target**: on every batch, read codes have ML 70-86 % and saturation < 0.5 % after
  search. **Speed**: probe <= 10 ms; search <= 30 s at setup; bracket adds <= 15 ms per extra frame
  of budget on the line i7.
- **Hardware**: >= 2.5 px per module (prefer 3+) for printed codes: the MV-CS200 at the case
  width gives 2.5-3.1 px where the old 4096 x 2160 camera gave 1.9-2.3 (R0). Polariser sheet on
  light and lens for shrink wrap; low-angle dark-field ring for dark caps; overdriven strobe
  before a faster lens (R13). Buy list in [05-HARDWARE.md](05-HARDWARE.md).
- **Build plan**: re-shoot hard cases on the MV-CS200 (R0, 0 pw, owner hour) -> brightness probe
  (0.5) -> Mono12 path through C API (`cr_decode16`, 1) -> bracket per trigger + fusion wiring
  (2) -> lighting recipe field and PLC channel in product master (1). Reuses Calibration,
  TestBurst, multiframe.cpp.

### 5.0.1 Correct — "Clean image" (inside Read code)

- **Purpose**: turn each code region into one the decoders can sample: even light, sharp edges,
  right polarity, enough pixels per module. **Status: Partial**: 13 fixes built as `cr_step`s
  (`engine/src/decode.cpp`), learned order per 4 x 4 zone; missing the module-scaled forms and the
  measured-PSF deblur.
- **Best in class**: Cognex sizes every filter from the module size and turns doubt into
  Reed-Solomon erasures ([RESEARCH-COGNEX-PATENTS.md](../../RESEARCH-COGNEX-PATENTS.md),
  conclusion); HALCON exposes `contrast_tolerance`, `small_modules_robustness`, `module_gap`,
  `polarity`, `mirrored` ([get_data_code_2d_param](https://www.mvtec.com/doc/halcon/2511/en/get_data_code_2d_param.html));
  Dynamsoft tries deblur and binarisation modes in order until the expected count is reached
  ([DM benchmark](https://www.dynamsoft.com/codepool/data-matrix-reading-benchmark-and-comparison.html):
  83 % default, 100 % tuned on 112 codes, vendor-run).

Every correction the reader must make or tolerate:

| # | Correction | Status (file) | Method to adopt (source) | Options (default, auto, level) | Effort (pw) |
| ---: | --- | --- | --- | --- | ---: |
| C1 | Brightness stretch | Have (`CR_STEP_PLAIN`, 1st-99.5th percentile; 99.99 for HDR) | keep | - | 0 |
| C2 | Dark and flat field | Have in reader (`CR_STEP_FLAT`, divide by local paper level) and for grading (`calibration.cpp`); dark frame Missing | add dark frame subtraction when Mono12 lands ([Euresys FFC](https://documentation.euresys.com/Products/COAXLINK/COAXLINK_10_3/Content/03_Using_Coaxlink/functional-guide/flat-field-correction/what-is-ffc.htm)) | Flat field (auto / card / off, auto, Advanced) | 0.5 |
| C3 | **Module-scaled local contrast** | Missing (fixed 4 x 4-tile CLAHE, clip 3) | Cognex US10607047 / US11138397 (owner's statement of 2026-10-30, legal clearance): window = a few modules from the learned module px; local contrast = gap between means of bottom 20 % and top 20 %, clamped; non-bimodal areas masked; output mapped piecewise-linear to [8, 247]; on failure retry with the other kernel; reuse the last frame's parameters ([US10607047](https://patents.google.com/patent/US10607047B2/en)) | Window modules (2-8, 4, learned, Advanced) | 1.5 |
| C4 | CLAHE | Have (`CR_STEP_CLAHE`) | keep as cheap fallback; tile size from module px | - | 0.2 |
| C5 | Glare | Have (`CR_STEP_GLARE`, per-area stretch) | add saturation mask: clipped modules become erasures, not guesses (US9734376 idea, per module) | Glare mask (on) | 1 |
| C6 | **Deblur with measured PSF** | Partial: RL with Wiener start at 1.2x / 2x the zone's learned sigma (`psf.cpp`, R9 part 2); PSF not used for DM, lattice or rendering candidates | per-zone elliptical Gaussian or 9 x 9 kernel fitted on read codes (known pattern through the homography, print growth fitted first); Wiener + 3 RL iterations; motion PSF from belt speed x exposure for 1D (R9, NEXT-GENERATION section 5) | Deblur (auto / off, auto, Advanced) | 1 |
| C7 | Super-resolution, single frame | Partial: Lanczos 2x / 4x (`CR_STEP_UPSCALE`, `UPSCALE4`); AI enhancer (`ml/enhance.py`, `CR_STEP_AI_ENHANCE`) trained on synthetic pairs only | fine-tune the enhancer on real pairs from the data engine (R12); module-centre-weighted SR loss ([Tanaka GCCE 2024](https://www.ams.giti.waseda.ac.jp/data/pdf-files/2024GCCE_tanaka.pdf) [S]); per-patch uncertainty picks steps as [Mosaic-SR](https://github.com/Henvezz95/mosaic-sr) (method only, repo has no licence); run NN restore in parallel with the first decode and decode the restored image second (Keyence [US12039400](https://patents.google.com/patent/US12039400), [US11854163](https://patents.google.com/patent/US11854163); owner's statement of 2026-10-30, legal clearance) | Enhancer (Last / Early / Second pass / Off, Last, tuned, Advanced) | 2 |
| C8 | Super-resolution, several frames | Partial: per-module evidence fused over 2-5 frames (`multiframe.cpp`); no sub-pixel reconstruction | expired Scanbuy [US8150163](https://patents.google.com/patent/US8150163): keep low-noise frames, accumulate per-sample statistics on a 2x grid from the measured shifts, decode the accumulated image | Frames to fuse (1-5, 1, no, Advanced) | 3 |
| C9 | Under-resolved solver (1.3-2.6 px/module) | Have for QR (`underres.cpp`, build switch `CR_UNDERRES` on); Missing for DM | extend to Data Matrix and to 1D via US9607200 sampling coefficients ([US11151346](https://patents.google.com/patent/US11151346B2/en), [US9607200](https://patents.google.com/patent/US9607200B2/en)); owner's statement of 2026-10-30 (legal clearance): the switch stays on for India; export builds per the owner's confirmed scope | - | 2 |
| C10 | Polarity | Partial: learned per product, image flipped once (`decode.cpp` 784-799); no setting, no output | add `Polarity` (Auto / Dark on light / Light on dark) and output; DPM light-on-dark already graded | Polarity (Auto, learned, Advanced) | 0.3 |
| C11 | Mirror | Partial: zxing tries mirrored reads; own decoders assume not mirrored (`match.cpp` 263) | `Mirror` (Auto / Off) setting and output; own decoders try the transposed grid when Auto | Mirror (Off for print, Auto for film read from the back) | 0.5 |
| C12 | Perspective, skew, rotation | Have (homographies in zxing, `qrgrid.cpp`, `gridfit.cpp`, `underres.cpp`; any angle) | add station homography learned once from a good read (expired [US7878402](https://patents.google.com/patent/US7878402B2/en)) for tilted fixed cameras | Station tilt (auto at teach) | 0.5 |
| C13 | Lens distortion | Partial: model measured (`calibration.cpp`), not applied to reading | undistort only regions near the image corners (cheap remap per region) | Lens (from Calibrate, Advanced) | 0.5 |
| C14 | Cylinder / curved surface | Partial: reads to 138 deg wrap without unwrap (`ml/experiments/cylinder.py`, 18/18) | cylinder model in grid fit and lattice: column positions follow arcsin of the radius; flexible K x K grid (`gridfit.cpp` flexibleFit) for wrinkles | Surface (Flat / Curved + axis, Flat, learned at teach, Advanced) | 1.5 |
| C15 | Dot cells (inkjet, dot peen) | Have (`CR_STEP_DOTS` cell filters; grey closing in `grade_dpm.cpp`) | add the expired Cognex chain: 3 x 3 blur, Sobel / 4, gain 6 clip, Gaussian sigma = (M-1)/6, resample to 5-10 px/module on failure ([US7965899](https://patents.google.com/patent/US7965899B2/en)); module gap no / small / big as HALCON | Module gap (Auto / none / small / big, learned) | 1 |
| C16 | Print growth / shrink | Partial: measured (`quality.cpp` print growth); module sampling threshold not shifted by it | sample at the threshold shifted by measured growth; render candidates with grown modules (PSF step does this for QR match) | auto | 1 |
| C17 | Noise | Have (Gaussian before threshold; median of scan lines for 1D) | keep; Mono12 lowers quantisation noise | - | 0 |
| C18 | Damage, occlusion, missing finder | Have / Partial: soft RS with erasures, lattice, known content, serial match | out-of-image and covered modules as erasures in hypotheses over the learned size (5.3.4) | - | (5.3.4) |
| C19 | Background texture, print near the code | Have: quiet-zone not required by lattice; specks not counted (R10) | keep; locator trained with distractors (`ml/synth.py`) | - | 0 |
| C20 | Illumination drift, temperature | Partial: LineAdvisor; no drift alarm | 5.7 drift monitor | - | (5.7) |
| C21 | Vibration, motion blur | Partial: RL deblur | short exposure + strobe first (L0); 1D motion PSF; frames | - | (C6) |

- **Outputs**: each read carries the step that read it (`cr_code.step`); per-zone PSF, polarity
  and module size go into the learned state.
- **Decide and safety**: no correction can create a read: every fix feeds the same decoders and
  checks (RS with reserve symbols, plausibility, check digits). An enhancer or SR read is a
  low-evidence read and must pass 5.0.4 (read-back or a second decoder).
- **Learn and tune**: step order per zone learned (`learned.cpp`); window sizes from learned
  module px; the bandit picks enhancer placement.
- **Accuracy target**: each new fix must add reads on the bench at the line targets with 0 wrong,
  or it is dropped (as `qzmask.py` and `labelrect.py` were). **Speed**: <= 8 ms per region per fix
  on the line i7 (module-scaled contrast <= 3 ms on a 300 x 300 crop).
- **Build plan** (P1 first): C3 module-scaled contrast (1.5) -> C6 PSF everywhere (1) -> C5 glare
  mask (1) -> C16 growth-shifted sampling (1) -> C10/C11 settings and outputs (0.8) -> C8
  multi-frame SR (3, needs real bursts) -> C9 DM under-resolved (2) -> C14 cylinder (1.5) -> C15
  dot chain (1) -> C7 enhancer on real pairs (2, P3) -> C2/C12/C13 (1.5).

### 5.0.2 Locate codes — "Find codes"

- **Purpose**: propose every code region (and nothing else that costs time) before decoding.
  **Status: Have**: classical texture-density locator (`engine/src/locate.cpp`), CodeNet AI
  locator (`engine/src/ai_locator.cpp`, `ml/model.py`, input 1/8, output 1/16, about 0.2 GMAC per
  20 MP), 1D band finder (`linear.cpp`, gradient structure tensor), slot map regions (`slots.cpp`),
  count-driven "search harder" passes. Missing: recall measured and recorded; a 1D / Pharmacode /
  DotCode class in CodeNet; slot-predicted regions fed before the locator on learned products.
- **Best in class**: Cognex PowerGrid / QuickSort (AI isolates codes) [S]
  ([DataMan 380](https://www.cognex.com/en-au/dataman-380) [S]); expired Cognex tile-and-angle
  histogram locator: 32 x 32 tiles, checkerboard visit, Roberts edges, 64 angle bins, 2 peaks =
  1D, 4 = DM, 6 = MaxiCode ([US7181066](https://patents.google.com/patent/US7181066B1/en));
  public localisation benchmark [BarBeR](https://ditto.ing.unimore.it/barber) [S] (8,748 images).
  OpenCV WeChat's CNN reads 0 % of multi-code images in Dynamsoft's test
  ([QR benchmark](https://www.dynamsoft.com/codepool/qr-code-reading-benchmark-and-comparison.html)):
  not usable for cases.

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L2 | AI locator | CodeNet probability map, connected areas over threshold | Locator (Auto / Classical / AI / AI then classical, Auto, tuned, Advanced); AI threshold (0.05-0.95, 0.4, tuned, Advanced) | regions, strongest first |
| L2 | Classical | texture density in two directions relative to local brightness on a shrunk image | Locator scale (1-8, 4); density (0.05-0.4, 0.12) | regions |
| L2 | Tile histogram **NEW** | US7181066 as tie-breaker: classify 1D / 2D / dot code by angle peaks, skew from uneven peaks (expired) | on when several types enabled | type hint per region |
| L2 | 1D bands | structure-tensor coherent blobs at any angle | Scan lines (auto 1-64) | band, angle |
| L2 | Slot prior **NEW** | predicted slots of the learned case lattice go to strong passes first; locator skipped on learned 100 ms products (R8). Skipping must never hide an off-lattice code: a full-image count check still runs, or the case is NG whenever the count is not exact | Layout (Free / Grid, learned) | slot regions |
| L2 | Coarse-to-fine | decode half-resolution first, then full-resolution windows round AI boxes (expired [US9036929](https://patents.google.com/patent/US9036929B2/en)) | auto | windows |
| L4 | Stop | expected count reached (Dynamsoft LocalizationModes idea [S]); stopping early is allowed only after the locator's code-like regions have all been counted: more plausible code regions than expected, or a region left undecoded, makes the case NG (`explain.code.extra`), so an extra code is never hidden by stopping | Expected count (0-2000) | done flag |
| L5 | Retrain | `ml/autolabel.py` labels complete images, `ml/retrain.py` with old-vs-new gate on the golden set | manual promotion | new model |

- **Corrections tolerated**: scale change (learned size +-15 %), rotation (any), low contrast
  (locator input stretched), glare (trained with glare), distractor text and 1D bars, partial codes
  at the edge (flagged CUT_OFF).
- **Outputs**: `cr_region` list with `from_ai` and `read`, found callback for the live view.
- **Decide and safety**: a region proposed but unread is a miss with a reason (5.6), never
  ignored; a case short of its count is NG.
- **Accuracy target**: **100 % of visible codes proposed** on the bench (recall per batch,
  recorded); false regions < 2 per image after the first case. Measured with
  `ml/evaluate.py` against truth boxes; BarBeR as a public cross-check. **Speed**: <= 25 ms per
  20 MP image on the line i7 (CodeNet at 1/8 plus classical); <= 8 ms on T2 GPU.
- **Build plan**: record recall per batch in the bench (0.5) -> slot prior regions (1, with R8)
  -> CodeNet 1D / Pharmacode / DotCode classes and retrain (2) -> tile-histogram tie-breaker (1)
  -> coarse-to-fine windows for 96-code cases (0.5).

### 5.0.3 Decode engines — "Read"

**Purpose**: turn a region into text, with proof. **Status: Have / Partial** per engine below.
This is the core of the reader; each engine is a sub-tool with its own row.

| Engine | Status (file) | What it does | Best in class / source | Missing and plan | Effort (pw) |
| --- | --- | --- | --- | --- | ---: |
| zxing-cpp 3 | Have (`decode.cpp`, `zxformats.cpp`) | QR, Micro QR, rMQR (dropped), DM, Aztec, PDF417, MicroPDF417, all 1D, DataBar incl. Expanded Stacked, Telepen, DX, MaxiCode partial ([README](https://github.com/zxing-cpp/zxing-cpp)); options tryHarder / tryRotate / tryInvert / tryDownscale default on, binarizer LocalAverage ([ReaderOptions.h](https://github.com/zxing-cpp/zxing-cpp/blob/master/core/src/ReaderOptions.h)) | fastest open decoder (3.6 ms / DM image, 65 % DM in Dynamsoft's test) | `maxNumberOfSymbols` clamp per crop is done (2-8); enable rMQR, MaxiCode flags (5.3.3, 5.3.9) | 0.5 |
| libdmtx | Have, build switch (`CR_STEP_DMTX`) | DM on deblurred AI regions, plausibility gate after a phantom read | 72 % DM but 3.6 s / image (Dynamsoft) | keep as last resort; time-capped | 0 |
| QR grid fit | Have (`gridfit.cpp`, `qrgrid.cpp`, `CR_STEP_GRIDFIT`) | n x n grid on the 3 finders, module means, global + local thresholds, flexible K x K net | HALCON `module_grid` variable | cylinder model (C14); grid from 2 finders + timing when one finder is covered | 1 |
| QR soft decode | Have v1-6 (`qrsoft.cpp`) | EC level and mask by soft format match, least certain codewords as erasures, 2 check symbols in reserve | Cognex erasure ideas (RESEARCH-COGNEX-PATENTS) | **versions 7-40** (alignment grid, version info soft match, interleaved blocks) | 2 |
| DM soft / known | Have square 10 x 10 to 26 x 26 (`dmmatch.cpp` kSizes) | erasures with 4 symbols reserve, known codewords fixed | expired Symagery US6685095 (erasures) | **rectangular 8 x 18-16 x 48, DMRE 8 x 48-26 x 64, squares 32-144** (multi-region placement, interleaved blocks) | 2 |
| DM lattice (inside-out) | Have (`lattice.cpp`, `CR_STEP_LATTICE`) | Fourier pitch +-15 % of learned size, phase, n x n windows scored by L and clocks, RS rejects wrong windows | Cognex PowerGrid; [US10956697](https://patents.google.com/patent/US10956697B2/en) / [US11455482](https://patents.google.com/patent/US11455482B2/en) (owner's statement of 2026-10-30, legal clearance): masked inverse-FFT module-centre map, 5 x 5 affine seed grown 8 ways, polynomial fit, out-of-image modules unknown, hypotheses over size / offset / mirror / polarity ranked by preferred size | adopt the full disclosed method: centre map, grown seed, polynomial (wrinkled caps), out-of-image erasures; R5 try-loop gating | 2 (coordinate with #153) |
| Known content | Have (`knownDecodeQr`, `knownSoftDecodeDm`, `CR_STEP_KNOWN`) | codewords shared with the case's other codes fixed | own (no vendor advertises it) | feed from the ERP print list (below) | 0 |
| Serial-run match | Have (`match.cpp`, `dmmatch.cpp`, `CR_STEP_MATCH`, default on) | candidates from gaps in the case's serial run; printer encoding must reproduce; score >= 0.75, lead >= 0.15; PSF re-score | own | position-to-serial prior (R8): candidates per gap from about N to 1-2 | 1 |
| **ERP print list** | Missing (R8) | the serials printed per roll / batch loaded per job: exact candidate set from the first code; every read checked against it (a serial never printed is a wrong read). A print-list candidate is accepted only after 5.0.4 confirms it (read-back or an independent decoder); the list never supplies a read | Keyence SR-X links readings between stations ([SR-X](https://www.keyence.com/products/barcode/barcode-readers/sr-x/)) | C API `cr_set_print_list` (hash set, up to 10 M serials), .NET import from ERP / L3 system, matching as known content with zero reads | 2 |
| Under-resolved solver | Have QR (`underres.cpp`) | area-overlap model, LM corners, 3 x 3 pattern tests (<= 512), erasures | [US11151346](https://patents.google.com/patent/US11151346B2/en) (1.3-1.5 px/module where edge decoders fail; 0.8 px/module example) | DM and 1D (C9) | (C9) |
| Multi-frame fusion | Have (`multiframe.cpp`, `cr_decode_frames`, `CR_STEP_FRAMES`) | per-module evidence over 2-5 aligned frames, clipped modules take nothing, quality weights | [US9734376](https://patents.google.com/patent/US9734376B2/en), [EP3109792](https://patents.google.com/patent/EP3109792B1/en) | real bursts not measured; feed bracket frames (5.0.0); SR reconstruction (C8) | (C8) |
| 1D any angle + generative fit | Have (`linear.cpp`, `rules1d.cpp`, `CR_STEP_LINEAR`, `LINEAR_SSCC`) | band median profile to zxing; SSCC / EAN / ITF-14 digits fitted through measured blur, accepted only when both checks already hold | Cognex Hotbars ([US9122952](https://patents.google.com/patent/US9122952B2/en): 171 orientations per 90 deg, 1/64 px offset); Scandit blurred reference patterns ([US8596540](https://patents.google.com/patent/US8596540)) | **segment stitching** of torn bars across scan lines with US9361499 evidence (margin 1.5, width tolerance 10 %, [US9361499](https://patents.google.com/patent/US9361499B2)); generic Code 128 / GS1-128 generative fit | 2.5 |
| PDF417 rows | Have (`pdf417.cpp`) | row fusion, GF(929) RS, miscorrection cross-check | - | MicroPDF417 own path (5.3.6) | (5.3.6) |
| Micro QR grid fit | Have (`microqr.cpp`) | M2-M4 grid fit | - | M1 | 0.3 |
| DotCode | Have (`dotcode.cpp`) | dot lattice, affine fit, GF(113) RS with erasures, 2 spare | HALCON `module_gap` [O]; saddle points [US9946947](https://patents.google.com/patent/US9946947B2/en) | real images | (5.3.7) |
| Pharmacode | Have (`pharmacode.cpp`) | bar widths edge to like edge, bar-count lock, value lock | - | two-track (5.2.10) | (5.2.10) |
| **ModuleNet** | Missing (R16) | 15-20 k parameter grid-conditioned module-probability net on a 4 px/module canonical patch; logits into the soft decoders | Cognex DL decoding [US20260010746](https://patents.google.com/patent/US20260010746A1/en) (pending; ViT predicts codewords, RS corrects; owner's statement of 2026-10-30, legal clearance) | P3 after the data engine (R12); kill if module error is not 30 % below `moduleMeans` | 4 |
| Parallel fast + robust decoders | Partial (passes in sequence) | first success wins on two threads | [US7984854](https://patents.google.com/patent/US7984854) (17.6 / 82.4 % CPU, 85 / 850 ms) | part of the scheduler (5.0.5) | (5.0.5) |

- **Corrections**: as 5.0.1; decoders absorb residual damage by RS erasures.
- **Outputs**: text, symbology identifier, GS1 flag, corners, angle, grid rows / cols, EC used.
- **Decide and safety**: every own path keeps reserve RS symbols (QR 2, DM 4 in `dmmatch.cpp`,
  DotCode 2 + 1 per 16); `plausibleAmong` checks soft, lattice and whole-image reads against the
  case's other texts; PDF417 miscorrection = NG. What never passes: a read whose RS used all
  capacity on an own path; a 1D read breaking its length or check rule; a Pharmacode without its
  value lock.
- **Learn and tune**: printer encoding learned and confirmed on the first decoded code; module
  sizes, PSF per zone, step order per zone.
- **Accuracy and speed**: per symbology (sections 5.2-5.3). A clean code <= 15 ms in a 1 MP
  region (spec 5.1).
- **Build plan (P1)**: ERP print list (2) -> DM rectangular / DMRE / large squares in own decoders
  (2) -> lattice full method (2, after #153) -> 1D segment stitching (2.5) -> QR soft v7-40 (2) ->
  grid fit with a covered finder (1); P3: ModuleNet (4).

### 5.0.4 Confirm and decide — "OK / NG"

- **Purpose**: report a code only when its text is proven; turn every doubt into NG with a reason.
  **Status: Partial.** Have: RS reserve symbols, plausibility among the case's texts, check digits
  and length locks (`rules1d.cpp`), GS1 checks (`Gs1Check.cs`), Pharmacode value lock, EAN parent
  equals product GTIN, PDF417 cross-check, libdmtx gate. Missing: evidence budget per read,
  read-back against serial neighbours, N-version rule for low-evidence paths, fewest-symbols
  ranking across candidates, standing adversarial set, wrong-read counter in the bench.
- **Best in class**: Scanbot confirms a read over several frames before returning it
  ([how-to](https://scanbot.io/blog/how-to-read-damaged-or-blurry-barcodes/)); NI ranks
  demodulation candidates by least error correction used [S]
  ([IMAQ Read Data Matrix 2](https://documentation.help/NI-Vision-LabView/IMAQ_Read_Data_Matrix_Barcode_2.html) [S]);
  no vendor publishes a false-accept number.

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L4 | Evidence budget **NEW** | log10 P(false) = RS term (2e + f against checks left) x parse fraction x attempts per region; accept when <= -9 (R11, NEXT-GENERATION section 6); summed per case | Max false-accept (1e-9 proposed; owner decision 5 in chapter 06; Advanced, locked). The budget is an extra gate (AND): it never lowers the reserve symbols of 5.0.3 (QR 2, DM 4) | `evidence` per code |
| L4 | Read-back **NEW** | re-render the accepted text with the learned encoding, compare module LLRs with its 1-2-digit serial neighbours; margin in bits; low margin holds the case | Min margin bits (learned floor 6, Advanced) | margin |
| L4 | N-version rule **NEW** | a read from soft, lattice, match, known, frames, enhancer or under-resolved must be reproduced by an independent decoder, a second frame, or read-back | on (locked) | confirmed flag |
| L4 | Fewest-symbols ranking **NEW** | when several hypotheses decode, keep the one using fewest RS symbols, and only if the second needs >= 2 more | on | chosen text |
| L4 | Content rules | check digits, length lock, GS1 syntax and associations, expected pattern, print list membership, duplicate memory | per tool (Essential) | rule results |
| L4 | NG on doubt | short count, any low-margin code, any rule break, timeout, camera / PLC / model doubt | locked | NG + explain key |
| L5 | Adversarial set **NEW** | rendered and photographed 1-digit serial neighbours, other products' labels, foil, random textures, RS-corrupted true codes; run at every engine build | - | wrong-read count |

- **Accuracy target**: 0 wrong reads on bench + adversarial set (>= 10,000 negatives); Monte-Carlo
  1e7 grids per soft path shows false accept <= 1e-9 at the accepted erasure counts. **Speed**:
  <= 2 ms per low-evidence read (read-back), 0 for clean reads.
- **Build plan**: texts in truth and wrong-read counter (1, owner spot-check) -> adversarial set
  (1) -> Monte-Carlo per path (0.5) -> evidence budget (2) -> read-back (1.5) -> N-version and
  ranking (1).

### 5.0.5 Scheduler and time budget

- **Purpose**: spend the case's time where reads are won and stop on time. **Status: Partial.**
  Have: one persistent `WorkerPool` per reader (`engine/src/pool.hpp`, R2), expected-count stop,
  learned step order per zone, caps per region (`max(80, budget/12)` ms). Missing: one priority
  queue of (region, action) by P(win)/ms; fixed splits remain (first pass 45 %, search passes
  30 %, second pass 70 %; `decode.cpp` 862, 1210, 1855); no i7 profile.
- **Best in class**: Cognex orders conveyor candidates "not decoded and leaving first"
  ([US10325129](https://patents.google.com/patent/US10325129B2/en)); hands an overrunning decode
  to another core only when "probably decodable" ([US8794521](https://patents.google.com/patent/US8794521B2/en));
  learns setting order by 1000 points per read cycle and EMA alpha = 2/51
  ([US9600703](https://patents.google.com/patent/US9600703B2/en)). Owner's statement of 2026-10-30 (legal clearance) covers
  all three.

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L3 | Plain pass | PLAIN on every region first | - | easy reads |
| L3 | Queue **NEW** | priority = P(win | zone, blur, size bucket) / median ms of successful calls (R10); points + EMA credit (US9600703) | Time budget (10-5000 ms, product master, Essential) | next action |
| L3 | Overrun rule **NEW** | a region may exceed its cap only when its finder or lattice was found | auto | - |
| L4 | Stop | expected count reached, budget spent, or every region exhausted | - | timed_out flag |
| L5 | Probe cases | every 20th case tries steps in random order (R10) | on | unbiased stats |

- **Accuracy target**: no code lost to time on any batch at the targets (today: 48 QR max 501 ms,
  48 DM 761 ms). **Speed**: 12 codes 100 ms, 24: 250-300, 45/48: 500, 96: 600 ms max on the line
  i7, measured by #148's soak at 225 ppm for the two-camera line.
- **Hardware**: if the i7 profile (R1) shows CPU-bound passes at 96 codes, a 16+ core CPU (Core
  Ultra 9 / i9 class) gives the most per rupee; a GPU helps only CodeNet and the enhancer.
- **Build plan**: i7 profile with `CR_PROF=1` (0.5, owned by #148) -> replay queue order offline
  on the (d) cases (0.5) -> queue behind a flag (2.5) -> overrun rule (0.5).

### 5.0.6 Learn and tune (per product)

- **Status: Partial.** Have: learned state per product (`learned.cpp`, `cr_get_learned` /
  `cr_set_learned`, step tables, zone read map, sizes, encoding, PSF), tuning bandit
  (`TuningAgent.cs`), Calibrator, teach from a probe read (`CodeTool.cs` CodeTeach), whole-image
  auto-find (first version, PR #152, merged), auto-tune on good and bad images (first version,
  PR #154, merged). Missing: data engine (R12)
  feeding the enhancer and ModuleNet; written promotion gate in the UI.
- **Rules**: learned state never crosses products (measured: carried across products 1878 vs
  1945 fresh); no threshold or model moves without golden-set reads >= old, wrong = 0, p95 time
  <= old + 5 %, shadow run, named promotion (NEXT-GENERATION section 3).
- **Build plan**: data engine export of read crops with module bitmaps (1.5) -> Learning page
  showing step order, sizes, PSF, thresholds and drift (1.5, P2).

### 5.0.7 Output and audit

- **Status: Partial.** Have: `cr_code`, `cr_code_ext` (symbology id, GS1 flag, angle, check digit,
  quiet zone, add-on, grid), misses with reasons and slots, regions callback, NG image store,
  plant events. Missing: per-code evidence and margin, engine / model / learned-state hashes in each
  case record, low-confidence thumbnails, deterministic replay mode.
- **PLC**: OK, NG, count read, reason code of the first miss, "light fault", "time out" bits /
  registers (PLC-INTERFACE.md).
- **Build plan**: evidence fields in `cr_code_ext` (0.5) -> hashes and replay (1) -> thumbnails
  (0.5).

---

## 5. Tool 5 Code Reader: the user tools

### 5.1 Read code (`code.read`) — "Read code"

- **Purpose**: find and decode every code of the enabled types in the region, check its content
  and count, and say OK or NG. **Status: Partial**: the engine and the .NET `CodeTool`
  (`dotnet/VisionCore/Tools/CodeTool.cs`) do the work; the common tool contract (`vs_code.dll`,
  Vision Studio Avalonia screens) is ROADMAP step 2.
- **Best in class**: Cognex DataMan setup tool (read setups tried in priority order,
  [help article](https://support.cognex.com/en/help-articles/how-to-configure-a-dataman-reader-to-use-multiple-read-setups) [S]);
  Keyence SR-X one-button tuning ([SR-X](https://www.keyence.com/products/barcode/barcode-readers/sr-x/)).
  Screens (described in words; see the vendor pages): a live image on the left with the read
  codes boxed green and their text, a settings column on the right with symbology tick boxes,
  "tune" and "train" buttons, a read-rate counter and a results history strip below.
- **Layers**: L2-L6 of 5.0. **Options**: those of TOOL-SPECIFICATIONS 5.1 (code types, expected
  count, expected text, time budget, locator, enhancer, scale, upscale, density, AI threshold,
  whole-image pass, gamma, grading, threads, size hints) plus the new ones from this chapter:
  Polarity, Mirror, Surface, Module gap, Frames per trigger, Pixel format, Print list (file / ERP
  link), Max false-accept (locked), Layout.
- **Outputs**: `Codes`, `Count`, `FirstText`, `Misses`, `Gs1`, plus `Evidence`, `Polarity`,
  `Mirrored`, `Modules`, `ModulePx`, `EccLevel` (5.3 outputs not yet exposed: Partial).
- **Decide and safety**: count == expected; more codes than expected is NG
  (`explain.code.extra`); every text matches its pattern and print list, every grade >= minimum
  when grading, every low-evidence read confirmed; anything else NG with its explain key. Today the
  shipped tool fails only a short count (`codes.Count < need`, `CodeTool.cs` `Judge`); the
  over-count rule and its explain key are still to build (in the build plan below).
- **Learn and tune**: teach from a box or whole-image auto-find (#152); auto-tune on saved good
  and bad images (#154); bandit while running.
- **Accuracy / speed**: as section 1. **Build plan**: over-count rule, more codes than expected is
  NG with `explain.code.extra` (0.2, P1, safety first); expose the new options and outputs through
  the contract (2) and in the Avalonia Read code page (1, with ROADMAP step 2).

### 5.2 1D codes, common rules (`code.read`, linear types)

- **Status: Have on synthetic frames, unmeasured on real ones.** Band finder at any angle, 24
  scan lines fused by the median, zxing decode, generative SSCC / EAN / ITF-14 fit, length lock,
  check digits (`rules1d.cpp`), quiet-zone check, add-ons, angle, rejected-by-rule counts
  (`cr_stats_ext`). Synthetic: 10 types x 10 conditions x 5 = 500/500; 0 reads from 40 empty
  images; SSCC at 25-45 deg tilt 0 -> 20/20, defocus 2.4 px 0 -> 18/20, modules 1.2 px 0 -> 13/20
  (NEXT-GENERATION section 5).
- **Best in class**: Cognex 1DMax with Hotbars II [S]
  ([technologies page](https://www.cognex.com/en-gb/products/barcode-readers/fixed-mount-barcode-readers/dataman-150-260-series/technologies) [S]);
  Hotbars method ([US9122952](https://patents.google.com/patent/US9122952B2/en)); partial-scan
  evidence integration ([US9361499](https://patents.google.com/patent/US9361499B2)); blur-robust
  classification ([Datalogic US9361503](https://patents.google.com/patent/US9361503)); learned
  per-column decoder ([US11334731](https://patents.google.com/patent/US11334731)); torn-code
  stitching ([Scanbot](https://scanbot.io/blog/how-to-read-damaged-or-blurry-barcodes/)).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Capture | strobe from PLC; bars along the motion so blur falls along the bars (NEXT-GENERATION section 5) | exposure (learned) | frame |
| L1 | Motion and focus blur | 1D Wiener bank from belt speed x exposure; measured PSF (C6) | auto | profile |
| L2 | Band find | structure tensor, any angle | Scan lines (auto, 1-64) | band |
| L3 | Profile | median of N scan lines along the band, sub-pixel edges | - | edges |
| L3 | Decode | zxing per type; generative fit for SSCC / EAN / ITF-14; **segment stitching NEW** with US9361499 evidence (accept top interpretation only when it beats the second by 1.5, widths within 10 %) | - | text |
| L3 | Under-resolved 1D **NEW** | sampling coefficients from samples-per-module and phase, test the valid character patterns (103 for Code 128) ([US9607200](https://patents.google.com/patent/US9607200B2/en)) | auto below 1.5 px/module | text |
| L4 | Rules | length lock, check digit verify / strip, quiet zone, add-on, GS1, expected text, product GTIN | per tool | OK / NG |

- **Corrections**: tilt (any angle), blur (Wiener, generative fit), low contrast (median profile),
  torn / scratched bars (stitching), small X (under-resolved), print growth (edge-to-like-edge
  widths), curvature on bottles (scan lines along the bars only; **cylinder** C14), quiet-zone
  intrusion (flagged).
- **Decide and safety**: a fused or stitched read must pass the type's check digit; types
  without one (ITF, Code 39, Codabar, Pharmacode) need a length or value lock or the tool is a
  setup error; a partial read is always rejected by the lock.
- **Accuracy target**: >= 99.9 % on >= 1,000 real labels per type, 0 wrong, 0 partial reads.
  **Speed**: <= 15 ms per label on the line i7 (synthetic median 3.7 ms, max 14.5 ms).
- **Hardware**: X >= 2 px (1.5 px with the under-resolved step); 2 MP side camera with strobe;
  bars along the conveyor.
- **Build plan**: owner saves 30-50 side-camera frames with known texts, `truth/samples-1d.csv`
  (0.5) -> baseline on real frames (0.5) -> segment stitching (1.5) -> under-resolved 1D (2, P4 if
  the baseline meets target).

Per-symbology cards follow. Each card names only what differs from 5.2.

#### 5.2.1 GS1-128, SSCC, Code 128

| Item | Content |
| --- | --- |
| Purpose / status | Case and pallet labels (SSCC = AI 00, 18 digits); **Have**: zxing, `CR_STEP_LINEAR`, generative SSCC fit `CR_STEP_LINEAR_SSCC` confirmed by mod-103 and GS1 mod-10, FNC1 -> `]C1` |
| Best in class | Cognex 1DMax / Hotbars; the generative fit is our own |
| Layers | L1 Wiener / PSF; L2 band; L3 zxing then generative fit (extend to any GS1-128 layout: AI 01 + 10 + 17 + 21 as fixed-length fields, variable fields need FNC1 matched filter); L4 mod 103, GS1 check digits, GS1 syntax (6.4), SSCC must be in the print list (5.0.3) |
| Corrections | blur, tilt, small X, print growth, torn bars (stitching) |
| Outputs | text, AIs, check digit OK, quiet zone OK, angle, grade (6.2) |
| Decide / safety | both checks hold before a fitted read stands; SSCC not in the list = NG |
| Learn | label layout (AI order) learned at teach |
| Target | 99.95 %, 0 wrong; <= 10 ms per label |
| Build | generic GS1-128 generative fit (1.5 pw, P1) |

#### 5.2.2 EAN-13, EAN-8, UPC-A, UPC-E, 2- and 5-digit add-ons

| Item | Content |
| --- | --- |
| Purpose / status | Retail packs; **Have**: zxing + fusion, add-ons reported apart (`cr_code_ext.addon`, PR #99), quiet zones per type (11/7 X, 7/7, 9/9, 9/7), UPC-A as EAN-13 in an EAN job, parent EAN must equal product GTIN (Station) |
| Best in class | every retail scanner; ISO/IEC 15420 reference decode (standard not opened; its rules are implemented in `decodability.cpp`, PR #82) |
| Layers | as 5.2; generative fit for EAN-13 built; L4 GTIN equals product master |
| Corrections | as 5.2; add-on mis-segmentation cannot change the main text (main decoded and checked first) |
| Decide / safety | add-on required / expected (PR #99); EAN misreads by zxing caught by the GTIN rule |
| Target | 99.95 %; <= 10 ms |
| Build | none beyond real-frame measurement (0.5 pw) |

#### 5.2.3 ITF-14, Interleaved 2 of 5

| Item | Content |
| --- | --- |
| Purpose / status | Shipper cartons, often on corrugated board with bearer bars; **Have**: zxing, generative ITF-14 fit, GS1 mod-10 verify, length lock required (a partial ITF is a valid shorter code: 5 wrong of 50 without a rule, TEST-REPORT) |
| Layers | L1 bearer-bar suppression **NEW** (mask the frame / bars found as long horizontal edges so they do not join the first and last bars); L3 zxing + fit; L4 length 14 + check digit locked by teach |
| Corrections | print growth on corrugated (wide-to-narrow ratio 2.25-3 measured per scan), low contrast on brown board, bearer bars |
| Target | 99.9 %, 0 partial reads; <= 10 ms |
| Build | bearer-bar mask (0.5 pw) and real corrugated frames (0.5) |

#### 5.2.4 Code 39 (and Code 32 / PZN, **NEW**)

| Item | Content |
| --- | --- |
| Purpose / status | Industrial and some pharma; **Have** standard and full ASCII, mod-43 verify / strip. **Missing**: Code 32 (Italian pharmacode, base-32 of a 9-digit number) and PZN (German, mod 11) conversions; the text stays as printed today |
| Layers | L3 zxing; L4 length lock or mod 43 required; Code 32 / PZN as an output transform with its own check |
| Target | 99.9 %; <= 10 ms |
| Build | Code 32 / PZN transforms with checks (0.5 pw, P4; only if an export customer needs it) |

#### 5.2.5 Code 93 and 5.2.6 Codabar

| Item | Content |
| --- | --- |
| Status | **Have**: Code 93 two check characters always verified; Codabar mod 16 over the whole text including start / stop, strip removes the character before stop (PR #79) |
| Decide / safety | Codabar without verify needs a length lock (setup check) |
| Target | 99.9 %; <= 10 ms. No work planned |

#### 5.2.7 GS1 DataBar family

| Item | Content |
| --- | --- |
| Purpose / status | Fresh food, coupons, small pharma packs. Variants: Omnidirectional, Truncated, Stacked, Stacked Omnidirectional, Limited, Expanded, Expanded Stacked ([variants](https://en.wikipedia.org/wiki/GS1_DataBar)). **Have**: zxing reads all; graded incl. stacked (PR #95); `]e0` identifier. **Partial**: no own robust path (fusion and generative fit are Code 128 / EAN / ITF only) |
| Layers | L2 band finder (stacked rows found between separator patterns as in `grade1d.cpp`); L3 zxing, **own finder-pair decode NEW** (ISO/IEC 24724 character widths by edge-to-similar-edge, already coded for grading in `decodability.cpp`) as a fallback; L4 check digit (mod 89 / mod 211 per variant), GTIN, GS1 AIs |
| Corrections | stacked row misalignment (rows decoded jointly, as grading does), curvature on small round packs |
| Target | 99.9 %; <= 15 ms |
| Build | own fallback decoder reusing the grading code (1.5 pw, P1 if a line prints DataBar, else P4) |

#### 5.2.8 GS1 Composite, CC-A / CC-B / CC-C (**NEW**)

| Item | Content |
| --- | --- |
| Purpose / status | A linear part (EAN/UPC, GS1-128, DataBar) with a stacked 2D part above it: CC-A (MicroPDF417 variant, up to 56 digits), CC-B (MicroPDF417, 338 digits), CC-C (PDF417, 2,361 digits, with GS1-128 only) ([symbology.dev composite](https://symbology.dev/reference/composite); ISO/IEC 24723 [S] [preview](https://webstore.ansi.org/preview-pages/ISO/preview_ISO+IEC+24723-2010.pdf)). Readers join both into one GS1 string once both halves are read ([Scandit composite docs](https://docs.scandit.com/composite-codes.html) [S]). **Missing**: zxing-cpp's README does not list composite; no own decoder |
| Layers | L2 find the linear part, then search directly above it for the 2D part (row height and width tied to the linear symbol); L3 linear decode with its linkage flag; CC-A / CC-B through a MicroPDF417 decoder with the CC-A row-address patterns; CC-C through `pdf417.cpp`; L4 both parts present when the linkage flag says so, joined AI string passes 6.4 |
| Decide / safety | a linear part with the linkage flag set but no 2D part read = NG (never report the linear part alone as the whole code) |
| Target | 99.9 % of pairs; <= 25 ms |
| Build | CC-A / CC-B decoder on the MicroPDF417 path (2), linkage and joining (0.5), grading as PDF417 rows (0.5); P4 |

#### 5.2.9 Pharmacode, one-track (Laetus)

| Item | Content |
| --- | --- |
| Purpose / status | Carton and leaflet identity on pharma packing lines (2-16 bars, values 3-131070; [Pharmacode](https://en.wikipedia.org/wiki/Pharmacode)). **Have** (`pharmacode.cpp`, PR #84): widths edge to like edge, two clean classes, 4-gap quiet zones, bar-count lock and value lock required, standard / reverse direction; bars lying flat in **#127**; grading in **#117** |
| Layers | L2 1D band finder; L3 width classes; L4 value equals the expected value (no check digit) |
| Corrections | ink spread (like-edge widths), direction (image orientation), bars upright within 75 deg (flat bars by #127) |
| Decide / safety | value lock is the only content proof: a tool without it is a setup error |
| Target | 99.9 %, 0 wrong values; <= 10 ms |
| Build | none beyond #117 / #127 and real frames (0.5) |

#### 5.2.10 Pharmacode two-track (**NEW**)

| Item | Content |
| --- | --- |
| Purpose / status | Laetus two-track: each bar position is full height, top half or bottom half, which gives a base-3 value (general knowledge, not verified: no specification page found; Wikipedia covers one-track only). **Missing** |
| Layers | L2 band finder; L3 per bar, the presence of ink in the top and bottom half of the band (two profiles); value by the Laetus table (obtain the specification from Laetus / customer); L4 bar-count and value lock |
| Target | 99.9 %, 0 wrong; <= 10 ms |
| Build | 1.5 pw once a specification and samples exist (P4) |

#### 5.2.11 Other 1D: MSI Plessey, Telepen, Code 11, DX film edge (**NEW**)

zxing-cpp reads Telepen and DX film edge ([README](https://github.com/zxing-cpp/zxing-cpp)); MSI
and Code 11 need small own decoders on the 1D profile (two-width classes, mod 10 / mod 11 checks).
None is on the owner's lines: **Missing**, 1 pw together, P4, only on customer demand.

### 5.3 2D codes, common rules (`code.read`, matrix and stacked types)

- **Status: Have** for QR, Micro QR, DM, Aztec, PDF417, DotCode. Missing outputs `Modules`,
  `ModulePx`, `EccLevel`, `Mirrored`, `Polarity` in the tool (engine has `grid_rows/cols` and
  `module_size_px`), and settings Polarity, Mirror, Module grid, Code size limits.
- **Best in class**: HALCON's 2D parameter set (polarity, contrast_tolerance,
  small_modules_robustness, module_gap, mirrored, finder_pattern_tolerance, candidate_selection,
  module_grid; [get_data_code_2d_param](https://www.mvtec.com/doc/halcon/2511/en/get_data_code_2d_param.html))
  with a training step that narrows ranges from samples; our learned state is the equivalent.
- **Build plan**: settings and outputs (1 pw, P2).

#### 5.3.1 QR Code (Model 2)

| Item | Content |
| --- | --- |
| Purpose / status | The owner's main child code (12/24/45/48/96 per case, shrink-wrapped excise labels). **Have**: zxing, grid fit with flexible net, soft decode v1-6, known content, serial-run match with PSF re-score, under-resolved solver, multi-frame, enhancer; graded (15415, 29158) |
| Best in class | Cognex 2DMax+ [S]; HALCON with `contrast_tolerance`; Dynamsoft 83 % on 1,232 public QR vs ZXing 32 % (vendor-run, [benchmark](https://www.dynamsoft.com/codepool/qr-code-reading-benchmark-and-comparison.html)) |
| Layers | L0 >= 2.5 px/module, polariser on film; L1 C3 module-scaled contrast, C6 PSF deblur, C8 frames; L2 CodeNet + slot prior; L3 zxing -> grid fit -> soft (v1-40) -> known / print list -> match -> under-resolved -> frames -> enhancer; L4 RS reserve 2, plausibility, read-back, GS1 / URL rules |
| Corrections | blur (24 QR: 18 misses; 48 QR: 15), low contrast under film, covered finder (grid from two finders + timing **NEW**), wrinkles (flexible grid), cylinder (C14), mirror (film read from behind: C11) |
| Outputs | text, version, EC level, mask, corners, grades |
| Decide / safety | a QR read by match / known / soft needs read-back or a second decoder (5.0.4) |
| Learn | printer encoding, version, module px, PSF per zone, polarity |
| Target | 24 QR 96.9 -> >= 99.9 %; 48 QR 98.4 -> >= 99.9 %; shrink-wrap 60.3 -> >= 99.9 % after re-shoot; 0 wrong; times per section 1 |
| Hardware | MV-CS200 with the case filling the frame (2.5-3.1 px/module on the excise label); cross-polarised light for film (R13) |
| Build | re-shoot (owner) -> C3 (1.5) -> C6 (1) -> covered-finder grid (1) -> print list (2) -> soft v7-40 (2) -> scheduler (4) |

#### 5.3.2 Micro QR (M1-M4)

| Item | Content |
| --- | --- |
| Status | **Have**: zxing + own grid fit M2-M4 (`microqr.cpp`, PR pdf417-microqr-robust: zxing 0/10 at 1.8 px blur, engine reads them); graded (M1 check codewords detect only) |
| Layers | L0 locked focus (Scanbot advice); L3 add M1 to the grid fit; L4 M1 has 2 check codewords that only detect: an M1 read needs a length / pattern lock |
| Target | 99.9 %; <= 15 ms. Build: M1 grid fit (0.3 pw, P4) |

#### 5.3.3 rMQR, Rectangular Micro QR (**NEW**; not in TOOL-SPECIFICATIONS)

| Item | Content |
| --- | --- |
| Purpose / status | ISO/IEC 23941 (2022), 32 versions, 7-17 modules high, 27-139 wide, EC M or H, QR-style finder top-left and a sub-finder bottom-right ([rMQR](https://en.wikipedia.org/wiki/Rectangular_Micro_QR_Code)). Fits narrow pharma strips and ampoule labels. **Missing**: zxing-cpp reads it ([README](https://github.com/zxing-cpp/zxing-cpp)) but `zxformats.cpp` line 42 drops it: no `cr_format` flag |
| Layers | L2 CodeNet trained with rMQR shapes (long thin regions); L3 zxing; own grid fit from main finder + sub-finder + timing (as `gridfit.cpp`); soft decode with erasures; L4 RS reserve 2 |
| Target | 99.9 %; <= 15 ms |
| Build | `CR_RMQR` flag and output (0.5), CodeNet samples (0.5), grid fit + soft (1); P4. Grading: 15415 on the grid (in 6.1 build) |

#### 5.3.4 Data Matrix ECC 200: square, rectangular, DMRE

| Item | Content |
| --- | --- |
| Purpose / status | Child codes on caps and cartons (48-can batch), GS1 DataMatrix for pharma. Sizes: square 10 x 10-144 x 144, rectangular 8 x 18-16 x 48 ([Data Matrix](https://en.wikipedia.org/wiki/Data_Matrix), ISO/IEC 16022:2024), DMRE 8 x 48-26 x 64 (ISO/IEC 21471; size list on [bwipp DMRE](https://github.com/bwipp/postscriptbarcode/wiki/Data-Matrix-Rectangular-Extension); [ISO 21471:2025](https://www.iso.org/standard/84783.html) [S]). **Have**: zxing (all sizes), libdmtx, lattice, soft / known / match for **square 10-26 only** (`dmmatch.cpp` kSizes). **Partial**: rectangular and DMRE only through zxing (DMRE support in zxing-cpp not stated on its README: verify) |
| Best in class | Cognex PowerGrid / 2DMax+ "critical elements missing" [S]; [US10956697](https://patents.google.com/patent/US10956697B2/en) hypotheses with out-of-image erasures; HALCON `finder_pattern_tolerance` high; Dynamsoft DPM templates 83 -> 100 % (vendor) |
| Layers | L0 dark-field ring for dark caps (48 DM LOW_CONTRAST 39); L1 C3, C6, C15 dots; L2 CodeNet + slot prior; L3 zxing -> lattice (full US10956697 method) -> L-hypotheses over learned size with covered modules as erasures -> soft / known / match (all sizes) -> libdmtx; L4 RS reserve 4, plausibility, read-back |
| Corrections | missing / covered L (NO_FINDER 16, COVERED 26), low contrast on dark caps, print bleed into border, curved caps (polynomial grid), dot print |
| Decide / safety | 4 reserve symbols on own paths; libdmtx read must look like the case's others |
| Target | 48 DM 64.2 % -> >= 99.9 % within 500 ms; 24 DM stays 100 %; 0 wrong |
| Hardware | low-angle dark-field ring light (or dome) on the cap station; 3+ px/module |
| Build | **#153 owns the 48 DM batch**: this chapter does not redesign it; after #153: own decoders for rectangular / DMRE / large squares (2), full lattice method (2), DM under-resolved (2, C9) |

#### 5.3.5 Aztec (compact, full-range, Rune)

| Item | Content |
| --- | --- |
| Status | **Partial**: zxing only (Aztec and Rune per [README](https://github.com/zxing-cpp/zxing-cpp)); graded 15415 on its grid |
| Layers | L3 **own path NEW**: bullseye fit, mode message soft-decoded (GF(16) RS), layer grid with reference grid lines every 16 modules, soft decode with erasures (GF(64-4096) by size) |
| Decide / safety | RS reserve 2 on the own path |
| Target | 99.9 %; <= 15 ms. Build 2 pw, P4 (no owner line uses Aztec) |

#### 5.3.6 PDF417, compact PDF417, MicroPDF417

| Item | Content |
| --- | --- |
| Status | **Have** PDF417: zxing + own row fusion, GF(929) RS, miscorrection cross-check (`pdf417.cpp`), graded by rows (`grade_pdf417.cpp`), scratches 1-3: 14->19, 5->18, 5->15 of 20 (synthetic). **Partial** MicroPDF417: zxing only |
| Layers | L2 band finder (start / stop patterns); L3 own MicroPDF417 row fusion **NEW** (row address patterns instead of indicators, same GF(929)), shared with CC-A / CC-B (5.2.8); L4 RS reserve, cross-check |
| Target | 99.9 %; <= 20 ms. Build 1.5 pw (P4, with composite) |

#### 5.3.7 DotCode

| Item | Content |
| --- | --- |
| Purpose / status | Tobacco (EU 2018/574) and some pharma GS1 data; AIM ISS DotCode 4.0 (2019), RS over GF(113), width + height odd ([DotCode](https://en.wikipedia.org/wiki/DotCode)). **Have** (`dotcode.cpp`, PR #84): dot clusters, affine lattice, GF(113) RS with erasures and 2 spare; 14 engine checks on spec figures; graded in **#117** |
| Best in class | HALCON reads DotCode with `module_gap` [O]; commercial TachyonIQ SDK [S]; saddle-point grid [US9946947](https://patents.google.com/patent/US9946947B2/en) |
| Layers | L0 CIJ / laser dots on moving packs: strobe; L1 dot detection by LoG / Determinant of Hessian at learned dot size; L3 lattice fit with saddle points as a second grid source; L4 2 spare words |
| Target | 99.9 % on real prints; <= 20 ms |
| Build | real images (owner or customer) then tune (2 pw, P4) |

#### 5.3.8 Han Xin (**NEW**)

| Item | Content |
| --- | --- |
| Purpose / status | ISO/IEC 20830:2021, 23 x 23-189 x 189, four 7 x 7 corner finders, RS levels L1-L4 (8-30 %) ([Han Xin](https://en.wikipedia.org/wiki/Han_Xin_code)); used in China. **Missing** (zxing-cpp does not list it) |
| Layers | L2 four-finder detection (1:1:1:1:3 ratios); L3 grid, data / RS per ISO 20830 (GB 18030 text); L4 RS reserve 2 |
| Target | 99.9 %; <= 20 ms. Build 4 pw (P4, only for a China export customer; zint has an encoder for test images) |

#### 5.3.9 MaxiCode (**NEW**)

| Item | Content |
| --- | --- |
| Purpose / status | ISO/IEC 16023, fixed 1-inch square, hexagonal modules, central bullseye, modes 2-6 ([MaxiCode](https://en.wikipedia.org/wiki/MaxiCode)); UPS parcels. **Missing**: zxing-cpp reads it partially ([README](https://github.com/zxing-cpp/zxing-cpp)); engine has no flag |
| Layers | L2 tile histogram 6 peaks at 60 deg (US7181066); L3 zxing (pure image) after own bullseye + hex grid rectification **NEW**; L4 RS |
| Target | 99.5 %; <= 20 ms. Build 1 pw (P4, logistics only) |

### 5.4 DPM — "Read code" in DPM mode (dot peen, laser etch, inkjet dots on metal, plastic, glass)

- **Purpose**: read Data Matrix and QR marked on the part itself. **Status: Partial.** Have: dot
  cell filters (`CR_STEP_DOTS`), glare correction, lattice for damaged L, CLAHE and deblur,
  light-on-dark polarity, DPM grading (6.3); dpm_tests.cpp 30 checks on synthetic marks. Missing:
  user settings Mark type / Dot size / Surface, mark-type auto-learn, lighting sequence per part,
  any real DPM part measured.
- **Best in class**: Cognex lighting recipes in [US11003875](https://patents.google.com/patent/US11003875B2/en)
  (laser etch on a polished cylinder: diffuse only; dot peen on rough: diffuse plus low-angle at
  about 20 deg; dot peen on polished: low-angle plus cross-polarised); bright / dark field retry
  from the last good setting ([US7604174](https://patents.google.com/patent/US7604174B2/en),
  expired); expired dot-peen filter chain ([US7965899](https://patents.google.com/patent/US7965899B2/en));
  Omron MicroHAWK "X-Mode" for curved and shiny parts [S]
  ([announcement](https://www.automate.org/vision/news/omron-microscan-debuts-duo-of-best-in-class-barcode-readers/boa) [S]);
  Hand Held Products DPM decoding ([US10650205](https://patents.google.com/patent/US10650205),
  owner's statement of 2026-10-30, legal clearance); DL enhance-then-decode on SLS polymer marks (Matuszczyk and Weichert,
  [Sensors 2023](https://www.ncbi.nlm.nih.gov/pmc/articles/PMC9921453/) [S]).

| Layer | Step | Method | Options (default, auto, level) | Output |
| --- | --- | --- | --- | --- |
| L0 | Light sequence **NEW** | per mark type: 90 diffuse / 30Q / 30S / dark field / dome; on a miss, the next light from the last good one (US7604174), driven by PLC outputs | Lights (list, learned order, Advanced); Retries (0-3, 1) | frame per light |
| L0 | Exposure | ML 70-86 % on the mark (ISO/IEC 29158) | auto | - |
| L1 | Mark-type filters | dot peen: Cognex chain (C15) or grey closing sized from the widest gap along a solid row (as `grade_dpm.cpp`); laser on metal: glare mask + module-scaled contrast; inkjet dots on glass / plastic: dot melting (`CR_STEP_DOTS`) | Mark type (Printed / Dot peen / Laser on metal / Laser on plastic / Inkjet dots, learned, Advanced); Dot size px (learned) | clean crop |
| L1 | Surface | cylinder model (C14); texture suppression by band-pass at the module frequency | Surface (Flat / Curved + axis, learned at teach) | - |
| L2 | Locate | lattice texture (Fourier pitch) more than finder; CodeNet with DPM samples | - | region |
| L3 | Decode | zxing -> lattice -> soft / known; light-on-dark tried | Polarity (Auto) | text |
| L4 | Decide | RS reserve 4, GS1 / UDI rules; graded C or better when grading on | Minimum grade (C, Essential when grading) | OK / NG |
| L5 | Auto-learn | mark type from the first read: dot spacing vs dot size, contrast polarity, gap width (spec 5.4) | - | settings |

- **Corrections**: specular glare (light choice, polariser, saturation mask), machining texture,
  dot gaps, uneven dot size, curvature, low contrast laser anneal marks, oxide or oil film.
- **Outputs**: text, mark type, light used, DPM grade at 50 / 80 % aperture, explain keys
  `explain.dpm.glare`, `explain.dpm.low_contrast`.
- **Decide and safety**: a mark read only after a light change is still a normal read (proof is
  RS), but its grade is reported with the light used; ungradable when grading is on = NG.
- **Accuracy target**: 99.9 % on marks grading C (29158) or better on a 500-part set per mark
  type; 0 wrong. **Speed**: <= 60 ms per mark including one light retry.
- **Hardware**: dome or low-angle ring light with four segments, PLC-switched; 4-6 px per cell
  for dot peen; telecentric lens only for gauging, not needed for reading.
- **Build plan** (P4): real parts (owner / customer) -> settings and auto-learn (2) -> light
  sequence with PLC (2) -> Cognex dot chain (1, C15) -> DL enhancer trained on DPM pairs (1).

### 5.5 Aggregation / case mode — "Read case"

- **Purpose**: read every code in a case, tray or crate; name each missing one by position; link
  children to the parent; refuse duplicates. **Status: Have / Partial.** Have: expected count
  drives effort and stop (`decode.cpp`); slot map from read code centres, rigid cases 0.04-0.1
  pitch off, shrink-wrap rejected (`slots.cpp`, `cr_miss_info.slot_row/col`); duplicate memory
  10,000 child / 1,000 case with orange flag (`DuplicateMemory.cs`); product master 12 / 24 / 45 /
  48 / 96 (`ProductMaster.cs`); parent / child cameras; serial-run and known-content decoding.
  Missing: ERP print list (R8), position-to-serial prior, parent-child rescue of a damaged child,
  whole-history duplicates in a database index (R6), 12- and 96-code images.
- **Best in class**: Keyence SR-X links code information between readers across stations to
  rescue damaged codes ([SR-X](https://www.keyence.com/products/barcode/barcode-readers/sr-x/));
  Honeywell multi-code display and transmit ([US9208367](https://patents.google.com/patent/US9208367),
  owner's statement of 2026-10-30, legal clearance); Dynamsoft expected-count stop [S]. No vendor advertises serial-run or
  known-content reading: these are our own.

| Layer | Step | Method | Options (default, auto, level) | Output |
| --- | --- | --- | --- | --- |
| L0 | Frames | one frame per case; bracket or burst when stopped | Frames (1-3, 1) | - |
| L2 | Slot map | affine lattice from read centres; case size learned from complete cases | Layout (Free / Grid, learned, Essential in case mode) | slots |
| L2 | Unread slots first **NEW** | empty nodes go to strong passes before the locator's leftovers | auto | regions |
| L3 | Context decoding | known content, serial run, **print list NEW**, position-to-serial prior **NEW** | Match faint codes (on), Read shared content (on), Print list (file / ERP) | texts |
| L4 | Count | read == expected; short = NG naming row / column and reason; more than expected = NG (`explain.code.extra`) | Expected count (product master, Essential) | OK / NG |
| L4 | Duplicates | same text twice in a case, in memory (10,000 / 1,000), and **in history NEW** (DB index on all aggregated codes) | Memory sizes (Advanced) | orange flag, NG |
| L4 | Parent-child | parent read (SSCC / GS1-128 / DM) on side camera; children list sent with it; **rescue NEW**: a child unread on the top camera but in the print list's next-expected set may be confirmed by a second camera or a re-scan, never assumed | Parent required (on) | aggregation record |
| L4 | Mixed product | a child whose GTIN differs from the product = NG ("foreign item") | on | NG |
| L6 | Record | parent, children, slots, times, image hash; to plant server and ERP / L3 | - | event |

- **Corrections**: case shift and rotation (slot map is the fixture), shrink-wrap lattice
  distortion (slot map off), partial occlusion by flaps (COVERED reason), reflections on film.
- **Decide and safety**: a case is OK only with every slot read, no duplicate, parent read and
  linked, every text in the print list when one is loaded; a code read twice with different
  texts in one slot = NG; "rescue" never fills a slot without a read.
- **Learn and tune**: case lattice, serial step and direction per product, position-to-serial
  offset.
- **Accuracy target**: **100 % of cases decided right** (complete and correct, or NG with the
  missing slots named) on >= 1,000 cases per product size; >= 99.9 % of visible codes read so that
  false NG stays <= 1 case in 1,000 on 24-code cases. **Speed**: per case targets of section 1.
- **Hardware**: 12- and 96-code cases need sample frames from the owner; for 96 codes on one
  20 MP frame check >= 2.5 px/module first (else two cameras with stitching, Tool 3.6).
- **Build plan** (P1): ERP print list (2, shared with 5.0.3) -> position-to-serial prior (1) ->
  unread slots first (0.5) -> history duplicates index (0.5) -> parent-child confirmation flow (1)
  -> 12 / 96 frames on the bench (owner) (0.5).

### 5.6 Read diagnosis — "Why not read"

- **Purpose**: tell the operator why each unread code failed and what to fix. **Status: Have**:
  `engine/src/diagnose.cpp` (sharpness = min(1, edge strength / (light - dark)) on the strongest
  10 % of edges, after expired [US9552506](https://patents.google.com/patent/US9552506); finder,
  quiet zone, contrast, saturation, texture, module size), reasons CUT_OFF, GLARE, LOW_CONTRAST,
  COVERED, TOO_SMALL, BLUR, NO_FINDER, UNKNOWN in about 20 ms after decoding; `MissDiagnosis.cs`;
  explain keys of spec 5.6. Missing: accuracy of the reason against truth (no confusion matrix);
  "ideal mark" difference map; per-reason fix advice tied to the lighting recipe.
- **Best in class**: Cognex re-renders the ideal mark from decoded data and maps the deformation
  ([US8027802](https://patents.google.com/patent/US8027802), expired); verifier-style grades after
  a failed decode (US9552506).

| Layer | Step | Method | Output |
| --- | --- | --- | --- |
| L4 | Measure | as today; add print growth and PSF sigma vs learned (BLUR vs FOCUS DRIFT) | measures |
| L4 | Reason | first rule that holds, order as `cr_miss_reason` | reason |
| L4 | Ideal-mark map **NEW** | for a code read in a later frame or by a slow path, render the ideal and show the difference (US8027802) | overlay |
| L5 | Counts | reasons since start, most frequent named on the Run screen; per slot over time | trend |
| L6 | Advice | reason -> action (light, focus, printer, camera distance) | text |

- **Accuracy target**: the reason matches a human label on >= 90 % of 300 labelled misses.
  **Speed**: <= 20 ms per image after decoding (unchanged decode time).
- **Build plan**: label 300 misses and record the confusion matrix (1) -> ideal-mark map (1).

### 5.7 Reader health and drift (**NEW**) — "Reader health"

- **Purpose**: warn before the read rate falls. **Status: Partial**: plant dashboards and alerts
  exist (CN line-pc-live-charts, vision-alerts), zone read map and step shares are in the learned
  state; no drift metric.
- **Method**: CUSUM per product over ML, saturation, PSF sigma per zone, share of reads needing a
  slow step, share of low-margin reads, mean time per case (NEXT-GENERATION section 3 Reporting).
  An alarm when a CUSUM crosses h = 5 sigma; a "focus drifted" alarm when sigma rises 20 %.
- **Accuracy target**: an alarm at least one shift before the read rate drops 0.1 % in replayed
  history. **Build**: 2 pw, P2.

---

## 6. Tool 6 Code Verify — "Grade code"

Tool 6 measures print quality to ISO/IEC 15415 (2D), 15416 (1D) and 29158 (DPM), checks GS1
data, writes reports and grades sampled parts on the line. Today every grade is "process control"
unless a reflectance calibration and the standard's lighting are recorded (TOOL-SPECIFICATIONS
Tool 6). Open work: **#150** (minimum grade on Micro QR, PDF417, Aztec, Pharmacode, DotCode),
**#117** (Pharmacode and DotCode grading), **#128** (1D band finding at large X), **#135** (DPM
cell modulation per codeword).

Grade notation (GS1): overall grade / aperture in mils / wavelength in nm, for example
1.5/08/660 for GS1 DataMatrix on healthcare items (GS1 training deck
[Topic 8](https://www.gs1my.org/sites/default/files/Topic%208%20-%20GS1%20DIY%20Self-Learning%20-%20GS1%20Barcode%20Specifications_1.pdf) [S]);
the symbol specification tables are in section 5 of the GS1 General Specifications and a
verifier must conform to ISO/IEC 15426-1 (1D) or 15426-2 (2D)
([GS1 support](https://support.gs1.org/support/solutions/articles/43000734152-how-can-i-measure-the-quality-of-my-printed-barcodes-)).

### 6.0 Verification setup — "Set up grading"

- **Purpose / status**: reflectance and geometry calibration so grades mean what a verifier's
  mean. **Have** (engine): white-card flat field (`cr_set_flat_field`), two-patch card
  (`cr_measure_card`, `cr_set_reflectance`), lens and mm per pixel from a chessboard
  (`cr_calibrate_chessboard`), DPM image-based reflectance (`cr_dpm_params` r_cal, ml_cal,
  sr_cal); .NET `GradingCalibration.cs`. **Partial**: Studio wizard, expiry reminder, card
  self-check, lighting geometry check.
- **Best in class**: Cognex starts calibration by decoding a card whose code carries its target
  reflectances, then iterates exposure for each of three lighting schemes
  ([US8169478](https://patents.google.com/patent/US8169478), expired); DM475V needs its 45 deg
  LabelLight for an ISO grade [S] ([DM475V](https://www.cognex.com/dataman-475v) [S]).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Exposure | ML of the card's light patch 70-86 % | auto | exposure |
| L1 | Flat field | white card under working light | - | flat map |
| L1 | Reflectance map | certified dark / light patches -> grey-to-reflectance line | Card Rmax, Rmin (Essential) | map |
| L1 | Scale and lens | chessboard at the code plane | square mm | mm per px, distortion |
| L1 | Aperture | from X: 3 / 5 / 10 / 20 mil per ISO practice | auto, Advanced | aperture |
| L4 | Self-check **NEW** | grade the card's reference symbols; each within +-1 grade step of its certified value | - | SetupOk |
| L5 | Expiry **NEW** | days since calibration; light ML drift > 5 % forces recalibration | days (30) | reminder |

- **Target**: card symbols within +-1 grade step; setup <= 2 min. **Build**: Studio wizard with
  card self-check and expiry (1.5 pw, P2). **Hardware**: a certified conformance card per
  ISO/IEC 15426-2, for example a 12-symbol Data Matrix card with four reflectance patches
  ([vendor listing](https://etesters.com/product/b8292c98-5a0c-4f75-8b24-4f72d31027dc) [S]); a
  45 deg four-side light for label grading.

### 6.1 2D print grade, ISO/IEC 15415 — "Grade code"

- **Status: Have**: `engine/src/quality.cpp` (~1 ms per code): decode, symbol contrast,
  modulation, reflectance margin, fixed pattern damage, axial and grid non-uniformity, UEC from
  the decoder (PR grade-uec-decoder), quiet zone, print growth; QR, Micro QR, Data Matrix, Aztec on
  their grid; PDF417 by rows with codeword yield (`grade_pdf417.cpp`). **Partial**: the five
  orientations / multiple images per ISO, rMQR, DotCode (in #117), DMRE sizes named in the report.
- **Method gaps to close**: grade over 5 images at 72 deg steps when the part can be turned (or
  report "single image"); aperture X/3 uncalibrated vs the set aperture calibrated (already
  reported); rMQR and DMRE grids.
- **Decide**: overall >= minimum (C default); a code that cannot be graded while grading is on =
  NG (`explain.verify.not_graded`).
- **Target**: +-0.5 grade of a 15426-2 verifier on 100 codes per symbology (6.7). **Speed**: ~1 ms
  per code inline. **Build**: rMQR / DMRE grids (0.5), multi-image grade (1); P2.

### 6.2 1D print grade, ISO/IEC 15416 — "Grade code"

- **Status: Have**: `engine/src/grade1d.cpp`, `decodability.cpp`: 10 scans through 0.8 X
  aperture, Rmin, SC, ECmin, MOD, defects, decodability per symbology (n-width and two-width
  codes, EAN/UPC characters 1, 2, 7, 8 per ISO/IEC 15420, DataBar per ISO/IEC 24724 incl. stacked
  rows), quiet zones per type, per-scan output (`cr_get_scan`). Open: **#128** band finding at
  large X; Pharmacode in **#117**.
- **Missing**: composite (CC parts graded as PDF417 rows plus the linear part), Code 32 / PZN
  (as Code 39), two-track Pharmacode.
- **Target**: +-0.5 grade on 100 labels per type vs a 15426-1 verifier. **Speed**: <= 3 ms per
  code. **Build**: composite grading (0.5, with 5.2.8); P4.

### 6.3 DPM grade, ISO/IEC TR 29158 — "Grade code"

- **Status: Have on synthetic marks**: `engine/src/grade_dpm.cpp` (PR #96): cell contrast, cell
  modulation, minimum reflectance from image-based calibration, fixed pattern and distributed
  damage, AN, GN, UEC, 50 / 80 % apertures, dot-peen closing, light-on-dark, lighting 90 / 30Q /
  30T / 30S / D. Open: **#135** (cell modulation per codeword on the real placement). Missing: DPM
  report page and Studio settings (Pending of the PR #96 note), real parts.
- **Best in class**: Cognex DM8072V / DM475V with 30 / 45 / 90 deg multi-quadrant light [S];
  dot-peen quality by camera or laser displacement ([US8442297](https://patents.google.com/patent/US8442297) [S]).
- **Target**: +-0.5 grade vs a DPM verifier on 100 marks per mark type. **Speed**: <= 3 ms per
  code. **Build**: report page + settings (1), real-part correlation (1); P4.

### 6.4 GS1 data validation — "Check code data"

- **Status: Have** (.NET): `Gs1Check.cs`, `Gs1Syntax.cs`, `Gs1Associations.cs`,
  `Gs1Dictionary.g.cs` from the GS1 Barcode Syntax Dictionary (release 2026-01-27, 224 AIs),
  check digits, real dates, expiry rules, mandatory associations and invalid pairs, Digital Link
  URIs, a named "pharma" set (01, 17, 10, 21). The engine reports the symbology identifier and
  GS1 flag (`cr_code_ext.symbology_id`, `codereader.h`, PR #101), and the GS1 tool fails a symbol
  without FNC1 first, or one whose identifier is not known (`CodeTool.Judge`, `CodeTool.cs`).
- **Missing**: the move into the C++ core with the
  Code Verify parent (1); the pharma set on the Teach page (0.2); India-specific content rules if
  the customer's L3 system needs them (export: GTIN + batch + expiry + serial per the importer's
  rules).
- **Target**: 100 % of rule breaks caught on a rule test set of >= 500 good and bad element
  strings (generated from the dictionary); 0 good strings refused. **Speed**: <= 0.2 ms per code.

### 6.5 Grade report — "Grade report"

- **Status: Have** for 2D (PR #69), 1D (PR #87), DataBar rows; laid out after the DataMan 475V
  report (sections: header, verification grade row, result bars, numbered parameters, general
  characteristics, data detail, diagnosis, images with the modulation overlay and histograms;
  1D adds ten scan profiles). **Missing**: DPM page, batch report (one PDF per batch with a grade
  histogram per parameter), FPD transition ratios, codeword / encodation analysis, operator's
  language.
- **Target**: report grades equal engine grades (test); <= 1 s per report. **Build**: DPM page (1,
  with 6.3), batch report (1); P2.

### 6.6 Production grading and trend — "Keep grades"

- **Status: Have** (PR #70): grade every Nth part on the line, NG below the minimum or ungradable,
  grades sent to the plant server per part.
- **Missing**: SPC chart per parameter (X-bar / R over the last 500 sampled parts) and a warning
  when the trend will cross the minimum within one shift (1 pw, P2, with 5.7).
- **Target**: a C-to-D drift flagged before the first D on replayed data.

### 6.7 Verifier conformance and correlation (**NEW**) — "Check our grades"

- **Purpose**: show that our grades agree with a certified verifier, which no line camera grade
  can claim without it. **Status: Missing** (INDUSTRY-METHODS section 13: "Check against a
  certified verifier: Missing").
- **Method**: ISO/IEC 15426-1 / 15426-2 define verifier conformance and reference calibration
  standards ([ISO/IEC 15426-2:2023 record](https://scc-ccn.ca/standardsdb/standards/2053564) [S]).
  Steps: (1) buy one conformant verifier (label: 15415 / 15416 with 45 deg light; DPM: 29158 with
  30 / 90 deg) and a conformance test card; (2) grade the card's primary reference symbols with
  our setup: each within the 15426 tolerance; (3) grade 100 production codes per symbology with
  both; (4) record mean and max difference per parameter; (5) a per-parameter offset only if the
  difference is systematic and the cause is understood (lighting geometry); (6) repeat after any
  grading code change (bench of graded images with the verifier's grades as truth).
- **Target**: overall grade within +-0.5 on 95 % of codes, never more than one letter apart;
  card symbols within tolerance. **Build**: 3 pw plus the verifier purchase; P5 (needed before any
  report is called "ISO verification").

### 6.8 Printer feedback (**NEW**) — "Tell the printer"

- **Purpose**: close the loop to the printer before codes become unreadable. **Status: Missing.**
  A Hand Held Products family covers verifier-to-printer feedback
  ([US10546160](https://patents.justia.com/patent/10546160), owner's statement of 2026-10-30, legal clearance; not opened
  in this pass [S]).
- **Method**: from 6.6 trends, map parameter to printer cause: print growth up = heat / ink
  spread; modulation down on one axis = head element out (CIJ / TIJ nozzle, TTO dot line);
  contrast down = ribbon / ink low; grid non-uniformity = web speed. Send a warning to the
  operator and, where the printer has an interface, a status message (no automatic setting
  change: a person acts).
- **Build**: 1.5 pw, P3.

---

## 7. Open pull requests that hold part of this work (cite, do not redesign)

| PR | What it holds | This chapter's rows |
| --- | --- | --- |
| #153 (draft, claim) | Data Matrix 48-can batch: every visible code within 500 ms | 5.3.4, 5.0.5 |
| #148 (draft, claim) | Two-camera line at 225 parts / min: timing budget, run at rate, 10-minute soak | 5.0.5 speed numbers, i7 profile |
| #150 (draft, tested) | Minimum print grade on Micro QR, PDF417, Aztec, Pharmacode, DotCode | 6.1, 6.2 |
| #117 (ready) | Grading of Pharmacode and DotCode | 5.2.9, 5.3.7, 6.2 |
| #127 (draft) | Pharmacode with its bars lying flat | 5.2.9 |
| #128 (draft, claim) | 1D grading band finding at large X | 6.2 |
| #135 (draft) | DPM cell modulation per codeword | 6.3 |

Merged since this table was drawn up: **#152** (auto-find on the whole image, codes and text with
field types; `AutoFind.cs`, `TextTeach.cs`) and **#154** (auto-tune of a job on saved good and bad
images, AS-03; `AutoTune.cs`), both now Have, first version (rows 5.0.6, 5.1).

## 8. Build order and effort

Ordered by today's misses (section 1), then by effort. Each step is accepted only when the bench
shows more reads with 0 wrong at the line targets (AGENTS.md rule 11).

| Order | Step | Rows | Effort (pw) | Needs from the owner | Moves |
| ---: | --- | --- | ---: | --- | --- |
| 1 | Re-shoot hard cases on the MV-CS200; save 3-frame bursts; 12- and 96-code frames; 30-50 side-camera 1D frames; texts in truth | 5.0.0, 5.2, 5.5 | 1 | one hour on the line, spot-check of texts | shrink-wrap, 12/96 unmeasured, 1D unmeasured |
| 2 | Wrong-read counter, adversarial set, Monte-Carlo per soft path | 5.0.4 | 2.5 | - | safety proof before reads rise |
| 3 | Module-scaled local contrast (US10607047 method) | C3 | 1.5 | - | LOW_CONTRAST (QR, DM, shrink-wrap) |
| 4 | Lighting recipe per product: polariser for film, dark field for caps | 5.0.0 | 1 | lights, line time | LOW_CONTRAST, GLARE |
| 5 | PSF deblur everywhere, glare mask, growth-shifted sampling | C5, C6, C16 | 3 | - | BLUR on 24 / 48 QR |
| 6 | Evidence budget, read-back, N-version, fewest-symbols ranking (before any print-list read is accepted) | 5.0.4 | 4.5 | decision: 1e-9 (owner decision 5 in chapter 06) | keeps 0 wrong as reads rise |
| 7 | ERP print list, position-to-serial prior, unread slots first (candidates confirmed by step 6) | 5.0.3, 5.5 | 3.5 | ERP export per roll | COVERED, LOW_CONTRAST on serial products |
| 8 | Scheduler queue on the pool (after #148's profile) | 5.0.5 | 3.5 | line PC afternoon | codes lost to time (48 QR 501 ms) |
| 9 | After #153: DM own decoders for all sizes, full lattice method, DM under-resolved | 5.3.4, C9 | 6 | - | DM robustness beyond the 48-can batch |
| 10 | Bracket per trigger, Mono12, multi-frame SR | 5.0.0, C8 | 6 | stopped-case bursts | TOO_SMALL, glare |
| 11 | QR soft v7-40, covered-finder grid, cylinder model | 5.3.1, C14 | 4.5 | - | larger QR, curved packs |
| 12 | 1D: segment stitching, generic GS1-128 fit, ITF bearer mask, DataBar own fallback | 5.2.x | 5 | real labels | 1D to 99.9 % |
| 13 | Locator recall recorded, CodeNet new classes, tile-histogram tie-breaker | 5.0.2 | 4 | - | NO_FINDER |
| 14 | Tool contract outputs and settings; Studio pages; Learning page; diagnosis confusion matrix; drift | 5.1, 5.3, 5.0.6, 5.6, 5.7 | 9 | labelled misses | P2 usability |
| 15 | Verify: setup wizard, rMQR / DMRE grids, multi-image grade, batch and DPM report, SPC (FNC1 check and symbology identifier are built: PR #101) | 6.0-6.6 | 8.5 | calibration card | P2 grading |
| 16 | Data engine, enhancer on real pairs, ModuleNet | C7, 5.0.3 | 7.5 | - | P3 |
| 17 | Aztec own path, MicroPDF417 + composite, two-track Pharmacode, Code 32 / PZN, Micro QR M1, MaxiCode, Han Xin, other 1D | 5.2.x, 5.3.x | 17.5 | samples per type | P4 coverage |
| 18 | DPM mode: settings, light sequence, dot chain, real parts | 5.4 | 6 | parts, lights | P4 DPM |
| 19 | Verifier correlation and printer feedback | 6.7, 6.8 | 4.5 | a conformant verifier | P5 certification |
| | **Total** | | **about 100** | | |

rMQR is not in step 17: it is enabled in wave 0 (chapter 06) behind its own new format flag
`CR_RMQR`, so an rMQR is never counted in a QR job (today `engine/src/zxformats.cpp:42` drops every
rMQR read; the flag does not exist yet).

Steps 1-9 (about 27 pw plus #153 and #148) are the path to >= 99.9 % on every batch the owner
runs today; steps 10-13 add margin for new products and harder prints.

## 9. Patented methods adopted in this chapter

All under the owner's statement of 2026-10-30 (legal clearance). Status from Google Patents (an estimate).

| Patent | Holder | Method adopted | Where | Status |
| --- | --- | --- | --- | --- |
| [US10607047](https://patents.google.com/patent/US10607047B2/en) / [US11138397](https://patents.google.com/patent/US11138397B2/en) | Cognex | module-scaled local tone mapping | C3 | active to ~2038-12 |
| [US10956697](https://patents.google.com/patent/US10956697B2/en) / [US11455482](https://patents.google.com/patent/US11455482B2/en) / US9911024 | Cognex | no-fixed-pattern grid, out-of-image erasures, ranked hypotheses | 5.0.3 lattice, 5.3.4 | active to ~2035-11 |
| [US11151346](https://patents.google.com/patent/US11151346B2/en), [US9607200](https://patents.google.com/patent/US9607200B2/en) | Cognex | under-resolved 2D and 1D | C9, 5.2 | active to ~2034-10 |
| [US9734376](https://patents.google.com/patent/US9734376B2/en), [US9104932](https://patents.google.com/patent/US9104932B2/en) | Cognex | multi-frame cell voting; frames of different settings | C5, C8, 5.0.0 | active to ~2033 |
| [US9122952](https://patents.google.com/patent/US9122952B2/en), [US9361499](https://patents.google.com/patent/US9361499B2) | Cognex | Hotbars projection; partial-scan evidence | 5.2 | active to ~2032 / ~2034 |
| [US9600703](https://patents.google.com/patent/US9600703B2/en), [US10325129](https://patents.google.com/patent/US10325129B2/en), [US8794521](https://patents.google.com/patent/US8794521B2/en), [US7984854](https://patents.google.com/patent/US7984854) | Cognex | setting order by points + EMA; leaving-first order; overrun when probably decodable; parallel decoders | 5.0.5 | active to ~2027-2033 |
| [US20140111656](https://patents.google.com/patent/US20140111656) (US9413981 family) | Cognex | interleaved-gain brightness probe | 5.0.0 | active to ~2033-09 |
| [US11003875](https://patents.google.com/patent/US11003875B2/en) | Cognex | DPM lighting recipes | 5.4 | active to ~2038-11 |
| [US20260010746](https://patents.google.com/patent/US20260010746A1/en) | Cognex | DL codeword prediction then RS | ModuleNet | pending |
| [US12039400](https://patents.google.com/patent/US12039400), [US11854163](https://patents.google.com/patent/US11854163) | Keyence | NN restore in parallel with first decode, second decode on the result | C7 | active to 2041-2042 |
| [US8596540](https://patents.google.com/patent/US8596540) | Scandit | blurred reference patterns for 1D | 5.2 generative fit | active to 2030-03 |
| [US9361503](https://patents.google.com/patent/US9361503) | Datalogic | blur-robust space + classifier | 5.2 (option) | active to 2034-11 |
| [US10650205](https://patents.google.com/patent/US10650205) | Hand Held Products | DPM decoding | 5.4 | active to 2038-09 |
| [US9208367](https://patents.google.com/patent/US9208367) | Honeywell | multi-code marking and transmit order | 5.5 | active to 2034-01 |
| [US12210930](https://patents.google.com/patent/US12210930) | Code Corp | decodability prediction | **ordering only**: an image is never skipped; unreadable stays NG (line safety) | active to 2043-07 |
| [US7181066](https://patents.google.com/patent/US7181066B1/en), [US9036929](https://patents.google.com/patent/US9036929B2/en), [US7965899](https://patents.google.com/patent/US7965899B2/en), [US7878402](https://patents.google.com/patent/US7878402B2/en), [US7604174](https://patents.google.com/patent/US7604174B2/en), [US9552506](https://patents.google.com/patent/US9552506), [US8027802](https://patents.google.com/patent/US8027802), [US8169478](https://patents.google.com/patent/US8169478), [US8150163](https://patents.google.com/patent/US8150163) | Cognex, Scanbuy | locator, windows, dot chain, homography, light retry, diagnostics, ideal mark, card calibration, multi-frame SR | 5.0.x, 5.4, 5.6, 6.0 | expired (US7412106, parent of the dot chain, to 2026-12-23) |

## Not opened / limits

- **Blocked or not opened in this pass**: ISO pages for ISO/IEC 23941, 21471, 15426-2, 24723
  (403 or search only: [S]); GS1 General Specifications download (403); GS1 change notice on DPM
  (403); Cognex technologies page, DM475V page and manual, DataMan 380 page (snippets only); NI
  IMAQ page; OpenCV WeChat docs; BarBeR paper; Sensors 2023 DPM paper; Tanaka GCCE 2024 paper;
  Omron MicroHAWK announcement; US10546160 and US8442297; the zxing-cpp docs site did not list
  formats or options (the README and `ReaderOptions.h` were read instead).
- **Not verified**: whether zxing-cpp reads DMRE sizes (README does not say); the Laetus two-track
  Pharmacode encoding described in 5.2.10 is from general knowledge, no specification page was
  found; the 1.5/08/660 grade for GS1 DataMatrix comes from a GS1 Malaysia training deck seen as
  a search result, not from the General Specifications; ISO/IEC 15420 and 24724 were not opened
  (their rules are cited as implemented in `decodability.cpp`).
- **No measured numbers exist** for: 12- and 96-code cases, real 1D labels, real PDF417 / Aztec /
  DotCode / Pharmacode / DataBar images, DPM parts, multi-frame bursts, the line i7, the AI
  enhancer on real crops, CodeNet recall, grade agreement with a verifier. Every target above is a
  target until the bench or the verifier shows it.
- **Bench limits**: truth holds counts, not texts, so "0 wrong" is inferred from count uniqueness
  (NEXT-GENERATION section 1); bench noise is about +-2 codes; reads are time-budgeted and a busy
  machine fails good code (exit 2 = not judged).
- **Effort figures** are estimates for one developer with an AI assistant; they exclude line time,
  owner image collection and hardware purchase lead times.
- **Patent status** is Google Patents' estimate; India (IN) members were not searched in InPASS in
  this pass. The owner's statement of 2026-10-30 (legal clearance) is recorded as his statement,
  not as a legal opinion of this study; the scope (territories, export) is for the owner to confirm.
- Vendor read rates quoted (Dynamsoft, Keyence, Cognex) are vendor-run or marketing.
