# Study chapter 02: Patent library of methods to adopt

Part of the Vision_System study of 2026-10-30. Branch `claude/tool-study`. Brief: the owner's request of
2026-10-30 ("i discussed with lawyer and we can use all patents and process in our library ...").

## 0. How to read this chapter

**Legal basis.** Owner's statement, 2026-10-30, after advice from his lawyer: "we can use all patents and process in
our library". This study gives no legal opinion and makes no freedom-to-operate claim. The scope
(territories, export, active US / EU / JP patents) is for the owner to confirm; until then the earlier
territory notes in [PATENTS.md](../../PATENTS.md) stay as written.
This chapter is a **library of patented methods to adopt**. Status and expiry are given as Google Patents
shows them; Google itself calls its status an assumption, not a legal conclusion. They are kept as
information. The earlier registers ([PATENTS.md](../../PATENTS.md),
[RESEARCH-COGNEX-PATENTS.md](../../RESEARCH-COGNEX-PATENTS.md), [INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md)
section 16) carry territory switches and "avoid" notes; they stay as written until the owner confirms the
scope. The `-DCR_UNDERRES=OFF` switch and similar territory switches stay in the code.

**Copyright is separate.** A patent describes a method. It does not give us the vendor's code, models,
manuals or screenshots. This chapter copies none of them. Parameters are quoted as numbers only.

**Source tags.**

| Tag | Meaning |
| --- | --- |
| [O] | Google Patents page opened and read in this session (WebFetch). The fetch tool reads only the first 100,000 characters of a page, and many pages are 150,000 to 500,000 characters, so **claim text was often not reached**. Where the row says "claim 1" the claim was seen. Where it says "steps (summary)" the steps come from the abstract and description. |
| [P] | Patent page opened in earlier repo research (cited file named in the row). Facts are carried over, not re-read today. |
| [S] | Seen only in a search-result snippet. Page not opened. Treat as a lead. |

**Dates.** "Priority" is the earliest priority date shown. "Expiry" is Google's anticipated or adjusted
expiry. "IN" says whether an Indian family member is listed on the page. No Indian member was shown on any
page opened, but Google's IN coverage is patchy and InPASS was not searched (see section 22).

**Layers** are L0 Acquire, L1 Correct, L2 Find, L3 Read or measure, L4 Verify and decide, L5 Learn and
tune, L6 Output and audit (study brief). **Our status** uses Have / Partial / Missing with the file or change
note.

**Effort** is person-weeks (pw) for one experienced developer with an AI assistant. These are estimates.

## 1. Count and what is new in this chapter

| Item | Count |
| --- | --- |
| Google Patents pages opened this session [O] | 50 (one fetch each; a few are application or sibling numbers of the same family) |
| Distinct patent and application numbers named in the tables | about 190 (counted by hand, an estimate; family members counted separately) |
| Carried from earlier repo research [P] | the rest of the numbered patents, about 70 |
| Snippet only [S] | about 50, listed in section 21 |

No expired / active tally is given: status is stated per row.

**Findings that change earlier notes.**

1. **US7065262 and US7016539** (Cognex PatMax family) are expired (2018). PatMax's core methods are open.
2. **US8971663** (Cognex synthetic golden template) is active to 2032-11-27 and was opened today: it is the
   multi-layer print inspection method. Covered by the owner's statement of 2026-10-30 (section 0); see
   section 11.
3. **US8345979** (Cognex, deformed pattern, deformation map) is expired (2023) and is the closest published
   method to PatFlex (inference). Google shows it expired; it gives a route to bent labels and wrinkled film.
4. **JP6586201B2** (Keyence, polarised plus unpolarised light sets with a crossed camera polariser) is a
   Japan-only patent, active to 2034. It is the polarisation recipe for shrink-wrap and glossy caps.
5. **US10198648** is Digimarc's (correlation 1D decoding, 53 scales), not Dynamsoft's. No Dynamsoft patent
   was found in two searches.
6. **US12067449** (Zebra, 4D mapping to remove duplicate decodes across cameras) and **US20180157882**
   (Cognex, fragment tracking) are the two conveyor / multi-camera methods closest to our 225 parts per
   minute two-camera line.
7. **US9233400** (Covectra) is the only pharma aggregation patent found. No Systech, Antares Vision, Optel
   or Korber aggregation patent turned up on Google Patents in three searches.
8. **No patent was found** for PatMax RedLine, Cognex OCRMax or OCVMax, the HALCON variation model,
   Domino or Markem-Imaje vision verification, or flat-field correction as such. These tools are built from
   public methods (section 20).

## 2. Acquisition and lighting (L0)

### 2.1 Auto-exposure, gain and light

