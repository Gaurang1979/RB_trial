# Study 03: Standards, regulations, guidelines and key papers

Date: 2026-10-30. Branch claude/tool-study. Part of the Vision_System study (owner's request of
2026-10-30: "Nothing must left in this study"). Layer names L0 to L6 are the common model
(L0 Acquire, L1 Correct, L2 Find, L3 Read or measure, L4 Verify and decide, L5 Learn and tune,
L6 Output and audit). Effort in person-weeks (pw).

## How to read the source marks

Many standards are paywalled and several official sites blocked automated reading today
(HTTP 403 / 429 / 404). Honesty matters more than looking complete, so every row carries a mark:

| Mark | Meaning |
| --- | --- |
| [O] | The page was opened today and the fact was read there |
| [S] | Seen only in a search-result summary today; not opened in full |
| [K] | From the author's knowledge of the standard (training data), not re-opened today. Treat numbers as "check against the purchased text" before they go into a product claim |

Numbers that drive a line decision (grade limits, quiet zones, X-dimensions) are listed as [K]
unless marked otherwise. They were checked against what the engine implements (engine/src/quality.cpp,
grade1d.cpp, grade_dpm.cpp), which was written from vendor documentation of the same standards; where
they differed (grid non-uniformity, section 1) the row now gives the engine's numbers.
Buying the texts (section 13) closes this gap.

Where a standard needs a licence key from the owner's lawyer: none. Standards are copyrighted
documents, not patents. We implement the method; we never copy the text of the standard into the repo.

---

## 1. Verification and print quality (the grading family)

These decide L4 for the Code Verify tool (tool spec 6.2 to 6.4) and the grade report.

### 1.1 ISO/IEC 15416 (linear symbols), ISO/IEC 15415 (2D), ISO/IEC TR 29158 (DPM)

