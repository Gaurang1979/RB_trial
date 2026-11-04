# Mining Cognex patents for a better line reader

Cognex's code-reading patents fall into two groups. The first is an **expired or lapsed classical toolkit** (2002–2005 priority). It includes a tile-and-angle-histogram locator, a module-sized dot-peen filter chain, coarse-to-fine windowed decoding, a fixed-camera homography, saddle-point detection, and decode-fail verification diagnostics, and its disclosed parameters can be copied almost line by line. The second group is a set of **active 2011–2018 families**, each running to between about 2031 and 2038: Hotbars-style 1D signal extraction, PowerGrid-style decoding without fixed patterns, under-resolved (sub-1-PPM) decoding, multi-frame cell voting, learned ordering of acquisition settings, conveyor candidate ordering, multi-gain auto-exposure and HDR tone mapping. Two deep-learning filings are still pending. **No Indian (IN) member was found for any family examined**, and Google Patents returns zero IN documents for assignee Cognex. That result is incomplete, though: the Indian Patent Office search (InPASS) sits behind a CAPTCHA and was not queried, and Google's IN coverage is visibly patchy. For an aggregation reader built on zxing-cpp and used on Indian lines, the techniques most worth trying first are these:

- a discrete under-resolved module solver for codes below about 1.3–1.5 pixels per module
- saturation masking plus class-mean-normalised confidence in the multi-frame fuser
- a points-and-moving-average scheme (EMA) for the learned step order
- a cheap brightness probe ahead of the Calibrator
- module-scaled filter sizing, including the expired dot-peen chain
- hypothesis ranking that treats out-of-image modules as erasures

This report is an engineering study of published patent documents. It is not legal advice, and its status data are Google Patents' own non-legal estimates.

## The families, their status and where they are in force

The table below consolidates every Cognex family the research verified. "Status" is Google Patents' anticipated status, which Google itself labels an assumption rather than a legal conclusion. "IN" records only whether India appears in the Google family list.

| Area | Family (lead numbers) | Priority | Status / expiry (Google) | Territories seen | IN |
|---|---|---|---|---|---|
| Locate (1D/2D) | US7181066 tile + angle histogram | 2002-12 | Expired, lifetime, 2024-12-07 | US | none |
| Locate / coarse-to-fine | US8265404, US9036929 | 2004-08 | Expired 2024-08 | US only | none |
| Dot-peen "donut" filter | US7412106 → US7965899 → US8270749 | 2005-06 | US7965899/US8270749 expired (fees); US7412106 shown active to 2026-12-23 | US only | none |
| Distorted (homography) | US7878402, US8322620, WO2007075362 | 2005-12 | Lapsed for fees | US, WO | none |
| Saddle points / DotCode | US9946947 | 2012-10 | "Expired – fee related" (conflicting snippet says active) | US, CN, DE, HK | none |
| 1D extraction (Hotbars) | US9122952, US9218536, US9589199 | 2011-12 | Active to ~2032-06 | US, WO, EP, JP, CN, HK | none |
| No / partial fixed pattern (PowerGrid-like) | US9911024, US10956697, US11455482 | 2014-11 | Active to ~2035-11 | US, EP, JP, CN, HK | none |
| Under-resolved 1D/2D | US9607200, US10032058, US10699089, US10599902, US11151346 | 2014-10 | Active to ~2034-10 | US, DE, CN, HK, JP (US10599902 branch) | none |
| 1D evidence integration | US9361499 | 2014-10 | Active to ~2034-10 | US only | none |
| Fragment tracking | US9836635, US10628648 | 2014-10 | Active (expiry not confirmed) | US | not checked |
| HDR local tone mapping | US10607047, US11138397 | 2017-12 | Active to ~2038-12 | US, plus CN/EP/JP per one listing | none |
| Multi-frame cell voting | US9734376, US10592715, EP2210211, EP3109792, JP5414685 | 2007-11 | US active to ~2033-03; EP to ~2028-11 | US, EP, JP, WO | none |
| Stitching across images | US9104932 | 2013-03 | Active to ~2033-08 | US, EP, JP, HK | none |
| Sorting acquisition settings | US9600703 | 2013-03 / 2015-08 | Active to ~2033-03 | US, DE, JP | none |
| Conveyor candidate ordering | US8740081 → US10325129 | 2011-11 | Active to ~2031-12 | US only | none |
| Multi-core reader | US8794521, US10154177, US10944891, US12149801 | 2012-10 | Active to ~2033-01 | US, DE, CN | none |
| Trigger-to-decode | US9298963, US9721134, US10210369 | 2010-12 | Active to ~2034-07 | US, CN, DE | none |
| Multi-gain auto-exposure | US9413981, US10110829 | 2012-10 | Active to ~2033-09 | US, CN, DE | none |
| BF/DF lighting retry | US7604174 (+ continuations to US9536124) | 2003-10 | Parent expired 2024; continuations not checked | US, WO, EP, JP, DE | none |
| Multiplexed decoders | US7984854, EP2041694, EP2466525 | 2006-07 | Active to ~2027-02 | US, WO, EP, JP | none |
| Verification | US9552506, US8027802, US8169478, US6700997 | 2000–2006 | All expired | US only | none |
| DPM handheld lighting | US11003875, US11574137 | 2018-11 | Active to ~2038-11 | US, EP, CN | none |
| DL symbol decoding | US20260010746, WO2026011064 | 2024-07 | Pending | US, WO | none yet |
| DL tuning of readers | WO2025137512 (+US) | 2023-12 | Pending | US, WO | none seen |