| Patent | Owner | Title | Priority | Status / expiry | Territories | IN | Tag |
| --- | --- | --- | --- | --- | --- | --- | --- |
| [US9413981](https://patents.google.com/patent/US9413981B2/en), [US10110829](https://patents.google.com/patent/US10110829B2/en) | Cognex | Camera parameters from multi-gain images | 2012-10 | Active, about 2033-09 | US, CN, DE | none seen | [P] RESEARCH-COGNEX-PATENTS |
| [US8146821](https://patents.google.com/patent/US8146821B2/en) | Symbol Technologies | Auto-exposure for multi-imager barcode reader | 2009-04-02 | Active, 2030-04-06 | US, EP, CN, WO | none | [O] |
| [US7604174](https://patents.google.com/patent/US7604174B2/en) | Cognex | Lighting retry from cached last-good setting | 2003-10 | Parent expired 2024 | US, WO, EP, JP, DE | none seen | [P] |
| [US8169478](https://patents.google.com/patent/US8169478) | Cognex | Calibration card: 2D code with start command and target reflectances | 2006 | Expired | US | none | [P] |
| [US9600703](https://patents.google.com/patent/US9600703B2/en) | Cognex | Sorting acquisition settings by points and moving average | 2013-03 | Active, about 2033-03 | US, DE, JP | none seen | [P] |
| [US10896303](https://patents.google.com/patent/US10896303B2/en) | (not opened) | CMOS exposure for code reading without iterative trial | n/a | not checked | n/a | n/a | [S] snippet only |

**US9413981 (multi-gain probe).** Steps: (1) capture one sub-sampled frame about 1 ms long with four
different gains on interleaved 2 x 2 pixel groups (example gains 1.5, 5, 10, 15); (2) find the gain image in
which the feature is most readable; (3) set global exposure and gain from it; (4) only then vary the
illumination. About 4 ms against 40 ms for a full frame.
**Adopt how:** the Hikrobot MV-CS200 cannot gain-interleave pixels, so build the equivalent as a 2 x 2 or
4 x 4 binned ROI probe at three gains in sequence (about 3 to 6 ms), then pick exposure and gain.
Feeds `CalibrationSearch.cs` (Have). Gain: faster set-up after a product change, fewer lost cases.
Effort 1 pw.

**US8146821 (Symbol).** Claim 1 is apparatus: a housing with two or more windows and cameras whose light
sources fire one after another, in step with each camera's exposure, inside one frame slice; light sensors
end the flash when exposure is adequate; exposure time is corrected for window transmissivity read from a
label. Numbers: six cameras at 60 frames per second, 16.66 ms sliced to about 2.77 ms per camera,
exposure under 500 microseconds, window transmission 55 to 90 percent.
**Adopt how:** **time-sliced strobing for the two-camera line** so camera A's light does not wash camera B.
Use PLC or camera hardware strobe outputs with a fixed offset. Layer L0. Status Missing. Effort 0.5 pw
plus a strobe controller if lights are not already camera-triggered.

**US9600703 (points and EMA).** 1000 points per successful read cycle shared among contributing images, a
carried-over seed image gets 600, each setting keeps an exponential moving average with alpha = 2/(50+1),
settings sorted by that average. **Adopt how:** `TuningAgent.cs` already has a bandit; add the points and
EMA as the prior for each product's step order. Status Partial. Effort 0.5 pw.

**US7604174.** Start from the cached last-good lighting, check by a decode or by a contrast test, switch
bright field and dark field or ring quadrants on failure. Dark field arrives at 45 degrees or less.
`CalibrationSearch` tries last-good first (Have). The bright / dark field switch needs a second light
channel (hardware, section 19).

### 2.2 Multi-exposure, HDR and tone mapping

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US10607047](https://patents.google.com/patent/US10607047B2/en), [US11138397](https://patents.google.com/patent/US11138397B2/en) | Cognex | Local tone mapping before decoding | 2017-12 | Active, about 2038-12 | US (CN, EP, JP per one listing) | [P] |
| [US9104932](https://patents.google.com/patent/US9104932B2/en) | Cognex | Stitching frames taken with different settings | 2013-03 | Active, about 2033-08 | US, EP, JP, HK | [P] |
| [US9734376](https://patents.google.com/patent/US9734376B2/en), [US10592715](https://patents.google.com/patent/US10592715B2/en), [EP2210211](https://patents.google.com/patent/EP2210211A1/en), [EP3109792](https://patents.google.com/patent/EP3109792B1/en), JP5414685 | Cognex | Multi-frame cell voting | 2007-11 | US about 2033-03, EP about 2028-11 | US, EP, JP, WO | [P] |
| [US7616256](https://patents.google.com/patent/US7616256B2/en) | (not opened) | Multiple exposure methods for electronic cameras | n/a | not checked | n/a | [S] |
| [US20100157078](https://patents.google.com/patent/US20100157078A1/en) | (not opened) | HDR image combining (short plus long exposure) | n/a | application | n/a | [S] |

**US10607047 steps:** neighbourhood sized from the pixels-per-module range; local contrast = 3 sigma or the
gap between the means of the bottom 20 percent and top 20 percent of the local histogram, clamped; areas
that are not bimodal masked as background; output mapped piecewise-linearly to about [5 to 10, 245 to 250];
retry with another kernel after a failed decode. **Adopt how:** replace the fixed 4 x 4 tile CLAHE in
`CR_STEP_FLAT` with this module-scaled mapping. It attacks LOW_CONTRAST misses (shrink-wrapped QR, faint
laser). L1. Status Partial. Effort 1.5 pw. Expected gain: High on the 60.3 percent shrink-wrap set.

**US9104932 steps:** correlate a synthetic finder-and-timing model against a Determinant-of-Hessian dot
image, pick polarity by the better correlation, call a candidate "promising" above 65 percent fixed-pattern
agreement, register frames taken under different exposure or light. **Adopt how:** make the test burst
(`TestBurst.cs`) a standard bracket of 2 to 3 exposures per trigger, registered by this correlation, then
fused in `multiframe.cpp`. Effort 2 pw.

**US9734376:** see section 6 (code reading). Already built as `multiframe.cpp`.

### 2.3 Polarisation, dark field and DPM lighting

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [JP6586201B2](https://patents.google.com/patent/JP6586201B2/en) | Keyence | Optical information reader: polarised and unpolarised light sets, crossed camera polariser | 2018-06-29 (parent JP2014-157029, 2014-07-31) | Active, 2034-07-31 | Japan only | [O] |
| [US6352204](https://patents.google.com/patent/US6352204) | IDEAS, now Omron (Microscan) | Optical symbol scanner with low-angle illumination | 1999-08-04 | Expired 2019-08-04 | US | [O] |
| [US8011583](https://patents.google.com/patent/US8011583) | Microscan, now Omron | Managing Data Matrix lighting | 2007-07-02 | Active, 2030-04-11 | US | [O] |
| [US7131587](https://patents.google.com/patent/US7131587) | Symbol Technologies | Illuminating and reading codes on reflective surfaces (extended light source) | 2004-03-02 | Expired 2024-03-10 | US, WO, EP, JP | [O] |
| [US10650205](https://patents.google.com/patent/US10650205) | Hand Held Products | Scanning and decoding DPM: bright-field and dark-field modes | 2018-09-28 | Active, 2038-09-28 | US | [O] |
| [US11003875](https://patents.google.com/patent/US11003875B2/en), [US11574137](https://patents.google.com/patent/US11574137B2/en) | Cognex | DPM handheld lighting recipes | 2018-11 | Active, about 2038-11 | US, EP, CN | [P] |
| [WO2016103285](https://patents.google.com/patent/WO2016103285A1/en) | Datalogic | Reading DPM by stereo imaging and a depth map | 2014-12-24 | Ceased | US, WO | [O] |
| [US20120067955](https://patents.google.com/patent/US20120067955) | Datalogic | Different illumination spectra against glints on DPM | n/a | not checked | n/a | [S] |
| [US9262661](https://patents.google.com/patent/US9262661B2/en) | Hand Held Products | Optical readers with polarisation sensing of light from indicia | n/a | not checked | n/a | [S] |

**JP6586201B2, steps (claim 1 paraphrase).** (1) Set a mode: first illuminator on and second off, or the
reverse. (2) Light of the first illuminator passes a first polarising filter; the second emits through an
unfiltered area. (3) The camera sees reflected light through a second polariser turned about 90 degrees
from the first. (4) Decode. Four illumination LEDs; each polariser halves the light so the crossed path
costs about 1/4; brightness tuned over 256 levels with about 27 tested per mode.
**Adopt how:** two light channels per camera, a ring with a cross-polariser film and a second ring without,
plus a polariser on the lens. Tune channel and brightness as one more `CalibrationSearch` arm. Cognex's own
guidance (crossed at 90 degrees, reader tilted 15 degrees) is in RESEARCH-COGNEX-PATENTS.
L0. Status Missing (never tried on the line). Expected gain: High for shrink-wrap and glossy cap film.
Effort 1 pw software plus lights (section 19).

**US6352204.** Claim 1 is apparatus. Light strikes the symbol at **10 to 18 degrees** to the surface while the
optic axis is about normal; light pipe tip about 40 degrees; fixed-scanner crossing angle 60 to 75 degrees
and illumination at the symbol 15 to 30 degrees; test symbol about 3 mm square. This is the number to build a
**low-angle dark-field bar light** for laser-etched and dot-peened Data Matrix.

**US8011583 (Microscan).** Light configurations from a bank of 2 to 120 sources, ten configurations (names
such as 90, D, 45Q, 30Q, 30T, 30S) at about 30, 45 and 90 degrees; **nine configurations tried for a flat
surface, six for a curved one**; each read graded A to F with A to E passing; alignment tolerance 5 degrees.
**Adopt how:** a "light recipe list per mark type" that the auto-tune walks, with the grade as the score.
L0 and L5. Status Partial (LineAdvisor suggests exposure only). Effort 1 pw.

**US10650205.** Bright field: incidence within 45 degrees of perpendicular. Dark field: between 45 and 90
degrees. Image rebuilt from the two modes then decoded. Indicia elevation about 1 mm or less.
**Adopt how:** capture one bright-field and one dark-field frame and merge before DPM decode.

**WO2016103285.** Stereo capture, rectify, depth map, read the code from the depth map; Z resolution 7
micrometres, XY 30 micrometres per pixel. Google shows: ceased (the WO application; the US family member
must be checked). **Adopt how:** only for dot-peen on rough metal
when light recipes fail. Needs a stereo head. Not for our printed codes.

### 2.4 Photometric stereo and 3D-assisted illumination

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US9518931](https://patents.google.com/patent/US9518931) (+ US9632036, US10648921) | Keyence | Image inspection by photometric stereo, 3+ lights switched one by one | 2014-06-09 | Active, 2035-06-15 | US | [O] |
| [US10169857](https://patents.google.com/patent/US10169857) | Keyence | Photometric stereo and flaw inspection | 2016-09-06 | Active, 2037-08-08 | US | [O] |
| [US9683943](https://patents.google.com/patent/US9683943), [US10489900](https://patents.google.com/patent/US10489900) | Keyence | Parameters for the inspection image from luminance images | n/a | not checked | n/a | [S] |
| [US9778203](https://patents.google.com/patent/US9778203) (+ US10036713, US10156525) | Keyence | Illumination head movable independent of camera | n/a | not checked | n/a | [S] |

**US9518931 steps (description).** Three or more lights, four as base (north, south, east, west of the
camera); five to eight by splitting the ring; **fully-on reference images captured before and after** the
partial-light images, light set to 1/4 per light when all four are on; when the part moves (conveyor), the
position of each partial image is **estimated by linear interpolation** between the two reference images,
with permitted displacement of 2, 4 or 6 degrees and 8, 16 or 32 pixels; halation removed by using the
third-largest of four pixel values.
**Adopt how:** build a **Relief** Base tool: four lit frames, normal and albedo maps, then show embossed or
laser-engraved text as shape not shading. Targets embossed text (89.4 percent synthetic character accuracy
today) and dents on caps. L0 and L1. Status Missing. Effort 4 pw plus a four-segment light and strobing
(hardware, section 19). The conveyor interpolation matters at 225 parts per minute.

## 3. Image correction (L1)

### 3.1 Flat field, uniformity, shading

| Patent | Owner | Title | Status | Tag |
| --- | --- | --- | --- | --- |
| [US5047861](https://patents.google.com/patent/US5047861) | (not opened) | Pixel non-uniformity correction, stored white reference per pixel | Old, expired by age | [S] |
| [US5818525](https://patents.google.com/patent/US5818525) | (not opened) | RGB correction with compressed flat-illuminated files, one or two point | Old | [S] |
| [US8259179](https://patents.google.com/patent/US8259179) | (not opened) | Non-uniform illumination compensation from a uniform target | not checked | [S] |
| [US20050041806](https://patents.google.com/patent/US20050041806A1/en) | (not opened) | Image shading gains from a uniformly lit white image | application | [S] |

No patent worth adopting is needed here: dividing by a white reference with a dark frame is textbook. The
only method note: store a reference per exposure and **per temperature band**, and re-validate it when the
image mean drifts (see the calibration validation method US9734419 in section 7). L1. We have flat field
inside the reader (`CR_STEP_FLAT`) and for grading (`calibration.cpp`). Missing: a user Base tool and the
drift check. Effort 1 pw.

### 3.2 Deblur, super-resolution, resolution recovery

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US8150163](https://patents.google.com/patent/US8150163) | Scanbuy | Recovering image detail from multiple frames in real time | 2006-04-12 | **Expired**, 2029 estimate, fee related | US | [O] |
| [US9195873](https://patents.google.com/patent/US9195873) | Intel | Decoding small barcodes from blurred images | 2012-06-29 | **Expired** (fee, 2023-11-24) | US | [O] |
| [US8867857](https://patents.google.com/patent/US8867857B2/en) | Samsung (Brazil) | Restoration of blurred barcode images by OTF deconvolution | 2011-12-28 | **Expired**, fee related | US | [O] |
| [US8596540](https://patents.google.com/patent/US8596540) | ETH Zurich, now Scandit | 1D from blurred images against stored blurred reference patterns | 2009-01-15 | Active, 2030-03-04 | US, EP, DK, ES, HU, PL, PT, WO | [O] |
| [US9361503](https://patents.google.com/patent/US9361503) | Datalogic | Blurred symbol mapped to a blur-robust space | 2014 | Active, about 2034 | US, EP | [P] INDUSTRY-METHODS |
| [US11854163](https://patents.google.com/patent/US11854163B2/en) | Keyence | Reading device with a net that restores the code image | 2021-01-18 | Active, 2042-03-15 | US | [O] |
| [US12039400](https://patents.google.com/patent/US12039400) | Keyence | Net run in parallel with first decode | 2021-12 | Active, 2041-12-06 | US | [P] |
| [US11875259](https://patents.google.com/patent/US11875259) | Socure | Generative super-resolution of barcodes in documents | n/a | Active | US | [P] research-codes |

**US8150163 steps (claim 1).** (a) frame in; (b) four-corner coordinates by corner template; (c) features from
intensities; (d) noise estimate by a barcode template; (e) if noise is below a threshold, accumulate feature
statistics; (f) test decodability from the statistics; (g) if not, next frame; (h) decode. Typical 5 to 20
frames, 3 features, histogram bit decision at density above 0.5, vote threshold floor(n/2).
**Adopt how:** already close to `multiframe.cpp`; add the **noise gate** (reject noisy frames before they
vote) and the cross-frame **sub-pixel accumulation** for shrink-wrap QR where each frame is under 2 pixels
per module. Google shows: expired. L1 and L3. Effort 1.5 pw.

**US8867857 steps (description).** Select an optical transfer function from a database, deconvolve (FFT size
the next power of two over the larger image side), decode, display. EAN-13 example. **Adopt how:** our
`psf.cpp` measures blur per zone, and a Wiener plus Richardson-Lucy deblur step already exists
(`engine/src/decode.cpp`, `CR_STEP_DEBLUR`, with a Gaussian kernel). The work is to feed it the **measured
per-zone kernel** instead, tried only after normal decode fails. Google shows: expired. Effort 1 pw.

**US8596540 steps (claim 1).** Capture, preprocess into one output pattern from brightness along the bar
code, compare with stored reference patterns that are blurred images of sharp nominal patterns, pick the most
similar, output the symbols. Reference patterns about 30 pixels; 400 neighbour combinations per interior EAN
digit (20 x 20). Our SSCC fit renders candidates through the measured blur, which is this method in
spirit. Adopt the **two-neighbour context (20 x 20) per digit** for GS1-128 and ITF-14 on blurred cartons.
Effort 1.5 pw.

**US11854163 (Keyence AI restore).** Description numbers: code pixels per module 4 to 6 as the net input
scale; input crop 1.5, 2.0 or 3.0 times the code length; reduction 1/2, 1/4, 1/8 and enlargement 2x, 4x,
8x; convolution kernels 3 x 3 or 5 x 5; 2D sizes 21 x 21 to 177 x 177 modules.
**Adopt how:** our `enhancer.onnx` (`ml/enhance.py`, `violations.py` pairs) already does this. Take over
the **resample-to-4-6-pixels-per-module** rule as the preprocessing and measure what it rescues. Keep it
after normal decode (the older note about US12039400 parallel run is now only a design choice). Have.
Effort 1 pw to re-train at the 4 to 6 px scale.

## 4. Locate and pattern matching (L2)

### 4.1 Cognex PatMax family (geometric, sub-model, pose refinement)

| Patent | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- |
| [US7016539](https://patents.google.com/patent/US7016539B1/en) | Fast, robust, multi-dimensional pattern recognition (PatMax core) | 1998-07-13 | **Expired**, 2018-07-13 | US | [O] |
| [US7065262](https://patents.google.com/patent/US7065262B1/en) | Fast high-accuracy multi-dimensional pattern inspection | 1997-11-26 | **Expired**, 2018-06-20 | US | [O] |
| [US6658145](https://patents.google.com/patent/US6658145B1/en), [US6856698](https://patents.google.com/patent/US6856698B1/en) | Pattern inspection / localization siblings | 1997 | not checked | US | [S] |
| [US6324299](https://patents.google.com/patent/US6324299B1/en) | Object image search using sub-models | 1998-04-03 | **Expired**, 2018-04-03 | US | [O] |
| [US6993177](https://patents.google.com/patent/US6993177) | Gauging based on global alignment and sub-models | 2001 | **Expired** 2023 | US | [P] INDUSTRY-METHODS |
| [US6983065](https://patents.google.com/patent/US6983065B1/en) | Oriented filters for feature extraction | 2001 | **Expired** 2023 | US | [P] |
| [US8345979](https://patents.google.com/patent/US8345979B2/en) | Finding and characterising a deformed pattern (deformation map) | 2003-07-22 | **Expired** (fee), 2023-07-22 | US | [O] |
| [US6941016](https://patents.google.com/patent/US6941016B1/en) | Finding contours in an image (spatial hash chaining) | 2001-12-31 | **Expired** (fee), 2023-08-28 | US | [O] |
| [US6748110](https://patents.google.com/patent/US6748110) | Object and feature detector: grey-level segmentation plus edges | 2000-11-09 | **Expired**, 2022-08-07 | US | [O] |

**US7016539 (PatMax core), what is disclosed.** The pattern is stored as an ordered list of boundary points
with direction; the search finds pose without resampling the image; the score is the visible share of the
model. Numbers in the description: about **64 probes per model**, probe spacing **0.5 to 4.0 pixels**, a
corner threshold of **22.5 degrees** where contours are split. Claims not read (329,647 characters; first
100,000 read).
**Adopt how:** for the Locate tool: model points from Sobel edges along contours, chosen at 0.5 to 4 px
spacing, about 64 per model; coarse pose from a pyramid, refine by least-squares on point-to-edge distance.
Gain: Med for the locator (target 0.05 px / 0.05 deg, 100 percent find). Effort 3 pw.

**US7065262 numbers.** 4 attraction steps and 4 propagation steps; corner threshold 16.875 degrees; 16-bit
force with 11 fractional bits (about 1/2048 grid unit); propagation rejects offsets over 3 grid units or 135
degrees; default noise threshold 10 grey levels; multi-resolution fraction 0.9. **Adopt how:** these are
the inner loop of the **sub-pixel alignment and defect field**: iterate attraction (each model point pulled
to its nearest image edge), propagate, fit the pose. The same field gives a per-point residual that can show
"where the print differs". L2 and L4. Effort 2 pw (shared with the locator).

**US6324299 (sub-models), steps (claim 1).** Define a full model and several sub-models; coarse candidate
poses of the whole object; refine each portion's pose from the coarse pose and the sub-models; derive the
fine poses of the whole from the portion poses. Sub-models cut at 50, 75 and 100 percent of segment length.
**Adopt how:** locate a label by its big frame, then fix position by 3 to 4 small anchors (logo, barcode
edge, text line). Removes the error from a label that stretches or from a shifted cap. Effort 1.5 pw.

**US8345979 (deformed pattern), steps (claim 1).** (1) 2D features of the deformed pattern in one image;
(2) divide the features into spatially coherent sub-patterns of more than one feature; (3) locate the first
sub-pattern and record it; (4) locate the second sub-pattern; (5) from both locations determine a
**deformation mapping**. Examples: search range expanded by 70 percent, 3 pixels leeway; user-selectable
transform fitted to matched points.
**Adopt how:** the PatFlex-like path (inference: Cognex says PatFlex handles curved and warped surfaces; no
PatFlex patent number found). For labels on bottles, shrink film and pouches: split the model into
sub-patterns, find each in a restricted range, fit a thin-plate or polynomial map, rectify before OCR or
code read. Google shows: expired. L1 and L2. Gain: Med to High for curved labels and wrinkled film. Effort 3 pw.

**US6941016 (contours).** Features from Sobel or large oriented filters chained into segments; a **spatial
hash table** of about 10 x 10 pixel bins joins only segments with endpoints in adjacent bins; chains under 4
edgelets dropped; line-fit RMS 0.5 px, edgelet angle within 30 degrees, gap up to 50 px, third pass allows
bends up to 90 degrees. **Adopt how:** edge-chaining for scratches, cracks and lines in the Inspect tool.
Effort 1.5 pw.

**US6748110 (blob + edges), steps (claim 1).** Grey-level segmentation gives boundary points; edge
detection gives edge data; where edge data lies within `maxDistance` (3) of the boundary, use the edge.
Edge thresholds such as magnitude 20 / length 30 or magnitude 40 / length 13. **Adopt how:** the **sub-pixel
blob outline**: threshold blob, snap its boundary to the nearest strong edge. Gives blob area and centroid
accuracy of a fraction of a pixel. L3. Effort 1 pw.

### 4.2 MVTec shape-based and component-based matching

| Patent | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- |
| [US7062093](https://patents.google.com/patent/US7062093B2/en) | System and method for object recognition (shape-based matching) | 2000-09-27 | **Expired** 2024-03-01 | US (EP priority) | [O] |
| [US7239929](https://patents.google.com/patent/US7239929B2/en) | Hierarchical component-based object recognition | 2002-08-30 | **Expired** 2024-12-12 | US | [O] |
| [US8260059](https://patents.google.com/patent/US8260059B2/en) | Deformable object recognition | 2008 | not checked | US | [P] RESEARCH-COGNEX-PATENTS |
| [US7751625](https://patents.google.com/patent/US7751625B2/en) | Automatic parameter determination | 2006-03-24 | Active per Google, 2029-03-05 (the earlier register says expired for US7953290, see the next row) | US | [O] |
| [US7953290](https://patents.google.com/patent/US7953290B2/en) | Automatic contrast (threshold) determination | 2006-03-24 | Expired (Google 2026-04-21) | US | [P] research-ai |
| [EP4636707](https://patents.google.com/patent/EP4636707A1/en) | Example-based parameter estimation, robust per pyramid level | 2024-04-17 | Pending | EP | [P] |

**US7062093 steps (summary; claims not read).** (a) model image; (b) multi-level pyramid; (c) precompute model
points and direction vectors per level; (d) current image; (e) its pyramid; (f) direction vectors; (g) match
metric at the coarsest level; (h) keep above-threshold local maxima; (i) track through finer levels;
(j) output poses at the finest level. Numbers: 2 x 2 block averaging per pyramid level; **5 x 5** search
window after propagation; pruning factor **0.9**; partial-score parameter g up to 0.9; inverse-length lookup
**256 x 256**; ROI dilation 5 x 5; sub-pixel threshold about **0.25 px**. The metric is the mean of the
normalised dot product of model direction and image gradient, which is immune to non-linear illumination.
**Adopt how:** the **Edge-Match** locator mode. It handles occlusion, clutter and lighting change that grey
correlation does not. Our `locate_tool.cpp` has grey-level matching with turn but no scale (Partial). Build
direction-vector matching with the greedy early-exit rule (stop adding terms when the running score cannot
reach the minimum). Gain: Med to High for the locator. Effort 3 pw.

**US7239929 steps (claim 1).** (a) decompose the object in the model image into initial components; (b) find
their relative poses over example images; (c) group components that move alike; (d) relative poses of
the groups; (e) hierarchical model with a root part. Online: find the root, then each other part in a limited
range. **Adopt how:** parts that move relative to each other (cap on a bottle, flip lid, hinged carton
flap, a label on a neck). Effort 2 pw.

**US7751625 and US7953290 (automatic parameters).** From one model image: edge-amplitude image; evaluate edge
regions across thresholds; take the threshold at the evaluation maximum as contrast; minimum part size at a
**5 percent** cutoff of edge pixels; hysteresis search stops at **95 percent** of the best; model points
reduced 20 percent per step; minimum contrast = noise standard deviation x 2.5; angle step halved until the
score passes the minimum. **Adopt how:** the **Auto-learn for Locate**: give a teach image, get contrast,
min size, angle step and pyramid depth with no user input. L5. Effort 1.5 pw. (US7751625 is shown active
to 2029 by Google; the owner's statement of 2026-10-30, section 0, applies.)

### 4.3 Keyence, Omron and others (pattern search)

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US8355590](https://patents.google.com/patent/US8355590) (+ US8150213, US8155473) | Keyence | Edge-angle bit image reduction and pattern model positioning | 2008-08-09 | Active, 2029-07-16 | US | [O] |
| [US8401305](https://patents.google.com/patent/US8401305), [US8406527](https://patents.google.com/patent/US8406527) | Keyence | Contour extraction / pattern model positioning | 2008 | "Expired, fee related" per register | US | [P] |
| [US10366307](https://patents.google.com/patent/US10366307B2/en) | Omron | Coarse-to-fine search with SIMD rearranged data | 2015-11-06 | Active, 2036-12-20 | US, EP, JP, KR, CN, WO | [O] |
| [US8422759](https://patents.google.com/patent/US8422759) | Omron | Edge-code histogram for coarse search | 2008 | Active, 2031 | US | [P] |
| [US9286669](https://patents.google.com/patent/US9286669) | Omron | Edge position and direction model with allowed direction change | 2012 | Active, 2034 | US | [P] |
| [US6640008](https://patents.google.com/patent/US6640008) | Nikon | Polar matching | 2001 | Expired 2021 | US | [P] |
| [US20120082385](https://patents.google.com/patent/US20120082385A1/en) | (not opened) | Edge based template matching | n/a | application | n/a | [S] |

**US8355590 (Keyence) steps (description).** (1) edge-angle image with an angle per pixel; (2) convert each
pixel's angle into an **edge-angle bit image**: 8 sections of 45 degrees, offset 22.5 degrees, one bit per
section (word width 8, 16, 32 or 64 bits); (3) **OR the bits inside each region** whose size is the
reduction ratio (for example 8 x 8 at one-eighth) to make the reduced image; coarse search on that,
fine positioning on the original or a less reduced image. Model edge angles at 256 levels over 0 to 360
degrees; saturated addition upper limit 3; multiple-candidate weight 1 - d1/d2.
**Adopt how:** a very fast coarse stage for the **20 MP frame**: bit-image reduction by OR is cheaper than
Gaussian pyramids and keeps thin lines. Combine with Omron's SIMD layout (US10366307: pyramid
160 x 120, 320 x 240, 640 x 480; fewer than n x m SIMD collations; about 1/64 the work of a full search).
Speed gain for Locate on 5472 x 3648 images. Effort 2 pw.

## 5. Measurement and sub-pixel edges (L3)

### 5.1 Sub-pixel edge methods

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US6408109](https://patents.google.com/patent/US6408109B1/en) | Cognex | Detecting and sub-pixel location of edges (gradient grid, interpolation) | 1996-10-07 | **Expired** 2016-10-07 | US | [O] |
| [US6690842](https://patents.google.com/patent/US6690842B1/en) | Cognex | Same family, edges not aligned to the pixel grid | 1996-10-07 | **Expired** 2016-10-07 | US | [O] |
| [US6175652](https://patents.google.com/patent/US6175652), [US6175644](https://patents.google.com/patent/US6175644) | Cognex | Feature analysis from multiple object images | n/a | not checked | US | [S] |
| [US6571006](https://patents.google.com/patent/US6571006) | Cognex | Measuring the extent of a group of objects | n/a | not checked | US | [S] |
| [US5845007](https://patents.google.com/patent/US5845007A/ja) | Cognex | Edge-based image histogram analysis | n/a | not checked | US | [S] |

**US6408109 / US6690842 steps (abstract and description).** (1) estimate gradient magnitude and direction on
the pixel grid (8-bit pixels, 11-bit gradient components, a 65,536 x 16-bit lookup table for magnitude and
direction, about 1.4 degree direction resolution, four CORDIC blocks with scale factor about 1.16); (2) pick
neighbours of each point **along the gradient direction** with boundaries at 22.5 degrees (3 x 3 grid:
4-bit direction; 5 x 5 grid: 5-bit); (3) keep local maxima of magnitude; (4) interpolate the sub-pixel
position along that 1D profile within plus or minus 0.5; diagonal neighbours spaced by the square root of 2.
**Adopt how:** the engine of a **Caliper and Edge Finder** tool: Sobel gradient, non-maximum suppression
along the gradient, parabolic interpolation. Matches the measurement target 0.1 px at 3 sigma if lens
distortion and flat field are corrected first. Status Missing for a user tool (parabola fit only inside
Locate). Effort 2 pw including edge scoring and a 3-point least-squares fit.

**US6993177 (sub-model gauging).** Locate small stable sub-models near each gauge, not one global pose.
Adopt as the "measure from local anchors" rule. Effort 1 pw, shared with US6324299.

### 5.2 Blob

**US6748110** (section 4.1) is the only blob patent opened. Blob analysis itself (threshold, connected
components, run-length coding, area, centroid, perimeter, orientation) is public textbook; no patent needed.
Adopt: connected components with run-length coding, then the sub-pixel snap of US6748110.

## 6. Calibration (L1)

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US6137893](https://patents.google.com/patent/US6137893) | Cognex | Calibration targets and finding their pose (lines pointing to a reference point) | 1996-10-07 | **Expired** 2016-10-07 | US, EP, JP, WO | [O] |
| [US9734419](https://patents.google.com/patent/US9734419B1/en) | Cognex | Validating camera calibration in a vision system | 2008-12-30 | Active, 2031-04-16 | not shown | [O] |
| [US10032273](https://patents.google.com/patent/US10032273B2/en) | Cognex | Calibration using inaccurate targets | 2013 | not checked | US | [S] |
| [US9393694](https://patents.google.com/patent/US9393694B2/en) | Cognex | Robust calibration machine vision to robot | 2010 | not checked | US | [S] |
| [US8619144](https://patents.google.com/patent/US8619144) | (not opened) | Automatic camera calibration to sub-pixel reprojection | n/a | not checked | US | [S] |
| [US12159429](https://patents.google.com/patent/US12159429B2/en) | MVTec | Hand-eye calibration | 2021 | Active | US | [P] PATENTS.md |
| [WO2018005561](https://patents.google.com/patent/WO2018005561A1/en) | (not opened) | Calibration targets with individualised markers | n/a | application | WO | [S] |

**US6137893 steps (claim 1 paraphrase).** Place the target on the object, image it, find edges of the target
lines, fit lines, and **intersect the fitted lines** to get the reference point. Fifty caliper points per
edge on the first pass; confusion threshold 2x between edge scores; four regions; orientation over 360
degrees. **Adopt how:** locate the calibration plate pose from the intersection of fitted lines, which is more
accurate than corner detection alone. L1.

**US9734419 steps (abstract).** Calibration stores parameters and discrepancy statistics. Validation images
the plate, extracts features, predicts their positions from the solved pose and intrinsics, and flags
recalibration when discrepancies pass thresholds: **mean plus 3 standard deviations** of calibration
discrepancies, or 125 percent of the maximum average discrepancy; acceptance when **90 percent** of
discrepancies fall below the 3-sigma threshold.
**Adopt how:** a daily **Calibration check** at shift start and after a camera is touched, with a pass / fail
and an NG-on-doubt lock when drift exceeds the limit. This is the line-safety piece. L1 and L4. Status
Partial (`calibration.cpp`, `GradingCalibration.cs`; no drift check). Effort 1 pw.

## 7. Code reading: 2D codes (L2, L3)

### 7.1 Damaged, partial, under-resolved

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US6685095](https://patents.google.com/patent/US6685095) | Symagery (Psion, Symbol) | Decoding damaged optical codes (erasures) | 1998-05-05 | **Expired** 2018-10-01 | US, EP, AU, WO | [O] |
| [US7965899](https://patents.google.com/patent/US7965899B2/en), [US8270749](https://patents.google.com/patent/US8270749B2/en), [US7412106](https://patents.google.com/patent/US7412106B1/en) | Cognex | Dot-peen / dotted DM filter chain | 2005-06 | US7965899 and US8270749 expired; parent US7412106 active to 2026-12-23 (Google) | US | [P] |
| [US9946947](https://patents.google.com/patent/US9946947B2/en) | Cognex | Saddle points for DotCode and dotted codes | 2012-10 | "Expired, fee related" | US, CN, DE, HK | [P] |
| [US9911024](https://patents.google.com/patent/US9911024B2/en), [US10956697](https://patents.google.com/patent/US10956697B2/en), [US11455482](https://patents.google.com/patent/US11455482B2/en) | Cognex | 2D matrix with incomplete or absent fixed patterns | 2014-11 | Active, about 2035-11 | US, EP, JP, CN, HK | [P] |
| [US11151346](https://patents.google.com/patent/US11151346B2/en), [US9607200](https://patents.google.com/patent/US9607200B2/en) | Cognex | Under-resolved symbols | 2014-10 | Active, about 2034-10 | US, DE, CN, JP | [P] |
| [US10235547](https://patents.google.com/patent/US10235547B2/en) | Hand Held Products | Enhanced matrix symbol error correction (rank codewords, erasures) | 2016-01-26 | Active, 2036-01-26 | US | [O] |
| [US10540532](https://patents.google.com/patent/US10540532B2/en) | Datalogic | Damaged or incomplete finder patterns | n/a | Active | US | [P] |
| [US11960966](https://patents.google.com/patent/US11960966B2/en) | ABBYY | CNN module map of a code | n/a | Active | US | [P] |
| [US11354526](https://patents.google.com/patent/US11354526B2/en) | not checked | Net locating partly unreadable Data Matrix | n/a | not checked | US | [P] |
| [US20260010746](https://patents.google.com/patent/US20260010746A1/en), [WO2026011064](https://patents.google.com/patent/WO2026011064A1/en) | Cognex | Deep learning symbol decoding (vision transformer plus RS) | 2024-07-03 | Pending | US, WO | [P] |

**US6685095 steps (claim 1).** (1) identify an image area with a code; (2) trace its contour; (3) locate at
least part of the finder pattern (**50 percent** of it, claims 3 and 14); (4) scan a region for a known
characteristic; (5) label the region as an **erasure** if the characteristic is missing; (6) decode.
Have: `softDecodeDm`, `rsDecodeDm`. Google shows: expired.

**US10235547 steps (abstract).** Rank codewords by optical contrast (local black / white threshold from 20
neighbouring cells); flag the lowest-contrast codewords as **erasures** in Reed-Solomon; each erasure needs
one check codeword instead of two. 8 cells per codeword; example 16 x 16 symbol with 12 EC codewords repairs
6 damaged data codewords; robust under about one third damage.
**Adopt how:** `qrsoft.cpp` and `underres.cpp` already rank by flip cost. Add the **contrast-ranked erasure
list per codeword** with a hard rule: more erasures than EC capacity means NG. Status Have for DM and QR
soft decoding; Partial for the explicit capacity rule. Effort 0.5 pw.

**US11151346 (under-resolved): steps, in the order Cognex discloses.**
1. Direct deduction: sparse module-to-pixel overlap matrix; a module covering a foreground or background pixel
   by at least **90 percent** is fixed (about 30 percent can do if the grid is accurate); propagate through
   pixels that two modules share when one is known.
2. Sub-grid test: for each unknown module test the **3 x 3 patterns (up to 512)** centred on it, keep those
   under a pixel-error threshold, work inward from finder and timing.
3. Constraint graph: link surviving patterns in **8 directions** where overlaps agree, prune nodes with no
   link in some direction.
4. Leftovers to Reed-Solomon as erasures.
The patent says edge decoders start failing at **1.3 to 1.5 pixels per module**, conventional decoding wants
about 2, and the target is below 1 (0.8 as the worked example). 1D members compute sampling coefficients from
samples per module and phase and test the valid patterns (103 for Code 128).
**Adopt how:** `underres.cpp` has steps 1, 2 and the erasure hand-off: Partial, on by default (`CR_UNDERRES`
ON, `engine/CMakeLists.txt:22`; change note of 2026-10-27). Missing: the 8-way **constraint graph pruning**
and the router that sends codes below 1.5 px/module here.
Targets TOO_SMALL (below 1.8 px/module) and shrink-wrap QR 60.3 percent. Effort 3 pw. Gain: High.

**US10956697 / US11455482 (no fixed pattern): steps.** Edge-magnitude image; 2D FFT, peaks give orientation
and pitch; optionally mask the spectrum around the peaks and inverse FFT to get predicted module centres;
seed a **5 x 5-module local affine patch**, translate it in 8 directions, refine, then a global polynomial;
extrapolate, out-of-image modules unknown; rank hypotheses over symbology, size, offset, mirror and polarity;
decode in rank order with erasures for the gaps. **Adopt how:** `lattice.cpp` works the same way. Missing:
the **masked inverse FFT centre map**, the polynomial for curved or wrinkled codes, and the "outside the
image is erasure" rule. Effort 2 pw. Gain: High for NO_FINDER and COVERED misses (48 DM 64.2 percent).

**US7965899 chain.** 3 x 3 smoothing; Sobel (components divided by 4); gain k = 6.0 clipped at 255; Gaussian
sigma = (module size - 1)/6; on failure sub-sample by 2, 4 or 8 until 5 to 10 px per module. Have as
`CR_STEP_DOTS` (smooths by about a third of a module). Missing: the Sobel-gain-Gaussian stages and the
resample rule. Effort 0.5 pw.

**US9946947 saddle points.** 5 x 5 neighbourhood compared with itself rotated 90 degrees (a saddle scores
near -1); dominant angle = half atan2 of two kernel responses 45 degrees apart; histogram of spacing gives
module pitch; fit a grid. Adopt for DotCode (`dotcode.cpp` exists, Partial) and dot-printed DM. Effort 1.5 pw.

### 7.2 Multi-frame, locator and workflow

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US7181066](https://patents.google.com/patent/US7181066B1/en) | Cognex | Tile and edge-angle histogram locator | 2002-12 | **Expired** 2024-12-07 | US | [P] |
| [US8265404](https://patents.google.com/patent/US8265404B2/en), [US9036929](https://patents.google.com/patent/US9036929B2/en) | Cognex | Low-resolution view then full-resolution windows | 2004-08 | **Expired** 2024-08 | US | [P] |
| [US9147095](https://patents.google.com/patent/US9147095) | NCR | Reduced-resolution contrast enhance, then correct distortion only in the ROI | 2013-11-22 | Active, 2033-12-10 | US | [O] |
| [US7878402](https://patents.google.com/patent/US7878402B2/en), [US8322620](https://patents.google.com/patent/US8322620B2/en) | Cognex | Decoding distorted symbols (fixed-station homography) | 2005-12 | Lapsed (fee) | US, WO | [P] |
| [US9122952](https://patents.google.com/patent/US9122952B2/en), [US9218536](https://patents.google.com/patent/US9218536B2/en), [US9589199](https://patents.google.com/patent/US9589199B2/en) | Cognex | 1D extraction (Hotbars) | 2011-12 | Active, about 2032-06 | US, WO, EP, JP, CN, HK | [P] |
| [US9361499](https://patents.google.com/patent/US9361499B2/en) | Cognex | 1D evidence integration over partial scans | 2014-10 | Active, about 2034 | US | [P] |
| [US9836635](https://patents.google.com/patent/US9836635B2/en), [US10628648](https://patents.google.com/patent/US10628648B2/en), [US20180157882](https://patents.google.com/patent/US20180157882A1/en) | Cognex | Tracking optical-code fragments across frames | 2014-10-09 | Active, 2034-10-09 | US | [O] |
| [US8794521](https://patents.google.com/patent/US8794521B2/en), [US10154177](https://patents.google.com/patent/US10154177B2/en) | Cognex | Multi-core reader, hand off only if "probably decodable" | 2012-10 | Active, about 2033-01 | US, DE, CN | [P] |
| [US7984854](https://patents.google.com/patent/US7984854) | Cognex | Multiplexed fast and robust decoders (85 / 850 ms) | 2006-07 | Active, about 2027-02 | US, WO, EP, JP | [P] |
| [US11295100](https://patents.google.com/patent/US11295100B2/en) | Keyence | Optical reading device: one core dispatches images to 8 decoding cores | 2020-03-19 | Active, 2041-02-22 | US | [O] |
| [US10325129](https://patents.google.com/patent/US10325129B2/en) | Cognex | Ordering candidates on a conveyor | 2011-11 | Active, about 2031-12 | US | [P] |
| [US10198648](https://patents.google.com/patent/US10198648B1/en) | Digimarc | 1D decode by correlation, staged orientation classifiers | 2015-04-10 | Active, 2036-04-08 | not stated | [O] |

**US9147095 (NCR), claim 1 paraphrase.** Capture; make successively lower-resolution images (1280 x 960, then
640 x 480, 320 x 240, 160 x 120, 80 x 60, 4x reduction per layer); from the lowest, enhance contrast and look
for a candidate; identify the ROI in the highest-resolution enhanced image; correct geometric distortion only
in that part; recover the code. **Adopt how:** with US9036929, a **pyramid search and ROI-only correction** for
the 20 MP frame. Cuts the 48 DM time (over 500 ms today). L2. Effort 1.5 pw. Gain: Med accuracy, High speed.

**US7181066 (tile locator), steps.** 32 x 32 tiles on a checkerboard; Roberts 2 x 2 cross with threshold
about 14; angle histogram of 256 bins averaged to 64 (about 5.6 degrees); peaks within 22.5 degrees clustered
down to 50 percent of the local maximum; **two peaks 180 degrees apart means bars, four peaks 90 degrees
apart means Data Matrix**, six means MaxiCode; grow regions within 10 degrees and 50 percent density. Adopt
as the tie-breaker when the AI locator is unsure and as the 1D / 2D classifier. Effort 1 pw.

**US20180157882 / US10628648 (fragment tracking).** Locate a fragment at time 1, a second at time 2, a
tracking model predicts where fragment 1 should be, and if fragment 2 falls inside the predicted range the two
are one code. Example: motion direction 90 degrees, maximum deviation 20 px, angular uncertainty 15
degrees, accuracy about half a module. **Adopt how:** cases that are only partly visible in one frame on the
225 parts per minute line: join fragments over 2 to 3 frames with the encoder shift. L2 and L3. Effort 2 pw.

**US11295100 (Keyence), numbers.** Nine cores: one dispatches, eight decode; burst about 30 fps (33 ms per
frame); 50 to 100 ms per decode; 12-bit reduced to 8-bit in preprocessing. **Adopt how:** the line PC (i7)
can run several images in parallel; add a **dispatcher with a time budget** per case type (12: 100 ms,
24: 250 to 300, 45 / 48: 500, 96: 600) in the scheduler. Status Partial (`pool.hpp`). Effort 1 pw.

**US10198648 (Digimarc).** UPC-A 95 modules; guard bars about 5 x the x-dimension; templates over 53 scales
(1.875 to 4.375 in 0.05 steps); eight 3-bit prefixes per symbol; three blur levels; rescaled box about 300 px;
16 orientation classifiers at 11.25 degree steps; correlation about 92 percent. **Adopt how:** the
**overlapping-prefix template** idea for EAN and ITF on blurred or skewed cartons. Effort 1.5 pw (our
`linear.cpp` has matched-filter geometry).

## 8. 1D codes (L2, L3)

| Patent | Owner | Title | Status | Tag |
| --- | --- | --- | --- | --- |
| US9122952 family (Hotbars) | Cognex | 171 orientations per 90 degrees, integer transfer templates | Active, about 2032 | [P] |
| US9607200 (1D members of US11151346) | Cognex | Sampling coefficients from samples per module and phase | Active, about 2034 | [P] |
| US9361499 | Cognex | Accept a character when the top reading beats the second by a margin | Active, about 2034 | [P] |
| US8596540 | Scandit | Blurred reference patterns | Active, 2030 | [O] |
| US10198648 | Digimarc | Correlation decode | Active, 2036 | [O] |
| US9361503 | Datalogic | Blur-robust space | Active, about 2034 | [P] |
| US11334731 | Univ. of Pittsburgh | Per-column classifier | Active, 2039 | [P] |
| US9098764 | (not opened) | Image-based reader: locate, then decode along a scanline | n/a | [S] |
| US8439260 | (not opened) | Real-time recognition using general cameras | n/a | [S] |

**Hotbars (US9122952) numbers.** 2D pixels projected on a 1D signal along 171 quantised orientations per 90
degrees; blur about 2.3 px along the bars and about 0.34 x the bin spacing across; bins 0.73 px apart; best
sub-pixel offset on an 8 x 8 grid at 1/8 px then 1/64 px.
**US9361499:** one unit of evidence per element per scan; accept when the top reading beats the second by
**1.5**; width tolerance **10 percent**.
**Adopt how for SSCC, GS1-128, EAN, ITF-14 (`linear.cpp`, Have, no real side-camera frames yet):** (1) the
Hotbars direction-by-direction blur (more blur along the bar than across); (2) the 1.5 margin as the accept
rule; (3) the 1D evidence sum across frames so a carton that shows 40 percent of the bars in each of three
frames still reads (checked by the check digit). Effort 2 pw total. Gain: Med to High on side labels at line
speed.

## 9. Verification and grading (L4)

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US9552506](https://patents.google.com/patent/US9552506) | Cognex | Industrial identification mark verification | 2004-12 | **Expired** | US | [P] |
| [US10061946](https://patents.google.com/patent/US10061946) | Cognex | Same, diagnostics when decode fails | 2004-12-23 | **Expired** (fee), 2024-12-23 | US | [O] |
| [US8027802](https://patents.google.com/patent/US8027802) | Cognex | Ideal mark rendered from decoded data, deformation map | 2006 | **Expired** | US | [P] |
| [US8169478](https://patents.google.com/patent/US8169478), [US6700997](https://patents.google.com/patent/US6700997) | Cognex | Calibration card; verifier | 2000-06 | **Expired** | US | [P] |
| [US10740582](https://patents.google.com/patent/US10740582B1/en) | Datalogic | Generic shape quality verification (ISO 29158 metrics for any shape, letters, logos) | 2019-01-22 | Active, 2039-02-08 | US | [O] |
| [US10546160](https://patents.justia.com/patent/10546160) | Honeywell / Hand Held Products | Verifier feedback loop to the printer | n/a | Active | US | [S] (Justia) |
| [US20170111523](https://patents.google.com/patent/US20170111523A1/en) | (not opened) | Detecting barcode printing errors | n/a | application | n/a | [S] |
| [US7347376](https://patents.google.com/patent/US7347376) | (not opened) | Verifying print quality of an encoded indicium | n/a | not checked | n/a | [S] |
| [US10559075](https://patents.google.com/patent/US10559075B2/en) | (not opened) | Printer-verifiers | n/a | not checked | n/a | [S] |

**US10061946 numbers (claim 1 seen).** Acquire, try to decode, and if decode fails, locate the code and measure
mark quality by finder-pattern quality (continuous finder: (NaD/NnD) x (NaL/NnL) x (Nd/Nl); discontinuous:
(NaD/NnD) x (NaL/NnL)) and/or histogram analysis of the quiet zone. **Sharpness S = min(1, edge light /
(light - dark))**; module separability from 0 to 1; symbol separability passes if m0 < T < m1; quiet zone at
least 1 cell for DM, **4 cells for dot-peen or inkjet**; filters 3 x 3 Sobel and 3 x 3 median.
**Adopt how:** `diagnose.cpp` already reports why a region did not read (Have). Missing: the numeric
**quality grades on a code that did not decode**, so a bad print is flagged NG at the printer instead of read
as "no code". Effort 1 pw.

**US10740582 (Datalogic) metrics.** Cell Contrast, Cell Modulation, Grid Non-Uniformity, Axial
Non-Uniformity, Fixed Pattern Damage, Unused Error Correction, Print Growth; detail size more than 3 px in
the acquired image (example 4 px); grades as real numbers or A to F. **Adopt how:** apply the **same metric
set to date and MRP text and logos** (`ocr/quality.cpp` exists, Partial), so text on a cap gets a graded
print quality, not only a pass / fail. Effort 2 pw. This is the one verification patent that touches text.

**US8027802 steps.** Re-render the ideal symbol from the decoded data, subtract from the image to get a
deformation map, score 0 to 100 against baselines 80, 90 or 95. **Adopt how:** **render the expected text
(from the job or the ERP) and difference it with the image**, to cut the OCV false rejects (true laser text
passes only 69 percent today). Status Missing (the difference map "is not built yet"). Effort 1.5 pw.

## 10. OCR, OCV and print-technology preprocessing (L1, L3, L4)

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US9317767](https://patents.google.com/patent/US9317767), [US9286534](https://patents.google.com/patent/US9286534) | Cognex | Selecting segmentation parameters for OCR against the known string | 2012-10-17 | Active, 2033-04-17 | US | [O] |
| [US10068153](https://patents.google.com/patent/US10068153) | Cognex | Handheld trains OCR from a font file | 2012-08-21 | Active, 2032-08-21 | US | [P] |
| [US8036463](https://patents.google.com/patent/US8036463B2/en) | Keyence | Character extracting: projection with background subtraction | 2006-09-13 | **Expired** (fee), 2030 estimate | US | [O] |
| [US5212741](https://patents.google.com/patent/US5212741A/en), [EP0552704](https://patents.google.com/patent/EP0552704B1) | Kodak | Dot-matrix and inkjet text preprocessing for OCR | 1992-01-21 | **Expired** 2013-01-18 | US, EP, JP, DE | [O] (EP0552704) |
| [US5357581](https://patents.google.com/patent/US5357581A/en) | Kodak / Global 360 | Detect dot-matrix regions, filter only there | 1992 | **Expired** | US | [P] |
| [EP0539854](https://patents.google.com/patent/EP0539854A2/en) | (not opened) | Detect dot-matrix text: three black pixels in a line | 1991 | old | EP | [S] |
| [EP0498873](https://patents.google.com/patent/EP0498873B1) | (not opened) | Dot-matrix video enhancement for OCR | 1991 | old | EP | [S] |
| [US4872024](https://patents.google.com/patent/US4872024) | Nagai et al. | Print inspection by dot count without reference | 1987 | **Expired** | US | [P] |
| [US10628934](https://patents.google.com/patent/US10628934B2/en) | Videojet | Printer with OCR trained per print technology, multi-stage inspection | 2016-08-11 | Active, 2037-08-11 | US | [O] |
| [US10810465](https://patents.google.com/patent/US10810465B2/en) | Datalogic | Auto-encoder reconstruction rejects false classifier results | 2018 | Active, 2039-01-29 | US | [P] |
| [US9384423](https://patents.google.com/patent/US9384423B2/en) | Xerox | Confidence from image and text embedding similarity | 2012 | Active, about 2034 | US | [P] |
| [US20130170765](https://patents.google.com/patent/US20130170765A1/en) | Samsung | Restoration of blurred barcodes (application of US8867857) | 2011 | see section 3.2 | US | [S] |

**US9317767 / US9286534 steps (summary; claim text not in the read portion).** Receive an image and the
known string; automatically generate segmentation parameters; segment; accept the parameter set if the result
meets criteria (default 80 percent area-overlap tolerance for matching regions); display the parameters.
**Adopt how:** the **OCR auto-teach**: on 20 to 50 known-good prints of a product, search spacing, pitch and
threshold parameters, score by correct segmentation against the known text, store per product. L5. The line
parameters are already estimated per image (dot pitch PR #80, word gap and slant PR #92, line finder PR #107,
cap height PR #132); what is missing is tuning them against the known text. Gain: High for CIJ text (72.9 percent character accuracy today),
because CIJ spacing and dot gaps differ from run to run. Effort 2 pw.

**US8036463 (Keyence), steps.** Sum pixel values along the character extraction direction at each position;
split into fixed-width sections (**110 to 120 percent of the character width**); take the minimum of each
section as background; find minima across groups; interpolate a base value; **subtract it** to cancel uneven
lighting; threshold (default 40 on a 0 to 255 scale); scan shift one pixel.
**Adopt how:** a **background-subtracted projection profile** for line and character cutting on curved or
unevenly lit laser text. Cheap and robust. Google shows: expired. L1 and L3. Effort 1 pw. Gain: Med for laser (100
percent real, but synthetic 92.6) and for caps.

**US5212741 / EP0552704 steps (claim 1).** (a) density histogram; (b) 2D spatial averaging with an N x N mask
(N odd, example 5 x 5) so dots become strokes; (c) contrast stretch by remapping density to the full range
(example shift 2); (d) edge enhancement; (e) binarise at the midpoint. **Adopt how:** we have a closing at
1.2 dot diameters; add the **per-region switch** of US5357581 (filter only where dot-matrix is detected: at
least three black pixels in a row separated by white). Effort 0.5 pw. Gain: Med for CIJ.

**US10628934 (Videojet).** Printer prints a multi-digit code; camera images it; a **trained OCR for that
print technology** reads each digit inside a region of interest from product parameters. About 50 percent
vision-system reliability in the background; example character matrix 4 x 4; check every 10th or 50th image.
**Adopt how:** keep **one OCR model per print technology** (CIJ, laser, TTO, embossed) and select by the job's
mark type. Status Partial: one OCR model (`ml/models/ocrnet.onnx`) serves all print types, and its accuracy
is measured per type. Gain: Med. Per-technology models and their selection are in the AI chapter.

**US10068153.** Convert a marker font file to a synthetic image to train OCR. Have (rendered system font,
PR #108). Extend with the dot-matrix and laser-stroke rendering of the font (Missing). Effort 1 pw.

**No patent found** for OCRMax's classifier or OCVMax's grading. Cognex describes the OCRMax segmenter and
classifier in its VisionPro help pages ([CogOCRMaxSegmenter](https://docs.cognex.com/vpro_950/web/EN/help/html/T_Cognex_VisionPro_OCRMax_CogOCRMaxSegmenter.htm)).
Keyence, Omron, Domino and Markem-Imaje: only US8036463 found. No claims of an OCV score formula for inkjet.

## 11. Print inspection, golden template, variation model (L1, L4)

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US5850466](https://patents.google.com/patent/US5850466) | Cognex | Golden template comparison for rotated or scaled images | 1995-02-22 | **Expired** 2015-12-15 | US | [O] |
| [US8971663](https://patents.google.com/patent/US8971663) | Cognex | Synthetic golden template for multi-layer patterns | 2012-05-21 | Active, 2032-11-27 | US, CN, DE | [O] |
| [WO1991020054](https://patents.google.com/patent/WO1991020054A1/en) | Imaging Technology Inc. | Patterned part inspection: per-pixel variability image | 1990-06-14 | Ceased | WO | [O] |
| [US20040066962](https://patents.google.com/patent/US20040066962) | (not opened) | Defect inspection with normalised difference histogram | n/a | application | n/a | [S] |
| [US20120070040](https://patents.google.com/patent/US20120070040) | (not opened) | Automated inspection of a printed image (SSIM against RIP image) | n/a | application | US, CN | [S] |
| [US7519222](https://patents.google.com/patent/US7519222B2/en) | (not opened) | Print defect detection by block activity | n/a | not checked | US | [S] |
| [US11562479](https://patents.google.com/patent/US11562479) | Seiko Epson | Virtual good image, user marks allowable defect, model rebuilt | 2019-03 | Active, about 2040 | US | [P] |

**US5850466 steps (claim 1 paraphrase).** Acquire sample grey images; measure each sample's sub-pixel
translation and a geometric characteristic; build templates from samples with similar characteristics; store
them in an **indexed multi-resolution array**; for a test image measure the same and **retrieve the matching
template** by index, compare. No run-time resampling. Sub-pixel accuracy about 1/8 px (nine templates give
1/6 px); sixteen 512 x 512 templates about 4 MB; residual whole-pixel error at most half a pixel;
correlation templates at least 40 x 40; threshold image **V = t1 x S + t2** (S is the standard-deviation
image).
**Adopt how:** for label and carton print inspection: build the **mean image and a standard-deviation image**
from 20 to 50 good parts, store the mean at a few sub-pixel phases (2 x 2 bins), pick by the measured shift,
flag pixels where |test - mean| exceeds t1 x S + t2. The HALCON variation model is the same idea
(no patent found). Gain: High for label print defects. Effort 2 pw. Targets false reject at most 0.5 percent
and escape at most 0.1 percent.

**WO1991020054 steps.** Align by correlation at 49 locations (5 x 5 fiducial, 11 x 11 patch), fit a surface to the 3 x 3 correlation
values for a fractional shift (about 1/6 px accuracy), shift by **bicubic convolution (k = -0.5)**, compare with the reference,
threshold at **3 standard deviations** of a per-pixel variability image, erode up to 8 x 8, then apply
size and shape tolerances. Google shows: ceased. Adopt as the exact recipe for the variation model; the erosion
kills isolated noise pixels so one speck is not a defect. Effort inside the 2 pw above.

**US8971663 steps (claim 1).** (1) register runtime images with models trained on canonical **layer masks**
to get poses; (2) warp the masks; (3) compute combination masks from warped mask combinations; (4) estimate
intensities in the combination regions; (5) blend into one synthetic golden template. In-mask value 255
(or above 235), out-of-mask above 0 (or above 10); transition options min, mean or max (40 and 60 give 40,
50 or 60).
**Adopt how:** labels with two to four print layers (base colour, text, hologram, varnish) where each layer
shifts a little. Build one golden image per part from layer masks so a shifted layer is not a false defect.
Effort 2 pw after the variation model. Gain: Med. (Active patent; the owner's statement of 2026-10-30, section 0, applies.)

## 12. Colour (L1, L3, L4)

| Patent | Owner | Title | Status | Tag |
| --- | --- | --- | --- | --- |
| [JPH09203664](https://patents.google.com/patent/JPH09203664A/en) | (not opened) | Hue inspection: mean Lab colour of the area against a reference, frequency distribution compare | old | [S] |
| [JP2016006416](https://patents.google.com/patent/JP2016006416A/en) | (not opened) | Colouring evaluation from Lab chromaticity histograms, overlap and delta E | not checked | [S] |
| [WO2005099916](https://patents.google.com/patent/WO2005099916A1/en) | (not opened) | Colour recognition from average hue under several intensities | not checked | [S] |

No Cognex, Keyence or Omron colour patent was identified. The measured need (cap colour, foil print colour,
label colour check) is covered by public methods: mean Lab in a region with delta E 2000, a Lab histogram
overlap score, white balance from a reference patch. **Adopt how:** a **Colour tool** with a reference-patch
white balance each shift, delta E per region and a Lab histogram overlap as the second check. L1 and L4.
Status Missing (INDUSTRY-METHODS 15.2). Effort 2 pw. Needs a colour camera and a daylight strobe
(section 19).

## 13. Deep learning, anomaly detection, active learning (L3, L5)

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US12361100](https://patents.google.com/patent/US12361100B1/en) | Cognex | Runtime image as candidate, suggested category, model update | 2019-12-09 | Active, 2043-05-23 | US | [P] RDV 5 |
| [US12315277](https://patents.google.com/patent/US12315277B2/en) (+ WO2023075825, EP4427204, CN118511204, JP2024-541040, KR2024-0125918) | Cognex | Candidate box offers unlabeled images for incremental training | 2019-12-09 | Active, 2040-12-09 | US, WO, EP, CN, JP, KR | [P] |
| [US11568629](https://patents.google.com/patent/US11568629B2/en), US12147895 | Cognex | Pattern finder plus neural classifier | 2018-06-06 | Active, 2039-08-14 | US, CN, JP, KR, DE | [P] |
| [US10937141](https://patents.google.com/patent/US10937141B2/en) | SuaLab (Cognex Ireland) | Siamese master versus test comparison | 2017-10-13 | Active, 2039-04-26 | US | [P] |
| [US11017259](https://patents.google.com/patent/US11017259B2/en) | Utechzone | Synthetic defect pairs plus edge image | 2018-06-29 | Active, 2039-10-23 | US | [P] |
| [US12182721](https://patents.google.com/patent/US12182721B2/en) | Yissum (Hebrew Univ.) | Patch-feature k-nearest-neighbour anomaly (SPADE, PatchCore) | 2020-03-25 | Active, about 2042-02-25 | US | [P] research-ai |
| [US12548135](https://patents.google.com/patent/US12548135B2/en), [EP4145401](https://patents.google.com/patent/EP4145401A1/en) | MVTec | Encoder-decoder plus regression net (student-teacher) | 2021-09-06 | US active about 2043; EP pending | US, JP, CN, KR, EP | [P] |
| [US11367225](https://patents.google.com/patent/US11367225) | Keyence | Rule-based check first, AI only on the unclear rest | 2019-05-16 | Active, about 2040-12 | US | [P] |
| [US12482244](https://patents.google.com/patent/US12482244B2/en) | 3M | Select a subset of images to label, train | 2020-12-28 | Active, about 2042-11 | US | [P] |
| [US11836909](https://patents.google.com/patent/US11836909B2/en) | Mellanox | Auto-labels from similar images, retrain | 2022-01-26 | Active, about 2042-03 | US | [P] |
| [US10997711](https://patents.google.com/patent/US10997711B2/en) | Fanuc | Normal reference paired with new item for a classifier | 2017-11-17 | Expired (fee) | US | [P] |
| Keyence [US11042976](https://patents.google.com/patent/US11042976), [US11042977](https://patents.google.com/patent/US11042977) | Keyence | Additional learning in run mode | n/a | not opened | US | [S] |
| Omron "AI Fine Matching" | Omron | Patent pending in Japan (product PDF) | 2020 | unknown | JP | [S], no number found |
| [US10810465](https://patents.google.com/patent/US10810465B2/en), [US11176454](https://patents.google.com/patent/US11176454), [US12210930](https://patents.google.com/patent/US12210930) | Datalogic, SICK, Code Corp | Classifier-reject, region classifier, decodability predictor | 2018 to 2022 | Active | US (SICK: EP, JP, KR, CN) | [P] |

**Adopt how (under the owner's statement of 2026-10-30, section 0).**

| Method | Patent | What we build | Tool | Effort |
| --- | --- | --- | --- | --- |
| Patch-memory anomaly (kNN of patch features to normal ones, threshold) | US12182721 | PatchCore-style Anomaly tool on cap, foil and carton good images, coreset 1 to 10 percent, a few dozen good images | AI Inspect | 4 pw |
| Student-teacher disagreement | US12548135 | EfficientAD-style fast second anomaly head | AI Inspect | 3 pw |
| Siamese master / test | US10937141 | Image-compare head for products whose background or pattern changes often | AI Inspect | 3 pw |
| Finder plus classifier | US11568629 | The classifier picks which locator templates run, or vets the finder's matches (reduces false finds) | Locate, AI locator | 2 pw |
| Rule first, AI on the unclear rest | US11367225 | Cascade: the classical check decides clear pass and clear fail, the net sees only the middle band; **a bad part still fails if either says fail** | Inspect | 1 pw |
| Operator marks an allowable defect | US11562479 | "Accept this variation" button that adds the image to the good set after a gate | NG review | 1 pw |
| Candidate-box teaching, sorted by confidence | US12315277, US12361100 | Our NG strip offers uncertain images for relabel; order by confidence or diversity; **retraining only after the operator confirms** | Auto-label (L5) | 2 pw |
| Auto-label similar images | US11836909, US12482244 | "Pre-label" run on request | Auto-label | 1.5 pw |
| Synthetic defect pairs | US11017259 | Already in `violations.py`; add the edge-image variant | Training | 1 pw |

Line safety stays above all of these: a net may add a fail but may never overrule a classical fail into a
pass. The "decodability predictor" of US12210930 (discard images predicted undecodable) is **not** adopted
because a discarded image is an unread code on a safety line; we adopt only the idea of ranking attempts.

## 14. Auto-setup, auto-tune, auto-learn, auto-label (L5)

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US8126253](https://patents.google.com/patent/US8126253B2/en) | Cognex | Automatically determining machine vision tool parameters from marked sample results | 2005-11-12 | **Expired** (fee), 2029-02-07 estimate | US, EP, JP | [O] |
| [US7545949](https://patents.google.com/patent/US7545949) | Cognex | Setting vision detector parameters from production-line information | n/a | not checked | US | [S] |
| [US7751625](https://patents.google.com/patent/US7751625B2/en), [US7953290](https://patents.google.com/patent/US7953290B2/en), [EP4636707](https://patents.google.com/patent/EP4636707A1/en) | MVTec | Parameter determination from the image | 2006 / 2024 | see 4.2 | US, EP | [O] / [P] |
| [JP6586201B2](https://patents.google.com/patent/JP6586201B2/en) | Keyence | Tuning unit reruns a reading test and checks the success rate (parent family) | 2014 / 2018 | Active, 2034-07-31 | JP | [O] |
| [WO2025137512](https://patents.google.com/patent/WO2025137512A1/en) | Cognex | ML quality metric drives a closed loop over focus, gain, exposure, light banks, polarisation, HDR, gamma | 2023-12-22 | Pending | US, WO | [P] |
| [US20250315980](https://patents.google.com/patent/US20250315980A1/en) | (not opened) | Commissioning a machine vision system | n/a | application | US | [S] |
| [US9600703](https://patents.google.com/patent/US9600703B2/en) | Cognex | Settings sorted by points and EMA | 2013-03 | Active, about 2033-03 | US, DE, JP | [P] |

**US8126253 steps (description).** The user marks the desired results on **sample images**; the tool runs
candidate parameter combinations; each output is compared with the marks (boundary tolerance 3 px; area
difference within 25 percent of the larger; count difference 2.5 percent); a heuristic picks the best
combination. Initial sample of up to 64 image-result pairs, top 25 percent kept after each culling step,
secondary search multipliers about 1.09 and 0.92. Typical set-up up to 60 s, retraining 5 to 10 minutes.
**Adopt how:** the **Auto-tune by marked examples** for Blob, Inspect and OCR segmentation. Operator marks
good and bad samples once, the tool searches parameter grids in rounds that keep the top quarter. Google shows:
expired. L5. Effort 2.5 pw (a generic tuner plus per-tool scoring).

**WO2025137512 (pending).** Pre-trained ROI finder plus a learned quality metric (linear, random forest,
SVM or net on K global, N regional and M decoder features) drives the loop. **Adopt how:** replace "did it
decode?" by a **continuous quality score** so tuning works when no decode succeeds yet. Effort 2 pw.

## 15. Conveyor, multi-camera and timing (L0, L2, L6)

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US12067449](https://patents.google.com/patent/US12067449B2/en), [US12596897](https://patents.google.com/patent/US12596897) | Zebra | 4D barcode / indicium mapping, duplicate decode removal across cameras | 2022-08-31 | Active, 2042-08-31 | US, WO, GB, JP | [O] (US12067449) |
| [US20180157882](https://patents.google.com/patent/US20180157882A1/en) | Cognex | Tracking optical codes: fragments | 2014-10-09 | Active, 2034-10-09 | US | [O] |
| [US10325129](https://patents.google.com/patent/US10325129B2/en) | Cognex | Candidate ordering on a conveyor | 2011-11 | Active, about 2031-12 | US | [P] |
| [US8825200](https://patents.google.com/patent/US8825200B2/en) | (not opened) | Tracking items, one camera per zone | n/a | not checked | US | [S] |
| [US12450453](https://patents.google.com/patent/US12450453) | (not opened) | Encoder triggers all cameras on a fixed pulse count | n/a | not checked | US | [S] |
| [US9298963](https://patents.google.com/patent/US9298963B2/en), [US9721134](https://patents.google.com/patent/US9721134B2/en), [US10210369](https://patents.google.com/patent/US10210369B2/en) | Cognex | Trigger-to-decode | 2010-12 | Active, about 2034-07 | US, CN, DE | [P] |
| [US8146821](https://patents.google.com/patent/US8146821B2/en) | Symbol | Sequenced lights of several cameras | 2009-04-02 | Active, 2030 | US, EP, CN, WO | [O] |

**US12067449 steps (summary).** Decode a barcode from 2D image data; identify an object from 3D image data;
link the two as reference data; with later 3D and 2D captures, check against a **4D projection** (position
plus time, using belt motion) of that reference to find **duplicate decodes** and **missed scans**; overlap
thresholds from at least 50 up to 90 percent. **Adopt how:** two cameras reading the same case at different
positions (our 225 parts per minute line) need one record per case. Use the encoder count (or the PLC's belt
counter) as the time axis, project each decoded code box to a common belt coordinate, and merge those within
one case box. Status Partial (`DuplicateMemory.cs` finds repeats of code content only). Effort 2 pw.
Gain: removes the "second camera sees the same case" duplicates and a missed-case hole in the PLC result.

**US20180157882 and US10325129.** Section 7.2. **US12450453 (snippet)** gives the idea of encoder-pulse
triggering of every camera, which on our GigE line is best done with a scheduled action command and PTP
(INDUSTRY-METHODS section 3).

## 16. Aggregation, multi-code cases and serialisation (L3, L4, L6)

| Patent | Owner | Title | Priority | Status / expiry | Territories | Tag |
| --- | --- | --- | --- | --- | --- | --- |
| [US20210182520](https://patents.google.com/patent/US20210182520A1/en) (= US11354528) | SICK | Reading a plurality of codes: merge views, count objects, compare with codes read, aggregation code | 2019-12-12 | Active (granted 2022-06-07) | US, EP, DE, ES | [O] |
| [US11030431](https://patents.google.com/patent/US11030431B2/en) | Code Corp | Efficiency reading multiple barcodes: priority code, ROI, discard early | 2018-07-16 | Active, 2039-07-16 | not shown | [O] |
| [US8469275](https://patents.google.com/patent/US8469275) | Metaform | Multiple barcode detection: repeat suppression by distance | 2009-04-20 | Active, 2030-04-19 | US | [O] |
| [US9233400](https://patents.google.com/patent/US9233400) | Covectra | Aggregating serialized goods: image authentication, divert to aggregation or reject | 2013-06-11 | Active, 2034-06-11 | US, WO | [O] |
| [US11216222](https://patents.google.com/patent/US11216222) | (not opened) | Printing method for serialised information; cameras at several points | n/a | not checked | US | [S] |
| [US9208367](https://patents.google.com/patent/US9208367) | Honeywell | Display decoded codes, user sets send order | 2012 | Active, about 2034 | US | [P] |
| [US11922269](https://patents.google.com/patent/US11922269) | Bayer | Codes introduced into surfaces and read | 2022 | Active, about 2042 | US | [P] |
| [EP3040906](https://patents.google.com/patent/EP3040906A1/en), [US10152622](https://patents.google.com/patent/US10152622) | (not opened) | Visual feedback for code readers: setup file keeps expected codes and symbologies | n/a | not checked | EP, US | [S] |
| [DE102024123557](https://patents.google.com/patent/DE102024123557A1/en) | (not opened) | Automatic object identification and serialisation | 2024 | application | DE | [S] |
| [US9171280](https://patents.google.com/patent/US9171280B2/en), [US20050261935](https://patents.google.com/patent/US20050261935A1/en) | (not opened) | Medication tracking; pharmaceutical product tracking (parent-child) | n/a | not checked | US | [S] |

**US20210182520 (SICK), claim 1 elements (apparatus).** Camera head records an image of adjacent objects;
control unit localises code zones and reads them; display marks read codes or objects; a hand reader picks up
what the camera missed. Dependent features: several camera heads, several illumination modules, **images under
different lighting directions merged**, rectangle-based **object counting** compared with codes read,
duplicate detection by serial number, aggregation-code mode.
**Adopt how:** for 12 to 96 child codes per case. (1) Count code-sized rectangles from the locator (the
`slots.cpp` lattice does this) and compare with the number decoded and with the product master count
(12, 24, 45, 48, 96); mismatch means NG with the missing slot highlighted. (2) Merge a second image under
another light direction only for the missing slots (not for the whole case). (3) Keep a manual-recover route:
operator hand scan against the missing slot, flagged in the audit log. Status Partial (`slots.cpp`,
`ProductMaster.cs`, `DuplicateMemory.cs`). Effort 1.5 pw.

**US11030431 (Code Corp).** Search for a priority barcode first, inside a region of interest; use the result
to decide whether to look for the second; **discard the image early** if the first is absent or the second
unlikely to decode. Parameters: ROI, symbology, polarity, size, relative position, orientation, blur
threshold, maximum search time. **Adopt how:** the **ROI list from the product master** (expected position
of the SSCC label relative to the case code grid). Early exit saves time in the 500 ms case budget. Discarding
an image must never be treated as a pass: the case still needs its full count (line safety). Effort 1 pw.

**US9233400 (Covectra).** Conveyed item is imaged for a unique signature, authenticity checked, authentic
items diverted to an aggregation line and the rest to rejection; adjustable conveyor walls, paddle width and
spacing; example 30 to 120 products per minute. A mechanical aggregation patent; our system does aggregation
by vision on the finished case, so the method to adopt is **reject branch by item authenticity** (code
present, duplicate, not-in-order-list). Already Have in principle (duplicate = orange, 10,000 child and 1,000
case codes remembered).

**No** patent was found for vendor aggregation software (Systech UniSeries, Antares Vision, Optel, Korber
Medipak, Seidenader). These tools are described only in trade pages
([Systech case aggregation ST16P](https://www.cphi-online.com/product/st16p-case-aggregation-station-2/),
[Antares Vision aggregation](https://antaresvisiongroup.com/lifescience/pharmaceutical-serialization-aggregation/)).
Pack-by-layer guidance and print-and-apply are the visible features; neither is patent-tied in what we found.

## 17. Reader architecture, speed and multi-core (L2, L3)

| Patent | Owner | Method | Status | Tag |
| --- | --- | --- | --- | --- |
| US7984854 | Cognex | Fast decoder and robust DPM decoder as parallel threads, first success wins; CPU split 17.6 / 82.4 percent; timeouts 85 / 850 ms | Active, about 2027-02 | [P] |
| US8794521, US10154177 | Cognex | Second core only if "probably decodable" (finder found) | Active, about 2033-01 | [P] |
| US11295100 | Keyence | Dispatcher core plus eight decoders, one image each | Active, 2041-02-22 | [O] |
| US10366307 | Omron | SIMD data layout for coarse-to-fine | Active, 2036-12-20 | [O] |
| US9147095 | NCR | Pyramid and ROI-only distortion correction | Active, 2033-12-10 | [O] |

All five support the same rule for us: **cheap first, expensive only where a code or text was found,
and always inside the case time budget**. They are about speed; accuracy follows because the 48 DM miss (64.2
percent) is mostly over the 500 ms budget. Effort for the scheduler is in item 21 of section 24.

## 18. Ranked first by tool family: what each family gives

| Tool family | Best patented methods to adopt | Gain | Effort total |
| --- | --- | --- | --- |
| L0 Acquire | JP6586201B2 polarisation, US6352204 low angle, US8011583 recipe list, US9413981 probe, US8146821 time slicing, US9518931 photometric stereo | High for glare and DPM | 8 pw |
| L1 Correct | US10607047 tone map, US8150163 noise gate and accumulation, US8867857 Wiener, US8345979 deformation, US9734419 drift check | High for LOW_CONTRAST, BLUR | 8 pw |
| L2 Locate | US7016539 + US7062093 edge match, US6324299 sub-models, US7239929 components, US8355590 + US10366307 fast coarse | Med to High | 11 pw |
| L3 Code read | US11151346, US10956697, US9734376, US9104932, US9361499 | High | 12 pw |
| L3 Measure | US6408109, US6690842, US6748110, US6993177 | Med | 5 pw |
| L3 OCR and OCV | US9317767, US8036463, US5212741, US8027802, US10740582 | High for CIJ and OCV | 8 pw |
| L4 Inspect | US5850466, WO1991020054, US8971663, US11367225 | High for labels | 6 pw |
| L5 Learn | US8126253, US7751625, WO2025137512, US9600703, JP6586201B2 | Med | 7 pw |
| L6 and line | US12067449, US20180157882, US20210182520, US11030431 | Med to High on the two-camera line | 5.5 pw |
| AI | US12182721, US12548135, US10937141, US11568629, US11854163 | Med | 14 pw |

## 19. Hardware to buy where a patent method needs it

| Hardware | Why | Patent method | Cost class |
| --- | --- | --- | --- |
| Crossed polariser film on lens and one polarised ring light, one unpolarised | Shrink-wrap and cap film glare | JP6586201B2, Cognex guidance | Low |
| Low-angle bar light, 10 to 18 degrees, or a four-direction ring | Laser, dot-peen and embossed text | US6352204, US9518931 | Low to medium |
| Strobe controller with camera-sequenced outputs | Two-camera time slicing; photometric stereo at belt speed | US8146821, US9518931 | Medium |
| Second light channel per camera (dark field) | Bright / dark field switching | US7604174, US10650205 | Medium |
| Sony Polarsens polarisation sensor camera (4-direction on chip) | Glare without a filter | INDUSTRY-METHODS section 3 | Medium |
| Colour camera and daylight strobe | Colour tool | Section 12 | Medium |
| Stereo or line-scan depth head | Dot-peen on rough metal only | WO2016103285 | High, optional |
| Calibration plate (checkerboard or line-intersection) | Calibration and drift check | US6137893, US9734419 | Low |

## 20. Tools built from public methods (no patent found)

| Tool | Public method and where |
| --- | --- |
| PatMax RedLine (fast coarse-to-fine) | No patent found. Bit-image OR reduction (US8355590) and SIMD layout (US10366307) give the same speed route |
| PatFlex | No patent found. US8345979 is the nearest published method (inference) |
| OCRMax / OCVMax | No patent found. Segmenter plus classifier per Cognex help pages; our `ocr/` tree |
| HALCON variation model | No patent found. US5850466 plus WO1991020054 are the same statistics |
| Flat field | Textbook per-pixel reference. Patents are old or off-topic |
| Blob | Textbook connected components, plus US6748110 snap |
| Colour | Lab mean and delta E; histogram overlap |
| Dynamsoft reader | No patent found in two searches |
| Domino and Markem-Imaje vision | No vision-verification patent found; their CIJ patents are about ink and drop control |

## 21. Snippet-only entries (page not opened)

US5047861, US5818525, US8259179, US20050041806 (flat field); US7616256, US20100157078 (HDR); US20120067955,
US9262661 (lighting and polarisation); US9683943, US10489900, US9778203 (Keyence photometric stereo
siblings); US6658145, US6856698, US6175652, US6175644, US6571006, US5845007 (Cognex siblings); US8619144,
WO2018005561, US10032273, US9393694 (calibration); US9098764, US8439260 (1D); US20040066962,
US20120070040, US7519222 (print inspection); JPH09203664, JP2016006416, WO2005099916 (colour); EP0539854,
EP0498873 (dot-matrix OCR); US8825200, US12450453, US12596897 (tracking); US11216222, EP3040906,
US10152622, DE102024123557, US9171280, US20050261935 (aggregation); US7545949, US20250315980
(auto-setup); US10896303 (CMOS exposure); US11042976, US11042977 (Keyence run-mode learning); US20170111523,
US7347376, US10559075 (verifiers); US10546160 (Justia page only). The assignee of many is unknown.

## 22. Territories, India and expiry watch

| Point | Fact |
| --- | --- |
| IN members | None listed on any page opened. Google's IN coverage is patchy (earlier research: 0 IN hits for Datalogic, Zebra, Keyence, SICK, Cognex). InPASS was not searched (CAPTCHA). |
| Owner's statement | 2026-10-30, after advice from his lawyer: "we can use all patents and process in our library". No legal opinion or freedom-to-operate claim is made here; the scope (territories, export, active US / EU / JP patents) is for the owner to confirm, and until then the territory notes in PATENTS.md and the switches in the code stay as written. |
| Expiring soon (Google) | US7412106 Cognex dot-peen parent, 2026-12-23; US7984854 Cognex multiplexed decoders, about 2027-02; EP3109792 Cognex multi-frame, about 2028-11 |
| Active until 2030 to 2035 | US8596540 (2030), US8011583 (2030), US8469275 (2030), US11151346 (2034), US10956697 (2035), US9734376 (2033), US9104932 (2033) |
| Active until 2036 to 2040 | US10607047 (2038) |
| Active beyond 2040 | US11854163 (2042), US12039400 (2041), US12067449 (2042), US12548135 (2043), US12361100 (2043), US12182721 (2042) |
| Pending | WO2026011064, US20260010746 (Cognex DL decoding), WO2025137512 (Cognex DL tuning), EP4636707 (MVTec), EP4145401 (MVTec) |

## 23. Our engine against each adopted patent (status index)

| Method | Patent | Our status | File or note |
| --- | --- | --- | --- |
| Erasure marking | US6685095 | Have | `softDecodeDm`, `rsDecodeDm` |
| Dot chain | US7965899 | Partial | `CR_STEP_DOTS` variant 0 |
| No-fixed-pattern lattice | US10956697 | Partial | `lattice.cpp` |
| Under-resolved | US11151346 | Partial, on by default | `underres.cpp`, `CR_UNDERRES` ON (`engine/CMakeLists.txt:22`); constraint-graph pruning and routing below 1.5 px/module missing |
| Multi-frame voting | US9734376 | Have | `multiframe.cpp` |
| PSF model | US11151346 shape | Have | `psf.cpp` |
| Local tone map | US10607047 | Partial | `CR_STEP_FLAT` fixed tiles |
| Learned step order | US9600703 | Partial | `TuningAgent.cs`, learned state |
| Calibrator | US9413981, US7604174 | Have | `CalibrationSearch.cs` |
| AI enhancer | US11854163 | Have | `ml/enhance.py`, `enhancer.onnx` |
| Diagnose | US9552506 | Have | `diagnose.cpp` |
| Station slot lattice | (classic) | Have | `slots.cpp` |
| Ideal-render difference | US8027802 | Missing | |
| OCR auto segmentation | US9317767 | Partial | line parameters estimated per image (PRs #80, #92, #107, #132); tuning against the known text missing |
| Edge-match locator | US7062093, US7016539 | Missing | `locate_tool.cpp` has grey correlation |
| Caliper, sub-pixel edge | US6408109 | Missing | |
| Variation model | US5850466 | Missing | |
| Calibration drift check | US9734419 | Missing | |
| 4D duplicate merge | US12067449 | Partial | `DuplicateMemory.cs` (content only) |
| Polarisation modes | JP6586201B2 | Missing | |
| Photometric stereo | US9518931 | Missing | |
| Anomaly (kNN, student-teacher) | US12182721, US12548135 | Missing | |

## 24. The ranked 25: patented methods with the largest expected accuracy gain

Ranking weighs expected gain on the measured misses (LOW_CONTRAST, BLUR, TOO_SMALL, NO_FINDER, COVERED; CIJ
72.9 percent; OCV 69 percent true pass; 48 DM 64.2 percent; shrink-wrap QR 60.3 percent), then effort.
Gains are my estimates from where the failures sit, not vendor numbers. Every row is under the owner's
statement of 2026-10-30 (section 0), with its scope still to be confirmed.

| # | Patent and method | Owner, status | Tool and layer | Targets | Effort (pw) |
| --- | --- | --- | --- | --- | --- |
| 1 | [US11151346](https://patents.google.com/patent/US11151346B2/en): complete the under-resolved solver (Partial, on by default; missing: 8-way constraint graph pruning and the route below 1.5 px/module) | Cognex, active 2034 | Code Reader, L3 | TOO_SMALL, shrink-wrap QR 60.3 percent | 3 |
| 2 | [US10956697](https://patents.google.com/patent/US10956697B2/en) / US11455482: masked inverse-FFT centre map, 5 x 5 affine seed, polynomial, out-of-image erasures | Cognex, active 2035 | Code Reader (DM), L3 | NO_FINDER, COVERED, 48 DM 64.2 percent | 2 |
| 3 | [US10607047](https://patents.google.com/patent/US10607047B2/en): module-scaled local tone map | Cognex, active 2038 | Correct, L1 | LOW_CONTRAST | 1.5 |
| 4 | [JP6586201B2](https://patents.google.com/patent/JP6586201B2/en): crossed-polariser light sets as a tuned light mode | Keyence, active 2034 (JP) | Acquire, L0 | glare on shrink-wrap and caps | 1 plus lights |
| 5 | [US9317767](https://patents.google.com/patent/US9317767): OCR segmentation parameters tuned on known text | Cognex, active 2033 | OCR auto-teach, L5 | CIJ 72.9 percent | 2 |
| 6 | [US8027802](https://patents.google.com/patent/US8027802): render expected text and difference it | Cognex, expired | OCV, L4 | OCV true pass 69 percent | 1.5 |
| 7 | [US9104932](https://patents.google.com/patent/US9104932B2/en): bracket exposures per trigger, register and fuse | Cognex, active 2033 | Acquire, L0 and L1 | LOW_CONTRAST, glare | 2 |
| 8 | [US8150163](https://patents.google.com/patent/US8150163): noise-gated multi-frame accumulation | Scanbuy, expired | Correct, L1 | BLUR, TOO_SMALL | 1.5 |
| 9 | [US5850466](https://patents.google.com/patent/US5850466) and [WO1991020054](https://patents.google.com/patent/WO1991020054A1/en): variation model, mean plus standard-deviation image, 3 sigma | Cognex expired, ceased | Inspect (labels), L4 | label print defects, false reject 0.5 percent | 2 |
| 10 | [US7062093](https://patents.google.com/patent/US7062093B2/en) with US7016539: edge-direction match, pyramid, sub-pixel | MVTec, Cognex, expired | Locate, L2 | 0.05 px, 100 percent find | 3 |
| 11 | [US8036463](https://patents.google.com/patent/US8036463B2/en): background-subtracted projection profiles | Keyence, expired | OCR segmentation, L1 | laser and cap text on curved parts | 1 |
| 12 | [US7965899](https://patents.google.com/patent/US7965899B2/en) and [US5212741](https://patents.google.com/patent/US5212741A/en): dot chain, resample to 5 to 10 px/module, per-region switch | Cognex, Kodak, expired | Correct, L1 | CIJ and dot-printed DM | 1 |
| 13 | [US8345979](https://patents.google.com/patent/US8345979B2/en): deformation map for bent labels and film | Cognex, expired | Locate and rectify, L1, L2 | curved labels, wrinkled film | 3 |
| 14 | [US6352204](https://patents.google.com/patent/US6352204) and [US8011583](https://patents.google.com/patent/US8011583): low-angle light 10 to 18 degrees, light recipe list per mark | Omron, expired and active | Acquire, L0 and L5 | laser and dot-peen codes | 1 plus lights |
| 15 | [US10740582](https://patents.google.com/patent/US10740582B1/en): ISO 29158 metric set for text and logos | Datalogic, active 2039 | Verify, L4 | graded print quality of text | 2 |
| 16 | [US20210182520](https://patents.google.com/patent/US20210182520A1/en) with US8469275: object count against codes read, second light only for missing slots | SICK, Metaform | Aggregation, L4 | COVERED, case count safety | 1.5 |
| 17 | [US12067449](https://patents.google.com/patent/US12067449B2/en) and US20180157882: 4D duplicate merge, fragment tracking | Zebra, Cognex, active | Two-camera line, L2, L6 | duplicates and missed cases at 225 ppm | 2 |
| 18 | [US6408109](https://patents.google.com/patent/US6408109B1/en) and US6690842: sub-pixel edge finder and caliper | Cognex, expired | Measure, L3 | 0.1 px at 3 sigma | 2 |
| 19 | [US9734419](https://patents.google.com/patent/US9734419B1/en): calibration validation, mean plus 3 sigma | Cognex, active 2031 | Calibrate, L1 and L4 | drift, safety lock | 1 |
| 20 | [US9361499](https://patents.google.com/patent/US9361499B2/en) with Hotbars blur model: 1D evidence integration, 1.5 margin | Cognex, active | 1D Reader, L3 | side labels at line speed | 2 |
| 21 | [US11295100](https://patents.google.com/patent/US11295100B2/en), US9147095, US9036929: dispatcher with time budget, pyramid search, ROI-only correction | Keyence, NCR, Cognex | Scheduler, L2 | 48 DM over 500 ms | 2.5 |
| 22 | [US8126253](https://patents.google.com/patent/US8126253B2/en) and [US7751625](https://patents.google.com/patent/US7751625B2/en): auto-tune by marked examples, auto-learn Locate parameters | Cognex expired, MVTec active | Auto-tune, L5 | set-up accuracy, fewer wrong tolerances | 3 |
| 23 | [US12182721](https://patents.google.com/patent/US12182721B2/en) and [US12548135](https://patents.google.com/patent/US12548135B2/en): PatchCore and student-teacher anomaly | Yissum, MVTec, active | AI Inspect, L3 | cap, foil and carton defects, escape 0.1 percent | 4 |
| 24 | [US9518931](https://patents.google.com/patent/US9518931): four-direction photometric stereo with belt interpolation | Keyence, active 2035 | Relief tool, L0 and L1 | embossed text 89.4 percent, dents | 4 plus lights |
| 25 | [US9600703](https://patents.google.com/patent/US9600703B2/en) and [US9413981](https://patents.google.com/patent/US9413981B2/en): points and EMA step order, multi-gain probe | Cognex, active | Calibrator, L5 | faster set-up, fewer lost cases | 1.5 |

**Total of the 25:** about 57 pw (hardware extra). Items 1 to 8 are about 14 pw and, on the numbers in the
brief, should address most of the 145 missed codes and the CIJ and OCV gaps. Items 1, 2, 3, 5 and 6 are the
first sprint.

Next in line (not ranked): US7239929 components (2 pw), US6324299 sub-models (1.5 pw), US9946947 saddle
points for DotCode (1.5 pw), US7181066 tile locator tie-break (1 pw), US8355590 / US10366307 fast coarse
search (2 pw), US8971663 multi-layer golden template (2 pw), US11854163 retraining at 4 to 6 px/module
(1 pw), US8596540 two-neighbour context for 1D (1.5 pw), US10235547 erasure capacity rule (0.5 pw).

## 25. Search log (what was searched and what came back empty)

Google Patents pages were opened by number. WebSearch was used for discovery only. Queries that returned no
usable patent: Cognex OCRMax / OCVMax numbers; Keyence OCR (one hit, US8036463); Omron OCR; Domino and
Markem-Imaje vision verification; Systech, Antares, Optel, Korber aggregation (one unrelated hit);
Dynamsoft (none); MVTec calibration and OCR (none); Keyence deep-learning defect classification (none
direct); Cognex PatMax RedLine and PatFlex numbers (none); colour inspection by Keyence, Omron or Cognex
(none); flat-field machine-vision patents (old or off-topic only).

## Not opened / limits

- **Claim text.** The fetch tool read only the first 100,000 characters of each page. Most pages are longer
  (up to 502,013 characters), and claims were usually not in the part read. "Claim 1" is stated only
  where it was seen. Elsewhere, "steps (summary)" are from the abstract and description. Before building a
  method, read the claims on Google Patents for the exact limits.
- **[S] entries** (section 21) were not opened; owners are unknown for many.
- **[P] entries** were opened in earlier research and carried over from PATENTS.md,
  RESEARCH-COGNEX-PATENTS.md, INDUSTRY-METHODS.md section 16, RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md and the
  raw research files; their numbers were not re-checked today.
- **Status and expiry** are Google's estimates. Several are inconsistent across sources (US7965899,
  US9946947, US7953290 shown "expired" in one place and US7751625 "active" for the sibling family; Keyence
  US8401305 and US8406527 "expired, fee related"; Honeywell US8702003 dates). The status is information
  that matters until the owner confirms the scope of his statement (section 0).
- **Territories.** Google's family lists were short or absent on many pages. India was not confirmed absent
  by InPASS. Japan-only JP6586201B2 and the Zebra / Cognex US families were read as listed.
- **Not found:** patents for PatMax RedLine, PatFlex, OCRMax, OCVMax, the HALCON variation model,
  Dynamsoft, vendor aggregation software and Domino / Markem-Imaje vision (section 20).
- **Effort and gain numbers** are my estimates from the failure reasons in the brief; no experiment was run.
  Nothing was built or run for this chapter.
- **Counts** in section 1 were made by hand and are estimates.