Links: [ISO 15416:2016](https://www.iso.org/standard/54716.html) [K],
[ISO/IEC 15415:2011](https://www.iso.org/standard/54717.html) [K],
[ISO/IEC TR 29158:2020](https://www.iso.org/standard/76310.html) [K] (successor of AIM DPM-1-2006, [AIM](https://www.aimglobal.org) [K]).

| Item | ISO 15416 (1D) | ISO 15415 (2D) | TR 29158 / AIM DPM-1-2006 (marks on parts) |
| --- | --- | --- | --- |
| Grade scale | 4 (A) to 0 (F), overall = lowest parameter; symbol = mean of 10 scans [K] | 4 to 0, overall = lowest parameter; symbol = mean of grades of 1 or more images [K] | Same 4..0 letter scale; the 2020 edition adds new thresholds [K] |
| Parameters | Rmin (<= 0.5 Rmax), symbol contrast SC, minimum edge contrast ECmin, modulation MOD, defects, decodability, decode, (quiet zone via decodability / decode) [K] | Symbol contrast, modulation, reflectance margin, fixed pattern damage, axial non-uniformity, grid non-uniformity, unused error correction, decode; print growth reported not graded [K] | Cell contrast (CC), cell modulation (CM), minimum reflectance (MR), fixed pattern damage, distributed damage, axial and grid non-uniformity, UEC; reported with the light and aperture used [K] |
| SC grade limits | A >= 70 %, B >= 55, C >= 40, D >= 20 [K] | same [K] | CC (2020) 30 / 25 / 20 / 15 % [K, from vendor docs, per change note `2026-10-30-claude-grade-dpm-29158`] |
| MOD (1D) | A >= 0.70, B >= 0.60, C >= 0.50, D >= 0.40 [K] | Modulation from reflectance margin: A >= 0.50, B >= 0.40, C >= 0.30, D >= 0.20 [K] | MR 20 / 15 / 10 / 5 % (2020) [K] |
| Defects (1D) | A <= 0.15, B <= 0.20, C <= 0.25, D <= 0.30 [K] | Fixed pattern damage by segment, per symbology | as 15415 |
| Decodability (1D) | A >= 0.62, B >= 0.50, C >= 0.37, D >= 0.25 [K] | UEC: A >= 0.62, B >= 0.50, C >= 0.37, D >= 0.25 [K] | same as 15415 |
| Axial non-uniformity (2D) | n/a | A <= 0.06, B <= 0.08, C <= 0.10, D <= 0.12 [K] | same |
| Grid non-uniformity (2D) | n/a | A <= 0.38, B <= 0.50, C <= 0.63, D <= 0.75 of a module [K; matches engine/src/quality.cpp:486] | same |
| Light | Monochrome 660 nm +/- 10 nm (ISO text: 630 to 700 nm) at 45 degrees to the symbol normal, uniform, for the default reference condition [K] | Same default: 45 degree ring light, 660 nm; the standard lets the application specify other angles and wavelengths and reports them in the grade string [K] | Needs the light that suits the mark: 30 degree (low angle) "30Q", 45 degree "45", 90 degree "90" (on axis) "D" diffuse; grade string names which [K] |
| Aperture | Circular, diameter about 80 % of the narrowest X; reference numbers 03 (0.075 mm), 05 (0.125), 06 (0.150), 08 (0.200), 10 (0.250), 20 (0.500) mm, chosen nearest to 80 % X [K] | Same rule: 80 % of X, rounded to the nearest reference aperture; 29158 also grades at a 50 % X aperture and reports both [K] | 80 % and 50 % aperture, "the better of two" reported [K] |
| Result string | "overall grade / aperture / light / angle", for example 3.0/06/660 [K] | same, for example 3.0/10/660/45 [K] | adds mark type and the DPM calibration symbol data [K] |
| Verifier calibration | Reflectance calibrated with a traceable reference card (the "conformance standard test print"), see ISO 15426 below [K] | same | Needs the Rcal, SRcal and MLcal values of a calibration symbol (the engine's calibration.cpp keeps them) [K] |

What our engine does today (read from the code and change notes, not from a bench against a
certified verifier):

| Part | Status | Where |
| --- | --- | --- |
| 1D: ten scans, Rmin, SC, ECmin, MOD, defects, decodability per symbology, decode, quiet zone, 0.8 X aperture, EAN/UPC chars 1, 2, 7, 8, DataBar including stacked rows | Have (process-control grade, not certified) | engine/src/grade1d.cpp, decodability.cpp; PR #82; stacked DataBar rows PR #95 |
| 2D: SC, MOD, RM, FPD, AN, GN, UEC, QZ for QR, Data Matrix, Micro QR, Aztec; PDF417 by rows | Have; GN and AN approximated; UEC from the decoder for QR, Micro QR, Data Matrix and Aztec (ISO 15415 form, PR #100; a read by the engine's own grid decoders keeps the module-count approximation) | engine/src/quality.cpp, grade_pdf417.cpp; PR #74, PR #100 |
| 29158 grading, 80 % default aperture, 50 % reported | Have on drawn symbols; real dot-peen and laser parts not yet checked; the cell-modulation codeword table is approximated | engine/src/grade_dpm.cpp; PR #96 |
| Light angle and wavelength of the standard | Missing as a reference set-up: we grade under the line light, so the grade is a process-control grade, not an ISO 15415 grade. Tool spec already says "process-control grades" | quality.cpp header comment |
| QR quiet zone | Partial: the engine checks a ring of 2 modules for QR; ISO/IEC 18004 asks 4 modules (Micro QR 2). A QR with a 2 to 3 module margin is graded clean but is out of specification | quality.cpp line 457 to 459 |
| Stacked DataBar, GS1 DataBar Stacked rows | Have (row by row, symbol takes the lowest row; PR #95) | change note `2026-10-30-claude-grade-databar-stacked` |

Action (all L4, L6):

1. Fix the QR quiet zone ring to 4 modules (0.2 pw with test; it can only make grades stricter, so it is line-safe).
2. Build a reference verification rig: a 45 degree ring light with 660 nm LED, a monochrome camera with an 80 % X aperture emulation, and a calibrated test card (section 1.2). Then grade the same codes with a certified handheld verifier (Cognex DataMan 475V, Honeywell/Axicon, Lightning Check) and fit the engine's numbers to it. 3 pw plus hardware.
3. Print the "grade string" in the report as the standards ask: grade/aperture/wavelength/angle, with "process control" when the light is not the reference one. 0.3 pw.

### 1.2 ISO/IEC 15426-1 (linear verifier conformance), 15426-2 (2D verifier conformance)

Links: [ISO/IEC 15426-1](https://www.iso.org/standard/54714.html) [K], [ISO/IEC 15426-2](https://www.iso.org/standard/54715.html) [K].

| Item | What it says | Tool / layer | Our status | Action |
| --- | --- | --- | --- | --- |
| Conformance test | A verifier must grade a set of reference symbols (Conformance Standard Test Print, supplied with a calibrated reflectance and grade) within a tolerance of the reference grade [K] | Code Verify, L4 | Missing: no reference print available | Buy a conformance test card (GS1 sells them; see section 13). Run the engine on it |
| Reflectance calibration | The verifier is calibrated with a card of known reflectance (such as the 0 %, 20 %, 40 %, 80 % steps) [K] | L0, L1 | Have the mechanism (calibration.cpp: image-based reflectance map); no reference card measured | Use a calibrated grey card (Edmund / Applied Image) and record Rcal |
| Repeatability | Calibration and grading must repeat inside the standard's tolerance [K] | L4 | Missing as a test | Add to TEST-ACCEPTANCE-PLAN: 30 repeated gradings of one card, report the spread |
| Claim wording | Only a device passing 15426 may say "ISO verifier" [K] | Marketing | Not claimed. Keep so | The product says "ISO-style process-control grading" until a conformance test passes |

### 1.3 ISO 15416 and 15415 vendor reading (where to learn, not to copy)

| Document | Link | What to take | Status |
| --- | --- | --- | --- |
| GS1 "Barcode verification" and the GS1 Verified programme | [gs1.org](https://www.gs1.org/services/verified-by-gs1) [K] | Which GS1 tables name which minimum grade; verifier list | Reference |
| Cognex "Barcode verification" white paper and DataMan verifier manuals | [cognex.com resources](https://www.cognex.com/resources/white-papers) [opened: HTTP 429, not read] | The 29158 set-ups, light heads and the grade string | Reference; fetch again later |
| Webscan / Axicon "Barcode quality" notes | [K] | Practical meaning of each parameter | Reference |
| Lightning Check | [lightning-check](https://lightningcheck.com) [K] | Picture-based verification aid | Reference |

---

## 2. Symbology standards

Each row is a symbology, with the numbers that a reader or a grader must know. "Ours" says where the engine meets it.

| Standard | Link | Key numbers [K unless marked] | Tool and layer | Ours | Action |
| --- | --- | --- | --- | --- | --- |
| ISO/IEC 16022:2024 Data Matrix (ECC 200) | [ISO](https://www.iso.org/standard/44230.html) [K]; [wiki](https://en.wikipedia.org/wiki/Data_Matrix) [O] | Square 10x10 to 144x144, rectangles 8x18 to 16x48 [O]; quiet zone 1 module; L finder plus alternating clock; Reed-Solomon; 2024 is edition 3 [O] | Read, Verify; L2, L3, L4 | Have (libdmtx, own decoders, grade 15415) | Check the 2024 edition's changes (buy) |
| ISO/IEC 18004:2024 QR Code (Model 2, Micro QR) | [ISO](https://www.iso.org/standard/83389.html) [K] | Versions 1 to 40 (21 to 177 modules); quiet zone 4 modules (Micro QR 2); EC levels L 7 %, M 15 %, Q 25 %, H 30 %; Micro QR M1 to M4 | Read, Verify | Have (zxing-cpp, own qrgrid / qrsoft); QZ check 2 not 4 | Fix QZ to 4 (see 1.1) |
| ISO/IEC 23941:2022 rMQR | [wiki](https://en.wikipedia.org/wiki/Rectangular_Micro_QR_Code) [O] | 32 versions, height 7 to 17 modules, width 27 to 139 [O]; quiet zone 2 modules [O]; EC level M (about 15 %) or H (about 30 %) [O] | Read on narrow cartons and tubes; L3 | Missing: zxing-cpp decodes rMQR, but engine/src/zxformats.cpp:42 drops every rMQR read; our own finder and the grade do not handle it | Enable rMQR behind its own format flag, then add it to the grade list (0.5 pw) if a customer uses it |
| ISO/IEC 15417 Code 128 | [ISO](https://www.iso.org/standard/43896.html) [K] | 11 modules per character, quiet zone 10 X, check character mod 103, sets A, B, C, FNC1 | Read, Verify | Have (zxing, rules1d, decodability) | None |
| ISO/IEC 15420 EAN/UPC | [ISO](https://www.iso.org/standard/46143.html) [K] | EAN-13 95 modules; quiet zones 11 X left, 7 X right (EAN-8 7 / 7; UPC-A 9 / 9) | Read, Verify | Have; the engine uses exactly 11 and 7 for EAN-13 (grade1d.cpp line 133) | None |
| ISO/IEC 16388 Code 39 | [ISO](https://www.iso.org/standard/43897.html) [K] | 9 elements, 3 wide; wide:narrow 2.0 to 3.0; quiet zone 10 X | Read | Have | None |
| ITF / Interleaved 2 of 5 (ISO/IEC 16390) | [ISO](https://www.iso.org/standard/43899.html) [K] | ITF-14 with bearer bars; quiet zone 10 X; wide:narrow 2.25 to 3.0 | Read, Verify | Have | Bearer-bar check in the verifier (see GS1 tables) |
| ISO/IEC 15438 PDF417 | [ISO](https://www.iso.org/standard/65502.html) [K] | 3 to 90 rows, 1 to 30 columns, 17 modules per codeword, EC level 0 to 8, quiet zone 2 modules | Read, Verify | Have (decode via zxing; grade_pdf417.cpp by rows; codewords checked against the full ISO/IEC 15438 table, PR #100) | None |
| ISO/IEC 24778 Aztec Code | [ISO](https://www.iso.org/standard/41548.html) [K] | Compact 15 to 27, full 19 to 151 modules; bullseye finder; no quiet zone | Read, Verify | Have (grade in quality.cpp) | None |
| ISO/IEC 24724 GS1 DataBar | [ISO](https://www.iso.org/standard/77338.html) [K] | Omnidirectional 96 modules, Limited 74, Expanded 102 to 534; finder widths 15 modules incl. neighbours; no quiet zone beyond guards | Read, Verify | Have, single row and stacked rows (PR #95) | None |
| AIM DotCode (ISS DotCode 4.0) | [wiki](https://en.wikipedia.org/wiki/DotCode) [O]; [AIM](https://www.aimglobal.org) [K] | Sparse round dots; no finder; each side at least 5 dots; width + height odd; quiet zone at least 3 dots [O]; Reed-Solomon over GF(113); 3 + ND/2 check words [O]; aimed at CIJ, laser and drilled marking [O] | Read on CIJ cartons and cans; L2, L3 | Have (dotcode.cpp). No grade: DotCode grading is in AIM's spec, not in 15415 | Add DotCode grade (dot size, dot fill, position error per AIM spec): 1.5 pw |
| Laetus Pharmacode spec | [wiki](https://en.wikipedia.org/wiki/Pharmacode) [O]; [Laetus](https://www.laetus.com) [K] | Integer 3 to 131070, 2 to 16 bars, narrow bar = 2^n, wide bar = 2 x 2^n [O]; bar width and gap ratios from the vendor sheet | Read on folding cartons and leaflets | Have (pharmacode.cpp) | Get the vendor sheet for the width tolerance |
| ISO/IEC 15424 symbology identifiers | [ISO](https://www.iso.org/standard/43892.html) [K] | `]` + code char + modifier; for example `]d2` GS1 Data Matrix, `]Q3` GS1 QR, `]C1` GS1-128, `]E0` EAN-13 | Read, GS1 check; L3, L4 | Have: `cr_code_ext.symbology_id` (engine/include/codereader.h, PR #101); the .NET GS1 check fails a symbol without FNC1 first, or one whose identifier is not known (dotnet/VisionCore/Tools/CodeTool.cs) | None |
| ISO/IEC 15418, 15434, 15459 | [ISO 15418](https://www.iso.org/standard/60936.html) [K] | Data identifiers and application identifier syntax; 15434 is the transfer syntax for batch data | GS1 check | Partial: AI parsing from GS1 dictionary (PR #123); DI syntax (ASC MH10) not built | Add only if a customer uses DIs |
| ISO 1073 / ISO 1831 OCR-A, OCR-B | [ISO 1831](https://www.iso.org/standard/6185.html) [K] | OCR-B: stroke 0.15 mm, character height 1.9 to 2.4 mm for the small size; OCR-A used on older printed matter; print quality grades are in ISO 1831 and the print contrast signal (PCS) is the key measure: PCS = (Rw - Rb) / Rw | Check text OCV; L3, L4 | Partial: OCV scores contrast, ink, pitch per character (PR #81) but has no OCR-B font tolerance table | Teach OCR-B (and OCR-A) font and check stroke and pitch against ISO 1831 (0.5 pw) |
| ISO/IEC 19762 AIDC vocabulary | [ISO](https://www.iso.org/standard/50185.html) [K] | Words | Docs | Reference | Use for the glossary |

---

## 3. GS1 standards

### 3.1 GS1 General Specifications (current release 25 or 26; owner should buy the PDF)

Link: [GS1 General Specifications](https://www.gs1.org/standards/barcodes-epcrfid-id-keys/gs1-general-specifications) [K, page blocked today: 403] and the [syntax dictionary](https://ref.gs1.org/tools/gs1-barcode-syntax-resource/) [K] that PR #123 uses.

| Topic | Numbers (always check the table of the current release) [K] | Tool, layer | Ours |
| --- | --- | --- | --- |
| AIs and data content | About 224 rows in the syntax dictionary (release 2026-01-27 per PR #123); FNC1 after variable-length AIs; fixed lengths for 00, 01, 02, 11 to 17, 20 | GS1 check, L4 | Have (Gs1Check.cs, Gs1Dictionary.g.cs, Gs1Associations.cs) |
| Symbol grade by application (written as grade/aperture/wavelength) | General retail scanning: 1.5/06/660 (EAN/UPC at the POS); other 1D and 2D: 1.5/10/660 or 1.5/06/660 by X; GS1-128 logistic labels at X >= 0.495 mm: 1.5/10/660 (C); healthcare GS1 DataMatrix: 1.5/05/660 or ISO 15415 grade C (1.5) minimum for X < 0.495 | Verify, L4 | Partial: the Read code tool has "lowest print grade"; GS1 per-application tables not coded as presets |
| X-dimension | EAN-13 min 0.264 mm, target 0.330, max 0.660 mm (80 to 200 %); GS1-128 min 0.250 mm for small, target 0.495 for logistic labels (max 1.016); ITF-14 0.495 to 0.990 mm; GS1 DataMatrix 0.254 to 0.615 mm general, 0.380 mm target for healthcare small packs; GS1 QR 0.380 target | Teach (L5), Verify | Missing as presets. We know X from the code itself (px per module) but not the mm, because the scale calibration is per job | Add a mm-based preset table to the Verify tool; uses the calibration scale of L1 (0.5 pw) |
| Quiet zones | EAN-13 11 / 7 X; GS1-128 10 X; ITF-14 10 X (bearer bars 2 X thick, 4.8 mm); GS1 DataMatrix 1 X all sides; GS1 QR 4 X | Verify | Have for 1D; DM 1 module; QR 2 (should be 4) |
| Symbol placement | Logistic label: SSCC at 32 to 800 mm above the base; at least 19 mm from edges; GS1 DataMatrix on pharma pack: on the face the pack is scanned from, one symbol, not on a fold | Docs, L6 | Docs only |
| Human-readable interpretation | AIs in brackets in the HRI, not in the symbol; OCR-B 3 to 4 mm for GS1-128 | Check text | Have: "(01)..." text inside the symbol fails (change note gs1-check) |
| Sunrise 2027 | Retail point-of-sale can scan 2D (GS1 DataMatrix, GS1 QR with Digital Link) from 2027 | Strategy | Reference |

Action: store the per-application tables as data (a JSON preset with X min/target/max, quiet zones, min grade string) next to Gs1Dictionary.g.cs and let the Teach page choose a preset ("GS1 healthcare 2D", "GS1 logistic label", "Retail EAN-13"). 1 pw. Buy the PDF (free to read on gs1.org, but print is paid).

### 3.2 GS1 Digital Link

Link: [GS1 Digital Link standard](https://www.gs1.org/standards/gs1-digital-link) [opened: HTTP 403]; [ref.gs1.org](https://ref.gs1.org/standards/digital-link/) [K].

| Item | What it says [K] | Ours |
| --- | --- | --- |
| URI | `https://example.com/01/09506000134352/10/ABC/21/123?17=261231`: path carries the key (01 GTIN, 8006, 8010, 00 SSCC) then qualifiers (10, 21, 22), query carries data attributes | Have: Gs1Check reads Digital Link keys (PR #123); path order checked |
| Carriers | GS1 QR, GS1 DataMatrix, NFC | Have for QR and DM |
| Compression | Optional compressed URI form | Missing: not needed now |

### 3.3 GS1 Healthcare guideline

Link: [GS1 Healthcare](https://www.gs1.org/industries/healthcare) [K]; [GS1 Healthcare GTIN allocation rules](https://www.gs1.org/standards/gs1-healthcare-gtin-allocation-rules) [K].

| Item | Content [K] | Ours |
| --- | --- | --- |
| Pack-level 2D | GS1 DataMatrix with (01) GTIN, (17) expiry, (10) batch, (21) serial | Have the check |
| Carton / case | GS1-128 with SSCC (00) and contents (02)/(37), or GS1 DataMatrix | Have |
| Quality | Direct-marked: ISO/IEC TR 29158; printed: ISO/IEC 15415 grade C at 1.5/05/660 | Partial |
| Aggregation | Parent SSCC lists child serials | Partial (aggregation flow exists in the product) |

---

## 4. Pharma serialisation and aggregation regulation

Principle for this product: these rules decide what the line must read, verify and report. They do not change the line-safety rule (NG when in doubt).

| Market | Rule and link | What it requires (numbers) | Tool and layer | Ours | Action |
| --- | --- | --- | --- | --- | --- |
| EU FMD | [Delegated Regulation (EU) 2016/161](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:32016R0161) [opened: page returned empty; text from knowledge] | Unique identifier (UI) = product code (GTIN or NTIN), serial number (numeric or alphanumeric, at most 20 characters, randomised so the probability of guessing it is negligible and in any case below 1 in 10,000, Art. 4(b) [K]), national reimbursement number if required, batch number, expiry date; carried in a 2D Data Matrix (ECC200) as machine-readable and printed as human-readable (Article 7); print quality: the "ISO/IEC 15415 grade C or better" rule is guidance (the Commission's Q&A refers to ISO/IEC 15415 and ISO/IEC 16022), not the Regulation text; anti-tampering device on each pack; verification at pack level against the repository (EMVS) at dispense [K] | Read code, Verify, Check text; L3, L4, L6 | Have: DM read, GS1 check (AI 01, 17, 10, 21), grade. Missing: the check that serial is at least randomised and non-repeating inside one batch (duplicate check exists, see memory "Barcode duplicates") | Add "FMD profile" preset (GS1 DM, 4 AIs, 15415 >= C, human-readable fields). 0.5 pw |
| US DSCSA | [FDA DSCSA overview](https://www.fda.gov/drugs/drug-supply-chain-integrity/drug-supply-chain-security-act-dscsa) [O] | Package-level tracing on a product identifier: NDC, serial number, lot number, expiry in a 2D data matrix [K; the page opened today does not list these fields]; electronic interoperable tracing; illegitimate products reported to FDA within 24 hours [O]; the 2023 enhanced system required lot-level tracing and verification, with some phased exemptions [K] | Read code, Verify; L3, L4, L6 | Have the data checks. A US customer needs the lot and expiry rules in the GS1 dictionary: Have | Profile "DSCSA" preset; transaction data is the ERP's job (not ours) |
| India: DGFT export barcoding | [GS1 India manual on DGFT track and trace](https://www.gs1india.org/media/DGFT_Drug_Track_N_Trace_Implementation_Manual.pdf) [opened: 404]; [Taxguru DGFT procedure](https://taxguru.in/dgft/dgft-procedure-relating-to-tracing-and-tracking-of-export-consignment-of-pharmaceuticals-and-drugs-regarding.html) [S]; [Pharmexcil barcoding](https://pharmexcil.com/barcoding/) [S] | Exporters of finished formulations use GS1 barcodes at primary, secondary and tertiary level; DGFT notification of January 2011, staged in 2011 and 2012; primary pack: GS1 Data Matrix with GTIN and serial; API, bulk drugs and intermediates exempt; exporters upload the parent-child (aggregation) data to the central portal; improper barcoding on secondary or tertiary packs can lead to the consignment being returned (customs notice 2018) [S] | Read code, aggregation, L3, L4, L6 | Have (aggregation cases 12, 24, 45, 48, 96, GS1-128 SSCC). **Status to confirm**: Sun Teknovation's page says the DGFT export track and trace was withdrawn (chapter 01c) | Confirm whether the rule still applies, then check the current portal name and file format with the owner's regulatory contact; build an export file in the portal's format (1 pw once format known) |
| India: iVEDA portal | [rfxcel India summary](https://rfxcel.com/?p=6571) [O] | iVEDA is the successor reporting portal for drug exports; the reporting deadline was extended to 31 March 2023 and postponed several times since 2018 [O] | L6 reporting | Missing: no export of the parent-child file (status to confirm, as DGFT above) | Same as above |
| India: CDSCO QR on formulations (top 300 brands, Schedule H2) | [CDSCO FAQ of 21 July 2023](https://cdsco.gov.in/opencms/resources/UploadCDSCOWeb/2018/UploadPublic_NoticesFiles/Final%20FAQs%20on%20QR%20code%2021.07.2023.pdf) [opened: PDF is compressed, text not readable]; [lexplosion on the FAQ](https://lexplosion.in/cdsco-issues-faq-for-implementation-barcode-requirement-on-the-label-of-300-brands-of-drugs/) [S]; [Medianama](https://www.medianama.com/2025/08/223-govt-qr-code-top-300-pharma-brands/) [S] | GSR 823(E) of 17 November 2022: makers of Schedule H2 formulations print or affix a bar code or QR code; mandatory for the 300 listed brands; effective 1 August 2023 per CDSCO [S]; data points listed by secondary sources: product identifier, generic and brand name, batch, manufacture and expiry date, manufacturer name and address, manufacturing licence number (the number of data points is reported as 8, 9 or 11 by different sources: read the notification text); on the primary pack, or on the secondary pack when space is short [O: rfxcel] | Read code, GS1 check, Check text; L3, L4 | Have QR and DM read; GS1 check works for GS1-format payloads. A non-GS1 payload (plain text, URL, JSON) needs a payload-format check | Add a "CDSCO QR" profile: required fields, regex per field, expiry after manufacture. Needs the exact field list first (0.5 pw) |
| India: June 2026 Schedule H2 widening | [Onmanorama 2026-06-26](https://www.onmanorama.com/upsc/current-affairs/2026/06/26/qr-codes-now-mandatory-for-vaccines-cancer-drugs.amp.html) [S] | Reported: Schedule H2 widened to categories (vaccines, antimicrobials, narcotic, anticancer) with phased dates, July 2027 and July 2028 [S: not opened; one secondary source] | Same | Same | Verify with CDSCO before quoting a date to a customer |
| India: APIs | [rfxcel](https://rfxcel.com/?p=6571) [O] | QR codes on API labels at each packaging level, 11 data points, from 1 January 2023 per that summary; [CDSCO notice, not opened] | Read, Check text | Same as above | Confirm with CDSCO |
| India: Legal Metrology (Packaged Commodities) Rules 2011 | [Legal Metropology portal](https://consumeraffairs.nic.in/organisation-and-units/division/legal-metrology/legal-metrology-packaged-commodities) [K, not opened] | Declarations on the pack: name and address of manufacturer or packer, net quantity (with units), month and year of manufacture or packing, MRP "inclusive of all taxes" with the "MRP Rs. xx.xx" format, consumer-care details; minimum character height by pack area (for example 1 mm for net quantity on small packs, rising with the pack area); batch and expiry for drugs under the Drugs Rules | Check text (OCV), L3, L4 | Have: date and MRP OCR / OCV. Missing: a rules preset (format "MRP Rs.", digits and decimals, month-year order) | Add "Legal Metrology" profile: regex for MRP, date order, min character height in mm after scale calibration. 0.5 pw |
| India: Drugs Rules 1945, Schedule P and rule 96 | [CDSCO drugs rules](https://cdsco.gov.in) [K] | Labels: batch, manufacturing date, expiry date, MRP, schedule marks; the expiry format "MM/YYYY" on strips | Check text | Partial | Add to the Legal Metrology profile |
| India: state excise QR and hologram labels | Varies by state (K) | Excise labels with QR or Data Matrix on liquor, cannabis and some FMCG; tamper hologram; the label gets read at the line | Read, Find | Have read; hologram presence is a Find / classify task | Out of scope unless a customer appears |
| WHO / other markets | [WHO GSMS](https://www.who.int/teams/regulation-prequalification/incidents-and-SF) [K]; Turkey ITS, Brazil SNCM (2D Data Matrix, GTIN, serial, batch, expiry), Saudi RSD (GS1 DM), China NMPA codes (end-to-end coding); UAE Tatmeen; Russia Chestny Znak (DM with crypto tail) [K] | Each market: a GS1 DM with a defined AI set; some add a cryptographic tail (Russia 91/92) or national keys | L4 profile | Only the GS1 part is checked | A profile table later; each market 0.3 pw |

---

## 5. Records, data integrity and software validation

| Document | Link | Key requirements [K] | Layer | Ours | Action |
| --- | --- | --- | --- | --- | --- |
| 21 CFR Part 11 (electronic records and signatures) | [eCFR](https://www.ecfr.gov/current/title-21/chapter-I/subchapter-A/part-11) [opened: redirect, not read] | 11.10: validation, accurate copies, protection of records, access limits, secure computer-generated time-stamped audit trails that do not obscure old values, operational and authority checks, device checks, training, written policies; 11.50: signature shows printed name, date and time, meaning (review, approval); 11.70: signature linked to its record; 11.100 to 11.300: unique ID, identity check before first use, two components for non-biometric signature, password ageing and loss management | L6, L5 (job and model approval) | Partial: audit trail is append-only and hash-chained (ARCHITECTURE.md 505); Part 11 mode is "optional"; section 12 open item "is Part 11 required in the first release?" | Make Part 11 mode a real build: signature manifestation (name, time, meaning), reason-for-change, no deletion, session lock, password rules. 3 pw with a gap review against the rule text |
| EU GMP Annex 11 (computerised systems) | [EC PDF](https://health.ec.europa.eu/system/files/2016-11/annex11_01-2011_en_0.pdf) [opened: compressed, text not readable] | Risk management (1); validation lifecycle (4); data storage and back-up (7); accuracy checks (6); audit trails for GMP-relevant changes and deletions (9); change and configuration management (10); periodic evaluation (11); security (12); incident management (13); electronic signature equal to a handwritten one (14); batch release by a qualified person (15); business continuity (16); archiving (17) [K] | L6, L5 | Partial | Cover in the validation pack below |
| ALCOA+ (data integrity) | [FDA guidance "Data Integrity and Compliance With Drug CGMP", Dec 2018](https://www.fda.gov/regulatory-information/search-fda-guidance-documents/data-integrity-and-compliance-drug-cgmp-questions-and-answers) [K]; [MHRA GxP data integrity guidance 2018](https://www.gov.uk/government/publications/guidance-on-gxp-data-integrity) [K] | Attributable, Legible, Contemporaneous, Original, Accurate, plus Complete, Consistent, Enduring, Available | L6 | Partial: images and results stored with time; original image kept for NG; a missing "who changed which tolerance" in every record is a gap if jobs are edited on the line | Check every NG and every job change has user, time, old and new value (0.5 pw) |
| GAMP 5, second edition | [ISPE GAMP 5](https://ispe.org/publications/guidance-documents/gamp-5-guide-2nd-edition) [K] | Software category 4 (configured product) for our product; URS, FS / config spec, risk assessment, IQ / OQ / PQ, traceability matrix, change control, supplier assessment | All | Missing: no validation pack | Prepare a "validation kit": URS (from requirements), FS (tool specs), IQ checklist, OQ scripts (the TEST-ACCEPTANCE-PLAN), PQ with challenge set; 2 pw |
| PIC/S PI 041 data integrity | [PIC/S](https://picscheme.org/en/publications) [K] | Aligns with ALCOA+ | L6 | as above | Reference |
| 21 CFR Part 210 / 211 (cGMP; 211.130 packaging and labelling operations; 211.125 labelling issuance) | [eCFR 211](https://www.ecfr.gov/current/title-21/chapter-I/subchapter-C/part-211) [K] | Labelling inspection, 100 % reconciliation of labels, electronic verification of packaging codes allowed (211.122, 211.130) | L4, L6 | Have the read-and-reject function; no label reconciliation report | Add a batch reconciliation report (issued, used, rejected, returned): 0.5 pw |
| Schedule M (India revised, 2023) | [CDSCO Schedule M](https://cdsco.gov.in) [K] | Revised GMP, computerised system validation chapter | L6 | Same as Annex 11 | Same pack |

---

## 6. Camera, interface and automation standards

| Standard | Link | Key points | Layer | Ours | Action |
| --- | --- | --- | --- | --- | --- |
| EMVA 1288 release 4.0 (June 2021) | [EMVA](https://www.emva.org/standards-technology/emva-1288/) [O: the page says "Standard for Measurement and Presentation of Specifications for Machine Vision Sensors and Cameras", release 4.0, June 2021; the parameter list is in the full standard] | The standard measures quantum efficiency, system gain, temporal dark noise, dark current, DSNU, PRNU, SNR, dynamic range and linearity [K] | L0, L1 | Missing: we have no camera characterisation. The Hikrobot MV-CS200 datasheet is the only number we use | Run a measured EMVA-style test (photon-transfer curve with a flat light and two exposures) on each camera for dark and flat-field (L1) and to set exposure and gain for the best SNR. 1 pw; helps the L1 dark and flat module |
| GenICam (GenApi, SFNC, GenTL, PFNC) | [EMVA GenICam](https://www.emva.org/standards-technology/genicam/) [K] | Camera features by standard name (ExposureTime, Gain, TriggerMode, TriggerSource, LineSelector, UserSetLoad); GenTL producer / consumer | L0 | Partial: the camera layer uses the Hikrobot MVS SDK only (`MvCameraControl` in dotnet/HikCamera/HikCamera.cs); no GenTL layer | Use SFNC names only; avoid vendor-only features |
| GigE Vision 2.x | [AIA GigE Vision](https://www.automate.org/vision/vision-standards/gige-vision) [opened: HTTP 403] | Streaming GVSP, control GVCP; 2.0 adds action commands (several cameras fire on one broadcast), 10GigE, RDMA; with PTP IEEE 1588 for a common clock and scheduled action commands [K] | L0 two-camera sync | Planned (requirements section 8: action commands and PTP) | Test needs two GigE cameras (the MV-CS200-10UM is USB3, so it cannot take action commands): scheduled action command within a few microseconds; set jumbo frames 9000, packet delay, one NIC per camera |
| USB3 Vision | [AIA](https://www.automate.org/vision/vision-standards/usb3-vision) [K] | Same GenICam model on USB 3; cable 3 to 5 m without active cable | L0 | Used (MV-CS200-10UM is a USB3 Vision camera) | None |
| IEC 61131-3 | [IEC](https://webstore.iec.ch/publication/4552) [K] | PLC languages (LD, ST, FBD, SFC); timers, edge detection | L6 PLC | The Delta DVP handshake is in docs/vision-system/PLC-DELTA-DVP.md | None |
| Modbus Application Protocol 1.1b3 and Modbus TCP | [modbus.org](https://modbus.org/specs.php) [K] | Function codes 03, 06, 16, 05, 15; port 502; 16-bit registers; max 125 registers per read, 123 per write | L6 | Have (Modbus TCP to Delta DVP) | Keep the handshake with a heartbeat and a watchdog (section 11) |
| OPC UA Machine Vision companion specification (OPC 40100-1, version 1.0, 2019-08-01) | [OPC reference](https://reference.opcfoundation.org/MachineVision/v100/docs/) [O: namespace `http://opcfoundation.org/UA/MachineVision`, covers control, configuration, recipe and result management per its title] | An information model for a vision system: state machine (Init, Ready, Busy...), recipe management, result transfer, configuration, health; part 1 version 1.1 also exists [K] | L6 | Missing: planned as OPC UA in the Multi-Camera and Enterprise licence (requirements section 10) | Build an OPC UA server (open62541) that exposes recipe load, trigger, result (OK/NG, codes) using the 40100 names. 2 pw |
| VDMA 40100 (same family) | [VDMA](https://www.vdma.org) [K] | The VDMA name for the companion spec | L6 | same | same |
| IEC 62443 (industrial security) | [ISA/IEC 62443](https://www.isa.org/standards-and-publications/isa-standards/isa-iec-62443-series-of-standards) [K] | 62443-4-1 secure development life cycle; 62443-4-2 component requirements (identification, use control, data integrity, restricted data flow); security levels SL 1 to 4; zones and conduits | L6 | Partial: roles, API key, signed licence; no threat model | A short threat model and a hardening guide for the line PC (disable USB storage, one NIC to the PLC, signed updates): 1 pw |
| Functional safety note (IEC 61508, ISO 13849-1) | [ISO 13849](https://www.iso.org/standard/69883.html) [K] | A vision tool that says "pass" is not a safety function under these standards; a reject gate is a quality function. The line's emergency stop and guards stay on the machine's safety PLC | Line safety | Note: our line-safety rule is a product-quality rule, not functional safety | Say this in the manual: no vision result is allowed to bypass a machine safety circuit |
| ISO 9001:2015 / ISO 13485:2016 | [ISO 13485](https://www.iso.org/standard/59752.html) [K] | Quality system; 13485 for medical devices (applies if the customer is a device maker: design control, validation of production software 7.5.6 and 4.1.6) | Process | Partial | Keep the change notes as design records; 13485 customers will ask for a software validation plan (same pack as GAMP 5) |
| ISO 14971 | [ISO](https://www.iso.org/standard/72704.html) [K] | Risk management | Process | Missing | Use for the false-accept hazard analysis |

---

## 7. Colour and print standards (for the colour tools)

| Standard | Link | Key numbers [K] | Tool, layer | Ours | Action |
| --- | --- | --- | --- | --- | --- |
| CIE 15:2018 Colorimetry | [CIE](https://cie.co.at/publications/colorimetry-4th-edition) [K] | XYZ, CIELAB, illuminant D50 / D65, observer 2 degree | Colour tools; L1, L3 | Missing (no colour tool in the engine or VisionCore yet) | Convert to Lab with a known light |
| CIEDE2000, CIE 142-2001 | [paper by Sharma, Wu, Dalal 2005](https://hajim.rochester.edu/ece/sites/gsharma/ciede2000/) [K] | dE00 formula with weighting; visual limits: dE00 about 1 just visible, 2 to 3 noticeable by eye; print tolerance often dE00 <= 2 to 3 | Colour check; L3, L4 | Check status in INDUSTRY-METHODS (dE76 / dE2000) | Use dE00 only |
| ISO 12647-2 (offset), -6 (flexo), -7 (digital proof) | [ISO 12647-2](https://www.iso.org/standard/72848.html) [K] | Target CIELAB per substrate (paper types 1 to 5) and tolerance dE*ab 5 solid; dE 3 on the paper white in the control wedge | Packaging colour match; L4 | n/a | For a customer who asks "does this print match the proof" |
| ISO 13655 (spectral measurement) | [ISO](https://www.iso.org/standard/65430.html) [K] | M0, M1, M2, M3 measurement conditions (UV content, polarisation filter); 45/0 or 0/45 geometry | L0 | Reference | Define a colour check as "camera relative to a golden part", not an absolute dE, unless a spectrophotometer is added |
| ISO 3664 (viewing conditions) | [ISO](https://www.iso.org/standard/72848.html) [K] | D50, 2000 lx for reviewing proofs | Human review | Reference | None |
| GS1 / PANTONE for barcodes | n/a | Black on white for 660 nm: red ink is invisible; blue and green dark ink are readable | L0 | Light colour selection in INDUSTRY-METHODS | Use a red-light check for red and orange inks |

---

## 8. Machine-vision lighting and lens application notes (practical documents)

All links [K], not opened (vendor sites returned 403). Read them for set-up rules; do not copy text.

| Document | Link | What to take | Layer |
| --- | --- | --- | --- |
| Edmund Optics, "Understanding the types of lighting in machine vision" | [link](https://www.edmundoptics.com/knowledge-center/application-notes/imaging/understanding-the-types-of-lighting-in-machine-vision/) | Bright field, dark field, backlight, diffuse dome, coaxial and structured light; which light for which surface | L0 |
| Edmund Optics, "Telecentric lenses", "Depth of field", "Resolution" notes | [knowledge center](https://www.edmundoptics.com/knowledge-center/application-notes/imaging/) | Depth of field versus f-number; telecentric for measurement (no perspective error); MTF at 100 lp/mm | L0 |
| CCS Inc. lighting guides | [ccs-grp.com](https://www.ccs-grp.com/) | LED wavelength choice (white, red 625, blue 470, IR 850); polarised and coaxial for shiny foil | L0 |
| Smart Vision Lights application notes | [smartvisionlights.com](https://smartvisionlights.com/) | Ring, dome, bar lights; strobe overdrive (a 10x current pulse); working distance and angle | L0 |
| Cognex "Lighting" guide, "Direct part mark" notes | [cognex.com](https://www.cognex.com/resources) | Dark-field for dot-peen, dome for laser etch, polarisation for shiny metal | L0 |
| Keyence "Vision lighting" guide | [keyence.com](https://www.keyence.com/ss/products/vision/fb/) | Same set | L0 |
| Basler / Teledyne "camera selection" guides | [Basler](https://www.baslerweb.com/en/learning/) | Pixel size, rolling versus global shutter (global for moving codes), CMOS read noise, exposure-motion blur rule: blur in px = speed x exposure / pixel size in mm | L0 |

Quantified rule for our line (225 parts per minute): a part moving at line speed v needs exposure t such that v x t is below 0.3 pixel in the object plane; strobe with a short pulse and a global-shutter sensor. The MV-CS200-10UM is a rolling-shutter camera (Sony IMX183, requirements section 8), so a short strobe works only when every row is exposed during the flash ([05-HARDWARE.md](05-HARDWARE.md) section 4.4).

---

## 9. Key academic papers (15 to 20 per family, with links)

Mark: [K] link written from memory (title and venue are the facts; DOI or arXiv number should be checked on opening).

### 9.1 Barcode and 2D code finding and reading (tools Read code, Find; L2, L3)

| Paper | Link | Idea | Ours |
| --- | --- | --- | --- |
| Joseph and Pavlidis, "Bar code waveform recognition using peak locations", IEEE PAMI 1994 | [IEEE](https://ieeexplore.ieee.org/document/295069) [K] | Decode 1D from edge and peak positions, robust to blur | Partial: our 1D uses edge positions |
| Zamberletti, Gallo, Albertini, "Robust angle invariant 1D barcode detection", ACPR 2013 | [IEEE](https://ieeexplore.ieee.org/document/6778456) [K] | Hough-space bar detection at any angle | Method known |
| Gallo and Manduchi, "Reading 1D barcodes with mobile phones using deformable templates", IEEE PAMI 2011 | [IEEE](https://ieeexplore.ieee.org/document/5648398) [K] | Deformable template; sub-pixel edges | Method known |
| Katona and Nyul, "Efficient 1D and 2D barcode detection using mathematical morphology", ISMM 2013 | [Springer](https://link.springer.com/chapter/10.1007/978-3-642-38294-9_14) [K] | Morphological finder for 1D and 2D | Have similar finder |
| Creusot and Munawar, "Real-time barcode detection in the wild", WACV 2015 | [IEEE](https://ieeexplore.ieee.org/document/7045916) [K] | Detect 1D with a learned classifier on a gradient map | Method known |
| Hansen et al., "Real-time barcode detection and classification using deep learning", IJCCI 2017 | [SciTePress](https://www.scitepress.org/Papers/2017/65740/65740.pdf) [K] | YOLO for 1D and 2D codes | Have AI locator (ai_locator.cpp) |
| Zharkov and Zagaynov, "Universal barcode detector via semantic segmentation", ICDAR 2019 | [arXiv:1906.06281](https://arxiv.org/abs/1906.06281) [K] | Segmentation network for any code, 1D and 2D | Have similar |
| Liyanage, "Efficient decoding of blurred, pitched, and scratched barcode images", ICIIS 2007 | [IEEE](https://ieeexplore.ieee.org/document/4579467) [K] | Decode with a model of blur | Have PSF estimate (psf.cpp); deblur in decode.cpp |
| Tekin and Coughlan, "A bayesian algorithm for reading 1D barcodes", CRV 2009 | [IEEE](https://ieeexplore.ieee.org/document/5191099) [K] | Probabilistic decode with a prior on widths | Method known |
| Zhang, "A flexible new technique for camera calibration", IEEE PAMI 2000 | [Microsoft](https://www.microsoft.com/en-us/research/publication/a-flexible-new-technique-for-camera-calibration/) [K] | Planar target calibration | Have (calibration.cpp) |
| Lowe, "Distinctive image features from scale-invariant keypoints", IJCV 2004 | [Springer](https://link.springer.com/article/10.1023/B:VISI.0000029664.99615.94) [K] | Feature matching for pattern find | Method known |
| Otsu, "A threshold selection method from gray-level histograms", IEEE SMC 1979 | [IEEE](https://ieeexplore.ieee.org/document/4310076) [K] | Global threshold; ISO 15416 global threshold is the mid point of Rmax and Rmin | Have |
| Sauvola and Pietikainen, "Adaptive document image binarization", Pattern Recognition 2000 | [ScienceDirect](https://www.sciencedirect.com/science/article/pii/S0031320399000552) [K] | Local threshold for uneven light | Have local threshold; check |
| Richardson 1972 and Lucy 1974 (deconvolution); Krishnan and Fergus, "Fast image deconvolution using hyper-Laplacian priors", NIPS 2009 | [NIPS](https://papers.nips.cc/paper/2009/hash/3dd48ab31d016ffcbf3314df2b3cb9ce-Abstract.html) [K] | Non-blind deblur | Have Wiener start plus Richardson-Lucy (engine/src/decode.cpp, about lines 135-180, step `CR_STEP_DEBLUR`) |
| Kupyn et al., "DeblurGAN-v2", ICCV 2019 | [arXiv:1908.03826](https://arxiv.org/abs/1908.03826) [K] | Learned deblur | Missing: an option for L1 |
| Zamir et al., "Restormer", CVPR 2022 | [arXiv:2111.09881](https://arxiv.org/abs/2111.09881) [K] | Transformer restoration | Missing |
| Reed and Solomon 1960; Berlekamp-Massey; Guruswami-Sudan list decoding, IEEE IT 1999 | [IEEE](https://ieeexplore.ieee.org/document/782103) [K] | Soft / list decoding beyond the hard limit, as in qrsoft.cpp | Have soft decoding |
| Debevec and Malik, "Recovering high dynamic range radiance maps", SIGGRAPH 1997 | [paper](https://www.pauldebevec.com/Research/HDR/) [K] | HDR from multi-exposure | Have multiframe (multiframe.cpp) |

### 9.2 OCR and text verification (tools Read text, Check text; L3, L4)

| Paper | Link | Idea | Ours |
| --- | --- | --- | --- |
| Graves et al., "Connectionist temporal classification", ICML 2006 | [ACM](https://dl.acm.org/doi/10.1145/1143844.1143891) [K] | Loss for unsegmented sequences | Have (CTC alignment in OCV) |
| Shi, Bai, Yao, "An end-to-end trainable neural network for image-based sequence recognition" (CRNN), 2015 | [arXiv:1507.05717](https://arxiv.org/abs/1507.05717) [K] | CNN + RNN + CTC | Have similar |
| Baek et al., "What is wrong with scene text recognition model comparisons?", ICCV 2019 | [arXiv:1904.01906](https://arxiv.org/abs/1904.01906) [K] | Four-stage framework; ablations | Reference |
| Baek et al., "Character region awareness for text detection" (CRAFT), CVPR 2019 | [arXiv:1904.01941](https://arxiv.org/abs/1904.01941) [K] | Character-level detection | Reference |
| Liao et al., "Real-time scene text detection with differentiable binarization" (DBNet), AAAI 2020 | [arXiv:1911.08947](https://arxiv.org/abs/1911.08947) [K] | Fast text detection | Reference |
| Li et al., "TrOCR", AAAI 2023 | [arXiv:2109.10282](https://arxiv.org/abs/2109.10282) [K] | Transformer OCR | Reference |
| Du et al., "PP-OCR: a practical ultra lightweight OCR system", 2020 | [arXiv:2009.09941](https://arxiv.org/abs/2009.09941) [K] | Small deployable OCR | Reference |
| Jaderberg et al., "Synthetic data and artificial neural networks for natural scene text recognition", 2014 | [arXiv:1406.2227](https://arxiv.org/abs/1406.2227) [K] | Train on synthetic text (we do: synthetic CIJ / TTO) | Have |
| Gupta et al., "Synthetic data for text localisation in natural images" (SynthText), CVPR 2016 | [arXiv:1604.06646](https://arxiv.org/abs/1604.06646) [K] | Synthetic scenes | Have similar |
| Lee et al., "Dot matrix and inkjet character recognition" style papers (search "dot matrix character recognition CIJ") | [Google Scholar](https://scholar.google.com/scholar?q=continuous+inkjet+dot+matrix+character+recognition) [K] | Dot join before classification | Have (closing) |
| Smith, "An overview of the Tesseract OCR engine", ICDAR 2007 | [Google](https://research.google/pubs/an-overview-of-the-tesseract-ocr-engine/) [K] | Classic engine | Reference |

### 9.3 Anomaly and surface defects (tools Defect, Compare; L3, L4, L5)

| Paper | Link | Idea | Ours |
| --- | --- | --- | --- |
| Bergmann et al., "MVTec AD", CVPR 2019 | [paper](https://www.mvtec.com/company/research/datasets/mvtec-ad) [K] | Benchmark | Use as a test set |
| Bergmann et al., "Uninformed students" (STAD), CVPR 2020 | [arXiv:1911.02357](https://arxiv.org/abs/1911.02357) [K] | Teacher-student anomaly | Reference |
| Defard et al., "PaDiM", 2020 | [arXiv:2011.08785](https://arxiv.org/abs/2011.08785) [K] | Patch Gaussian model | Reference |
| Roth et al., "PatchCore", CVPR 2022 | [arXiv:2106.08265](https://arxiv.org/abs/2106.08265) [K] | Memory bank of patches; near 99 % AUROC on MVTec AD | Study 02 / AI-DEEP-LEARNING |
| Zavrtanik et al., "DRAEM", ICCV 2021 | [arXiv:2108.07610](https://arxiv.org/abs/2108.07610) [K] | Reconstruction plus discriminative | Reference |
| Batzner et al., "EfficientAD", WACV 2024 | [arXiv:2303.14535](https://arxiv.org/abs/2303.14535) [K] | Millisecond-level anomaly detection | Candidate for the i7 without GPU |
| Bergmann et al., "Beyond dents and scratches" (MVTec LOCO) 2022 | [arXiv:2022 IJCV](https://www.mvtec.com/company/research/datasets/mvtec-loco) [K] | Logical anomalies | Reference |
| Tsai, "A review of machine vision defect detection", or Czimmermann et al. 2020 "Visual-based defect detection and classification approaches for industrial applications" | [Sensors](https://www.mdpi.com/1424-8220/20/5/1459) [K] | Survey | Reference |

### 9.4 Geometry, locate and measure (tools Locate, Gauge, Calibrate; L1, L2, L3)

| Paper | Link | Idea | Ours |
| --- | --- | --- | --- |
| Steger, "An unbiased detector of curvilinear structures", IEEE PAMI 1998 | [IEEE](https://ieeexplore.ieee.org/document/659930) [K] | Sub-pixel lines | Method known |
| Steger, "Occlusion, clutter, and illumination invariant object recognition", ISPRS 2002 | [ISPRS](https://www.isprs.org/proceedings/XXXIV/part3/papers/paper162.pdf) [K] | Shape-based matching (the Halcon base) | Candidate |
| Ulrich and Steger, "Empirical performance evaluation of object recognition methods", 2001 | [Google Scholar](https://scholar.google.com/scholar?q=Ulrich+Steger+performance+evaluation+object+recognition) [K] | Compare matchers | Reference |
| Hinterstoisser et al., "Gradient response maps for real-time detection of textureless objects" (LINEMOD), IEEE PAMI 2012 | [IEEE](https://ieeexplore.ieee.org/document/6042881) [K] | Fast template matching | Candidate |
| Borgefors, "Hierarchical chamfer matching", IEEE PAMI 1988 | [IEEE](https://ieeexplore.ieee.org/document/3204) [K] | Distance-transform matching | Reference |
| Brown, "Close-range camera calibration", Photogrammetric Engineering 1971 | [ASPRS](https://www.asprs.org) [K] | Radial and tangential distortion model | Have |
| Joint Committee, "GUM: Guide to the expression of uncertainty in measurement", JCGM 100:2008 | [BIPM](https://www.bipm.org/en/committees/jc/jcgm/publications) [K] | Uncertainty budget | Use for the 0.1 px 3 sigma claim |
| MSA 4th edition (AIAG) gauge R&R | [AIAG](https://www.aiag.org/quality/automotive-core-tools/msa) [K] | Gauge repeatability: %GRR below 10 % good | Use for acceptance |

### 9.5 Colour (tool Colour check; L1, L3)

| Paper | Link | Idea |
| --- | --- | --- |
| Sharma, Wu, Dalal, "The CIEDE2000 color-difference formula: implementation notes", Color Res. Appl. 2005 | [Rochester](https://hajim.rochester.edu/ece/sites/gsharma/ciede2000/) [K] | Correct formula and test data |
| Luo, Cui, Rigg, "The development of the CIE 2000 colour-difference formula", Color Res. Appl. 2001 | [Wiley](https://onlinelibrary.wiley.com/doi/10.1002/col.1049) [K] | Origin |
| Finlayson, Drew, "Color constancy", and Buchsbaum "A spatial processor model for object colour perception" 1980 | [ScienceDirect](https://www.sciencedirect.com/science/article/pii/0016003280900587) [K] | Gray-world light normalisation |

---

## 10. Standards-to-tool matrix (one view)

| Tool | Layers | Standards that apply |
| --- | --- | --- |
| Read code (1D, 2D) | L2, L3 | ISO 16022, 18004, 23941, 15417, 15420, 16388, 15438, 24778, 24724, DotCode, Pharmacode, ISO 15424, GS1 General Specs |
| Code Verify | L0, L4 | ISO 15415, 15416, 29158, 15426-1/-2, GS1 tables, ISO 1831 (text) |
| GS1 check | L4 | GS1 General Specs, syntax dictionary, ISO 15418, Digital Link, ISO 15424 |
| Aggregation | L4, L6 | GS1 SSCC, GS1 Healthcare, DGFT, EU FMD (not aggregated in EU), DSCSA |
| Read text, Check text (OCR, OCV) | L3, L4 | ISO 1073, 1831, Legal Metrology, Drugs Rules |
| Colour, Defect, Compare, Locate, Gauge | L1 to L4 | CIE, ISO 12647, GUM, MSA |
| Camera and light | L0 | EMVA 1288, GenICam, GigE Vision, USB3 Vision |
| Output, PLC, audit | L6 | Modbus, IEC 61131-3, OPC 40100, IEC 62443, 21 CFR 11, Annex 11, ALCOA+, GAMP 5 |

---

## 11. Gaps and actions in one list (ranked)

| Rank | Gap | Layer | Effort | Reason |
| --- | --- | --- | --- | --- |
| 1 | QR quiet zone 2 not 4 modules | L4 | 0.2 pw | Out-of-spec codes graded clean: a bad-part risk |
| 2 | (Done: symbology identifier in the C API and the FNC1-first GS1 check, PR #101) | L3, L4 | 0 | - |
| 3 | Reference verification rig and conformance test (15426) before any "ISO grade" claim | L0, L4 | 3 pw plus hardware | Grades are process-control only today |
| 4 | Part 11 mode and validation pack (IQ / OQ / PQ, URS, FS, trace matrix) | L6 | 5 pw | Pharma customers audit this first |
| 5 | Regulatory profiles: FMD, DSCSA, CDSCO QR, Legal Metrology, DGFT export file (DGFT status to confirm) | L4, L6 | 3 pw | Customer-facing; needs exact field lists from the owner |
| 6 | GS1 per-application preset table (X mm, quiet zones, grade string) | L4, L5 | 1 pw | Set-up in one click |
| 7 | OPC UA 40100 server | L6 | 2 pw | Enterprise licence |
| 8 | EMVA-style camera characterisation | L0, L1 | 1 pw | Basis for dark / flat field and exposure |
| 9 | IEC 62443-style threat model and hardening guide | L6 | 1 pw | Security |
| 10 | DotCode grading (open PR #117), rMQR grading (stacked DataBar grading done, PR #95) | L4 | 2 pw | Only when a customer needs it |
| 11 | ISO 1831 / OCR-B font tolerances in OCV | L3, L4 | 0.5 pw | Text print quality |
| 12 | Batch label reconciliation report (211.130) | L6 | 0.5 pw | cGMP |

---

## 12. Compliance checklist for a pharma line

Tick each line before a line goes live. "Us" means the software; "Plant" means the customer.

Reading and print quality
- [ ] Every 2D code is GS1 DataMatrix (or GS1 QR where the market allows), read with the symbology identifier checked (]d2 / ]Q3). Us: Have (identifier field and FNC1-first check, PR #101).
- [ ] Print quality: ISO/IEC 15415 grade C (1.5) or better at the reference light and aperture on a certified verifier, sampled per batch. Us: process-control grade only; Plant: a certified verifier for release grade.
- [ ] Direct-marked codes (laser, dot-peen): ISO/IEC TR 29158 grade with the stated light.
- [ ] Quiet zones: DM 1 module, QR 4 modules, GS1-128 10 X, EAN-13 11 / 7 X.
- [ ] X-dimension inside the GS1 table for the application (for example DM 0.254 to 0.615 mm). Needs the mm scale calibration of L1.
- [ ] Human-readable text matches the code (GTIN, batch, expiry, serial) and is printed in the required font and size (OCR-B where required).

Data rules
- [ ] AI set per market: EU FMD (01, 17, 10, 21 and national number where needed), DSCSA (NDC-based GTIN, lot, expiry, serial), India export (GTIN, serial; aggregation to SSCC), India CDSCO QR (field list from the notification).
- [ ] Serial numbers are unique; duplicates inside the batch fail; duplicate memory (10,000 children, 1,000 cases) as in the owner's decision.
- [ ] Expiry is in the future and later than manufacture; batch and dates match the job's order data.
- [ ] Legal Metrology: MRP format "inclusive of all taxes", net quantity, manufacturer address, month and year, minimum character height.

Aggregation
- [ ] Case holds the exact child count (12, 24, 45, 48, 96); every child read once; no child in two cases; parent SSCC read and linked.
- [ ] Parent-child file is exported in the format of the regulator (DGFT / iVEDA portal, status to confirm; EU repository not needed for aggregation).

Line safety
- [ ] A bad part never passes: camera, PLC link, job or model in doubt means NG and the line is told (requirements section 12).
- [ ] The reject has a confirmed gate: reject confirmation signal, a bin-full stop, and a bad-count limit that stops the line.
- [ ] No vision result bypasses a machine safety circuit (functional safety note, section 6).

Records and validation
- [ ] Audit trail on jobs, tolerances, models and users: who, when, old and new value, reason; no deletion (21 CFR 11.10(e), Annex 11 clause 9).
- [ ] Electronic signature shows name, time and meaning; linked to the record (11.50, 11.70, Annex 11 clause 14).
- [ ] Unique user IDs, role-based access, session lock and password rules (11.300, 11.200, 62443-4-2).
- [ ] NG images and results stored, original not altered, time-synchronised (ALCOA+, NTP or PTP).
- [ ] Validation pack: URS, FS, risk assessment, IQ, OQ, PQ with a challenge set (known bad parts that must be rejected), traceability matrix, change control (GAMP 5).
- [ ] Challenge test at start of each batch and at shift change: known-bad samples must be rejected.
- [ ] Back-up and restore tested; clock check (Annex 11 clause 7).
- [ ] Software version, model version and job version recorded with every result.

Hardware
- [ ] Camera characterised (dark, flat, exposure blur < 0.3 px), light stable (strobe), lens focus locked; lighting and camera re-verified after any maintenance (L0 checklist).
- [ ] Two-camera sync (action command or PTP) tested at 225 parts per minute.
- [ ] Network: one NIC per GigE camera, jumbo frames, no shared traffic; PLC link separate.

---

## 13. Documents the owner should buy (paywalled)

Prices from the ISO / IEC stores change often and were not read today (stores blocked or not opened). The ranges are what these documents cost at the ISO store in recent years [K]; check the price on the day.

| Priority | Document | Where | Price [K, approximate] | Why |
| --- | --- | --- | --- | --- |
| 1 | ISO/IEC 15415:2011 Print quality, 2D | [ISO](https://www.iso.org/standard/54717.html) | CHF 150 to 200 | Exact thresholds and the grade string for the 2D grade |
| 1 | ISO/IEC 15416:2016 Print quality, linear | [ISO](https://www.iso.org/standard/54716.html) | CHF 150 to 200 | Same for 1D |
| 1 | ISO/IEC TR 29158:2020 DPM quality | [ISO](https://www.iso.org/standard/76310.html) | CHF 150 to 200 | Replaces the vendor-documentation approximation in grade_dpm.cpp |
| 1 | ISO/IEC 15426-2 and 15426-1 verifier conformance | [ISO](https://www.iso.org/standard/54715.html) | CHF 100 to 150 each | Needed before any "verifier" claim |
| 1 | ISO/IEC 18004:2024 QR Code | [ISO](https://www.iso.org/standard/83389.html) | CHF 150 to 200 | Quiet zone, EC tables, finder rules |
| 1 | ISO/IEC 16022:2024 Data Matrix | [ISO](https://www.iso.org/standard/44230.html) | CHF 150 to 200 | Fixed pattern damage segments, 2024 changes |
| 2 | ISO/IEC 15424 symbology identifiers | [ISO](https://www.iso.org/standard/43892.html) | CHF 60 to 100 | Identifier field |
| 2 | ISO/IEC 23941 rMQR, 24778 Aztec, 24724 DataBar, 15438 PDF417 | ISO store | CHF 100 to 150 each | Only for the symbologies a customer uses |
| 2 | AIM ISS DotCode 4.0 | [AIM](https://www.aimglobal.org) | USD 0 to 100 (free to members) | DotCode grading |
| 2 | GS1 General Specifications (print copy; the PDF is free) | [GS1](https://www.gs1.org/standards/barcodes-epcrfid-id-keys/gs1-general-specifications) | Free PDF | The tables of section 3 |
| 2 | ISPE GAMP 5, second edition | [ISPE](https://ispe.org/publications/guidance-documents/gamp-5-guide-2nd-edition) | USD 300 to 400 (member discount) | Validation pack |
| 2 | EMVA 1288 release 4.0 | [EMVA](https://www.emva.org/standards-technology/emva-1288/) | Free download | Camera characterisation |
| 3 | IEC 62443-4-1 and 4-2 | [IEC](https://webstore.iec.ch) | CHF 200 to 300 each | Security |
| 3 | ISO 1831 and ISO 1073-2 | [ISO](https://www.iso.org/standard/6185.html) | CHF 60 to 100 each | OCR-B print quality |
| 3 | ISO 12647-2 and ISO 13655 | ISO store | CHF 100 to 150 each | Colour customers |
| 3 | OPC 40100-1 | [OPC](https://reference.opcfoundation.org/MachineVision/v100/docs/) | Free | Already free |
| 3 | GS1 conformance test card (Verified) and a calibrated reflectance card | [GS1 / Applied Image] | Roughly USD 200 to 600 | Verifier conformance test (1.2) |
| 3 | Laetus Pharmacode vendor sheet | [Laetus](https://www.laetus.com) | Free on request | Width tolerances |

Free and worth saving as local reading (do not copy into the repo): EU Delegated Regulation 2016/161, the FDA DSCSA pages, CDSCO notification GSR 823(E) and its FAQ, the DGFT notices, GS1 India implementation manuals, Legal Metrology rules, 21 CFR Part 11, EU GMP Annex 11, FDA and MHRA data-integrity guidance.

---

## Not opened / limits

- Opened and used today: [EMVA 1288 page](https://www.emva.org/standards-technology/emva-1288/), [FDA DSCSA page](https://www.fda.gov/drugs/drug-supply-chain-integrity/drug-supply-chain-security-act-dscsa) (does not list identifier fields), [rfxcel India summary](https://rfxcel.com/?p=6571), [OPC UA Machine Vision reference](https://reference.opcfoundation.org/MachineVision/v100/docs/), Wikipedia pages for [rMQR](https://en.wikipedia.org/wiki/Rectangular_Micro_QR_Code), [DotCode](https://en.wikipedia.org/wiki/DotCode), [Pharmacode](https://en.wikipedia.org/wiki/Pharmacode) and [Data Matrix](https://en.wikipedia.org/wiki/Data_Matrix) (Wikipedia is a reliable summary only, not the standard), and the repo's engine files and change notes.
- Search summaries only [S]: DGFT export barcoding, iVEDA, CDSCO GSR 823(E), Schedule H2 widening of June 2026. The secondary sources disagree on the number of QR data points (8, 9 or 11), the API requirement and the dates: read the official notifications before any customer claim.
- Opened but not readable: EUR-Lex 2016/161 (empty page), CDSCO FAQ PDF and EU Annex 11 PDF (compressed text), eCFR Part 11 (redirected), GS1 India manual (404), DGFT notification list (nothing found).
- Blocked (HTTP 403 or 429 or TLS failure): GS1 General Specifications and Digital Link pages, Cognex white papers, AIA GigE Vision / USB3 Vision pages, Edmund Optics, GHSI, EMVO, ISO store pages were not fetched.
- All numbers marked [K] (grade limits, apertures, X-dimensions, quiet zones, fees) are from memory of the standards and must be checked against purchased texts. Prices in section 13 are rough ranges, not read from a store.
- Academic paper links in section 9 are from memory: titles, authors and venues are the facts; the arXiv numbers listed are the ones I am most sure of, the IEEE and publisher links may need a title search.
- Not covered: country rules for Brazil, Turkey, Saudi Arabia, China, Russia beyond a one-line mention; excise labels by state; ISO/IEC 15434 transfer syntax details; Laetus two-track pharmacode; Code 93, Codabar, MSI, Plessey (not in the owner's jobs); paper-level verification of any "almost 100 %" claim.
- No engine code was built or run; status words come from reading files and change notes.
