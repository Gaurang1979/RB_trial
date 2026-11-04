# Study 05: Hardware that takes reading and inspection to almost 100 %

Date: 2026-10-30. Part of the Vision_System study (common brief: owner's request of 2026-10-30,
"I can upgrade hardware if required"). Layer names L0-L6 are those of the brief. This chapter is
mostly L0 (acquire) and the line PC; it says what each purchase does to the miss reasons we measure.

Legend. Sources opened in this study carry no mark. **[S]** = seen only in a search snippet.
**[not opened]** = named from the author's general knowledge or a link not opened; check before buying.
All prices are **author's estimates** (nothing below is a published price: the vendor pages opened
say "price on request"). Get quotes before any decision. Costs are INR with USD at about 84 INR.

---

## 1. Summary

1. **Cheapest big win first: the exposure.** The camera's default `ExposureUs` is 2000
   (`dotnet/HikCamera/HikCamera.cs:14`). At 225 mm/s that is 0.45 mm of travel, 4.5 px at 0.1 mm
   per pixel [estimate; example speed and pitch: 60 mm pitch at 225 parts per minute, section 4.4]. A strobe pulse of about 100 microseconds (overdriven LED light with
   controller) removes BLUR from moving parts. It costs less than any camera.
2. **Next: the light, per problem.** Our biggest misses are LOW_CONTRAST (114 miss regions on the
   shrink-wrapped excise cases, 39 on 48 DM caps, section 3). That is a light problem, not a pixel
   problem: coaxial or dome light for film and foil, low-angle dark-field for dark caps, polariser
   where it helps.
3. **Then the camera.** The reference camera, MV-CS200-10UM, is a rolling-shutter 20 MP sensor
   (IMX183, USB3, 19.2 fps, requirements section 8; 2.4 um pixel from the sensor datasheet, not in
   the repo). For a moving line move to a
   **global-shutter** sensor. Resolution only helps TOO_SMALL (below 1.8 px per module); it does not
   help LOW_CONTRAST or BLUR.
4. **A 25 MP square sensor is not an upgrade for wide cases.** 5120 pixels across is fewer than 5472.
   For a wide case use a 3:2 sensor or two cameras (section 5).
5. **The line PC is the unknown.** The reference PC has DDR3 memory (PPC-0313-065-D), so it is an old
   CPU generation. All our time targets were measured on an Apple M3, 8 cores
   (`docs/BATCH-TEST-RESULTS.md`). Before buying anything else, run `tools/bench/batch_check.py` on the
   line PC. A modern 8-16 core CPU (T2/T3) is a certain buy; a GPU only if the AI tools need it.
6. **Triggering.** Software trigger over Modbus TCP has millisecond jitter. At 375 mm/s, 10 ms is
   3.75 mm [estimate, not measured; example speed and pitch]. Allow a hardware trigger (photoeye to camera Line0) and camera strobe (Line1) on the fast
   line. The code exists (`HikCamera.cs` Line0 / Line1); `docs/vision-system/ARCHITECTURE.md:529` plans
   to remove it. That decision should be revisited for 225 ppm.
7. **Prove each purchase** with `tools/bench/batch_check.py` (codes) and `tools/bench/ocr_bench.cpp`
   (text), on images taken with the new part, same samples, before and after (section 12).

---

## 2. Today's hardware (found in the repo)

| Item | What the repo says | Source |
| --- | --- | --- |
| Top camera | Hikrobot MV-CS200-10UM: 20 MP, 5472 x 3648, 1" Sony IMX183, **rolling shutter**, USB3, 19.2 fps, C-mount (2.4 um pixel, from the sensor datasheet; not stated in the repo) | `docs/REQUIREMENTS-VISION-SYSTEM.md` section 8 "Reference line hardware" |
| Older camera | 4096 x 2160 (8.8 MP); the old excise, hologram and faint-label images come from it | `docs/RESEARCH-NEXT-GENERATION.md` R0 |
| Lens | Not recorded anywhere in the repo (focal length, f-number, make). **Gap: record it.** | grep of docs, `HikCamera.cs` |
| Light | PLC switches the light ("Strobe Light now we are starting through PLC", owner 2026-10-23); camera can also drive Line1 strobe with pre-delay and pulse length (`StrobeOnLine1`, `StrobePreDelayUs`, `StrobeDurationUs`). Make and type of light not recorded | requirements section 8; `HikCamera.cs:14-44` |
| Trigger | Design rule: no camera I/O cable; PLC trigger over Ethernet gives a software trigger; hardware trigger and strobe "removed" in the new runtime | requirements section 8; `TOOL-SPECIFICATIONS.md:146`; `ARCHITECTURE.md:529`; `JOB-FILE-FORMAT.md:298` |
| PLC | Delta DVP12SE11T (Ethernet, Modbus TCP) + DVP16SP11T; encoder A/B on X0/X1 into high-speed counter C251; photoeyes X2/X24/X25 per camera; reject valve Y0; light enable Y1 | `docs/vision-system/PLC-DELTA-DVP.md` |
| Line PC | Fanless industrial i7 PC, PPC-0313-065-D, 16 GB DDR3, 128 GB SSD, Windows 10 IoT; "i5 or i7 with no dedicated GPU"; owner: "can be slightly upgraded", and (2026-10-29) can use the processor and GPU the job needs | requirements section 8 |
| Line speed | 225 parts per minute on the two-camera line (3.75 parts per second). Part pitch and conveyor speed are **not in the repo**; this chapter uses 60 mm and 100 mm pitch (225 and 375 mm/s) as examples | owner brief |
| Cases | 12/24/45/48/96 codes, "standing still or moving slowly" | requirements section 8 |
| Measured on | Apple M3, 8 cores, Release build; **not on the line PC** | `docs/BATCH-TEST-RESULTS.md` header |
| Known guard | Whole-PC CPU, memory and disk sampled every 5 s; a frame with no free buffer becomes NG | `docs/changes/2026-10-29-claude-line-pc-guard.md`, `2026-10-30-claude-line-pc-guard-cpu-buffers.md` |
| Open optics tests | R0 (re-shoot hard cases on MV-CS200), R13 (f/4 vs f/8, polariser, Mono12), R14 (4x exposure) are **not done** | `RESEARCH-NEXT-GENERATION.md` R0, R13, R14; INDUSTRY-METHODS rank 1 |

What this means: we have never tried a second light, a polariser, a different lens or a strobe
pulse on the real line, and we do not know the lens. Several purchases below are therefore
"measure first, buy second" (R13 says the same).

---

## 3. Our measured misses, and which hardware answers which

Read / visible from the baseline on main (`truth/batch-baseline.csv`); missing codes and miss
regions from `docs/BATCH-TEST-RESULTS.md` (carried run, so its missing counts differ slightly from
the baseline; its UNKNOWN column is left out here):

| Batch | Read / visible (baseline) | Missing codes | BLUR | LOW_CONTRAST | NO_FINDER | COVERED | GLARE | TOO_SMALL |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 24 QR | 372 / 384 | 12 | 18 | 5 | 12 | 0 | 5 | 0 |
| 48 QR (short images) | 843 / 857 | 10 | 15 | 14 | 6 | 4 | 0 | 0 |
| 48 DM | 61 / 95 | 34 | 4 | 39 | 16 | 26 | 0 | 0 |
| other QR (shrink-wrap) | 129 / 214 (60.3 %) | 83 | 0 | 114 | 4 | 11 | 0 | 19 |

