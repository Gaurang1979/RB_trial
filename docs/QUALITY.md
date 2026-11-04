# Code quality parameters and how the reader responds

Internal reference: which quality parameter makes a code hard to read, and what the reader
(engine and AI) does about it. Not a customer verification feature: grades from a line
camera are process-control indications, not ISO-certified verification, which needs a
calibrated verifier and ISO lighting.

Grades follow ISO practice: A (4) best ... F (0) fail. The engine grades every decoded 2D
code when `grade_quality` is on (`engine/src/quality.cpp`, `bench --quality codes.csv`).

**Grading calibration (2026-10-29, `engine/src/calibration.cpp`, Calibrate page "Grading
calibration").** As NI Vision and DALSA verification tools do: (1) a white card under the line's
lighting sets a flat field, so uneven light does not lower a grade; (2) a card with a dark and a
light patch of certified reflectance maps grey levels to reflectance %, the unit of the ISO
thresholds (2D modules are then sampled through the 0.8 X measuring aperture); (3) a chessboard on
the case top gives mm per pixel (and the lens distortion with three or more views), so the
X-dimension is reported in mm. Kept per product in `data/grading/`. With it the grades are
verifier-style; without it they stay camera-relative, as before. It is still not a certified
verifier: ISO lighting geometry (45 degrees, specified wavelength) is the line's, not the standard's.

**1D grading (`engine/src/grade1d.cpp`):** 10 scan reflectance profiles across the bars through a
0.8 X aperture; each scan graded on Rmin, SC, ECmin, MOD, defects (largest valley inside a space or
peak inside a bar), decodability (Code 128 reference decode) and decode; the symbol grade is the
mean of the scan grades, as ISO/IEC 15416 sets it.

## 1D (linear) codes: ISO/IEC 15416

Used for the parent case code (GS1-128 / SSCC, EAN-13) on the side camera.

| Parameter | Measures | A | B | C | D | F | Typical cause | Reader response |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Decode | Reference decode works | pass | | | | fail | any | multiple scan lines, rotation |
| Rmin / Rmax | Darkest bar ≤ 0.5 Rmax | pass | | | | fail | grey bars, dark substrate | exposure advice |
| Symbol contrast (SC) | Rmax − Rmin | ≥70% | ≥55% | ≥40% | ≥20% | <20% | faded print, low light | contrast stretch, exposure advice |
| Edge contrast (ECmin) | Weakest bar/space edge | ≥15% | | | | <15% | ink spread, blur | sharpen / deblur |
| Modulation (MOD) | ECmin / SC | ≥0.70 | ≥0.60 | ≥0.50 | ≥0.40 | <0.40 | blur, uneven ink | deblur, flat-field |
| Defects | Spots / voids in bars | ≤0.15 | ≤0.20 | ≤0.25 | ≤0.30 | >0.30 | print voids, dust | median filtering |
| Decodability | Bar width tolerance used | ≥0.62 | ≥0.50 | ≥0.37 | ≥0.25 | <0.25 | print growth, speed | scan-line averaging |
| Quiet zone | Clear space each side (≥10 X) | pass | | | | fail | text / label edge too close | ROI on the label |

## 2D codes: ISO/IEC 15415 (printed) and ISO/IEC 29158 (direct part marks)

Data Matrix (ECC 200) and QR Code. 29158 adds cell contrast and cell modulation for
marked or low-contrast surfaces (caps, metal, film).

| Parameter | Measures | A | B | C | D | F | Typical cause on the sample lines | Reader response |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Decode | Reference decode | pass | | | | fail | any of the below | the whole fix chain |
| Symbol contrast (SC) | Light − dark modules | ≥70% | ≥55% | ≥40% | ≥20% | <20% | under-exposure, film haze, faded print | stretch, CLAHE, enhancer; exposure advice |
| Modulation (MOD) | Each module's distance from the threshold | ≥0.50 | ≥0.40 | ≥0.30 | ≥0.20 | <0.20 | blur, uneven light inside the code, uneven ink | flat-field, deblur, enhancer |
| Reflectance margin (RM) | Same, modules on the wrong side count as 0 | ≥0.50 | ≥0.40 | ≥0.30 | ≥0.20 | <0.20 | half-printed modules, shadow edges | local threshold, enhancer |
| Fixed pattern damage (FPD) | QR: finders, separators, timing. DM: L and clock track | none | | | | severe | glare or foil over a finder, label edge through the L | enhancer (inpaints fixed patterns), libdmtx |
| Axial non-uniformity (AN) | X vs Y module pitch | ≤0.06 | ≤0.08 | ≤0.10 | ≤0.12 | >0.12 | oblique view, stretched print | perspective sampling (decoder) |
| Grid non-uniformity (GN) | Grid points vs ideal grid | ≤0.38 | ≤0.50 | ≤0.63 | ≤0.75 | >0.75 | wrinkled labels under film, curved caps / cans | alignment patterns; flexible grid (planned) |
| Unused error correction (UEC) | Error correction left after decoding | ≥0.62 | ≥0.50 | ≥0.37 | ≥0.25 | <0.25 | damage, glare, any of the above | all fixes; higher EC level when printing |
| Print growth (PG) | Dark modules too big / small | informative | | | | | ink spread or starvation | enhancer trained on growth / loss |
| Quiet zone (QZ) | Clear margin: QR 4 modules, DM 1 | clear | | | | intruded | foil, text, label edge, dark case touching the code | flat-field; enhancer whitens the margin |
| Cell contrast (CC, 29158) | Contrast inside modules | ≥30% | ≥25% | ≥20% | ≥15% | <15% | marks on caps / metal | CLAHE, deblur |
| Cell modulation (CM, 29158) | Module uniformity | as MOD | | | | | textured surface | enhancer |
| Minimum reflectance (29158) | Light area reflectance | ≥ 5% of reference | | | | | very dark surface | exposure advice |

### QR Code: symbol-specific checks

| Item | What can go wrong | Reader response |
| --- | --- | --- |
| Finder patterns (3) | One finder hidden by glare / foil: the decoder cannot find the code | enhancer inpainting; region from the AI locator |
| Timing patterns | Grid misread on wrinkles | alignment pattern (version ≥ 2); flexible grid (planned) |
| Alignment pattern(s) | Damaged on curved surfaces | decoder falls back to finder-only grid |
| Format information | Both copies damaged: wrong mask / EC level | decoder tries all; higher EC level |
| Version information (v7+) | As above | n/a on these lines (versions 1-3) |
| Error correction level | L 7%, M 15%, Q 25%, H 30% | samples: envelopes M, cartons Q. Recommend Q or H for damage-prone labels |

### Data Matrix: symbol-specific checks

| Item | What can go wrong | Reader response |
| --- | --- | --- |
| L finder (solid edges) | Label edge or dark cap merges with it (quiet zone of 1 module) | flat-field, libdmtx |
| Clock track (alternating) | Blur merges the modules | deblur (Richardson-Lucy) before decoding |
| Module size | Below about 3 px | AI enhancer trained on small modules (2x super-resolution) |

## Image (line) parameters the engine also measures

| Parameter | Why it matters | Target | Reader response |
| --- | --- | --- | --- |
| Module size (px) | Under ~3 px detail is lost | ≥ 4 px (cartons measured 3.2 px, shrink-wrap 2.3 px) | AI super-resolution; lens / field-of-view advice |
| Label brightness | Dark images lose grey levels | 150-200 of 255 | exposure advice (advisor) |
| Lighting uniformity over a code | Shadows, film edges | ≥ 0.8 | flat-field per code |
| Saturation (glare) | Saturated modules carry no data | 0% | flat-field; polariser advice |
| Sharpness | Defocus | ≥ 0.32 (engine scale) | deblur first |

## Why a code did not read (decode-fail diagnostics)

After decoding, every unread region of a short case gets a measured reason (`cr_get_miss_info`,
`engine/src/diagnose.cpp`; .NET `DecodeResult.MissInfo`). The method follows the expired Cognex
verifier patents US9552506 / US10061946 and US8027802 (see `PATENTS.md`). It runs within a cap
of about 20 ms per image, only when the case is short, so the decode time is unchanged.

| Measure | How | Compared with |
| --- | --- | --- |
| Sharpness | min(1, mean of the strongest 10% Sobel edges / (light - dark)); light / dark = means of the brightest / darkest 10% of the code area | the image's read codes (blur under 0.7 of their median), else 0.35 |
| Contrast | light - dark, grey levels | the read codes (low under 0.45 of their median, never above 8), else 25 |
| Saturated | share of the code area at 250+ | glare from 10% |
| Code texture | share of the code area on an edge above the pixel noise and a tenth of the contrast | a code 0.15-0.9, blank under 0.06 |
| Module size | the product's (this image's decoded codes, else learned, else the calibration hints), else the AI side over the modules, else texture transitions | too small under 2 px |
| Finder | QR: three 1:1:3:1:1 run centres at a right angle with legs of n - 7 modules; DM: two adjacent solid sides | found on 95% of the 1,960 read codes of the 55 samples |
| Quiet zone | dark share of a ring 0.5 to 2 modules (DM 1.5) outside the code (the miss diagnosis; the print grade checks the symbology's whole zone: QR 4 modules, Micro QR and rMQR 2, DotCode 3, Data Matrix 1, a part off the image counting dark; with a calibration in flat-field or reflectance units, as the modules) | clear under 20% |

The reason is the first that holds: CUT_OFF (the code runs off the image), GLARE, LOW_CONTRAST,
COVERED (no code texture; BLUR instead when the edges are much softer still), TOO_SMALL, BLUR,
NO_FINDER, else UNKNOWN. The Live page tags each red box with the reason, shows the numbers on
hover and counts the reasons since the start; the advisor names the most frequent one.

On the 55 samples the five codes still unread read as: the four 1.9 px/module QR on the TN excise
hologram labels TOO_SMALL; the unread bottle on 11.bmp (about 3750, 1095: a tilted, defocused
label) BLUR, though only just (sharpness 0.69 of the image's read codes, threshold 0.7; the
softest 1% of read codes measure the same). The miss list also holds regions that are not codes
(bottle glass, the label flags of read bottles that carry a second copy of the code): these come
out COVERED, NO_FINDER, BLUR or LOW_CONTRAST, so read the top causes, not every box.

## What the sample images show (34 images, 1,104 codes graded)

| Line | Main failing parameters | Root cause | Best fix |
| --- | --- | --- | --- |
| Shrink-wrapped TN excise case | MOD / RM 90%, FPD 22%, QZ 20%, module 2.3 px | codes too small for the camera's field of view; hologram foil over codes | optics: modules ≥ 4 px; polariser; reader: super-resolution enhancer |
| Envelopes, boxes with tape | MOD / RM 60-70%, FPD ~20%, QZ ~15% | film edges and foil across the code; uneven light | flat-field (added), enhancer, cross-polarised light |
| Data Matrix caps | MOD / RM 85% | labels bent over caps: defocus | deblur + libdmtx (added); depth of field: smaller aperture, more light |
| Bottles, cans, cartons | MOD / RM ~20-40% | mild blur, small modules (3.2 px on cartons) | fine today; 4 px modules for margin |
