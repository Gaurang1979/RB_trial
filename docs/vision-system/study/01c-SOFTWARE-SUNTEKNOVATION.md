# 01c. Sun Teknovation: pharma vision inspection and track and trace

Study date: 2026-10-30. Owner request: "study https://www.sunteknovation.com website, check pdf files,
options of vision systems, tools and how it works. also see youtube channel of the same and see how its
tools are working." Part of the [study index](00-INDEX.md).

Sun Teknovation Pvt Ltd (Gandhinagar, Gujarat) is a maker of pharma machine-vision inspection machines
and serialisation (track and trace) machines. It may be a competitor or a partner of the owner; this
chapter stays factual. **Everything below is vendor marketing.** No page gives a test method, a data set
or a measured false-reject rate. Where a number is given, it is a claim. Marks: [S] = seen only in a
search snippet or an unopened page. Nothing in this chapter is marked [S] unless said.

## 1. Sources opened

| Source | How read | Result |
| --- | --- | --- |
| [Home page](https://www.sunteknovation.com/) | WebFetch | Menu, product list, clients, regulatory list, claims |
| [Sitemap](https://www.sunteknovation.com/sitemap.xml) | WebFetch | 478 URLs: 15 products pages, ~40 blog posts, per-country T&T pages, 6 language copies (ru, ar, fr, de, it, sp) |
| [robots.txt](https://www.sunteknovation.com/robots.txt) | curl | Only form handlers and `/lic/auth/login.php` blocked |
| [machine-vision-system](https://www.sunteknovation.com/machine-vision-system) | WebFetch | Index of seven machines |
| [eDrashti](https://www.sunteknovation.com/blister-inspection-system-bis-edrashti), [eArjun](https://www.sunteknovation.com/pinhole-detector-earjun), [eSanket](https://www.sunteknovation.com/esanket-barcode-pharmacode-ocr-reader), [eSaksham](https://www.sunteknovation.com/esaksham-sealing-inspection-system), [eTabspec](https://www.sunteknovation.com/tablet-inspection-system-tis), [eMatra](https://www.sunteknovation.com/ematra-online-checkweigher), [eSampoorna](https://www.sunteknovation.com/esampoorna-standalone-360-deg-inspection-system) | WebFetch each | Product pages (thin; the PDF is richer) |
| [track-and-trace](https://www.sunteknovation.com/track-and-trace), [online](https://www.sunteknovation.com/online-track-and-trace), [offline](https://www.sunteknovation.com/offline-track-and-trace), [at-machine](https://www.sunteknovation.com/at-machine), [aggregation](https://www.sunteknovation.com/aggregation), [manual-aggregation](https://www.sunteknovation.com/manual-aggregation), [multi-2d-aggregation](https://www.sunteknovation.com/multi-2d-aggregation) | WebFetch each | T&T pages |
| [about-us](https://www.sunteknovation.com/about-us), [erp/](https://www.sunteknovation.com/erp/) | WebFetch | About text; `/erp/` is only a login page (Sun Teknovation's own ERP, nothing visible) |
| Blogs: [pinhole](https://www.sunteknovation.com/what-is-a-pinhole-detector-how-micro-holes-in-blister-foil-are-caught-before-filling), [how T&T works](https://www.sunteknovation.com/how-track-and-trace-systems-work-in-pharmaceutical-manufacturing), [L3 vs L4](https://www.sunteknovation.com/l3-vs-l4-serialization-a-pharma-manufacturers-guide-to-choosing-the-right-track-and-trace-vendor), [AI tablet](https://www.sunteknovation.com/ai-based-tablet-inspection-system-achieving-100-defect-detection-in-pharma-manufacturing), [code reader vs OCR](https://www.sunteknovation.com/when-a-pharma-code-reader-is-enough-and-when-you-need-ocr), [DAVA / Schedule H2](https://www.sunteknovation.com/india-dava-schedule-h2-compliance-the-serialization-framework-indian-pharma-manufacturers-cant-ignore-after-2026), [validation guide](https://www.sunteknovation.com/validating-visual-inspection-systems-in-pharma-a-stepwise-guide-to-quality-and-compliance) | WebFetch each | Method detail (see sections 3 and 4) |
| [Brochure v6 PDF](https://www.sunteknovation.com/assets/pdf/sun-teknovation-brochure-v6.pdf) | curl (17 MB, 12 pages); WebFetch refused (over 10 MB) | **Image-only PDF, no text layer.** Rendered each page to PNG and ran `tesseract` OCR; looked at page 5 (eTabSpec) as an image. Brochure facts below are OCR and can contain small errors |
| YouTube [channel videos page](https://www.youtube.com/@SunTeknovation/videos) | WebFetch (page body empty), curl | Channel id `UC2gg4BzXJDA03jquDe7_yWw`; RSS feed worked |
| RSS feed `https://www.youtube.com/feeds/videos.xml?channel_id=UC2gg4BzXJDA03jquDe7_yWw` | curl | 15 videos (the feed's limit) |
| 15 video pages | curl, parsed `lengthSeconds` and description | Length and full description for 14; one page had no data. No transcripts fetched (see section 7) |

Other PDFs: the brochure is the only PDF found (the home page links only it; the sitemap lists none).
`/assets/pdf/` was not listable.

## 2. The company in numbers (claims)

| Claim | Where | Note |
| --- | --- | --- |
| 1,350+ installations (site), 1,250+ (brochure, blogs) | home page, PDF p.1 | Figures differ between sources |
| 200+ clients; 22+ inline products (PDF), "12+" (site) | PDF p.1, home page | Differ |
| 12+ / 13+ / 14+ years; first order 2015 | PDF, About, [YouTube aMtlBO5ni60](https://www.youtube.com/watch?v=aMtlBO5ni60) | Differ |
| Countries: 9+ (PDF), 13+ and "60 countries" (site) | PDF p.1, home page | Inconsistent |
| Works: Gandhinagar (E-53 / E183 Electronic GIDC, Sector 26) and Sanand GIDC (Ahmedabad) | PDF p.1 | |
| Clients on the logo wall: Cipla, Glenmark, Intas, Wockhardt, Haleon, Emami, Ipca, JB Chemical, Renata, RPG Life Sciences, MSN, Medreich and others | home page, PDF p.12 | Logos only; no case study |
| Directors: Ajay Roshania, Hiren Shah | home page | |
| Language copies: ru, ar, fr, de, it, es | sitemap | |
| Key advantage "one-button teaching" | PDF p.2, About | Industry-first claim, no detail |
| Edge computing: processing at the machine, not the cloud | [YouTube qiQDSm1Q2dI](https://www.youtube.com/watch?v=qiQDSm1Q2dI) | Statement of principle |
| Names: eDrashti (sight), eArjun, eSampoorna (complete), eSanket (sign) | [YouTube VINbe4W1CPA](https://www.youtube.com/watch?v=VINbe4W1CPA) | Brand only |

## 3. Machine vision products

Layer references: L0 Acquire ... L6 Output and audit (see [00-INDEX](00-INDEX.md)). "Not stated" means the
vendor pages and brochure do not say.

### 3.1 eDrashti: blister inspection system (BIS)

Pages: [product](https://www.sunteknovation.com/blister-inspection-system-bis-edrashti), brochure p.4,
blog [validating visual inspection](https://www.sunteknovation.com/validating-visual-inspection-systems-in-pharma-a-stepwise-guide-to-quality-and-compliance).

| Item | Detail |
| --- | --- |
| Inspects | Tablets and capsules in blisters (also vials); secondary items (cartons, bottles, shippers) per the page; checks run on every blister |
| Foil and film types (PDF) | Aluminium foil, white PVC, colour PVC, opaque PVC, Aclar film |
| Models (PDF, forward x depth mm) | Four sizes: forward 160 / 200 / 250 / 300, depth 240 / 300 / 350 / 400 (blister format size classes; exact pairing not clear from OCR) |
| Options and tools | AI-based auto-teaching; auto tolerance setting; "no need to teach a defective product"; flexible user management; rejection analysis report; retrofit possible; large database to reduce rejection; multi-zone defect detection; "intelligent classification" to lower false rejects |
| Hardware | "High-resolution vision cameras"; lights not described; "world's first on-the-go hardware configuration" (settings adjustable while the machine runs, claim) |
| Software | Self-learning AI engine; "proprietary algorithms"; deep learning and machine learning named, no detail; automatic reports; "21 CFR Part 11 Ready" |
| Speed and accuracy | "High speed", "no production stoppage". No numbers. |
| How it works (from the pages) | Camera takes the blister image; the AI engine compares it to a taught good blister (taught from good product only, tolerances set automatically); each zone is checked in milliseconds; a defect flags the blister for rejection; rejects and images are logged; a report is built |
| Validation advice (blog) | URS, risk assessment, IQ, OQ, PQ, software and traceability validation, change control; IQ covers camera, light and rejector alignment, PLC and conveyor link; OQ measures false-reject rate at full speed; PQ runs full shifts on real batches. Part 11 items: audit trail, password rules with access levels, backup, record retention for images and reject logs. No acceptance numbers given |

### 3.2 eArjun: pinhole detector (PHD)

Pages: [product](https://www.sunteknovation.com/pinhole-detector-earjun), brochure p.3,
[blog](https://www.sunteknovation.com/what-is-a-pinhole-detector-how-micro-holes-in-blister-foil-are-caught-before-filling).

| Item | Detail |
| --- | --- |
| Inspects | Aluminium foil (blister base foil and lid foil, strip machine foil, HFFS and VFFS machine foil) for pinholes, pores, fissures, fractures and cracks |
| Smallest defect | Pinhole diameter "as low as 10 um" (claim) |
| Hardware (PDF table, OCR) | Six sizes, field of view 200 / 240 / 260 / 300 / 320 / 380 mm; system length 332 to 490 mm, width 43 mm, height 323 mm; inbuilt LED pass/fail lamp; plug and play; GMP build |
| Method | **Not stated.** The page says "advanced detection technology"; the product is a thin bar (43 mm wide) that straddles the foil web, which points to a light-through-foil line sensor, but this is our inference, not the vendor's statement |
| Software | Can be integrated with software; "first unit with audit and production report facility" (claim) |
| Speed | Not stated |
| How it works | Foil enters the field of view; the unit monitors it; a hole is flagged; pass/fail goes to the LED and the machine; the production report is stored |

### 3.3 eSanket: barcode, pharmacode and OCR reader (code inspection)

Pages: [product](https://www.sunteknovation.com/esanket-barcode-pharmacode-ocr-reader), brochure p.7,
[blog code reader vs OCR](https://www.sunteknovation.com/when-a-pharma-code-reader-is-enough-and-when-you-need-ocr).

| Item | Detail |
| --- | --- |
| Reads | 1D barcode, 2D (Data Matrix, QR), pharmacode, presence/absence, logos and artwork, batch details in several languages |
| Text | OCR (read) and OCV (verify); overprint check; "foil code reading (FCR)" in primary packaging |
| Hardware | Cameras VGA to 5 MP; several cameras for full coverage; "adaptable illumination units" by pack type |
| Speed | Up to 600 inspections per minute, with multiple codes per pass (claim; the page says rivals are "under 100 per minute") |
| Mounting | Labelling machine, conveyor, cartoner, lead foil, stick-pack machine |
| Software | Flexible user management; automatic report; no grading standard (ISO 15415 / 15416) mentioned |
| Blog rules | Pharma code (pharmacode) proves the right product / label; OCR proves the batch and expiry text; OCV proves print quality; a 2D read does not prove separately printed text; one line may need all four in one unit |

### 3.4 eSaksham: sealing inspection system

Pages: [product](https://www.sunteknovation.com/esaksham-sealing-inspection-system), brochure p.6.

| Item | Detail |
| --- | --- |
| Inspects | Induction seals on bottles: loose, damaged, overheated, missing |
| Method | **High-resolution thermal camera** (the seal is heated by the induction sealer; the camera looks at the heat picture); non-destructive |
| Rejection | Integrated pneumatic rejector and diverter that routes bottles off the line; "auto rejection" |
| Other | Bottle of any height; compact user interface; GMP machine; several seals checked at once |
| Not stated | Line speed, camera resolution, accuracy |

### 3.5 eTabspec (eTabSpec 24T): tablet inspection system (TIS)

Pages: [product](https://www.sunteknovation.com/tablet-inspection-system-tis), brochure p.5,
[YouTube Eu6yGPI_Nhk](https://www.youtube.com/watch?v=Eu6yGPI_Nhk),
[blog AI tablet inspection](https://www.sunteknovation.com/ai-based-tablet-inspection-system-achieving-100-defect-detection-in-pharma-manufacturing).

| Item | Detail |
| --- | --- |
| Inspects | Tablets, capsules and caplets falling from a tablet counter / bottle line: colour, size, shape, shade variation, foreign matter, physical damage (chipped, broken, double, powder), capsule print and joins (PDF shows capsules with printed "150 PGBN" text and defect crops) |
| Layout (PDF) | Modular design; two to four lanes of tracks; each lane has its own camera view on a 24-inch-class touch screen (model "24T"); the screen shows four camera strips with accept / reject / total counters, foreign-image gallery and diameter / thickness read-outs |
| Speed | **32,000 tablets per minute for an 8 mm round tablet** (PDF) |
| Options | Auto dimension measurement on product; special-type rejection; foreign-matter report with image; de-duster for powder product; database to improve the process; easy reading process; less manual measurement error |
| AI | Deep learning; "learns new tablet and capsule types on its own"; works on low-contrast product-and-foil combinations (claim) |
| Software | Touch HMI; "21 CFR Part 11 Ready"; auto log of each reject with batch, defect type and image |
| Accuracy | "100% inspection" (every tablet looked at). No detection or false-reject number. Blog says "thousands" and elsewhere "tens of thousands" of tablets per minute (inconsistent) |
| How it works | Tablets flow on tracks past cameras under the counter; each is imaged from several angles; AI compares it to the taught spec in milliseconds; rejects are blown out by the special-type rejector; counts and images are logged by batch |

### 3.6 eMatra: online checkweigher

Page: [product](https://www.sunteknovation.com/ematra-online-checkweigher). Not vision; sold in the same family.

| Model | Range | Scale step | Accuracy | Speed | Belt (mm) | Display | Rejector | IP |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| CW-100G | 5 to 100 g | 0.05 g | +-0.2 g | 160 per min | 250 x 100 | 10 inch | swing, air blow, push or drop-up | IP54 |
| CW-600G | 5 to 600 g | 0.1 g | +-0.5 g | 140 per min | 310 x 250 | 10 inch | same | IP54 |
| CW-100g Pro | 1 to 500 g | 0.05 g | +-0.1 g | belt 10 to 70 m/min | 200 to 150 (unit unclear) | 15 inch | air blow | IP65 |

Common: SS304 frame, platform height 750 +-50 mm, three-colour tower light, optional buzzer, 220 V
single phase. Load cell type and software are not stated.

### 3.7 eSampoorna: standalone 360 degree inspection system

Pages: [product](https://www.sunteknovation.com/esampoorna-standalone-360-deg-inspection-system), brochure
pp.10 to 11, [YouTube y66H6JcR1r8](https://www.youtube.com/watch?v=y66H6JcR1r8).

| Item | Detail |
| --- | --- |
| Inspects | Round tablets (about 5 to 18 mm), oval / oblong tablets (to about 25 mm), capsules and softgels; other shapes on request. Detects "a much wider list" of defects than the typical-defect picture shows |
| Method (PDF p.11) | **Six inspection units, one per side. Colour, backlight and 3D images are taken at the same time for each side: 18 images per product.** Product is filled into pockets of an upper cylinder and held by vacuum in a fixed position; the first unit images the top; the lower cylinder carries it past five more units; images are analysed; each product is classed good or defective; a sorting unit puts it in the good or defective bin and **checks that the sort worked** |
| Speed (indicative, per hour) | Round tablets: 13-18 mm 80,000; 10-12 mm 160,000; 8-9 mm 260,000; 7-5 mm 320,000; 5 mm 400,000. Oval / oblong: over 18 mm 60,000; 14-18 mm 80,000; 11-13 mm 160,000; 8-10 mm 180,000; under 8 mm 200,000 |
| Setup | "No defects needed for training"; computer-assisted setting; intuitive sensitivity; feedback on detection and yield; 10 minute setup; 10 minute cleaning and changeover; tool-free; no optical adjustment |
| New product flow | New product information, mechanical tooling selection, computer-assisted setting, new inspection solution; standard tooling for round / oblong / oval tablets and capsules and softgels; dedicated plug-and-play tooling for special shapes |
| Software | Graphical user interface; "powered by AI for defect identification"; no part 11 line on the page |
| Notes | Colour + backlight + 3D per side is the most complete imaging recipe on the site |

### 3.8 Print and inspection machine (video only)

[YouTube TJDQhXZVCOk](https://www.youtube.com/watch?v=TJDQhXZVCOk) (66 s, 2025-12-23). Not on the website
product list. Inspects batch and expiry presence, legibility, missing / smudged / blurred / faded print,
character accuracy, barcode presence, position and readability, print alignment. 100% inline, automatic
rejection, image-based logging. Fits blister lines, carton / label printing, bottle labelling, serialisation
and aggregation. Also see "eTrack Print and Inspection" in the brochure (section 4.2).

### 3.9 Other items mentioned

| Item | Where | Note |
| --- | --- | --- |
| Capsule and vial inspection | home page FAQ | No product page |
| Fill-level and closure checks | [YouTube y66H6JcR1r8](https://www.youtube.com/watch?v=y66H6JcR1r8) | Named as 360-degree detections; no product page |
| Retrofit upgrades for legacy machines | [YouTube aMtlBO5ni60](https://www.youtube.com/watch?v=aMtlBO5ni60), home page | Embedded design services |

## 4. Track and trace products

### 4.1 Software names

| Name | What the site says |
| --- | --- |
| eTrack | Level 3 serialisation and aggregation software (home page); brochure p.8 and p.9 show an "eTrack" badge on the online, offline, print and inspection, and manual / multi-2D aggregation machines |
| eTrack L3 | Plant-level (L3) solution |
| L4 | Blog [L3 vs L4](https://www.sunteknovation.com/l3-vs-l4-serialization-a-pharma-manufacturers-guide-to-choosing-the-right-track-and-trace-vendor): L3 is the "control tower" for one site (assigns and reconciles serials, tracks batch status, receives aggregation from the lines, keeps an audit trail of produced / rejected / reworked); L4 sits above sites, is the single contact for external parties and consolidates plants and contract packers, with each country's format and end point. Sun says it designs from L3 up to L4 |
| Own portal | Not described; an ERP login page exists (`/erp/`) with no content |
| Part 11 | "21 CFR Part 11 compliant, GMP-ready"; "complete validation documentation" (online machine page) |

No page lists the L4 / L5 integrations (SAP, Oracle, TraceLink, Optel, Arvato). Not stated.

### 4.2 Machines

| Machine | Level | What it does | Hardware and speed |
| --- | --- | --- | --- |
| **Online track and trace** ([page](https://www.sunteknovation.com/online-track-and-trace)) | Line, carton | Prints a unique 2D / Data Matrix code on each carton, prints on 2 sides, verifies the code (OCR / OCV of the human-readable text for the encoded code), air-rejects bad cartons into a rejection bin, reports online to government systems in real time | Inbuilt server, PLC control, vacuum conveyor, integrated printer, several sensors, **up to 220 cartons per minute**, self-adjusts to carton size, compact; 21 CFR Part 11 and validation documents |
| **Offline track and trace** ([page](https://www.sunteknovation.com/offline-track-and-trace)) | Line, batch-wise | Batch is serialised without a live link to the line, data uploaded later; same family and cameras (page lists high-resolution cameras for blister image, OCR/OCV with Data Matrix inspection, image acquisition then defect detection) | Inbuilt server, vacuum conveyor, PLC control. Speed 220 cartons/min is stated on the page |
| **eTrack Print and Inspection** (PDF p.8) | Line | Print plus inspect for batch / expiry / code on the carton | Same badge set |
| **AT machine** ([page](https://www.sunteknovation.com/at-machine)) | Carton | Applies anti-tamper security labels to cartons, both sides; **camera checks label alignment**; stand-alone or linked to the online machine and the packing conveyor; one software when used with the online machine; PDF p.7 shows "double size" labelling | No speed stated |
| **Manual aggregation** ([page](https://www.sunteknovation.com/manual-aggregation)) | Bottle, carton, bundle, shipper | Operator scans every unit with a handheld scanner into the carton, attaches a unique ID to the outer carton, scans it, the system links parent to child, optional list check, saved in the central database | Entry level for slow lines; floor-mounted, scanner-based |
| **Multi-2D aggregation** ([page](https://www.sunteknovation.com/multi-2d-aggregation)) | Mono carton, inner carton, shipper, pallet | A camera reads the 2D codes of a stack at once; no mechanical movement; "first in class auto-tilted table"; "hand tracing for rejection removal" (the operator removes the rejects, PDF); auto-focus camera; on-the-go hardware configuration | Camera count and speed not stated |
| **Aggregation overview** ([page](https://www.sunteknovation.com/aggregation)) | Unit, carton, case, pallet | Five steps: serialise unit; link units to carton; cartons to case; cases to pallet; store every level in the database | Not stated |

### 4.3 Online versus offline (from the site)

| | Online | Offline |
| --- | --- | --- |
| Data link | Real time to regulator and central systems | Local store, manual sync later |
| Fits | High-speed cartoners | Existing offline packing lines, batch serialisation |
| Source | [blog how T&T works](https://www.sunteknovation.com/how-track-and-trace-systems-work-in-pharmaceutical-manufacturing), [offline blog](https://www.sunteknovation.com/offline-track-and-trace-for-batch-serialization-in-offline-packaging-lines) (title only) | same |

### 4.4 How the track and trace chain works (blog, eight steps)

1. Generate a unique serial per unit. 2. Print it on the pack. 3. Camera checks the print is correct and
readable. 4. Record each serial with product data. 5. Aggregate units into cartons and pallets. 6. Upload
(online) to central and regulator platforms. 7. Record movements in the supply chain. 8. Scan at the end
point to confirm authenticity. A failure at one step (unreadable code, missing data) breaks the chain.

### 4.5 Government portals and markets

About 21 national systems are listed (home page, per-country pages in the sitemap, e.g.
[India](https://www.sunteknovation.com/online-track-and-trace-for-india), Russia (Chestny ZNAK / CRPT),
Kazakhstan (IS MPT), Indonesia (BPOM), USA (DSCSA), EU (FMD, EU Hub), China (NMPA, AliHealth eCode),
Uzbekistan (ASL Belgisi), Turkey (ITS), Korea, Armenia, Azerbaijan, Brazil (ANVISA SNCM), Saudi Arabia
(RSD), UAE (Tatmeen), Ukraine, Rwanda, Egypt, Ethiopia, Malaysia, Vietnam, Thailand; blogs also cover Kenya,
Nigeria, Morocco, Sri Lanka, Zambia, Taiwan).

India, per the [DAVA / Schedule H2 blog](https://www.sunteknovation.com/india-dava-schedule-h2-compliance-the-serialization-framework-indian-pharma-manufacturers-cant-ignore-after-2026):
GS1 Data Matrix at primary, secondary and tertiary level carrying GTIN, serial, batch, expiry and
manufacturing date; pre-production batch notice to the DAVA portal; serialisation and aggregation data at
packing time; Sun says it sends this in the required **XML**. DGFT runs DAVA (export, 2012); CDSCO runs
Schedule H2 (top 300 domestic brands). The online-machine page says the DGFT export track and trace was
withdrawn and authentication applies to 300 brands. The page does not say how credentials or retries work.
iVEDA: not mentioned on any page opened.

## 5. YouTube channel: "Sun Teknovation Pvt Ltd"

Channel [@SunTeknovation](https://www.youtube.com/@SunTeknovation/videos), id `UC2gg4BzXJDA03jquDe7_yWw`.
Listing from the RSS feed (15 entries, which is the feed limit; older videos may exist). Length from the
video page. Views are from the feed on 2026-10-30.

| Date | Title | Length | Views | Link |
| --- | --- | --- | --- | --- |
| 2026-03-11 | Nepal Pharma Expo 2026 | 8 s | 377 | [4RUlh_yGSZA](https://www.youtube.com/watch?v=4RUlh_yGSZA) |
| 2026-02-25 | Compliance and Ethics in Pharma | 81 s | 12,712 | [FiGCULrrTGU](https://www.youtube.com/watch?v=FiGCULrrTGU) |
| 2026-02-06 | The Rise of 360 degree Vision Inspection in Pharma Manufacturing | 134 s | 112 | [y66H6JcR1r8](https://www.youtube.com/watch?v=y66H6JcR1r8) |
| 2025-12-23 | Print and Inspection Machine, Pharma Vision Inspection System | 66 s | 148 | [TJDQhXZVCOk](https://www.youtube.com/watch?v=TJDQhXZVCOk) |
| 2025-12-15 | Tablet Inspection System, eTabspec, 100% Inline Tablet Quality Control | 71 s | 8,471 | [Eu6yGPI_Nhk](https://www.youtube.com/watch?v=Eu6yGPI_Nhk) |
| 2025-12-03 | PMEC 2025 (final day wrap-up) | 125 s | 50 | [zzR1tSersbg](https://www.youtube.com/watch?v=zzR1tSersbg) |
| 2025-12-03 | PMEC 2025, Day 2 | 57 s | 19 | [9Egve7Zhc08](https://www.youtube.com/watch?v=9Egve7Zhc08) |
| 2025-12-03 | PMEC 2025, Day 1, official highlights | 69 s | 58 | [oO-6xHkNQ7I](https://www.youtube.com/watch?v=oO-6xHkNQ7I) |
| 2025-12-02 | AI Vision Inspection and Track and Trace Systems, Made in India for the World | 341 s | 153 | [aMtlBO5ni60](https://www.youtube.com/watch?v=aMtlBO5ni60) |
| 2025-10-17 | Sun Teknovation at PMEC India 2025, Booth C19, Greater Noida | 29 s | 635 | [zjzC2rBnHfc](https://www.youtube.com/watch?v=zjzC2rBnHfc) |
| 2025-09-25 | Pharma Pro Pack 2025, Hyderabad (Booth A11, Hall 5, HITEX) | 53 s | 55 | [-dOAz2gLCk8](https://www.youtube.com/watch?v=-dOAz2gLCk8) |
| 2025-09-25 | Pharma Pro Pack 2025, Hyderabad (second upload) | not read | 50 | [Q8iZ78uzDBI](https://www.youtube.com/watch?v=Q8iZ78uzDBI) |
| 2025-07-30 | AI in Pharma: Smarter Quality Control | 61 s | 313 | [U-s5oZWXmQI](https://www.youtube.com/watch?v=U-s5oZWXmQI) |
| 2025-07-25 | Why Sun Teknovation Names Its Products with an Indian Touch | 126 s | 60 | [VINbe4W1CPA](https://www.youtube.com/watch?v=VINbe4W1CPA) |
| 2025-07-14 | How Edge Computing Is Revolutionizing Pharma Inspection | 52 s | 162 | [qiQDSm1Q2dI](https://www.youtube.com/watch?v=qiQDSm1Q2dI) |

### 5.1 What the videos tell us about how the tools work

I could read titles, lengths and descriptions only. **I could not watch frames.** Several pages reported
auto-caption tracks, but I did not download or read a transcript. So nothing below describes the screen
itself; it is what the descriptions say.

| Video | What it says about the tool |
| --- | --- |
| eTabspec (71 s) | Inline AI-powered 100% inspection on tablet counters and bottle lines; "high-resolution cameras, deep learning, automatic defect recognition"; defect classes shape, size, colour, surface; reject at line speed; defect log with images for audits; fits tablet counter stations, bottle filling, high-mix low-volume CMO work. The brochure picture of the 24T screen (section 3.5) is the best view of its UI |
| Print and inspection machine (66 s) | Print checks listed in section 3.8; consistent across shifts and operators; image-based logging |
| 360 degree vision (134 s) | "AI-powered, multi-camera"; detects tablet micro-cracks, wrong / missing label characters, seal and closure faults, fill-level changes; classifies defects, finds patterns, turns data into insight; ROI "in months" (claim) |
| AI in Pharma (61 s) | Instant defect detection and root-cause analysis; self-learning; fewer operator actions |
| Edge computing (52 s) | Processing on the machine, lower latency, no cloud dependence |
| Overview (341 s) | Lists AI blister, tablet, seal and pinhole inspection, full-line serialisation and aggregation, digital traceability and data integrity, retrofit; first partnership 2015 |
| PMEC Day 1 and 2 | Live demos of eTabspec, eDrashti, eSaksham; real-time defect detection and workflow simulation; machine walkthroughs |
| Names (126 s) | Naming story only |
| Compliance and Ethics (81 s) | Not product content (12,712 views, the channel's most viewed) |
| Expo videos | Event notices (PMEC India 2025 booth C19; Pharma Pro Pack 2025; Nepal Pharma Expo 2026) |

## 6. How Sun Teknovation's tools work (synthesis)

1. **One teach flow, good samples only.** eDrashti, eSampoorna and eTabspec all say "no defective sample
   needed to teach": the system learns the normal appearance and sets tolerances itself (L5). This is
   anomaly detection plus auto tolerance, one button.
2. **Several fixed views per product, light chosen per surface.** eSampoorna: 6 sides x (colour, backlight,
   3D) = 18 images per product, product held in a vacuum pocket so each view is repeatable (L0, L1).
3. **Classify, then eject, then verify the ejection.** eSampoorna checks that the sort happened.
   Vision-only pages do not say whether the rejector is checked (L4, L6).
4. **Reject bin plus count reconciliation.** Online T&T has an air-reject bin; L3 reconciles produced /
   rejected / reworked serials (L4, L6).
5. **Edge only.** All decisions are on the machine (inbuilt server and PLC); the plant layer (eTrack L3 and
   L4) collects.
6. **Part 11 and validation as a sales point.** "Part 11 Ready", audit trail, user levels, URS-IQ-OQ-PQ
   packages.
7. **Modular family on one UI and database.** Same badge and "single software" across print, inspect,
   aggregate and label machines.
8. **Process knowledge in blogs.** The blog set (40+ posts) targets search engines and regulation
   explainers; it is a content channel more than technical documentation.

## 7. Comparison: Sun Teknovation versus Vision_System today

Status for our engine: Have / Partial / Missing. Sources: [INDUSTRY-METHODS](../INDUSTRY-METHODS.md),
[TOOL-SPECIFICATIONS](../TOOL-SPECIFICATIONS.md), [PLANT-PLATFORM](../PLANT-PLATFORM.md). Our numbers are
from the measured results in the study brief; nothing here is re-measured.

| Area | Sun Teknovation (claim) | Vision_System today | Status | Our tool id |
| --- | --- | --- | --- | --- |
| 2D code read (QR / DM) | Up to 600 inspections per min, multi-code | 92.2% of 1,723 / 1,868 real codes, 0 wrong reads inferred from counts; times met on the Mac for 24 QR, 45 QR and 24 DM, 48 QR at the limit (501 ms max vs 500); 48 DM over (761 ms); 12 and 96 not measured; line PC unknown | Have (measured, stricter) | `code.read` |
| Case aggregation reading, 12 to 96 codes | Multi-2D camera, no numbers | Have; 24 QR 96.9%, 45 QR 100%, 48 DM 64.2% | Have, accuracy gap on DM | `code.read` case mode (5.5) |
| 1D labels (SSCC, GS1-128, EAN, ITF-14, DataBar) | 1D codes | Have | Have | `code.read` linear |
| Pharmacode | Reads it | Own decoder, both directions, 2-16 bars, 3-131070 (`engine/src/pharmacode.cpp`); generated images only, no real carton measured | Have (not measured on real cartons) | `code.read` linear |
| OCR / OCV | Reads and verifies, languages | OCR laser 92.6%, TTO 98.0%, CIJ 72.9%, embossed 89.4% synthetic; OCV 0 wrong accepted, true text 69% pass | Partial (accuracy gap) | `ocr.ai`, `ocr.classic`, `ocr.ocv` |
| Code grading | Not mentioned | ISO 15415 / 15416 / TR 29158 and GS1 checks built (`quality.cpp`, `grade1d.cpp`, `grade_dpm.cpp`, `Gs1Check.cs`), camera-relative process-control grades | Have; ahead of Sun | `verify.iso15415`, `verify.iso15416`, `verify.iso29158`, `verify.gs1` |
| Blister inspection (foil types, pocket content) | AI, taught from good blisters, 5 foil types | No blister tool; generic surface / presence tools exist | Missing | `inspect.surface`, `ai.*` (Tool 10) |
| Pinhole in foil | 10 um, thin bar sensor | Not our hardware class | Missing | none (hardware sensor) |
| Induction seal check, thermal | Thermal camera | `inspect.cap` is optical only | Missing | `inspect.cap` plus thermal source |
| Tablet / capsule inspection, counter | 32,000 / min, AI, special reject | Not built | Missing | `ai.*`, `inspect.surface`, `blob.analyse` |
| 360 degree tablet (6 views, colour + backlight + 3D) | 18 images, up to 400,000 / hour | Not built | Missing (large hardware item) | `mce.multicam`, `calib.stitch` |
| Checkweigher | 3 models, +-0.1 to +-0.5 g | Not vision | Missing; out of scope | none |
| Anti-tamper label with camera check | Label alignment | Label presence tools only | Partial | `inspect.label`, `locate.geometric` |
| Serialisation (print serials, L1 to L3) | eTrack L3, 220 cartons/min | No serial generation; we read and aggregate | Missing | PLANT-PLATFORM (plant server) |
| Aggregation database, parent-child | Yes, 4 levels incl. pallet | Duplicate memory 10,000 child and 1,000 case codes; no portal export | Partial | `code.read` case mode, plant server |
| Duplicate-code check | Not stated | Have (orange state) | Have | `code.read` |
| Rework flow | Audit trail of reworked serials (L3 blog) | No rework path | Missing | plant server |
| Government portals (DAVA XML, EU hub, Tatmeen and others) | 21 systems claimed | None | Missing | plant server export module |
| ERP / L4 / L5 | L4 designed; no connectors named | PLANT-PLATFORM plan; ERPNext licence only | Partial (plan) | `mce.database`, `mce.mqtt`, `mce.opcua` |
| One-button teach, good samples only | Claimed in all machines | Teach exists per tool; no anomaly teach | Partial | Tool 10 AI, Teach layer (L5) |
| Auto tolerance | Claimed | Auto-tolerance planned | Partial | L5 |
| On-the-go setting change | Claimed | Not specified | Missing | Job editor live edit |
| Rejection verification | eSampoorna only | PLC handshake; line-safety NG on doubt | Have (design) | `base.logic`, PLC-DELTA-DVP |
| Part 11 (audit trail, user levels, e-records) | "Ready", validation pack | audit_log table in plan; licensing; Part 11 pack not built | Partial | `base.log`, plant server |
| Validation package (URS, IQ, OQ, PQ) | Blog + documents | TEST-ACCEPTANCE-PLAN, no IQ/OQ/PQ set | Partial | docs |
| Edge only, no cloud | Yes | Yes (line PC) | Have | n/a |
| Languages | 6 web languages | 15 UI languages planned; only `en.json` exists (dotnet/VisionStudio/i18n, dotnet/Studio/i18n) | Missing (15 planned; only en.json exists) | LOCALISATION |
| Open tool library (locate, measure, calibrate, blob) | Not exposed | 12 tool groups specified; Read code, Read text and Find the part built; the rest not built | Partial (wider by design) | Tools 0 to 12 |
| Customer proof | 200+ clients, ~1,300 machines | In-house, own line | Missing | n/a |

## 8. What Vision_System should adopt or beat

Effort in person-weeks (pw), one developer with an AI assistant; indicative.

| # | Item | Why | Tool id | Layer | Effort |
| --- | --- | --- | --- | --- | --- |
| 1 | **Pharmacode on real cartons**: the decoder exists (`pharmacode.cpp`); measure it on real cartons and add Pharmacode to the bench | Sun sells it; common on Indian cartons | `code.read` linear | L3 | 0.5 pw + owner images |
| 2 | **Print and inspect station**: code + OCR + OCV + presence in one job, one pass, one NG | Sun's eSanket / print-and-inspect proposition; ours is built in parts | `code.read`, `ocr.*`, `verify.*`, `base.logic` | L3, L4 | 2 pw |
| 3 | **One-button teach from good samples** (anomaly / auto-tolerance) | Sun's key advantage; strongest idea to adopt | Tool 10, `inspect.surface` | L5 | 4 pw |
| 4 | **Blister inspection tool** (pocket content: present, count, colour, crack, foreign) with foil presets (Alu, PVC, Aclar) | Biggest pharma family we lack | `inspect.surface`, `colour.match`, `ai.*` | L2, L3 | 6 pw |
| 5 | **Tablet / capsule inline inspection** (multi-lane; size, shape, colour, foreign) | eTabspec at 32,000/min; needs fast cameras and rejector | `blob.analyse`, `colour.*`, `ai.*` | L3 | 8 pw |
| 6 | **Multi-view product inspection** (colour + backlight + 3D per side; 18 images per product) | eSampoorna recipe; use the same image-set idea for caps, bottles | `mce.multicam`, `calib.stitch` | L0 | 6 pw plus hardware |
| 7 | **Reject verified** (sensor or camera confirms the part really left) | eSampoorna does it; we must guarantee a bad part never passes | `base.logic`, PLC | L4, L6 | 1 pw |
| 8 | **Thermal seal check** (thermal camera source plus ROI temperature rules) | eSaksham; hardware buy: thermal GigE camera | `inspect.cap`, `base.source` | L0, L3 | 3 pw plus hardware |
| 9 | **Serialisation and aggregation chain** (GS1 serial pool, batch notice, parent-child tables, rework and reconcile, DAVA XML export) | Sun's eTrack L3 is the product behind their lines; our plant server holds the data | plant server (PLANT-PLATFORM) | L6 | 10 pw |
| 10 | **Part 11 pack** (e-signature, user levels, immutable audit, record retention, backup, validation documents and IQ / OQ / PQ templates) | Sun sells "Part 11 Ready" and validation | `base.log`, plant server | L6 | 5 pw |
| 11 | **Rejection analysis report** (reject reason by batch with image, top causes) | Named in eDrashti and eTabspec | `base.log`, `script.spc` | L6 | 2 pw |
| 12 | **On-the-go setting edit** (change tolerances without stopping, with audit and rollback) | Sun claim; must stay safe: edit applies to the next part only, logged | Job editor | L5 | 3 pw |
| 13 | **Beat Sun: measured numbers.** Publish detection and false-reject rates on a real data set, with the miss reasons (LOW_CONTRAST, BLUR, TOO_SMALL, NO_FINDER, COVERED) | Sun gives none; this is our proof | docs, TEST-ACCEPTANCE-PLAN | L4 | 1 pw |
| 14 | **Beat Sun: grading and code verification** (ISO 15415 / 15416, GS1 rules) in the same job | Sun does not mention grading | `verify.*` | L3 | in plan |
| 15 | **Beat Sun: open tool library** (locate, measure, calibrate, colour, scripting) for non-pharma jobs | Sun sells fixed machines | Tools 0 to 12 | all | in plan |
| 16 | **Beat Sun: multi-code aggregation accuracy** (DM 48 at 64.2%): needs a higher-resolution camera and faster lights, then own decode | Sun multi-2D gives no number | `code.read` | L0, L3 | see 05-HARDWARE |
| 17 | **Hardware to buy** if the owner wants these families: 12 to 20 MP area cameras with global shutter, strobe lights with polarisation, thermal GigE camera, backlight panels, air-reject valves with a proof sensor, line-scan unit for foil pinholes | Raises accuracy and speed | see [05-HARDWARE](05-HARDWARE.md) | L0 | n/a |

Not worth copying: the checkweigher (separate mechanical product), the ERP login portal, the blog
programme (content marketing).

## 9. Not opened / limits

- **Video frames and transcripts.** I read only RSS data and each video page's description. No transcript
  was downloaded, no screen was seen. Descriptions are marketing posts.
- **Brochure OCR.** The PDF has no text layer; text is from `tesseract` on 2,400 px renders. Tables (eArjun
  sizes, eDrashti model sizes) can have misread digits. Only page 5 was checked visually.
- **Pages not opened:** the six language copies; per-country T&T pages
  (`online-track-and-trace-for-*`, `offline-track-and-trace-for-*`); most of the ~40 blog posts (seven
  opened); Team, History, Careers, Gallery, Certificate, Media, Events, Testimonials, Podcast, Exhibition,
  `/lp/`, `/lic/` pages. The Certificate page may list real certifications; unknown.
- **WebFetch summaries** came from a small model that reads each page; the pages themselves were not
  inspected as raw HTML. Product pages said they have no specification tables beyond those cited.
- **Not found:** Sun's cameras (make, resolution) for eDrashti, eTabspec and eSampoorna; pinhole method;
  false-reject or detection rates; software names beyond eTrack; L4 and L5 connectors; iVEDA support;
  Part 11 evidence (audit certificate or third-party validation); any test report.
- **Inconsistencies in the vendor's own numbers** (installations, years, countries, products) are kept as
  found. Treat all counts as marketing.
- Relationship to the owner (competitor or partner) is not known to this study.