| Miss reason | Physical cause | Hardware lever (this chapter) | Does more pixels help? |
| --- | --- | --- | --- |
| LOW_CONTRAST | Film / foil flattens the print; dark cap, dark code on dark ground; faint ink | Light type and angle (coaxial, dome, cross-polariser, dark-field), wavelength, filter, exposure, Mono12 | No (only if it is TOO_SMALL too) |
| BLUR | Motion during exposure; defocus (depth of field, curved can side); diffraction at f/8 on small pixels; soft lens corners | Strobe pulse, global shutter, lower f-number to f/4-5.6 plus a lens that resolves the pixel, focus plane, telecentric | Slightly (smaller blur in codes) but not the cause |
| TOO_SMALL | Module under 1.8 px | More pixels, longer focal length, smaller FOV, second camera | Yes |
| NO_FINDER | Finder pattern lost to curvature, glare, blur, a cropped case | Light (glare), lens quality in corners, FOV margin | Partly |
| COVERED | Code hidden by fold, film edge, another label | Camera angle, second camera from another side; not fixable by a sensor | No |
| GLARE | Specular reflection | Cross-polarisation, dome / diffuse, polarisation sensor, angle off specular | No |

Rule from this table: the first spend on a failing product is **light and exposure**, not
resolution. Only 19 of 176 miss regions in the "other QR" batch (28 of them UNKNOWN) were TOO_SMALL.

---

## 4. Resolution math

### 4.1 Pixels per module

`px/module = module size (mm) / object pixel size (mm)`, and
`object pixel size = field of view width (mm) / pixels across`.

Our reader reads 2.3-3 px modules today and fails below about 1.8 px (TOO_SMALL threshold in the
bench). 1D codes: the zxing path failed below 1.5 px per module on synthetic frames
(`RESEARCH-NEXT-GENERATION.md` section on linear codes).

| Code type | Failing | Design minimum (read) | Design target (robust at line speed) | Why |
| --- | --- | --- | --- | --- |
| QR printed on carton (inkjet / flexo) | < 1.8 px | 2.5 px | 3.5-4 px | Blur of 0.5-1 px and print growth eat 1 px of margin |
| Data Matrix printed | < 1.8 px | 2.5 px | 3.5-4 px | Same; DM has no quiet zone to spare (L-finder must be seen) |
| DM / QR laser or dot-peen on metal or cap (DPM) | < 2.5 px | 3 px | 4-5 px | Dots, not squares: each module is a few dots; cell fill needs more pixels. Common DPM practice, [not opened] |
| 1D X-dimension (GS1-128, EAN, ITF-14, SSCC) | < 1.5 px | 2 px | 3 px | Edge position needs sub-pixel; bar width error from blur and ink spread |
| OCR, character height | < 12 px | 20 px | 30-40 px (stroke 3 px+) | Our `ocr_bench --char-h` option; CNN reads 32-px-high lines in `ml/` (see chapter on OCR) |
| OCR, stroke / dot width (CIJ, TTO) | < 1.5 px | 2 px | 3 px | Rule: pixel <= stroke / 3 |
| CIJ dot pitch (5x7, 7x9 matrix) | < 2 px per dot gap | 3 px | 4 px | Dots must stay separate, then be joined (our `--join`) |
| Measurement (gauge) | n/a | 1/10 of tolerance per pixel is the usual rule | 1/10-1/20 | Telecentric lens, sub-pixel edge gives 0.1 px; requirement section 13 targets 0.1 px 3 sigma |

### 4.2 Field of view and sensor choice (worked examples)

Assumptions to replace with the real numbers (module size and case size are not in the repo):

| Case | FOV width | Sensor 5472 px (20 MP, 3:2) | Sensor 4504 px (20 MP, square) | Sensor 5120 px (25 MP, square) | Sensor 4096 px (12 MP) |
| --- | --- | --- | --- | --- | --- |
| 12 labels (small case) | 250 mm | 0.046 mm/px | 0.056 | 0.049 | 0.061 |
| 48 / 96 codes (large case) | 600 mm | 0.110 mm/px | 0.133 | 0.117 | 0.146 |
| module 0.30 mm gives | at 600 mm | **2.7 px** | 2.3 px | 2.6 px | 2.0 px |
| module 0.30 mm gives | at 450 mm | 3.6 px | 3.0 | 3.4 | 2.7 |

