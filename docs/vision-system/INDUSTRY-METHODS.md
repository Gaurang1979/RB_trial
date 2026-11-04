# Industry methods, tool by tool

What the leading vision vendors, open-source libraries, recent AI papers and patents do in each
tool we build, set against what our engine already does, with a ranked list of the missing
methods most likely to bring codes and laser print to 100 % on the owner's lines.

Owner, 2026-10-30: "for every tools we are going to build check all All possible AI tools and
cognex, halcon, teledyne dalsa, omron, national instruments and worlds best software methods,
patents and tools. prepare list of process, tools, filter and everything applicable to get 100%
results."

This document adds to, and does not repeat, the earlier studies:
[RESEARCH-COGNEX-HALCON.md](../RESEARCH-COGNEX-HALCON.md),
[RESEARCH-COGNEX-PATENTS.md](../RESEARCH-COGNEX-PATENTS.md) (Cognex code-reading patents, ten
ideas), [PATENTS.md](../PATENTS.md) (patent register),
[RESEARCH-NEXT-GENERATION.md](../RESEARCH-NEXT-GENERATION.md) (reader roadmap R0-R16),
[RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md](RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md) and
[AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md). The tool list is
[TOOL-SPECIFICATIONS.md](TOOL-SPECIFICATIONS.md) with the phases of section 15 of the
[requirements](../REQUIREMENTS-VISION-SYSTEM.md).

## How to read the tables

- **Ours:** **Have** = built and tested in the engine or .NET; **Partial** = part of it built, or
  built only inside the code reader and not as a user tool; **Missing** = only specified, or not
  even specified. The file or change note follows where it helps.
- **Gain:** the expected effect on reads or catches on the owner's lines. **High** = addresses a
  cause of misses we measure today; **Med** = likely to help, not yet measured; **Low** = rare
  case or comfort. Every gain is a forecast until the bench (`tools/bench/batch_check.py`) or
  `ocr_bench` shows it.
- **Effort:** **S** = days; **M** = one to three weeks; **L** = a month or more, or needs
  hardware or images from the owner.
- **Sources:** every vendor page, paper and patent named was opened on 2026-10-30, unless marked
  **[S]** (seen only as a search-result summary; treat as unverified). Vendor read rates are
  marketing unless a test is described.
- **Patents:** status and expiry are Google Patents' own estimates. The notes on what to design
  around are an engineering note for legal review, **not legal advice**. India (IN) members were
  not checked in this pass; InPASS needs a manual search (see [PATENTS.md](../PATENTS.md)).

## 1. Where we stand

Measured on the 53 real sample images, at the line time targets (`truth/batch-baseline.csv`,
main of 2026-10-30), with **0 wrong reads**:

| Batch | Codes read | Miss reasons ([BATCH-TEST-RESULTS.md](../BATCH-TEST-RESULTS.md); a miss can have several) |
| --- | ---: | --- |
| 24 QR (16 images) | 372 / 384 (96.9 %) | BLUR 18, NO_FINDER 12, LOW_CONTRAST 5, GLARE 5 |
| 45 QR (6) | 270 / 270 (100 %) | none |
| 48 QR (18) | 843 / 857 (98.4 %) | BLUR 15, LOW_CONTRAST 14, NO_FINDER 6, COVERED 4 |
| 24 DM (2) | 48 / 48 (100 %) | none |
| 48 DM (2) | 61 / 95 (64.2 %), over the 500 ms target | LOW_CONTRAST 39, COVERED 26, NO_FINDER 16: dark Data Matrix caps |
| Other QR, shrink-wrapped excise cases (9) | 129 / 214 (60.3 %) | LOW_CONTRAST 114 under the wrap, TOO_SMALL 19 (below about 1.8 px per module), COVERED 11 |
| **All** | **1,723 / 1,868 (92.2 %)** | |

Miss causes re-counted 2026-11-03 (Req 17.12 MISS-EV-01..03; one run of the 58 sample images in
calibration mode, every region diagnosed, Mac busy so 1,537 of 1,868 read): the miss list now keeps
a LOW_CONTRAST or BLUR region only with code evidence (a finder, or code texture at the product's
module size) and leaves out regions whose code was read in another region. Before, 932 regions:
LOW_CONTRAST 285, BLUR 196, NO_FINDER 173, UNKNOWN 150, COVERED 54, GLARE 52, TOO_SMALL 22. After,
532: BLUR 75, NO_FINDER 173, UNKNOWN 150, COVERED 54, GLARE 52, TOO_SMALL 22, LOW_CONTRAST 6. So
about 98 % of the old LOW_CONTRAST regions and 62 % of the BLUR regions were background or codes
read elsewhere. The per-batch counts in the table above are the older counts.

No real 12-code or 96-code case, no real 1D label, and no real PDF417, Aztec, DotCode, Pharmacode
or DPM part has been measured yet.

Text (`ocr_bench`, synthetic lines unless stated; change note
`2026-10-30-claude-ocr-ai-reader.md`): AI reader characters right laser 92.6 %, thermal transfer
98.0 %, CIJ dot print 72.9 %, embossed 89.4 %; real laser lines 100 % (389 lines). OCV accepted
**0** changed or look-alike texts on every print type, but a true laser text passes its check only
69 % of the time and a good CIJ line about 5 % (false rejects, the safe direction).

So most misses come from the image, not the decoders: low contrast under shrink wrap and on dark
caps, blur on the 24- and 48-code QR cases, codes smaller than about 1.8 px per module, covered or
missing finders, and the time budget on 48-code Data Matrix cases. The ranked list in section 15 is built on that.

## 2. Published read rates

Nobody publishes a measured rate on pharma aggregation cases. What exists:

