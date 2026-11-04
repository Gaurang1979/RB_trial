# 04a Tools: Base, Locate, Measure, Calibrate, Blob and the correction layer

Part of the [Vision System study](00-INDEX.md). Tools 0 to 4 of
[TOOL-SPECIFICATIONS.md](../TOOL-SPECIFICATIONS.md), plus every tool the leaders have that our spec
lacks (marked **NEW**), plus the image-correction layer L1 as one service that every tool uses.
This chapter extends, and does not repeat, sections 3 to 7 of
[INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md). Layer names L0-L6 are those of the
[index](00-INDEX.md). Status words: **Have**, **Partial**, **Missing**. Effort in person-weeks (pw)
for one experienced developer with an AI assistant.

Patented methods are written as methods to adopt, with the patent cited (owner's statement of
2026-10-30, legal clearance). Status and expiry are given as facts. Vendor screens are described in words with a
link; nothing from a vendor manual is copied.

## Where we stand today (checked in the code, 2026-10-30)

| Area | What the code does | Gap to the requirement |
| --- | --- | --- |
| Locate | [engine/include/vs_locate.h](../../../engine/include/vs_locate.h), [engine/src/fixture/locate_tool.cpp](../../../engine/src/fixture/locate_tool.cpp): grey-level NCC (`cv::matchTemplate`, TM_CCOEFF_NORMED) at about 64 px pattern size, then a finer search; 1D parabola per axis for the peak; angle by parabola over angle steps; no scale, no mask, no polarity option; `LocateToolConfig` in [dotnet/VisionCore/Tools/LocateTool.cs](../../../dotnet/VisionCore/Tools/LocateTool.cs) has search area, max angle 15°, min score 0.6 | [engine/tests/locate_tests.cpp](../../../engine/tests/locate_tests.cpp) passes at **< 1.0 px and < 0.5°** error. Requirement section 13 asks **0.05 px and 0.05°** and 100 % find: 20× tighter in position, 10× in angle. No edge-based matcher exists. |
| Measure | Nothing. `cv::Canny`, `fitLine`, `fitEllipse`, `HoughCircles` are not called anywhere in `engine/src`. Edge profiles exist only for 1D bar widths (`linear.cpp`, `decodability.cpp`) | Requirement: 0.1 px (3σ) repeatability. All of Tool 2 is Missing. |
| Calibrate | [engine/src/calibration.cpp](../../../engine/src/calibration.cpp): `findChessboardCorners` + `cornerSubPix` (5×5 window) + `calibrateCamera` (k1, k2, p1, p2, k3); a lens model is kept when RMS ≤ **1.0 px**; undistortion of **points** only (fixed-point inverse); mm per pixel from the last view; flat field from a white card at 1/8 resolution. Built behind `CR_HAVE_CALIB` (OpenCV 5 moved calibration into its own `calib` module) | Acceptance at 1.0 px is 10× looser than the HALCON rule of about 0.1 px. No image undistort, no N-point, no hand-eye, no dark frame, no plate pose, no validity hash. Used only for grading. |
| Base | Pre-processing lives inside the code reader as `CR_STEP_*` (flat, CLAHE, unsharp, adaptive threshold, Richardson-Lucy deblur, glare, upscale) in `decode.cpp`; `cv::` functions used: `GaussianBlur` (36), `Sobel` (17), `morphologyEx` (16), `threshold` (14), `remap` (13), `warpAffine` (5), `warpPerspective` (2), `adaptiveThreshold` (4), `createCLAHE`, `phaseCorrelate`, `dft` | No user Base tool. Only three tool classes exist in `dotnet/VisionCore/Tools` (Code, Text, Locate). |
| Blob | `cv::connectedComponentsWithStats` (10 uses) and `findContours` (3) inside the reader (dots, DotCode, Micro QR, 1D bands) | No Blob tool. |
| Camera | [dotnet/HikCamera/HikCamera.cs](../../../dotnet/HikCamera/HikCamera.cs): Hikrobot MVS, trigger, exposure, gain, strobe; auto-exposure search `dotnet/CodeReader/CalibrationSearch.cs`; test burst `TestBurst.cs`; multi-frame fusion `engine/src/multiframe.cpp` | No GenICam path for other makers, no sequencer HDR, no polarisation, no multi-light. |

## Summary table

Accuracy targets are repeatability (3σ over 100 captures of a still part) unless "accuracy" is
said, which is the error against a known truth. Speed is on T1 (i5, 6 cores, 5 MP) from
requirements section 11; "line PC" is the owner's i7 with the 20 MP MV-CS200 (5472 × 3648).

| Id | Name (operator words) | Layers | Today | Accuracy target | Speed target | pw | Phase | Depends on |
| --- | --- | --- | --- | --- | --- | ---: | --- | --- |
| L1 | Correction service | L1, L4, L5 | Partial | adds ≤ 0.01 px bias | ≤ 6 ms 5 MP (remap), 0 ms when applied to points | 3 | P1 | 3.1, 0.6 |
| 0.1 | Image source "Camera" | L0, L6 | Have (Hikrobot) | 0 dropped frames | trigger → exposure ≤ 2 ms | 1.5 | P1 | — |
| 0.2 | Camera settings "Camera" | L0, L5 | Partial | brightness ± 5 % of taught | auto setup ≤ 3 s | 1.5 | P1 | 0.1 |
| 0.16 | **NEW** Multi-exposure / HDR / sequencer "Light and dark" | L0, L1 | Partial | no saturated module in 99.9 % of frames | 2-4 frames + merge ≤ 15 ms | 2 | P2 | 0.1, multiframe |
| 0.17 | **NEW** Polarisation "Remove shine" | L0, L1 | Missing | glare area −80 % on foil | ≤ 5 ms 5 MP | 1.5 | P3 | polarised camera |
| 0.18 | **NEW** Photometric stereo / multi-light "Show relief" | L0, L1 | Missing | embossed text contrast ×3 | 4 frames + ≤ 20 ms | 3 | P3 | 0.16, light controller |
| 0.3 | Image convert "Clean image" | L1 | Missing | lossless | ≤ 4 ms demosaic | 0.5 | P1 | — |
| 0.4 | Crop / ROI "Clean image" | L1, L2 | Partial | pose error ≤ 0.01 px | ≤ 1 ms 1 MP | 0.5 | P1 | 1.4 |
| 0.5 | Histogram "Camera" | L4, L5 | Partial | exact | ≤ 1 ms | 0.5 | P1 | — |
| 0.20 | **NEW** Image check (frame gate) "Is the picture good?" | L0, L4 | Partial | 100 % of bad frames NG | ≤ 2 ms | 1 | P1 | 0.5 |
| 0.6 | Flat-field incl. dark frame "Even light" | L1 | Partial | residual shading ≤ 2 % | ≤ 6 ms | 1 | P1 | — |
| 0.7 | Filters incl. shock filter "Clean image" | L1 | Partial (internal) | — | Gauss 3×3 ≤ 2 ms | 1.5 | P1 | — |
| 0.8 | Morphology "Clean image" | L1 | Partial (internal) | exact | ≤ 5 ms | 0.5 | P1 | — |
| 0.9 | Threshold incl. auto "Black and white" | L1, L3 | Partial (internal) | Otsu exact | ≤ 2 / 6 ms | 1 | P1 | 0.5 |
| 0.10 | Image arithmetic "Clean image" | L1 | Missing | exact | ≤ 2 ms | 0.5 | P1 | — |
| 0.11 | Polar unwrap "Unroll ring" | L1 | Missing | ≤ 0.05 px mapping error | ≤ 3 ms 1 MP | 1 | P1 | 2.3 |
| 0.19 | **NEW** Cylinder unwrap "Unroll bottle" | L1 | Missing (reader tolerates 138°) | arc-length error ≤ 0.5 % | ≤ 5 ms | 2 | P2 | 2.2, 3.1 |
| 0.12 | Affine / perspective "Straighten" | L1 | Missing as tool | ≤ 0.01 px | ≤ 5 ms 5 MP | 1 | P1 | 1.4 |
| 0.13 | Overlay | L6 | Have (UI) | — | ≤ 0.5 ms | 0.5 | P1 | — |
| 0.14 | Result logic "OK / NG" | L4, L6 | Partial | Error ⇒ NG always | ≤ 0.1 ms | 1 | P1 | — |
| 0.15 | Logging "Save results" | L6 | Have | 0 lost records | never blocks | 0.5 | P1 | — |
| 1.1 | Correlation match "Find the part" | L2 | Have (needs work) | 0.05 px / 0.05° , 100 % find | ≤ 15 ms ±15°, ≤ 40 ms ±180° | 2 | P1 | 2.10 |
| 1.2 | Geometric match "Find the part" | L2 | Missing | 0.05 px / 0.05°, 100 % find, 30 % occlusion | ≤ 30 ms ±180° | 6 | P1 | 2.10, 2.11 |
| 1.2F | **NEW** Fast mode (RedLine-style) | L2 | Missing | ≤ 0.1 px / 0.1° | ≤ 10 ms ±180°, scale 0.8-1.2 | 2 | P2 | 1.2 |
| 1.2A | **NEW** Anisotropic scale | L2 | Missing | 0.05 px, scale 0.001 | ≤ 1.5 × 1.2 | 1.5 | P2 | 1.2 |
| 1.6 | **NEW** Local deformable "Find bent part" | L2, L1 | Missing | ≤ 0.2 px per mesh node | ≤ 80 ms | 4 | P3 | 1.2 |
| 1.7 | **NEW** Descriptor / perspective "Find printed face" | L2 | Missing | homography ≤ 0.3 px corners | ≤ 50 ms | 3 | P3 | — |
| 1.3 | Multi-instance "Find all parts" | L2, L4 | Missing | 100 % count | ≤ 1.5 × single for 50 | 1.5 | P1 | 1.1/1.2 |
| 1.4 | Fixture | L2 | Partial | ≤ 0.01 px propagation | ≤ 0.05 ms | 1 | P1 | 1.x |
| 1.5 | AI locator | L2 | Missing (CodeNet is codes only) | 100 % find, ≤ 0.05 px after refine | ≤ 80 ms T1, ≤ 25 ms T2 | 4 | P3 | 1.2, Tool 10 |
| 2.10 | **NEW** Sub-pixel edge engine | L3 | Missing | bias ≤ 0.02 px, σ ≤ 0.02 px on clean edge | ≤ 15 ms 5 MP contour | 2 | P1 | — |
| 2.11 | **NEW** Robust fit library | L3, L4 | Missing | exact on test vectors | ≤ 0.2 ms / 1000 pts | 1.5 | P1 | — |
| 2.1 | Caliper "Check size" | L3 | Missing | 0.05 px (3σ) clean, 0.1 px line | ≤ 0.2 ms | 1.5 | P1 | 2.10 |
| 2.2 | Find line "Check edge" | L3 | Missing | 0.05 px / 0.02° | ≤ 1 ms | 1 | P1 | 2.1, 2.11 |
| 2.3 | Find circle / arc "Check hole" | L3 | Missing | diameter 0.05 px | ≤ 1.5 ms | 1 | P1 | 2.1, 2.11 |
| 2.4 | Find ellipse "Check hole" | L3 | Missing | axes 0.1 px | ≤ 2 ms | 1 | P1 | 2.3 |
| 2.5 | Find corner "Check corner" | L3 | Missing | 0.07 px | ≤ 2 ms | 0.5 | P1 | 2.2 |
| 2.6 | Geometry "Check distance / angle" | L3, L4 | Missing | exact arithmetic | ≤ 0.05 ms | 1 | P1 | 3.x |
| 2.7 | Gap "Check gap" | L3 | Missing | 0.1 px per width | ≤ 1 ms | 0.5 | P1 | 2.1 |
| 2.8 | Pitch "Check pins" | L3 | Missing | 0.1 px pitch | ≤ 1 ms | 1 | P1 | 2.1 |
| 2.9 | Edge count / thread "Count edges" | L3 | Missing | 100 % count | ≤ 0.5 ms | 0.5 | P1 | 2.1 |
| 2.12 | **NEW** Metrology model "Measure the part" | L2, L3 | Missing | as members | ≤ 5 ms 10 objects | 2 | P2 | 2.1-2.4 |
| 2.13 | **NEW** Steger lines "Find thin line" | L3 | Missing | centre 0.05 px, width 0.1 px | ≤ 10 ms 1 MP | 2 | P2 | 2.10 |
| 3.1 | Grid calibration + lens models "Set real size" | L1, L5 | Partial | RMS ≤ 0.1 px | ≤ 2 s 15 images | 2.5 | P1 | — |
| 3.2 | Undistort / rectify "Straighten image" | L1 | Missing | warp error ≤ 0.01 px | ≤ 10 ms 5 MP | 1 | P1 | 3.1 |
| 3.3 | N-point "Set real size" | L1 | Missing | residual shown per point | ≤ 1 ms | 0.5 | P1 | — |
| 3.4 | Scale "Set real size" | L1 | Partial | ≤ 0.1 % | instant | 0.5 | P1 | — |
| 3.5 | Hand-eye 2D "Teach robot" | L1, L6 | Missing | ≤ 2 px equiv. | ≤ 1 s | 2 | P4 | 1.2 |
| 3.6 | Stitching "Join cameras" | L1 | Missing | seam ≤ 0.2 px | ≤ 25 ms 2 × 5 MP | 2.5 | P4 | 3.1, Tool 11 |
| 3.7 | **NEW** Telecentric model | L1 | Missing | RMS ≤ 0.1 px | ≤ 2 s | 1 | P2 | 3.1 |
| 3.8 | **NEW** Line-scan calibration | L1 | Missing | RMS ≤ 0.2 px, speed ≤ 0.1 % | ≤ 5 s | 2 | P4 | 3.1 |
| 3.9 | **NEW** Calibration acceptance and drift check | L4, L5 | Missing | stale calib ⇒ NG 100 % | ≤ 1 ms per image | 1.5 | P1 | 3.1 |
| 4.1 | Blob labelling + features "Count / spot" | L3 | Partial (internal) | area ± 0.5 %, centroid 0.05 px | ≤ 10 ms 5 MP 1000 blobs | 2 | P1 | 0.9 |
| 4.2 | Contour "Check outline" | L3 | Missing | deviation 0.1 px | ≤ 5 ms 2000 pts | 1.5 | P1 | 2.10 |
| 4.3 | Count "Count items" | L3, L4 | Missing | 100 % count | as 4.1 | 1 | P1 | 4.1 |
| 4.4 | Filter and sort "Count / spot" | L3, L4 | Missing | exact | ≤ 0.2 ms / 1000 | 0.5 | P1 | 4.1 |
| 4.5 | **NEW** Texture segmentation "Find surface areas" | L3 | Missing | pixel accuracy ≥ 98 % | ≤ 20 ms 5 MP | 1.5 | P3 | 0.7 |
| T | **NEW** Geometry truth set and rig | L5 | Missing | — | — | 3 | P1 | — |
| | **Total** | | | | | **~89** | | |