Reading: a wide case over 500 mm on one camera cannot reach 3.5 px per module with any sensor
sold today in this class with 0.3 mm modules. Options: (a) crop the FOV to the code area (the
Calibrator already proposes this), (b) two cameras side by side with overlap (hardware triggers,
section 9), (c) move to a 3:2 sensor above 20 MP. The pixel counts above are from the camera pages
opened (IMX183 from the repo; MV-CH250 5120 x 5120; MV-CH120 4096 x 3000;
[Pregius S IMX531/541 4504 x 4504, IMX535 4096 x 3000, IMX547 2448 x 2048 [S]](https://www.baslerweb.com/en/sales-support/knowledge-base/sony-pregius-s-sensors/)).

### 4.3 Pixel size, mono or colour, global shutter, frame rate

| Choice | Rule for our work | Reason |
| --- | --- | --- |
| Mono, not colour | Mono for all code reading and OCR; colour only where colour is the check (label colour, excise stamp colour) | A Bayer colour sensor loses about half the resolution and sensitivity for a monochrome print. Mono also allows a bandpass filter matched to the LED colour |
| Pixel size | 2.4-3.45 um is fine if the lens resolves it; do not go below 2.4 um on a 1" lens at f/8 | Diffraction, section 4.5 |
| Global shutter | Required on any moving line (225 ppm); rolling shutter only for stopped cases | Rolling shutter skews by speed x readout time; with a strobe it also forces the flash to cover the whole readout window |
| Frame rate | At least 1.3 x parts per second (3.75 x 1.3 = 5 fps) per camera, plus the bandwidth to carry it | Headroom for missed triggers |
| Bit depth | Mono10/12 on faint products (R13) | 4-16 times the grey levels on a low-contrast label for no light cost; needs 1.5 x the bandwidth in packed 12-bit |

Bandwidth check (Mono8, one camera at 3.75 fps): 5472 x 3648 = 20 MB per frame = 75 MB/s: fits
GigE (about 115 MB/s usable) but not Mono12 packed (112 MB/s, no margin). 25 MP at its full 4.5 fps
is 118 MB/s: **does not fit GigE**; needs 5GigE / 10GigE or USB3. Hikrobot lists 10GigE 12 MP
models (MV-CH120-10TM / -10TC) [S](https://www.maxxvision.com/produkte/kameras/10gige-vision-kameras/2556/mv-ch120-10tm).

### 4.4 Motion blur limit

`blur (px) = speed (mm/s) x exposure (s) / object pixel size (mm)`. Keep blur at or below 0.3 px
for DM / QR at 2.5-3.5 px per module, and at or below 0.2 px for dot-matrix text.

| Speed | Pitch 60 mm = 225 mm/s | Pitch 100 mm = 375 mm/s |
| --- | --- | --- |
| Pixel 0.20 mm: exposure for 0.3 px | 267 us | 160 us |
| Pixel 0.10 mm: exposure for 0.3 px | 133 us | 80 us |
| Pixel 0.05 mm: exposure for 0.3 px | 67 us | 40 us |
| **Default 2000 us at pixel 0.10 mm** [estimate; example speed and pitch] | **4.5 px blur** | **7.5 px blur** |
| Trigger jitter 10 ms (Modbus TCP) [estimate; example speed and pitch] | 2.25 mm = 22 px at 0.10 mm | 3.75 mm = 37 px |
| Trigger jitter 0.1 ms (hardware) | 0.02 mm | 0.04 mm |

Consequences: (1) a 100 us exposure needs 20 times the light of 2000 us: use a **strobe light in
overdrive** (Effilux EFFI-Ring AutoStrobe claims up to 700 % over continuous, [vendor claim,
opened](https://1stvision.com/lighting/Effilux-ring-lights); Smart Vision Lights sells an
"OverDrive-only" light, [opened](https://smartvisionlights.com/)). (2) Position jitter matters for the
FOV margin and for the reject tracking, not for blur; the PLC's encoder handles tracking
(`PLC-DELTA-DVP.md`). (3) With a rolling-shutter camera, a short flash gives the right picture
only if every row is exposed during the flash: the exposure must be at least the readout time, so
daylight and plant lights must be blocked (shroud plus bandpass filter). A global-shutter camera
removes this limit.

### 4.5 Depth of field, diffraction and f-number

Formulas (thin lens, circle of confusion c = 2 pixels):
`DOF = 2 N c (1 + m) / m^2`, `m = sensor width / FOV`, Airy disc diameter = 2.44 lambda N (1 + m),
diffraction-limited cut-off `1 / (lambda N)` lp/mm. Green light, lambda = 0.55 um.

Worked: 2.4 um pixel, 5472 px = 13.1 mm sensor, FOV 450 mm: m = 0.029, c = 4.8 um.
DOF = N x 11.6 mm: f/4 = 46 mm, f/8 = 93 mm.

| f-number | Diffraction cut-off | Diffraction MTF at 2.4 um Nyquist (208 lp/mm) | Airy disc | DOF at m = 0.029 |
| --- | --- | --- | --- | --- |
| f/2.8 | 649 lp/mm | 0.61 | 3.8 um (1.6 px) | 32 mm |
| f/4 | 455 lp/mm | **0.44** | 5.4 um (2.2 px) | 46 mm |
| f/5.6 | 325 lp/mm | 0.25 | 7.5 um (3.1 px) | 65 mm |
| f/8 | 227 lp/mm | **0.03** | 10.7 um (4.5 px) | 93 mm |

This is why R13 says "f/8 costs MTF at 2.5 px per module on 2.4 um pixels": at f/8 the lens cannot
carry detail at pixel pitch (ideal, no aberrations; real lenses are worse). The trade is
depth (flat cartons need little: 20-30 mm; curved cans need more) against sharpness. For
2.74 um pixels (Pregius S) Nyquist is 182 lp/mm: f/4 gives 0.50, f/8 about 0.10. For 3.45 um pixels
(IMX304, IMX250) Nyquist is 145 lp/mm: f/4 about 0.60, f/8 about 0.25. **Larger pixels are more forgiving, and
they gather more light per pixel (shorter strobe).** A 12 MP / 3.45 um global-shutter sensor can
read better than a 20 MP / 2.4 um sensor at the same FOV when modules are 3 px or more.

### 4.6 Lens choice

| Need | Lens | Notes |
| --- | --- | --- |
| Case top, flat, 250-600 mm FOV | Fixed focal, C-mount, **1" or larger format, rated for the pixel size** (resolving power at least 1.5 x Nyquist, flat MTF to the corners) | Example: f = WD x sensor width / FOV = 600 x 13.1 / 450 = 17.5 mm, so 16 or 25 mm. Moritex ML-M-UR series is rated 5 MP / 200 lp/mm on 2/3" [S](https://empireoptics.com/collections/machine-vision/series_ml-m-ur); ML-MC-XR for 2.2 um pixels on 1.2" [S](https://empireoptics.com/pages/moritex-ml-mc-xr-series). Do not use a 2/3" lens on a 1" sensor: dark corners, soft corners (owner already reports soft corners) |
| Curved can or cap | Lower N (f/4) plus a light that does not depend on exact focus | Corner blur reported by owner 2026-10-23 |
| Measurement stations | **Bi-telecentric** lens (no perspective error, constant magnification over depth) and a collimated backlight | Needed for the 0.1 px 3 sigma target (requirements 13); one lens per FOV, costly |
| Part height varies (mixed products on one line) | **Liquid lens** or motorised focus C-mount (a Basler ace 2 / Allied Vision / Hikrobot-compatible liquid lens, [not opened]) | Focus per product from the job; changes the focal distance in ms; adds a little aberration. Our Calibrator already has a focus score bar |
| Dark caps, DPM | Fixed lens with a **short working distance and a ring light close to the part** | Dark-field angle depends on the light height, not the lens |
| Filters | **Bandpass filter matched to the LED** (e.g. 630 nm red LED + 630 nm bandpass), 20-40 nm wide | Removes plant light and day-night drift; makes a short strobe the only light. Midwest Optical, Edmund, Schneider/B+W sell them [not opened] |
| Polariser | Linear polariser on the lens at 90 degrees to a polarised light | Cross-polarisation removes specular glare from film; **darkens holographic foil** (R13 critics) so test per product |

---

## 5. Cameras

### 5.1 What to move to

| Option | Sensor, shutter, pixel | Resolution, speed, interface | Good for | Not for | Link |
| --- | --- | --- | --- | --- | --- |
| Hikrobot MV-CH120-10GM / -GC (opened) | Sony IMX304 1.1", global, 3.45 um | 4096 x 3000, 9.4 fps, GigE, C-mount | **Best fit for 225 ppm cases up to about 400 mm FOV**: 3.45 um is forgiving, 9.4 fps is 2.5 x headroom, fits GigE | 600 mm FOV with 0.3 mm modules (2.0 px) | [MaxxVision page](https://www.maxxvision.com/produkte/kameras/gige-vision-kameras/281/mv-ch120-10gm?c=22) |
| Hikrobot MV-CH250-90GM / -GC (opened) | Gpixel GMAX0505 1.1", global, 2.5 um | 5120 x 5120, 4.5 fps, GigE, C-mount. Listed as discontinued at that reseller | Stopped or slow cases, 450 mm FOV; very high pixel count | 225 ppm (4.5 fps is 1.2 x the part rate; 118 MB/s saturates GigE); wide cases (5120 across is fewer than 5472) | [MaxxVision page](https://www.maxxvision.com/produkte/kameras/gige-vision-kameras/2725/mv-ch250-90gm-v4) |
| Hikrobot MV-CH120-10TM (10GigE) [S] | 12 MP, 10GigE | not opened | Same as above with Mono12 and 2 cameras on one NIC | | [MaxxVision](https://www.maxxvision.com/produkte/kameras/10gige-vision-kameras/2556/mv-ch120-10tm) |
| Hikrobot MV-CS050-10GC [S] | Sony IMX264 2/3", global (colour) | 5 MP, GigE | Small FOV (cap, label) cheap global-shutter | Large cases | [Syscom listing](https://syscom.mx/products/236573) |
| Hikrobot MV-CS200 (today) | IMX183 1" rolling | 5472 x 3648, 19.2 fps, USB3 | Stopped cases, large FOV, cheap | Moving line without short flash and shroud | requirements section 8 |
| Sony Pregius S 4th gen (Basler ace 2 / boost, Allied Vision Alvium, Teledyne Genie Nano, Emergent, XIMEA) | Back-illuminated, global, **2.74 um**: IMX531/541 20 MP 4504 x 4504, IMX535 12 MP 4096 x 3000, IMX547 5 MP 2448 x 2048 | Fast, in GigE, 5GigE, 10GigE, USB3 versions | When the owner wants one camera brand with PTP, sequencer and a strong SDK. Higher sensitivity than IMX304 per pixel area | Price: 1.5-2 x Hikrobot [S, not opened] | [Basler on Pregius S](https://www.baslerweb.com/en/sales-support/knowledge-base/sony-pregius-s-sensors/) [S], [Framos IMX531](https://framos.com/products/sensors/area-sensors/imx531aaqj-es-24003/) [S] |
| Polarisation sensor camera: Sony IMX250MZR (5.07 MP, 2464 x 2056, 3.45 um, global; 4 polarisers 0/45/90/135 degrees on each 2x2 block), IMX253MZR (12.37 MP) | Polarisation (opened: Emergent) | 5 and 12 MP, vendors: Emergent, Allied Vision, Lucid, FLIR, IDS, Basler [S] | Glare on film, foil and glossy cartons **with one exposure and no polariser on the light**; the SDK gives degree of polarisation. Each output angle has a quarter of the pixels | Hikrobot polarisation model not found in this study | [Emergent on Polarsens](https://emergentvisiontec.com/polarization-image-sensor-technology-sony-polarsens-how-does-it-work/), [Framos IMX250MZR](https://framos.com/en/produkte/imx250mzr-c-20334) [S] |
| Line-scan (Hikrobot MV-CL series [not opened], Teledyne Dalsa, Basler racer) | Line sensor, 4k-16k pixels | Image built from encoder pulses | **Cylinders and cartons passing at speed** (one rotation unwrapped, no curvature): our `cylinder-unwrap` could use this for cans; cartons on a conveyor with very high resolution along a wide belt | Stopped parts; needs an encoder, strong line light | [Hikrobot site](https://www.hikrobotics.com/en/machinevision) [not opened] |

Frame-rate maths for the 225 ppm line: two cameras, one part each at a time, are each at
3.75 fps unless each camera takes every second part (then 1.9 fps). The camera frame rate is not
the limit; the **exposure, the trigger and the PC time per frame** are.

### 5.2 Polarisation sensor, honest verdict

A polarisation sensor removes glare and exposes surface orientation in a single frame at the
cost of three quarters of the angle samples. For shrink-wrapped excise cases (film over a paper
label), compute: image I0/I45/I90/I135 -> diffuse component = 2 x min over angles (the common
approach, [not opened]). This is the cleanest answer to the 114 LOW_CONTRAST regions if film glare
is the cause. It is **not** certain it is the cause (R13: hologram foil, geometry); first test
with a cheap linear polariser sheet on the light and on the lens (cost: a few hundred rupees).
If that helps, buy the polarisation camera or fit a proper polarised ring.

### 5.3 What we need from the camera driver (L0 software, for the purchase to work)

| Need | Why | State |
| --- | --- | --- |
| Hardware trigger on Line0 with debounce, strobe out on Line1 with pre-delay and pulse length | Microsecond jitter, microsecond exposure | Have (`HikCamera.cs`), planned to be removed |
| Scheduled action commands with PTP | Two cameras fire together, no wiring | Missing; planned (Tool 11) |
| Sequencer (several exposures per trigger, different gain / exposure / light) | Dark-field then bright-field, bracket faint and saturated | Missing; INDUSTRY-METHODS "more acquisition methods" |
| Mono10 / Mono12, packed or unpacked | Faint labels (R13) | Missing in the pipeline (engine takes 8 bit) |
| Camera-side flat-field, gain, black level, ROI, binning | Faster frames, less host work | Partial |
| Chunk data: frame ID, timestamp, exposure | Lost-frame detection already exists (`FramesMissed`) | Have (frame numbers) |

---

## 6. Lighting per problem

### 6.1 Technique chooser

| Problem | Technique | How it works | Setup | Typical product |
| --- | --- | --- | --- | --- |
| Printed code on matt carton, flat | Bar or ring, 45 degrees, diffused | Light scatters from paper, ink absorbs | Light 30-45 degrees from the axis, two sides, diffuser | Any |
| **Foil, hologram, glossy label, shrink wrap (specular)** | **Dome (diffuse) or coaxial on-axis diffuse light** | Light arrives from all angles (dome) or along the lens axis through a beam splitter (coaxial) and returns to the lens only from flat specular areas; a flat code area shows evenly | Dome ID at least 1.5 x object (Edmund note, [403 on open; from snippet, S](https://www.edmundoptics.com/knowledge-center/application-notes/illumination/brightfield-illumination-with-dome-lights-for-machine-vision/)); coaxial for small flat spots | Effilux EFFI-SDOME / EFFI-FD / EFFI-CAS [S], CCS HPD / LFXV, AI Dome DL-series |
| **Film glare over paper (shrink wrap)** | **Cross-polarisation** (polariser on light, analyser on lens at 90 degrees) | Specular reflection keeps polarisation and is blocked; diffuse reflection from paper is partly depolarised and passes | A polarised ring or bar plus a lens polariser; 2-3 stops of light lost, so strobe overdrive | Effilux EFFI-Ring with polariser (opened), CCS, SVL |
| **Dark cap, laser / dot-peen / embossed mark (DPM)** | **Low-angle dark-field ring (10-30 degrees to the surface)**, close to the part; sometimes combined with a dome | Flat surface reflects light away from the lens; marks and dots scatter it up: bright mark on dark ground | Light height 5-30 mm, ring ID just over the cap; dome plus dark-field ring "hybrid" for mixed surfaces | Effilux EFFI-Ring with adjustable angle 10-90 degrees (opened); AI dark-field rings; DPM scanner designs in patent literature (US10216969 [S]) |
| Embossed text, dents, scratches | **Photometric stereo** (3-6 lights from different sides, one image each, combine) | Surface slope from shading in each direction | Sequencer plus 4-direction light with channels | Effilux EFFI-SRing (4 channels, opened); Keyence LumiTrax (cited in INDUSTRY-METHODS); HALCON photometric_stereo |
| Part outline, hole, measurement | **Telecentric backlight** | Collimated light, silhouette | Light larger than the part | CCS, AI backlights |
| Ink that is faint in visible light (CIJ on dark, UV ink, IR ink) | Wavelength choice | Ink is more absorbing or fluorescent at one wavelength | Red 630 nm on blue or green print; IR 850/940 nm to hide colour print; UV 365 nm for fluorescent ink | Any narrowband LED + bandpass |
| Coloured print on coloured ground | LED colour that the ground reflects and the ink absorbs; or complement | Contrast control | Test red / green / blue (3 shots) | Any |
| Stray light, day-night drift | Shroud plus bandpass | Only our light reaches the sensor | Cover the station | |

### 6.2 Strobe, overdrive and controllers

| Item | What | Notes |
| --- | --- | --- |
| Strobe / overdrive | LED driven at several times its continuous current for 10-500 us | Needed for 100 us exposures. Duty-cycle limited (check MaxOnUs, R14). Vendors: Effilux AutoStrobe / up to 700 % in overdrive (vendor claim, opened); Smart Vision Lights OverDrive "JWL150-DO" and Multi-Drive (opened) |
| Controller | Light controller with channels, trigger in, per-channel delay, pulse length | Smart Vision Lights "LLM LED Light Manager" (named on the page, opened); CCS PD3 series [not opened]; Effilux flash controllers; Ethernet light controllers fit the requirement "Lighting is switched by the PLC or an Ethernet light controller" |
| Multi-light sequence | Several channels fire in order per trigger, one image each (sequencer) | The PLC handshake already has steps for "bright-field then dark-field" (requirements section 8). Hardware timing is better than PLC timing |
| Light on the PLC (today) | Y1 enables the light for the whole inspection | Fine for continuous lights, wrong for 100 us pulses |
| Safety | LED is IEC 62471 rated; IR / UV need shields for eyes [not opened] | |

### 6.3 Vendors

| Vendor | Strong in | Link |
| --- | --- | --- |
| CCS (Japan) | Full range, HPD / dome, coaxial, backlight, low-angle, controllers (PD3 / PDV) | [ccs-grp.com](https://www.ccs-grp.com) [not opened; product page 404 on the URL tried] |
| Smart Vision Lights (USA) | Linear bars, ring, dome, **OverDrive and Multi-Drive**, integrated controllers | [smartvisionlights.com](https://smartvisionlights.com/) (opened) |
| Advanced Illumination (USA) | Domes (DL series), **dark-field ring lights** (sold by Machine Vision Direct), coaxial, backlight | [advancedillumination.com](https://www.advancedillumination.com) [not opened]; [dark-field rings](https://machinevisiondirect.com/collections/advanced-illumination-dark-field-ring-lights-1) [S: 429 on open] |
| Effilux (France) | EFFI-Ring (angle 10-90, optional polariser, AutoStrobe, IP65), EFFI-SRing (4 channels), EFFI-Flex bars, EFFI-FD flat dome, EFFI-CAS coaxial | [1stVision page](https://1stvision.com/lighting/Effilux-ring-lights) (opened); [EFFI-Flex datasheet](https://www.alliancevision.com/files/eclairages/Effilux/Effilux_EFFI-Flex.pdf) [S] |
| Moritex (Schott) | Lenses and some lights | [Empire Optics catalogue](https://empireoptics.com/collections/machine-vision/series_ml-m-ur) [S] |
| Keyence | LumiTrax (photometric stereo, multi-light) | [LumiTrax](https://www.keyence.com/landing/vision/lumitrax-lighting.jsp) (cited in INDUSTRY-METHODS) |

Indian availability and price: not found; ask vendor distributors in India (many resell Hikrobot and
CCS). Indicative (estimate): ring / bar ₹25-70k ($300-800); dome ₹60k-1.5 lakh ($700-1,800);
overdrive controller ₹15-40k ($180-480); telecentric backlight ₹40k-1.2 lakh.

---

## 7. Triggering and synchronisation

| Layer of the problem | Today | For 225 ppm | Notes |
| --- | --- | --- | --- |
| Photoeye | Delta inputs X2 / X24 / X25 (24 V PNP) | **Fibre-optic or laser photoeye, response 50-250 us**, wired to camera Line0 *and* PLC | A standard photoelectric sensor (0.5-1 ms response) is fine too; the PLC scan is the problem |
| PLC latency | DVP12SE scan (a few ms) plus Modbus TCP and Windows: 5-20 ms jitter, not measured (requirements Phase 1 says "measured") | Measure with a scope on trigger in vs light out; do not trust the figure | Trigger jitter 10 ms at 375 mm/s = 3.75 mm |
| Encoder | A/B on X0/X1 into C251 for tracking | Keep. For line-scan, encoder pulses go to the camera | Position, not time, is the right trigger for varying speed |
| Camera trigger | Software over Ethernet | **Hardware Line0, with debounce 0-20 us; strobe Line1** (owner decision 1, chapter 06) | `HikCamera.cs` has it. Each frame must match the PLC's trigger ID; a missing, extra or mismatched frame is NG |
| Two cameras at one place | Software, in turn | **GigE Vision 2.0 scheduled action command with PTP (IEEE 1588)**: one broadcast, each camera fires at the same timestamp; Basler describes sub-microsecond to microsecond precision (vendor claim, [opened](https://docs.baslerweb.com/action-commands) for unscheduled; scheduled [S](https://www.baslerweb.com/en/vision-campus/interfaces-and-standards/multi-camera-systems-with-gige-2)) | Needs PTP-capable cameras and NIC / switch; Hikrobot support is [not opened], ask |
| Multi-exposure | None | Sequencer: 2-4 exposures per trigger with different exposure, gain, light channel; first frame has the budget minus about 15 ms per extra frame (R14) | Use on stopped cases first; on moving parts each extra frame moves the part: 0.1 ms between frames at 375 mm/s = 0.04 mm (fine for 100 us-spaced strobes, not for ms-spaced) |
| Reject timing | PLC tracks by encoder count | Keep | Not a camera problem |

Recommendation: keep the "no camera I/O" rule for stopped and slow cases and for jobs where the
PLC does everything; **allow Line0 / Line1 for the 225 ppm job** as an explicit option, subject to
owner decision 1 in [chapter 06](06-ACCURACY-AND-TIMELINE.md). Line-safety rule for that option:
each hardware-triggered frame must match the PLC's trigger ID (the camera's frame counter or an
encoder stamp checked against the PLC's trigger count); a missing, extra or mismatched frame is NG,
and the PLC result time-out (`ResultTimeout`, [PLC-INTERFACE.md](../PLC-INTERFACE.md)) still
applies. The PLC still decides OK / NG and the reject.

---

## 8. Line PC

### 8.1 Where we are

Reference PC: fanless i7, DDR3, 16 GB, 128 GB SSD, no GPU. DDR3 means an i7 of about 4th-6th
generation [inference, not stated in the repo]. The engine's measured times are on an M3 with
8 cores (24 QR: 221 ms median for 250-300 ms target; 45 QR: 59-403 ms; 48 DM up to 745 ms against
500 ms). A 4-core old i7 may be 2-3 times slower **[estimate]**. **Measure first** by running
`tools/bench/batch_check.py` on that PC (needs the sample folder and a build there).

### 8.2 Tiers of requirements section 11, with our tools

| Tier | Spec (requirements) | What runs well | Our tools it covers | Buy now? |
| --- | --- | --- | --- | --- |
| T1 Entry | i5 / Ryzen 5, 6+ cores, 16 GB, no GPU, 1 camera to 5 MP, 50-200 ms | Code reading on small FOV, simple OCR | 12-code case, 1D, small OCR | Only for stations that are already met |
| **T2 Standard** | **i7 / Ryzen 7, 8+ cores, 32 GB, RTX 4060 / A2000 8 GB**, 1-2 cameras, 20-80 ms | Classical tools, our readers on 20 MP, OCR CNN, anomaly / classification | **All current jobs, 24-96 code cases at 250-600 ms, OCR, OCV, the two-camera line** | **Yes: the default line PC** |
| T3 Performance | i9 / Ryzen 9, 64 GB, RTX 4070-4080 / A4000, 2-4 cameras, 10-40 ms | Detection / segmentation, 2-4 cameras at speed | 225 ppm with two cameras plus AI defects | If T2 fails the 100 ms target |
| T4 Training | i9 / Threadripper, RTX 4090 / A5000 24 GB, 64-128 GB | Training | OCR model, anomaly, classifier training (not on the line) | Office, one machine |
| T5 Edge | Jetson Orin NX / AGX, 16-32 GB | Fanless, 1-2 cameras, 30-150 ms | Compact stations with ONNX / TensorRT | Later, needs an ARM / Linux build of the engine |

### 8.3 What to specify

| Part | Specification | Reason | Our tool needing it |
| --- | --- | --- | --- |
| CPU | 8-16 cores (e.g. Core i7-13700 / 14700 class or Ryzen 7 / 9), base clock above 3 GHz, a CPU with **AVX2 (and AVX-512 + VNNI, or AVX-VNNI) for INT8 ONNX** | Reader runs parallel decode attempts per code; OpenCV DNN CPU INT8 uses VNNI. Note: Intel desktop 12th-14th gen has AVX-VNNI but no AVX-512; Ryzen 7000 has AVX-512 with VNNI; Xeon W has AVX-512 [from general knowledge, not opened: verify on the CPU's ARK / datasheet] | Code Reader (96 codes in 600 ms), OCR, anomaly, INT8 models (`ml/`) |
| RAM | 32 GB, dual channel DDR5 | 20-25 MP frames are 20-30 MB each; buffers (FrameIntake keeps 3), image history and AI | Guard buffers |
| GPU | **None** until an AI tool misses its time on CPU; then RTX 4060 8 GB (T2), RTX 4070 (T3). Needs a CUDA build of OpenCV DNN or ONNX Runtime CUDA / TensorRT, which our engine does **not** have today (OpenCV DNN CPU) | Fast OCR CNN, anomaly, segmentation | AI tools; ask the engine owner before buying |
| NIC | **One Intel NIC port per GigE camera** (Intel i210 / i350 or PoE card), jumbo frames 9014, interrupt moderation tuned, no switch sharing for two 20 MP cameras; 5/10GigE if 25 MP | Frames of 20 MB at 3.75 fps per camera: 75 MB/s per camera: two cameras need two ports or a 5GigE port | Camera driver |
| USB3 camera | Own USB3 controller per camera, cable under 3-5 m or active | Requirements section 11 | |
| Storage | **NVMe SSD 512 GB-1 TB industrial (wide temperature, power-loss protection)**, plus a second disk or network share for audit | NG images up to 5 GB cap in the guard; logs; job versions | L6 audit |
| OS | Windows 10/11 IoT Enterprise LTSC | Requirements | |
| Form | Fanless with GPU is hard; use a **box PC with filtered fan** or Neousys / Advantech GPU-capable PCs: [Neousys](https://www.neousys-tech.com) [not opened], [Advantech](https://www.advantech.com) [not opened], Axiomtek, ADLINK | Plant dust, heat; current panel PC has a front panel for the operator | |
| UPS | Online UPS 1-2 kVA, runtime 10 min, with USB / relay shutdown signal to Windows | Power cuts; avoids database corruption of counts and the 10 000 child / 1 000 case duplicate memory | L6 |
| Time sync | NTP on the PC; PTP master on the NIC if PTP cameras | Audit timestamps | L6 |

Indicative cost (estimates): T2 box PC with CPU, 32 GB, NVMe: ₹1.5-3 lakh ($1,800-3,600); with RTX
4060: ₹1.8-3.5 lakh. T3: ₹3.5-6 lakh. Panel PC for the operator unchanged. UPS ₹25-60k.

---

## 9. Tables per station

Priority: P1 = do now (cheap or biggest effect), P2 = next, P3 = only if a test shows the need.
"Effect" = expected change to the miss reasons in section 3; **all are hypotheses until the test in
section 12 passes**. Costs are estimates per station.

### 9.1 Aggregation case top, printed QR / DM, 12-96 codes

| Problem | Hardware change | Expected effect | Cost (est.) | Priority |
| --- | --- | --- | --- | --- |
| Soft corners, blur (24 QR: BLUR 18) | 1"-class lens rated for the pixel, at f/4-5.6 (not f/8); focus plane set at code height (curved cans: cap height) | BLUR down, corner NO_FINDER down | ₹30-80k | P1 |
| Fixed exposure 2000 us on a moving case | Strobe light plus controller; exposure at or under the section 4.4 limit | BLUR on moving cases gone; allows lower gain (noise) | ₹40-90k (light + controller) | P1 |
| 48 DM LOW_CONTRAST 39, COVERED 26 | Dome or diffuse light (the caps' dark DM) and low-angle dark-field; see 9.4 | LOW_CONTRAST down | see 9.4 | P1 |
| Module under 3 px on 600 mm case (TOO_SMALL) | 3:2 sensor above 20 MP, **or two cameras with 10 % overlap**, or crop to the code area | TOO_SMALL down (19 regions in shrink-wrap batch) | ₹2-4 lakh per camera | P2 |
| Frame rate / bandwidth with larger sensor | 5GigE or 10GigE NIC and switch, or a second GigE port | No drops | ₹15-60k | P2 |
| Trigger jitter | Hardware trigger (Line0) | Fixed FOV margin, no blur from jitter | cabling only | P2 |

### 9.2 Shrink-wrapped excise-label cases (LOW_CONTRAST 114, TOO_SMALL 19)

| Problem | Hardware change | Expected effect | Cost | Priority |
| --- | --- | --- | --- | --- |
| Film flattens ink, reflection | **Cross-polarisation** test with polariser sheets first | LOW_CONTRAST down (if film glare is the cause); GLARE down | ₹500 for sheets, ₹30-60k for a polarised ring | P1 (test) |
| Specular highlights from folds | **Dome** (diffuse) above the case; coaxial over the label spot | Highlights spread, contrast even | ₹60k-1.5 lakh | P1 |
| Hologram foil under film | **No cross-polarisation** (it darkens foil); use dome or coaxial | Avoid wrong purchase | none | P1 |
| Faint 5-label product (exposure) | 4x exposure + Mono12 (R13/R14 before any purchase) | LOW_CONTRAST down at no cost | none | P1 |
| 2.5 px modules (excise labels from the old 8.8 MP camera) | **Re-shoot on the MV-CS200 (R0)**: pixel count alone takes 1.9 to about 2.5 px per module (RESEARCH-NEXT-GENERATION) | TOO_SMALL down | 1 hour | P1 |
| Wide polarisation need, glare varies by case | Polarisation-sensor camera (IMX250MZR) | GLARE and LOW_CONTRAST down without a polariser on the light; test needs the camera | ₹1.2-2.5 lakh | P3 |

### 9.3 Caps and cartons with laser / CIJ / TTO / embossed text (OCR / OCV)

Measured: laser 92.6 % chars (synthetic; real 100 % on 389 lines), TTO 98.0 %, CIJ 72.9 %,
embossed 89.4 %, OCV true laser passes 69 % (false rejects).

| Problem | Hardware change | Expected effect | Cost | Priority |
| --- | --- | --- | --- | --- |
| CIJ 72.9 %: dots, gaps, thin strokes | **Pixel at most stroke / 3; 3-4 px per dot gap**: tighter FOV (second camera for the text only), cross-polarised bar light, **red / green bandpass matched to ink contrast**, short strobe | More characters read; false rejects down | ₹1.5-3 lakh (camera + lens) | P1 |
| Embossed 89.4 % | **Low-angle dark-field** from one side, or 4-direction photometric stereo (sequencer) | Shape from shading; characters separate from the carton | ₹50k-1.5 lakh | P2 |
| Laser on dark cap | Dark-field ring close to cap, plus bright-field dome in a second sequencer step | Contrast up | see 9.4 | P1 |
| TTO ribbon (98.0 %) | Bar light with polariser (ribbon is shiny) | Residual errors | ₹30k | P3 |
| OCV false rejects 69 % | Light and focus repeatable, **fixed lens and light so the teach holds**; telecentric not needed | Fewer false rejects (needs same image conditions) | included | P1 |
| Text on curved caps | Lower f-number, curvature unwrap (software), short working distance | | | P2 |

### 9.4 Dark Data Matrix caps (48 DM: LOW_CONTRAST 39, COVERED 26, NO_FINDER 16)

| Problem | Hardware change | Expected effect | Cost | Priority |
| --- | --- | --- | --- | --- |
| Mark is nearly the same grey as a dark cap | **Low-angle dark-field ring** with adjustable angle (Effilux EFFI-Ring 10-90 degrees, opened) at 5-30 mm above the caps, strobe overdrive | The dots / modules scatter light, flat cap stays dark: contrast switches from lost to bright dots | ₹50-90k | P1 |
| Domed caps give a bright centre in a dome light | Dark-field ring, **not** on-axis | Avoids specular spot | | P1 |
| Mixed surfaces | Hybrid dome + dark-field (sequencer: 2 shots, host picks the better per code) | LOW_CONTRAST further down | ₹1-2 lakh | P2 |
| Modules 2.5 px or less | Section 4.1: DPM design minimum 3 px, target 4-5; a 12 MP/3.45 um camera over the cap row only | TOO_SMALL down | ₹1.2-2.5 lakh | P2 |
| 96 caps on a case | The top camera sees all; add a **second camera at 45 degrees** only if COVERED stays | COVERED down (a physical angle problem) | ₹2-4 lakh | P3 |

### 9.5 Two-camera line, 225 parts per minute

| Problem | Hardware change | Expected effect | Cost | Priority |
| --- | --- | --- | --- | --- |
| Rolling shutter skew and blur | **Global-shutter camera** (IMX304 / IMX250 / Pregius S), strobe at or under 130 us (section 4.4) | BLUR gone; geometry exact | ₹1.2-4 lakh x 2 | P1 |
| Trigger jitter, camera synchronisation | Hardware trigger, **scheduled action commands + PTP**, one light per camera on its own strobe channel (no crosstalk) | Fixed FOV margins, both cameras on the same part | switches and PTP NIC ₹20-60k | P1 |
| Time per frame | Line PC at least T2; camera ROI to the part area | Time met at 225 ppm (budget 266 ms per part, two cameras in parallel) | PC ₹1.5-3 lakh | P1 |
| Bandwidth | One NIC port per camera, jumbo frames | No loss of frames | ₹10-30k | P1 |
| Reject tracking | Encoder on the conveyor shaft, valve with fast response | Not an accuracy issue of the camera | existing | n/a |
| Missed frame | Existing guard counts it as NG | Line safety already holds | none | have |

### 9.6 1D labels (SSCC, GS1-128, EAN, ITF-14, DataBar)

| Problem | Hardware change | Expected effect | Cost | Priority |
| --- | --- | --- | --- | --- |
| Failures under 1.5 px per bar / tilted | X-dimension at least 2 px (longer focal or closer), light across the bars | Fewer NO_FINDER | lens ₹30-60k | P2 |
| Glossy label | Cross-polarised bar light | LOW_CONTRAST down | ₹30-60k | P2 |
| Corrugated ITF-14 | Larger FOV is fine (X >= 0.495 mm) | | none | n/a |
| No real 1D images yet | **Collect first** (research notes: only synthetic) | | none | P1 (data) |

### 9.7 Measurement and cylinders (future tools)

| Problem | Hardware change | Expected effect | Cost | Priority |
| --- | --- | --- | --- | --- |
| 0.1 px, 3 sigma gauge | Bi-telecentric lens, collimated backlight, a camera with 3.45 um pixels, a stable mount, temperature | Hits requirements 13 | ₹2-5 lakh per station | P3 until a product needs it |
| Full 360 deg label on a can | Line-scan camera with a rotating roller, or 3 area cameras at 120 degrees | Whole label, no curvature | ₹3-8 lakh | P3 |

---

## 10. Interactions and traps

1. **More pixels with the same lens can be worse.** A lens rated for 2/3" / 5 MP on a 20 MP 1" sensor
   limits detail and darkens corners; the owner already sees soft corners.
2. **f/8 for depth of field costs resolution** (section 4.5). Prefer a flatter focus plane and a
   focus calibration to a big f-number.
3. **A strobe without a short exposure and a bandpass is useless in a lit plant**: ambient light
   dominates the exposure.
4. **A polariser costs 2-3 stops**; the light must be brighter and the strobe in overdrive.
5. **Mono12 saves nothing if the host takes 8 bit.** The engine takes 8 bit today (Missing).
6. **Hardware changes invalidate teach**: the OCV and calibration data are tied to the camera,
   lens setting and sensor ROI (`TOOL-SPECIFICATIONS.md:515`). Re-teach after each change.
7. **Software trigger, no camera I/O, is a stated owner design rule.** The recommendation to allow
   Line0/Line1 for 225 ppm contradicts `ARCHITECTURE.md:529` for that job; it needs the owner's
   decision (decision 1 in chapter 06). If allowed, each frame must match the PLC's trigger ID
   (frame counter or encoder stamp against the PLC count); a missing, extra or mismatched frame is
   NG, and the PLC result time-out still applies.
8. **Line safety**: a new light, filter or lens should only add reads; it is used only after the
   section 12 test shows 0 wrong reads. A lamp failure or dirt makes the image dark: keep the existing "dark image is NG" guard and add a
   light-health check (brightness drift alarm, `PLC-INTERFACE.md` warning 600).

---

## 11. Purchase list, ordered by accuracy gain per rupee

| # | Purchase | Cost (est.) | Misses it answers | Gain | Proof (section 12) |
| --- | --- | --- | --- | --- | --- |
| 0 | **Measurements, no purchase:** ruler on module size and case width; record lens make, f, focal length, exposure; R0 re-shoot; R13 f/4 vs f/8, polariser sheet, Mono12; R14 4x exposure | ₹0-1,000 | LOW_CONTRAST, TOO_SMALL | Possibly most of (a), holograms and faint labels (R0) | Re-run batch on new images |
| 1 | **Shroud + bandpass filter + polariser sheets** | ₹5-20k | LOW_CONTRAST, GLARE | High per rupee | Same-case before/after |
| 2 | **Strobe controller + overdrive light** (bar or ring) on the moving stations | ₹40-90k per station | BLUR | High for moving cases | Blur metric, batch |
| 3 | **Dark-field ring** for dark DM caps | ₹50-90k | LOW_CONTRAST 39, COVERED partly | High on 48 DM | `batch_check` 48 DM row |
| 4 | **Dome or coaxial** for shrink-wrap / foil | ₹60k-1.5 lakh | LOW_CONTRAST 114 | High on "other QR" (129 / 214 = 60.3 % today, baseline) | `batch_check` other QR row |
| 5 | **Line PC T2** (8+ cores, 32 GB, NVMe, Intel NICs; GPU later) | ₹1.5-3 lakh | Time (48 DM over 500 ms), headroom for everything | Needed anyway: time targets fail on old PC | `batch_check` time columns on that PC |
| 6 | **Lens at f/4-5.6, 1" class, per product** | ₹30-80k per lens | BLUR, NO_FINDER corners | Medium-high | `batch_check` 24 QR BLUR |
| 7 | **Global-shutter camera** (IMX304 12 MP, or Pregius S) for the 225 ppm line, x 2 | ₹1.2-4 lakh each | Moving-part BLUR and geometry | High for 225 ppm | Line trial, miss rate per 1,000 parts |
| 8 | **Hardware trigger + PTP / scheduled actions, light channels per camera** | ₹20-60k + software | Jitter, camera sync | Medium | Scope + bench of two-camera job |
| 9 | **Second camera or 3:2 sensor above 20 MP** for 600 mm cases | ₹2-4 lakh | TOO_SMALL, 96-code cases | Medium (only if px/module < 3 after 1-8) | `batch_check` 96 row |
| 10 | **Photometric-stereo set** (4-channel ring + sequencer) for embossed text | ₹80k-2 lakh | Embossed 89.4 %, OCR | Medium | `ocr_bench` embossed |
| 11 | **Polarisation-sensor camera** | ₹1.2-2.5 lakh | GLARE on film | Only if cross-polariser sheets help and a single shot is needed | `batch_check` other QR |
| 12 | **GPU (RTX 4060)** | ₹30-40k | Time of AI OCR / anomaly | Only if CPU misses | `ocr_bench` time |
| 13 | **Telecentric lens + backlight** for gauging | ₹2-5 lakh | Measurement accuracy | Only when gauge tools ship | Gauge R&R |
| 14 | **UPS** | ₹25-60k | Reliability, not accuracy | Always | Power-cut test |
| 15 | **Line-scan for cans** | ₹3-8 lakh | Curved cans | Only if unwrap software fails | Can batch |

Sum of items 1-6 (light, PC, lens): about ₹3.5-8 lakh ($4,200-9,500) per station [estimate], before
any new camera. Cameras (7, 9) are the large items and are bought **last** unless TOO_SMALL
dominates.

---

## 12. Tests that prove each purchase

Rules (AGENTS.md rule 10, 11): test once, scoped; Exit 2 of `batch_check.py` means not judged (busy
machine); never refresh the baseline on a busy Mac; a pull request that reads more codes runs
`--update` and commits `truth/batch-baseline.csv` with old and new numbers.

### 12.1 The protocol for any hardware change

1. **Before**: on the old setup, capture a fixed set per product: 20 cases x (stopped, slow, line
   speed), each also saved as a 3-frame burst; same codes, same lens, same ruler photo. Store in
   `VS_IMAGES` (customer data, outside git).
2. **After**: the same cases, new part, same positions. Do not re-teach in between unless the
   change requires it; record the re-teach.
3. Run the engine on both sets with the same build:
   `python3 tools/bench/batch_check.py --samples <folder>` (reads every batch, compares with
   `truth/batch-baseline.csv`, runs a noisy batch twice). Check the miss-reason table, not just the
   count.
4. For text: `ocr_bench <folder> truth.tsv --model ocrnet.onnx --charset ocr_charset.txt
   --read-only --csv out.csv`, then again with `--mutate` (a one-character change must fail):
   character accuracy, wrong reads at or above `--min-quality`, and OCV false-reject rate.
5. Add `truth/samples.csv` rows for the new images before the run; zero wrong reads is the gate
   (line safety: a wrong code is worse than a missed one).

### 12.2 Per purchase

| # | Purchase | Test | Pass rule |
| --- | --- | --- | --- |
| 0 | Measurements, R0/R13/R14 | Re-shoot, batch on new images | Document px/module and which fix wins |
| 1 | Shroud, bandpass, polariser | 20 shrink-wrap cases with and without; LOW_CONTRAST count from MissInfo | LOW_CONTRAST regions down by half, no new wrong read |
| 2 | Strobe | 20 moving cases, exposure from 2000 us to under the 4.4 limit; compute edge width in px of a code at line speed | Blur under 0.3 px; BLUR count down; reads up |
| 3 | Dark-field ring | The 48 DM caps image set (`Image_20220312160710251.bmp` reads 19 / 47 today) | 48 DM row up from 64.2 %, time under 500 ms |
| 4 | Dome / coaxial | The 9 "other QR" images (129 / 214 = 60.3 % today, `truth/batch-baseline.csv`) | Visible codes read at or above 90 %, 0 wrong |
| 5 | Line PC | `batch_check.py` on the line PC (and `bench --runs 3`) | Every time target met with margin of 20 %; CPU below the guard threshold |
| 6 | Lens | 24 QR batch, BLUR and NO_FINDER, centre and corners | BLUR count down; corners read like centre |
| 7 | Global-shutter camera | 1,000-part line trial at 225 ppm | Zero wrong reads, NO READ rate per 1,000 parts below target; zero missed frames (`FramesMissed`) |
| 8 | Hardware trigger / PTP | Scope: trigger in -> light out -> exposure; both cameras' timestamps | Jitter under 0.2 ms; camera offset under 5 us |
| 9 | Second camera / sensor | 96-code batch | TOO_SMALL regions 0 |
| 10 | Photometric stereo | `ocr_bench` embossed set with `--read-only` | Character accuracy up from 89.4 %, OCR target of 99.5 % |
| 11 | Polarisation camera | Same shrink-wrap set | GLARE and LOW_CONTRAST below the polariser-sheet result |
| 12 | GPU | `ocr_bench --runs 3` time | Time within the station budget |
| 14 | UPS | Cut mains for 30 s | Clean shutdown, counts intact |

### 12.3 Gaps in our test tools (software work this chapter asks for)

| Gap | Why | Effort |
| --- | --- | --- |
| Capture a "before / after" set with a fixed protocol and store the exposure, gain, light, lens and camera serial in the file name or sidecar | Today only BMP files, lens unknown | 1 pw |
| A px/module and blur estimate per image, written to the batch CSV | Proves the cause of each miss | 1 pw |
| Mono12 and 16-bit input to the engine and bench | Faint labels | 2 pw |
| Polarisation-sensor decode (Stokes, degree of polarisation) | Option 11 | 1 pw |
| Sequencer / multi-light job step and host fusion | Options 3, 10 | 3 pw |
| PTP / scheduled action in the camera layer | Option 8 | 2 pw |
| Run `ocr_bench` and `bench` on the line PC and keep the results as the PC's baseline | PC unknown | 0.5 pw |

---

## Not opened / limits

- **Not opened**: Hikrobot's own site and datasheets (only reseller pages were opened); Hikrobot
  MV-CL line-scan, MV-CS global-shutter and polarisation models (none found in this study); CCS and
  Advanced Illumination product pages (404, 429); Basler's Pregius S page (HTTP 406), docs of scheduled
  action commands and PTP (opened only the unscheduled action command page), Edmund Optics dome note
  (403), Moritex catalogue (snippet), Neousys and Advantech, and standards for DPM print-quality
  grading (ISO/IEC 15415, ISO/IEC TR 29158 / AIM DPM-1-2006, GS1 General Specifications for X-dimension).
  Marked [S] or [not opened].
- **Not in the repo**: the lens, light make, conveyor speed, part pitch, module sizes in mm, case sizes in mm;
  the 225 ppm line's geometry. All px-per-module and blur numbers here use the stated examples and
  must be recomputed with a ruler on the line (R13).
- **All prices are estimates** by the author (none published on pages opened); the Indian price of Hikrobot,
  CCS, Effilux and Basler items was not found. Get quotes.
- **CPU facts** (AVX-512 / AVX-VNNI per CPU family) are general knowledge; check the CPU datasheet.
  The "old i7" and "2-3 times slower" statements are inferences from DDR3, not measurements.
- **Diffraction numbers** are for ideal lenses (section 4.5) and green light; real lenses are worse.
  Pixel size of IMX183 (2.4 um) is from the sensor datasheet, not the repo.
- **No test was run** for this chapter; every "expected effect" is a hypothesis for section 12.