| Claim | Source | What it shows |
| --- | --- | --- |
| Data Matrix, 57 images, 112 codes: Dynamsoft 83 % default, **100 % with tuned templates**; libdmtx 72 % at 3.6 s per image; zxing-cpp 65 % at 3.6 ms | [Dynamsoft DM benchmark](https://www.dynamsoft.com/codepool/data-matrix-reading-benchmark-and-comparison.html) (vendor-run) | Per-job tuning, not the default reader, closes the gap. Our product master and learned state are that tuning. |
| QR, 536 images, 1,232 codes: Dynamsoft 83 %, BoofCV 61 %, OpenCV WeChat 49 % (0 % on images with many codes), Scandit 44 %, ZBar 39 %, ZXing 32 % | [Dynamsoft QR benchmark](https://www.dynamsoft.com/codepool/qr-code-reading-benchmark-and-comparison.html) (vendor-run) | Plain open-source decoders are far from 100 %; WeChat's CNN reader is unusable for aggregation. |
| Keyence SR-X: AI chip trained on over 100,000 images, "strive for 100 %", no measured number | [Keyence SR-X](https://www.keyence.com/products/barcode/barcode-readers/sr-x/) | Marketing only. |
| Cognex DataMan 380 tunnel "> 99 %" | Cognex press release [S] | Logistics tunnel, not pharma. |
| Anomaly detection: Dinomaly 99.6-99.7 image AUROC on MVTec AD; EfficientAD-M 99.1 at 4.5 ms (GPU); state of the art **below 60 % AU-PRO on MVTec AD 2** (lighting shift, tiny defects) | [Dinomaly](https://arxiv.org/abs/2405.14325), [EfficientAD](https://arxiv.org/html/2303.14535), [MVTec AD 2](https://arxiv.org/abs/2503.21622) | Public benchmark numbers do not transfer to a plant. |
| Text: PARSeq 91.9 % (synthetic) / 96.0 % (real) word accuracy on scene text; no vendor publishes accuracy for dot-matrix or inkjet date codes | [PARSeq](https://arxiv.org/abs/2207.06966) | Our own `ocr_bench` on real line images is the only usable number. |

**Rule:** our bench on `VS_IMAGES` is the acceptance number for every method below.

## 3. Acquisition and image preparation (Tool 0)

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Lighting per mark | Low-angle dark field for DPM and dark caps; diffuse or 45° for labels; ISO grading only with defined light | Cognex DM475V (30/45/90° light) [S]; Microscan US6352204 (low-angle DPM light) [S]; HALCON `manual_is_imprinted` for reflections ([set_text_model_param](https://www.mvtec.com/doc/halcon/2505/en/set_text_model_param.html)) | Partial: LineAdvisor suggests exposure; no lighting rule per product | High (dark DM caps, shrink wrap) | L (line time) |
| Glare under film | Cross-polarised light and lens polariser | Common practice; RESEARCH-NEXT-GENERATION R13 | Missing (never tried on the line) | High (shrink-wrap LOW_CONTRAST) | L (line time) |
| Exposure search | One-button auto-tune of exposure, gain and light | Keyence SR-X auto tuning ([page](https://www.keyence.com/products/barcode/barcode-readers/sr-x/)); Cognex US9413981 (interleaved gains, active, no IN member found) | Have: `CalibrationSearch.cs`, cached-first, brightness probe | Med | - |
| Several exposures per trigger | Bracket 2-3 exposures on one trigger, fuse | Cognex US9104932 / US9734376 (active) | Partial: test burst and multi-frame fusion built (`multiframe.cpp`, `TestBurst.cs`); automatic bracket per trigger only specified | High | M |
| Flat field | Divide by a white reference | HALCON, Cognex | Have inside the reader (`CR_STEP_FLAT`) and for grading (`calibration.cpp`); no Base tool | Low | S |
| Local contrast | Local normalisation (none / global / local / local advanced) | Cognex OCRMax ([doc](https://docs.cognex.com/is_611/web/EN/ise/Content/Reference/OCRMax.htm)); HALCON `contrast_tolerance` ([get_data_code_2d_param](https://www.mvtec.com/doc/halcon/2511/en/get_data_code_2d_param.html)); Cognex US10607047 module-scaled tone mapping (active to 2038) | Partial: fixed 4×4-tile CLAHE (`decode.cpp`) | High | S-M |
| Filters, morphology, threshold, polar, arithmetic | Standard kit | All vendors; OpenCV | Partial: inside the reader only; no user Base tools (only Code, Text and Locate tool classes exist) | Low for reading; needed for P1 | M |

More acquisition methods for later phases: GigE **scheduled action commands** with PTP for
cameras that must fire together ([Basler](https://docs.baslerweb.com/action-commands)); a
**sequencer burst** of 2-4 exposures merged on the host
([Basler HDR how-to](https://docs.baslerweb.com/knowledge/how-to-hdr-imaging-with-basler-cameras-using-sequencer-mode-and-pypylon));
an on-chip **4-direction polarisation sensor** for glare on foil and glossy cartons
([Sony Polarsens](https://emergentvisiontec.com/polarization-image-sensor-technology-sony-polarsens-how-does-it-work/));
**photometric stereo** with 3-6 lights for embossed text and dents (Keyence
[LumiTrax](https://www.keyence.com/landing/vision/lumitrax-lighting.jsp), HALCON
[photometric_stereo](https://www.mvtec.com/doc/halcon/13/en/photometric_stereo.html), Omron
[FH](https://industrial.omron.eu/en/products/fh-vision-system)); a dark-frame flat field
([Euresys](https://documentation.euresys.com/Products/COAXLINK/COAXLINK_10_3/Content/03_Using_Coaxlink/functional-guide/flat-field-correction/what-is-ffc.htm));
and HALCON's halo-free [shock filter](https://www.mvtec.com/doc/halcon/13/en/shock_filter.html)
for sharpening. All are **Missing**; gain Med for embossed and foil print, effort M each.

## 4. Locate (Tool 1)

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Grey-level match | Normalised cross-correlation over a pyramid, sub-pixel by fit | All vendors; OpenCV `matchTemplate` | Have: `locate_tool.cpp`, `LocateTool.cs` (turn, no scale) | - | - |
| Edge-based match | Model of edge points with direction; pose found without resampling; score = visible share of the model | Cognex [PatMax](https://docs.cognex.com/vpro_930sr1/web/help/html/d65f63c3-490e-4a49-9da3-125054771348.htm); HALCON [find_shape_model](https://www.mvtec.com/doc/halcon/2205/en/find_shape_model.html); DALSA [Sapera](https://www.1stvision.com/cameras/Teledyne-DALSA-Sapera-Processing) ("1/50 pixel", vendor claim) | Missing (spec 1.2) | Med (parts that change brightness) | M |
| Fast coarse-to-fine | Large features first, small features after; contrast and feature thresholds relative, not absolute | Cognex [PatMax RedLine](https://docs.cognex.com/vpro_970/web/EN/help/html/e4faf0d2-0237-465b-aa18-8ed510d49594.htm) | Missing | Med (lighting drift) | M |
| Sub-pixel ladder | Interpolation; least squares (three levels); with deformation up to N px | HALCON `find_shape_model` | Partial: parabola fit only | Low | S |
| Polarity | Use, ignore globally, ignore locally | HALCON metric | Missing | Low | S |
| Scale in x and y apart | Anisotropic scale for oblique cameras | HALCON [create_aniso_shape_model](https://www.mvtec.com/doc/halcon/13/en/create_aniso_shape_model.html) | Missing (spec has uniform scale only) | Low | M |
| Bent and wrinkled parts | Local deformable model returns a deformation field and a rectified image | HALCON [find_local_deformable_model](https://www.mvtec.com/doc/halcon/13/en/find_local_deformable_model.html); Cognex PatFlex [S] | Missing | Med (pouches, labels on bottles, shrink wrap) | L |
| Printed faces in perspective | Interest points and randomised ferns, homography | HALCON [descriptor model](https://www.mvtec.com/doc/halcon/13/en/create_uncalib_descriptor_model.html) | Missing | Low | M |
| Setup advice | Tilt the pattern about 5° off the pixel grid; avoid blur; undistort first; train several instances rather than rely on elasticity | Cognex PatMax page above | Missing (Auto-learn warnings) | Low | S |

## 5. Measure (Tool 2)

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Caliper | Project grey values across the box into a 1D profile; derivative-of-Gaussian peaks are edges; sigma below half the box length | Cognex [Caliper](https://docs.cognex.com/vpro_930sr1/web/help/html/f36c9fbe-115f-4c43-a423-259f2be8c218.htm); HALCON [measure_pos](https://www.mvtec.com/doc/halcon/13/en/measure_pos.html) | Missing (only 1D bar widths inside the reader) | Med (P1 measuring job) | M |
| Edge scoring | Position, size, contrast and straddle scores 0-1, combined by geometric mean, with a reason per edge | Cognex Caliper; HALCON [fuzzy_measure_pos](https://www.mvtec.com/doc/halcon/13/en/fuzzy_measure_pos.html) | Missing | Low | S |
| Sub-pixel edges | Deriche, Lanser, Canny with hysteresis; Devernay sub-pixel non-maxima | HALCON [edges_sub_pix](https://www.mvtec.com/doc/halcon/13/en/edges_sub_pix.html); [IPOL Devernay](https://www.ipol.im/pub/art/2017/216/) | Missing | Med | M |
| Thin lines | Steger ridge detector (Gaussian derivatives, width and asymmetry) | HALCON [lines_gauss](https://www.mvtec.com/doc/halcon/13/en/lines_gauss.html) | Missing | Low | M |
| Robust fits | Tukey (recommended) or Huber for lines; geometric circle fit when noise is large; RANSAC for outliers | HALCON [fit_line](https://www.mvtec.com/doc/halcon/13/en/fit_line_contour_xld.html), [fit_circle](https://www.mvtec.com/doc/halcon/13/en/fit_circle_contour_xld.html); [RANSAC](https://en.wikipedia.org/wiki/Random_sample_consensus) | Missing | Med | S |
| Measure from local anchors | Locate small stable sub-models near each gauge, not one global pose | Cognex US6993177 (expired) | Missing | Low | M |
| Accuracy | No vendor publishes a caliper accuracy; the knobs are sigma, averaging and interpolation | - | - | - | - |

## 6. Calibrate (Tool 3)

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Plate calibration | Zhang: homography start, then maximum-likelihood refinement with distortion | [Zhang 2000](https://www.microsoft.com/en-us/research/publication/a-flexible-new-technique-for-camera-calibration/); OpenCV | Partial: `calibration.cpp`, for grading in mm only | Med (P1) | S |
| Models | Polynomial or division distortion, tilt lens, telecentric, line scan | HALCON [calibrate_cameras](https://www.mvtec.com/doc/halcon/13/en/calibrate_cameras.html); OpenCV rational, thin prism, fisheye | Partial: OpenCV radial and tangential | Low | M |
| Pass rule | Back-projection RMS about 0.1 px or better for one camera; report intrinsic standard deviations | HALCON | Missing | Med (trust) | S |
| Undistort | Warp with a valid-pixel mask | Cognex [checkerboard calibration](https://docs.cognex.com/vpro_980/web/EN/help/html/d3cfc90a-4145-4047-bcd3-fc84cd9d08ed.htm) | Missing (model computed, never applied) | Low | S |
| N-point, hand-eye | Linear from point pairs with RMS; hand-eye with at least 10 poses, 30° or more about 2 axes | Cognex; HALCON [hand_eye_calibration](https://www.mvtec.com/doc/halcon/13/en/hand_eye_calibration.html); OpenCV Tsai, Park, Daniilidis | Missing | Low (no robots today) | M |

## 7. Blob and count (Tool 4)

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Threshold and label | 8- or 4-connected labelling (Spaghetti, BBDT); minimum size; cleanup | HALCON [connection](https://www.mvtec.com/doc/halcon/13/en/connection.html); OpenCV `connectedComponentsWithStats`; Cognex blob [S] | Partial: used inside the reader (10 places); no user tool | Med (P1) | S |
| Shape features | Convex hull, same-moment ellipse, smallest circle, rotated rectangle | HALCON [shape_trans](https://www.mvtec.com/doc/halcon/13/en/shape_trans.html) | Missing as a tool | Low | S |
| Texture segmentation | Laws texture masks plus classification | HALCON [texture_laws](https://www.mvtec.com/doc/halcon/13/en/texture_laws.html) | Missing | Low | M |

## 8. Code Reader: finding and cleaning every code (Tool 5, all symbologies)

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Find all codes first | Texture / finder search; CNN detector; YOLO or segmentation | Cognex PowerGrid, Hotbars [S]; OpenCV WeChat CNN [S]; [BarBeR](https://ditto.ing.unimore.it/barber) 8,748 public images and 25 localisation papers [S] | Have: classical density locator (`locate.cpp`) and CodeNet AI locator (`ai_locator.cpp`) | - | - |
| Measure the locator | Recall on a public set and on our own misses | BarBeR | Missing: CodeNet recall is printed by `ml/evaluate.py` but not recorded | Med (finds NO_FINDER misses) | S |
| Stop at the expected count | Try localisation, binarisation and deblur modes in order until the expected number of codes is read | Dynamsoft modes ([performance settings](https://www.dynamsoft.com/barcode-reader/docs/mobile/programming/android/quick-performance-settings.html) [S]); HALCON `candidate_selection` default / extensive / all | Have: count-driven search-harder pass, learned step order per zone | - | - |
| Binarisation variants | Global, local block, module-mean, grey equalisation | Dynamsoft, HALCON | Have: adaptive, module-mean global and local (`gridfit.cpp`) | - | - |
| Deblur | Wiener or Richardson-Lucy with a measured PSF | Classic; Datalogic US9361503 (blur-robust classifier, active) | Have: RL with Wiener start; PSF measured per zone (`psf.cpp`) but used only to re-score candidates | Med: deblur with the measured PSF | S |
| Super-resolution (one image) | CNN SR (EDSR, VDSR, ESPCN, SRCNN), Mosaic-SR | [Sancar 2025](https://peerj.com/articles/cs-2841) [S]; [Mosaic-SR](https://github.com/Henvezz95/mosaic-sr) (no licence: do not copy) | Partial: Lanczos 2×/4×; AI enhancer net (`enhancer.onnx`) | Med | M |
| Super-resolution (several frames) | Keep low-noise frames, accumulate statistics, decode what no single frame can | Scanbuy US8150163 (**expired**, free) | Partial: multi-frame evidence fusion built; no sub-pixel SR reconstruction | Med (TOO_SMALL) | M |
| Restore with a net, then decode | Net trained on degraded / ideal pairs restores the code, second decode on the result | **Keyence US11854163 and US12039400 (active to 2041-42)** | Have (enhancer, trained on `violations.py` pairs, placed after normal decoding) | - | see section 14 |
| Under-resolved symbols | Area-weighted pixel model, 3×3 hypotheses, RS erasures | Cognex US11151346 (active to 2034, no IN member found) | Partial: `underres.cpp` (about 1.3-2.6 px per module), on by default (`CR_UNDERRES` ON in `engine/CMakeLists.txt`); constraint-graph pruning and routing below 1.5 px per module missing | Med (TOO_SMALL) | M |
| Polarity, mirror | Auto, or set per job | HALCON `polarity`, `mirrored`; NI auto [S] | Partial: learned and automatic; no user setting or output | Low | S |
| Perspective, curved surface | Homography; cylinder model | HALCON `module_grid` variable; Omron MicroHAWK X-Mode [S] | Have: homographies; cylinders read to 138° without an unwrap | Low | - |
| Decoders | zxing-cpp, libdmtx, own | [zxing-cpp](https://github.com/zxing-cpp/zxing-cpp): no DotCode, no Pharmacode, no deblur | Have: zxing-cpp, libdmtx (with a plausibility gate), own decoders for Pharmacode, DotCode, PDF417 rows, Micro QR, 1D at any angle | - | - |
| Rank candidates | Prefer the reading that needed the fewest error-correction symbols | NI IMAQ Read Data Matrix "demodulation mode" [S] | Partial: soft decoders keep 2 RS symbols in reserve; no cross-candidate ranking by symbols used | Med (safety) | S |
| Confirm before accept | Same text in two frames before reporting | [Scanbot](https://scanbot.io/blog/how-to-read-damaged-or-blurry-barcodes/) | Partial: serial-run, known-content and read-back gates; no rule that a low-evidence read must be reproduced (RESEARCH-NEXT-GENERATION section 6) | Med (safety) | M |
| Never skip an image | Do not drop images a model predicts undecodable | Code Corp US12210930 does drop them (active to 2043) | Have the opposite: unreadable = NG with a reason | - | - |

## 9. QR and Micro QR

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Grid from finders | Fit the n × n grid on the three finders; flexible grid for wrinkled film | HALCON `module_grid` | Have: `gridfit.cpp`, `qrgrid.cpp`, flexible K × K displacement net | - | - |
| Soft decoding | Least certain codewords as RS erasures | Cognex (RESEARCH-COGNEX-PATENTS) | Have for versions 1-6 (`qrsoft.cpp`); **missing above version 6** | Low today (our QR are small) | M |
| Known content | Fix codewords shared with the case's other codes | Our own | Have: `knownDecodeQr` | - | - |
| Serial-run match | Candidates from the case's serial run, printer encoding reproduced | Our own (no vendor advertises it) | Have: `match.cpp`, default on | - | - |
| ERP print list as exact candidates | Load the printed serials per roll so the candidate set is exact from the first code | Keyence SR-X links readings across stations (similar idea) | Missing (R8) | **High** (COVERED and LOW_CONTRAST on serial products) | M |
| Module-scaled contrast before sampling | Local mean and contrast over a few modules | Cognex US10607047 (active; own form needed) | Missing (fixed-tile CLAHE) | High | S-M |
| Micro QR, rMQR | Grid fit; locked focus | zxing-cpp reads rMQR; Scanbot | Have Micro QR (`microqr.cpp`); rMQR not enabled | Low | S |

## 10. Data Matrix

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Inside-out lattice | Module pitch by Fourier, windows, L-finder fit, RS rejects wrong windows | Cognex PowerGrid; US9911024 family | Have: `lattice.cpp` | - | - |
| Damaged or missing L finder | `finder_pattern_tolerance` high; grid extrapolated from a known symbol size; modules outside the image as erasures | [HALCON](https://www.mvtec.com/doc/halcon/2511/en/get_data_code_2d_param.html); Cognex US10956697 / US11455482 (active to 2035, own form needed) | Partial: lattice and soft decoding; no hypothesis set over the learned size with out-of-image erasures | **High** (48 DM: NO_FINDER, COVERED) | M |
| Dot-printed cells | Gap setting no / small / big; melt dots into modules | HALCON `module_gap`; NI cell filters; Cognex US7965899 (expired) | Have: `CR_STEP_DOTS` cell filters | - | - |
| Dark caps, low contrast | Dark-field light plus local normalisation; HALCON `contrast_tolerance` low | HALCON; Cognex | Partial (software); lighting not tried | **High** (48 DM LOW_CONTRAST) | M + line time |
| Erasure decoding | Weak codewords as RS erasures | Symagery US6685095 (expired) | Have: `softDecodeDm`, `knownSoftDecodeDm` | - | - |
| Time on 48 DM | Persistent pool, cost-aware scheduler | Cognex US10325129 (conveyor ordering) | Partial (R2 / R10 open); the 225 ppm speed work holds this | High (one 48 DM image is over time) | M |
| Tuned templates | DPM and deformation templates | Dynamsoft: 83 % → 100 % tuned | Have the equivalent: product master, learned sizes, learned encoding | - | - |

## 11. 1D codes (SSCC / GS1-128, EAN/UPC, ITF-14, DataBar, Pharmacode)

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Locate at any angle | Texture bands (Hotbars); fast scan; scan-line candidates | Cognex Hotbars II [S]; HALCON `find_bar_code` [S]; Dynamsoft `LM_ONED_FAST_SCAN` [S] | Have: structure-tensor band locator (`linear.cpp`) | - | - |
| Many scan lines | Median of many scan lines along the band | Classic (expired Symbol / Metrologic patents) | Have | - | - |
| Torn or scratched bars | Join intact segments of several scan lines; result must pass the check digit | [Scanbot](https://scanbot.io/blog/how-to-read-damaged-or-blurry-barcodes/) | Missing | Med | S |
| Blurred bars | Fit rendered candidates through the measured blur, confirmed by both check digits | Ours; Scandit US8596540 (blurred reference patterns, active to 2030) | Have for SSCC / EAN / ITF-14 (`linear.cpp`, `rules1d.cpp`) | - | - |
| Learned 1D decoder | Per-column classifier | Univ. of Pittsburgh US11334731 (active) | Missing, and avoid | - | - |
| Validate | Length lock, check digits, GS1 AI parse | All | Have (`rules1d.cpp`, `Gs1Check.cs`) | - | - |
| **Real images** | Measure on real side-camera labels | - | **Missing: no real 1D image has been measured** | High (unknown until measured) | L (owner frames) |

## 12. Other symbologies and DPM

| Item | What the leaders do | Ours | Gain | Effort |
| --- | --- | --- | --- | --- |
| PDF417 | zxing-cpp; HALCON `module_width_min` | Have: row fusion, GF(929) RS, cross-check | Low | - |
| Aztec | zxing-cpp; HALCON `finder_pattern_tolerance` | Have (zxing-cpp only) | Low | - |
| DotCode | HALCON (module gap); no open-source decoder; patents US2019/0130236 [S], CN113297872A [S] | Have: own decoder (`dotcode.cpp`), synthetic tests only | Low until a line uses it | - |
| Pharmacode | No published vendor method | Have: own (`pharmacode.cpp`) | Low | - |
| DPM mode | Mark type, dot size, curved surface; dark-field light; ISO TR 29158 grading | Partial: filters built, user parameters specified only; Hand Held Products US10650205 (DPM, active) to check before a DPM mode ships | Low today (no DPM parts on the lines) | M |
| rMQR, Han Xin, MaxiCode | zxing-cpp reads rMQR and MaxiCode | Missing | Low | S |

## 13. Aggregation, diagnosis and Code Verify (Tools 5.5, 5.6, 6)

| Process step | Method | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Expected count, slot map | Count drives effort; lattice of read centres finds empty slots | Dynamsoft expected count [S]; NI / DALSA fixtures | Have: `slots.cpp` | - | - |
| Parent-child link | Use the parent's list to rescue a damaged child | Keyence SR-X station linking | Partial: duplicate memory and product master; ERP list missing (R8) | High | M |
| Miss reasons | Sharpness, finder, quiet zone, contrast per miss | Cognex US9552506 (expired) | Have: `diagnose.cpp` | - | - |
| Grade 2D, 1D, DPM | ISO/IEC 15415, 15416, TR 29158 | Cognex DM475V needs its own light for an ISO grade [S]; Matrox MIL ([article](https://www.vision-systems.com/boards-software/article/16746638/image-processing-software-from-matrox-imaging-features-improved-code-reading-functions)) | Have: `quality.cpp`, `grade1d.cpp`, `grade_dpm.cpp`; camera-relative unless calibrated | - | - |
| Check against a certified verifier | Same codes graded by both | - | Missing | Med (trust in grades) | L (verifier) |
| GS1 data | Syntax dictionary, check digits, dates, associations | GS1 | Have | - | - |

## 14. OCR and OCV (Tool 7)

### 14.1 OCR

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Find text lines | Fixed region with angle and skew range; DL word detector | Cognex OCRMax; HALCON Deep OCR ([page](https://www.mvtec.com/knowledge-base/technologies/deep-learning/methods/deep-ocr)); [docTR](https://github.com/mindee/doctr) | Have: letter-by-letter line finder (`lines.cpp`) | - | - |
| Line layout prior | Expected block structure, for example "2 2 2-4" with separators | HALCON `text_line_structure` ([set_text_model_param](https://www.mvtec.com/doc/halcon/2505/en/set_text_model_param.html)) | Missing (format check is per line, not a layout prior for cutting) | Med | S |
| Slant and baseline | Shear search; baseline tolerance | HALCON; Cognex OCVMax shear | Have: `slantOf` | - | - |
| Dot joining | Max gap between dots in a letter, min gap between letters, **tight-spacing flag** | HALCON `dot_print_*`; OCRMax fragment rules; Kodak US5212741 / US5357581 (expired) | Have: learned dot size, dot-grid compare, fixed-pitch cutting; tight-spacing case not handled as such | Med (CIJ) | S |
| Print-type models | Separate pretrained classifiers: DotPrint, Pharma, Industrial, OCR-A/B, SEMI, each with a reject class | HALCON ([create_text_model_reader](https://www.mvtec.com/doc/halcon/2505/en/create_text_model_reader.html)) | Partial: one CRNN for all print types | Med | M |
| Recogniser | CRNN + CTC; transformer (PARSeq); pretrained omni-font | [CRNN](https://arxiv.org/abs/1507.05717); [PARSeq](https://arxiv.org/abs/2207.06966); Cognex ViDi Read [S]; Zebra DL-OCR [S] | Have: CRNN ONNX; classical taught font | - | - |
| Second, independent reader | Two readers must agree before a read is trusted | Our design choice (no vendor source found) | Have (PR #162): agreement rule in Read text (second model, taught font or AI, else the shifted re-read); an independently trained second model (reader B) missing | Med (safety) | S (rule) + M (reader B) |
| Training data | Rendered fonts, synthetic dot print with dropout; real line images | SynthText [S]; dot-matrix papers [S]; HALCON trains Deep OCR from labelled images | Partial: synthetic only, plus 389 real laser lines | **High** (CIJ and embossed are synthetic-only) | M + owner images |
| Training detail | CTC loss normalised by line length | HALCON 25.11 ([release notes](https://www.mvtec.com/products/halcon/documentation/release-notes-2511-0)) | Missing | Low-Med | S |
| Constrained decoding | Per-position character sets; format automaton; lexicon | OCRMax field strings; OCVMax key sets | Have: format automaton under CTC; lexicon (word list) missing | Low | S |
| Confidence and reject | Gap between best and second; reject class; word-level temperature scaling | OCRMax confusion threshold; HALCON `_Rej`; [Slossberg](https://arxiv.org/abs/2012.12643) | Have (PR #162): line confidence (CTC probability of the whole read) calibrated on our validation sets (gamma 0.9), tested on the synthetic and real Harpic sets | - | - |
| Auto-tune segmentation | Choose segmentation parameters by comparing with the known text | Cognex US9317767 / US9286534 (active to 2033) | Our teach asks only about unsure letters; keep tuning by confidence, not by matching the known text (section 16) | - | - |

### 14.2 OCV

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Reference text | Render the expected text from a font file, including printer makers' fonts (Domino, Videojet, Markem) | Cognex OCVMax ([doc](https://docs.cognex.com/vpro_930sr1/web/help/html/c2da0f57-4fd9-4938-9a5e-14f61d0edb9d.htm)) | Partial: rendered system font (`FontRender.cs`); printer fonts missing | Med (CIJ, TTO) | S-M |
| Per-letter freedom | Shift, angle, scale and shear per string and per letter | OCVMax | Partial: allowed shift; no per-letter scale or shear | **High** (laser true-text pass 69 %) | M |
| Look-alike check | Confusion matrix against similar letters; "confused" margin about 0.1 = fail | OCVMax | Have: forced alignment, per-letter score; changed or look-alike text 0 accepted | - | - |
| Print quality | Contrast, stroke width, missing dots, letter similarity; limits from good prints | OCVMax contrast and stroke width; Nagai US4872024 (dot counts, expired) | Have: contrast, ink, pitch, position, limits mean ± k·σ from 10+ good parts | - | - |
| Clean-print mode | Stricter limits for stable printers | OCVMax | Missing | Low | S |
| Limits from real prints | Learn limits on the line's own good prints, not synthetic | Keyence IV4 AI OCV (learns variation, no data) | Partial: learning built; not run on real CIJ / TTO prints | **High** (false rejects) | S + owner images |
| Numeric print-quality standard | none published for OCV (unlike ISO 15415 for codes) | - | - | - | - |

## 15. Inspect, Colour, AI tools, Teach and auto-tune

### 15.1 Inspect (Tool 8)

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Golden template | Register, average good parts into templates indexed by pose, subtract, blob the difference | Cognex US5850466 (expired); Cognex [PatInspect](https://docs.cognex.com/vpro_970/web/EN/help/html/N_Cognex_VisionPro_PatInspect.htm) (brighter-only and darker-only modes) | Missing | Med (label print, cap) | M |
| Variation model | Ideal image and per-pixel variation; fail where the difference passes both an absolute and a k·σ limit, set apart for too bright and too dark | HALCON [prepare_variation_model](https://www.mvtec.com/doc/halcon/13/en/prepare_variation_model.html) | Missing (OCV print quality uses mean ± k·σ per letter) | Med | M |
| Scratches and hairs | Bank of oriented band-pass filters, keep the strongest response and its angle | Cognex US6983065 (expired) | Missing | Low | M |
| Embossed and dented | Shape image from directional lights | LumiTrax, HALCON, Omron (section 3) | Missing | Med (embossed text) | L (lights) |
| Bead and glue | Edge pairs along a path, width profile | Omron FH (no method published); Cognex and HALCON measure tools | Missing | Low | M |
| AI defects | See 15.3 | Omron FH AI, MVTec Global Context | Missing | Med | L |

### 15.2 Colour (Tool 9)

| Process step | Method or filter | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| White reference | Per-channel flat field from a white target | Euresys | Missing (engine is grey only) | Low | S |
| Colour difference | CIEDE2000 in Lab, tested against the Sharma test pairs; print limit about ΔE 2.0, strict 0.5 | [Colour difference](https://en.wikipedia.org/wiki/Color_difference); Sharma notes (PDF not readable) | Missing | Low today (grey cameras) | S |
| Vendor colour tools | Cognex ColorMatch, Keyence, Sherlock colour | No vendor colour document could be read | - | - | - |

### 15.3 AI deep-learning tools (Tool 10)

| Process step | Method | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Anomaly from good images | PatchCore (kNN on patch features); EfficientAD (student-teacher); Dinomaly (DINOv2, one model for all products) | [PatchCore](https://arxiv.org/abs/2106.08265); [EfficientAD](https://arxiv.org/html/2303.14535); [Dinomaly](https://arxiv.org/abs/2405.14325) (Apache-2.0); [anomalib](https://github.com/open-edge-platform/anomalib); HALCON, Zebra, Teledyne [Astrocyte](https://www.teledynevisionsolutions.com/products/astrocyte-ai/), Omron, Keyence | Missing: `ml/experiments/anomaly.py` caught 0 / 56 torn labels at 0 false alarms, not shipped | Med (label and cap checks) | L |
| Logical anomaly | Count, position and order of parts (local + global nets) | HALCON Global Context ([doc](https://www.mvtec.com/doc/halcon/2511/en/toc_deeplearning_anomalydetection.html)) | Partial: slot map for codes only | Med | M |
| Few-image classify | Frozen DINOv2 features + small head; continual learning; out-of-distribution "not sure" | [DINOv2](https://arxiv.org/abs/2304.07193); HALCON 25.11 `fit_dl_out_of_distribution` | Missing | Low for reading | M |
| Counting | Point-based counting from a few labelled objects | MVTec Deep Counting [S] | Missing (codes counted by the reader) | Low | M |
| Labelling | Click-to-mask, text-prompted pre-labels on the trainer PC | [SAM 2](https://github.com/facebookresearch/sam2) (Apache-2.0); [Grounding DINO](https://arxiv.org/abs/2303.05499); [OWLv2](https://arxiv.org/abs/2306.09683); [Florence-2](https://arxiv.org/abs/2311.06242) | Partial: auto-labels from engine reads (`autolabel.py`) | Low | M |
| Synthetic defects | CutPaste, DRAEM, AnomalyDiffusion | [CutPaste](https://arxiv.org/abs/2104.04015); [DRAEM](https://arxiv.org/abs/2108.07610); [AnomalyDiffusion](https://arxiv.org/abs/2312.05767) | Have for codes (`violations.py`) and text (`synth_*.py`) | - | - |
| CPU speed | Static INT8 (QDQ) in ONNX Runtime; OpenVINO NNCF with about 300 calibration images | [ONNX Runtime](https://onnxruntime.ai/docs/performance/model-optimizations/quantization.html); [NNCF](https://github.com/openvinotoolkit/nncf) | Missing: OpenCV DNN only, no INT8 gate | Med (time) | M |
| Calibration, drift | Temperature scaling; energy score; two-sample test on features; CUSUM | [Guo](https://arxiv.org/abs/1706.04599); [Liu](https://arxiv.org/abs/2010.03759); [Rabanser](https://arxiv.org/abs/1810.11953) | Partial: OCR temperature only; drift monitor missing | Med (safety) | M |

Licence: Ultralytics YOLO models are AGPL-3.0 even when trained from scratch
([licence](https://www.ultralytics.com/license)); use Apache-2.0 or MIT detectors (RT-DETR, YOLOX,
D-FINE) as AI-DEEP-LEARNING.md already says.

### 15.4 Teach, auto-find, auto-learn, auto-tune

| Process step | Method | Who uses it | Ours | Gain | Effort |
| --- | --- | --- | --- | --- | --- |
| Pick the teach images | Auto image selector; images suggested to learn | Keyence VS-G ([functions](https://www.keyence.com/products/vision/vision-sys/vs-g/functions/)); Omron FH [S] | Missing (diversity ordering planned in RDV #13) | Low | S |
| Find every code and text | Reader probe read sets type, count, lengths | Ours | Partial: codes (`CodeTeach`); text and fields not auto-found | Med (setup time) | M |
| Re-run stored images after any change | List every verdict that changed | Keyence VS-G image database | Partial: the model gate does it for models, not for parameter changes | Med (safety) | S |
| Threshold sweep | Sweep a threshold, score each result, keep the best | MVTec US7953290 (Google: **expired** 2026-04-21; confirm) | Partial: bandit tuner (`TuningAgent.cs`) | Low | S |
| Parameter search | Bayesian search with pruning; never lower the catch of bad parts | [Optuna](https://arxiv.org/abs/1907.10902) | Partial: Thompson-sampling bandits | Med | M |
| Parameters from labelled examples | Robust combination per pyramid level | MVTec EP4636707 (pending) | Avoid this form (section 16) | - | - |


## 16. Patents to check before building or exporting

For legal review, not legal advice. Expired patents are free for anyone to use. Earlier entries
are in [PATENTS.md](../PATENTS.md) and [RESEARCH-COGNEX-PATENTS.md](../RESEARCH-COGNEX-PATENTS.md);
these are new.

### 16.1 Touches something we already have

| Patent | Owner | Claim, one line | Status | Our part | Engineering note |
| --- | --- | --- | --- | --- | --- |
| [US11854163](https://patents.google.com/patent/US11854163B2/en) | Keyence | Claim 1: a reading device whose housing holds the camera, a store with a neural net trained in advance on pairs of defective and ideal code images, and a processor that restores the code image with the net and decodes the restored image | Active to 2042-03-15 (JP priority 2021) | AI enhancer (`enhancer.onnx`, trained on `violations.py` pairs; `CR_STEP_AI_ENHANCE`) | Closest risk found. Our camera and PC are separate units, which may matter for a claim to one housing. Ask counsel before export to the US; keep the enhancer behind a territory switch like `CR_UNDERRES`; measure what it rescues on real crops (R12) and drop it if little |
| [US12039400](https://patents.google.com/patent/US12039400) | Keyence | Net inference and a first decode run in parallel, second decode on the generated image | Active to 2041-12-06 | Enhancer placement | Keep the enhancer strictly after normal decoding fails (today's "Last" placement), not in parallel |
| [US12299532](https://patents.google.com/patent/US12299532) | Keyence | Capture, binarise and decode a code on a workpiece (claims not read) | Active to 2043-12-04 | Binarisation steps | Claims to be read |
| [US9317767](https://patents.google.com/patent/US9317767), [US9286534](https://patents.google.com/patent/US9286534) | Cognex | Segmentation parameters chosen automatically by checking the segmented result against the known text | Active to 2033 | OCR teach and auto-tune | Tune by recognition confidence on good prints, not by matching segmentation to the known text |
| [US10068153](https://patents.google.com/patent/US10068153) | Cognex | Handheld scanner trains OCR from a synthetic image made from a marker font file | Active to 2032-08-21 | Rendered system font (PR #108) | Claim is to a handheld scanner; review the wording |
| [US12182721](https://patents.google.com/patent/US12182721B2/en) | Yissum (Hebrew Univ.) | Anomaly where the distance of patch features to their k nearest normal features passes a threshold (the core of SPADE and PatchCore) | Active to about 2042-02-25 | Planned PatchCore anomaly tool | Counsel before PatchCore ships |
| [US12548135](https://patents.google.com/patent/US12548135B2/en), [EP4145401](https://patents.google.com/patent/EP4145401A1/en) | MVTec | Encoder-decoder plus a regression net; anomaly where they disagree (student-teacher) | US active to about 2043; EP pending | Planned EfficientAD-style tool | Counsel before it ships |

### 16.2 Avoid when building the next methods

| Patent | Owner | Claim, one line | Status | Avoid |
| --- | --- | --- | --- | --- |
| [US12210930](https://patents.google.com/patent/US12210930) | Code Corp | A model predicts decodability; images predicted undecodable are discarded | Active to 2043 | Any decodability pre-filter (also against line safety) |
| [US9361503](https://patents.google.com/patent/US9361503) | Datalogic | Blurred symbol mapped to a blur-robust space and classified | Active to 2034 (EP too) | A learned 1D classifier in such a space |
| [US11334731](https://patents.google.com/patent/US11334731) | Univ. of Pittsburgh | Per-column classifier predicts 1D characters from rows | Active to 2039 | Learned per-column 1D decoder |
| [US8596540](https://patents.google.com/patent/US8596540) | Scandit | 1D from blurred image compared with stored blurred reference patterns | Active to 2030 (EP and others) | Stored blurred-pattern tables; our fit renders candidates through the measured blur and is to be reviewed |
| [US11176454](https://patents.google.com/patent/US11176454) | SICK | ML classifier assigns code information to code regions | Active to 2040 (EP, JP, KR, CN) | Review before EU or CN export |
| [US10650205](https://patents.google.com/patent/US10650205) | Hand Held Products | Scanning and decoding DPM marks (claims not read) | Active to 2038 | Read the claims before a DPM mode ships |
| [US9208367](https://patents.google.com/patent/US9208367) | Honeywell | Several codes shown with decoded ones marked; user sets the send order | Active to 2034 | A user-ordered send screen |
| [US11922269](https://patents.google.com/patent/US11922269) | Bayer | Codes introduced into surfaces and read | Active to 2042 | Read the claims (pharma owner) |
| [US10810465](https://patents.google.com/patent/US10810465B2/en) | Datalogic | Auto-encoder reconstruction error rejects a classifier result | Active to 2039 | That reject; use the confusion gap instead |
| [US9384423](https://patents.google.com/patent/US9384423B2/en) | Xerox | Confidence from image and text embedding similarity | Active to about 2034 | That confidence |
| [US10628934](https://patents.google.com/patent/US10628934) | Videojet | Printer with an OCR trained per print technology | Active to about 2037 | Claim ties to a printer; review if we sell to printer makers |
| [US11367225](https://patents.google.com/patent/US11367225) | Keyence | Rule-based check first, deep-learning classifier only on the unclear rest | Active to about 2040 | Counsel before a classical-then-AI cascade |
| [US11562479](https://patents.google.com/patent/US11562479) | Seiko Epson | User marks a defect as allowable, model rebuilt to accept it | Active to about 2040 | That UI loop |
| [US12482244](https://patents.google.com/patent/US12482244B2/en), [US11836909](https://patents.google.com/patent/US11836909B2/en), [US12361100](https://patents.google.com/patent/US12361100B1/en), [US12315277](https://patents.google.com/patent/US12315277B2/en) | 3M, Mellanox, Cognex | Automatic selection of images to label; auto-labels from similar images; run-time images suggested for training | Active to 2040-43 | Keep labelling operator-started, offline, with a gate |
| [EP4636707](https://patents.google.com/patent/EP4636707A1/en) | MVTec | Parameters estimated from labelled examples, combined robustly per pyramid level | Pending | That form of auto-learn |
| [US8971663](https://patents.google.com/patent/US8971663) | Cognex | Synthetic golden template for multi-layer print: layer masks, poses, blended intensities | Active to about 2032 | That pipeline for layered print inspection |
| [US8422759](https://patents.google.com/patent/US8422759) | Omron | Edge-code histogram for the coarse search, then edge-code images | Active to 2031 | Edge-code histograms in the Locate coarse stage |
| [US9286669](https://patents.google.com/patent/US9286669) | Omron | Model of edge positions and directions with a user-set allowed change of direction | Active to 2034 | Tolerating deformation by loosening edge direction; use a displacement field or sub-models |

### 16.3 Free to use (expired)

[US8150163](https://patents.google.com/patent/US8150163) Scanbuy, multi-frame super-resolution;
[US9195873](https://patents.google.com/patent/US9195873) Intel, small codes from blurred images;
[US5212741](https://patents.google.com/patent/US5212741A/en) and
[US5357581](https://patents.google.com/patent/US5357581A/en) Kodak, dot-matrix preprocessing;
[US4872024](https://patents.google.com/patent/US4872024) dot-count print inspection;
[US7953290](https://patents.google.com/patent/US7953290B2/en) MVTec, automatic threshold (Google:
expired 2026-04-21; confirm). For Locate, Measure and Inspect: MVTec shape-based matching
[US7062093](https://patents.google.com/patent/US7062093B2/en) (2024) and
[US7239929](https://patents.google.com/patent/US7239929B2/en) (2024); Cognex PatMax-family
[US7016539](https://patents.google.com/patent/US7016539B1/en) and
[US7065262](https://patents.google.com/patent/US7065262B1/en) (2018), sub-model gauging
[US6993177](https://patents.google.com/patent/US6993177) (2023), oriented filters
[US6983065](https://patents.google.com/patent/US6983065B1/en) (2023), golden template
[US5850466](https://patents.google.com/patent/US5850466) (2015); Nikon polar matching
[US6640008](https://patents.google.com/patent/US6640008) (2021). Keyence pattern search
[US8401305](https://patents.google.com/patent/US8401305) and
[US8406527](https://patents.google.com/patent/US8406527) show "expired, fee related": confirm.
No patent was found for PatMax RedLine, PatFlex or the HALCON variation model. Honeywell [US8702003](https://patents.google.com/patent/US8702003)
shows inconsistent dates: counsel to check.

## 17. Ranked list: the missing methods most likely to reach 100 %

For QR, Data Matrix, 1D and laser OCR / OCV on the owner's lines. Ranked by the misses each
addresses today (section 1), then by effort. Every item is accepted only when the bench shows
more reads with 0 wrong at the line time targets.

The **consolidated backlog across all 13 tools** (0–12, including a test per method, the current
build state with PR numbers, the line-safety note and the ViDi-style AI plan) is in
[TOOL-METHODS-BACKLOG.md](TOOL-METHODS-BACKLOG.md). It supersedes this reader-only list as the
pick-from backlog; the rows below stay as the measured reader detail.

| # | Method | Addresses | Source | Gain | Effort |
| ---: | --- | --- | --- | --- | --- |
| 1 | **Lighting and optics per product, measured on the line:** cross-polarised light for shrink wrap, dark-field for dark Data Matrix caps, case filling the frame for at least 2.5 px per module; stored per product | LOW_CONTRAST (about 20 per shrink-wrap image, 26 on the dark-cap image), TOO_SMALL | Cognex, Microscan, HALCON (section 3); R0 / R13 of RESEARCH-NEXT-GENERATION | High | L (line time) |
| 2 | **Module-scaled local contrast** in place of fixed-tile CLAHE, in our own form. Measured 2026-10-31 (PR #169): no gain (1733 -> 1732 reads), not adopted. The LOW_CONTRAST misses are background regions; the unread shrink-wrap labels are never located (rank 8) | LOW_CONTRAST on QR and DM | HALCON `contrast_tolerance`, OCRMax local normalisation; Cognex US10607047 (design around) | High | S-M |
| 3 | **ERP print list as exact candidates** for known-content and serial-run decoding from the first code | COVERED, LOW_CONTRAST on serial products | R8; Keyence station linking | High | M |
| 4 | **Bracketed exposures on one trigger**, fused by the multi-frame reader with saturation masks | Glare and dark areas in one image | Cognex US9104932 / US9734376 (own form, already designed) | High | M |
| 5 | **Data Matrix with a damaged or missing L:** hypotheses over the learned symbol size, modules outside the image as erasures | 48 DM NO_FINDER, COVERED | HALCON `finder_pattern_tolerance`; Cognex US10956697 (design around) | High | M |
| 6 | **Multi-frame super-resolution** from the burst (the under-resolved solver is already on by default). Built 2026-10-31 (PR #175): after the fused modules fail on a QR below 2.6 px per module, the code region is registered to a tenth of a pixel and reconstructed 2-4x finer by back-projection, then read. Drawn 3-frame bursts: no gain from 1.2 px per module up (the solver already reads them), 8 -> 9 of 10 at 1.1 and 0 -> 4 of 10 at 1.0, 0 wrong, about 135 ms per region; real bursts not yet recorded | TOO_SMALL shrink-wrap labels | Scanbuy US8150163 (expired); Cognex US11151346 (no IN member found) | Med | M |
| 7 | **Deblur with the measured PSF per zone** (the PSF is measured today but used only to re-score) | BLUR (33 on the 24- and 48-code QR batches) | Classic Wiener / RL; `psf.cpp` | Med | S |
| 8 | **Locator recall measured** on our misses and on the public BarBeR set | NO_FINDER | BarBeR | Med | S |
| 9 | **Fewest-symbols ranking and two-frame confirmation** for low-evidence reads | Safety: keeps 0 wrong as reads rise | NI demodulation mode; Scanbot | Med | S-M |
| 10 | **Real 1D images** from the side camera, then joining intact segments of several scan lines for torn bars | 1D is unmeasured | Scanbot | High (unknown) | S + owner frames |
| 11 | **Laser OCV per-letter freedom** (scale and shear per letter) and printer-maker fonts as the reference | Laser true-text pass 69 % (false rejects) | Cognex OCVMax | High; measured 2026-10-31 (PR #166) on the AI read: unsafe per letter, +0.5 point per line; keep for reference glyphs (printer fonts) | M |
| 12 | **OCV limits learned on real good prints** from the line | False rejects | Keyence IV4; our learning, never run on real prints | High | S + owner images |
| 13 | **AI OCR trained on real line images** with length-normalised CTC loss, and print-type models (dot print, pharma) | CIJ 72.9 %, embossed 89.4 % (synthetic) | HALCON Deep OCR, DotPrint and Pharma classifiers | Med-High | M + owner images |
| 14 | **Two independent text readers must agree**, plus word-level confidence calibrated on our OK / NG set | Safety for OCR | Slossberg; HALCON reject class | Med | S |
| 15 | **Text-line layout prior** ("2 2 2-4") and a tight-spacing setting for dot print | CIJ letter cutting | HALCON `text_line_structure`, `dot_print_tight_char_spacing` | Med | S |

Items 1, 10, 12 and 13 need time on the line or images from the owner. Item 4's form and item 6's
switch need the patent notes in section 16 and [PATENTS.md](../PATENTS.md) read first.

## 18. Limits of this study

- Several vendor pages blocked fetching (Cognex technology and Edge Learning pages, NI, Zebra,
  Omron, Teledyne DALSA product pages); facts from them are marked [S]. Teledyne DALSA, Zebra
  Aurora, Datalogic and Honeywell publish no reading algorithm we could find.
- Patent claims were read in full only for Keyence US11854163; the others are summarised from
  abstracts. No India (IN) search was run.
- No method here was tried on our images in this study. The gains are forecasts.
- The ISO 15415 / 15416 / 29158 formulas were not re-researched; the engine already implements
  them (section 13).
