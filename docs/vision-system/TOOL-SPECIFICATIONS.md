# Tool specifications

This document specifies every tool in the vision system: what it is for, what it takes in and
gives out, its parameters, what one click teaches, when it passes or fails, and what it tells the
operator when it fails. It follows the free Base tool and the 12 licensable parent tools of
[REQUIREMENTS-VISION-SYSTEM.md](../REQUIREMENTS-VISION-SYSTEM.md) section 4, which wins where the
two disagree. The tool interface itself (handles, result structs, threading) is in
[ARCHITECTURE.md](ARCHITECTURE.md); how parameters are stored is in
[JOB-FILE-FORMAT.md](JOB-FILE-FORMAT.md). Every speed and accuracy figure here is a **target**,
not a measured result, unless it says otherwise. Targets are confirmed by
[TEST-ACCEPTANCE-PLAN.md](TEST-ACCEPTANCE-PLAN.md).

## Contents

- [1. Common tool contract](#1-common-tool-contract)
- [2. Licence codes and libraries](#2-licence-codes-and-libraries)
- [Tool 0. Base (free)](#tool-0-base-free)
- [Tool 1. Locate](#tool-1-locate)
- [Tool 2. Measure](#tool-2-measure)
- [Tool 3. Calibrate](#tool-3-calibrate)
- [Tool 4. Blob & Count](#tool-4-blob--count)
- [Tool 5. Code Reader](#tool-5-code-reader)
- [Tool 6. Code Verify](#tool-6-code-verify)
- [Tool 7. OCR / OCV](#tool-7-ocr--ocv)
- [Tool 8. Inspect](#tool-8-inspect)
- [Tool 9. Colour](#tool-9-colour)
- [Tool 10. AI Deep Learning](#tool-10-ai-deep-learning)
- [Tool 11. Multi-Camera & Enterprise](#tool-11-multi-camera--enterprise)
- [Tool 12. Scripting & Advanced Logic](#tool-12-scripting--advanced-logic)
- [Cross-tool summary](#cross-tool-summary)
- [Open questions](#open-questions)

## 1. Common tool contract

Every sub-tool implements the same contract, so the job designer, the Run screen, the SDK and the
PLC mapping treat all tools alike. The binary interface is defined in
[ARCHITECTURE.md](ARCHITECTURE.md); this section states what each part means for a tool author.

| Part | What every tool provides |
| --- | --- |
| Tool ID | A stable dotted name, for example `locate.geometric`. Used in job files, explain keys, logs and the SDK. Never renamed within a major version. |
| Typed inputs | Named inputs of the types below. An input is either set in the tool or wired from another tool's output. |
| Typed outputs | Named outputs with a type and a unit. Every tool also gives the four standard outputs: `Pass`, `Confidence`, `TimeMs`, `Explain`. |
| Parameters | Each marked **Essential** (shown by default, at most 5 per tool, see [UI-UX-SPEC.md](UI-UX-SPEC.md)) or **Advanced** (behind the Advanced switch). Every parameter has a type, default, range and unit. |
| Graphics | Overlay primitives (box, rotated box, line, circle, arc, polygon, cross, text, mask) in image or fixture coordinates. Colour plus shape: green tick for pass, red cross for fail, yellow "!" for warnings. |
| Pass/fail | A tool passes when its own criteria hold. A tool that could not run (missing input, no licence, bad parameter) returns **Error**, which is never shown as OK. |
| Confidence | 0 to 100 for every result. Each tool states how it maps its native score to 0–100, so one pass limit (default 50 unless the tool says otherwise) means roughly the same everywhere. |
| Timing | Wall time of the tool's own work in ms, excluding waiting for inputs. |
| Auto-learn | From a clicked point or a drawn box, the tool proposes its ROI and parameters from the current image. |
| Auto-tune | Given OK and NG images, the tool searches its tunable parameters to maximise the margin between the worst OK and the best NG (see below). |
| Explain | On failure, a key such as `explain.caliper.no_edge` with named arguments, plus the region to highlight. Strings live in the resource files of [LOCALISATION.md](LOCALISATION.md). |
| CPU and GPU | Classical tools give bit-identical results on CPU in the vision application and the SDK. Where a GPU path exists, results stay within 0.02 px, 0.02° and 1 confidence point of the CPU path (target). |

**Common data types**

| Type | Meaning |
| --- | --- |
| `Image` | Mono 8/16-bit or colour (RGB 8-bit), with width, height, stride, timestamp, camera ID and frame ID. Passed without copying where possible. |
| `Region` | ROI: rectangle, rotated rectangle, circle, annulus, annulus sector, polygon or mask. Optionally relative to a fixture. |
| `Pose` | x, y (px or mm), angle (°), scale. Maps model or fixture space to image space. |
| `Fixture` | A named coordinate space defined by a `Pose`. Tools placed in a fixture move with the part. |
| `Calib` | Pixel-to-world mapping, including lens distortion, from Tool 3. |
| `Point`, `Line`, `Circle`, `Ellipse` | Geometry in image px, and in mm when a `Calib` is present. |
| `Number`, `Bool`, `Text`, `List<T>` | Plain values. Numbers always carry a unit. |

**Units.** Outputs are in px and degrees. When a `Calib` input is wired, every length output is
also given in mm (or inch, as the job sets). Angles are in degrees, counter-clockwise positive,
image x to the right and y down.

**Auto-tune method.** The tool lists its tunable parameters and their ranges. The tuner runs the
tool over the sample set with a coarse grid, then a local search around the best point. The score
is `margin = min(OK confidence) − max(NG confidence)`, with run time as a tie-breaker against the
job's cycle-time target. With OK images only, it maximises the lowest OK confidence and keeps
1.5× the run-time headroom. The result is shown as "Before / After" with the new margin; nothing
changes until the user accepts.

**Auto-tolerance.** For numeric outputs, limits are proposed as mean ± k·σ over 10–30 good parts
(default k = 4), as in the requirements section 5. Each tool lists which outputs get limits.

**Explain keys.** Keys follow `explain.<tool>.<reason>`. English texts below are examples; the
final wording is set in [LOCALISATION.md](LOCALISATION.md). Every text says what to do, not what
went wrong internally. Arguments appear as `{name}`.

**Block layout used below.** Each sub-tool has: purpose and operator name; inputs and outputs;
parameters; auto-learn and auto-tune; pass/fail and explain keys; targets and algorithm. Standard
outputs (`Pass`, `Confidence`, `TimeMs`, `Explain`) and the common inputs (`Image`, `Region`,
`Fixture`) are not repeated unless a tool uses them differently. Cycle-time targets assume a 5 MP
mono image on T1 hardware unless stated.

## 2. Licence codes and libraries

One serial prefix and one DLL per parent tool. Serial format and activation are in
[LICENSING.md](LICENSING.md); the DLL ABI is in [SDK-API.md](SDK-API.md).

| # | Parent tool | Serial prefix | Library (Windows) | Depends on |
| --- | --- | --- | --- | --- |
| 0 | Base (free) | `VS-BASE-` (issued free, no charge) | `vs_core.dll`, `vs_base.dll` | — |
| 1 | Locate | `VS-LOC-` | `vs_locate.dll` | Base |
| 2 | Measure | `VS-MEA-` | `vs_measure.dll` | Base |
| 3 | Calibrate | `VS-CAL-` | `vs_calib.dll` | Base |
| 4 | Blob & Count | `VS-BLB-` | `vs_blob.dll` | Base |
| 5 | Code Reader | `VS-COD-` | `vs_code.dll` | Base |
| 6 | Code Verify | `VS-VER-` | `vs_verify.dll` | Base, `vs_code.dll` for the reference decode (see open questions) |
| 7 | OCR / OCV | `VS-OCR-` | `vs_ocr.dll` | Base |
| 8 | Inspect | `VS-INS-` | `vs_inspect.dll` | Base |
| 9 | Colour | `VS-COL-` | `vs_colour.dll` | Base |
| 10 | AI Deep Learning | `VS-DL-` | `vs_dl.dll` | Base, AI inference runtime |
| 11 | Multi-Camera & Enterprise | `VS-MCE-` | `vs_multicam.dll` | Base |
| 12 | Scripting & Advanced Logic | `VS-SCR-` | `vs_script.dll` | Base |

The Base sub-tools live in `vs_base.dll`. `vs_core.dll` holds the common image, region, result and graphics model, the camera layer
binding, the base PLC protocols, and the AI assistant features that every licence includes
(auto-find, auto-learn, auto-tolerance, auto-light check, drift monitor; see
[AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md)). A sub-tool that needs another parent's library (for
example Inspect's label check uses Locate's matcher internally) loads it for internal use only;
the user-facing tool still checks only its own parent licence.

---

## Tool 0. Base (free)

Operator names: **Camera**, **Clean image**. Ships with every installation. Phase P1.

### 0.1 Image source (`base.source`) — "Camera"

Purpose: deliver one image per trigger from a camera, a file or a folder.

- Inputs: trigger (software trigger from the PLC handshake, free-run timer, or manual).
- Outputs: `Image`, `FrameId` (int), `Timestamp` (µs), `CameraId` (text), `Dropped` (int, frames lost since start).

| Parameter | Type | Default | Range | Level |
| --- | --- | --- | --- | --- |
| Source | enum | Camera | Camera / File / Folder / Simulator | Essential |
| Camera | device ID | first found | GigE Vision or USB3 Vision device | Essential |
| Acquisition | enum | PLC trigger | PLC trigger / Free run / Timed | Essential |
| Timed interval | ms | 100 | 10–60 000 | Advanced |
| Folder order | enum | Name | Name / Date / Random; loop on/off | Advanced |
| Buffer count | int | 8 | 2–64 | Advanced |
| Grab timeout | ms | 1000 | 50–10 000 | Advanced |
| Auto reconnect | bool | on | — | Advanced |

- Auto-learn: lists cameras as thumbnails; one tap selects one and runs 0.2 auto setup.
- Pass/fail: passes when a complete frame arrives within the timeout.
- Explain: `explain.source.timeout` = "No picture from the camera. Check the cable and the camera power." · `explain.source.incomplete` = "The picture arrived broken. Check the network cable; ask the engineer to check jumbo frames." · `explain.source.not_found` = "Camera {name} is not connected. Plug it in or choose another camera." · `explain.source.folder_empty` = "This folder has no pictures."
- Targets: software trigger to exposure start ≤ 2 ms on the PC side, excluding PLC and network time (measured in P1). Zero dropped frames at the camera's rated frame rate on a dedicated NIC.
- Algorithm: GenICam/GenTL producer per camera, one acquisition thread per camera, a ring of pre-allocated buffers handed to the job without copying. Trigger source is always `Software` (or `Action` under Tool 11). Hardware trigger lines, strobe outputs and camera digital I/O are not exposed.

### 0.2 Camera settings (`base.camera`) — "Camera"

Purpose: set and auto-tune exposure, gain, white balance and transport for GigE Vision and USB3
Vision cameras.

- Inputs: camera handle from 0.1, optional ROI for the auto functions.
- Outputs: `Exposure` (µs), `Gain` (dB), `FocusScore` (0–100), `Brightness` (0–255 mean in ROI), `Saturated` (% of pixels at maximum).

| Parameter | Type | Default | Range | Level |
| --- | --- | --- | --- | --- |
| Exposure | µs | from auto setup | camera limits | Essential |
| Gain | dB | 0 | camera limits | Essential |
| Auto setup | button | — | runs auto exposure, white balance, focus assist | Essential |
| Pixel format | enum | Mono8 / BayerRG8 | Mono8, Mono10/12 (packed), Bayer 8/10/12, RGB8 | Advanced |
| Sensor ROI, binning | box, int | full, 1 | camera limits | Advanced |
| Black level, gamma | float | camera default | camera limits | Advanced |
| White balance R/G/B | float | from auto | 0.5–4 | Advanced |
| Packet size (GigE) | bytes | 8192 if NIC allows, else 1500 | 576–9000 | Advanced |
| Inter-packet delay (GigE) | ticks | auto | 0–camera limit | Advanced |
| PTP enable (GigE) | bool | off (on in synchronised jobs) | — | Advanced |
| Throughput limit (USB3) | MB/s | camera max | camera limits | Advanced |
| Frame rate limit | fps | off | camera limits | Advanced |

- Auto-learn (Auto setup): auto exposure brings the mean of the ROI to 50–60% of full scale with ≤ 0.5% saturated pixels, preferring exposure over gain up to the job's motion-blur exposure limit; auto white balance on a user-tapped white or grey area; focus assist shows a live `FocusScore` bar with "turn until the bar is highest" and records the peak.
- Auto-tune: with sample OK images, picks exposure and gain that maximise the downstream tools' lowest confidence (uses the job, not just brightness).
- Pass/fail: settings tool passes unless the camera rejects a value. It warns (not fails) at runtime when brightness drifts more than 15% from the taught value.
- Explain: `explain.camera.too_dark` = "Image is too dark. Turn the light up or press Auto Light." · `explain.camera.too_bright` = "Image is too bright. Turn the light down or press Auto Light." · `explain.camera.blur` = "Image is blurred. Turn the focus ring until the bar is highest." · `explain.camera.bandwidth` = "The camera is sending too much data. Ask the engineer to use a separate network port."
- Targets: auto setup completes in ≤ 3 s at 30 fps.
- Algorithm: GenICam SFNC feature names (`ExposureTime`, `Gain`, `PixelFormat`, `GevSCPSPacketSize`, `DeviceLinkThroughputLimit`, `PtpEnable`). Auto exposure by bisection on mean level with a saturation cap, sequential frames at one setting each (see [../PATENTS.md](../PATENTS.md) for the per-pixel-group gain patent this avoids). Focus score = mean of the top 10% Sobel magnitudes normalised by local contrast, as in the existing engine's sharpness measure. Settings are saved in the job and written back on job load. The PLC switches lighting; this tool never drives a light.

### 0.3 Image convert (`base.convert`) — "Clean image"

Purpose: change pixel type or colour form so later tools get what they need.

- Inputs: `Image`. Outputs: `Image`.
- Parameters: Mode (enum: To grey / Bayer demosaic / Extract channel R, G, B, H, S, V, L, a, b / 16→8-bit scale; default To grey; Essential), Demosaic quality (Fast bilinear / Edge-aware; default Edge-aware; Advanced), Bit window for 16→8 (auto from histogram; Advanced).
- Auto-learn: none needed; inserted automatically when a tool needs grey and the camera is colour.
- Pass/fail: always passes unless the input is missing. Explain: `explain.convert.no_colour` = "This camera gives black-and-white pictures. Colour tools need a colour camera."
- Targets: ≤ 4 ms for 5 MP demosaic on T1. Algorithm: OpenCV/IPP colour conversion; edge-aware (VNG-class) demosaic.

### 0.4 Crop / ROI (`base.crop`) — "Clean image"

Purpose: cut a region out of the image, optionally rotated with the fixture, to speed up later tools.

- Inputs: `Image`, `Region`, optional `Fixture`. Outputs: `Image` (cropped, axis-aligned to the region), `Pose` (crop to source).
- Parameters: Region (Essential), Follow fixture (bool, on; Essential), Interpolation (Nearest / Bilinear / Bicubic, Bilinear; Advanced), Fill outside image (0–255, 0; Advanced).
- Auto-learn: drawn box becomes the region; snaps to the nearest strong edges within 10 px.
- Pass/fail: fails when more than 25% of the region falls outside the image. Explain: `explain.crop.outside` = "The part is too near the picture edge. Move the part to the middle."
- Targets: ≤ 1 ms for a 1 MP rotated crop. Algorithm: affine warp with the fixture pose; output carries its pose so results map back to the full image.

### 0.5 Histogram (`base.histogram`) — "Camera"

Purpose: measure grey-level distribution in a region and hint when the image is too dark or bright.

- Inputs: `Image`, `Region`. Outputs: `Histogram` (List<int>, 256 or 65 536 bins), `Mean`, `StdDev`, `Min`, `Max`, `Median` (grey levels), `Saturated` (%), `Dark` (% below 5).
- Parameters: Region (Essential), Low limit and high limit on mean (grey level, off; Essential), Bin count (Advanced).
- Auto-learn: over 10–30 good parts, proposes mean limits by auto-tolerance.
- Pass/fail: within the mean limits when they are set. Live hint shown always: "too dark" when mean < 40 or > 30% dark; "too bright" when > 2% saturated.
- Explain: `explain.histogram.too_dark` = "Image is too dark. Turn the light up or press Auto Light." · `explain.histogram.too_bright` = "Image is too bright. Turn the light down or press Auto Light." · `explain.histogram.out_of_range` = "Brightness here is {value}, expected {low}–{high}. Check the light and the part."
- Targets: ≤ 1 ms per 5 MP. Algorithm: integer histogram, vectorised.

### 0.6 Flat-field (`base.flatfield`) — "Clean image"

Purpose: remove uneven lighting and lens shading.

- Inputs: `Image`, optional taught reference. Outputs: `Image`.
- Parameters: Method (Taught reference / Rolling background; default Rolling background; Essential), Background size (px, auto = 4× the largest feature; 8–512; Advanced), Target level (grey, 160; 50–240; Advanced), Dark frame (taught or none; Advanced).
- Auto-learn: "Teach white": capture a plain white or grey target, stored as the reference. Without it, the background size is set from the smallest feature the downstream tools need.
- Pass/fail: passes unless the reference does not match the image size. Explain: `explain.flatfield.reference_mismatch` = "The white reference is from another camera setting. Teach white again."
- Targets: ≤ 6 ms for 5 MP on T1. Algorithm: divide by a smoothed reference (or a large median / morphological background); same idea as the engine's `CR_STEP_FLAT`.

### 0.7 Filters (`base.filter`) — "Clean image"

Purpose: smooth, sharpen or de-noise an image before other tools.

- Inputs: `Image`, optional `Region`. Outputs: `Image`.
- Parameters: Filter (Smooth Gaussian / Smooth box / Median / Sharpen unsharp mask / Local contrast CLAHE / Edge Sobel; default Smooth Gaussian; Essential), Strength (0–100, 30; Essential; mapped to kernel size, sigma or amount), Kernel size (px, 3–31 odd; Advanced), Sigma (px, 0.5–10; Advanced), CLAHE tiles and clip limit (Advanced).
- Auto-learn: "Make it clearer" button tries each filter at three strengths and keeps the one that most raises the downstream tool's confidence.
- Pass/fail: always passes. Explain: none needed.
- Targets: 3×3 Gaussian ≤ 2 ms, 5×5 median ≤ 6 ms, CLAHE ≤ 8 ms for 5 MP on T1. Algorithm: OpenCV/IPP separable kernels; same filters the engine's step chain uses (`CR_STEP_CLAHE`, `CR_STEP_SHARPEN`).

### 0.8 Morphology (`base.morph`) — "Clean image"

Purpose: grow, shrink, open or close bright or dark regions.

- Inputs: `Image` (grey or binary). Outputs: `Image`.
- Parameters: Operation (Erode / Dilate / Open / Close / Top-hat / Black-hat; Close; Essential), Size (px, 3; 1–51; Essential), Shape (Square / Disc / Line at angle; Disc; Advanced), Iterations (1; 1–10; Advanced).
- Auto-learn: none. Pass/fail: always passes.
- Targets: 5×5 disc ≤ 5 ms for 5 MP on T1. Algorithm: van Herk/Gil-Werman for rectangles, decomposed discs.

### 0.9 Threshold (`base.threshold`) — "Clean image"

Purpose: turn a grey image into black and white.

- Inputs: `Image`, `Region`. Outputs: `Image` (binary), `Level` (grey level used).
- Parameters: Method (Auto Otsu / Fixed / Band / Local adaptive; Auto Otsu; Essential), Level (0–255, 128; Essential when Fixed), Polarity (Dark objects / Light objects; Dark; Essential), Local window (px, 31; 3–255; Advanced), Offset (grey, 5; −50–50; Advanced).
- Auto-learn: click on an object: polarity from the click, level from Otsu inside the drawn box.
- Pass/fail: always passes. Warns `explain.threshold.flat` = "Light and dark are too close here. Improve the light or use Local mode." when the two Otsu classes differ by less than 15 grey levels.
- Targets: ≤ 2 ms global, ≤ 6 ms adaptive for 5 MP on T1. Algorithm: Otsu, integral-image local mean.

### 0.10 Image arithmetic (`base.arith`) — "Clean image"

Purpose: combine two images (subtract a background, average frames, mask).

- Inputs: `ImageA`, `ImageB` (same size). Outputs: `Image`.
- Parameters: Operation (Subtract / Absolute difference / Add / Average / Min / Max / AND / OR / Multiply by mask; Absolute difference; Essential), Scale and offset (1.0, 0; Advanced).
- Pass/fail: fails when sizes differ. Explain: `explain.arith.size` = "The two pictures are different sizes. Use the same camera setting for both."
- Targets: ≤ 2 ms for 5 MP. Algorithm: saturating SIMD arithmetic.

### 0.11 Polar unwrap (`base.polar`) — "Clean image"

Purpose: unroll a ring (cap, bottle neck, O-ring) into a straight strip.

- Inputs: `Image`, annulus `Region`, optional `Fixture`. Outputs: `Image` (strip, angle across, radius down), `Pose` mapping.
- Parameters: Annulus centre, inner and outer radius (Essential), Start angle and direction (0°, counter-clockwise; Advanced), Angular resolution (auto = outer circumference in px; Advanced).
- Auto-learn: click on a round feature: 2.3 Find circle sets centre and radii.
- Pass/fail: fails when the annulus leaves the image. Explain: `explain.polar.outside` = "The ring is not fully in the picture. Centre the part."
- Targets: ≤ 3 ms for a 1 MP strip. Algorithm: precomputed bilinear lookup table, rebuilt only when geometry changes.

### 0.12 Affine transform (`base.affine`) — "Clean image"

Purpose: rotate, scale, shift or mirror an image, or straighten it to a fixture.

- Inputs: `Image`, `Pose` or explicit values. Outputs: `Image`.
- Parameters: Rotation (°), scale, shift, mirror (all Essential when not wired), Interpolation (Bilinear; Advanced), Output size (same as input; Advanced).
- Pass/fail: always passes. Targets: ≤ 5 ms for 5 MP bilinear on T1. Algorithm: IPP/OpenCV `warpAffine`.

### 0.13 Overlay (`base.overlay`) — no operator name (shown on every result)

Purpose: draw extra text, shapes or values on the result image for the operator and saved images.

- Inputs: any outputs to display, optional `Fixture`. Outputs: graphics only.
- Parameters: Items (list of shape or text with position, colour, size; Essential), Show on (Run screen / Saved image / Both; Both; Advanced), Burn into saved image (off; Advanced).
- Pass/fail: none (always passes). Targets: ≤ 0.5 ms. Algorithm: vector graphics rendered by the viewer; rasterised only when burnt in.

### 0.14 Result logic (`base.logic`) — "OK / NG"

Purpose: combine tool results into the part's OK/NG and the reason sent to the PLC.

- Inputs: `Pass` and values from any tools. Outputs: `Pass` (part result), `NgCode` (int, first failing rule), `NgText` (explain key of the first failing tool).
- Parameters: Rule (All must pass / Any passes / Custom AND-OR tree; All must pass; Essential), Reason order (tool order or priority list; Advanced), Treat Error as NG (always on, read-only).
- Auto-learn: created automatically with "All must pass" over every tool in the job.
- Pass/fail: as the rule. A tool in Error always makes the part NG with `explain.logic.tool_error` = "Tool {tool} could not run. Call the engineer."
- Targets: ≤ 0.1 ms. Algorithm: boolean tree. Formulas and state machines are Tool 12.

### 0.15 Logging (`base.log`) — "Save results"

Purpose: record each inspection and its image for traceability and reports.

- Inputs: any outputs, `Image`. Outputs: `RecordId`.
- Parameters: Save images (All / NG only / Every Nth / None; NG only; Essential), N (10; Advanced), Disk quota (GB, 50; Advanced), Image format (BMP / PNG / JPEG quality 90; PNG; Advanced), CSV export (off; Advanced), Fields (all outputs; Advanced).
- Pass/fail: never changes the part result; on disk full it raises an alarm `explain.log.disk_full` = "The disk is full. Old pictures are being deleted. Tell the supervisor."
- Targets: never blocks the inspection thread; writes queue in the background (queue full drops images, not records). Algorithm: SQLite or PostgreSQL record per part, files written by a background writer, quota clean-up oldest first.

---

## Tool 1. Locate

Operator name: **Find the part**. Licence `VS-LOC-`. Phase P1 (AI locator P3). Requirements: one-click
teach, rotation ±180°, scale 0.8–1.2, 30% occlusion, subpixel pose, 0.05 px / 0.05° repeatability
(target).

### 1.1 Correlation match (`locate.ncc`) — "Find the part"

Purpose: find a taught picture of the part by grey-level similarity; best for textured parts
with few clear edges.

- Inputs: `Image`, search `Region`. Outputs: `Pose` (x, y px; angle °; scale), `Score` (0–1), `Fixture`.
- Parameters:

| Parameter | Type | Default | Range | Level |
| --- | --- | --- | --- | --- |
| Model | taught image + mask | — | — | Essential |
| Accept score | 0–100 | 60 | 10–99 | Essential |
| Angle range | ° | ±15 | 0–180 | Essential |
| Scale range | ratio | 1.0–1.0 | 0.8–1.2 | Advanced |
| Pyramid levels | int | auto | 1–6 | Advanced |
| Contrast invariance | enum | Normalised | Normalised / Ignore polarity | Advanced |
| Subpixel | bool | on | — | Advanced |

- Auto-learn: draw a box around the part; masks out low-texture areas, picks the pyramid depth from the model size and texture, sets the search region to the image and angle range to ±15°.
- Auto-tune: raises accept score and narrows angle range while every OK image still finds the part with margin ≥ 10 points.
- Pass/fail: best score ≥ accept score. Confidence = score × 100.
- Explain: `explain.locate.not_found` = "Part not found. Check that a part is there and the light is on." · `explain.locate.low_score` = "Part looks different from the taught one (score {score}). Teach again if the part has changed." · `explain.locate.edge` = "Part is partly outside the picture. Move it to the middle."
- Targets: ≤ 15 ms (±15°) and ≤ 40 ms (±180°) for a 200 × 200 px model in 5 MP on T1. Repeatability 0.05 px / 0.05° on static parts.
- Algorithm: normalised cross-correlation over an image pyramid, coarse-to-fine, angle step from model radius at each level; FFT correlation at the top level for large models; subpixel position by 2D quadratic fit of the score peak and angle by parabola fit across angle steps.

### 1.2 Geometric match (`locate.geometric`) — "Find the part"

Purpose: find the part by its edge shape, robust to lighting change, partial occlusion and
clutter. The default locator.

- Inputs: `Image`, search `Region`. Outputs: `Pose`, `Score`, `Coverage` (share of model edges found, 0–1), `Clutter` (extra edges, 0–1), `Fixture`.

| Parameter | Type | Default | Range | Level |
| --- | --- | --- | --- | --- |
| Model | taught edges + origin | — | — | Essential |
| Accept score | 0–100 | 50 | 10–99 | Essential |
| Angle range | ° | ±180 | 0–180 | Essential |
| Scale range | ratio | 1.0–1.0 | 0.8–1.2 | Essential |
| Edge contrast | grey levels | auto | 2–255 | Advanced |
| Polarity | enum | Same | Same / Ignore / Ignore locally | Advanced |
| Greediness | 0–1 | 0.8 | 0–1 | Advanced |
| Max overlap | 0–1 | 0.5 | 0–1 | Advanced |
| Fine pose | bool | on | — | Advanced |

- Auto-learn: draw a box (or one click; auto-find proposes the part outline). Edges are extracted with hysteresis at the auto contrast, short and noisy chains removed, the origin set at the model's centre of mass, and the pyramid depth from edge density. A "model quality" bar warns when the model is symmetric (angle ambiguous) or has too few edges.
- Auto-tune: sets the edge contrast and accept score to maximise the OK/NG margin; narrows angle and scale ranges to what the OK set shows plus 50%.
- Pass/fail: score ≥ accept score. Confidence = score × 100. Warns when coverage < 0.7 although score passed.
- Explain: `explain.locate.not_found` (as 1.1) · `explain.locate.occluded` = "Part is partly covered ({coverage}% visible). Remove what is covering it." · `explain.locate.symmetric` = "This part looks the same when turned. Add a feature that shows its direction to the box."
- Targets: ≤ 30 ms (±180°, scale 1.0) and ≤ 60 ms (±180°, scale 0.8–1.2) on T1; about half on T2. Repeatability 0.05 px / 0.05°, 100% find rate on the test set, 30% occlusion tolerated.
- Algorithm: own implementation of edge-gradient shape matching. Model = edge points with gradient direction at each pyramid level. Score = mean |cos| of the angle between model and image gradient directions (polarity option), evaluated at candidate poses with early termination (greediness). Coarse-to-fine tracking of candidates, then least-squares refinement of the pose against subpixel image edges (point-to-line distance, iterated). Check the shape-matching patents listed in [../PATENTS.md](../PATENTS.md) before release.

### 1.3 Multi-instance search (`locate.multi`) — "Find all parts"

Purpose: find every copy of a part (trays, blister packs, cases of bottles).

- Inputs: `Image`, `Region`, a 1.1 or 1.2 model. Outputs: `Poses` (List<Pose>), `Scores` (List), `Count` (int), `Fixtures` (List).
- Parameters: Expected count (0 = any; Essential), Max count (64; 1–1000; Advanced), Max overlap (0.3; Advanced), Sort by (Score / Row-major / Column-major / Distance from point; Row-major; Advanced), plus the model's own parameters.
- Auto-learn: draw a box around one part; the tool then finds all others and proposes the expected count from the image.
- Pass/fail: count equals expected count (or ≥ minimum) and each found score ≥ accept score. Confidence = lowest score × 100.
- Explain: `explain.locate.count_low` = "Found {found} of {expected} parts. Missing places are marked red." · `explain.locate.count_high` = "Found {found}, expected {expected}. Remove extra parts."
- Targets: ≤ 1.5× single-search time for up to 50 instances on T1. Algorithm: keep all candidates above threshold at the top pyramid level, non-maximum suppression by overlap, then refine each. When parts sit on a grid, a lattice fit to found poses names empty slots (the same method as the Code Reader's slot map).

### 1.4 Fixture (`locate.fixture`) — no operator name (wired automatically)

Purpose: define a part coordinate space so other tools move with the part.

- Inputs: `Pose` (from 1.x, 2.5 corner, 2.6 geometry, or two points). Outputs: `Fixture`.
- Parameters: Source (Pose / Point and angle / Two points / Point and line; Pose; Essential), Origin offset and angle offset (Advanced), Use scale (off; Advanced).
- Auto-learn: the job designer wires the first locator's pose into a fixture and places every new tool in it.
- Pass/fail: fails only when the input pose is missing; dependent tools then return Error with `explain.fixture.no_part` = "Part was not found, so this check was skipped."
- Targets: ≤ 0.05 ms. Algorithm: 2D rigid (or similarity) transform; tools store their ROIs in fixture space.

### 1.5 AI locator (`locate.ai`) — "Find the part"

Purpose: find parts whose appearance varies too much for 1.1 or 1.2 (natural products, printed
packs, reflective parts). Brief here; training and models are in
[AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md).

- Inputs: `Image`, `Region`. Outputs: `Poses`, `Scores`, `Boxes` (rotated), `Fixture`.
- Parameters: Model (Essential), Accept score (50; Essential), Max count (Advanced), Refine with geometric match (off; Advanced).
- Auto-learn: label 5–10 images with a box and an arrow for direction; training runs locally.
- Pass/fail and explain: as 1.1/1.3, plus `explain.locate.ai_unsure` = "The AI is not sure this is the part ({score}). Add this picture to the training set."
- Targets: ≤ 25 ms on T2 GPU, ≤ 80 ms on T1 (OpenVINO) for 5 MP. Algorithm: oriented-box detector exported to ONNX, optional geometric refinement for subpixel pose. Licensing: see open questions.

---

## Tool 2. Measure

Operator name: **Check size**. Licence `VS-MEA-`. Phase P1. Results in mm when a `Calib` is wired.
Tolerance bands drawn green and red on the image. Auto-tolerance applies to every numeric output.
Repeatability target ≤ 0.1 px (3σ) on static parts.

### 2.1 Caliper (`measure.caliper`) — "Check size"

Purpose: find one edge or a pair of edges along a short line and measure position or width.

- Inputs: `Image`, rotated-box `Region` (projection box), optional `Fixture`, `Calib`. Outputs: `Edges` (List<Point>, each with contrast), `Position` (px/mm along the box), `Width` (pair mode, px/mm), `Contrast` (grey levels).

| Parameter | Type | Default | Range | Level |
| --- | --- | --- | --- | --- |
| Mode | enum | Single edge | Single edge / Edge pair / All edges | Essential |
| Polarity | enum | Any | Dark→light / Light→dark / Any | Essential |
| Expected width (pair) | px/mm | from teach | > 0 | Essential |
| Tolerance (low/high) | px/mm | from auto-tolerance | — | Essential |
| Edge threshold | grey levels | auto | 1–255 | Advanced |
| Filter half-width | px | 2 | 1–20 | Advanced |
| Score by | enum | Contrast | Contrast / Position / Width match / Combined | Advanced |
| Projection length | px | box height | 1–box height | Advanced |

- Auto-learn: click on an edge: the box is placed perpendicular to the local gradient, 40 px long and 20 px wide, polarity from the edge; in pair mode, click between two edges and the box spans both.
- Auto-tune: edge threshold and filter half-width for the lowest position σ across OK images.
- Pass/fail: an edge (or pair) found with contrast ≥ threshold, and value within tolerance. Confidence = 100 × min(1, contrast / (3 × threshold)) × tolerance factor (100 at nominal, 50 at the limit).
- Explain: `explain.caliper.no_edge` = "No edge found here. Check the light or move the box." · `explain.caliper.weak_edge` = "The edge is faint. Improve the light or the contrast." · `explain.caliper.out_of_tolerance` = "Size is {value} {unit}, allowed {low}–{high}." · `explain.caliper.many_edges` = "Several edges found. Make the box smaller or clean the part."
- Targets: ≤ 0.2 ms per caliper. 0.05 px (3σ) on a clean edge.
- Algorithm: average pixels across the box into a 1D profile (bilinear sampling along the box), Gaussian derivative, peaks above threshold, subpixel position by parabola fit through the three samples at the peak, candidates ranked by the chosen scoring function.

### 2.2 Find line (`measure.line`) — "Check edge"

Purpose: fit a straight edge from many calipers along it.

- Inputs: `Image`, `Region` (line with a search width), `Fixture`, `Calib`. Outputs: `Line` (start, end, angle °), `Straightness` (max residual, px/mm), `PointsUsed` (int), `Angle` (°).
- Parameters: Line (drawn; Essential), Polarity (Essential), Caliper count (10; 3–200; Advanced), Search width (px, 20; Advanced), Outlier distance (px, 1.5; Advanced), Fit (Least squares / RANSAC then least squares / Tukey robust; RANSAC; Advanced), Min points used (60%; Advanced).
- Auto-learn: click on an edge: the line follows the edge as far as it stays straight (within 1 px), calipers spaced every 10 px.
- Pass/fail: share of points used ≥ minimum; angle and straightness within limits when set. Confidence = 100 × share of points used × mean contrast factor.
- Explain: `explain.line.not_found` = "Edge not found along the line. Check the light or move the line." · `explain.line.broken` = "Only {used}% of the edge was found. The edge may be damaged or dirty." · `explain.line.angle` = "Edge is at {value}°, allowed {low}–{high}°."
- Targets: ≤ 1 ms for 20 calipers. 0.05 px / 0.02° (target).
- Algorithm: calipers (2.1) along the line, RANSAC for inliers, total least squares on inliers, optional iterative Tukey reweighting.

### 2.3 Find circle / arc (`measure.circle`) — "Check hole"

Purpose: measure a hole, boss or arc: centre, diameter, roundness.

- Inputs: `Image`, annulus or arc `Region`, `Fixture`, `Calib`. Outputs: `Circle` (centre, radius), `Diameter` (px/mm), `Roundness` (max − min radius), `PointsUsed`.
- Parameters: Annulus (Essential), Polarity and direction (inward / outward; Essential), Diameter tolerance (Essential), Caliper count (36; 8–360; Advanced), Outlier distance (1.5 px; Advanced), Fit (RANSAC + geometric LS; Advanced).
- Auto-learn: click inside a hole: radial edge search finds the rim, the annulus is set to radius ± 15%.
- Pass/fail: as 2.2 plus diameter and roundness within tolerance.
- Explain: `explain.circle.not_found` = "Hole not found. Check that the hole is there and lit." · `explain.circle.out_of_round` = "The hole is out of round by {value} {unit}." · `explain.circle.diameter` = "Diameter is {value} {unit}, allowed {low}–{high}."
- Targets: ≤ 1.5 ms for 36 calipers. Diameter repeatability 0.05 px (target).
- Algorithm: radial calipers, RANSAC on 3-point circles, then algebraic fit (Taubin) as a seed and geometric least squares (Levenberg-Marquardt) on inliers.

### 2.4 Find ellipse (`measure.ellipse`) — "Check hole"

Purpose: measure oval features or circles seen at an angle.

- Outputs: `Ellipse` (centre, major, minor, angle), `Axes` (px/mm). Parameters as 2.3, plus Axis ratio limits (Advanced).
- Auto-learn: as 2.3; the tool switches 2.3 to 2.4 when the axis ratio is below 0.95.
- Explain: `explain.ellipse.not_found` as 2.3.
- Targets: ≤ 2 ms. Algorithm: direct least-squares ellipse fit (Fitzgibbon) on RANSAC inliers, then geometric refinement.

### 2.5 Find corner (`measure.corner`) — "Check corner"

Purpose: locate a corner where two edges meet; often used as a fixture.

- Inputs: two line regions or one L-shaped region. Outputs: `Point` (corner), `Angle` (between edges, °), `Line1`, `Line2`.
- Parameters: Edge regions (Essential), Expected angle (90°; Advanced), Angle tolerance (Advanced).
- Auto-learn: click near a corner: two lines fitted along the two strongest edge directions.
- Explain: `explain.corner.not_found` = "Corner not found. Check that the corner is in the box." · `explain.corner.angle` = "Corner angle is {value}°, expected {expected}°."
- Targets: ≤ 2 ms. Algorithm: two 2.2 fits, intersection.

### 2.6 Geometry (`measure.geometry`) — "Check distance / angle"

Purpose: compute distances, angles, intersections and perpendiculars between found features.

- Inputs: two or more of `Point`, `Line`, `Circle`, `Ellipse`; `Calib`. Outputs: `Value` (px/mm or °), `Point` (for intersections and projections), `Line` (for constructions).
- Parameters: Operation (Point–point distance / Point–line distance / Line–line angle / Line–line intersection / Perpendicular from point / Circle–circle distance / Midpoint / Line through two points / Circle through three points; Essential), Tolerance (Essential).
- Auto-learn: tap two features on the image; the tool offers the likely operations ("Distance between these?", "Angle between these?").
- Pass/fail: value within tolerance. Confidence = lower of the input confidences × tolerance factor.
- Explain: `explain.geometry.out_of_tolerance` = "{name} is {value} {unit}, allowed {low}–{high}." · `explain.geometry.parallel` = "The two edges are parallel, so they do not meet."
- Targets: ≤ 0.05 ms. Algorithm: closed-form geometry in world coordinates when calibrated (so lens distortion does not bias distances).

### 2.7 Edge width / gap (`measure.gap`) — "Check gap"

Purpose: measure many widths or gaps along a feature (strip width, slot, seam).

- Inputs: rotated `Region`, `Calib`. Outputs: `Widths` (List), `Min`, `Max`, `Mean` (px/mm), `MinPosition` (Point).
- Parameters: Polarity pair (Essential), Tolerance on min and max (Essential), Sample count (20; Advanced), Edge threshold (Advanced).
- Auto-learn: draw a box across the gap; polarity from the profile.
- Explain: `explain.gap.narrow` = "Gap is too narrow at the red mark ({value} {unit})." · `explain.gap.wide` = "Gap is too wide at the red mark ({value} {unit})."
- Targets: ≤ 1 ms for 20 samples. Algorithm: pair calipers (2.1) along the box.

### 2.8 Pin pitch (`measure.pitch`) — "Check pins"

Purpose: measure position, pitch and count of repeated features such as connector pins or leads.

- Inputs: rotated `Region`, `Calib`. Outputs: `Count`, `Pitches` (List), `Positions` (List<Point>), `Widths` (List), `WorstPitch`.
- Parameters: Expected count (Essential), Nominal pitch and tolerance (Essential), Polarity (Advanced), Min width (Advanced).
- Auto-learn: draw a box over the row; count and pitch measured from the image, nominal set from it.
- Explain: `explain.pitch.missing` = "Pin {index} is missing." · `explain.pitch.bent` = "Pin {index} is out of place by {value} {unit}." · `explain.pitch.count` = "Found {found} pins, expected {expected}."
- Targets: ≤ 1 ms for 40 pins. Algorithm: all-edges caliper, pair matching by polarity, pitch from centre differences.

### 2.9 Count edges / thread (`measure.edges`) — "Count edges"

Purpose: count edges or thread crests along a line and measure their spacing.

- Outputs: `Count`, `Spacings` (List), `MeanPitch`. Parameters: Line (Essential), Expected count or pitch (Essential), Min contrast (Advanced), Polarity (Advanced).
- Auto-learn: draw a line across the thread; count and pitch from the profile.
- Explain: `explain.edges.count` = "Counted {found} edges, expected {expected}." · `explain.edges.pitch` = "Spacing is {value} {unit}, expected {expected}."
- Targets: ≤ 0.5 ms; the first and last edge of a row within 0.1 px of the inner edges' accuracy on a soft image (1 px blur, 3.2 px between edges). Algorithm: 1D profile, Gaussian derivative peaks, period from autocorrelation for a robust mean pitch. An end edge's response is overlapped by a neighbour on one side only and would lean outwards (0.35 px at that blur and spacing): its lean is found by drawing the row again from the edge's own free side, mirrored as the shared response, and taken off; no blur model is assumed.

---

## Tool 3. Calibrate

Operator name: **Set real size**. Licence `VS-CAL-`. Phase P1. Every calibration reports its
residual error in µm and is stored in the job with the camera serial and settings it belongs to.
A changed camera serial, lens setting or sensor ROI invalidates it and the job warns.

### 3.1 Grid calibration (`calib.grid`) — "Set real size"

Purpose: compute pixel-to-mm mapping, lens distortion and plate pose from a checkerboard or dot
grid.

- Inputs: one or more `Image` of the plate. Outputs: `Calib`, `ResidualUm` (RMS and max, µm), `PxPerMm` (at the plate plane), `Coverage` (% of field covered by detected points).

| Parameter | Type | Default | Range | Level |
| --- | --- | --- | --- | --- |
| Plate type | enum | Checkerboard | Checkerboard / Dot grid / Charuco-style coded board | Essential |
| Square or dot pitch | mm | from plate list | 0.1–100 | Essential |
| Lens model | enum | Radial 2 | None / Radial 2 / Radial 3 + tangential | Advanced |
| Calibration mode | enum | Single image, plane | Single image plane (Tsai) / Multi-pose (Zhang) | Advanced |
| Origin | enum | Plate corner | Plate corner / Clicked point / Fixture | Advanced |
| Max residual | µm | 0.25 × pixel size at plate | — | Advanced |

- Auto-learn (wizard): a live good/bad overlay shows found corners in green and missing ones in red, with hints: "Move the board to the left corner", "Tilt it a little". In multi-pose mode it asks for 5–15 positions until coverage ≥ 80%.
- Pass/fail: residual RMS ≤ max residual and coverage ≥ 60% (single image) or 80% (multi-pose).
- Explain: `explain.calib.board_not_found` = "Board not found. Make sure the whole board is in the picture and evenly lit." · `explain.calib.residual_high` = "Accuracy is {value} µm, needed {limit} µm. Hold the board flat and try again." · `explain.calib.coverage_low` = "The board covers only {value}% of the picture. Move it to the empty areas." · `explain.calib.invalid` = "The camera or lens was changed. Calibrate again."
- Targets: residual RMS ≤ 0.1 px equivalent on a good plate (target); compute ≤ 2 s for 15 images.
- Algorithm: checkerboard corners by saddle-point detection with subpixel refinement; dots by blob centroid with perspective correction of the ellipse centre. Zhang's method for intrinsics and distortion (Brown-Conrady), Tsai-style single-plane calibration when only one image is given, final Levenberg-Marquardt over all parameters. Undistortion map cached for 3.2.

### 3.2 Lens correction (`calib.undistort`) — "Straighten image"

Purpose: produce an image with lens distortion removed, and optionally rectified to the world
plane, for tools that do not accept a `Calib` directly.

- Inputs: `Image`, `Calib`. Outputs: `Image` (corrected), `Calib` (of the corrected image: pure scale).
- Parameters: Output (Undistort only / Rectify to world plane; Essential), Output scale (px/mm, auto; Advanced), Interpolation (Bilinear; Advanced).
- Pass/fail: fails when the calibration is invalid (see 3.1 explain).
- Targets: ≤ 10 ms for 5 MP on T1, ≤ 4 ms on T2 GPU. Algorithm: precomputed remap table.

### 3.3 N-point calibration (`calib.npoint`) — "Set real size"

Purpose: map pixels to world coordinates from 3 or more known points (fixture holes, stage
positions), when no plate is available.

- Inputs: list of (pixel point, world point) pairs; points can come from tools. Outputs: `Calib`, `ResidualUm`.
- Parameters: Model (Scale only / Similarity / Affine / Perspective; Affine; Essential), Points (Essential).
- Auto-learn: click features in the image and type their world coordinates; tool suggests the model from the point count (≥ 2 similarity, ≥ 3 affine, ≥ 4 perspective).
- Explain: `explain.npoint.too_few` = "Add at least {needed} points." · `explain.npoint.residual_high` (as 3.1).
- Targets: compute ≤ 1 ms. Algorithm: linear least squares (DLT for perspective, normalised), residual per point shown so a wrong point stands out.

### 3.4 Pixel-to-mm scale (`calib.scale`) — "Set real size"

Purpose: the simplest calibration: one scale from a known length in the image.

- Inputs: a measured length in px (from 2.x) or two clicked points. Outputs: `Calib` (scale only), `PxPerMm`.
- Parameters: Known length (mm; Essential), Separate X/Y scale (off; Advanced).
- Auto-learn: click both ends of a feature of known length, type the length.
- Explain: `explain.scale.warning` = "This is a simple scale. It ignores lens distortion and tilt; use the board for better accuracy."
- Targets: instant. Algorithm: ratio, optionally averaged over repeated measures.

### 3.5 Hand-eye 2D (`calib.handeye`) — "Teach robot"

Purpose: map camera coordinates to robot coordinates so the job can send part poses to a robot.

- Inputs: robot poses (from the PLC or typed) and the matching found poses of a target from 1.x. Outputs: `Calib` (camera→robot, 2D similarity or affine), `RotationCentre`, `ResidualUm`.
- Parameters: Camera mounting (Fixed / On robot; Essential), Motion plan (9-point grid plus 2 rotations; Advanced), Model (Similarity / Affine; Advanced).
- Auto-learn: the wizard lists the moves; the robot (via the PLC) moves to each, the job grabs and locates the target, and records both.
- Explain: `explain.handeye.inconsistent` = "Robot positions do not match the camera. Check the numbers sent by the robot." · `explain.handeye.target_lost` = "Target not found at move {index}."
- Targets: residual ≤ 2 px equivalent (target). Algorithm: least squares on point pairs; rotation centre from the circle through the target poses during pure rotations. 2D only; robot motion planning is out of scope.

### 3.6 Multi-camera stitching (`calib.stitch`) — "Join cameras"

Purpose: put two or more calibrated cameras into one world frame and, optionally, build one
stitched image. Requires Tool 11 for multi-camera jobs.

- Inputs: one 3.1 calibration per camera, each against a shared plate or plates with known offsets. Outputs: `WorldCalib` per camera, `StitchedImage` (optional), `SeamError` (µm at overlaps).
- Parameters: Plate layout (Essential), Output resolution (px/mm; Essential), Blend at seams (Feather / None; Advanced).
- Explain: `explain.stitch.seam` = "Cameras {a} and {b} disagree by {value} µm. Calibrate both again with the board still."
- Targets: stitched 2 × 5 MP ≤ 25 ms on T1. Algorithm: each camera's plate pose composed into the world frame; remap tables per camera into the shared raster; tools on the stitched image use world coordinates directly.

---

## Tool 4. Blob & Count

Operator name: **Count / spot**. Licence `VS-BLB-`. Phase P1.

### 4.1 Blob analysis (`blob.analyse`) — "Count / spot"

Purpose: find connected regions after a threshold and measure each.

- Inputs: `Image`, `Region`, `Fixture`, `Calib`. Outputs: `Blobs` (List of: area, centroid, perimeter, circularity, elongation, holes, bounding box, rotated box, angle, mean grey), `Count`, `TotalArea`.

| Parameter | Type | Default | Range | Level |
| --- | --- | --- | --- | --- |
| Threshold | enum + level | Auto (Otsu) | Auto / Fixed / Band / Local | Essential |
| Polarity | enum | Dark on light | Dark on light / Light on dark | Essential |
| Min area | px or mm² | from teach | ≥ 1 px | Essential |
| Max area | px or mm² | from teach | — | Advanced |
| Connectivity | enum | 8 | 4 / 8 | Advanced |
| Fill holes | bool | off | — | Advanced |
| Exclude touching ROI edge | bool | on | — | Advanced |
| Morphology clean-up | enum | none | none / open 3 / close 3 | Advanced |

- Auto-learn: click on a sample blob: polarity and threshold from it, area limits set to its area ± 50%, circularity and elongation limits ± 30% (see 4.4).
- Auto-tune: threshold and morphology to maximise the difference in counts between OK and NG, or to keep counts stable across OK images.
- Pass/fail: count within limits when set (else always passes; the values feed other tools). Confidence = 100 × (1 − share of blobs within 10% of a filter limit).
- Explain: `explain.blob.none` = "Nothing found. Check the light and the threshold." · `explain.blob.too_many` = "Found {found}, at most {max} allowed. Extra spots are marked red." · `explain.blob.too_few` = "Found {found}, at least {min} needed."
- Pass rules (built, VisionCore `BlobTool`): presence (any / present / absent), count range, every counted blob's area range, total area range. Filters leave blobs out; pass rules fail the part. Further reasons: `explain.blob.present` = "Spots were found where none are allowed. They are marked red." · `explain.blob.area` = "A spot is too small or too large. It is marked red." · `explain.blob.total_area` = "The spots together cover too little or too much." · `explain.blob.image_dark` = "The picture is almost black. Check the light and the camera." · `explain.blob.image_bright` = "The picture is almost white. Check the exposure, the light and that nothing covers the part." · `explain.blob.incomplete` = "Too many spots to measure in time. Check the threshold and the light." · `explain.blob.setup` = "This count tool is not set up so that a part can pass. Open the job and fix the tool." · `explain.blob.region` = "The area to check is partly outside the picture. Check the part's position and the area."
- Line safety: a dark or white frame (area mean below 8 or above 250), an area cut by the picture's edge, and a run that cannot finish within its time budget or with more than 20 000 blobs past the cheap filters are NG, never "no spots found". Blobs left out only for touching the area's edge still count against "none allowed", the highest count and the highest total area. Keep first N cannot be combined with those rules. Auto threshold keeps its level at least 15 grey levels from the background's mean, so a clean area's noise is never split and a small spot is still found.
- Measures (built, engine `vs_blob.h`): area, centroid, perimeter, circularity, compactness P²/(4πA), same-moment ellipse (axes, angle), elongation (major / minor axis), upright and turned box, holes, mean grey, touches edge. The perimeter's edge correction (+π) is exact for a disc; a square reads circularity 0.83 instead of π/4. Measured: 5 MP with 1 000 blobs in 6.9 ms on the development Mac (2026-10-31).
- Targets: ≤ 10 ms for 5 MP with up to 1 000 blobs on T1.
- Algorithm: threshold (0.9), run-length connected components, moments for centroid, angle and elongation (ratio of second-moment eigenvalues), perimeter by corrected crack length, circularity 4πA/P², holes by Euler number.

### 4.2 Contour analysis (`blob.contour`) — "Check outline"

Purpose: extract subpixel outlines and compare their shape (convexity, defects, curvature).

- Inputs: `Image` or `Blobs`. Outputs: `Contours` (List<polyline>), `Length`, `Convexity` (0–1), `Defects` (List of indent depth and position), `MaxDeviation` (vs taught outline).
- Parameters: Edge threshold (Essential), Smoothing (px, 1; Advanced), Taught outline (optional; Advanced), Max deviation (Essential when taught).
- Auto-learn: click a part; its outline is taught as reference.
- Explain: `explain.contour.dent` = "Outline has a dent of {value} {unit} at the red mark." · `explain.contour.not_closed` = "Outline is broken. Improve the contrast at the edge."
- Targets: ≤ 5 ms for a 2 000-point outline. Algorithm: subpixel marching squares on the smoothed image, Douglas-Peucker simplification for display only, convex hull defects, deviation by closest-point distance after pose alignment.
- Built (engine `vs_blob.h` contour calls, VisionCore `BlobTool` with `CheckOutline`): on the kept blobs of 4.1. The outline is the blob's boundary chain with each point moved along the normal to where the smoothed grey crosses the edge level (halfway between the blob and a 2 px ring round it unless set), instead of marching squares: one outline per blob, already in order. Outputs per blob: outline, length, enclosed area, convexity (area / hull area), defects (depth, deepest point, hull chord), edge share, closed, and with a taught outline the largest and mean deviation and the pose. Placement: centroid and axis of the enclosed area, then closest-point ICP (rigid, no scale, far pairs left out so a dent does not pull it; 12 start angles when the shape has no clear axis). Pass rules: largest dent, lowest convexity, largest deviation from the taught outline. Further reasons: `explain.contour.deviation` = "The outline differs from the taught one more than allowed. The place is marked red." · `explain.contour.convexity` = "The outline is not as full as needed: it has dents or gaps. Check the part." Line safety, whenever an outline rule is set: an outline cut by the area's edge (or a part left out for touching it), or with under 90% of its points on a grey edge, is `not_closed` (NG); the rules need a lowest count of 1 or more, so a missing part never passes them; the dent rule sees the deepest indent even when it is shallower than the listing depth; a run past the time budget, also inside one outline, is not checked.
- Built 2026-11-03 (Req 17.12 BLB-OUT-01, BLB-OUT-02): `outlineUnit` (`Px` default, `Mm`) sets the unit of the largest dent and largest deviation and of the `max_dent` and `max_deviation` values; `Mm` needs the camera's calibration (setup error without it: NG, never a guess) and the engine still measures in px, the limits being brought to px. Without a calibration the values stay in px and the details say so ("outline values in px (the camera has no real size)"). `BlobTool.AutoTolerance(config, goodImages, calibration)` measures the taught outline on 3 or more good images and suggests the largest deviation and dent as the worst good value times 1.5 plus 0.3 px, in the config's unit; it suggests nothing when an image shows no single closed, found outline or nothing is taught, and it never applies the limit itself.

### 4.3 Count (`blob.count`) — "Count items"

Purpose: count items with one setting: the simplest operator tool, built on 4.1.

- Inputs: `Image`, `Region`. Outputs: `Count`, `Positions` (List<Point>).
- Parameters: Expected count (Essential), Allowed difference (0; Essential). Advanced: all 4.1 parameters plus Split touching items (off / distance-transform watershed).
- Auto-learn: draw a box around the items and type or confirm the count shown.
- Pass/fail: |count − expected| ≤ allowed difference.
- Explain: `explain.count.missing` = "{missing} items missing. Empty places are marked." · `explain.count.extra` = "{extra} items too many."
- Targets: as 4.1. Algorithm: 4.1 with area filter; touching items split by distance-transform peaks and watershed.

### 4.4 Sort and filter (`blob.filter`) — "Count / spot"

Purpose: keep blobs whose features fall within limits and sort them.

- Inputs: `Blobs`. Outputs: `Blobs` (kept), `Rejected` (List), `Count`.
- Parameters: Filters (list of feature, min, max; Essential: area, circularity, elongation), Sort by (any feature, ascending or descending; Advanced), Keep first N (Advanced).
- Auto-learn: click one good blob and one bad blob; limits are set halfway between their features for each feature that separates them, and features that do not separate are left off.
- Explain: `explain.filter.rejected` = "Spot {index} was left out: {feature} is {value}, allowed {low}–{high}."
- Targets: ≤ 0.2 ms per 1 000 blobs. Algorithm: vectorised feature comparison.

---

## Tool 5. Code Reader

Operator name: **Read code**. Licence `VS-COD-`. Phase P2. Requirements: auto-detect symbology,
many codes per image, any orientation, read rate ≥ 99.9% on graded samples (target), AI rescue for
damaged codes.

### 5.0 Built from the existing engine

The Code Reader is the existing `codereader` engine (`engine/`, C API
`engine/include/codereader.h`, .NET wrapper `dotnet/CodeReader/`) wrapped in the common tool
contract. The engine keeps its own C API for existing users; `vs_code.dll` adds the contract
layer, the new symbologies and the licence check. Migration steps are in
[ARCHITECTURE.md](ARCHITECTURE.md).

**Carries over unchanged**

| Capability | Engine source of truth | Notes |
| --- | --- | --- |
| Formats: QR, Data Matrix ECC 200, Code 128 incl. GS1-128 / SSCC, EAN-13 | `cr_format` | `cr_format` values are already bit flags, so a multi-format mask is a small change |
| Locator modes: Auto, Classical (texture density), AI, AI then classical | `cr_locator`, `cr_load_model` | AI locator (CodeNet) ONNX model |
| AI enhancer for damaged codes, with placement Last / Early / Second pass / Off | `cr_enhance`, `cr_load_enhancer` | This is the requirements' "AI rescue" |
| Fix chain per region: plain, upscale 2× and 4×, CLAHE, sharpen, adaptive threshold, lighting flattening, deblur (two strengths), glare correction, libdmtx, QR grid fit, Data Matrix lattice (inside-out), 1D scan-line fusion at any angle, SSCC fit through blur, full-image passes | `cr_step` | Each read reports the step that read it; each 4 × 4 image zone learns its own step order |
| Multi-frame fusion of 2–5 frames | `cr_decode_frames` | |
| Expected count per image, serial-run matching of faint codes, reading with shared known content, slot map naming every unread slot | `cr_params.expected_count`, `match_serials`, `CR_STEP_MATCH`, `CR_STEP_KNOWN`, `cr_miss_info.slot_row/col` | Aggregation features; see 5.5 |
| Miss diagnosis: Cut off, Glare, Low contrast, Covered, Too small, Blur, No finder, Unknown, with measured sharpness, contrast, saturation, texture, module size, finder and quiet zone | `cr_miss_reason`, `cr_get_miss_info` | Becomes the explain keys in 5.6 |
| Quality grading after ISO/IEC 15415 and 29158 as process control (camera grey levels, not calibrated reflectance) | `cr_quality`, `grade_quality` | About 1 ms per code (engine note). Calibrated grading is Tool 6 (see 6.0) |
| Learned state per product (step order, code sizes, printer encoding), zone read map, calibration hints | `cr_get_learned`, `cr_set_learned`, `cr_get_zone_stats`, `hint_*` | Saved in the job per product variant |
| Time budget, thread count, live found-regions callback | `time_budget_ms`, `threads`, `cr_set_found_callback` | |
| Territory build switches for patent-sensitive parts (for example `CR_UNDERRES`) | build options | See [../PATENTS.md](../PATENTS.md); the licence must carry the territory |

**Added 2026-10-29** (PR #60): EAN-8, UPC-A, UPC-E, Code 39, Code 93, ITF / ITF-14, Codabar, GS1
DataBar, Micro QR, PDF417 and Aztec, read by zxing-cpp and, for the 1D types, by the engine's 1D
scan-line fusion at any angle. `cr_format` is a flag set (`CR_ALL_LINEAR`, `CR_ANY`); a read of a
type that is not enabled is never reported, even when zxing reads it with the same decoder (a
Micro QR or rMQR in a QR job; QR jobs that used to report those now drop them). rMQR is its own type
(`CR_RMQR`, added 2026-10-31, PR #158), read by zxing-cpp and, since 2026-11-03 (Req 17.1 GRD-02), graded by ISO/IEC 15415 with its own
fixed patterns (finder, sub-pattern, corner and alignment patterns, edge and vertical timing) and a
2-module quiet zone, so the Read code tool takes a lowest grade for it. A UPC-A symbol is
an EAN-13 with a leading 0: an EAN-13 job reports it as a 13-digit EAN-13. The 1D rules of 5.2 are
in `cr_params`: `min_length`, `max_length` (characters as printed, check digit included; not for
EAN/UPC, which have fixed lengths) and `check_digit` (as read / verify / strip, Code 39 modulo 43
and ITF modulo 10); the rules are applied before a read counts anywhere (the 1D scan's quota, the
whole-image pass's plausibility check). Teaching (auto-find) locks the type, the count and, for
types without a fixed length, the length; an ITF whose reads carry a correct check digit gets
"verify". An ITF tool with no length lock, check digit or expected text fails every part as a
setup error, since a partial ITF read is a valid shorter code. Code 39 is read as standard and full
ASCII only (no Code 32 or PZN conversion: the text stays as printed). Every type read here is
graded (Tool 6) and takes a lowest print grade (PR #150).

Pharmacode and DotCode (built 2026-10-30) have decoders of the engine's own: zxing-cpp 3.1 reads
neither. **Pharmacode** (`engine/src/pharmacode.cpp`, `CR_PHARMACODE`, `CR_PHARMACODE_REV`):
2-16 bars, values 3-131070, on the 1D band finder's scan lines. Bar widths are measured edge to
like edge (bar plus gap), so ink spread does not move them between narrow and wide. With no check
digit, a read stands only with gaps all alike, two clean width classes (or, for a code of one
width, a clear narrow or wide ratio: spread of a third to half a unit on narrow bars gives no
read), a light quiet zone of 4 gaps on both sides, and a bar count inside the tool's lock
(`min_length` and `max_length` both set; they count bars). Bars alone cannot tell a code of one
width under heavy ink spread from its all-wide twin, nor a pack turned round, so the Read code
tool also needs the value the code must read: teaching sets the bar count and the value read (a
tool without either is a setup error). The tool states the reading direction, never guessed,
since read the other way the bars give another valid value (`cr_params.pharma_direction`,
`PharmacodeDirection` in the Read code tool and the job file; added 2026-10-30, PR #127):
**across** (the default) for bars upright, standard = the bar on the right weighs least, "reverse"
for a code passing the camera turned round; **down** for bars lying flat (a code turned a quarter
turn), standard = the bottom bar weighs least (turned clockwise), "reverse" = the top bar (turned
counter-clockwise). Bars more than 75 degrees off the stated direction's upright (or flat) are not
read, and an unknown direction reads no Pharmacode (NG). **DotCode** (`engine/src/dotcode.cpp`, `CR_DOTCODE`): dot clusters, the lattice from the
dots' diagonal neighbours and an affine fit grown from the centre, the grid sampled and tried in
each rotation whose top-left point is a dot position; Reed-Solomon over GF(113) with unreadable
dot patterns as erasures, accepted only while two check words stay spare (one more per 16 checks); a symbol whose rotations
give two texts, a Structured Append part or a reader-programming symbol is not reported. GS1 data
comes out as for the other GS1 codes (FNC1 as ASCII 29). Neither type is graded yet.

Still to come from the table below: PDF417 row fusion,
Micro QR grid fit, EAN/UPC add-ons as a separate output, Codabar check digit, quiet-zone check and
the `ScanAngle` / `CheckDigitOk` outputs of 5.2, and print grading (Tool 6) of the new types.

**Added 2026-10-30** (PR #79): the 5.2 outputs and checks. Every code carries its reading angle
(`cr_code_ext.angle`, `ScanAngle`: degrees clockwise from left-to-right, 180 = upside down); 1D codes
carry `check_digit_ok` (`CheckDigitOk`: EAN/UPC, Code 128, Code 93 and DataBar always verified;
Code 39, ITF and Codabar when the tool verifies) and `quiet_zone_ok` (`QuietZoneOk`, not DataBar:
no mark of half a module or more within the type's quiet zone, 10 X, EAN-13 11 X / 7 X, EAN-8 7 X,
UPC-A 9 X, UPC-E 9 X / 7 X, 5 X after an add-on, measured on the median of five scan lines; a zone
off the image fails). Codabar's check digit is modulo 16 over the whole text, start and stop
characters included, and Strip removes the character before the stop character. EAN/UPC 2- and
5-digit add-ons are read whenever present and reported apart (`cr_code_ext.addon`, `AddOn`); the text
holds the main symbol only. The Quiet zone check is on by default, also for jobs saved before it
existed: a mark in the zone, or a zone that could not be measured, fails the code with
`explain.code1d.quiet_zone`. A short read whose missing code was dropped by the 1D rules says why
(`cr_stats_ext.rejected_check_digit` first, then `rejected_length`): `explain.code1d.check_digit` or
`explain.code1d.too_short`. Scan lines (1-64) stay automatic.

**Added 2026-10-30** (PR #99): the add-on check. EAN-13, EAN-8, UPC-A and UPC-E tools have
`AddOnRequired` and `ExpectedAddOn` (a 2- or 5-digit text, or a template with batch fields such as
`{ISSUE}`; setting it makes the add-on required). With either set, a code whose add-on is missing,
unreadable or in doubt (reads of the same main text disagreed) fails with
`explain.code1d.addon_missing`, and one whose add-on differs from the expected text fails with
`explain.code1d.addon_wrong`; the add-on is checked after the main text and GS1 checks. Either
setting on a tool of any other type is a setup error, as is a fixed expected add-on that is not 2 or
5 digits. A mis-segmented add-on cannot change the main text: the main symbol is decoded and its
check digit verified before the add-on is looked for, and the engine removes the add-on from the
text only when the text ends with exactly the add-on's digits.

**To be added** (the plan; see the note above for what is built)

| Family | Symbologies | Decoder plan |
| --- | --- | --- |
| EAN/UPC | EAN-8, EAN-13, UPC-A, UPC-E, 2- and 5-digit add-ons | zxing-cpp, through the engine's 1D scan-line fusion |
| Code 39 / Code 93 | Code 39 (standard, full ASCII, optional check digit), Code 93 | zxing-cpp |
| ITF | Interleaved 2 of 5, ITF-14 with bearer bars | zxing-cpp; length lock to reject short partial reads |
| Codabar | Codabar | zxing-cpp |
| GS1 DataBar | Omnidirectional, Truncated, Stacked, Limited, Expanded (and Expanded Stacked) | zxing-cpp |
| Pharmacode | Laetus one-track Pharmacode | Own decoder (bar width classes from the 1D profile) |
| Micro QR | M1–M4 | zxing-cpp; grid fit extended from the QR path |
| PDF417 | PDF417, optional MicroPDF417 | zxing-cpp; row-wise scan fusion for damaged rows |
| Aztec | Compact and full-range | zxing-cpp |
| DotCode | DotCode | Own decoder (dot grid from the lattice method, then RS); zxing-cpp has no DotCode reader |
| DPM | Dot-peen and laser-marked Data Matrix and QR on metal, plastic, glass | See 5.4 |

### 5.1 Read code (`code.read`) — "Read code"

Purpose: find and decode every code of the enabled types in the region.

- Inputs: `Image` (8-bit grey; colour converted by 0.3), `Region`, `Fixture`; optional extra frames for fusion. Outputs: `Codes` (List of: text, symbology, corners (4 points), angle °, step that read it, copies, quality grade 0–4 when on), `Count` (int), `FirstText` (text), `Misses` (List of region + reason, see 5.6), `Gs1` (parsed GS1 element strings when present).

| Parameter | Type | Default | Range | Level |
| --- | --- | --- | --- | --- |
| Code types | set | Auto (types seen at teach) | any of the symbologies above | Essential |
| Expected count | int | 1 | 0 (unknown)–2 000 | Essential |
| Expected text | pattern | none | exact / regex / GS1 AI rules / list | Essential |
| Time budget | ms | 200 | 10–5 000 | Essential |
| Locator | enum | Auto | Auto / Classical / AI / AI then classical | Advanced |
| AI enhancer | enum | Last | Last / Early / Second pass / Off | Advanced |
| Locator scale | int | 4 | 1–8 | Advanced |
| Max upscale | int | 2 | 1–3 | Advanced |
| Locator density | float | 0.12 | 0.05–0.4 | Advanced |
| AI threshold | float | 0.4 | 0.05–0.95 | Advanced |
| Whole-image pass | bool | on | — | Advanced |
| Gamma (whole-image pass) | float | 0.5 | 0.3–1.5 | Advanced |
| Grade quality | bool | off | — | Advanced |
| Minimum grade | enum | off | A–D | Advanced (when grading on) |
| Threads | int | 0 (all) | 0–64 | Advanced |
| Code size hints | modules, px | learned | — | Advanced (read-only by default) |

- Auto-learn: draw a box around a code (or tap the auto-find suggestion "Read this code?"): the tool decodes it with Code types = all, then locks the types to what it read, sets expected count to the number found, records module size and code size as hints, and proposes the expected-text pattern from the content (for example a GS1 GTIN + lot + expiry structure).
- Auto-tune: over OK images, chooses locator mode, enhancer placement and locator density for the highest read rate within the time budget, then the lowest time at that read rate. This is the existing tuning agent (`dotnet/CodeReader/TuningAgent.cs`) moved behind the contract.
- Pass/fail: count read = expected count (more codes than expected is NG: a foreign or extra pack in the case; expected count 0 sets no upper limit. With the count read, the engine still decodes the code-sized regions left, so an extra pack's code is read wherever it lies. A code seen more often than the product's label carries is NG too: an extra pack carrying a copy of a code in the case. That is judged from the product's symbols per code, or from a product learned over 20 cases to carry each code once, never from a guess), every text matches the expected pattern, and (when grading) every grade ≥ minimum. Confidence per code = 100 for a clean decode with no erasures, reduced by the share of error correction used and by later fix steps (target mapping in [ARCHITECTURE.md](ARCHITECTURE.md)); tool confidence = lowest code confidence, 0 when short.
- Explain: `explain.code.not_found` = "No code found. Check that the code is in the box and lit." · `explain.code.short` = "Read {found} of {expected} codes. Unread codes are marked red with the reason." · `explain.code.extra` = "Read {found} codes, {expected} expected. Check the case for a foreign or extra pack." · `explain.code.extra_copy` = "{count} codes seen more often than the label carries. Check the case for an extra pack with the same code." · `explain.code.wrong_text` = "Code reads {text}, expected {expected}." · `explain.code.low_grade` = "Code print quality is {grade}. Check the printer." · `explain.code.timeout` = "Reading took too long. Ask the engineer to check the time budget."
- Targets: single code in a 1 MP region ≤ 15 ms on T1; whole 5 MP image with up to 50 codes within the 200 ms default budget on T1. Read rate ≥ 99.9% on graded samples and ≥ the Cognex read rate on the shared damaged and DPM set of ≥ 1 000 images (requirements section 13).
- Algorithm: the engine's locate → per-region fix chain → decode → whole-image fallback, with learned step order per zone; zxing-cpp and libdmtx decoders plus the engine's own QR grid fit, Data Matrix lattice and Reed-Solomon erasure decoding (see [../RESEARCH-COGNEX-HALCON.md](../RESEARCH-COGNEX-HALCON.md)).

### 5.2 1D code specifics (`code.read`, linear types)

Purpose: rules that apply only to linear codes.

- Extra outputs: `ScanAngle` (°), `CheckDigitOk` (bool), `QuietZoneOk` (bool).
- Extra parameters (Advanced): Minimum length and maximum length (per type), Check digit (Verify / Strip / Ignore for Code 39, ITF, Codabar), Quiet zone check (on), Scan lines (auto; 1–64), Add-on required and Expected add-on (EAN/UPC; off, empty).
- Explain: `explain.code1d.quiet_zone` = "Something is printed too close to the barcode ends. Keep 10 bar widths clear." · `explain.code1d.check_digit` = "Check digit is wrong. The code may be misprinted." · `explain.code1d.too_short` = "Only part of the barcode was read. Make sure the whole barcode is in the box." · `explain.code1d.addon_missing` = "The small add-on barcode (2 or 5 digits) next to the main barcode is missing or could not be read. Check it is printed and inside the box." · `explain.code1d.addon_wrong` = "The add-on barcode holds the wrong number. Check the label is for this batch."
- Algorithm: the engine's band locator (gradient structure tensor) and median of many scan lines along the bars at any angle (`CR_STEP_LINEAR`), SSCC fit through blur confirmed by both check digits (`CR_STEP_LINEAR_SSCC`) extended to EAN/UPC and ITF-14.

### 5.3 2D code specifics (`code.read`, matrix and stacked types)

Purpose: rules that apply only to 2D and stacked codes.

- Extra outputs: `Modules` (rows × columns), `ModulePx`, `EccLevel`, `Mirrored` (bool), `Polarity`.
- Extra parameters (Advanced): Polarity (Auto / Dark on light / Light on dark), Mirror (Auto / Off), Module grid (Fixed / Flexible for wrinkled labels), Code size limits (modules).
- Explain: `explain.code2d.too_small` = "Code is too small in the picture ({value} px per square). Move the camera closer or use a longer lens." · `explain.code2d.no_finder` = "The code's corner pattern is damaged or covered."
- Algorithm: zxing-cpp for QR, Micro QR, PDF417, Aztec; libdmtx and the engine's lattice reader for Data Matrix; grid fit for faint QR; own DotCode reader.

### 5.4 DPM (`code.read`, DPM mode)

Purpose: read codes marked directly on parts by dot peen or laser.

- Extra parameters (Advanced): Mark type (Printed / Dot peen / Laser on metal / Laser on plastic / Inkjet dots), Dot size (px, learned), Surface (Flat / Curved, with cylinder axis).
- Auto-learn: classifies the mark type from the first read (dot spacing vs dot size, contrast polarity) and sets these.
- Explain: `explain.dpm.glare` = "Shiny surface is hiding the code. Use diffuse or dark-field light, or tilt the part." · `explain.dpm.low_contrast` = "Mark is too faint to read. Check the marking machine or the light angle."
- Algorithm: dots joined by gradient magnitude smoothed over about one module before locating (method of expired patent US7965899, see [../PATENTS.md](../PATENTS.md)); glare correction (`CR_STEP_GLARE`); CLAHE and deblur; lattice reader for damaged L patterns; cylindrical rectification for curved parts. Lighting advice comes from the miss diagnosis, since bright-field or dark-field switching is a PLC-driven lighting step.

### 5.5 Aggregation reading (`code.read`, case mode)

Purpose: read every code in a case or tray and name the missing ones by position (the existing
product's main use).

- Extra outputs: `Slots` (rows × columns map with read text or empty), `Duplicates` (List), `SerialRun` (first, last, gaps).
- Extra parameters: Layout (Free / Grid; Essential in case mode), Match faint codes to the serial run (off; Advanced), Read shared content (on; Advanced), Frames to fuse (1; 1–5; Advanced).
- Explain: `explain.case.slot_empty` = "No code at row {row}, column {col}. Check the item there." · `explain.case.duplicate` = "Code {text} appears twice in this case."
- Algorithm: the engine's slot map (lattice fitted to read code centres), serial-run matching (`CR_STEP_MATCH`), known-content read (`CR_STEP_KNOWN`), multi-frame fusion.

### 5.6 Read diagnosis (`code.read`, misses)

Purpose: tell the operator why each unread code did not read.

| Engine reason | Explain key | English example |
| --- | --- | --- |
| `CR_MISS_CUT_OFF` | `explain.code.cut_off` | "Code is cut off at the picture edge. Move the part or the camera." |
| `CR_MISS_GLARE` | `explain.code.glare` | "Glare is hiding the code. Tilt the light or use a polariser." |
| `CR_MISS_LOW_CONTRAST` | `explain.code.low_contrast` | "Code is too faint. Turn the light up or check the print." |
| `CR_MISS_COVERED` | `explain.code.covered` | "Code is covered, folded or missing. Check the label." |
| `CR_MISS_TOO_SMALL` | `explain.code.too_small` | "Code is too small for this camera. Move the camera closer." |
| `CR_MISS_BLUR` | `explain.code.blur` | "Code is blurred. Check focus, or the part moved during the picture." |
| `CR_MISS_NO_FINDER` | `explain.code.no_finder` | "The code's corner pattern is damaged. Check the print." |
| `CR_MISS_UNKNOWN` | `explain.code.unknown` | "Code could not be read. Save this picture for the engineer." |

- Targets: diagnosis within the engine's cap of about 20 ms per image, after decoding, so decode time is unchanged (engine note). The Run screen counts reasons since start and names the most frequent one.

---

## Tool 6. Code Verify

Operator name: **Grade code**. Licence `VS-VER-`. Phase P2. Requires a calibrated setup: a
reflectance calibration against a certified calibration card, the ISO aperture set from the
X-dimension, and lighting that meets the standard's geometry. Without it, the tools still run but
label every grade "process control, not ISO verification" (see [../QUALITY.md](../QUALITY.md)).

The engine is gaining verifier-style calibration as this is written (work in progress on the
`calibrated-grading` branch, not yet merged): a white-card flat field (`cr_set_flat_field`), a
dark/light reflectance card (`cr_measure_card`, `cr_set_reflectance`), lens and mm-per-pixel
calibration from a chessboard (`cr_set_lens`, `cr_calibrate_chessboard`), and ISO/IEC 15416
grading of 1D codes over 10 scans through a 0.8 X aperture. Tool 6 builds on these. Even with
them, the line's lighting geometry is not the standard's, so a report says "verifier-style" unless
6.0 confirms ISO lighting.

**On the line (built 2026-10-29, PR #70).** Grading on a running line is a measurement, never a report: no PDF
is made in the cycle (the grade report, 6.5, is made on request off the line). The Read code tool grades when a
lowest print grade is set or "Keep grades for the trend" is on; "Grade one part in N" grades only every Nth part
(1 = every part), since grading costs time and print quality changes slowly. A sampled part below the lowest grade,
or one that cannot be graded, is NG; the parts between samples are not judged on their grade (their text, count
and GS1 data still are). Each graded part's grades (overall and per parameter, the worst over its codes), symbol
contrast and modulation are kept with its result and sent to the plant server with the part's inspection result
(values `<tool>.grade`, `<tool>.grade.modulation`, ...), for a print-quality trend per line.

### 6.0 Verification setup (`verify.setup`) — "Set up grading"

Purpose: calibrate reflectance and confirm the setup meets the standard.

- Inputs: an image of a plain white card, images of a certified dark/light reflectance card, optional chessboard images for scale. Outputs: `ReflectanceCalib` (flat field + grey-to-reflectance map), `XDimMm`, `SetupOk` (bool), `ApertureMils`.
- Parameters: Card certified Rmax and Rmin (Essential), X-dimension (mm, Essential), Lighting (45° four-side / 30° / 90° diffuse / dark field; Essential), Aperture (auto from X-dimension; Advanced).
- Auto-learn: reads the card's printed values from its own code where present; sets exposure so Rmax is 70–85% of full scale.
- Algorithm: the engine's `cr_set_flat_field`, `cr_measure_card` and `cr_set_reflectance`; scale and lens from 3.1 or `cr_calibrate_chessboard`. Kept per product and camera.
- Pass/fail: card grades within ±1 grade step of its certified values (target, per ISO/IEC 15426 practice). Explain: `explain.verify.setup_fail` = "The grading setup does not match the test card. Check the light and calibrate again." · `explain.verify.expired` = "Grading calibration is older than {days} days. Calibrate with the test card."

### 6.1 2D print grade (`verify.iso15415`) — "Grade code"

- Inputs: decoded code from 5.1 or its own reference decode, `ReflectanceCalib`. Outputs: overall grade (A–F and 4.0–0.0), per-parameter grades and values: Decode, Symbol contrast, Modulation, Reflectance margin, Fixed pattern damage, Axial non-uniformity, Grid non-uniformity, Unused error correction, Quiet zone, Print growth (informative).
- Parameters: Minimum overall grade (C; Essential), Scans (5 orientations per ISO, on; Advanced), Report fields (Advanced).
- Pass/fail: overall grade ≥ minimum. Confidence = 25 × numeric grade.
- Explain: `explain.verify.grade_low` = "Code grade is {grade}, minimum {min}. Worst: {parameter}." · `explain.verify.contrast` = "Print contrast is low. Check ink and label colour." · `explain.verify.modulation` = "Print is uneven. Check the printer head." · `explain.verify.quiet_zone` = "Something is printed too close to the code."
- Algorithm: engine `cr_quality` measures with calibrated reflectance and the ISO synthetic aperture; scan grade = lowest parameter; overall = mean of scans.
- Built 2026-10-30 (PR #117): **DotCode** graded by this method on the dot grid its decoder fitted (AIM ISS DotCode Rev 4.0 refers its print quality to ISO/IEC 15415's matrix method). What the symbology changes: it has no finder or clock pattern, so Fixed pattern damage is not graded (-1); Grid non-uniformity is the largest distance of a printed dot's centre (darkness-weighted, within 0.7 pitch of its place) from its place on that grid, in pitches; Unused error correction comes from its own Reed-Solomon blocks, 1 - (2 e + f) / (check words - spare), the decoder's spare check words (2, plus one per 16 checks) counted as 15415's misdecode protection, of the worst block; Quiet zone 3 X. `cr_code_ext.grid_rows` / `grid_cols` give its dot grid; `fixed_pattern_damage` reads -1. Not yet checked against the ISS clause text itself. The Read code tool takes a lowest print grade on it with PR #150.

- Built 2026-10-30 (PR #150): the Read code tool takes a lowest print grade and grade recording on every type the engine reads: Micro QR, PDF417 and Aztec (graded on their module grid or by rows) and Pharmacode and DotCode (PR #117) are no longer refused at setup. As for every type, a code that cannot be graded while grading is on fails the part (`explain.verify.not_graded`). Part mark grading (ISO/IEC TR 29158) stays Data Matrix and QR only.
- Built 2026-11-01 (PR #217): **Pixel check** (requirements AS-14). A 2D code needs 2.5 px per module (below 1.8 the read is unsure, INDUSTRY-METHODS.md); a 1D code, PDF417 and Pharmacode 2.0 px per narrow bar (unsure below 1.5). During job setup (Try) the code tool measures each code's module or narrow bar with the engine's quality measure even when it does not grade (the verdict is judged as without it; the line never measures). Below the need the engineer gets the factor and the remedies: the camera k times nearer, a lens of k times the focal length (in mm when `Cameras[].LensFocalMm` is set), or a camera with k² the pixels; the tool shows "too few pixels" until an engineer presses "Accept as is" (`Tools[].PixelsAccepted`). Advice only.

### 6.2 1D print grade (`verify.iso15416`) — "Grade code"

- Outputs: overall grade, per-scan-line grades and values: Decode, Rmin/Rmax, Symbol contrast, Edge contrast min, Modulation, Defects, Decodability, Quiet zone (thresholds as [../QUALITY.md](../QUALITY.md)).
- Parameters: Minimum grade (C; Essential), Scan lines (10; Advanced).
- Explain: `explain.verify.defects` = "Bars have voids or spots. Check the printer." · `explain.verify.decodability` = "Bar widths are off. Check printer speed and heat."
- Algorithm: the engine's 1D grading: 10 scan reflectance profiles across the bars through a 0.8 X aperture, each graded on Rmin, SC, ECmin, MOD, defects, decodability and decode; symbol grade = mean of scan grades.
- Built 2026-10-29 (PR #68): every 1D type but DataBar is graded (`engine/src/decodability.cpp`). Decodability per symbology: n-width codes (Code 128, Code 93, EAN-13, EAN-8, UPC-A, UPC-E) by each character's edge-to-similar-edge distances against the decision points; two-width codes (Code 39, Codabar, ITF) by each character's closest element to the threshold halfway between its narrow and wide means, over half the distance between them (0 = an element on the threshold; Codabar's wide count from the decoded character). A scan whose elements do not fit the type counts 0. X is the mean of the narrow bars' and narrow spaces' widths, so print gain does not change it. Quiet zones per type (EAN-13 11 / 7 X, EAN-8 7 / 7, UPC-A 9 / 9, UPC-E 9 / 7, others 10 / 10). Each scan is limited to the symbol and its quiet zones, and is decoded alone (a scan line running onto a darker carton no longer takes the label's edge for a bar); a stripped check digit must be correct to count as read. DataBar, and Micro QR, PDF417 and Aztec in 6.1, are not graded yet (no grade rather than a wrong one; a minimum grade on them is refused). On the line: the Read code tool's "Lowest print grade" (A-D) fails a code below it, naming the worst parameter (`explain.verify.grade_low`), after the text and GS1 checks; a code that cannot be graded while grading is on fails (`explain.verify.not_graded`); grades read "process control" without a reflectance calibration. Not yet: the EAN/UPC bar-width check for characters 1, 2, 7 and 8, and quiet-zone and Rmin results per scan in the C API.
- Built 2026-10-30 (PR #82): the EAN/UPC bar-width check for characters 1, 2, 7 and 8 (ISO/IEC 15420 reference decode): their pairs (1/7, 2/8) share the edge-to-similar-edge distances, so the two bars' total is read against the threshold between the pair (set A 4 modules, sets B and C 3); its margin over 1 module joins the character's decodability, and a total on the other digit's side counts 0 when the decoded text names the printed digit. GS1 DataBar graded (ISO/IEC 24724 reference decode): Omnidirectional and Truncated, Limited and Expanded, the variant from each scan's own decode; each data character by its edge-to-similar-edge distances, each finder against 15 modules of the characters beside it (its own width runs between dissimilar edges, which ink spread moves); no quiet zone beyond the guards' light module (Limited's right guard 5 X); the stacked variants are not graded (no grade rather than a made-up one). Each scan is decoded from its profile thresholded at the global threshold, as the standard decodes, for every 1D type.
- Built 2026-10-30 (PR #95): the stacked GS1 DataBar variants graded (Stacked, Stacked Omnidirectional, Expanded Stacked; ISO/IEC 24724): the rows are found between their separator patterns (lines along the bars agreeing on 90% of their samples, at least 2.5 X tall; the separators are not scanned), ten scans inside each row from 10% to 90% of its height, each row's characters and finders by the row's structure (top row: guard, character, finder, character, guard; bottom row behind a guard bar and space; Expanded Stacked rows as Expanded, read either way). No row decodes alone, so each scan is decoded with the other rows' best scans as one symbol; a row that reads in no scan leaves the symbol undecoded (F). A row's grade is the mean of its scans', the symbol's the lowest row's, its parameters that row's means; the scan lines are listed row by row (ten per row). Fewer than two rows found: not graded. Every DataBar now reads one orientation per row (the one its scans read better in), so a symbol upside down grades as the right way up. The Read code tool takes a lowest print grade on GS1 DataBar.
- Built 2026-10-30 (PR #117): **Pharmacode** graded by these scans. It has no print quality standard of its own, so it is graded as verifiers grade it, with the engine's Pharmacode decoder as each scan's reference decode (locked to the bar count the value names, in the direction the code was read: corner 0 to corner 1). Decodability: each bar's bar + gap pitch, edge to similar edge (narrow 3 units, wide 5), as the share left of the decoder's 15% tolerance; the unit from the symbol's length and value; a code of one width (all narrow or all wide) also by each bar's width over its pitch against the decoder's limits (narrow up to 0.44, wide from 0.52), which ink spread moves. X is the narrow bar (one unit). Quiet zones 8 X on both sides (the decoder's 4 gaps). The ten scans run from 10% to 90% of the bars' own height, measured across the bars (the band finder's height over a few sparse bars is rough). The Read code tool takes a lowest print grade on it with PR #150.

### 6.3 DPM grade (`verify.iso29158`) — "Grade code"

- Outputs: 15415 parameters plus Cell contrast, Cell modulation, Minimum reflectance, and the image's mean light level used.
- Parameters: as 6.1; Lighting angle recorded for the report.
- Algorithm: ISO/IEC 29158 (AIM DPM) method: image adjusted so the mean light is at the target level, then cell-based measures; closing filter sized from the dot size for dot-peen marks.
- Built 2026-10-30 (PR #96, `engine/src/grade_dpm.cpp`): Data Matrix and QR. Cells sampled through a circular synthetic aperture of 80 % (default) or 50 % of the cell, or the better of the two; both overall grades reported. Polarity from the cells (light marks on a dark part graded as such). Dot peen: the widest gap between dots along a solid fixed-pattern row sizes a grey closing of the mark before sampling (none on solid laser marks). Cell contrast (ML - MD) / ML (A 30 %, B 25, C 20, D 15); cell modulation per cell against the global threshold, graded against the error correction as 15415 modulation; minimum reflectance from the image-based calibration Rcal x SRcal / SRtarget x ML / MLcal (A 20 %, B 15, C 10, D 5; not measured without it); fixed pattern damage per segment (Data Matrix L sides, clock tracks, quiet zone; QR finders by damaged-cell count, timing patterns) and distributed damage, their mean grade; axial and grid non-uniformity and UEC as 6.1. Mean light reported with a 70..86 % flag. C API `cr_set_dpm` / `cr_get_dpm`; .NET `DpmGrade` (notation "3/80/660/30Q"); the Read code tool's grade standard ISO 29158 judges its lowest grade on it (Data Matrix and QR only; a code not graded fails). Not yet: the DPM report page and the Studio settings.

### 6.4 GS1 data validation (`verify.gs1`) — "Check code data"

Purpose: check the decoded content follows GS1 rules.

- Inputs: `Codes` text. Outputs: `Elements` (List of AI, value), `Valid` (bool), `Errors` (List).
- Parameters: Required AIs (for example 01, 10, 17, 21; Essential), Check digit (on), Date rules (expiry after production date; Advanced).
- Explain: `explain.gs1.missing_ai` = "Code has no {ai_name}." · `explain.gs1.check_digit` = "The GTIN check digit is wrong." · `explain.gs1.bad_date` = "Expiry date {value} is not a real date." · `explain.gs1.association` = "The {ai_name} needs {partner} in the same code." (or: "may not be in one code")
- Algorithm: GS1 General Specifications AI table and syntax; existing parser in `dotnet/CodeReader/Gs1Data.cs` moved into the C++ core.
- Built 2026-10-29 (PR #67) in .NET first (`dotnet/CodeReader/Gs1Check.cs`), as a check on the Read code tool ("Check GS1 data"): the raw form (FNC1 as ASCII 29, a GS1 symbology identifier allowed) and GS1 Digital Link URLs; the human-readable form "(01)..." only from typed or OCR text: in a symbol it is a print defect (the label's text line encoded) and fails. Each AI known (the common pharma, food and logistics AIs, measures 310n-369n, amounts 390n/392n, company AIs 91-99), its value numeric or of the 82-character set, of the right length; predefined-length AIs (00, 01, 02, 11-20, 31-36, 41n) need no separator, others end at one unless last (a missing separator makes the next AI part of the value, which then fails its length, a required AI or an expected value, never a silently shortened value); no AI twice, no trailing separator; check digits of GTIN, SSCC, GLN and those inside 8006 and 8003; real dates (day 00 = the month's end for dates that allow it; a production date with day 00 is read as the month's first day when compared); expiry (17, and 7003 to the minute) not before production and not past the picture's time (on by default); required AIs (at least one when the check is on); expected AI values from batch fields (for example `10={BATCH}`). Every problem fails the code and names its reason (`explain.gs1.syntax`, `check_digit`, `bad_date`, `missing_ai`, `wrong_value`). Teaching switches the check on for GS1-capable types (GS1-128, Data Matrix, QR, DataBar) when the code read holds GS1 data with a key (GTIN, SSCC, GLN, ...), with every AI read required except measures. Added 2026-10-30 (PR #73, `dotnet/CodeReader/Gs1Associations.cs`, tables): the General Specifications' association rules (section 4.13): mandatory associations (for example 10, 11, 13, 15, 16, 17 and 20 only with 01, 02 or 8006; 21 only with 01 or 8006; 02 only with 37 and 37 only with 02; 250 with 21; trade measures with 01 or 02, logistic measures also with 00; 254 with 414; 8020 with 415; 12 with 8020; 422 and 7003 with 01 or 02; 710-715 with 01) and invalid pairs (01 with 02, 37 or 8006; 37 with 8006; one measure with two decimal positions), each failing as `explain.gs1.association`; GS1 Digital Link structure: any path prefix, then one primary key (01, 00, 414, 415, 8003, 8004, 8006), then only that key's qualifiers in their order (01 and 8006: 22, 10, 21; 414: 254; 415: 8020), data attributes only in the query (a key or qualifier there fails), a GTIN-8, -12 or -13 in the path read as GTIN-14; a named required set "pharma" (01, 17, 10, 21). Extended 2026-10-30 (PR #123): the AI table and its rules come from the GS1 Barcode Syntax Dictionary (release 2026-01-27, `dotnet/CodeReader/Gs1Dictionary.g.cs`, written by `tools/gs1/gen_gs1_table.py` from `tools/gs1/gs1-syntax-dictionary.txt` and GS1's code lists): every AI of the General Specifications (224 rows, for example 03, 235, 253, 255, 401, 402, 426, 43nn, 7040, 725n, 8010, 8017, 8026, 8030), each value split into its components (digits, the 82- or 39-character set, base64url; fixed or variable; optional parts) and each component's checks: check digits and the GMN check pair, real dates and times, ISO 3166 country and ISO 4217 currency codes, piece of total, IBAN, and the rest of the dictionary's checks except the North American coupon codes (8110, 8112: type and length only); predefined length (no separator) as the dictionary marks it; all its mandatory associations ("01+21" groups, several rules per AI) and invalid pairs (a measure with two decimal positions is one); Digital Link keys with their qualifier sequences (01: 22, 10, 21 or 235) and only the AIs it allows as data attributes in the query. `Gs1Data` (product matching) reads the same AIs. Not yet: that a symbol starts with FNC1 (the engine does not yet report the symbology identifier), the "pharma" set in the Teach page, and the move into the C++ core with the Code Verify parent tool.

### 6.5 Grade report (`verify.report`) — "Grade report"

Purpose: produce a PDF grade report per code or per batch.

- Outputs: PDF file path. Parameters: Template (ISO full / Summary; Essential), Language (operator's language with English parameter names; Advanced), Include image (on).
- Content: code image, decoded text, overall and per-parameter grades and values, aperture, lighting, wavelength, calibration date, setup status, operator and time.
- Targets: ≤ 1 s per report. Language rules per [LOCALISATION.md](LOCALISATION.md).
- Built 2026-10-29 (PR #69), laid out after the Cognex DataMan 475V Verification Report (its reference manual, "Examining the Results" and "Report Settings"), with its own small PDF writer (`dotnet/VisionCore/Reports/`: `MiniPdf.cs`, `GradeReport.cs`, `SymbolAnalysis.cs`; no PDF library added), from Vision Studio's Test page. Header: station, software, last calibrated, company, operator, batch (from GS1 AI 10 when not given), note, job, tool / camera, image and its time, report time. Verification grade row: overall (letter and value for 2D; letter only for 1D, whose scan mean the engine rounds), aperture as the engine used it (0.8 X; X/3 for uncalibrated 2D), wavelength, lighting, formal grade (grade/aperture mil/wavelength/angle, only with a reflectance calibration, a mm scale and the setup recorded; for 1D from the mean of the engine's scan grades, truncated to one decimal, from the engine's scans). Result bars: grade acceptance against the tool's lowest print grade (the print grade only), GS1 data check (element syntax and the tool's GS1 rules and batch values; FNC1 position not checked). Quality parameters numbered with grade, value and PASS / FAIL; for Data Matrix the fixed-pattern segments LLS, BLS, LQZ, BQZ, TQZ, RQZ, TCT, RCT, each cell checked against its known pattern. General characteristics: symbology and standard, matrix size and data region (only when it matches a real size, DMRE included; else "not estimated"), encoded characters, total and data codewords, error-correction budget and capacity used, ECC 200, polarity, contrast uniformity and its module, MRD, print growth, X and pixels per module; for 1D magnification (EAN/UPC), inspection zone height, scans and the usual aperture for the X-dimension (3, 5, 10 or 20 mil). Print growth as % of X and, with a scale, as a distance. Lighting written as the angle and the sides lit (45Q: four sides, T two, S one), also in the formal grade. Data detail: data, GS1 elements (problems first), ASCII values. Diagnosis: for each parameter below B (and for fixed-pattern cells read wrong or print growth past 15 % of X) what it usually means and what to check, in the report's own words (after Cognex's white paper "Understanding Barcode Verification Results"). Images: the symbol and, for 2D, the modulation overlay (the decode grid with the aperture at each cell, cells below B yellow, F or on the wrong side red); the modulation values table with the quiet-zone ring (rows and columns -1 and n, expected light) (each cell's modulation x 100 coloured by grade, pale for light and deep for dark cells, a fixed-pattern cell on the wrong side as 0 in dark red or pink, as the verifier's legend); the histograms of the symbol region and of the module centres with the threshold and the A-F band bar; for 1D the scan reflectance profile. Units mil, mm or µm; each section can be left out. The grades are the engine's; every cell figure, segment check, histogram and profile is labelled the report's estimate from grey levels (homography placement, 10th / 90th percentile threshold as the engine). Not yet: the FPD transition ratios and average grade, print growth per axis, codewords and encodation analysis as decoded, the batch report, the operator's language.
- 1D report (2026-10-30, PR #87), after ISO/IEC 15416 verifier reports (`LinearAnalysis.cs`, a port of the engine's scan measurement in `grade1d.cpp`). The ten scan lines from 10 % to 90 % of the bar height, the bar height found from the image because a reader's corners may be a thin line. A table per scan: height, Rmin, Rmax, SC, ECmin, MOD, defects, decodability, decode, quiet zone left and right in X, and the scan grade, each cell coloured by its grade. The mean row gives the symbol means and their grades, and the mean of the scan grades. The ten scan reflectance profiles, each with its grade, its lowest parameter, the threshold, Rmin and Rmax, and the type's minimum quiet zones (green when clear, red when short). Rmin and quiet zone rows among the parameters. General characteristics add the measured X, bar width growth in X and as a distance, symbol width (first to last bar) and bar height, and the least quiet zones against the type's minimum (EAN-13 11/7 X, EAN-8 7/7, UPC-A 9/9, UPC-E 9/7, others 10 X) with PASS / FAIL. Data detail adds the check digit: GS1 mod 10 for EAN / UPC (UPC-E through its UPC-A) and ITF-14, Code 39's optional mod 43, and for Code 128 and Code 93 the check characters the decoder checked. The diagnosis gives, for each per-scan parameter below B, which scans and where on the bars: a few scans point to a local cause, every scan to one across the symbol. Decodability, decode and the scan grades are the engine's (`CodeQuality.ScanLines`, PR #78). Without them the table is the report's estimate from the image, and each grade is shown as "at most" because decodability and decode are left out.

---

## Tool 7. OCR / OCV

Operator names: **Read text**, **Check text**. Licence `VS-OCR-`. Phase P2. Target: ≥ 99.5%
character accuracy on industrial dates and lot codes without training (requirements section 13).
Scripts: Latin by default; Devanagari and others as options.

### 7.1 AI OCR (`ocr.ai`) — "Read text"

Purpose: read printed text with no training. The default OCR tool.

- Inputs: `Image`, `Region`, `Fixture`. Outputs: `Lines` (List of: text, box, per-character confidence), `Text` (all lines joined), `CharCount`.

| Parameter | Type | Default | Range | Level |
| --- | --- | --- | --- | --- |
| Expected format | pattern | from teach | see 7.4 | Essential |
| Script | enum | Latin | Latin / Devanagari / other installed packs | Essential |
| Character set limit | text | from format | any subset | Advanced |
| Print type | enum | Auto | Auto / Normal / Dot matrix / Inkjet / Embossed | Advanced |
| Min character confidence | 0–100 | 60 | 10–99 | Advanced |
| Rotation | enum | Auto | 0 / 90 / 180 / 270 / Auto | Advanced |
| Fine-tuned model | file | none | from Tool 10 | Advanced |

- Auto-learn: draw a box around the text (or tap "Check this text?"); the tool reads it, shows the result to confirm, and proposes a format from it (for example `DD/MM/YY` and `LOT A99999`).
- Auto-tune: picks print type and preprocessing for the highest lowest-character confidence on OK images.
- Pass/fail: every line read, every character ≥ min confidence, and the text matches the expected format. Confidence = lowest character confidence.
- Explain: `explain.ocr.no_text` = "No text found. Check the printer and the light." · `explain.ocr.unsure` = "Character {index} is unclear (read as {char}). Check the print." · `explain.ocr.format` = "Text {text} does not match {format}."
- Targets: one 3-line date/lot block ≤ 25 ms on T1 (OpenVINO), ≤ 8 ms on T2 GPU.
- Algorithm: PP-OCR text detection (DB) and recognition (SVTR/CRNN class) in ONNX, run through the AI inference runtime; character set constrained by the expected format for decoding (lexicon-limited CTC). Check PaddleOCR model licences before release.
- Built 2026-10-31 (PR #162), Read text safety (study 04c 7.1, INDUSTRY-METHODS rank 14): with no expected text, a line passes only when **a second reader reads the same text** (spaces not compared) at or above min quality, and its score is at most the **line confidence**: the CTC probability of the whole read text (spaces counted as blanks), calibrated as 100 · P^(1/0.9) on synthetic validation lines, so a dropped "." or an extra letter lowers it. Second readers (`vs_ocr_set_agreement`, `TextToolConfig.Agreement`; Auto by default, Off refused in Read text): a second AI model installed as `ocrnet_b.onnx` (`vs_ocr_load_second_model`), the AI model when the taught font reads first (when it reads every taught character), the taught font when the AI reads first (engine API); with none, the first reader on the line moved by half a step (catches reads that hang on the exact cut, not a model's blind spot). A line already below min quality is not read again; a second read that the time budget leaves no room for fails the line. Format lines must read the same under the format. Read text without formats now fails a part when any line read is unclear (before: any text read passed). `vs_ocr_line_ext` gives `agreement`, `second_readers` and `line_confidence`; the operator sees "Character n of "…" is unclear (read as x, s): the second reader reads it otherwise" (`explain.ocr.unsure`). Check text is unchanged.

### 7.2 Classical OCR (`ocr.classic`) — "Read text"

Purpose: read text from a taught font, for fixed fonts, very fast lines or PCs without AI
acceleration.

- Inputs: `Image`, `Region`. Outputs: as 7.1.
- Parameters: Font (taught; Essential), Expected format (Essential), Polarity (Auto; Advanced), Char width and height range (from teach; Advanced), Segmentation (Projection / Connected parts / Fixed pitch; Advanced), Dot join (px, auto for dot matrix; Advanced).
- Auto-learn (font training): draw a box over sample text and type what it says; the tool segments, pairs each character image with the typed character, and asks only about the ones it is unsure of. Adds to the font from any later image with "Teach this character".
- Pass/fail and explain: as 7.1, plus `explain.ocr.unknown_char` = "Character {index} is not in the taught font. Teach it."
- Targets: ≤ 5 ms per 20-character line on T1. Algorithm: binarise, segment, normalise each character to a fixed cell, features (normalised grey plus gradient histogram), classifier (small MLP or SVM trained in seconds), format-constrained choice.
- Built 2026-10-29 (PR #62) as a second reader of the OCR engine (`vs_ocr_params.reader = VS_READER_FONT`, `engine/src/ocr/font.cpp`): the line finder straightens each line as for the AI; the line is cut into characters at the columns without ink; each character's columns, stretched to a 16 x 32 cell, are compared by normalised correlation with every taught sample (at shears of up to ±4°, and with its width against the taught width); the result is written as the same per-column character probabilities the AI gives, so reading, checking (OCV), TIME / DATE fields and character boxes are shared. A character's score is how well it fits times how clearly it beats the next-best character (a lead of 0.10 in correlation for full marks), so two characters as alike as O and 0 in some fonts score low and a check fails rather than passing on noise. Touching characters are cut only through a thin bridge and only where every piece clearly matches one character; a character broken in two (missing dots) is joined when that matches better; a character never taught reads with a low score; no character is ever left out of the read (the column grid grows instead). Texture that is not text (a barcode in the region) and the time budget stop the reading: nothing is read and a check fails. Dot print is melted into strokes first. Teaching: type what is printed on a line (`vs_ocr_teach_font`); a text with a character too few or too many, the same character typed for two different shapes, or a new character that matches a taught one nearly perfectly is refused. The font is kept in the job as text (`vs_ocr_get_font` / `vs_ocr_set_font`); Vision Studio's Teach page teaches it line by line; a font tool with no font, or a damaged one, fails every part as a setup error. A full score needs a correlation of 0.90, or, when every character was taught from several prints, as good as those prints match each other. Measured on drawn print: 5 to 10 ms per 10-character line on the development PC (T1 not yet measured). Instead of a classifier it uses nearest-sample correlation, which trains instantly. Built 2026-10-30 (PR #75): once the font has characters, the Teach page reads a line with it and shows one box per character, sure ones filled in and the ones it is unsure of (below the tool's strictness) left empty for the engineer; every box stays editable, because a character never taught can read as a taught one with confidence (an 8 as a B); teaching then uses the read's own cut when the even cut does not fit. "Teach this character" (`vs_ocr_teach_char`, an image point such as a character box's centre): after Try on any picture (a loaded NG image or a grabbed one) the Teach page lists the characters below OK, those never taught first, with the expected character shown but never filled in; the engineer types what is printed and that one character is added. It is refused when it clearly looks like another taught character, so a misprint is not taught as good by mistake. Characters the font was never taught, or that look like no taught character, get the status Unknown (`VS_CHAR_UNKNOWN`; pass and fail are unchanged) and the tool explains `explain.ocr.unknown_char`. The font's character boxes now cover the columns each character was cut from.

### 7.3 OCV (`ocr.ocv`) — "Check text"

Purpose: verify that the printed text is exactly the expected text, with a print-quality score
per character.

- Inputs: `Image`, `Region`, expected text (from the job, recipe, PLC or another tool such as a decoded code). Outputs: `Match` (bool), `CharResults` (List of: expected char, found char, quality 0–100, status OK / Missing / Broken / Wrong / Extra), `PrintQuality` (0–100, lowest char).
- Parameters: Expected text source (Essential), Min print quality (70; Essential), Font (rendered system font or taught font; Advanced), Allow shift (± px; Advanced).
- Auto-learn: from one good print, the tool reads it (7.1), confirms the text and takes the per-character images as reference.
- Pass/fail: every expected character present, correct, and quality ≥ minimum; no extra characters.
- Explain: `explain.ocv.missing` = "Character {index} ({char}) is missing." · `explain.ocv.broken` = "Character {index} ({char}) is broken or faint." · `explain.ocv.wrong` = "Character {index} is {found}, should be {char}." · `explain.ocv.extra` = "Extra marks after the text."
- Targets: ≤ 10 ms per 20-character line on T1.
- Algorithm: locate the string (7.1 detection), align each expected character template by NCC within the allowed shift, quality from correlation plus stroke coverage and contrast; wrong-character check by 7.1 recognition at that position.
- Built 2026-10-30 (PR #93): **Expected text source** per line (`TextLineSpec.Source`): the job (template or full text, as before), the PLC (`PartId` or `ProductId` of the trigger, `PlcValues`, passed by `JobRunner.Run`) or another tool that ran before it on the same camera (the one code a Read code tool read, or the one line a Read text tool read). The value is checked as printed. A value that is missing or empty, a source tool that did not pass, or one that gave no text or more than one, fails the part (`explain.ocv.no_expected`); the engine never passes an empty expected line either. **PrintQuality** (`ToolResult.PrintQuality`): the lowest character score over every expected line, 0 when a line is not found. **Allow shift** (`allow_shift_px`, `TextToolConfig.AllowShiftPx`): where each line's first character sits, measured from the area's corner (so it follows a fixture), is learned with the print quality of good parts (`vs_ocr_learn_print`, kept in the print reference); a line farther away than allowed is NOT OK (`explain.ocv.shift`). Set with no place learned, every part is NOT OK (`explain.ocv.shift_unlearned`) until a good part is learned; learning accepts a part whose only fault is that. `vs_ocr_get_line_ext` gives the shift per line. The rendered system font is built since PR #108 (below).
- Built 2026-10-30 (PR #136): the PLC source also gives `UserIn1..4` (decimal; 0 = no value) and `Text`, a string of up to 254 characters from the PLC's text block (Modbus registers 500–627, [PLC-INTERFACE.md](PLC-INTERFACE.md) section 3.7); the line keeps its place (`TextLineSpec.PlcText`: register, length, ASCII or S7 string, byte order). The block must be stamped with this trigger's `TriggerID`; a stale, empty or unreadable string fails the part (`explain.ocv.no_expected`).
- Built 2026-10-30 (PR #108): **Font: rendered system font** (`TextToolConfig.Rendered`, `FontRender`): reader Font with no teaching from print. The engineer picks an installed font, bold or not, the height of a capital letter in image pixels (it also sets the letter height the reader looks for when the tool's is 0) and the characters the print may hold (default A–Z, 0–9, `- / . :`); the fixed text of the template lines is added by itself. VisionCore draws the characters with SkiaSharp, 8 to a line, and teaches each line to the taught-font reader (`vs_ocr_teach_font`), so the engine's classical reader and check are unchanged. The font is drawn when the job is saved and kept with it (`Font`, with `RenderedAs` naming what it was drawn from), so the line PC reads with what was checked at save, even without the font installed; the line never draws one itself (a font changed since the save fails every part until it is drawn and saved again). Small letters and signs are drawn between capitals, so each is taught at its own height. The AI reader ignores these settings. Refused at save: dot-matrix or inkjet print (drawn shapes have no dots), no font, a font not installed (unless the kept font still matches), a size outside 10–300 px, a character of the fixed text or character set with no glyph in the font, a line the engine cannot cut into its characters at that size, and a format needing characters outside the set. Bold on a family with no bold face thickens the regular one. On the line a wrong, missing or extra character, or a field value with a character outside the set, is NOT OK. Print quality limits are not taken from the drawn font (its contrast and ink do not match real print and would fail good parts): until good parts are learned the recogniser's score against the drawn characters decides, as for a taught font. VisionStudio Teach: "Font from" Sample print / System font, with the font, letter height, bold, characters and Draw the font.
- Built 2026-11-01 (PR #217): **Pixel check** (requirements AS-14). Read text and Check text need a capital letter 20 px high; below 12 px the read is unsure (the rendered font takes 10 px at the least). The capital height of each line read is compared with it during job setup (Try), with the factor missing and the remedies; the tool shows "too few pixels" until an engineer accepts it. Advice only: it never changes the result.
- Built 2026-10-31 (PR #166): **Verification margin** (study 04c, 7.3.3): each checked character's margin in nats, ln(P(expected) / P(best rival)), the rival being any other character within one column of it or nothing printed there (`vs_ocr_char_quality.margin`, `CharQuality.Margin`), and each line's lowest (`vs_ocr_line_ext.margin`, `OcrLine.Margin`). The tool shows it on each line's detail and keeps the lowest over every expected line with the part's result as the value `margin` (-99 when a line is not found), for trends: several nats is a clear print, near 0 a look-alike explains the print almost as well. Reported only: OK / NG is decided as before. Per-letter scale and shear freedom on the AI read was measured and not built (study 04c, 7.3.1).

### 7.4 Format validation (`ocr.format`) — "Check date / lot"

Purpose: check that read text is a valid date, lot or expiry, and consistent with the clock.

- Inputs: `Text`. Outputs: `Fields` (date, lot, other), `Valid` (bool).
- Parameters: Format (templates such as `DD/MM/YYYY`, `MMM YY`, `LOT ^[A-Z]\d{5}$`; Essential), Date rules (expiry = production + N months; date within ± N days of today; Advanced), Month names language (Advanced).
- Auto-learn: proposes the format from the confirmed text.
- Explain: `explain.format.date_invalid` = "{value} is not a real date." · `explain.format.date_wrong` = "Date {value} should be {expected}. Check the printer date." · `explain.format.mismatch` = "Text does not match the pattern {format}." · `explain.format.price_invalid` = "The price {value} is not a real price. Check the printer text."
- Targets: ≤ 0.1 ms. Algorithm: format templates compiled to patterns; date arithmetic in the job's time zone.
- Built 2026-10-29 (PR #61) in Vision Studio's Read text mode (`dotnet/VisionCore/Tools/TextFormat.cs`): one format per printed line, literal text with fields `{D:5}` digits, `{A:1}` letters, `{X:6}` letters or digits, `{?:3}` any, `{MFD:DD/MM/YY}` / `{EXP:MMM YY}` dates (DD, MM, MMM, YY = 20YY, YYYY; English month names) that must be real dates, `{re:...}` for the engineer. A space matches any spacing; letters match in either case; a read character is never corrected. Each format takes the first unused read line it fits (a stray line does not shift the rest; lines no format claims are not checked), and that line must be read at or above the tool's minimum quality. Every date is named MFD or EXP, once per tool (a setup error otherwise, as are empty or invalid formats and format lines in Check mode; the job does not save). Date rules: always, expiry not before production and not already past; optionally expiry = production + N months (to the day, or one day before, when both dates have a day; else by month) and production within N days of the picture's time. Teaching proposes a format per line, names dates by their label (MFD, MFG, PKD / EXP, USE BY, BEST BEFORE) or by order across the block, never turns a dotted number such as a price into a date without a label, and sets the expiry months from the taught dates and the production-date window (2 days) when the taught production date is the picture's date. Explain keys: `explain.format.mismatch`, `explain.format.date_invalid`, `explain.format.date_wrong`, `explain.format.setup`, `explain.ocr.unsure`. Month names (2026-10-30, PR #83): MMM accepts the month names of the tool's languages (`MonthLanguages`; English by default; Spanish, French, German, Portuguese and Italian, with and without accents); Hindi and the other Indian languages are left out because Indian packs print months in English, Hindi has no fixed short month names and the recogniser reads Latin characters only. Format-constrained reading (2026-10-30, PR #83): each format is also given to the OCR engine (`vs_ocr_set_format`, both readers), which reads its line only with the characters the format allows at each place (digits, letters, the printed literals, DD as 01-31, MM as 01-12, MMM as the month names) by a CTC Viterbi over the format's positions. The constraint chooses but never vouches: a character keeps its own probability (an O printed where a digit belongs reads as 0 with the 0's low score and fails), print the format has no place for fails as extra, and two equal characters with no gap seen between them score low (one wide 0 never reads as 00). Formats take lines as in Vision Studio (each the first unused line that passes under it). `{re:}` parts and counts above 64 are not given to the engine; such a line is read freely and checked by its pattern. Indian packs (2026-10-31, PR #161; study 04c 7.4.1, 7.4.3, 7.4.4): the month-name language `en-IN` adds JUNE, JULY, SEPT and the full English names (BEST BEFORE MARCH 2027); Hindi month names wait for a Devanagari reader (the AI charset has none). Legal Metrology fields: `{MRP}` = the label (MRP, M.R.P., MAXIMUM RETAIL PRICE, with or without a colon), the currency (₹, Rs., Rs, INR) and rupees with paise; `{PRICE}` the currency and amount alone (unit sale price); `{MRP:whole}` whole rupees; `{MRP:46.00}` or `{MRP:1,250.00}` exactly that amount (Indian grouping); an amount of 0 fails (`explain.format.price_invalid`); `{PER}` the unit of a unit sale price (per or /, then g, gm, kg, ml, l, ltr, pc, unit ..., optionally 1 or 100 before it); `{LIFE}` the months of "Best before N months from manufacture", which needs an MFD date in the tool: the expiry it gives (MFD + N months, a day less when the MFD has a day, else to the end of that month) must not be past, it must equal the expiry months when they are set, and a printed EXP must be MFD + N months. All of these have fixed word lists, so the engine reads them under the format too. Teaching proposes them from what is read (MRP: ₹159.35 → `{MRP}`, Rs.10/- → `{MRP:whole}/-`, 18 months after BEST BEFORE → `{LIFE}`) and names dates from the Indian labels Mfg. Dt., Pkd. On, Date of packing, MFG/PKD, Use before, Best before end, Expires. In Check text mode the MRP comes from the batch record as before (a `{MRP}` batch field, matched exactly). Read text that decides OK/NG (2026-10-31, PR #181; full system test 2026-10-30: in free reading 22 of 103 wrong laser lines scored at or above strictness 60): a Read text tool needs a format on every line, and a format of only any-character fields (`{?}`, `{?:5}`, `{X}` without a count, no printed text) does not count; otherwise the job does not save (`explain.text.no_rule`), and a job saved before the check fails every part as a setup error. The other way is **Report only** (`ReportOnly`, Read text only): the tool shows what it reads, its result is marked report only (ⓘ, never OK; `explain.text.report_only`) and never decides the part. A camera whose enabled tools are all report only is refused at save, and NG at run if it reaches the line; a Check text line cannot take its expected text from a report-only tool. Vision Studio's Teach page has the Report only box, a warning with the explain text under the tool, and a tag (no format / report only) in the tool list.

### 7.5 Dot-matrix and inkjet mode (shared by 7.1–7.3)

Purpose: make dotted and inkjet text readable.

- Parameters (Advanced): Print type (Dot matrix / Inkjet), Dot size (px, learned), Join strength (auto).
- Algorithm: dot detection by blob size, closing with a structuring element sized from dot pitch, then the normal path. The AI recogniser is trained with dot-matrix augmentation; embossed and etched text may need fine-tuning in Tool 10.
- Explain: `explain.ocr.dots_missing` = "Some print dots are missing. Clean the printer nozzles."
- Built 2026-10-30 (PR #93): **Dot size** (`dot_size_px`, learned from a good print with `vs_ocr_measure_dots` / `TextTool.LearnDots`, which also gives the dot pitch; solid print is refused): the line finder joins dots with a closing of 1.2 dot diameters, and the taught font melts dot print by about half a dot. **Join strength** (`join_strength`, 0 = normal, 0.5 to 3): scales both. Much more joining blurs O into 0 (1.5 already did on the test print). **Inkjet** (`VS_PRINT_INKJET`, never chosen by Auto): a grey opening fills the grain inside the strokes of each straightened line. It helps heavy grain (test print with 40 % of the ink pixels missing: 4 of 5 OK as Inkjet, 0 as Solid) and hurts light grain (30 %: 1 of 5 against 5), so the print type is chosen on good parts (auto-tune, 7.1).
- Built 2026-10-31 (PR #160): **Tight spacing** (`vs_ocr_set_tight_spacing`, `TextToolConfig.TightSpacing`, off by default) for CIJ print whose characters stand no farther apart than the dots of one character: the line finder joins dots along the line only within about half a dot (across it as before, so a missing dot is still bridged), and the taught font finds the pitch on dots not joined along the line and melts print less along it. Test print at 5.4 dot pitches per character: 5 of 5 OK with it, 0 of 5 without.

### 7.6 Text-line layout (shared by 7.1-7.4)

Purpose: use the known structure of the text block to find, join and check its lines (HALCON `text_line_structure`; tool study 04c 7.4.6).

- Parameter: Layout (`TextToolConfig.Layout`, `vs_ocr_set_layout`): each line's groups of characters apart by word gaps, lines top to bottom apart by `|`, e.g. `2 2 2-4 | 6`. Characters count as printed (punctuation too). Set in the job or learned from a good part (`TextTool.LearnLayout`, `vs_ocr_learn_layout`); empty = none, and the tool then works exactly as without it.
- Algorithm: when a region shows more text lines than the layout lists, the line finder joins pieces side by side in one band (the narrowest gap first, up to 8 capitals, no longer than the layout's longest line). Every joined piece must hold characters of the line's read or check, else the line fails as one cut short would. Read text: the layout's lines are matched to the found lines in order; a matched line with no format is read with as many characters as its layout line allows (a format of any characters) and must fit it: its count, and each group's when every word gap is seen. Check text: expected line i is layout line i, and its expected text must have a count the layout allows.
- Decide: a layout line not found, or a line that does not fit, is NG with the reason (`vs_ocr_layout_reason`). Explain: `explain.ocr.layout` = "The text does not fit the layout set for it: a line, a character or a word gap is missing or extra."
- Measured (PR #160, layout from each image's truth): synthetic laser OCV 92 → 101 of 150 and 125 → 140 of 200; CIJ 7 → 7 of 150; real Harpic 469 → 469 of 475; one changed character accepted 0 everywhere.

---

## Tool 8. Inspect

Operator name: **Check defects**. Licence `VS-INS-`. Phase P4. Teach from 5–20 good parts. Every
tool has a sensitivity slider with a live preview of what would fail on the image set.

### 8.1 Presence / absence (`inspect.presence`) — "Is it there?"

Purpose: check a feature is present (or absent) in a region.

- Inputs: `Image`, `Region`, `Fixture`. Outputs: `Present` (bool), `Score` (0–100), `Measure` (value of the chosen measure).
- Parameters: Measure (Mean brightness / Edge amount / Pattern match / Pixel count above threshold; Auto; Essential), Expect (Present / Absent; Essential), Sensitivity (slider; Essential).
- Auto-learn: draw a box on a good part; with one image of the feature absent (optional), the tool picks the measure that best separates them; with good parts only, limits by auto-tolerance.
- Pass/fail: score on the expected side of the limit. Confidence = distance from the limit scaled to 0–100.
- Explain: `explain.presence.missing` = "{name} is missing." · `explain.presence.unexpected` = "Something is here that should not be."
- Targets: ≤ 1 ms. Algorithm: the chosen measure; pattern match uses 1.1 within the region.

### 8.2 Golden template (`inspect.template`) — "Compare to good"

Purpose: find any difference from good parts.

- Inputs: `Image`, `Region`, `Fixture`. Outputs: `Defects` (blobs: area, position, contrast), `DefectCount`, `LargestArea`, `DiffImage`.
- Parameters: Sensitivity (Essential), Minimum defect size (px or mm²; Essential), Edge tolerance (px, 1.5; Advanced), Light tolerance (on; Advanced: normalise brightness first).
- Auto-learn: show 5–20 good parts; the tool aligns them by the fixture, builds a mean and a variation image, and sets sensitivity so all of them pass with margin.
- Auto-tune: sensitivity and minimum size from OK/NG sets.
- Pass/fail: no defect above minimum size. Confidence = 100 × (1 − largest defect score / limit), floored at 0.
- Explain: `explain.template.difference` = "Part differs from good at the red mark ({area} {unit}²)." · `explain.template.alignment` = "Part could not be lined up. Check Find the part."
- Targets: ≤ 15 ms for a 2 MP region on T1. Algorithm: subpixel alignment by fixture, per-pixel tolerance = k·σ + offset with σ widened near edges (edge tolerance by dilation of the gradient map), difference thresholded and blobbed (4.1).

### 8.3 Edge flaw (`inspect.edge`) — "Check edge"

Purpose: find nicks, burrs and bumps on an edge or outline.

- Inputs: `Region` along an edge or a taught contour. Outputs: `Flaws` (List of: position, depth, length), `MaxDeviation`.
- Parameters: Max deviation (px/mm; Essential), Min flaw length (Advanced), Edge path (line, arc or taught contour; Essential).
- Auto-learn: click on the edge; the path follows it (2.2/2.3 or 4.2) and the normal variation is learned from good parts.
- Explain: `explain.edge.nick` = "Nick of {value} {unit} on the edge at the red mark." · `explain.edge.burr` = "Burr of {value} {unit} at the red mark."
- Targets: ≤ 3 ms for a 2 000-point edge. Algorithm: dense calipers along the path, deviation from the fitted or taught shape, runs of deviation over the limit become flaws.

### 8.4 Surface defect (`inspect.surface`) — "Check surface"

Purpose: find scratches, dents and stains on a plain or regular surface.

- Inputs: `Image`, `Region`. Outputs: `Defects` (List of: type Scratch / Spot / Stain, area, length, contrast), `Count`.
- Parameters: Defect types (Essential), Sensitivity (Essential), Min size (Essential), Background size (auto; Advanced), Texture (Plain / Brushed with direction / Regular pattern; Advanced).
- Auto-learn: from good parts, background level and noise are learned; clicking a defect example sets its type and minimum size.
- Explain: `explain.surface.scratch` = "Scratch {length} {unit} long at the red mark." · `explain.surface.spot` = "Spot or dent at the red mark." · `explain.surface.stain` = "Stain at the red mark."
- Targets: ≤ 20 ms for 5 MP on T1. Algorithm: background estimate (large median or polynomial), residual; line (ridge) detector at the scratch width for scratches; DoG for spots; low-frequency residual for stains; blob and classify by shape. For textured surfaces where this fails, the suggestion is Tool 10 anomaly detection.

### 8.5 Bead / glue (`inspect.bead`) — "Check glue line"

Purpose: check a glue, sealant or weld bead is continuous, the right width and in the right place.

- Inputs: `Image`, taught bead path, `Fixture`, `Calib`. Outputs: `Widths` (List along path), `Offsets` (List), `Gaps` (List of start, length), `MinWidth`, `MaxWidth`, `MaxOffset`.
- Parameters: Width tolerance (Essential), Max offset (Essential), Max gap length (Essential), Sample spacing (px, 5; Advanced), Bead polarity (Advanced).
- Auto-learn: trace the bead with a finger or click along it; the path is snapped to the bead centre and nominal width measured.
- Explain: `explain.bead.gap` = "Glue missing for {length} {unit} at the red mark." · `explain.bead.narrow` = "Glue too thin at the red mark." · `explain.bead.offset` = "Glue is off its path by {value} {unit}."
- Targets: ≤ 10 ms for a 300-sample path. Algorithm: pair calipers perpendicular to the path (2.1) at each sample, gap = run of samples with no pair.

### 8.6 Label inspection (`inspect.label`) — "Check label"

Purpose: check a label is present, in position, straight, and correctly printed.

- Inputs: `Image`, `Region`, `Fixture`. Outputs: `Offset` (x, y; px/mm), `Skew` (°), `PrintScore` (0–100), `Defects`.
- Parameters: Position tolerance (Essential), Skew tolerance (°, 2; Essential), Print check (on; Essential), Print sensitivity (Advanced).
- Auto-learn: tap the label: edges found (2.2), its design taught as a template.
- Explain: `explain.label.missing` = "Label is missing." · `explain.label.skew` = "Label is crooked by {value}°." · `explain.label.position` = "Label is {value} {unit} out of place." · `explain.label.print` = "Label print is damaged or wrong at the red mark."
- Targets: ≤ 25 ms on T1. Algorithm: label edges by 2.2, pose relative to the part fixture, print by 8.2 inside the label fixture.

### 8.7 Cap / seal check (`inspect.cap`) — "Check cap"

Purpose: check caps are present, fully screwed, straight, and the seal or tamper band is intact.

- Inputs: side or top view `Image`, `Fixture`. Outputs: `Height` (px/mm), `Tilt` (°), `BandOk` (bool), `BandGaps` (List).
- Parameters: View (Side / Top; Essential), Height tolerance and Tilt limit (Essential), Band check (on; Advanced).
- Auto-learn: tap the cap; side view finds the cap top and bottle shoulder lines; top view finds the cap circle and band annulus.
- Explain: `explain.cap.missing` = "Cap is missing." · `explain.cap.high` = "Cap is not screwed down ({value} {unit} high)." · `explain.cap.tilt` = "Cap is tilted by {value}°." · `explain.cap.band` = "Seal band is broken at the red mark."
- Targets: ≤ 10 ms on T1. Algorithm: side: two 2.2 lines, height and angle between them. Top: 2.3 circle, 0.11 polar unwrap of the band, gaps by profile.

---

## Tool 9. Colour

Operator name: **Check colour**. Licence `VS-COL-`. Phase P4. Needs a colour camera. All colour
tools work in CIE Lab (D65) after white-reference correction, so they tolerate lighting drift.

### 9.0 White reference (`colour.white`) — shared by all colour tools

Purpose: correct colour for the current light.

- Inputs: `Image`, a region on a white or grey reference visible in every image, or a taught reference image. Outputs: `Gains` (R, G, B), `Drift` (% change since teach).
- Parameters: Reference mode (Fixed patch in every image / Taught once; Essential), Colour chart correction (off / 3×3 matrix from a 24-patch chart; Advanced).
- Explain: `explain.colour.white_lost` = "White reference not visible or dirty. Clean it." · `explain.colour.drift` = "Light colour has changed by {value}%. Check the lamp."
- Algorithm: per-channel gains to make the patch neutral; optional colour correction matrix by least squares on chart patches; sRGB to Lab with D65.

### 9.1 Colour match (`colour.match`) — "Check colour"

Purpose: check a region's colour is close to a taught colour.

- Inputs: `Image`, `Region`. Outputs: `MeanLab`, `DeltaE`, `BestMatch` (name).
- Parameters: Taught colours (Essential), Max ΔE (3.0; Essential), ΔE formula (ΔE2000; Advanced).
- Auto-learn: click a colour to learn it (mean and spread over a small area or the drawn box).
- Pass/fail: ΔE ≤ max. Confidence = 100 × (1 − ΔE / (2 × max)), floored at 0.
- Explain: `explain.colour.mismatch` = "Colour is off by {value} (allowed {max}). Check the material or print."
- Targets: ≤ 1 ms. Algorithm: mean Lab in region, CIEDE2000.

### 9.2 Colour presence (`colour.presence`) — "Is the colour there?"

Purpose: measure how much of a taught colour is in a region.

- Outputs: `Area` (px or mm²), `Fraction` (%). Parameters: Colour (Essential), Tolerance (ΔE; Essential), Min and max area (Essential).
- Auto-learn: click the colour; area limits from good parts.
- Explain: `explain.colour.too_little` = "Not enough {colour} here ({value}%)." · `explain.colour.too_much` = "Too much {colour} here ({value}%)."
- Targets: ≤ 5 ms for 2 MP. Algorithm: per-pixel ΔE (LUT on quantised RGB), count.

### 9.3 Colour segmentation (`colour.segment`) — "Find colour areas"

Purpose: split the image into regions of taught colours, as input to blob tools.

- Outputs: `LabelImage`, `Masks` (one per colour). Parameters: Colours (Essential), Tolerance (Advanced), Smoothing (Advanced).
- Auto-learn: click each colour of interest once.
- Targets: ≤ 10 ms for 5 MP on T1. Algorithm: nearest taught colour per pixel within tolerance, through a 3D lookup table.

### 9.4 Colour sorting (`colour.sort`) — "Sort by colour"

Purpose: decide which of N taught colours a part is (variant check, wire order).

- Outputs: `Class` (name), `DeltaE` to best and second best. Parameters: Classes (Essential), Min separation (ΔE; Advanced).
- Auto-learn: show each variant once and name it.
- Explain: `explain.colour.unsure` = "Colour is between {a} and {b}. Check the part."
- Algorithm: nearest class by ΔE2000 with a margin rule.

### 9.5 ΔE measurement (`colour.deltae`) — "Measure colour"

Purpose: report colour difference against a reference for quality records.

- Outputs: `L`, `a`, `b`, `DeltaE76`, `DeltaE94`, `DeltaE2000`, `dL`, `da`, `db`. Parameters: Reference (taught or typed Lab; Essential), Formula and limit (Essential).
- Targets: ≤ 1 ms. Note: a camera with a white reference gives relative colour; absolute colorimetry needs the chart correction of 9.0 and stable light.

---

## Tool 10. AI Deep Learning

Operator name: **Teach by example**. Licence `VS-DL-`. Phase P3. Full specification, training
workflow, model formats and inference rules are in [AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md).
The AI assistant features (auto-find, auto-learn, auto-tolerance, auto-light check, drift
monitor) are in every licence and also specified there.

| Sub-tool | Tool ID | Outputs | Training data | Pass/fail | Example explain key |
| --- | --- | --- | --- | --- | --- |
| Anomaly detection | `dl.anomaly` | anomaly score 0–100, heat map, defect regions | 20–100 good images | score ≤ limit | `explain.dl.anomaly` = "Something unusual at the red area." |
| Classification | `dl.classify` | class, probability per class | 30+ per class | expected class with probability ≥ limit | `explain.dl.class` = "Looks like {class}, expected {expected}." |
| Object detection | `dl.detect` | boxes, classes, scores, count per class | 50+ labelled | counts and classes as rules | `explain.dl.missing` = "{class} missing at the marked place." |
| Segmentation | `dl.segment` | mask per class, area, regions | 20+ pixel-labelled | area or region rules | `explain.dl.region` = "Defect area {area} {unit}² at the red area." |
| AI OCR fine-tuning | `dl.ocr_tune` | fine-tuned model for 7.1 | 50+ images | as 7.1 | as 7.1 |
| AI locator | `dl.locate` | poses (training for 1.5) | 5–10 images | as 1.5 | as 1.5 |
| Auto-labelling | `dl.autolabel` | proposed labels for review | existing model | n/a (training aid) | n/a |

Common rules: confidence is the model's calibrated probability × 100; every result has a heat
map or box for the explain view; FP16/INT8 models pass an accuracy check before deployment; CPU
fallback always available. Targets: under 20 ms for 5 MP on T2 GPU (requirements section 13);
training under 10 minutes for typical datasets on a local GPU.

---

## Tool 11. Multi-Camera & Enterprise

Operator name: **Connect machines**. Licence `VS-MCE-`. Phase P4. Base already includes one
camera per job, all PLC protocols of [PLC-INTERFACE.md](PLC-INTERFACE.md), TCP/IP and CSV
output. These sub-tools are job-level rather than image tools; they follow the same contract,
with pass/fail meaning "the link or the sync is healthy".

### 11.1 Multi-camera job (`mce.multicam`) — "Add camera"

Purpose: run one job over 2–8 cameras as Independent, Synchronised, Sequential or Stitched
(requirements section 8).

- Inputs: one 0.1 source per camera; part ID or shift-register position from the PLC for Sequential. Outputs: per-camera results, `Pass` (combined), `SyncSpreadUs` (spread of exposure timestamps).
- Parameters: Job type (Essential), Cameras (Essential), Combine rule (All must pass; Advanced), Part link (Part ID / Shift register depth N; Essential for Sequential), Camera timeout (Advanced).
- Auto-learn: thumbnails of all found cameras; tapping several builds the job with one branch each.
- Explain: `explain.multicam.missing` = "Camera {name} sent no picture for this part." · `explain.multicam.link` = "Results for part {id} from station {station} are missing."
- Targets: per-camera pipelines run in parallel; added overhead ≤ 2 ms per part. Algorithm: one acquisition thread and one tool-chain worker per camera; results joined by trigger ID or part ID.

### 11.2 Action-command sync (`mce.sync`) — "Fire together"

Purpose: trigger several GigE cameras at the same instant from one PLC trigger.

- Parameters: Sync method (GigE action command with PTP / Sequential software triggers; Essential), Action device key, group key and mask (auto; Advanced), Scheduled delay (µs, 0; Advanced).
- Pass/fail: all cameras report PTP locked and exposure timestamps within the limit (100 µs default, target). Explain: `explain.sync.ptp` = "Camera {name} clock is not in step. Ask the engineer to check PTP." · `explain.sync.spread` = "Cameras fired {value} µs apart."
- Algorithm: GigE Vision `ACTION_CMD` broadcast on the camera subnet, optionally scheduled with PTP time; USB3 cameras use sequential software triggers.

### 11.3 OPC UA server (`mce.opcua`) — "Connect machines"

- Purpose: publish results, status and job control as an OPC UA information model.
- Parameters: Endpoint and port (4840; Essential), Security policy (Basic256Sha256 Sign & Encrypt; Essential), Users and certificates (Advanced), Published values (Advanced).
- Explain: `explain.opcua.cert` = "A client certificate was refused. Ask the engineer to trust it."
- Targets: value update ≤ 10 ms after the result. Algorithm: open62541.

### 11.4 MQTT (`mce.mqtt`) — "Connect machines"

- Purpose: publish results to a broker for dashboards and IIoT.
- Parameters: Broker URL, TLS, credentials reference (Essential), Topic template (`vs/{line}/{job}/result`; Advanced), QoS (1; Advanced), Payload (JSON fields; Advanced).
- Explain: `explain.mqtt.broker` = "Cannot reach the data server. Results are kept and sent later."
- Algorithm: local queue with store-and-forward; never blocks inspection.

### 11.5 MES / SQL output (`mce.database`) — "Send to MES"

- Purpose: write records to a plant SQL database or MES endpoint.
- Parameters: Connection (SQL Server / PostgreSQL / MySQL / REST; Essential), Table or endpoint mapping (Essential), Retry and buffer size (Advanced).
- Explain: `explain.db.offline` = "Factory database is not reachable. Results are kept and sent later."
- Algorithm: background writer with local buffer in the job database; at-least-once delivery with a record ID for de-duplication.

### 11.6 Web HMI (`mce.webhmi`) — "Remote screen"

- Purpose: read-only view of the Run screen, counters and last NG images on a tablet.
- Parameters: Enabled (Essential), Port and TLS certificate (Advanced), Allow control (off; Advanced; role-checked).
- Targets: image refresh ≥ 2 per second per viewer without affecting cycle time. Screens are in [UI-UX-SPEC.md](UI-UX-SPEC.md).

---

## Tool 12. Scripting & Advanced Logic

Operator name: **Custom rules**. Licence `VS-SCR-`. Phase P4. Scripts run sandboxed and cannot
crash the runtime (requirements section 4).

### 12.1 Script (`script.code`) — "Custom rules"

Purpose: run C# or Python code over tool outputs (and images, read-only) for rules the standard
tools do not cover.

- Inputs: any typed outputs; `Image` read-only. Outputs: user-declared typed outputs, `Pass`.
- Parameters: Language (C# / Python; Essential), Code (Essential), Time limit (ms, 20; Advanced), Memory limit (MB, 256; Advanced).
- Pass/fail: as the script sets `Pass`; a script that throws, exceeds its time limit or memory limit returns Error.
- Explain: the script may return its own explain key from its job's string table; built-in: `explain.script.timeout` = "Custom rule took too long. Call the engineer." · `explain.script.error` = "Custom rule failed. Call the engineer."
- Targets: call overhead ≤ 0.5 ms (C#), ≤ 2 ms (Python). Algorithm: separate sandbox process with a typed message channel; C# compiled with Roslyn in a restricted load context; Python in an embedded interpreter with a module allow-list; watchdog kills and restarts the sandbox.

### 12.2 Formula (`script.formula`) — "Calculate"

Purpose: one-line maths and logic without writing a script (for example `(A.Width + B.Width) / 2`).

- Inputs: any outputs. Outputs: `Value`, optional `Pass` from limits.
- Parameters: Expression (Essential), Limits (Essential).
- Explain: `explain.formula.out_of_range` = "{name} is {value}, allowed {low}–{high}."
- Targets: ≤ 0.02 ms. Algorithm: expression parsed once to a typed tree; arithmetic, comparison, logic, min/max/abs/sqrt, string functions.

### 12.3 State machine (`script.state`) — "Sequence"

Purpose: keep state across parts: count consecutive NG, alternate recipes, multi-step sequences.

- Inputs: any outputs, PLC signals. Outputs: `State`, user variables.
- Parameters: States and transitions (graphical editor; Essential), Persist across restart (off; Advanced).
- Explain: `explain.state.alarm` = "{count} bad parts in a row. Stop and check the machine."
- Algorithm: table-driven state machine evaluated once per part, after all tools.

### 12.4 Data analysis / SPC (`script.spc`) — "Trends"

Purpose: track measurements over time and warn before parts go out of tolerance.

- Inputs: numeric outputs. Outputs: `Mean`, `Sigma`, `Cp`, `Cpk`, `Trend` (slope per hour), `RuleViolations` (List).
- Parameters: Values to track (Essential), Subgroup size (5; Advanced), Rules (Western Electric 1–4; Advanced), Window (parts or hours; Advanced).
- Pass/fail: never fails a part; raises warnings. Explain: `explain.spc.trend` = "{name} is drifting towards its limit. Check the machine soon." · `explain.spc.cpk` = "Process capability for {name} is low ({value})."
- Targets: ≤ 0.1 ms per value. Algorithm: running statistics (Welford), control-chart rules on subgroups, Cp/Cpk from limits.

---

## Cross-tool summary

All cycle times are **targets** for planning, to be confirmed by the built-in benchmark in P1
([TEST-ACCEPTANCE-PLAN.md](TEST-ACCEPTANCE-PLAN.md)). Basis: one 5 MP mono image (colour for
Tool 9), a typical ROI as stated in each tool, tool time only (no acquisition). T1 = Core i5 / Ryzen
5, no discrete GPU (OpenVINO on iGPU); T2 = Core i7 / Ryzen 7 with an RTX 4060 / A2000-class GPU,
as in the requirements section 11.

| Tool | T1 target (ms) | T2 target (ms) | Runs on | Phase |
| --- | --- | --- | --- | --- |
| 0.1–0.2 Image source, camera settings | ≤ 2 (PC side) | ≤ 2 | CPU | P1 |
| 0.3–0.12 Image processing (per operation) | 1–8 | 1–4 | CPU (GPU optional for large images) | P1 |
| 0.13–0.15 Overlay, logic, logging | ≤ 1 | ≤ 1 | CPU | P1 |
| 1.1 Correlation match (±15°) | ≤ 15 | ≤ 8 | CPU | P1 |
| 1.2 Geometric match (±180°) | ≤ 30 | ≤ 15 | CPU | P1 |
| 1.3 Multi-instance (≤ 50) | ≤ 45 | ≤ 25 | CPU | P1 |
| 1.5 AI locator | ≤ 80 | ≤ 25 | iGPU (T1), GPU (T2) | P3 |
| 2.1–2.9 Measure (per tool) | ≤ 2 | ≤ 2 | CPU | P1 |
| 3.1 Grid calibration (setup, not per part) | ≤ 2 000 | ≤ 2 000 | CPU | P1 |
| 3.2 Lens correction image | ≤ 10 | ≤ 4 | CPU (T1), GPU (T2) | P1 |
| 3.6 Stitch 2 × 5 MP | ≤ 25 | ≤ 10 | CPU (T1), GPU (T2) | P4 (needs Tool 11) |
| 4.1–4.4 Blob & Count | ≤ 10 | ≤ 6 | CPU | P1 |
| 5.1 Read code, single code in 1 MP | ≤ 15 | ≤ 10 | CPU (AI locator and enhancer on iGPU/GPU) | P2 |
| 5.1 Read code, ≤ 50 codes in 5 MP | ≤ 200 (default budget) | ≤ 100 | CPU + GPU | P2 |
| 6.1–6.3 Grade one code | ≤ 20 | ≤ 15 | CPU | P2 |
| 7.1 AI OCR, 3-line block | ≤ 25 | ≤ 8 | iGPU (T1), GPU (T2) | P2 |
| 7.2 Classical OCR, 20 characters | ≤ 5 | ≤ 5 | CPU | P2 |
| 7.3 OCV, 20 characters | ≤ 10 | ≤ 8 | CPU (+ GPU for detection) | P2 |
| 8.1 Presence | ≤ 1 | ≤ 1 | CPU | P4 |
| 8.2 Golden template (2 MP region) | ≤ 15 | ≤ 6 | CPU (T1), GPU (T2) | P4 |
| 8.3–8.7 Edge, surface, bead, label, cap | ≤ 25 | ≤ 15 | CPU | P4 |
| 9.0–9.5 Colour | ≤ 10 | ≤ 5 | CPU | P4 |
| 10 Anomaly / classification | ≤ 150 (OpenVINO) | ≤ 20 | GPU (CPU fallback) | P3 |
| 10 Detection / segmentation | not recommended on T1 | ≤ 20 | GPU (T2 minimum; T3 for multi-camera) | P3 |
| 11.1–11.2 Multi-camera, sync | ≤ 2 overhead | ≤ 2 overhead | CPU | P4 |
| 11.3–11.6 OPC UA, MQTT, MES, web HMI | off the inspection path | off the inspection path | CPU | P4 |
| 12.1 Script (call overhead) | ≤ 2 | ≤ 2 | CPU, sandbox | P4 |
| 12.2–12.4 Formula, state machine, SPC | ≤ 0.1 | ≤ 0.1 | CPU | P4 |

## Open questions

- **AI locator licence.** 1.5 sits under Locate in the requirements, while its training sits in
  AI Deep Learning. Proposal: inference and the 5–10 image training run under `VS-LOC-`; the full
  training workflow (auto-label, versioning) needs `VS-DL-`. To be confirmed in
  [LICENSING.md](LICENSING.md).
- **Code Verify without Code Reader.** Verification needs a reference decode. Proposal: `VS-VER-`
  may call `vs_code.dll` for the reference decode of the code being graded, without a `VS-COD-`
  licence, but cannot run 5.1 as a tool.
- **Free Base serial.** Whether Base needs a `VS-BASE-` serial at all, or is unlocked by the
  installation itself. A serial makes counting installations easy; no serial is simpler for users.
- **Patent territory per licence.** Engine parts built for Indian lines (for example the
  under-resolved QR reader and multi-frame fusion) need a territory switch before export
  ([../PATENTS.md](../PATENTS.md)). The licence file could carry the territory so one build serves
  all markets.
- **Stitching phase.** 3.6 is in Calibrate (P1) but needs multi-camera jobs (P4). Proposal: ship
  per-camera world calibration in P1 and the stitched image in P4.
- **Confidence mapping for codes.** The engine has no per-code confidence today; the mapping from
  error correction used and fix step to 0–100 needs data from the benchmark set before it is
  fixed.
- **DotCode and Pharmacode decoders.** Built in the engine (2026-10-30, 5.0); their read rate on
  real line images is still to be measured.
