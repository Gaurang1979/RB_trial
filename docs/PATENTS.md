# Patent register

**Owner's rule, 2026-11-01** (after advice from his lawyer): "Do not lookup for patent validity and
valid countries. Simply build the tools, take ideas from patent and improve tools. We are not
copying their exact model or codes and as per my discussion with lawyer we can use patented methods
with additional or changed or improved approach. Always use patented methods with twist or without
twist. In future do not stop development due to patent issue." This register is a list of method
sources to build from. Its status and territory columns are history: nobody updates or checks them,
and no development waits on them (AGENTS.md).

Patents studied for this reader: what they cover, where they are in force, and which part of the
engine is related. Patents are territorial: a patent binds only in the countries that granted it,
and an expired patent is free for anyone to use. Using a method for one's own production is not,
by itself, an exemption in most countries (India's Patents Act exempts experiment and research,
not production use). This is an engineering register, not legal advice: confirm with a patent
attorney before relying on it, especially before selling or exporting the reader.

Status from Google Patents legal-status data (an assumption, not a legal conclusion), checked
2026-10-23.

| Patent | Owner | Covers | Status | Territories | Related engine part |
| --- | --- | --- | --- | --- | --- |
| US6685095 | Symagery (Psion / Symbol) | Data Matrix with a damaged L finder: find about half of the L, count rows and columns, mark missing perimeter elements as erasures for Reed-Solomon | **Expired** 2018-10-01 | US | `softDecodeDm`, `rsDecodeDm` (erasures) |
| US7965899 | Cognex | Dot-peened / dotted Data Matrix: Sobel gradient magnitude, amplify, smooth by about one module so each dot becomes a solid blob, then the standard locate and decode; repeat subsampled | **Expired** (fee related; adjusted expiry 2026-03-14) | US | Tried 2026-10-23 on the unread printed Data Matrix caps (48-cap image, crate): no gain (the caps are printed, not dot-peened), so not added then. **Used since 2026-10-29** for dot-printed (inkjet) codes: `CR_STEP_DOTS` variant 0 smooths by about a third of a module so the dots run together (expired, free to use) |
| US9911024, US10956697, US11455482 (+ EP3023905, EP3467700, EP4137988, JP, CN, HK) | Cognex | 2D matrix symbols with incomplete or absent fixed patterns: module positions from an edge power spectrum or dot detection, a grid model extrapolated over the symbol, binary-matrix hypotheses (position, orientation, size, mirroring, polarity) ranked and decoded | **Active**, anticipated expiry 2035-11-18 | US, EP, JP, CN, HK. **No Indian (IN) member found** | `lattice.cpp` (PR #12) works on the same principle. Fine on lines in India if no Indian patent exists; check before use in the US, Europe, Japan, China or Hong Kong |
| US10540532 | Datalogic | Optical codes with damaged or incomplete finder patterns | Active | US (family not checked) | general |
| US11354526 | (not checked) | Neural network locating partly unreadable Data Matrix | (not checked) | | AI locator (CodeNet) is our own design |
| Cognex Hotbars (1D) | Cognex | Texture-based 1D barcode location, high-resolution 1D signal | No patent number found | | 1D parent code: zxing-cpp |
| US11151346 (+ US9607200) | Cognex | Under-resolved symbols (below about 1.5 px per module): each pixel is an area-weighted mix of the modules it overlaps; known modules (finders, timing) seed; 3x3 neighbourhood hypotheses tested against the pixels | Active, to 2034-10-09 | US, DE, CN, JP (no IN found) | `underres.cpp` (`underresDecodeQr`, 2026-10-27, prototype `underres.py`) works on the same principle in our own form: area-and-blur overlap model, function modules and the format fixed, all 3 x 3 patterns tested per module, flip cost over the neighbourhoods as confidence, least certain codewords as RS erasures. Called from the grid fit for QR at 1.1-2.6 px per module. Built for the owner's lines in India; **build with `-DCR_UNDERRES=OFF`** for a reader used in the US, Germany, China or Japan |
| EP3109792, US9734376 (+ US10592715, EP2210211), JP5414685, WO2009064759 | Cognex | Reading with multiple frames: the located code split into 4 x 4-cell subregions, a subregion dropped when its share of saturated pixels passes a threshold; per frame Otsu, each cell scored (T - raw) / (T - M0) towards dark or (raw - T) / (M1 - T) towards light (M0, M1 the class means); scores summed over frames with equal weight; a cell decided when its two sums differ enough, else a Reed-Solomon erasure | Active: US to about 2033-03, EP to about 2028-11 | US, EP, JP, WO (**no Indian member found**) | `multiframe.cpp` (`cr_decode_frames`; 2026-10-24, upgraded 2026-10-27): per-module evidence normalised by class means, as the patent, but in our own form: clipping judged per module (not per 4 x 4 subregion; in an overexposed frame only clipped patches, not lone clipped light modules), thresholds and class means of each module's 9 x 9 neighbourhood (not one Otsu per frame), frames weighted by their quality on the code (inverse variance of their evidence; the patent weights frames equally), the reference grid chosen by fixed-pattern fit, one signed sum per module with weak modules as erasures and two check symbols in reserve. Built for the owner's lines in India; the claims cover the summed class-mean-normalised confidences, so check before use in the US or Europe (a territory switch is planned, RESEARCH-NEXT-GENERATION.md) |
| US8150163 | Scanbuy | Multi-frame super-resolution of a barcode from several low-resolution frames (registration, then a higher-resolution image of the code) | **Expired** (16.3 of INDUSTRY-METHODS) | US | `superResolve` / `superResolveQrFrames` (`multiframe.cpp`, 2026-10-31, PR #175): only after the fused modules fail, for QR below 2.6 px per module; crops of the code region already aligned to a pixel, registered to a tenth of a pixel (phase correlation, Lucas-Kanade), levels matched, clipped pixels left out, a 2-4x finer grid by classic iterative back-projection (Irani-Peleg, 1991), then the normal grid fit and soft decode. No area-weighted module hypotheses are tested against the frames (that is the US11151346 under-resolved form; the super-resolved image is at about 4 px per module, where the solver does not run). Free to use as an expired method; counsel to confirm the expiry and that no live continuation covers registration-then-reconstruction |
| US11151346 / US9607200 (see above) | Cognex | Hypotheses tested against the pixels through a pixel-formation model | Active, to 2034-10-09 | US (no IN found) | `psf.cpp` (`psfScore`, 2026-10-27) renders serial-run candidates through a measured Gaussian blur and print growth and correlates them with the pixels: a different model (lens blur on a resolved code, 3-4 px per module, not area-weighted sub-pixel mixing), but the same shape of test. Check before use in the US |
| US10607047 | Cognex | Local tone mapping before decoding (local mean and contrast over a few modules, 8-bit mapping) | Active, to 2038-12-03 | US (no IN found) | Similar to our lighting flattening (`CR_STEP_FLAT`). Module-scaled contrast was built and measured 2026-10-31 (PR #169) and **not adopted** (no gain on the sample images). If it is built again, the design-around points, **for legal review**: independent claims 1, 19 and 20 map raw pixels to a mapped value of *smaller bit depth*, and `cr_decode` takes 8-bit grey only, so an 8-bit-in, 8-bit-out step lacks that element; the trial used the Gaussian local mean and standard deviation (Wallis-type normalisation, published 1976), with no bottom / top 20 % means, no non-bimodal mask and no retry with another kernel; its output (deviation times a fixed gain, clipped) is for counsel to compare with the patent's 3σ contrast and piecewise-linear map. A future 10- or 12-bit camera path must not feed such a step raw pixels without counsel |
| US10325129 | Cognex | Ordering code candidates on a moving conveyor (new / leaving / already decoded) within the time to the next image | Active, to 2031-12-02 | US | Not needed for still cases |
| US9036929 | Cognex | Low-resolution full view, then full-resolution windows around the symbol | **Expired** 2024-08-04 | US | We already decode full-resolution crops of located regions |
| US9552506, US10061946 (verifier: grading a mark after a failed decode), US8027802 (ideal mark re-rendered from the decoded data, deformation map) | Cognex | Sharpness = min(1, edge strength / (light - dark)) from the strongest 10% of edges and the 10% lightest / darkest pixels; finder score; quiet zone; results as overlays | **Expired** | US | `diagnose.cpp` (`cr_get_miss_info`, 2026-10-27): why each unread region did not read, shown on the Live page. The difference map of US8027802 is not built yet |
| US8322620 | Cognex | Distorted symbols: a homography trained once for a camera at a fixed angle, then every image rectified | **Expired** 2025 (fee) | US | Free to use; our codes are rectified per code |
| US9413981, US10110829 | Cognex | Auto-exposure from one sub-sampled frame with **different gains on interleaved 2 x 2 pixel groups** (for example 1.5, 5, 10, 15) at about 1 ms; global exposure and gain from the most readable gain image, then the illumination | Active, to about 2033-09 | US, CN, DE. **No Indian (IN) member found** | `CalibrationSearch` (2026-10-27) probes with **separate sequential frames**, each at one exposure and one gain for the whole sensor (decimated when the camera can), not per-pixel-group gains; it then orders exposure / gain before light timing. Differs from the claimed form; check the claims before export to the US, China or Germany |
| US7604174 | Cognex | Lighting retry: start from the cached last-good setting, check it by a decode or a contrast test, switch bright / dark field on failure | **Expired** 2024 (parent; continuations to US9536124 not checked) | US, WO, EP, JP, DE | `CalibrationSearch` tries the product's last-good camera settings first and drops a trial before decoding when its contrast is too low |

1D (`linear.cpp`, 2026-10-27): the band locator (gradient structure tensor), the median of many scan lines and matched-filter geometry are classic signal processing; scanning along a bar band at any angle resembles omnidirectional 1D reading, whose early patents (Symbol, Metrologic) have expired. Decoding an SSCC by fitting rendered candidate symbols through a measured blur, confirmed by the check digits, resembles template-based 1D decoding; no numbered patent was checked. A numbered search is owed before use outside India.

Slot map (`slots.cpp`, 2026-10-29): a lattice fitted to the read codes' centres (nearest-neighbour pitch vectors, least squares, Lagrange-Gauss reduction) is classic geometry; fixturing a part and inspecting expected positions is what NI Vision Builder and DALSA Sherlock offer as configurable steps. No numbered patent was checked.

## MVTec (HALCON)

MVTec Software GmbH is family-owned and independent (no parent company found). Its 18 patent
families on Google Patents (2026-10-23) cover 3D object recognition and pose (EP2048599, US8260059
deformable objects, JP6216508), shape-based matching (US7062093, US7239929), calibration (hand-eye
US12159429, range devices EP1256831), anomaly detection (US12548135), 3D reconstruction (JP7486740),
automatic parameter determination (US7953290, 2006, about expired; EP4636707A1, 2024, pending) and
application development (WO2001067389). **None covers bar code or data code reading.** The nearest
to us is automatic parameter determination, which our learned per-product settings resemble.

## Full study (2026-10-27)

`docs/RESEARCH-COGNEX-PATENTS.md` covers Cognex's code-reading families in full: location and
preprocessing, damaged / under-resolved / distorted 2D, 1D, illumination and auto-tuning,
multi-frame and conveyor, verification and DPM, deep learning; with each family's process steps,
disclosed parameters, status, expiry and territories, and ten ideas ranked for this reader.
India: no Indian (IN) member was found in any family, but the Indian Patent Office search
(InPASS) was not run (CAPTCHA), and WO2026011064 can still enter India until about 2027-02-03.
Run a manual InPASS applicant search before relying on it.

## Before selling or exporting

The reader is built for the owner's own lines in India. Before it is sold, or used on lines in a
country where an active patent above is in force, the related engine part needs a legal check or
a switch to turn it off.

Sources: Google Patents pages of the patents above (patents.google.com/patent/<number>).