The order of build is in [Build order](#build-order-for-this-chapter) at the end.

---

## L1 Correction service (shared by every tool) — no operator name

- **Purpose.** One place that knows how this camera, lens, light and product distort the image, and
  corrects it the same way for every tool, so a lens model taught for Measure also serves OCR, the
  code reader and Inspect. **Status: Partial**: pieces exist in `calibration.cpp` (point undistort,
  white-card flat field), `decode.cpp` (`CR_STEP_FLAT`, `CR_STEP_GLARE`, polarity flip, deblur),
  `psf.cpp` (blur sigma per zone), `learned.cpp` (per-product state). They are not one service and
  not used outside the reader.
- **Best in class.** HALCON keeps the camera model separate from the tools and offers both
  "rectify the image once" ([gen_image_to_world_plane_map](https://www.mvtec.com/doc/halcon/13/en/gen_image_to_world_plane_map.html),
  map computed once and reused) and "correct the measured points" (calibrated measure results).
  Cognex VisionPro passes a calibration space with every result and warns that the calibration
  setup must match the run setup ([checkerboard calibration](https://docs.cognex.com/vpro_980/web/EN/help/html/d3cfc90a-4145-4047-bcd3-fc84cd9d08ed.htm)).
- **Design rule (ours).** Geometry is corrected on **points**, not pixels, for Locate and Measure
  (an image remap smooths edges and costs up to 0.02 px; points cost nothing). Images are remapped
  only for tools that need a straight picture (OCR, golden template, polar/cylinder unwrap). Both
  paths use the same model, so a result in mm is identical whichever path was taken (unit test).

| Layer | Step | Method (source) | Options (name, values, default, auto-learned, level) | Output |
| --- | --- | --- | --- | --- |
| L1 | Dark and flat field | (Raw − Dark) × Gain, Gain = mean(Flat − Dark) / (Flat − Dark) per pixel ([Euresys FFC](https://documentation.euresys.com/Products/COAXLINK/COAXLINK_10_3/Content/03_Using_Coaxlink/functional-guide/flat-field-correction/what-is-ffc.htm)) | Flat: none / taught white / rolling; default rolling; learned yes; essential | even image or correction factors |
| L1 | Lens distortion | Brown-Conrady or division model from 3.1 ([HALCON calibrate_cameras](https://www.mvtec.com/doc/halcon/13/en/calibrate_cameras.html)) | Apply to: points / image / both; default points; Advanced | undistorted points, valid mask |
| L1 | Perspective and world plane | Plate pose → homography to the plane z = 0 ([gen_image_to_world_plane_map](https://www.mvtec.com/doc/halcon/13/en/gen_image_to_world_plane_map.html)) | Plane: plate / plate + thickness offset (mm); Advanced | mm coordinates |
| L1 | Scale | from 3.1 / 3.3 / 3.4; per product height (case top vs carton) | Product height (mm), from product master; learned yes | mm per px at that height |
| L1 | Illumination normalisation | module- or feature-scaled local mean/σ normalisation (INDUSTRY-METHODS item 2) | Window: auto from feature size; Advanced | normalised image |
| L1 | Blur / PSF | measured sigma per zone (`psf.cpp`); Wiener or Richardson-Lucy (existing `CR_STEP_DEBLUR`) | Deblur: off / measured / fixed sigma; default off for Measure (it moves edges), on for reading | sharpened image |
| L1 | Glare | multi-exposure merge (0.16), polariser (0.17), saturation mask | Glare handling: mask / merge / polariser; auto | mask of untrusted pixels |
| L1 | Polarity, mirror | learned per product (existing `decode.cpp` polarity) | Polarity: dark-on-light / light-on-dark / learn; Mirror: off / on / learn | flag |
| L1 | Rotation, skew | fixture pose from 1.x | wired automatically | pose |
| L1 | Curvature | 0.11 polar, 0.19 cylinder | per tool | unwrapped strip + mapping |
| L1 | Print growth / shrink | edge threshold at 50 % of local contrast so growth shows as width, not position; growth measured as (measured − nominal width)/2 | Report growth: on; Advanced | growth µm per side |
| L1 | Dot gaps | closing with a disc of the learned dot gap (existing `CR_STEP_DOTS`) | Dot gap px: learned | joined strokes |
| L1 | Temperature / drift | reference features re-measured every N parts (3.9) | N = 100; Advanced | drift px, alarm |
| L4 | Validity | hash of camera serial, sensor ROI, binning, pixel format, focus mark, lens id; mismatch ⇒ correction invalid ⇒ every dependent tool Error ⇒ NG | read-only | valid / invalid |
| L5 | Learn | each correction learned in the Calibrate wizard, stored per camera and per product | — | correction profile |
| L6 | Audit | profile id, date, operator, residuals written with every result | — | trace |

- **Corrections** (all of the list): lens distortion, perspective, scale, flat field, illumination
  drift, glare, polarity, mirror, rotation, skew, scale change (from Locate scale), curvature,
  blur, noise (0.7), print growth, dot gaps, damage (tools mask the untrusted pixels), background
  texture (0.7, 4.5), temperature and vibration (3.9 drift), timing jitter (0.1 timestamps, encoder
  position in Tool 11). Automatic: flat, lens, perspective, scale, polarity, mirror, rotation once taught.
- **Outputs.** `CorrectionProfile` id, `Valid`, residuals, drift px; overlays of the valid mask.
- **Decide and safety.** An invalid or missing profile that a tool needs makes that tool Error,
  which is NG (TOOL-SPECIFICATIONS section 1). A profile older than its re-check interval warns; one
  that fails 3.9 is invalid. Pixels flagged as saturated never count as edge or ink evidence.
- **Learn and tune.** Kept per camera (lens, flat, dark) and per product (height, polarity, mirror,
  growth). Drift signals: flat-field mean change > 10 %, reference feature moved > 0.2 px, blur
  sigma up > 20 %.
- **Accuracy target.** The service adds ≤ 0.01 px bias (point path) and ≤ 0.02 px (image path),
  measured on the synthetic truth set T by comparing tool results with and without a known
  synthetic distortion applied and removed. **Speed:** point path ≈ 0; 5 MP remap ≤ 6 ms T1,
  ≤ 20 ms on the 20 MP line PC with 8 threads.
- **Build plan.** (1) `vs_correct.h` C API with profile load/save and validity hash, 1 pw;
  (2) point and image paths sharing one model, reuse `calibration.cpp::undistort`, 1 pw;
  (3) wiring into Locate, Measure and the reader, tests, 1 pw.

---

## Tool 0. Base

### 0.1 Image source — "Camera"

- **Purpose.** One image per trigger from a camera, file, folder or simulator. **Have** for
  Hikrobot (`HikCamera.cs`, `Cameras.cs` `ICamera`), BMP files (`dotnet/CodeReader/BmpImage.cs`);
  GenICam for other makers Missing.
- **Best in class.** GenICam GenTL producers with SFNC feature names ([EMVA GenICam](https://www.emva.org/standards-technology/genicam/introduction-new/));
  GigE scheduled action commands for cameras that must fire together ([Basler action commands](https://docs.baslerweb.com/action-commands)).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Grab | GenTL producer per camera, ring of pre-allocated buffers, zero copy | Source: Camera / File / Folder / Simulator, Camera; Buffers 8 (2-64), Advanced | `Image`, `FrameId`, `Timestamp` µs |
| L0 | Trigger | software from PLC handshake; Action (Tool 11) | PLC trigger / Free run / Timed; essential | — |
| L4 | Frame check | complete payload, frame id continuous, timestamp interval vs encoder | Grab timeout 1000 ms | `Dropped`, `Incomplete` |
| L6 | Trace | frame id and timestamp in every result | — | — |

- **Corrections.** Timing jitter: timestamp per frame, encoder count per trigger (PLC), part-to-image
  match by count, not by order. **Outputs.** `Image`, `FrameId`, `Timestamp`, `CameraId`, `Dropped`.
- **Decide and safety.** Incomplete frame, timeout, frame-id jump or a trigger without an image ⇒
  NG for that part with `explain.source.*`; never re-use the last image.
- **Learn.** Camera list with thumbnails. **Target.** 0 dropped frames at rated rate over a 7-day soak;
  trigger to exposure ≤ 2 ms PC side. **Hardware.** Dedicated NIC per GigE camera, jumbo frames 9000.
- **Build.** GenICam (Basler pylon or Aravis-style GenTL consumer) behind `ICameraDriver`, 1 pw;
  simulator from folder at line rate, 0.5 pw.

### 0.2 Camera settings — "Camera"

- **Purpose.** Exposure, gain, pixel format, ROI, binning, white balance, transport. **Partial**:
  exposure, gain, trigger, strobe in `HikCamera.cs`; auto-exposure search `CalibrationSearch.cs`,
  advice `LineAdvisor.cs`.
- **Best in class.** Keyence SR-X one-button tuning (cited in INDUSTRY-METHODS section 3); GenICam SFNC names.

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Auto setup | bisection on ROI mean to 50-60 % full scale with ≤ 0.5 % saturated; exposure before gain up to the motion-blur limit (exposure ≤ 0.3 module / speed) | Exposure µs, Gain dB (essential); pixel format, ROI, binning, black level, gamma (Advanced) | settings |
| L0 | Focus assist | top-10 % Sobel magnitude / local contrast (existing sharpness measure) | — | `FocusScore` |
| L5 | Auto-tune | pick exposure that maximises the downstream tools' lowest confidence (spec 0.2) | on OK set | Before / After |
| L4 | Drift | brightness vs taught ± 15 % warns, ± 30 % NG (0.20) | limits | warning |

- **Corrections.** Illumination drift (re-tune button and alarm), motion blur (exposure cap from
  line speed), saturation. **Outputs.** `Exposure`, `Gain`, `FocusScore`, `Brightness`, `Saturated`.
- **Decide.** Camera refuses a value ⇒ Error. **Learn.** Per product, written on job load and read
  back to verify (a camera that silently kept the old value is Error). **Target.** Re-applied settings
  read back equal 100 %; auto setup ≤ 3 s. **Build.** read-back verify + Mono12 + ROI/binning UI, 1.5 pw.

### 0.16 NEW Multi-exposure, HDR and sequencer — "Light and dark"

- **Purpose.** Two to four exposures (or light settings) per trigger, merged, for glare and dark
  areas in one part (foil, shrink wrap, dark caps). **Partial**: `TestBurst.cs` (1×, 0.6×, 1.6×) feeds
  `multiframe.cpp` (frames aligned by `phaseCorrelate`, evidence per module summed); no per-trigger
  sequencer.
- **Best in class.** Camera sequencer sets, one burst per trigger, host merge with a triangle weight,
  radiance map, optional tone map ([Basler HDR how-to](https://docs.baslerweb.com/knowledge/how-to-hdr-imaging-with-basler-cameras-using-sequencer-mode-and-pypylon)).
  Bracketed exposures fused for reading: Cognex US9104932 and US9734376 (active; listed in
  [INDUSTRY-METHODS section 3](../INDUSTRY-METHODS.md)); adopt their form under the owner's statement of
  2026-10-30 (legal clearance).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Burst | camera sequencer (SFNC `SequencerMode`), or frame burst with exposure set per frame; light segment per frame via strobe outputs | Frames 2 / 3 / 4, default 2; exposures × 0.5 and × 2 of taught; Advanced | N frames |
| L1 | Align | `phaseCorrelate` (existing) or Locate pose per frame; motion ≤ 2 px between frames allowed | Max motion px 2 | aligned stack |
| L1 | Merge | per-pixel weighted sum, weight triangle on grey (0 at 0 and at saturation), exposure-normalised; saturation mask union | Output: radiance 16-bit / tone-mapped 8-bit / best-frame per region | merged image, mask |
| L3 | Read path | reader uses per-module evidence fusion (existing multiframe) rather than merged pixels | — | reads |

- **Corrections.** Glare, dark areas, motion ghosts (frames rejected if alignment residual > 1 px).
- **Decide.** A region saturated in every frame is untrusted (no edge, no ink, no code evidence).
  Frame count short ⇒ NG. **Learn.** Exposure ratio from the taught histogram (two peaks).
- **Target.** No module with all frames saturated in 99.9 % of parts on shrink-wrap and foil sets.
  **Speed.** Each extra frame costs one frame time (MV-CS200: about 1/5.9 s at full frame [S]; use sensor
  ROI); merge ≤ 15 ms 20 MP. **Hardware.** Camera with sequencer; strobe controller with 2-4 segments.
- **Build.** Sequencer on Hikrobot (if exposed) and GenICam, 1 pw; merge + mask, 1 pw.

### 0.17 NEW Polarisation — "Remove shine"

- **Purpose.** Remove specular glare on foil, blister lids, glossy cartons and shrink wrap.
  **Missing** (cross-polarised light never tried on the line: INDUSTRY-METHODS item 1).
- **Best in class.** On-chip four-direction wire-grid polariser (0/45/90/135°, 2 × 2 super-pixel),
  Sony Polarsens ([Emergent](https://emergentvisiontec.com/polarization-image-sensor-technology-sony-polarsens-how-does-it-work/)).
  Simpler: polariser on the light and analyser on the lens at 90° (common practice).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Capture | cross-polarised pair, or polarised sensor | Mode: Cross-polarised light / Polarised sensor | raw |
| L1 | Convert | Stokes S0 = I0 + I90, S1 = I0 − I90, S2 = I45 − I135; DoLP = √(S1² + S2²)/S0; AoLP = ½ atan2(S2, S1) (textbook Stokes, own derivation) | Output: Min-glare (min over 4) / S0 / DoLP / AoLP; default Min-glare | grey image |
| L1 | Demosaic | 2 × 2 super-pixel or bilinear per angle | Fast / Interpolated | full-res planes |

- **Corrections.** Glare (automatic per pixel), half resolution per angle (needs 2× pixels per
  feature). **Decide.** DoLP saturated or S0 < 5 % ⇒ untrusted mask. **Target.** Glare pixel area
  down ≥ 80 % on foil set; read rate on shrink-wrap from 60 % toward 99 % together with 0.16.
  **Hardware.** First try linear polariser film on the bar light and a lens polariser (low cost);
  polarised camera (IMX250MZR class, 5 MP) only if that fails. **Build.** 1.5 pw.

### 0.18 NEW Photometric stereo and multi-light composite — "Show relief"

- **Purpose.** Embossed and debossed text, dents, blister-lid relief: separate shape from print.
  **Missing.**
- **Best in class.** Keyence LumiTrax fires four directions in sequence and builds a shape image
  and a texture image ([Keyence](https://www.keyence.com/landing/vision/lumitrax-lighting.jsp));
  HALCON [photometric_stereo](https://www.mvtec.com/doc/halcon/13/en/photometric_stereo.html)
  (≥ 3 images, slant 30-60°, Lambertian, outputs gradient, albedo, height by Poisson/FFT); Omron FH
  four-light photometric stereo ([Omron FH](https://industrial.omron.eu/en/products/fh-vision-system));
  Woodham's I = L·n ([Wikipedia](https://en.wikipedia.org/wiki/Photometric_stereo)).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Sequence | 4 (or 8) light segments, one frame each, from 0.16 | Lights 4 / 8; slant 30-60° | 4-8 frames |
| L1 | Solve | per-pixel least squares n·ρ = L⁺ I (pseudo-inverse for > 3 lights); specular pixels dropped (brightest of N out) | Drop brightest: on | normals, albedo |
| L1 | Composites | curvature (divergence of normals) = "shape"; albedo = "texture"; optional height by Poisson | Output: Shape / Texture / Height / Normal X, Y | images for OCR, Inspect |
| L5 | Learn | light directions from a matte sphere or flat plate tilt | — | L matrix |

- **Corrections.** Print colour vs relief, glare (specular rejection), uneven light (albedo).
- **Decide.** Fewer than 3 valid lights at a pixel ⇒ masked. Part moved > 1 px between frames ⇒ NG
  (motion test from 0.16 alignment). **Target.** Embossed-text contrast ≥ 3× of single-light image;
  embossed OCR from 89.4 % (synthetic) toward 99.5 %. **Speed.** 4 frames + solve ≤ 20 ms 5 MP.
  **Hardware.** 4-segment ring or dome light with segment strobe; the part must stand still or
  move ≤ 1 px in the burst (encoder-timed). **Build.** 3 pw.

### 0.3 Image convert — "Clean image"

- **Purpose.** To grey, Bayer demosaic, channel extract (R G B H S V L a b), 16→8 bit. **Missing**
  (engine is 8-bit grey; colour belongs to Tool 9, chapter 04d).
- **Best in class.** Edge-aware demosaic in all vendor SDKs. **Layers.** L1 only: OpenCV/IPP conversion;
  options Mode (To grey), Demosaic (Edge-aware), Bit window (auto from histogram, Advanced).
- **Corrections.** 12-bit headroom kept for Measure (edge fit in 16-bit). **Decide.** Colour tool on
  mono camera ⇒ Error. **Target.** Bit-exact against OpenCV; ≤ 4 ms 5 MP demosaic. **Build.** 0.5 pw.

### 0.4 Crop / ROI — "Clean image"

- **Purpose.** Cut a region, rotated with the fixture, carrying its pose. **Partial** (internal crops).
- **Layers.** L2 region in fixture space → L1 `warpAffine` with the fixture pose (bilinear),
  output `Pose` crop→source so results map back exactly. Options: Region, Follow fixture (on),
  Interpolation (Bilinear), Fill (0).
- **Decide.** > 25 % outside the image ⇒ fail; any part of a Measure ROI outside ⇒ Error (never a
  partial measure). **Target.** Pose round-trip error ≤ 0.01 px (unit test); ≤ 1 ms 1 MP. **Build.** 0.5 pw.

### 0.5 Histogram — "Camera"

- **Purpose.** Grey distribution, mean, σ, saturated %, dark %. **Partial** (`diagnose.cpp`
  contrast/saturation, percentile stretch in `decode.cpp`).
- **Layers.** L3 integer histogram; L4 limits on mean; L5 auto-tolerance mean ± 4σ over 10-30 good
  parts. **Outputs.** `Histogram`, `Mean`, `StdDev`, `Min`, `Max`, `Median`, `Saturated`, `Dark`.
- **Target.** Exact; ≤ 1 ms 5 MP. **Build.** 0.5 pw (UI + tool wrapper).

### 0.20 NEW Image check (frame gate) — "Is the picture good?"

- **Purpose.** One gate before any tool: is this frame fit to judge a part? It turns doubt about the
  camera into NG (requirements section 12). **Partial**: `diagnose.cpp` measures sharpness, contrast,
  saturation for misses only; `LineAdvisor.cs` advises.
- **Best in class.** No vendor sells it as one tool; Cognex and Keyence show image-quality metrics in
  the reader (sharpness after expired Cognex US9552506, cited in the inventory).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L4 | Brightness | ROI mean vs taught | ± 30 % ⇒ NG, ± 15 % warn | `BrightOk` |
| L4 | Focus | sharpness vs taught | < 70 % of taught ⇒ NG | `FocusOk` |
| L4 | Saturation | % at max in ROI | > 2 % ⇒ warn, > 10 % ⇒ NG | — |
| L4 | Frozen / duplicate frame | hash of a 64 × 64 thumbnail vs previous | identical ⇒ NG | — |
| L4 | Part present | variance in ROI vs empty-belt image | — | `PartPresent` |

- **Decide.** Any NG rule ⇒ part NG with the reason; never skipped silently. **Learn.** All limits
  auto-learned from 30 good frames. **Target.** 100 % of injected bad frames (dark, blurred, frozen,
  half frame) NG on a 500-frame test; 0 false NG on 1000 good frames. **Build.** 1 pw.

### 0.6 Flat-field incl. dark frame — "Even light"

- **Purpose.** Remove light fall-off, lens shading and fixed-pattern offset. **Partial**:
  `calibration.cpp::setFlatField` (white card, 1/8 resolution, Gaussian 3, gain only); `CR_STEP_FLAT`
  (local paper level). **No dark frame.**
- **Best in class.** Euresys formula with averaged dark frame and per-channel gain, redo when
  camera, light or optics change ([Euresys](https://documentation.euresys.com/Products/COAXLINK/COAXLINK_10_3/Content/03_Using_Coaxlink/functional-guide/flat-field-correction/what-is-ffc.htm));
  PRNU / DSNU naming ([Adimec](https://www.adimec.com/get-better-image-information-from-machine-vision-cameras-removing-artifacts-with-flat-field-correction/)).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L5 | Teach dark | average of 16 frames, lens capped, same exposure | Dark frame: none / taught; Advanced | offset map |
| L5 | Teach flat | average of 16 frames of a white or grey target, slightly defocused or moved to hide texture | — | gain map |
| L1 | Apply | (Raw − Dark) × Gain, gain clipped to 4× | Method: Taught / Rolling background (median or morphological, size 4× largest feature) | even image |
| L4 | Check | gain > 4× anywhere ⇒ warn "light too uneven" | — | warning |

- **Corrections.** Light fall-off, shading, dust on lens (shows as gain spots: warn), PRNU, DSNU.
- **Decide.** Reference from another exposure, ROI or binning ⇒ invalid (L1 hash). **Target.**
  Residual shading ≤ 2 % (max-min over the plain target after correction). ≤ 6 ms 5 MP.
  **Build.** dark frame, full-res option, hash, Base tool wrapper, 1 pw.

### 0.7 Filters incl. shock filter — "Clean image"

- **Purpose.** Smooth, sharpen, de-noise, local contrast, edge images. **Partial** (internal:
  Gaussian, unsharp, CLAHE 4 × 4 clip 3, Lanczos upscale, RL deblur).
- **Best in class.** HALCON [shock_filter](https://www.mvtec.com/doc/halcon/13/en/shock_filter.html):
  dilate or erode toward edges chosen by Canny or Laplace, sharpens without unsharp-mask halo.

| Filter | Method | Default | Use |
| --- | --- | --- | --- |
| Gaussian, box, median | separable / histogram median | σ 1 | noise |
| Bilateral / guided | edge-preserving smoothing | σs 3, σr 20 | textured print before Locate |
| Unsharp | I + a(I − G) | a 0.5 | reading only (moves edges: never before Measure) |
| Shock | iterate: sign(−Laplace) × gradient magnitude step | 10 iterations, dt 0.5 | blurred print for OCR / codes |
| CLAHE / local normalise | tile or feature-scaled | auto window | low contrast |
| Sobel / Scharr magnitude, direction | 3 × 3 | — | Inspect, Locate debug |
| Deblur | Wiener / RL with measured PSF (L1) | off | blur |

- **Learn.** "Make it clearer" tries each filter at three strengths and keeps the one that raises the
  downstream confidence most (spec). **Safety.** A Measure tool downstream of a sharpening or shock
  filter warns at save ("this filter can move edges"). **Target.** Gaussian 3 × 3 ≤ 2 ms, median 5 × 5 ≤ 6 ms,
  CLAHE ≤ 8 ms 5 MP. **Build.** 1.5 pw (wrappers + shock + guided).

### 0.8 Morphology — "Clean image"

- **Purpose.** Erode, dilate, open, close, top-hat, black-hat, on grey or binary. **Partial**
  (`morphologyEx` in 16 places).
- **Layers.** L1; van Herk/Gil-Werman for rectangles, disc as decomposed lines, line at angle.
  Options: Operation (Close), Size 3, Shape (Disc), Iterations 1.
- **Target.** Exact vs OpenCV; ≤ 5 ms 5 × 5 disc 5 MP. **Build.** 0.5 pw.

### 0.9 Threshold incl. auto-threshold — "Black and white"

- **Purpose.** Grey → binary for Blob, Count and presence. **Partial** (internal adaptive and
  module-mean thresholds).
- **Best in class.** HALCON [binary_threshold](https://www.mvtec.com/doc/halcon/13/en/binary_threshold.html)
  (Otsu "max_separability" or smoothed-histogram minimum; light / dark) and
  [auto_threshold](https://www.mvtec.com/doc/halcon/13/en/auto_threshold.html) (Gaussian-smoothed
  histogram, every minimum a threshold, Sigma default 2). Otsu fails for small objects and unequal
  classes; Kittler-Illingworth minimum-error and triclass Otsu handle those ([Otsu](https://en.wikipedia.org/wiki/Otsu%27s_method)).
  MVTec automatic threshold patent US7953290 (Google: expired 2026-04-21; confirm) (INDUSTRY-METHODS section 16.3).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L3 | Global | Otsu / Kittler-Illingworth / smoothed-histogram minimum / fixed / band | Method: Auto (Otsu, falls back to KI when one class < 10 %); essential | binary, `Level` |
| L3 | Local | integral-image mean or Sauvola, window 31, offset 5 | Advanced | binary |
| L3 | Multi-level | auto_threshold style: all histogram minima | Sigma 2 | label image |
| L4 | Flat check | class means differ < 15 grey ⇒ warn; < 8 ⇒ Error | — | — |

- **Corrections.** Uneven light (local mode or 0.6 first), polarity (click learns it).
- **Target.** Otsu level identical to OpenCV; ≤ 2 ms global, ≤ 6 ms local 5 MP. **Build.** 1 pw.

### 0.10 Image arithmetic — "Clean image"

- **Purpose.** Subtract, absolute difference, add, average, min, max, AND, OR, mask. **Missing.**
- **Layers.** L1 saturating SIMD (`cv::absdiff`, `addWeighted` exist in OpenCV). Size mismatch ⇒ Error.
  **Target.** ≤ 2 ms 5 MP. **Build.** 0.5 pw.

### 0.11 Polar unwrap — "Unroll ring"

- **Purpose.** Unroll an annulus (cap top, bottle neck, O-ring, round label) into a strip. **Missing.**
- **Best in class.** HALCON [polar_trans_image_ext](https://www.mvtec.com/doc/halcon/13/en/polar_trans_image_ext.html):
  angle across, radius down, bilinear, radii inclusive, half-pixel centre convention. Nikon polar
  matching [US6640008](https://patents.google.com/patent/US6640008) (expired 2021).
- **Layers.** L2 centre from 2.3 (sub-pixel) → L1 precomputed bilinear table (rebuilt only when
  geometry changes) → output strip + inverse mapping for overlays. Options: centre, inner/outer
  radius (essential), start angle 0°, direction CCW, angular samples = outer circumference (Advanced),
  overlap 5° so a feature on the seam is whole.
- **Corrections.** Off-centre part (centre per image from 2.3), ellipse from tilt (use 2.4 centre and
  axes: elliptical unwrap). **Decide.** Annulus out of image ⇒ Error. **Target.** Mapping error
  ≤ 0.05 px against analytic truth; ≤ 3 ms 1 MP. **Build.** 1 pw.

### 0.19 NEW Cylinder unwrap — "Unroll bottle"

- **Purpose.** Flatten print on a bottle, vial, can or tube seen from the side, so OCR, codes and
  Inspect see a flat label. **Missing** as a step; the reader already reads codes wrapped up to 138°
  without it (`ml/experiments/cylinder.py`, 18/18).
- **Best in class.** HALCON and Cognex ship cylinder unwrapping in their 3D / calibrated toolsets
  (pages not opened this pass, see limits). Method here is ours: known radius and axis.

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L2 | Axis and edges | two 2.2 lines on the silhouette edges → axis, apparent radius | auto | axis, r px |
| L1 | Model | orthographic: x = r sin θ; perspective: ray-cylinder intersection using 3.1 intrinsics and radius in mm | Radius mm (product master), Camera model: Ortho / Perspective | mapping |
| L1 | Remap | arc-length strip, ± 60° default usable | Usable angle 120° (Advanced) | flat strip, valid mask |
| L4 | Quality | columns with stretch > 2× marked low-confidence | — | mask |

- **Corrections.** Curvature, perspective, axis tilt (rotated by the axis angle), radius change
  (re-measured per image). **Target.** Arc-length error ≤ 0.5 % within ± 60° on printed-grid cylinders
  of 3 diameters. ≤ 5 ms per label. **Hardware.** For 360° coverage: 3-4 cameras or a rotating
  station (Tool 11). **Build.** 2 pw.

### 0.12 Affine and perspective transform — "Straighten"

- **Purpose.** Rotate, scale, shift, mirror, straighten to a fixture; **perspective (NEW)** from four
  points or a homography (cartons seen at an angle). **Missing** as a tool; homographies exist inside
  the reader (`qrgrid`, `gridfit`, own 4-point solver in `quality.cpp` because OpenCV 5 moved
  `getPerspectiveTransform`).
- **Layers.** L1 `warpAffine` / `warpPerspective`, bilinear or bicubic, valid mask; L2 pose wired
  from 1.x or four clicked / found corners. **Target.** ≤ 0.01 px round trip; ≤ 5 ms 5 MP. **Build.** 1 pw.

### 0.13 Overlay — no operator name

- **Have** (`dotnet/Studio/Controls/ResultOverlay.cs`, `cr_set_found_callback`). Add fixture-space
  primitives (rotated box, arc, annulus, tolerance band green/red) for Tools 1-4, burn-in option.
  ≤ 0.5 ms. **Build.** 0.5 pw.

### 0.14 Result logic — "OK / NG"

- **Purpose.** Combine tool results into the part result and the reason sent to the PLC. **Partial**
  (`dotnet/VisionCore/Plc/PlcRuntime.cs`; PLC link loss is NG).
- **Layers.** L4 boolean tree (All must pass default; Any; custom AND/OR); reason order; L6 `NgCode`,
  `NgText` to PLC registers. Treat Error as NG is read-only on.
- **Safety.** Missing tool result, Error, timeout, invalid calibration, frame gate fail ⇒ NG. A rule
  that could make the part OK when a tool did not run is refused at save. **Target.** Truth-table
  unit tests 100 %; ≤ 0.1 ms. **Build.** 1 pw.

### 0.15 Logging — "Save results"

- **Have**: NG image store, resource guard, plant events (change notes line-pc-guard, ng-review,
  pi1-event-schema), Parts list (PR #151). Add: correction profile id and calibration residual per
  record; every Nth image for drift study. 0 lost records; never blocks. **Build.** 0.5 pw.

---

## Tool 1. Locate

Common to all locators: output `Pose` (x, y px and mm, angle °, scale, scale x / y), `Score`,
`Coverage`, `Clutter`, `Fixture`; the target of requirements section 13 is **0.05 px / 0.05°
repeatability and 100 % find** on the benchmark set.

**How the 0.05 px target is reached (design note).** Position noise of a least-squares pose fit
falls with the square root of the number of edge points used: σ ≈ σ_noise / (|∇I| · √N) per axis
(first-order estimate, own derivation). With image noise 2 grey levels, edge gradient 40 grey/px and
N = 400 edge points this gives σ ≈ 0.0025 px, so 3σ ≈ 0.008 px: the target is noise-safe for any
model with ≥ 200 good edge points. What limits real accuracy is **bias**: interpolation of
correlation peaks (current parabola fit, about 0.1-0.3 px systematic on grey-level peaks), edges
aligned to the pixel grid, blur, and lens distortion. Hence: least-squares refinement on sub-pixel
edges (2.10), the PatMax advice to tilt patterns about 5° off the pixel grid, and undistorted points
(L1). The current test bound of 1.0 px / 0.5° must be replaced by the truth-set test (T).

### 1.1 Correlation match (NCC) — "Find the part"

- **Purpose.** Find a taught grey picture; best for textured parts with few clean edges. **Have,
  needs work**: `locate_tool.cpp` (pyramid, ± 180°, parabola sub-pixel, no scale, no mask, no
  polarity, test bound 1 px / 0.5°).
- **Best in class.** NCC is common to every vendor; DALSA Sapera search combines area-based NCC and
  edge-based search, vendor claim "1/50 pixel" ([1stVision](https://www.1stvision.com/cameras/Teledyne-DALSA-Sapera-Processing)).
  A trade article lists NCC's weaknesses under rotation, scale, blur and occlusion
  ([Vision Systems Design](https://www.vision-systems.com/boards-software/article/16736565/geometric-modeling-challenges-normalized-correlation-to-pattern-matching)).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L5 | Teach | model + mask (low-texture areas masked automatically: local σ < 3 grey); pyramid depth from texture scale | Accept 60 (essential), Angle ± 15 (essential), Scale 1.0-1.0 (0.8-1.2, Advanced), Pyramid auto | model |
| L2 | Coarse | FFT NCC at top level for models > 64 px (`cv::matchTemplate` today) over angle steps = atan(1 / model radius at that level) | — | candidates |
| L2 | Track | candidates down the pyramid, ± 1 step in angle and scale | Greediness 0.8 | candidate pose |
| L3 | Fine | 2D quadratic fit on 3 × 3 score peak, then **least-squares image registration** (Lucas-Kanade on the masked model, similarity warp, 3-5 iterations) | Fine pose: on | pose 0.05 px |
| L4 | Decide | score ≥ accept; margin to second peak ≥ 0.1 (else ambiguous ⇒ NG); pose in plausible range | Max shift / angle plausibility (learned) | `Pass` |

- **Corrections.** Brightness and contrast (normalised), polarity (option "Ignore polarity" =
  max of NCC and −NCC), blur (pyramid start level), scale (scale steps), lens distortion (L1 on the
  resulting points), occlusion (masked NCC; occlusion > 30 % lowers score below accept, by design).
- **Outputs.** `Pose`, `Score`, `SecondScore`, `Fixture`; overlay of the model outline.
- **Decide and safety.** Ambiguous (second candidate within 0.1) ⇒ NG with `explain.locate.ambiguous`;
  part at image edge ⇒ NG; pose jump vs last 20 parts > learned limit ⇒ NG plus warning (a mixed
  product). **Learn.** Auto-tune accept score and angle range on the OK and NG sets under the P.4 rules
  ([04d](04d-TOOLS-INSPECT-AI-PLATFORM.md) P.4: no known bad image may pass), with ≥ 10 points
  margin; the accept score is never set below the taught value without approval;
  drift = median score falling 5 points over 1000 parts.
- **Target.** 0.05 px / 0.05° (3σ) on 100 captures of a still part; accuracy ≤ 0.05 px on the
  synthetic truth set (100 renders, random sub-pixel shift, ± 15° angle, blur σ 0.8-1.5, noise σ 2);
  100 % find on the owner's real set (the `locate_tests` real-image run). **Speed.** ≤ 15 ms ± 15°,
  ≤ 40 ms ± 180° for 200 × 200 model in 5 MP T1; 20 MP line PC: search at 1/4 resolution first.
- **Build.** mask + polarity + scale 0.8 pw; LK refinement 0.7 pw; truth tests and new bounds 0.5 pw.

### 1.2 Geometric (edge-based) match — "Find the part"

- **Purpose.** Default locator: robust to light change, occlusion and clutter, ± 180°, scale.
  **Missing.**
- **Best in class.** Cognex PatMax: model of boundary points with direction, no pixel grid, pose
  refined in a fixed number of steps; tips: tilt about 5° off the pixel grid, blur reduces accuracy,
  train several instances, undistort first ([PatMax doc](https://docs.cognex.com/vpro_930sr1/web/help/html/d65f63c3-490e-4a49-9da3-125054771348.htm));
  patents [US7016539](https://patents.google.com/patent/US7016539B1/en) (expired 2018),
  [US7065262](https://patents.google.com/patent/US7065262B1/en) (expired 2018).
  HALCON shape-based matching: pyramid, angle and scale steps, metric use / ignore global / ignore
  local polarity, greediness, sub-pixel ladder none / interpolation / least_squares (high, very high)
  / max_deformation 0-32 px ([find_shape_model](https://www.mvtec.com/doc/halcon/2205/en/find_shape_model.html),
  [create_scaled_shape_model](https://www.mvtec.com/doc/halcon/13/en/create_scaled_shape_model.html));
  patent [US7062093](https://patents.google.com/patent/US7062093B2/en) (expired 2024) and components
  [US7239929](https://patents.google.com/patent/US7239929B2/en) (expired 2024). Keyence edge-angle
  coarse search plus least-squares fine positioning along search lines
  ([US8401305](https://patents.google.com/patent/US8401305), [US8406527](https://patents.google.com/patent/US8406527);
  Google: "expired - fee related"). Omron edge-code histogram coarse search
  [US8422759](https://patents.google.com/patent/US8422759) (active to 2031): adopt as an optional
  pre-filter under the owner's statement of 2026-10-30 (legal clearance). Spread-orientation response maps for
  very fast scoring: Hinterstoisser et al., TPAMI 2012 ([TUM abstract](https://portal.fis.tum.de/en/publications/gradient-response-maps-for-real-time-detection-of-textureless-obj/)).

| Layer | Step | Method | Options (default, auto-learned, level) | Output |
| --- | --- | --- | --- | --- |
| L5 | Teach | sub-pixel edges (2.10) with hysteresis at auto contrast; chains < 10 px removed; points thinned to every 2 px; per pyramid level its own model (Keyence note: edge angles degrade when reduced, so re-extract per level) | Edge contrast auto (yes, Advanced); Min chain 10 px | model per level |
| L5 | Model check | symmetry (self-match score at other angles > 0.8 ⇒ warn), grid-aligned share of edges > 60 % ⇒ advise 5° tilt, < 100 points ⇒ warn | — | model quality bar |
| L2 | Coarse | quantised gradient orientation (8 bins), spread over 3 × 3, response maps; score = mean max-cos over model points; optional edge-code histogram pre-filter (US8422759) | Angle ± 180 (essential), Scale 1.0-1.0 (0.8-1.2, essential), Greediness 0.8 | candidates |
| L2 | Track | candidates down the pyramid, angle step halves per level, early termination when score cannot reach accept (greediness) | Max overlap 0.5 | candidates |
| L3 | Sub-pixel ladder | (a) score interpolation; (b) least squares point-to-line distance against image sub-pixel edges in a search band ± 2 px, Tukey weights, 3 / 6 / 10 iterations for LS / high / very high; (c) with deformation ≤ N px | Sub-pixel: Interpolation / LS / LS high / LS very high; default LS high | pose |
| L3 | Polarity | use / ignore globally / ignore locally (per point |cos|) | Polarity: Same (Advanced) | — |
| L4 | Score and coverage | score = mean cos over model points; coverage = share of points with a matching edge within 1 px; clutter = unmatched image edges in the model hull | Accept 50 (essential) | `Score`, `Coverage`, `Clutter` |
| L4 | Decide | score ≥ accept; coverage ≥ 0.7 else warn, ≥ 0.5 else NG; second candidate within 0.05 ⇒ ambiguous NG; residual RMS > 0.3 px ⇒ NG (fit not trustworthy) | — | `Pass` |
| L6 | Outputs | pose, fixture, overlay of found edges green / missing red | — | — |

- **Corrections.** Lighting change and gradient reversal (polarity modes), occlusion (score is
  visible fraction; 30 % tolerated), clutter (direction must match), rotation and scale (search),
  blur (pyramid levels, LS refinement band), lens and perspective (L1 points), print growth (edge
  position moves symmetrically; the pose does not), temperature drift (3.9).
- **Learn and tune.** Auto-learn from a box or a click (auto-find proposes the outline). Auto-tune
  contrast and accept score for margin; narrow angle and scale to OK spread + 50 %. Kept per product:
  model per level, ranges, polarity. Drift: coverage median falls 5 %, residual rises 50 %.
- **Accuracy target.** 0.05 px / 0.05° (3σ) on 100 still captures; ≤ 0.05 px / 0.05° accuracy on the
  synthetic truth set (500 renders: shift, angle, scale 0.8-1.2, blur σ 0.6-2, noise σ 1-4,
  occlusion 0-30 %, clutter); 100 % find, 0 false find on 200 empty / wrong-part images.
- **Speed.** ≤ 30 ms ± 180° scale 1.0, ≤ 60 ms with scale 0.8-1.2 on T1 5 MP; about half on T2.
- **Hardware.** Edges ≥ 3 px apart, contrast ≥ 20 grey; telecentric lens where height varies and
  scale must not change.
- **Build.** (1) model + per-level extraction 1 pw; (2) response-map coarse search SIMD 2 pw;
  (3) tracking + NMS 1 pw; (4) LS ladder with Tukey 1 pw; (5) tests, auto-learn, .NET tool 1 pw.
  Reuses 2.10, 2.11, pyramid code of 1.1.

### 1.2F NEW Fast mode (RedLine-style) — option of 1.2

- **Purpose.** Much faster search when angle and scale are both open. **Missing.**
- **Best in class.** Cognex PatMax RedLine: coarse large features first, then fine small ones;
  relative feature threshold and relative contrast; one uniform scale; accuracy modes Fast /
  Standard / Accurate ([VisionPro](https://docs.cognex.com/vpro_970/web/EN/help/html/e4faf0d2-0237-465b-aa18-8ed510d49594.htm),
  [In-Sight](https://docs.cognex.com/is_574/web/EN/ise/Content/Reference/FindPatMaxRedLine.htm):
  defaults ± 15°, accept 50, XY overlap 70, timeout 5000 ms, up to 1000 results). No patent found.
- **Layers.** L2 coarse on large-scale features only (edges at σ 4), thresholds relative to the
  image's own contrast percentile (survives lighting drift), AVX2 response maps, 1-2 refinement steps.
  Options: Accuracy Fast / Standard / Accurate (Standard). **Decide.** as 1.2; Fast mode result is
  always refined by one LS step before a Measure tool uses it.
- **Target.** ≤ 0.1 px / 0.1° Fast, as 1.2 Accurate; ≤ 10 ms ± 180°, scale 0.8-1.2, 5 MP T1. **Build.** 2 pw.

### 1.2A NEW Anisotropic scale — option of 1.2

- **Purpose.** Separate row and column scale (oblique camera, stretched film, line-scan speed error).
  **Missing.**
- **Best in class.** HALCON [create_aniso_shape_model](https://www.mvtec.com/doc/halcon/13/en/create_aniso_shape_model.html)
  and [find_aniso_shape_model](https://www.mvtec.com/doc/halcon/13/en/find_aniso_shape_model.html)
  (scale steps per axis; NumMatches 0 = all; MaxOverlap by enclosing rotated rectangle; least_squares
  recommended for accuracy).
- **Layers.** L2 scale grid in x and y; L3 LS refinement over a 5-parameter (x, y, θ, sx, sy) or
  6-parameter affine pose. Options: Scale X, Scale Y ranges (1.0, Advanced). **Target.** 0.05 px,
  scale ± 0.001. Time ≤ 1.5 × 1.2. **Build.** 1.5 pw.

### 1.6 NEW Local deformable match — "Find bent part"

- **Purpose.** Pouches, labels on bottles, shrink wrap, blister foil: find the part and its local
  bend, and give a rectified image. **Missing** (the reader has a K × K displacement net for wrinkled
  QR in `gridfit.cpp::flexibleFit`, reusable).
- **Best in class.** HALCON [find_local_deformable_model](https://www.mvtec.com/doc/halcon/13/en/find_local_deformable_model.html):
  returns rectified image, vector field and deformed contours; smoothness min 3, typical 11. Cognex
  PatFlex [S]. Omron allowed edge-direction change [US9286669](https://patents.google.com/patent/US9286669)
  (active to 2034): adopt as the tolerance mode under the owner's statement of 2026-10-30 (legal clearance).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L2 | Global | 1.2 with relaxed coverage | — | rough pose |
| L3 | Mesh | model split into M × N cells; each cell a sub-model searched ± D px; displacement field regularised by thin-plate / smoothness term (as `flexibleFit`) | Cells 4 × 4 (Advanced), Max deformation 8 px, Smoothness 11 | vector field |
| L1 | Rectify | remap by the field | Output rectified image: on | flat image for OCR / Inspect |
| L4 | Decide | cells found ≥ 80 %, neighbour displacement jump ≤ 2 px | — | `Pass`, `Deformation` max px |

- **Target.** Mesh-node error ≤ 0.2 px on synthetic bends (sine bends 2-8 px); 100 % find on pouch
  and bottle-label sets. ≤ 80 ms T1. **Build.** 4 pw.

### 1.7 NEW Descriptor / perspective match — "Find printed face"

- **Purpose.** Textured printed faces (cartons, labels) under strong perspective or any rotation.
  **Missing.**
- **Best in class.** HALCON [create_uncalib_descriptor_model](https://www.mvtec.com/doc/halcon/13/en/create_uncalib_descriptor_model.html)
  (interest points + randomised ferns, 50-450 points, returns homography) and calibrated perspective
  deformable model with metric 3D pose ([create_planar_calib_deformable_model](https://www.mvtec.com/doc/halcon/13/en/create_planar_calib_deformable_model.html)).
- **Layers.** L2 ORB / AKAZE keypoints (OpenCV features module), matching, RANSAC homography
  ([RANSAC](https://en.wikipedia.org/wiki/Random_sample_consensus)); L3 refinement by edge LS (1.2
  machinery, 8-parameter); L4 inliers ≥ 30 and inlier ratio ≥ 0.4, else NG. Options: Points 300,
  Perspective allowed on/off, Calibrated pose (needs 3.1).
- **Target.** Corner error ≤ 0.3 px after refinement; ≤ 50 ms 5 MP. **Build.** 3 pw.

### 1.3 Multi-instance search — "Find all parts"

- **Purpose.** Every copy (trays, blisters, case slots). **Missing** as a locator (codes have the slot
  map, `engine/src/slots.cpp`).
- **Layers.** L2 keep all candidates above accept at top level; NMS by rotated-rectangle overlap
  (HALCON MaxOverlap; Cognex XY overlap 70 %); refine each (1.2 ladder); L4 lattice fit to poses
  (reuse `slots.cpp`) names empty slots. Options: Expected count, Max count 64 (1-1000), Max overlap
  0.3, Sort (row-major).
- **Decide.** Count ≠ expected ⇒ NG with empty slots marked; an instance off the lattice by
  > 0.3 pitch ⇒ NG. **Target.** 100 % count on tray / blister sets; ≤ 1.5 × single search for 50.
  **Build.** 1.5 pw.

### 1.4 Fixture — wired automatically

- **Purpose.** A part coordinate space; ROIs stored in fixture space. **Partial** (LocateTool shifts
  code and OCR regions; no generic fixture object).
- **Layers.** L2 rigid or similarity transform from a pose, point + angle, two points, point + line
  (2.5 corner, 2.6 geometry); chains of fixtures (part → feature); in mm when calibrated.
- **Decide.** Missing pose ⇒ dependent tools Error ⇒ NG (`explain.fixture.no_part`). **Target.**
  Propagation ≤ 0.01 px (double precision); ≤ 0.05 ms. **Build.** 1 pw.

### 1.5 AI locator — "Find the part"

- **Purpose.** Parts too variable for 1.1 / 1.2 (natural products, printed packs, reflective parts).
  **Missing** for parts; CodeNet (`ml/model.py`, `engine/src/ai_locator.cpp`) finds codes only.
- **Best in class.** Oriented-box detectors in Cognex ViDi Locate and MVTec deep-learning object
  detection (covered in chapter 04d and [AI-DEEP-LEARNING.md](../AI-DEEP-LEARNING.md)).
- **Layers.** L2 oriented-box detector (ONNX, OpenCV DNN today; OpenVINO / TensorRT per
  requirements) → L3 optional 1.2 refinement in the box for sub-pixel pose → L4 accept 50, unsure
  band 40-60 ⇒ NG with "add this picture". L5 label 5-10 images with box + arrow.
- **Target.** 100 % find on the product set; pose after refinement as 1.2; ≤ 80 ms T1, ≤ 25 ms T2.
  Without refinement it may not drive a Measure tool (refused at save). **Build.** 4 pw (shares the
  Tool 10 training worker).

---

## Tool 2. Measure

Common rules: results in px and, with a `Calib`, in mm (geometry computed in world coordinates);
tolerance bands green / red; auto-tolerance mean ± 4σ over 10-30 good parts; **repeatability target
0.1 px (3σ) on static parts** (requirements section 13), tighter where stated.

**How 0.1 px is reached.** Caliper position noise ≈ σ_noise / (|g'| √L), with g' the edge gradient
(grey / px) and L the projection length in pixels (own first-order estimate): noise 2 grey,
gradient 30 grey/px, L = 10 px gives 0.02 px (3σ 0.06 px). Bias sources dominate: edge-model
(parabola through three samples biases by up to about 0.05 px for asymmetric blur), interpolation
(HALCON: bilinear + float projection for maximum accuracy, margin ≥ 1 px bilinear, ≥ 2 px bicubic,
[gen_measure_rectangle2](https://www.mvtec.com/doc/halcon/13/en/gen_measure_rectangle2.html)),
sigma ≥ 0.5 × box half-length invalid ([measure_pos](https://www.mvtec.com/doc/halcon/13/en/measure_pos.html)),
lens distortion and vibration. Hence 2.10 below and L1 point correction.

### 2.10 NEW Sub-pixel edge engine (shared)

- **Purpose.** One sub-pixel edge routine for Caliper, Locate, Contour and Calibrate. **Missing.**
- **Best in class.** HALCON [edges_sub_pix](https://www.mvtec.com/doc/halcon/13/en/edges_sub_pix.html)
  (Deriche, Lanser, Shen, Canny, Sobel-fast; Lanser and modified Shen remove diagonal amplitude
  bias); Canny with Devernay sub-pixel non-maxima suppression ([IPOL 2017](https://www.ipol.im/pub/art/2017/216/));
  moment-based and partial-area methods ([Edge detection](https://en.wikipedia.org/wiki/Edge_detection)).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L3 | 1D (profiles) | Gaussian-derivative profile; peak by (a) parabola, (b) Gaussian fit on 5 samples, (c) zero crossing of 2nd derivative with linear interpolation | Peak model: Gaussian 5-point (default) | position, contrast |
| L3 | 2D (contours) | Gaussian gradient σ 1-1.5 (or Deriche), Devernay sub-pixel NMS, hysteresis, chaining | Filter: Gauss / Deriche / Sobel-fast; σ 1; Low / High auto | contour points with angle, response |
| L1 | Inputs | 16-bit kept when camera gives Mono12; saturated pixels masked | — | — |
| L5 | Self-test | at start-up, a rendered edge at 10 sub-pixel offsets must be found within 0.01 px | — | engine OK flag |

- **Target.** Bias ≤ 0.02 px and σ ≤ 0.02 px on rendered edges (blur σ 0.6-2, noise σ 2, all
  angles 0-90°); ≤ 15 ms for a 5 MP contour image on T1. **Build.** 2 pw.

### 2.11 NEW Robust fit library (shared)

- **Purpose.** Lines, circles, ellipses, planes of points with outliers. **Missing.**
- **Best in class.** HALCON line fit regression / huber / tukey (recommended) / drop / gauss,
  clipping 2.0 Tukey, 1.0 Huber, 5 iterations ([fit_line_contour_xld](https://www.mvtec.com/doc/halcon/13/en/fit_line_contour_xld.html));
  circle algebraic / ahuber / atukey / geometric / geohuber / geotukey ([fit_circle_contour_xld](https://www.mvtec.com/doc/halcon/13/en/fit_circle_contour_xld.html));
  OpenCV `fitLine` M-estimator constants FAIR 1.3998, WELSCH 2.9846, HUBER 1.345 ([imgproc.hpp](https://raw.githubusercontent.com/opencv/opencv/4.x/modules/imgproc/include/opencv2/imgproc.hpp));
  RANSAC with k = log(1 − p) / log(1 − wⁿ), MSAC, PROSAC ([RANSAC](https://en.wikipedia.org/wiki/Random_sample_consensus)).
- **Methods.** Line: RANSAC (2 pts) → total least squares → Tukey IRLS. Circle: RANSAC (3 pts) →
  Taubin algebraic seed → geometric LM. Ellipse: direct least squares (Fitzgibbon) seed → geometric
  refinement. Residuals, covariance (σ of each parameter) always returned.
- **Target.** Exact on published test vectors and rendered data; parameter σ reported within 20 %
  of Monte Carlo truth. ≤ 0.2 ms per 1000 points. **Build.** 1.5 pw.

### 2.1 Caliper — "Check size"

- **Purpose.** One edge or a pair along a short box: position or width. **Missing.**
- **Best in class.** Cognex Caliper: projection into a 1D profile, filter half-width, edge scorers
  (position, size, contrast, straddle) combined ([Caliper](https://docs.cognex.com/vpro_930sr1/web/help/html/f36c9fbe-115f-4c43-a423-259f2be8c218.htm));
  HALCON fuzzy scoring by geometric mean with a minimum ([fuzzy_measure_pos](https://www.mvtec.com/doc/halcon/13/en/fuzzy_measure_pos.html));
  In-Sight edge defaults caliper height 5, contrast 5, width 3 ([InspectEdge](https://docs.cognex.com/is_571/web/EN/ise/Content/Reference/InspectEdge.htm)).

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L2 | Place | box in fixture space; auto-learn: perpendicular to local gradient, 40 × 20 px | Box (essential) | sampling grid |
| L3 | Project | bilinear float sampling, mean across the box (HALCON `int_zooming` false equivalent) | Projection length = box height; Interpolation Bilinear / Bicubic | profile |
| L3 | Edges | 2.10 1D, Gaussian derivative σ = filter half-width / 2 | Filter half-width 2 (1-20); Edge threshold auto (3 × noise σ) | candidates |
| L3 | Score | contrast, position, width-match, straddle → 0-1, geometric mean | Score by: Combined (Advanced) | ranked edges |
| L4 | Decide | edge(s) found, contrast ≥ threshold, value within tolerance; second candidate within 80 % score in pair mode ⇒ NG "several edges" | Mode Single / Pair / All; Polarity; Tolerance | `Position`, `Width`, `Contrast`, `Pass` |
| L6 | Output | mm via L1 points; overlay box and edges | — | — |

- **Corrections.** Rotation (fixture), lens and scale (L1 points), uneven light (contrast relative),
  print growth (pair mode reports growth separately), noise (projection averaging), blur (σ),
  vibration (100-capture σ checked in 3.9).
- **Learn and tune.** Auto-tune threshold and half-width for the lowest σ on OK images.
  Auto-tolerance. Drift: contrast median −20 %.
- **Target.** 0.05 px (3σ) on a clean edge, 0.1 px on line images, measured on 100 still captures of
  a chrome-on-glass edge target and on the truth set. ≤ 0.2 ms each. **Build.** 1.5 pw.

### 2.2 Find line — "Check edge"

- **Purpose.** A straight edge from many calipers. **Missing.**
- **Layers.** L3 calipers every 10 px (count 10, 3-200) → 2.11 RANSAC (1.5 px) → TLS → Tukey; L4 points
  used ≥ 60 %, straightness, angle limits. Outputs `Line`, `Angle`, `Straightness`, `PointsUsed`,
  parameter σ.
- **Corrections.** Dirt and nicks (outliers), lens (L1), rotation (fixture).
- **Decide.** Points used < 60 % ⇒ NG "edge broken"; parameter σ above 1/3 of tolerance ⇒ NG (the
  measurement cannot decide). **Target.** 0.05 px / 0.02° on 100 captures; ≤ 1 ms for 20 calipers.
  **Build.** 1 pw.

### 2.3 Find circle / arc — "Check hole"

- **Purpose.** Centre, diameter, roundness of a hole, boss or arc. **Missing.**
- **Best in class.** HALCON metrology circle (measure regions perpendicular to the rim, half-lengths
  20 and 5, σ 1.0, threshold 30; [add_metrology_object_circle_measure](https://www.mvtec.com/doc/halcon/13/en/add_metrology_object_circle_measure.html));
  geometric fit recommended for large noise (HALCON fit_circle).
- **Layers.** L3 radial calipers (36; 8-360) inward or outward → 2.11 RANSAC + Taubin + geometric
  LM; arc when angle span < 360°. L4 diameter, roundness (max − min radius), points used.
- **Target.** Diameter 0.05 px, centre 0.03 px (3σ); ≤ 1.5 ms. **Build.** 1 pw.

### 2.4 Find ellipse — "Check hole"

- As 2.3 with ellipse fit (Fitzgibbon seed, geometric refinement); auto-switch from 2.3 when axis
  ratio < 0.95. Outputs centre, axes, angle. **Target.** Axes 0.1 px; ≤ 2 ms. **Build.** 1 pw.

### 2.5 Find corner — "Check corner"

- Two 2.2 fits and their intersection; corner σ propagated from line covariance. Expected angle 90°
  ± tolerance. **Target.** 0.07 px (3σ); ≤ 2 ms. Used as a fixture source. **Build.** 0.5 pw.

### 2.6 Geometry — "Check distance / angle"

- **Purpose.** Distances, angles, intersections, perpendiculars, midpoints, circle from three points.
  **Missing.**
- **Layers.** L3 closed-form in world coordinates (lens bias removed); uncertainty propagated from
  inputs. L4 tolerance; NG when the propagated 3σ exceeds a third of the tolerance band (cannot
  decide), or lines are parallel for an intersection.
- **Target.** Exact (double), ≤ 0.05 ms. **Build.** 1 pw.

### 2.7 Edge width / gap — "Check gap"

- Pair calipers along a box (20 samples): widths, min, max, mean, position of min. Growth / shrink
  of print shown as width change. **Target.** 0.1 px per width; ≤ 1 ms. **Build.** 0.5 pw.

### 2.8 Pin pitch — "Check pins"

- All-edges caliper, pair matching by polarity, centres, pitch; missing / bent pin by index.
  Lattice check reuses `slots.cpp` idea in 1D. **Target.** 0.1 px pitch, 100 % count; ≤ 1 ms for 40.
  **Build.** 1 pw.

### 2.9 Count edges / thread — "Count edges"

- 1D profile, Gaussian-derivative peaks, period by autocorrelation; Steger option (2.13) for thin
  crests. **Target.** 100 % count on test set; ≤ 0.5 ms. **Build.** 0.5 pw.

### 2.12 NEW Metrology model — "Measure the part"

- **Purpose.** Many shapes measured together and fitted to one part model (circles, lines,
  rectangles, ellipses) with one alignment, so a tray of holes or a carton outline is one tool.
  **Missing.**
- **Best in class.** HALCON metrology model: create, set image size, add objects, apply, get results;
  generic parameters measure_distance, measure_transition, num_instances, min_score,
  distance_threshold, rand_seed ([create_metrology_model](https://www.mvtec.com/doc/halcon/13/en/create_metrology_model.html)).
  Gauging from local sub-models instead of one global pose: Cognex [US6993177](https://patents.google.com/patent/US6993177) (expired 2023).
- **Layers.** L2 global pose (1.2) → local sub-model per gauge entity (US6993177) → L3 2.1-2.4 per
  object → L4 per-object tolerances + relations (distance, parallel, concentric). Options: objects,
  sub-models on/off, Random seed fixed (repeatable results).
- **Target.** As member tools; ≤ 5 ms for 10 objects. **Build.** 2 pw.

### 2.13 NEW Steger line (ridge) detector — "Find thin line"

- **Purpose.** Centre and width of thin lines: printed rules, tear strips, cracks, hair, threads,
  laser marks. **Missing.**
- **Best in class.** HALCON [lines_gauss](https://www.mvtec.com/doc/halcon/13/en/lines_gauss.html):
  Gaussian-derivative quadratic fit per pixel, maximum of 2nd derivative across the line, hysteresis
  linking (Low 0.25-0.5 × High), width left / right, asymmetry and contrast with bar / parabolic /
  gaussian line models; σ default 1.5, lowest valid σ for best localisation. Steger, "An Unbiased
  Detector of Curvilinear Structures", IEEE TPAMI 20(2), 1998 (cited there; paper not opened).
- **Layers.** L3 Hessian eigen-analysis at σ ≥ w/√3, sub-pixel centre, width with bias correction;
  L4 width and continuity limits. Options: Light / Dark, σ auto from width, Low / High auto.
- **Target.** Centre 0.05 px, width 0.1 px on rendered lines 2-10 px wide; ≤ 10 ms 1 MP. **Build.** 2 pw.

---

## Tool 3. Calibrate

Every calibration reports residuals in px and µm, the parameter standard deviations, and is stored
with the camera serial, sensor ROI, binning, pixel format and lens id (L1 hash).

### 3.1 Grid calibration and lens models — "Set real size"

- **Purpose.** Pixel-to-mm, lens distortion, plate pose. **Partial**: `calibration.cpp` (chessboard,
  5-coefficient model, accepts RMS ≤ 1.0 px, scale from last view, points only, grading only).
  Above 1.0 px it silently drops the lens model and keeps the scale; 3.9 replaces that with a
  refusal (dependent tools Error, NG).
- **Best in class.** Zhang's planar method ([Microsoft Research](https://www.microsoft.com/en-us/research/publication/a-flexible-new-technique-for-camera-calibration/)).
  HALCON [calibrate_cameras](https://www.mvtec.com/doc/halcon/13/en/calibrate_cameras.html): division
  or polynomial distortion, tilt lens, telecentric, line scan; back-projection RMS about 0.1 px is a
  good single-camera fit; parameter std deviations. OpenCV ([calib3d.hpp](https://raw.githubusercontent.com/opencv/opencv/4.x/modules/calib3d/include/opencv2/calib3d.hpp)):
  rational k4-k6, thin prism s1-s4, tilted τx τy, fisheye (Kannala), `calibrateCameraRO` refines an
  inaccurate target, `findChessboardCornersSB` (localised Radon, sub-pixel). Cognex: checkerboard
  better than dots, tiles ≥ 15 × 15 px [S], cannot calibrate stitched images
  ([Cognex](https://docs.cognex.com/vpro_980/web/EN/help/html/d3cfc90a-4145-4047-bcd3-fc84cd9d08ed.htm)).

| Layer | Step | Method | Options (default; level) | Output |
| --- | --- | --- | --- | --- |
| L0 | Plate | chrome-on-glass checkerboard or coded board (ChArUco-style, partial views allowed); dot grid with ellipse-centre correction | Plate type Checkerboard (essential); pitch mm (essential) | images |
| L3 | Corners | `findChessboardCornersSB` or saddle point + 2.10 refinement; coded board ids | — | points |
| L3 | Model | Zhang init → LM over all; models None / Radial 2 / Radial 3 + tangential / Rational / Division / Tilted / Telecentric (3.7) / Line scan (3.8) | Lens model Radial 3 + tangential (Advanced) | intrinsics |
| L3 | Plate refinement | `calibrateCameraRO`-style object-point release for printed plates | off (on for paper plates) | — |
| L3 | Single-image mode | homography + known focal length (Tsai-like) for the line where only one pose is possible | Mode Single / Multi-pose | — |
| L3 | Plane and height | plate pose → world plane; plate thickness offset; product height from product master | — | world plane |
| L4 | Acceptance | 3.9 rules | — | `Pass` |
| L6 | Report | RMS / max residual px and µm, residual map, coverage %, σ of each parameter, PDF | — | report |

- **Corrections.** Lens distortion, perspective, scale, plate print error (RO mode), plate thickness,
  height difference between plate and product.
- **Learn.** Wizard with live green / red corners and hints until coverage ≥ 80 % (multi-pose) and
  each image area has ≥ 3 views. **Target.** RMS ≤ 0.1 px (HALCON rule), max ≤ 0.3 px; scale error
  ≤ 0.02 % against a certified length on the plate not used in the fit. ≤ 2 s for 15 images.
- **Hardware.** Glass plate with certified pitch (± 1-2 µm class), sized to fill ≥ 60 % of the field;
  diffuse back or front light. **Build.** move into a `vs_calib.h` tool with full model choice,
  report, hash 1.5 pw; coded board + RO mode 1 pw. Reuses `calibration.cpp`.

### 3.2 Lens correction / rectify — "Straighten image"

- **Purpose.** Image without distortion, optionally rectified to the world plane in mm. **Missing**
  (no `cv::undistort` / remap tables built; `remap` is used elsewhere).
- **Best in class.** HALCON map computed once, bilinear 5-channel or sub-pixel coordinate map
  ([gen_image_to_world_plane_map](https://www.mvtec.com/doc/halcon/13/en/gen_image_to_world_plane_map.html));
  Cognex unfilled pixels marked with a mask, warp approximation error default 0.01 px.
- **Layers.** L1 remap table (float or 16.16 fixed), valid mask, output scale px/mm. **Target.** Warp
  error ≤ 0.01 px vs exact model; ≤ 10 ms 5 MP T1, ≤ 4 ms T2 GPU. **Build.** 1 pw.

### 3.3 N-point calibration — "Set real size"

- Linear least squares (scale / similarity / affine / perspective with normalised DLT); residual per
  point shown so a wrong point stands out; leave-one-out residual. **Target.** ≤ 1 ms; refuses a model
  with fewer points than parameters + 2. **Build.** 0.5 pw.

### 3.4 Pixel-to-mm scale — "Set real size"

- **Partial** (scale from chessboard). From a measured length (2.x) or clicks; X / Y separate;
  warns that it ignores lens and tilt. **Target.** ≤ 0.1 % with a 2.2-measured reference of ≥ 500 px.
  **Build.** 0.5 pw.

### 3.5 Hand-eye 2D — "Teach robot"

- **Purpose.** Camera → robot mapping. **Missing** (no robots on today's lines; P4).
- **Best in class.** HALCON [hand_eye_calibration](https://www.mvtec.com/doc/halcon/13/en/hand_eye_calibration.html):
  ≥ 10 poses advised (min 3), rotation ≥ 30° (60 preferred) about ≥ 2 axes, quality as pose error,
  std deviation and covariance; OpenCV Tsai, Park, Horaud, Andreff, Daniilidis.
- **Layers.** 9-point grid + 2 rotations (2D), LS similarity / affine, rotation centre from the circle
  of target poses; 3D methods (Park default) when a robot gives full poses. **Target.** ≤ 2 px
  equivalent, report translation and rotation RMS. **Build.** 2 pw.

### 3.6 Multi-camera stitching — "Join cameras"

- **Purpose.** Cameras in one world frame; optional stitched image. **Missing.**
- **Layers.** Each camera 3.1 against a shared plate or plates with known offsets; joint LM over all
  cameras (shared world pose); per-camera remap into one raster; seam error at overlaps; feather
  blend. Measure across cameras uses world coordinates, never the stitched pixels (Cognex: nonlinear
  calibration unsupported on stitched images).
- **Target.** Seam disagreement ≤ 0.2 px equivalent; ≤ 25 ms 2 × 5 MP. **Build.** 2.5 pw (needs Tool 11 sync).

### 3.7 NEW Telecentric model

- **Purpose.** Calibrate telecentric lenses (magnification instead of focal length, orthographic
  projection). **Missing.**
- **Best in class.** HALCON telecentric and tilted telecentric models (calibrate_cameras above).
  Constant magnification with distance, front element as large as the object ([Telecentric lens](https://en.wikipedia.org/wiki/Telecentric_lens)).
- **Layers.** Affine camera + radial distortion, LM. **Target.** RMS ≤ 0.1 px. **Hardware.** For any
  Measure job where part height varies by more than (tolerance × working distance / field), buy a
  telecentric lens sized to the part. **Build.** 1 pw.

### 3.8 NEW Line-scan calibration

- **Purpose.** Line-scan cameras on round products and webs (speed vector, encoder). **Missing.**
- **Best in class.** HALCON line-scan model with motion vector, 12 internal parameters
  (calibrate_cameras above); Cognex needs a separate line-scan calibration ([Cognex](https://docs.cognex.com/vpro_980/web/EN/help/html/d3cfc90a-4145-4047-bcd3-fc84cd9d08ed.htm)).
- **Layers.** Plate moved at constant speed; fit intrinsics + motion vector; anisotropic scale check
  (1.2A) in run to detect speed error. **Target.** RMS ≤ 0.2 px, along-track scale ≤ 0.1 %.
  **Build.** 2 pw.

### 3.9 NEW Calibration acceptance and drift check

- **Purpose.** Rules that make a calibration trustworthy, and a running check that it still holds.
  **Missing** (current rule: RMS ≤ 1.0 px). Line-safety rule: a calibration over the RMS limit is
  refused, and the tools that depend on it report Error, which is NG. Today
  `engine/src/calibration.cpp` (lines 111-114) silently falls back when RMS > 1.0 px: it drops the
  lens model and keeps the scale. That fallback must go.

| Rule | Value | Source / reason |
| --- | --- | --- |
| RMS back-projection | ≤ 0.1 px good, ≤ 0.25 px usable with warning, else fail | HALCON calibrate_cameras |
| Max residual | ≤ 3 × RMS | outlier corner |
| Coverage | ≥ 80 % of field (multi-pose), ≥ 60 % single | TOOL-SPECIFICATIONS 3.1 |
| Views | ≥ 10 poses, tilts ≥ 2 axes, ≥ 3 views per field cell | Zhang; HALCON hand-eye advice |
| Parameter σ | σ(k1) / |k1| ≤ 10 %, σ(fx) / fx ≤ 0.1 % | over-fit guard |
| Independent check | certified length not in the fit, error ≤ 0.02 % | traceability |
| Validity hash | camera serial, ROI, binning, format, lens id, focus mark | L1 |
| Running drift | 2-4 fixed reference marks in the field (or a known feature on every part) re-measured each N = 100 parts; drift > 0.2 px warns, > 0.5 px or > ⅓ tolerance ⇒ NG and recalibrate | temperature, vibration |
| Repeatability study | 100 captures of a still part, 3σ per output, before release of a Measure job | requirements section 13 |

- **Target.** Any calibration failing a rule is refused 100 %; injected drift of 0.5 px is caught
  within N parts 100 %. ≤ 1 ms per check. **Build.** 1.5 pw.

---

## Tool 4. Blob and Count

### 4.1 Blob labelling and features — "Count / spot"

- **Purpose.** Connected regions after a threshold, each measured. **Partial** (internal
  `connectedComponentsWithStats` in 10 places).
- **Best in class.** HALCON [connection](https://www.mvtec.com/doc/halcon/13/en/connection.html)
  (8-neighbourhood default) and region shapes convex hull, same-moment ellipse, smallest circle,
  rotated rectangle ([shape_trans](https://www.mvtec.com/doc/halcon/13/en/shape_trans.html));
  OpenCV Spaghetti, SAUF, BBDT with parallel versions ([imgproc.hpp](https://raw.githubusercontent.com/opencv/opencv/4.x/modules/imgproc/include/opencv2/imgproc.hpp));
  algorithms two-pass union-find, one-component-at-a-time, run-based ([CCL](https://en.wikipedia.org/wiki/Connected-component_labeling));
  moments, orientation ½ atan(2μ11 / (μ20 − μ02)), eccentricity, Hu invariants ([Image moment](https://en.wikipedia.org/wiki/Image_moment)).

| Layer | Step | Method | Options (default; level) | Output |
| --- | --- | --- | --- | --- |
| L1 | Prepare | 0.6 flat, 0.9 threshold (Auto Otsu / KI, fixed, band, local), 0.8 clean-up | Threshold Auto (essential); Polarity Dark on light (essential); Morph none | binary |
| L3 | Label | Spaghetti / BBDT (OpenCV `connectedComponentsWithStats`, parallel) | Connectivity 8 / 4 | labels |
| L3 | Features | area, centroid (grey-weighted option, sub-pixel), perimeter (corrected crack length), circularity 4πA/P², elongation (eigenvalue ratio), angle, holes (Euler), bounding and rotated box, convex hull area / solidity, same-moment ellipse, smallest circle, mean / σ grey, Hu moments, distance to nearest blob | — | `Blobs` |
| L4 | Limits | Min area (essential), max area, exclude touching ROI edge (on), fill holes | default ± 15 % around the OK spread of area (± 50 % would count a half tablet as one) | `Count`, `Pass` |

- **Corrections.** Uneven light (0.6 / local threshold), scale (mm² via L1), rotation (fixture),
  noise (min area, open 3), touching edge (excluded or flagged as doubt).
- **Decide and safety.** Blob within 10 % of a filter limit lowers confidence; a blob cut by the ROI
  edge never counts as good presence. **Learn.** Click a sample blob: polarity, threshold, limits.
  Auto-tune threshold and morphology for the widest OK / NG count margin.
- **Target.** Area ± 0.5 % and centroid 0.05 px on rendered discs ≥ 20 px diameter; ≤ 10 ms 5 MP with
  1000 blobs. **Build.** 2 pw (tool wrapper, features, .NET, tests).

### 4.2 Contour analysis — "Check outline"

- **Purpose.** Sub-pixel outlines, convexity defects, deviation from a taught outline. **Missing**
  (`findContours` used internally).
- **Layers.** L3 sub-pixel contour by 2.10 or marching squares on the smoothed image; convex-hull
  defects; deviation by closest-point distance after 1.2 pose alignment; curvature. L4 max deviation,
  dent depth. **Target.** Deviation 0.1 px (3σ); ≤ 5 ms for 2000 points. **Build.** 1.5 pw.

### 4.3 Count — "Count items"

- **Purpose.** One-setting count, built on 4.1. **Missing** as a tool.
- **Layers.** 4.1 with area filter; touching items split by distance-transform peaks + marker-controlled
  watershed (Meyer flooding; [Watershed](https://en.wikipedia.org/wiki/Watershed_(image_processing)));
  area-based estimate as a cross-check (total area / median item area) — the two counts must agree
  within 1, else NG (doubt). **Target.** 100 % count on tablet / vial / blister sets with touching items;
  as 4.1 speed. **Build.** 1 pw.

### 4.4 Filter and sort — "Count / spot"

- Feature limits (min / max per feature), sort by any feature, keep first N; auto-learn limits halfway
  between a good and a bad blob for features that separate them. **Target.** Exact; ≤ 0.2 ms per
  1000. **Build.** 0.5 pw.

### 4.5 NEW Texture segmentation — "Find surface areas"

- **Purpose.** Separate regions by texture rather than grey level (matte vs glossy foil, label
  paper vs carton, fabric). **Missing.**
- **Best in class.** HALCON [texture_laws](https://www.mvtec.com/doc/halcon/13/en/texture_laws.html)
  (Laws vectors L, E, S, W, R, U, O, sizes 3 / 5 / 7, large mean filter, then classification).
- **Layers.** L1 Laws energy maps (or Gabor bank) → L3 per-pixel classifier (Gaussian mixture or
  small MLP) trained on two to five clicked areas → 4.1 on the class map. **Target.** Pixel accuracy
  ≥ 98 % on labelled test images; ≤ 20 ms 5 MP. **Build.** 1.5 pw.

---

## T NEW Geometry truth set and repeatability rig

Without a truth the 0.05 px target cannot be proved. **Missing.**

| Part | What | Count | Pass rule |
| --- | --- | --- | --- |
| Synthetic renders | anti-aliased edges, circles, lines, patterns, at known sub-pixel pose, blur σ 0.6-2, noise σ 1-4, light gradient, occlusion 0-30 % | 500 per tool | bias and 3σ within target |
| Still captures | 100 frames of a still part, line camera and light | 1 set per product | 3σ ≤ target per output |
| Stage moves | manual XY micrometer stage (10 µm) and rotation stage (0.01°): 20 known moves | per tool | error ≤ 2 × repeatability |
| Calibration plates | certified chrome-on-glass checkerboard; independent certified length | 1-2 plates | 3.9 rules |
| Real find set | owner's line images per product (today `locate_tests` real run) | ≥ 200 | 100 % find, 0 false |

Hardware to buy (chapter 05 prices it): certified glass plate, XY + rotation micrometer stage, a
vibration-damped camera stand for the still test. **Build.** generator + harness in `engine/tests`
and the bench 3 pw.

---

## Build order for this chapter

| Step | Items | pw | Why first |
| --- | --- | ---: | --- |
| 1 | T truth set, 2.10 sub-pixel edges, 2.11 robust fits | 6.5 | Everything else is measured against them |
| 2 | L1 service, 3.1 + 3.2 + 3.9 calibration | 8 | Measure and Locate need mm and a trustworthy model |
| 3 | 1.1 upgrade (to pass 0.05 px), 1.4 fixture, 0.20 frame gate, 0.14 logic | 5 | Closes today's 1 px / 0.5° test gap; safety gates |
| 4 | 1.2 geometric match, 1.3 multi | 7.5 | Default locator of the spec |
| 5 | 2.1-2.9 measure tools | 8 | P1 measuring job (requirements section 15) |
| 6 | 4.1-4.4 blob and count, 0.3-0.12 base wrappers, 0.6 dark frame | 13 | P1 tool kit |
| 7 | 0.16 HDR, 0.19 cylinder, 1.2F, 1.2A, 2.12, 2.13, 3.7 | 12.5 | P2: reading and line speed |
| 8 | 0.17, 0.18, 1.5, 1.6, 1.7, 4.5 | 17 | P3: AI and hard surfaces |
| 9 | 3.5, 3.6, 3.8 | 6.5 | P4: robots, multi-camera, line scan |
| | Remaining small items (0.1, 0.2, 0.13, 0.15, 3.3, 3.4) | 5 | alongside |
| | **Total** | **~89** | |

Steps 1-6 (about 48 pw) are the P1 foundation for tools 0-4: one developer with an AI assistant,
about 11-12 months; two developers in parallel along the dependency lines (Locate / Measure vs
Base / Blob / Calibrate) about 6-7 months.

## Not opened / limits

- Opened this pass: HALCON `lines_gauss`, `gen_image_to_world_plane_map`, `binary_threshold`,
  `auto_threshold`, `create_planar_calib_deformable_model`, `find_aniso_shape_model`; Wikipedia Otsu,
  image moment, connected-component labelling, watershed, telecentric lens; TUM abstract of
  Hinterstoisser 2012. All other links were opened in the earlier research pass of 2026-10-30
  (scratch file research-geometry, through a summarising fetch tool); I did not re-open them.
- Not opened: Steger 1998 paper (IEEE page returned empty); Devernay 1995 report; OpenCV docs pages
  (403, e.g. the calibration-pattern tutorial; headers on GitHub were used instead); Fitzgibbon
  ellipse paper and Taubin circle paper (named from common knowledge, not opened); Hinterstoisser
  full PDF (quantisation details not verified); Cognex PatFlex article (403); Basler HDR use-case page;
  vendor cylinder-unwrap pages (HALCON / Cognex) — the cylinder method here is our own.
- Patents Cognex US9104932 / US9734376, US9552506 and MVTec US7953290 are cited from
  INDUSTRY-METHODS.md and the inventory, not re-opened by me. US8422759 and US9286669 (Omron,
  active) were opened in the earlier pass for summary only; claims not read.
- MV-CS200 full-frame rate ([S], not opened). Stokes / DoLP formulas are textbook, written from
  knowledge (the Wikipedia polarisation page fetched did not contain them).
- The noise formulas σ ≈ σ_noise / (|∇I| √N) are first-order estimates of ours, not from a source;
  the truth set (T) is what proves the targets.
- No vendor publishes locator or caliper accuracy with a described test; the 0.05 px / 0.05° and
  0.1 px targets come from our requirements, not from a competitor's measured figure. DALSA's
  "1/50 pixel" is a vendor claim.
- Effort figures are estimates for one developer with an AI assistant; they exclude UI design time
  in Vision Studio beyond each tool's property page.