Sources: the individual Google Patents pages cited in the sections below, and the portfolio check ([Google Patents](https://patents.google.com/?assignee=Cognex&cpc=G06K7/10)).

## Expired patents hand over a free locator and filter kit

The oldest locator is **US7181066**, by Wagman, Nadabar and Davis (priority 2002, expired 2024-12-07), and it is the most complete. It works through the image in fixed steps:

1. **Tiling.** The image is cut into **32×32 tiles** and only a checkerboard of them is visited, the tiles where (x+y) mod 2 = 0 (mod 3 for a sparser scan).
2. **Cheap pre-screen.** Only 20–50% of a tile's pixels are tested. Edges come from a **2×2 Roberts cross**, with an approximate magnitude threshold of **14** (exact threshold 10). The pixel-count threshold is about twice the region width.
3. **Angle histogram.** Edge angles go into **256 bins, averaged down to 64 bins of about 5.6°**. Peaks are clustered within ±22.5° down to 50% of the local maximum.
4. **Classification by peak pattern.** Bars give **two peaks 180° apart**, Data Matrix gives **four peaks 90° apart**, and MaxiCode gives six peaks 60° apart. Perspective skew shows up as unequal peak spacing.
5. **Region growing.** A neighbouring tile is merged when its orientation is within **±10°** and its signal density within **±50%**.

([Google Patents US7181066B1](https://patents.google.com/patent/US7181066B1/en))

**US9036929/US8265404** (Nunnink and Equitz, expired 2024-08) adds the obvious next step: decode a sub-sampled full-field image first, then decode **full-resolution windows**. The first window is at the centre, later windows are placed from a finder-pattern hit, and the loop runs until a read succeeds. The patent gives sub-sampling ratios (½, ⅓, ¼) but no window sizes ([Google Patents US9036929B2](https://patents.google.com/patent/US9036929B2/en)).

The dot-peen family **US7412106 → US7965899 → US8270749** (Nadabar and Ye, priority 2005) turns ring-shaped peened dots into solid modules with a linear chain:

1. 3×3 noise smoothing.
2. 3×3 Sobel magnitude, with each component divided by 4.
3. Gain **k = 6.0**, clipped at 255.
4. A Gaussian with **σ = (module size − 1)/6** and kernel width 2·3σ + 1.
5. If decoding fails, sub-sample by ×2, ×4 or ×8 until modules are **5–10 px**, then retry.

Sampling uses the median of the minimum and maximum reflectance as its threshold ([Google Patents US7965899B2](https://patents.google.com/patent/US7965899B2/en); [Google Patents US8270749B2](https://patents.google.com/patent/US8270749B2/en)). The continuations are expired for unpaid fees. The parent US7412106 still shows as "Active" to **2026-12-23**, about two months away, so treat that last patent as live until then ([Google Patents US7412106B1](https://patents.google.com/patent/US7412106B1/en)).

Three more old families are free or nearly free. The homography family **US7878402/US8322620** learns an 8-unknown projective transform once, at train time, from the four corners of a symbol imaged at an oblique angle. It solves the transform by least squares and pre-warps every later frame from the same fixed station. The family lapsed for non-payment ([Google Patents US7878402B2](https://patents.google.com/patent/US7878402B2/en); [Google Patents US8322620B2](https://patents.google.com/patent/US8322620B2/en)).

The saddle-point detector **US9946947** finds module corners on dotted or checkerboard-like codes:

1. Compare each **5×5 neighbourhood** with a copy of itself rotated 90°. A saddle point scores close to −1.
2. Take the dominant angle as **½·atan2** of two kernel responses 45° apart.
3. Histogram the spacing between detections to get the module pitch, and fit a grid.

Google lists the family as "Expired – fee related", but one search snippet called it active, so check it on the USPTO register ([Google Patents US9946947B2](https://patents.google.com/patent/US9946947B2/en)).

The common thread across these patents is **filter scale tied to module size**. A smoothing window is about one module, σ = (M − 1)/6, a contrast neighbourhood spans "a few modules", and images are re-sampled to 5–10 px per module. In practice that means estimating module size first and deriving every image-fix kernel from it.

## Active families hold the hard decoding cases until 2032–2038

**Hotbars (US9122952, Silver and Bachelder, active to about 2032-06)** projects 2D pixels onto a 1D signal along any of **171 quantised orientations per 90°**. It uses precomputed integer weight "transfer templates" fetched by direct memory access (DMA). The blur is controlled in each direction separately: about **2.3 px** along the bars, about **0.34 × the bin spacing** across them, with bins spaced **0.73 px** apart. The best sub-pixel offset is found on an 8×8 grid at 1/8 px, then again at **1/64 px** ([Google Patents US9122952B2](https://patents.google.com/patent/US9122952B2/en)). Cognex markets the technology as Hotbars, launched in 2012 ([Cognex 2012 press release](https://www.cognex.com/company/press-releases/2012/cognex-dataman-takes-a-quantum-leap-in-barcode-reading-performance)). The family covers the US, EP, JP, CN and HK.

**The no-fixed-pattern family (US9911024 / US10956697 / US11455482, Ye, Wang and Negro, priority 2014-11, active to about 2035-11)** is the most likely basis of PowerGrid. Cognex announced PowerGrid in 2014 as a texture-based locator that reads codes whose finder, clocking pattern or quiet zone is damaged ([Cognex 2014 press release](https://www.cognex.com/company/press-releases/2014/cognex-revs-up-2-d-barcode-reading-read-2-d-matrix-and-challenging-dpm-codes)); the mapping to these patents is an inference. The disclosed method runs in this order:

1. Compute an edge-magnitude image.
2. Take a 2D FFT. Spectrum peaks give orientation and pitch.
3. Optionally **mask the spectrum around the peaks and inverse-FFT it**, which yields an image of predicted module centres that is refined to sub-pixel.
4. Seed a **5×5-module local affine patch** at the centre, translate it in **eight directions**, refine against nearby features, then fit a global polynomial to all correspondences.
5. Extrapolate module positions. Positions that fall outside the image are treated as unknown.
6. Rank hypotheses over symbology, size, offset, mirroring and polarity, including hypotheses with missing finder or clock rows. Ranking can favour sizes the user prefers.
7. Decode in rank order, with Reed-Solomon erasures absorbing the gaps.

Claim 10 of US10956697 is broad: locate some modules, predict positions, sample, extract a binary matrix and decode. Claim 18 adds the frequency-domain analysis ([Google Patents US10956697B2](https://patents.google.com/patent/US10956697B2/en); [Google Patents US11455482B2](https://patents.google.com/patent/US11455482B2/en)). The team's lattice Data Matrix decoder (edge spectrum to pitch and angle, then ranked windows) is functionally close to this family, which is in force in the US, EP, JP and CN.

**The under-resolved family (Bachelder and Negro, priority 2014-10, active to about 2034-10)** states the most useful engineering benchmark in the whole portfolio. Edge-based decoders "start failing" at **1.3–1.5 pixels per module (PPM)**, conventional decoding needs about **2 PPM**, and the target is **below 1 PPM**, with 0.8 PPM as the worked example ([Google Patents US11151346B2](https://patents.google.com/patent/US11151346B2/en)). The 2D method in US11151346 has three stages:

1. **Direct deduction.** Build a sparse module-to-pixel overlap matrix. Fix any module that covers a foreground or background pixel by at least **90%**; about 30% can be enough when the grid is accurate. Then propagate through pixels that are mostly covered by two modules, one of them already known.
2. **Sub-grid test.** For each unknown module, enumerate the **3×3 patterns (up to 512)** centred on it and keep those below a pixel-error threshold. Work from the finder and timing patterns inward.
3. **Constraint graph.** Link surviving patterns in **eight directions** where their overlaps agree, and repeatedly prune nodes that lack a link in some direction.

Modules that stay ambiguous go to Reed-Solomon as erasures, because an unknown costs about half as much as a wrong value. The 1D members (US9607200 and its continuations) instead compute sampling coefficients from samples-per-module and phase, and test the valid character patterns, for example **103 for Code 128** ([Google Patents US9607200B2](https://patents.google.com/patent/US9607200B2/en)). The territories differ by branch: US9607200 has DE, CN and HK members, and US10599902 has JP and DE members ([Google Patents US10599902](https://patents.google.com/patent/US10599902)).

**US9361499** (US only, to about 2034) integrates 1D element-width evidence across partial scans. Each scan adds one unit per element. A character is accepted when the top interpretation beats the second by a confidence margin, **1.5** in the example, and the width tolerance is **10%** ([Google Patents US9361499B2](https://patents.google.com/patent/US9361499B2)).

**Local tone mapping (US10607047/US11138397, Gao and Bachelder, active to about 2038-12)** maps a 12-bit image to 8 bits:

- The neighbourhood is sized from the pixels-per-module range.
- Local contrast is measured as **3σ**, or as the gap between the means of the bottom 20% and top 20% of the local histogram, and is then clamped.
- Areas that are not bimodal are optionally masked as background.
- The output is mapped piecewise-linearly into roughly **[5–10, 245–250]**.
- After a failed decode it retries with another kernel, or reuses the last frame's parameters when tracking.

([Google Patents US10607047B2](https://patents.google.com/patent/US10607047B2/en)). The research notes disagree on this family's reach. One note lists it as US-only; the portfolio check lists CN, EP and JP for US11138397 ([Google Patents US11138397B2](https://patents.google.com/patent/US11138397B2/en)). Assume it covers all four until that is checked.

## Multi-frame, tuning and conveyor patents mirror the team's own architecture

Several of the team's components (the multi-frame reader, the learned per-product step order, the Calibrator and the bandit) have close Cognex analogues.

**Multi-frame voting (US9734376 / EP2210211 / EP3109792, Nadabar, priority 2007-11).** Each frame is processed in these steps:

1. Split the located code into **4×4-cell subregions**.
2. Mark every cell of a subregion "unknown" if its fraction of saturated pixels exceeds a threshold.
3. Run Otsu's method on that frame alone.
4. Score each cell as (T − raw)/(T − M0) towards "0" or (raw − T)/(M1 − T) towards "1", where M0 and M1 are the class means on either side of the threshold T.
5. Add the scores to two running sums per cell. Every frame has **equal weight**.

A cell is decided only when the gap between its two sums passes a margin. Otherwise it goes to Reed-Solomon as an erasure. The patent discloses no numeric thresholds ([Google Patents US9734376B2](https://patents.google.com/patent/US9734376B2/en)). The US member runs to about **2033-03** and EP3109792 to about **2028-11** ([Google Patents EP3109792B1](https://patents.google.com/patent/EP3109792B1/en)).

**Stitching across images (US9104932, to about 2033-08).** It registers frames taken with *different* acquisition settings by correlating a synthetic finder-and-timing model against a Determinant-of-Hessian dot image. It chooses polarity by keeping whichever of the two Determinant-of-Hessian polarities correlates better. A candidate counts as "promising" above **65%** fixed-pattern agreement ([Google Patents US9104932B2](https://patents.google.com/patent/US9104932B2/en)).

**Sorting acquisition settings (continuation-in-part US9600703, to about 2033-03).** This learns which settings to try first:

- Each successful read cycle has **1000 points**, shared among the images that contributed to it, for example 500/250/250.
- A carried-over seed image gets 600 of the 1000.
- Each setting keeps an exponential moving average with **α = 2/(50+1) ≈ 0.0392**, a period chosen to match a 50-wafer lot.
- Settings are sorted by that average, and last-cycle winners can be promoted to the top.

([Google Patents US9600703B2](https://patents.google.com/patent/US9600703B2/en)). This is the nearest published relative of the team's learned per-product step order.

**Conveyor ordering (US10325129, US only, to about 2031-12).** Candidates are sorted into four groups by whether they were already decoded and whether they are about to leave the field of view. They are decoded in the order not-decoded-and-leaving first, decoded-and-staying last, and leftovers are dropped when the next image arrives or a timeout fires. The example assumes about **20 images per code transit** ([Google Patents US10325129B2](https://patents.google.com/patent/US10325129B2/en)).

**Multi-core reader (US8794521).** A decode that overruns its time on one core is handed to a second core only if the code is "probably decodable", for example because its finder has already been found ([Google Patents US8794521B2](https://patents.google.com/patent/US8794521B2/en)).

**Multiplexed decoding (US7984854, to about 2027-02).** A fast decoder and a robust DPM decoder run as parallel threads on the newest frame, and the first success wins. The example splits the CPU 17.6% / 82.4%, with timeouts of 85 ms and 850 ms ([Google Patents US7984854](https://patents.google.com/patent/US7984854)).

Several families cover acquisition and lighting:

- **Multi-gain auto-exposure (US9413981/US10110829, to about 2033-09).** One sub-sampled frame is captured with **four gains on interleaved 2×2 pixel groups** (example gains 1.5, 5, 10 and 15) at about 1 ms exposure, which takes about **4 ms against about 40 ms** for a full frame. Global gain and exposure are computed from the most readable gain image, and only then is the illumination varied ([Google Patents US20140111656](https://patents.google.com/patent/US20140111656)).
- **Bright-field / dark-field lighting retry (US7604174).** It starts from a cached last-good lighting setting, checks it by a decode attempt *or* a statistical contrast test, and switches bright field and dark field or ring quadrants on failure. Dark field arrives at 45° or less to the surface. The parent expired in 2024 ([Google Patents US7604174B2](https://patents.google.com/patent/US7604174B2/en)).
- **Polarisers.** Cognex's own guidance is crossed polarisers at **90°** on the lens and the lights, and a reader tilted **15°** from perpendicular to avoid its own glare ([Cognex lighting guide](https://www.cognex.com/en-hu/what-is/industrial-barcode-reading/barcode-reader-lighting); [DataMan glare page](https://support.cognex.com/docs/dmst_618/web/EN/Fixed_Mount_Manual/Content/Topics/FMREF/OptimizingPerformance/AvoidReflectionsGlare.htm)).
- **Product behaviour.** DataMan readers try multiple read setups in a fixed priority order ([Cognex help article](https://support.cognex.com/en/help-articles/how-to-configure-a-dataman-reader-to-use-multiple-read-setups)). No Cognex patent found claims a bandit or a learned reordering of decoder options, but that rests on limited searching.

The two deep-learning filings are both pending and found so far only in the US and WO:

- **WO2025137512** (priority 2023-12-22) tunes a reader. A pre-trained ROI finder plus a machine-learning **quality metric** built from K global, N regional and M decoder features (linear regression, random forest, SVM or a neural network) drives a closed loop over focus, gain, exposure, light banks, polarisation, filtering, HDR and gamma until the metric meets a criterion ([Google Patents WO2025137512A1](https://patents.google.com/patent/WO2025137512A1/en)).
- **US20260010746 / WO2026011064** (priority 2024-07-03) claims, broadly, any deep-learning module that maps a symbol image to "embedded information". It uses a vision transformer to predict codewords, followed by classical Reed-Solomon correction, and adds an oriented-box detector and a barcode / non-barcode crop classifier ([Google Patents US20260010746A1](https://patents.google.com/patent/US20260010746A1/en)).

The convolutional neural network (CNN) module-map patent US11960966 that turns up in searches belongs to ABBYY, not Cognex ([Google Patents US11960966B2](https://patents.google.com/patent/US11960966B2/en)).

## Verification and DPM patents are mostly expired workflow, not ISO arithmetic

Cognex's verification patents claim *workflow*, not the ISO 15415/29158 formulas.

**US9552506** (expired) grades a mark *after a failed decode*:

- Sharpness is **S = min(1, edge strength / (light − dark))**, computed from the strongest 10% of edges and the 10% lightest and 10% darkest pixels.
- Finder-pattern scores are products of count ratios.
- Module separability uses a 0.15 band.
- The minimum quiet zone is 1 cell for continuous marks and 4 cells for dot marks.
- Results are shown as overlays on the image.

([Google Patents US9552506](https://patents.google.com/patent/US9552506)).

**US8027802** (expired) re-renders a **synthetic ideal mark from the decoded data**, builds a deformation map from the difference, and scores the mark 0–100 against baselines of 80, 90 or 95 ([Google Patents US8027802](https://patents.google.com/patent/US8027802)).

**US8169478** (expired) starts calibration when it decodes a card whose 2D code holds a start command plus target reflectance values. It then iterates exposure for each of **three lighting schemes** until the image matches the targets ([Google Patents US8169478](https://patents.google.com/patent/US8169478)).

No Cognex patent found closes the loop back to the *printer*. That idea belongs to a Honeywell / Hand Held Products family, US10546160 ([Justia US10546160](https://patents.justia.com/patent/10546160)).

Most of the active DPM patents are hardware. The **US11003875** handheld (to about 2038, US, EP and CN) gives lighting recipes:

- laser etch on a polished cylinder: diffuse light only
- dot-peen on a rough surface: diffuse plus low-angle light, with the low-angle strip tilted about 20°
- dot-peen on a polished surface: low-angle plus cross-polarised light

([Google Patents US11003875B2](https://patents.google.com/patent/US11003875B2/en)).

## No Indian member surfaced, but the check is incomplete

Patents are territorial. Under Section 48 of the Patents Act 1970, a patentee can stop others from making, using, selling or importing the invention **in India** only if it holds an Indian patent ([IP India, s.48](https://ipindia.gov.in/writereaddata/Portal/ev/sections/ps48.html)). Three lines of evidence point the same way:

- Google Patents returns **0 IN documents** for assignee Cognex (and for Microscan, Nunnink and Bachelder), against 786 US, 150 CN, 109 EP and 77 JP results ([Google Patents](https://patents.google.com/?assignee=Cognex&country=IN)).
- QuickCompany, which mirrors Indian Patent Office publications, finds **no "cognex" patents** ([QuickCompany](https://www.quickcompany.in/patents?q=cognex)).
- **No family list examined includes IN.** Several key families (the PowerGrid-like family, the under-resolved family and US9361499) never filed under the PCT at all, so a late Indian national-phase entry is not possible for them.

The evidence has three weaknesses:

- **InPASS was not queried**, because its search needs a CAPTCHA ([InPASS](https://iprsearch.ipindia.gov.in/PublicSearch/)).
- Google's IN coverage is patchy: it also shows zero IN hits for Datalogic, Zebra, Keyence and SICK, although it does show 95 for Honeywell ([Google Patents](https://patents.google.com/?assignee=Honeywell&country=IN)).
- The deep-learning family **WO2026011064 can still enter India until about 2027-02-03**. WO2025137512's window closed around 2026-07-22, and an entry in that window might not be indexed yet ([Google Patents WO2026011064A1](https://patents.google.com/patent/WO2026011064A1/en)).

Exports are the real exposure. A reader or software shipped to the US, EP/DE, JP, CN or HK falls under whichever active families are in force there. That means mainly the PowerGrid-like family (US, EP, JP, CN to about 2035), the under-resolved family (US, DE, CN, JP to about 2034), Hotbars (to about 2032), multi-frame voting (US to about 2033, EP to about 2028), US9600703 and the tone-mapping family. A manual InPASS applicant search ("Cognex", "Cognex Technology and Investment", "Manatee Works", "Sualab", "ViDi") and a claim-level freedom-to-operate review by counsel should come before any export. This section summarises filing data; it is not legal advice.

## Ten Cognex ideas the team should try first

The ranking weighs expected read-rate gain on Indian aggregation lines against how little the current stack already covers. The stack is zxing-cpp with an AI locator, the image-fix steps, the lattice Data Matrix decoder, soft Reed-Solomon with reserve symbols, known-content decoding, serial-run matching, the multi-frame reader, learned step order, the Calibrator and the bandit. No Cognex analogue was found for known-content decoding or serial-run matching, so those remain the team's own differentiators.

| # | Idea to try | Cognex source and disclosed parameters | Where it plugs in | Status note |
|---|---|---|---|---|
| 1 | Discrete under-resolved solver for codes at 0.8–1.5 PPM: 90% overlap seeding, two-module propagation, 3×3 pattern pruning (≤512), 8-way constraint graph, leftovers as erasures | US11151346 | New fallback after the lattice decoder; also feeds soft RS | Active US (+JP/DE branch) to ~2034; none in IN found |
| 2 | Hand-over rule at 1.3–1.5 PPM: route codes below it to the under-resolved path instead of zxing's edge decoder | US11151346 benchmark | Router in front of the decoders | Benchmark only |
| 3 | Saturation masking in multi-frame fusion: saturated modules add zero evidence; class-mean-normalised confidence; vary exposure across the 3-frame burst | US9734376 (4×4 subregions, Otsu per frame, M0/M1, equal weights); US9104932 (different settings per frame) | Multi-frame reader | Active US/EP; use per-module masks and quality-weighted frames, not the claimed 4×4/equal-weight form |
| 4 | Contribution points + EMA for learned step order: 1000 points per successful cycle shared among contributing steps, α = 2/51, last winner promoted | US9600703 | Learned per-product step order; seed for the bandit's prior | Active US to ~2033 |
| 5 | Dot-peen / inkjet-dot chain: 3×3 blur → Sobel/4 → ×6, clip → Gaussian σ = (M−1)/6; on failure resample to 5–10 px/module | US7965899 family | New image-fix step before the lattice decoder | Expired; parent US7412106 shown live to 2026-12-23 |
| 6 | Module-scaled tone mapping instead of fixed-tile CLAHE: neighbourhood a few modules wide, contrast = 3σ or 20%-tail means, clamped, non-bimodal areas masked, output range 5–250 | US10607047 / US11138397 | Image-fix (CLAHE/glare) | Active to ~2038; study, design your own form for export |
| 7 | Brightness probe before the Calibrator: short binned/ROI frame, black level and percentiles → starting exposure/gain; order exposure/gain first, light second; hard limit on saturated-pixel fraction | US9413981 (4 ms vs 40 ms; light after gain/exposure) | Calibrator, bandit arm order | Per-pixel-group gain is the claimed part; sequential frames differ |
| 8 | Hypotheses with out-of-image modules as erasures, ranked by known per-product symbol size; masked inverse-FFT module-centre map; 5×5 affine seed grown 8 ways then polynomial for curved/wrinkled DM | US10956697 / US11455482 | Lattice DM decoder | Active US/EP/JP/CN to ~2035; closest to the team's own decoder, so check before export |
| 9 | Time budget on a pass: undecoded codes first, already-decoded last; allow overrun only when the finder is found ("probably decodable"); fast and robust decoders in parallel, first success wins | US10325129; US8794521; US7984854 (85/850 ms timeouts) | Scheduler for the 12–96 codes per case | Active US to ~2031/2033/2027 |
| 10 | Station homography + decode-fail diagnostics: learn H once per fixed camera from a good read; on failure report sharpness (top/bottom 10%), finder score, quiet zone; re-render the ideal mark from decoded data for a difference map | US7878402/US8322620; US9552506; US8027802 | Calibrator setup, operator dashboard | All expired |

Two cheaper additions sit just outside the top ten. The expired **US7181066** tile-histogram locator can run alongside the AI locator: it classifies 1D versus 2D by its 2- or 4-peak angle pattern and estimates skew from uneven peak spacing, which makes a good tie-breaker when the network is unsure. Expired **coarse-to-fine windowing (US9036929)** suits a 20 MP frame: decode at half resolution first, then retry full-resolution windows around the AI boxes. For glare, the patents add no software trick beyond what Cognex publishes as practice: crossed polarisers at 90°, a 15° camera tilt, and a low-angle or dark-field second strobe channel as an extra arm for the bandit ([Cognex lighting guide](https://www.cognex.com/en-hu/what-is/industrial-barcode-reading/barcode-reader-lighting); [Google Patents US7604174B2](https://patents.google.com/patent/US7604174B2/en)).

## Conclusion

The patents show that Cognex's competitive edge in code reading rests on three ideas, not on any single secret. The first is that **every filter and sampling step is scaled to the module**. The second is that **uncertainty is turned into Reed-Solomon erasures** rather than guesses, whether it comes from saturation, under-resolution, multi-frame disagreement or modules outside the image. The third is that the reader **learns which acquisition settings to try first**. The team already has the second idea in soft Reed-Solomon with reserve symbols, and a stronger form of the third in its bandit. Its biggest uncovered gap is the band below about 1.5 pixels per module, where zxing-style edge decoding is expected to fail and Cognex's discrete solver works. That band is where a 20 MP camera viewing a 96-code case will sit.

The legal picture favours domestic use and calls for caution on exports. Nothing found gives Cognex rights in India today, but that rests on an unrun InPASS search and on a deep-learning family that can still enter India until early 2027. The prudent sequence is to build the ideas above in forms that differ from the claims (per-module masks, quality-weighted frames, sequential exposure probes), run the manual InPASS check now, and get a claim-level review by counsel before any reader ships to the US, Europe, Japan or China.
