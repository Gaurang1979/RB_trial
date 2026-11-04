# Study 01b: Machine-vision software of the other vendors, SDKs and open source

Part of the Vision_System study of 2026-10-30. Cognex and MVTec (HALCON) are in the other
chapter, 01a. This chapter covers Teledyne DALSA, Omron, National Instruments, Keyence, Zebra
(Aurora, Matrox), the smaller vendors, and the open-source libraries and runtimes.

Owner request (2026-10-30): "i discussed with lawyer and we can use all patents and process in our
library. Start working on detailed study report of all softwares, all patents, all documents,
tools, screen shots. I need almost 100% accuracy in all tools with required timeline."

Rules used here (from the common brief):

- Patents are described as methods to adopt. Where a patent is named, the note says "owner's statement of
  2026-10-30 (legal clearance)". Status and expiry are Google Patents' own estimates.
- Copyright is separate. Nothing from a vendor (code, models, manual text, pictures) goes into the
  repo. "Screens" are described in words, with the vendor page where the picture can be seen.
- Layer names L0 to L6 are the common model: L0 Acquire, L1 Correct, L2 Find, L3 Read or measure,
  L4 Verify and decide, L5 Learn and tune, L6 Output and audit.
- Our tool ids are from [TOOL-SPECIFICATIONS.md](../TOOL-SPECIFICATIONS.md). Status words:
  Have / Partial / Missing. Effort in person-weeks (pw), one developer with an AI assistant.
- **Source marks.** Every link without a mark was opened on 2026-10-30 and read. **[S]** means seen
  only as a search-result summary: treat as unverified. **[X]** means the page was tried and could
  not be read (404, 403 or binary PDF); the fact comes from a search summary. Vendor numbers are
  marketing unless a test is described.

Read with [INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md), which already lists methods per tool and
the ranked missing methods. This chapter does not repeat those tables. It adds the product view:
what each vendor ships, how the user works with it, and what we take.

## Contents

1. [How to read the adopt tables](#1-how-to-read-the-adopt-tables)
2. [Teledyne DALSA](#2-teledyne-dalsa)
3. [Omron](#3-omron)
4. [National Instruments](#4-national-instruments)
5. [Keyence](#5-keyence)
6. [Zebra: Aurora Vision, Aurora Design Assistant, MIL, fixed scanners](#6-zebra)
7. [Other vendors](#7-other-vendors)
8. [Open source, SDKs and runtimes](#8-open-source-sdks-and-runtimes)
9. [Cross-product table](#9-cross-product-table)
10. [What to build first: ranked list from this chapter](#10-what-to-build-first)
11. [Not opened / limits](#not-opened--limits)

## 1. How to read the adopt tables

Each product ends with a table "What Vision_System should adopt". Columns:

- **Idea:** the product feature, in our words.
- **Our tool id:** from TOOL-SPECIFICATIONS.md (0.x Base, 1.x Locate, 2.x Measure, 3.x Calibrate,
  4.x Blob, 5.x Code Reader, 6.x Verify, 7.x OCR/OCV, 8.x Inspect, 9.x Colour, 10 AI, 11.x Multi
  camera and Enterprise, 12.x Script).
- **Status:** Have / Partial / Missing, as of the engine inventory of 2026-10-30
  ([../../engine/src](../../../engine/src), [../../docs/changes](../../changes)).
- **Layer and effort:** L0 to L6, pw.

Statuses repeat what INDUSTRY-METHODS.md and the inventory say. A "Partial" usually means the
method is inside the code reader (`engine/src/decode.cpp`, `lattice.cpp`, `font.cpp`) and has no
user-facing tool yet.

---

## 2. Teledyne DALSA

Teledyne Vision Solutions sells the DALSA software line. Pages: Sherlock
([page](https://www.teledynevisionsolutions.com/products/sherlock/)), Sapera Processing
([page](https://www.teledynevisionsolutions.com/products/sapera-processing/)), Astrocyte
([page](https://www.teledynevisionsolutions.com/products/astrocyte-ai/)), BOA2
([page](https://www.teledynevisionsolutions.com/products/boa2/)).

### 2.1 The product family at a glance

| Product | What it is | Who programs | Runs on |
| --- | --- | --- | --- |
| Sherlock 8 | No-code vision development environment, point and click, runs a finished job on a PC | Integrator, no code; .NET API for C++, C#, VB; GUIBuilder for the operator screen | Any Windows x64 PC; or bundled with VICORE, GEVA 400/4000, GEVA 312T vision systems |
| Sapera Processing | C++ / .NET SDK of the same algorithms | Programmer | Windows, Intel CPU, optional GPU |
| Sapera Vision Software | The family name for Sapera LT (acquisition) plus Sapera Processing (algorithms) | Programmer | Windows / Linux |
| iNspect Express | Embedded job editor and runtime of the BOA smart camera | Integrator | On the camera (BOA2, now discontinued) |
| Astrocyte | Code-free AI trainer (classification, anomaly, detection, segmentation) | Integrator, no code | Windows 10/11, NVIDIA GPU >= 8 GB for training |
| Sapera AI | The AI inference licence in Sapera Processing | Programmer | GPU or CPU |
| Z-Trak 3D Apps Studio | 3D inspection with DALSA laser profilers, inside Sherlock from 8.30 | Integrator | Windows |

Facts read on the pages: Sherlock 8.50 current, an AI Plug-in exists for 8.50, software download
needs sign-in, thermal (infrared) and X-ray cameras are supported, mixed cameras in one job are
allowed, third-party GigE cameras work through GenICam, USB only through DirectShow. Frame grabbers
Xtium2, Xtium and Xcelera work through Sapera LT.

### 2.2 Sherlock 8: tool list and options

Source: Sherlock page. Tool groups as the page lists them. Options below are what the page states;
the Sherlock manual (login) was not opened.

| Group | Tools / what the page says | Main options we should know |
| --- | --- | --- |
| Preprocessors | Image conditioning applied to a region of interest before analysis | Per-ROI, so each tool can have its own filter chain (our Base 0.7 is global) |
| Positioning | Pattern finding for locating and aligning objects | Used as the fixture for later tools |
| Measurement | Edge location, dimensioning of parts and shapes | |
| Analysis | Edge counting, feature extraction, contrast detection, statistics | |
| Calibration | Linear, nonlinear and perspective distortion; pixels to real-world units | |
| Colour | Teach colours or measure RGB values | |
| Barcode | Reads and verifies 1D and 2D codes, with grading | Grading is inside the tool |
| OCR | Taught character sets, or a pre-trained neural-net OCR tool | Two engines in one tool |
| 3D | Z-Trak 3D Apps Studio, 8.30 and later | |
| Thermal | Infrared applications | |
| Development UI | Point-and-click programming, dockable windows, flexible ROI options, monitor and report windows, built-in editing and debugging | |
| Custom GUI | GUIBuilder (no code) or the .NET API | |

### 2.3 Sapera Processing: tools in depth

Source: [Sapera Processing page](https://www.teledynevisionsolutions.com/products/sapera-processing/).
These are the most detailed published numbers of the DALSA line. All are vendor claims, no test is
described.

| Tool | What it does | Published options and numbers |
| --- | --- | --- |
| Search, area-based | Normalised cross-correlation | Position accuracy 1/25 pixel; "extremely fast when no scaling is involved" |
| Search, edge-based | Matches object contours; rotation and contrast independent | Position 1/50 pixel, angle 1/80 degree, up to 5 instances |
| Blob analysis | Segments objects, geometric and grey features | 1-bit, 8-bit, 16-bit input; unlimited frame length with line-scan; automatic threshold; sort and select by feature |
| OCR | Scale, contrast, rotation and aspect-ratio invariant | Solid and dot-matrix fonts, italics, trainable fonts; binary algorithm (faster) and grey algorithm (tolerates noise); several strings per run; Unicode output including Asian |
| Quick Barcode | High-speed 1D and 2D reading, tolerant of noise, occlusion, distortion | Two licences: standard and DPM (etched or dot-peened) |
| Standard Barcode | More parameters, "golden model", Barcode Studio setup | Two licences: Standard (no training) and Advanced (adds Data Matrix model training) |
| Print quality grading | Verifies marks | ISO/IEC 15416, 15415, 16022, AIM DPM, AS9132 |
| Calibration | Lens distortion, positioning distortion, arbitrary distortion | Feeds the 2D Measurement tool |
| AI Inference (Sapera AI licence) | Runs Astrocyte models | Classification, anomaly, detection, segmentation; continual learning for classification; tiling for small defects in large images; runtime heatmaps; GPU or CPU |

Key points for us:

- Two barcode engines (a fast one and a "golden model" one with training) match our own split:
  fast decoders first, slow rescue steps after. The "Advanced" Data Matrix model training is the
  same idea as our `learned.cpp` per-product state.
- Edge-based search with 1/50 px and 1/80 deg is the vendor level to compare with our requirement of
  0.05 px and 0.05 deg (requirements section 13). They quote it without a test.
- OCR with a binary and a grey algorithm: the binary one is fast, the grey one tolerates noise. We
  have this split already (`ocr.classic` and `ocr.ai`).

### 2.4 Astrocyte: workflow screen by screen (described in words)

Source: [Astrocyte page](https://www.teledynevisionsolutions.com/products/astrocyte-ai/). The page
shows screenshots of the dataset view and of the validation view. Described here from the text.

1. **Dataset screen.** Images come from a live camera or frame grabber, or from PNG, JPG, BMP, GIF,
   TIFF files. Masks exclude regions. The user sees a thumbnail grid and a class list.
2. **Annotate screen.** The user draws labels by hand, or uses a pre-trained model or semi-
   supervised object detection to propose labels. Pascal VOC, MS COCO and KITTI label files can be
   imported. Rotated boxes are supported for detection.
3. **Train screen.** Three tuning depths (low, medium, high) set hyperparameters automatically; they
   can also be set by hand. Training can be paused and resumed and can use several GPUs. The page
   says "under 10 minutes with good data".
4. **Validate screen.** Accuracy, recall, mAP, IoU, a confusion matrix and heatmaps.
5. **Export.** A proprietary model file with architecture, weights and metadata. It is imported by
   the AI tool in Sapera Processing or Sherlock.

Other facts: UI languages English, Chinese, Korean. Local training, so images stay on the PC. Training
needs Windows 10 or 11, 16 GB RAM minimum (32 GB ideal), NVIDIA GPU with at least 8 GB, compute
capability 6.0, driver 516.31 or later; the page recommends an RTX 4070 or 5070 or better.
Inference needs a PC with an Intel CPU; a GPU is optional (RTX 4000 / 5000 class recommended).

The four model types and what they are for, in our terms:

| Astrocyte type | Training data | Our use |
| --- | --- | --- |
| Classification | Labelled images per class, continual learning in the field | Cap type, label variant, print state (OK / smeared / missing) |
| Anomaly detection | Good images only, no annotation | Label and cap checks on a new product with no bad samples |
| Object detection | Boxes, also rotated | Locate cases, labels, code areas |
| Segmentation | Pixel masks | Defect shape, then blob tools measure it |

### 2.5 BOA and iNspect Express

Source: [BOA2 page](https://www.teledynevisionsolutions.com/products/boa2/). All BOA2 models are
discontinued. XA2 1664 x 1216 at 20 fps, XA3 1920 x 1440 at 15 fps, XA5 2560 x 2048 at 10 fps, mono,
global shutter, dual-core 1.5 GHz CPU, Ethernet and RS232. Tools of iNspect Express: pattern
matching, precision measurement, flaw detection, verification, presence and absence, robot
guidance, part identification, 2D matrix and barcode reading, OCR. Price not published.

Why it matters to us: a smart camera job is one "page" the operator can understand: trigger,
find, measure, decide, output. Our Studio job page should be as short.

### 2.6 DALSA communication and licensing

- Sherlock talks Modbus, EtherNet/IP, Profinet, OPC UA, MQTT and serial, has onboard discrete I/O
  controls and tools to read and write PLCs and robots (Sherlock page). Our Modbus TCP is the same
  class. OPC UA and MQTT are our tools 11.3 and 11.4 (Missing).
- Licence: Sherlock "licensed to run on any Windows x64 PC" (page); Sapera Processing licences are
  per tool (Quick Barcode standard or DPM, Standard Barcode standard or advanced, AI by Sapera AI).
  No price is published for any DALSA software on the pages read.

### 2.7 What Vision_System should adopt from Teledyne DALSA

| Idea | Our tool id | Status | Layer, effort |
| --- | --- | --- | --- |
| Edge-based search with sub-pixel and angle numbers | 1.2 `locate.geometric` | Missing (spec only) | L2, 3 pw |
| Up to N instances of a model | 1.3 `locate.multi` | Missing | L2, 1 pw (on 1.2) |
| Per-ROI preprocessor chain | 0.7 `base.filter` | Partial (inside reader only) | L1, 1 pw |
| Two OCR engines (binary, grey) | 7.1 / 7.2 | Have | - |
| Model training for Data Matrix ("Advanced") | 5.1 | Partial (`learned.cpp`) | L5, 1 pw |
| Print quality grading in the reader tool | 6.1-6.3 | Have (`grade1d.cpp`, `grade_dpm.cpp`, quality.cpp); no GS1 report tool | L4, 1 pw for report |
| Anomaly detection trained on good images | 10 (anomaly) | Missing (`ml/experiments/anomaly.py` tried, not shipped) | L3 / L5, 4 pw |
| Tiling for small defects in a large image | 10 | Missing | L3, 1 pw |
| Continual learning of a classifier in the field | 10 | Missing | L5, 2 pw |
| Rotated boxes in detection | 1.5 `locate.ai` | Missing | L2, 1 pw |
| Import of COCO / VOC / KITTI labels | 10 (dl.autolabel) | Partial (`ml/autolabel.py`) | L5, 1 pw |
| Heatmap at runtime | 10 | Missing | L6, 0.5 pw |
| Training screen with three tuning depths | 10 | Missing (scripts only) | L5, 3 pw |
| Operator screen builder (GUIBuilder) | 11.6 / Studio | Missing | L6, 4 pw |

---

## 3. Omron

Omron sells three families we care about: FH / FZ vision controllers, the MicroHAWK / Microscan
readers with AutoVISION and Visionscape software, and the LVS verifiers (ex Label Vision Systems,
ex Microscan). Pages opened: [FH page](https://industrial.omron.eu/en/products/fh-vision-system).

### 3.1 FH vision system

Facts from the FH page: controllers FH-L551 (Atom, 4 GB, 2 cameras), FH-2051/2052 (Celeron, 8 GB,
2, 4 or 8 cameras), FH-5051/5052 (Core i7, 8 GB), FH-5551/5552 (Core i7, 32 GB). All run Windows 10
IoT Enterprise LTSC 2019 64-bit. Up to 4 Sentech GigE line-scan cameras (2K, 4K, 8K). SWIR and
polarised cameras exist in two resolutions each. Photometric stereo works with cameras up to 20.4
Mpixel. Communication: Ethernet TCP/IP, EtherNet/IP, PROFINET slave, RS-232C, EtherCAT slave on most
models, USB. A perpetual licence with no subscription is stated. 21 CFR Part 11 and GMP are
available as an upgrade (audit trail, encrypted files, user access control). "Open FH" accepts
algorithms made in HALCON HDevelop. The page quotes over 2,000 parts per minute for Digimarc
watermark decoding and 99.9 % reads on packaging (vendor claims).

**Processing items.** The FH setup software works as a flow of "processing items" in a list; the
user adds an item, draws its region and sets its parameters, and the items run top to bottom with
branches. The full item list is in the FH manuals (login). From the pages that could be read and
from search summaries [S], the items are:

| Group | Processing items | Notes / main options | Source |
| --- | --- | --- | --- |
| Search | Shape Search III (edge model, registered model, high speed), Search (grey correlation), Fine Matching (sub-pixel) | Shape Search III is the core search item [S]; re-teach of Shape Search III has its own help page | [help page](https://omron.helpjuice.com/fh-vision-reteaching-shape-search-iii) [S] |
| Edge | Edge Position, Edge Pitch, Edge Width | Pitch item has its own setup and log in the FQ2 manual [S] | [FQ2 manual](https://www.manualslib.com/products/Omron-Fq2-S-Ch-Series-3608384.html) [S] |
| Defect | Scratch / Defect, Precise Defect, Density defects, EC defect | Names from the F250 brochure layout, graphic unreadable [S] | [brochure](https://assets.omron.eu/downloads/latest/brochure/en/xpectia_fh_brochure_en.pdf) [X] |
| Blob | Labeling | Connected regions with features [S] | same [X] |
| Colour | Colour data, colour inspection | | FH page |
| Text | OCR, character inspection (QUEST character verification) [S] | Verification of print | brochure [X] |
| Codes | Barcode, 2D code, 2D Code II (variable light, washed marks), code quality | 2D Code II named on the FH page | FH page |
| Bead | Glue bead inspection | Named on the FH page; no method published | FH page |
| 3D / guidance | 2D robot guidance, 3D bin picking (high end) | | FH page |
| AI | AI Fine Matching, AI Scratch Defect filter (FH-2052, 5052, 5551, 5552), AI Defect Inspection (self-learning, three steps: prepare images, annotate, learn) | AI Fine Matching reduces over-detection by comparing against a rebuilt good model | FH page |
| Lighting | Photometric stereo / multi-direction lighting | Up to 20.4 Mpixel cameras | FH page |
| Watermark | Digimarc digital watermark decoding | | FH page |

I could not read the item names "Precise Defect" and "Glue bead" in any Omron document; they come
from the brief. The FH page confirms bead inspection and AI scratch defect as capabilities, but not
those exact item names.

**Workflow (described).** The setup PC or the controller shows a scene list, a flow editor on the
left (items in order), an image window in the centre with the drawn regions, and a parameter pane
on the right. "Measurement" and "Run" modes switch between teaching and production. A flow can hold
several scenes (jobs) switched by a PLC command. Screens are in the FH brochure and on the page
above.

**AI.** AI Fine Matching and AI Scratch Defect work inside the normal flow, so the AI item replaces
only the step that over-detects. This is the pattern we want: AI as an item in a classic flow, not a
second product.

### 3.2 MicroHAWK, AutoVISION, Visionscape

Search summaries [S] ([Omron MicroHAWK solutions page](https://industrial.omron.co.uk/en/solutions/product-solutions/microhawk-takes-code-reading-to-the-next-level) [S]):

- MicroHAWK V4x0-F and F4x0-F are small smart readers and cameras with liquid-lens autofocus, 
  built-in lighting, and "X-Mode" decoding for curved, shiny or textured surfaces.
- Two AutoVISION licences: a sensor licence (locate, presence, measure, colour) and a full licence
  that adds X-Mode code reading, trainable-font OCR and code quality inspection.
- AutoVISION is the simple setup; Visionscape is the advanced platform (full auto-ID, verification,
  machine vision tools). AutoVISION projects can move up to Visionscape.
- EtherNet/IP, Ethernet TCP/IP, PROFINET, IP65/IP67, daisy-chain networking.
- The V-series brochure was fetched as a PDF but not readable [X].

AutoVISION screen (from the usual layout, not verified from a page): a left list of "Locate",
"Inspect" and "Read" tools, a live image, a result table below, and a "Train" button on each tool.
Do not rely on this description; see the MicroHAWK page above.

### 3.3 Verifiers: LVS-9510 and related

[LVS-9510 page](https://industrial.omron.eu/en/products/lvs-9510) [S] (page not opened; summary
from search): desktop 1D / 2D verifier, fields of view 76 mm to over 152 mm, ISO/IEC 15415, 15416,
15418, 15426-1 and -2, GS1 US certified, 21 CFR Part 11 ready, "nine ISO parameters" plus blemish,
opacity and human-readable validation. ISO modes are field-upgradable licences. Calibration with
conformance test cards (ISO/IEC 15415 Data Matrix card). Our `grade1d.cpp`, `grade_dpm.cpp`,
`grade_pdf417.cpp` and `quality.cpp` already compute grades. What a verifier adds is a **traceable
calibration** against a conformance card and a **fixed, defined light**; see
[INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md) "Lighting per mark".

### 3.4 What Vision_System should adopt from Omron

| Idea | Our tool id | Status | Layer, effort |
| --- | --- | --- | --- |
| Shape Search III type edge model, scored by visible share | 1.2 | Missing | L2, 3 pw (shared with DALSA row) |
| Fine Matching (sub-pixel refine after coarse match) | 1.2 | Missing | L2, 1 pw |
| AI Fine Matching: compare against a rebuilt good model to cut over-detection | 8.2 + 10 | Missing | L3, 3 pw |
| AI Scratch Defect filter as an item inside a classic flow | 8.4 + 10 | Missing | L3, 3 pw |
| Edge Position / Pitch / Width items | 2.1, 2.7, 2.8 | Missing (spec) | L3, 2 pw |
| Labeling (connected components with features) | 4.1 | Partial (internal) | L3, 1 pw |
| Glue bead item | 8.5 | Missing | L3, 2 pw |
| Photometric stereo from several lights | 8.4, 7.5 (embossed) | Missing | L0 / L1, 4 pw plus lights |
| 2D Code II (reading under variable light, washed marks) | 5.1 | Partial (CLAHE, deblur, lattice, AI enhancer) | L3, measure on real crops |
| X-Mode curved / shiny code reading | 5.1 | Have (cylinders read to 138 deg, glare step) | - |
| Scene list switched by PLC command | 11.1, base.logic | Partial (product master) | L6, 1 pw |
| Audit trail, encrypted job files, user access (Part 11) | 0.15, licence | Partial | L6, 3 pw |
| Open interface to run third-party algorithms inside the flow | 12.1 | Missing | L3, 2 pw |
| Verifier-grade calibration card and fixed light | 6.0 | Partial (`calibration.cpp`) | L4, 2 pw plus hardware |

**Hardware to buy.** Omron lists photometric stereo for up to 20.4 MP. Our MV-CS200 camera is 20 MP,
so a four-direction light head plus a strobe controller is the one purchase that opens embossed
text and dents (see Keyence LumiTrax below).

---

## 4. National Instruments

NI sells Vision Development Module (a library for LabVIEW, LabWindows/CVI and .NET), Vision
Assistant (an interactive builder that writes code), and Vision Builder for Automated Inspection
(VBAI, a no-code state-machine builder). Acquisition is IMAQ (frame grabbers) and IMAQdx (GigE
Vision and USB3 Vision cameras).

### 4.1 Vision Builder for Automated Inspection

Sources: [Vision Systems article on VBAI 3.0 (2006)](https://www.vision-systems.com/boards-software/article/16736701/graphical-environment-supports-branching-looping);
[US7864178B2](https://patents.google.com/patent/US7864178) (the state-diagram patent).
The current NI product pages returned 404, so the step list is from the 2006 article and a search
summary [S] of the NI learning path ([learn.ni.com](https://learn.ni.com/learning-paths/vision-builder-ai) [S]).

**Structure.** A job is a **state diagram**. Each state holds a list of inspection steps that run in
order. Arrows between states are transitions with conditions. Every state has a default transition
that is always true. The article gives these facts: more than 50 tools in the step library, loops with
iteration counts set by a global variable, and an example where an "Inspect" state reads OCR to find
a pack type, then one of three state machines (14, 20 or 24 TPI) checks the part and a failed check
rejects it.

**Step families** (from the article):

| Family | Steps |
| --- | --- |
| Inspection | The 50+ vision tools: pattern and geometric match, edge detect, caliper, find circle, blob, colour match, OCR / OCV, barcode, Data Matrix grading, local adaptive threshold, golden-template compare (from the tool library [S]) |
| Image I/O | IMAQ I/O: TTL and opto-isolated lines, input change detect |
| DAQ | DAQmx analog I/O, digital ports |
| Industrial I/O | Handshake with PLC (Modbus, EtherNet/IP, serial) |
| Image processing | OCV, Data Matrix grading, local thresholding |

**Typical state diagram of a pack line (our reading of the method).**
Initialise -> Wait for trigger -> Acquire -> Find the part -> (if found) Inspect, else NG -> Decide ->
Send result to PLC -> Wait for trigger. Branches choose a product variant. The patent
[US7864178](https://patents.google.com/patent/US7864178) (NI, priority 2005-11-09, listed Active, adjusted
expiry 2027-11-27 by Google's estimate) claims building a program as a state diagram with
transition conditions on object characteristics (object detected, dimension, colour, intensity,
text, barcode, location, pass), nested diagrams, and execution that holds a transition until its
condition is met. Owner's statement of 2026-10-30 (legal clearance). Our tool 12.3 `script.state` is this idea.

**Screens (in words).** The VBAI main window has a state-diagram pane on the left (state icons
joined by arrows), an inspection-step list under the selected state, an image display in the centre
with the drawn regions, and a results panel. Steps are added from a palette. Pictures are in the
NI learning path above.

### 4.2 Vision Assistant, Vision Development Module, IMAQ

Search summaries [S] of the NI docs ([Searching and Matching](https://www.ni.com/docs/en-US/bundle/ni-vision-labview-api-ref/page/imaqvision/searching_and_matching_pal.html) [S],
[Geometric Pattern Matching (feature based)](https://www.ni.com/docs/en-US/bundle/ni-vision-labview-api-ref/page/imaqvision/geometric_pattern_matching_(feature_based)_pal.html) [X]):

| Function | What NI offers | Options seen |
| --- | --- | --- |
| Pattern matching | Grey and colour pattern matching, shape matching | Score 0 to 1000 |
| Geometric matching | Edge-based and feature-based: learn a template (IMAQ Learn Geometric Pattern), then search (IMAQ Match Geometric Pattern); multi-template; advanced learn and match VIs | Number of matches, minimum score; returns geometric features per match |
| Golden template | Compare a part to a good one | Tool in the library [S] |
| OCR | Taught character sets, OCR using Vision Assistant | Training in the interactive tool [S] |
| Data Matrix | Read and grade Data Matrix; "demodulation mode" cell filters [S] (also in [INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md)) | Cell filters, rank by symbols used |
| Acquisition | IMAQ (frame grabbers), IMAQdx (GigE, USB3) | |
| Deep learning | A DL model importer exists in recent releases | Not opened, treat as unverified |

Vision Assistant: an interactive screen with an image browser at the bottom, the script (list of
steps) on the right, and a tool palette on the left. It writes a LabVIEW VI or a C / .NET program
from the script. This "script of steps, then export" is the same idea as our job file plus the
C API.

Licensing and price: not published on the pages that opened. NI sells Vision Development Module as a
development licence plus a deployment licence per target.

### 4.3 What Vision_System should adopt from NI

| Idea | Our tool id | Status | Layer, effort |
| --- | --- | --- | --- |
| State diagram as the job model (states, steps, transitions, default transition) | 12.3 `script.state` | Missing (spec) | L4 / L6, 3 pw |
| Loop over objects with iteration count | 12.3 | Missing | L4, 0.5 pw |
| Result of one step as transition condition | 12.3 | Missing | L4, in 12.3 |
| Industrial I/O step with PLC handshake | 0.14, 11.x | Have (Modbus TCP, `PlcRuntime.cs`) | - |
| Script-then-export (job file + C API) | SDK-API | Have | - |
| Geometric match with score 0 to 1000 and min score | 1.2 | Missing | L2, in 1.2 |
| Golden template compare | 8.2 `inspect.template` | Missing | L3, 3 pw |
| OCR training screen | 7.2 | Partial (font teach) | L5, 1 pw |
| Data Matrix cell filters and rank by symbols used | 5.1 | Partial (no cross-candidate rank, INDUSTRY-METHODS) | L3, 0.5 pw |
| Local adaptive threshold step | 0.9 | Partial (internal) | L1, 0.5 pw |

---

## 5. Keyence

Keyence sells image processing controllers (CV-X, XG-X, VS series, VS-G), vision sensors (IV, IV3,
IV4) and code readers (SR-X, SR-2000, SR-5000 line). Pages opened:
[SR-X](https://www.keyence.com/products/barcode/barcode-readers/sr-x/),
[IV4](https://www.keyence.com/products/vision/vision-sensor/iv4/),
[VS-G functions](https://www.keyence.com/products/vision/vision-sys/vs-g/functions/),
[LumiTrax](https://www.keyence.com/landing/vision/lumitrax-lighting.jsp),
[CV-X training: inspection tools](https://keyence.com/support/user/vision/cvx-training_inspectiontools.jsp).
The CV-X product page and the CV-X functions page returned 404.

### 5.1 CV-X tool list (24 tools, from the Keyence training page)

| # | Tool | What it does |
| --- | --- | --- |
| 1 | Auto-Teach Inspection | Sets up an inspection from a batch of good parts (another page says at least 30) [S] |
| 2 | Black-and-White Area | Count black or white pixels vs a binary threshold |
| 3 | Color Component | RGB or HSB comparison |
| 4 | Color-Specific Area | Count pixels of chosen colours |
| 5 | Shading Pattern | Grey pattern match, presence or relocation |
| 6 | PatternTrax / Outline Pattern | Outline match for presence or repositioning |
| 7 | Total Stain and Each Stain | Defects, stains, scratches; count items |
| 8 | Contrast with Background | Find and count lighter or darker clusters |
| 9 | Blob / Cluster | Count, sort, locate multiple items |
| 10 | Edge Processing and common settings | How edges are detected |
| 11 | Edge Position | Locate an edge |
| 12 | Edge to Circumference | Angle from a radial edge on a curved surface |
| 13 | Edge Slope / Edge Angle | Angle from an edge between two surfaces |
| 14 | Edge Width | Two edges and the distance |
| 15 | Edge Pitch | Centre pitch or gap pitch over a series of edges |
| 16 | Trend Edge overview | Shared settings of the Trend Edge family |
| 17 | Trend Edge Position | Profile along circle, arc, ring: min / max or a position |
| 18 | Trend Edge Width | Width along a part or diameter around the circumference |
| 19 | Trend Edge Stain | Defects along lines, circles, ovals, free curves |
| 20 | 1D and 2D Code Reading | Output the decoded text |
| 21 | OCR / Characters | Extract and read strings in the region |
| 22 | Dimensions and Geometry | Points, lines, shapes |
| 23 | Shading | Average or range grey for presence or a light reference |
| 24 | Position Adjustment | Shift regions to follow the part |

Not on that page, from search summaries [S]: ShapeTrax (edge-feature pattern search, "ShapeTrax3A" on
the VS series e-learning page, and "ShapeTrax 3" with automatic contour settings on XG-X per a German
reseller), AI tools on newer controllers, and LumiTrax.

**Trend Edge** in plain words: the tool does not look for one edge. It draws many short scan lines
along a path, finds the edge on each, and plots the edge position along the path. A burr or chip shows
as a bump in the plot, so one tool finds shape errors on a curved outline without a golden template.
Our Edge flaw tool (8.3) is planned to work this way; it is Missing today (no calipers yet).

### 5.2 LumiTrax

Source: [LumiTrax page](https://www.keyence.com/landing/vision/lumitrax-lighting.jsp). Four
directional lights (upper, right, left, lower); a high-speed camera takes one image under each; the
differences are merged. Named outputs: **texture image** and **shape image**. Used for stamped
characters on cast metal, printed-surface defects ignoring colour, small height changes with less
glare. Light sizes up to a 400 mm light are listed.

For us: this is the same method as photometric stereo in HALCON and Omron FH (INDUSTRY-METHODS
section 3). The bottleneck is hardware: four exposures per part must happen inside the line time.
At 225 parts per minute one part has 267 ms for both cameras, so four 5 ms strobed exposures fit if
the camera and the light controller can be triggered in sequence (Missing: action-command sync,
tool 11.2).

### 5.3 VS series and VS-G

Source: [VS-G functions](https://www.keyence.com/products/vision/vision-sys/vs-g/functions/).
32-core controller with built-in AI engine, runs AI segmentation 13x faster than VS-L/S/C for a
5 MP image (vendor claim); VS-AD1 is needed for AI Segmentation and AI Detection Fine Mode.
Dedicated image storage with a new compression above 100x at equal quality, so good-part images can be
kept. **Auto Image Selector / Auto Parameter Tuning:** the user picks a setup image and marks the
defect location; the tool guides to settings. **Settings verification:** stored images re-judge
after a change to confirm earlier verdicts did not change. Up to 8 cameras, 30+ camera types.

Screen in words: a left list of tools in the program flow, a centre image with the region frames,
a bottom results row. For each tool a wizard guides from the sample image to a trial.

### 5.4 IV / IV3 / IV4 vision sensors

Source: [IV4 page](https://www.keyence.com/products/vision/vision-sensor/iv4/).

| AI function | What it does |
| --- | --- |
| AI Imaging II | Tests over 10,000 imaging conditions, picks the one where the target stands out |
| AI Target Extraction | Removes background elements that cause false detections |
| AI Identify | One registered image keeps detection stable against changes |
| AI Count (and AI Through Count) | Learns features from several viewpoints; counts targets that pass |
| AI OCR | Reads characters, dates, numbers from built-in learned data; characters not in the dictionary can be taught |
| Numeric output | X, Y and angle deviation |
| AI trigger | No separate trigger sensor |

Setup uses OK and NG images; the sensor analyses colour, brightness, shape, area and edges
[S: [control.com](https://control.com/news/ai-powered-imaging-keyence-introduces-iv4-h-series-vision-sensor/)]. PoE, IP67. Price page not opened.

### 5.5 SR-X and SR-2000 code readers

Source: [SR-X page](https://www.keyence.com/products/barcode/barcode-readers/sr-x/).

- **SR-X Drive** decoding with a built-in AI chip trained on over 100,000 images (vendor claim) for
  stains, scratches, rough backgrounds, uneven cell colour. No read-rate figure is published.
- **Auto tuning:** automatic focus, "press of a button", 3-button unit.
- 72 % less volume than the old model, IP67, rotating connector, HDR wide CMOS, built-in three-way
  light (direct, polarised, diffused).
- **Linked readers:** readers on one network pass code information from an earlier station to a later
  one, which improves later reads. Setup in a browser by IP address, no software.
- **Web Traceability Tool:** statistics and error images showing which reader failed, when and why.
  **Web Multi Monitor** shows all units in one screen (LiveView).
- Keyence patents on the AI restore step: US11854163, US12039400 (see INDUSTRY-METHODS section 14).
  Owner's statement of 2026-10-30 (legal clearance).

### 5.6 What Vision_System should adopt from Keyence

| Idea | Our tool id | Status | Layer, effort |
| --- | --- | --- | --- |
| Auto-Teach from N good parts | 5 / 7.3 / 8.2 | Partial (OCV limits learned from good parts) | L5, 2 pw |
| Trend Edge Stain (many scan lines along a curved outline) | 8.3 `inspect.edge` | Missing | L3, 2 pw |
| Edge Pitch / Width / Slope / to Circumference | 2.7, 2.8, 2.6 | Missing | L3, 2 pw |
| Total / Each Stain | 8.4, 4.1 | Missing | L3, 1.5 pw |
| LumiTrax-style four-light capture, texture and shape images | 8.4, 7.5, 0.x | Missing | L0 / L1, 4 pw plus lights |
| Auto Image Selector (choose teach images, mark the defect) | 10, 8.x | Missing | L5, 1 pw |
| Re-run stored images after any change, list verdicts that changed | 0.15, release gate | Partial (model gate only) | L5, 1 pw |
| Image storage with strong compression for good parts | 0.15 `base.log` | Partial (saves images; no compression policy) | L6, 1 pw |
| AI Imaging: search over imaging conditions | 0.2 | Partial (`Calibration.cs` search) | L0, 1 pw |
| Linked readers: earlier station helps later station | 5.5, 11.1 | Partial (duplicates memory, product master; ERP list Missing, R8) | L4, 3 pw |
| Reader statistics and error images, browser view | 0.15, 11.6 | Partial (plant dashboard) | L6, 1 pw |
| AI restore of a degraded code image | 5.1 | Have (`enhancer.onnx`, after normal decode) | - |

---

## 6. Zebra

Zebra bought Adaptive Vision (now Aurora Vision) and Matrox Imaging (now Aurora Imaging Library,
MIL, and Aurora Design Assistant), and sells FS-series fixed industrial scanners.

### 6.1 Aurora Vision Studio and Aurora Vision Library

Sources: [Zebra product page](https://www.zebra.com/us/en/products/software/machine-vision/aurora-vision-studio.html),
[documentation index (v5.7.4)](https://docs.adaptive-vision.com/current/studio/),
[all-filters list](https://docs.adaptive-vision.com/current/studio/filters/AllFilters.html).

- **What it is.** A drag-and-drop vision builder. A program is a **data-flow graph of filters**:
  each filter is a box with input and output ports; connections carry images, regions, arrays,
  numbers. The docs cover connections, conditional execution, arrays, and "macrofilters" (a group of
  filters saved as one). Aurora Vision Library is the same filters as a C++ / .NET library.
- **Size.** The all-filters page states **3808 filters** in total (libraries: GenICam and GigE
  Vision, Standard Library, Third Party).
- **Filter groups seen.** Template matching (`CreateEdgeModel2`, `CreateGrayModel`,
  `EnhanceSingleObjectMatch`), shape fitting (`DetectCircle_LSD`, `FitCircleToEdges`,
  `FitPathToRidges`, `FitSegmentToStripe`), golden template (`CreateGoldenTemplate2`,
  `CompareGoldenTemplate_Edges`, `CompareGoldenTemplate_Intensity`), OCR (`ExtractText`,
  `ExtractText2`), blobs (`ExtractBlobs_Intensity`, `ExtractBlobs_Range`,
  `FindMaxStableExtremalRegions`), code generation (`GenerateDatamatrixCode`). The code-reading
  filters were in the part of the page I could not read.
- **UI (words).** Left: Project Explorer and the filter catalogue. Centre: the program graph with
  filters and wires. Right or bottom: filter properties and the image / data preview, a
  **Diagnostic Mode** that shows the data on every wire, and a Statistics view (time per filter).
  A Filmstrip control browses stored images. Template matching, OCR and golden-template models are
  set in **guided setup pages**. Workspaces store window layouts. HMI screens can be designed for the
  operator.
- **Deep learning.** The docs mark deep-learning pages as another product: the **Deep Learning
  Add-on**. A 2024 press release reports new anomaly detection tools and a new training engine with
  data balancing [S: [Nasdaq](https://www.nasdaq.com/press-release/zebra-technologies-adds-new-deep-learning-tools-aurora-machine-vision-software-2024) timed out; [Logistics IT](https://www.logisticsit.com/articles/2024/09/02/zebra-technologies-adds-new-deep-learning-tools-to-aurora-machine-vision-software) [S]].
- **Licence.** Not on the pages read. A free trial and a fact sheet are offered; a "Remote License
  Upgrade" topic exists. No price.

### 6.2 Aurora Design Assistant (ex Matrox Design Assistant)

Source: [tech specs PDF](https://www.zebra.com/content/dam/support-dam/en/documentation/unrestricted/presentation/design-assistant-tech-specs-en-us.pdf) [X] (binary, only the section titles read: tools list, flowchart IDE, communications protocols, deep learning, hardware, licensing, operator interface)
and a search summary [S]. It is an IDE where an application is built as a **flowchart** (not code).
Deep-learning tools for anomaly detection, classification, object detection and segmentation.
Operator interface built in the same tool. Hardware includes Matrox Indio / Iris GTR smart cameras
and PCs. The deep-learning add-on is inference on Linux; training needs an NVIDIA GPU [S].

### 6.3 Aurora Imaging Library (MIL)

Sources: [MIL user guide intro](https://aurorahelp.zebra.com/aurorail/MILPP1/HelpFilesPP1/extfile/z_MIL_UG_introduction/UserGuide/introduction/Introduction_to_MIL_and_MILLite.htm) [S],
[MIL 9 fact sheet](https://www.zebra.com/content/dam/zebra_new_ia/en-us/support-and-downloads/industrial-machine-vision-and-fixed-scanners/aurora-imaging-library/9/mil9-fact-sheet-en-us.pdf) [X] (404 on fetch).
A C / C++ / C# / Python SDK of hardware-independent modules:

| Module | What it does (from summaries [S]) |
| --- | --- |
| Model Finder | Geometric pattern matching, edge-based, rotation and scale search |
| Pattern Matching | Grey correlation |
| Metrology | Fit lines, circles, tolerance checks, also 3D profile features (MIL 10 PP3 notes) |
| Edge Finder | Edge points and contours |
| Measurement | 1D and 2D measurement markers |
| Blob Analysis | Regions and features |
| Bead Inspection | Width and defects of a glue or sealant bead along a path |
| Code Reader | 1D and 2D reading and verification; MIL 10 PP3 notes list Code Reader as deprecated (replaced by a newer reader) |
| String Reader | OCR for printed and dot-matrix strings, with font training |
| Colour, Calibration, Registration, 3D | Standard |
| Deep learning | Classification, segmentation, anomaly in recent versions |

MIL is the oldest of the libraries here and the usual reference for "Model Finder" accuracy
claims. A dongle or software licence is needed; price on request.

### 6.4 Zebra fixed industrial scanners FS40, FS42, FS70

Sources: [FS40 spec sheet](https://zebra.com/content/dam/zebra_dam/en/spec-sheets/fs40-spec-sheet-en-us.pdf) [S],
[FS42 reference guide](https://docs.zebra.com/us/en/machine-vision/fixed-scanners/fs42-fixed-industrial-scanner-product-reference-guide/getting-started/configurations.html) [S].
FS42: 2.3 MP autofocus (5 MP version at some resellers), fast or standard 2D decoder, red or white
light, Ethernet / PoE, serial, USB, industrial protocols, positioned for deep-learning OCR; one
reseller claims an NPU [S, unverified]. Configured through Aurora (one platform to set up, deploy,
run). FS40 and FS70 are the same family with software upgrades for vision tools.

### 6.5 What Vision_System should adopt from Zebra

| Idea | Our tool id | Status | Layer, effort |
| --- | --- | --- | --- |
| Data-flow graph as the job editor, with a Diagnostic Mode showing data on every wire | Studio, 12.x | Partial (tool list; no graph) | L6, 6 pw |
| Macrofilter (saved group of tools reused) | 12.1 / job format | Missing | L6, 2 pw |
| Statistics view: time per tool | 0.15, TEST-ACCEPTANCE | Partial (time per step in reader) | L6, 1 pw |
| Filmstrip of stored images | Studio | Partial (NG review) | L6, 1 pw |
| Guided setup page for template, OCR, golden template | 1.2, 7.2, 8.2 | Partial (Teach page for OCR / code) | L5, 2 pw |
| Edge-model match with enhance-single-object step | 1.2 | Missing | L2, in 1.2 |
| Golden template: edges variant and intensity variant | 8.2 | Missing | L3, in 8.2 |
| Bead inspection along a path | 8.5 | Missing | L3, 2 pw |
| Metrology tolerances | 2.x | Missing | L3, 1 pw |
| Deep-learning add-on as separate licence | 10, LICENSING | Partial (spec) | L5 |
| Anomaly with data balancing in the trainer | 10 | Missing | L5, 2 pw |

---

## 7. Other vendors

### 7.1 Basler pylon vTools and pylon AI

Sources: [pylon vTools](https://www.baslerweb.com/en/software/pylon-vtools) [X: 406 on fetch; summary from search [S]].
Tools: Code Readers (barcode, Data Matrix, QR, PDF417, Aztec), template and geometric pattern
matching, colour or threshold blob analysis, and "pylon AI vTools" (classical or AI). Licences
**Starter** (limited), **Basic** and **Pro**; trial of 80 days in the pylon Viewer, or 5 minutes per
run for 180 days in an app (sources disagree). Works with every Basler camera visible through pylon.
Basler also sells the Euresys **Open eVision** library (see 7.5). Idea for us: vTools are
callable from the camera SDK; our C API plays that role. Price: not published.

### 7.2 SICK: Lector, Inspector, Nova, dStudio

Sources: [SICK Nova page](https://www.sick.com/us/en/catalog/products/machine-vision-and-identification/machine-vision/sick-nova/c/g557626) (only names visible),
[Inspector83x press](https://www.vision-systems.com/directory/software/ai/press-release/55091107/sick-inc-sick-inspector83x-delivers-ai-quality-control-out-of-the-box) [S].
Inspector83x runs AI on the device (quad-core CPU and AI accelerator, mono or colour, up to 5 MP),
taught from examples; complex scenes can be trained on the cloud service **dStudio** and exported
as a small file. OCR / OCV and rule-based tools combine with AI. **Nova** is the preinstalled app
platform (immediate commissioning), extendable with Lua and HALCON. Lector83x is the code-reading
sibling, FPGA based (AMD case study) [S]. Idea for us: **Lua-style sandbox script** = tool 12.1.

### 7.3 Datalogic / Data Sensing: IMPACT and Matrix readers

The product page redirected to datasensing.com and was not read further. IMPACT is Datalogic's
vision software and Matrix is its reader line [no page read]. Nothing is claimed here. Re-check:
[datasensing.com](https://www.datasensing.com/).

### 7.4 Hikrobot VisionMaster (VM) and MV-ID readers

The owner uses Hikrobot cameras, so this one matters. Sources: Hikrobot site (download page and
product page: no feature text opened [X]); search summaries [S]:
[qviro VM page](https://qviro.com/product/hikrobot/vision-master) [S],
[MV-ID3050XM datasheet](https://belgorod.dataid.ru/upload/iblock/718/ejyyhilg3b72omoy239jnpi1jg70zqzs/MV-ID3050XM_Industrial_Code_Reader_datasheet_20260129_7126.pdf) [S].

- **VisionMaster:** over 1000 operators (third-party listing, 30 % confidence on the review site),
  three modes: graphical flow, SDK, operator-screen design. Traditional tools: locate, measure,
  OCR / OCV, 1D / 2D code reading. Deep learning: classification, target detection, character
  positioning and recognition, segmentation; anomaly heat maps; training inside the platform from
  annotation to detection. Aimed at logistics and packaging.
- **MV-ID readers:** fixed and handheld, 0.4 MP (MV-ID2004M), 1.3 MP (MV-ID2013M), 5 MP
  (MV-ID3050XM, up to 90 codes per second per the datasheet [S]); 1D (Code 128, EAN, ITF) and 2D (QR,
  Data Matrix); the 3050XM datasheet says it uses a deep-learning algorithm.
- **For us:** VM is the free-with-camera option the owner already has. A fair benchmark is: run the
  same 58 sample images through VM's code tool and compare with our 92.2 %. That is a test, not a
  study item; it needs a Windows PC (the owner has none, per memory), so it is Missing and listed
  under limits.

### 7.5 Euresys Open eVision (now sold by Basler)

Source: [Open eVision documentation home](https://documentation.euresys.com/Products/OPEN_EVISION/OPEN_EVISION_2_16/en-us/Content/00_Home/home.htm);
Basler's Open eVision page appeared in search [S].

| Library | Function |
| --- | --- |
| EasyImage, EasyColor | Pre-processing grey and colour |
| EasyObject | Blob analysis |
| EasyGauge | Sub-pixel measurement |
| EasyFind | Geometric pattern matching with feature points (faster and more robust than correlation per Euresys [S]) |
| EasyMatch | Area pattern matching |
| EChecker2 | Golden template validation |
| EasyBarCode, EasyMatrixCode, EasyQRCode (with Micro QR) | Codes |
| EasyOCR, EasyOCR2 | Text; OCR2 is deep-learning based, short text such as part numbers, serials, expiry and manufacturing dates [S] |
| EasyDeepLearning, EasyClassify, EasySegment, EasyLocate | DL inspection: classify, segment defects, locate objects and defects |
| Easy3D, Easy3DLaserLine, Easy3DObject, Easy3DMatch | 3D |

Euresys also publishes the flat-field correction guide cited in INDUSTRY-METHODS. Licence: per
library, dongle or software key; no price on the pages read.

### 7.6 Stemmer Common Vision Blox (CVB)

The CVB product page returned 404 [X]. From general knowledge only (not verified here): CVB is a
library of tools (matching, OCR, barcode, DL "Polimago") for Stemmer cameras. Not described further.

### 7.7 Mech-Mind (Mech-Vision, Mech-DLK) and Landing AI LandingLens

- **Mech-DLK** ([community post V2.5.0](https://community.mech-mind.com/t/topic/2178) [S]): a stand-alone
  deep-learning platform; modules for classification, defect segmentation (including unsupervised),
  instance segmentation, and OCR (text detection plus text recognition for digits, letters, some
  symbols). Model export for Mech-Vision and SDKs in C, C++, C#. A European reseller lists
  EUR 3,530 to 16,710 excluding VAT [S]. Mech-Vision links to robots (3D picking), less relevant.
- **LandingLens** ([page](https://www.landing.ai/landinglens)): no-code platform with four steps
  **Upload, Label, Train, Deploy**. Label book (class definitions), flags for mislabelled images,
  collaborative labelling, cloud or edge deployment (Windows app or API), continuous learning from
  deployment data. The page does not name model types or licence terms.

### 7.8 Siemens SIMATIC MV, Banner, Neurocle, SuaKIT

SIMATIC MV page and Neurocle page returned 404 or no data; SuaKIT's domain did not resolve; Banner
was not searched. **Nothing is claimed for these four.** See limits.

### 7.9 What Vision_System should adopt from the other vendors

| Idea | Source | Our tool id | Status | Layer, effort |
| --- | --- | --- | --- | --- |
| Licence tiers Starter / Basic / Pro with a trial | Basler | LICENSING | Partial (VS-xxx codes) | L6, 0.5 pw |
| Sandbox script (Lua) next to rule tools | SICK Nova | 12.1 | Missing | L4, 2 pw |
| Cloud-trained AI exported as a small file to run on the box | SICK dStudio | 10 | Partial (offline training, ONNX) | L5 |
| Upload, Label, Train, Deploy as four screens | LandingLens | 10 | Missing | L5, 4 pw |
| Label book and mislabel flags | LandingLens | dl.autolabel | Missing | L5, 1.5 pw |
| Text detection plus recognition as two models | Mech-DLK, docTR | 7.1 | Have (CRNN; detection partial) | - |
| Unsupervised segmentation | Mech-DLK | 10 | Missing | L3, 3 pw |
| Open library of many tools behind one licence | Euresys, Basler | all | Have as design | - |
| Hikrobot VM as a cross-check on real images | Hikrobot | TEST-ACCEPTANCE | Missing (needs Windows PC) | L5, 1 pw |
| EasyOCR2 for dates and serials | Euresys | 7.1, 7.4 | Have (own OCR; real laser 100 %) | - |

---

## 8. Open source, SDKs and runtimes

Everything below was opened on 2026-10-30 unless marked.

### 8.1 Libraries we use or could use

| Library | What it is | Licence | State and notes | Link |
| --- | --- | --- | --- | --- |
| OpenCV | Image processing, calibration, DNN | Apache-2.0 | In use. The contrib page names no module list in its README; list is `modules/README.md` (not opened) | [opencv_contrib](https://github.com/opencv/opencv_contrib) |
| OpenCV contrib modules of interest | wechat_qrcode, ximgproc (guided filter, ridge), xfeatures2d, text, dnn_superres, phase_unwrapping, structured_light, barcode | Mostly Apache-2.0; some xfeatures2d code is patented/non-free | Names from general knowledge, **not verified here** | [opencv_contrib](https://github.com/opencv/opencv_contrib) |
| zxing-cpp | 1D and 2D decoder: EAN/UPC/DataBar, Code 39/93/128, ITF, Codabar, Aztec, Data Matrix ECC200, MaxiCode (partial), PDF417, QR Model 1 (read) and 2, Micro QR, rMQR | Apache-2.0 | In use. Thread safe, C++20, writer via zint since 3.0. README does not show tryHarder-type options | [zxing-cpp](https://github.com/zxing-cpp/zxing-cpp) |
| libdmtx | Data Matrix ECC200 read and write, tools dmtxread / dmtxwrite | Simplified BSD for the core; other parts LGPL v2 | In use (`CR_HAVE_DMTX`). README gives no runtime options; docs are on an old wiki | [libdmtx](https://github.com/dmtx/libdmtx) |
| ZBar | EAN-13/UPC-A, UPC-E, EAN-8, Code 128/93/39, Codabar, ITF, QR, SQ Code | LGPL-2.1 | 787 commits, 117 open issues; no Data Matrix. LGPL needs care in a signed package | [ZBar](https://github.com/mchehab/zbar) |
| BoofCV | Java vision library: QR, fiducials, calibration, features, optical flow | Apache-2.0 | Java; useful as a second opinion for QR and calibration, not for the engine | [BoofCV](https://boofcv.org/index.php?title=Main_Page) |

### 8.2 Commercial code SDKs

| SDK | What it offers | Licence | Note |
| --- | --- | --- | --- |
| [Dynamsoft Barcode Reader](https://www.dynamsoft.com/barcode-reader/docs/core/introduction/index.html) | 1D: Code 39/128/93, Codabar, EAN, UPC, ITF, Industrial 2 of 5, MSI, Code 11/32, GS1 DataBar; 2D: QR, Micro QR, Data Matrix, PDF417, Aztec, MaxiCode, DotCode; postal; Pharmacode, Telepen, GS1 Composite. Batch scanning "100+ barcodes in one pass". Web, mobile, desktop, server | Commercial, 30 day trial, store | Mode lists are in the API: `expectedBarcodesCount` (0 = find at least one; 1 = fastest; highest plausible for many), `localizationModes` (array up to 8), `deblurModes` (array of 10 slots) [S: [settings reference](https://www.dynamsoft.com/barcode-reader/docs/server/programming/cplusplus/api-reference/simplified-barcode-reader-settings.html) [S]]. Binarisation and texture-detection modes not confirmed |
| [Scandit](https://www.scandit.com/products/barcode-scanning/) | AI scanning, context-aware choice of the intended code, falls back to OCR of the printed digits when a code is too damaged, MatrixScan (multi-code), AR overlays, DPM Data Matrix listed | Core, Standard, Advanced tiers; flat annual or usage-based; 30 day trial | Vendor claims: under 200 ms decode, "zero false positives for all major barcode types" (marketing, no test) |

Ideas worth taking: **expected count drives effort** (we have `slots.cpp`); **OCR of the printed
digits next to a damaged code** (Scandit; we have the OCR engine and the human-readable line of
GS1-128 labels: Missing as a cross-check, 1.5 pw, L4); modes as an ordered list with a time budget
(same shape as our step list).

### 8.3 OCR and detection models

| Project | What | Licence | Facts read | Link |
| --- | --- | --- | --- | --- |
| PaddleOCR / PP-OCRv5 | Detection + recognition, many languages | Apache-2.0 | Multilingual model has 2M parameters; adds Devanagari, Telugu, Tamil, Arabic, Cyrillic (109 languages); C++ deployment, Paddle Inference or ONNX Runtime; OpenVINO, ONNX Runtime and TensorRT acceleration; a PP-OCRv6 sizes tiny 1.5M / small 7.7M / medium 34.5M. "Over 40 %" better accuracy for some models is vendor text | [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) |
| docTR | Two-stage OCR; detectors DBNet, LinkNet, FAST; recognisers CRNN, SAR, MASTER, ViTSTR, PARSeq, VIPTR; layout LW-DETR | Apache-2.0 | PyTorch; no ONNX or benchmarks on the page | [docTR](https://github.com/mindee/doctr) |
| MMOCR | Many detectors and recognisers (DBNet++, FCENet, ABINet, ASTER, SATRN, SVTR) | Apache-2.0 | Last release 1.0.0 on 2023-04-06; maintenance unclear | [MMOCR](https://github.com/open-mmlab/mmocr) |
| anomalib | PatchCore, PaDiM, STFPM and more; MH-PatchCore, RAD, FoundAD in v2.7.0; OpenVINO export; benchmarks via YAML | Apache-2.0 | ONNX legacy exporter removed in 2.7.0; Anomalib Studio pre-release low-code app | [anomalib](https://github.com/open-edge-platform/anomalib) |
| Ultralytics (YOLO26) | Detection, segmentation, classification, OBB, pose, depth | **AGPL-3.0 or paid Enterprise Licence** | Enterprise licence needed for closed products. Our job file and C API ship in a closed package, so **do not embed Ultralytics code or weights without the Enterprise licence** | [Ultralytics](https://github.com/ultralytics/ultralytics) |
| RT-DETR / RT-DETRv2 / v4 | Real-time transformer detector | Apache-2.0 | COCO, 640 px, T4 TensorRT FP16: R18 46.5 AP at 217 FPS; R50 53.1 AP at 108 FPS; R101 54.3 AP at 74 FPS. v4 released 2025-11-18, no numbers on the page | [RT-DETR](https://github.com/lyuwenyu/RT-DETR) |
| SAM 2 / 2.1 | Promptable segmentation, images and video | Apache-2.0 | A100: tiny 38.9M params 91.2 FPS; large 224.4M 39.5 FPS. Useful as an **annotation helper** only, not on the line | [SAM 2](https://github.com/facebookresearch/sam2) |

### 8.4 Runtimes

| Runtime | Use | Facts | Link |
| --- | --- | --- | --- |
| ONNX Runtime | Run ONNX models on many chips | Execution providers: CPU, oneDNN, XNNPACK, CUDA, TensorRT, TensorRT RTX, OpenVINO, MIGraphX, DirectML, QNN, CoreML (preview), WebGPU and more. Licences are per provider | [EP list](https://onnxruntime.ai/docs/execution-providers/) |
| OpenVINO | Intel CPU, iGPU, NPU | Apache-2.0; C++, Python, C, NodeJS; reads PyTorch, TensorFlow, ONNX, TFLite, PaddlePaddle | [OpenVINO](https://github.com/openvinotoolkit/openvino) |
| TensorRT | NVIDIA GPU inference | Open-source part (plugins, ONNX parser, samples) Apache-2.0; the GA binaries are separate downloads under NVIDIA terms; current GA 11.4.0.106; linux x86-64, aarch64, Windows | [TensorRT](https://github.com/NVIDIA/TensorRT) |

Our engine runs ONNX through OpenCV DNN today. **Hardware to buy:** an NVIDIA RTX 4060 / A2000 class
card (the T2 target in TOOL-SPECIFICATIONS) for detection, segmentation and anomaly nets; Astrocyte's
own page recommends RTX 4000/5000 for inference and an RTX 4070 or better with 8 GB or more for
training, which is a fair reference. OpenVINO on the i7's iGPU covers T1.

### 8.5 What Vision_System should adopt from open source

| Idea | Our tool id | Status | Layer, effort |
| --- | --- | --- | --- |
| PP-OCRv5 or docTR recogniser as a second AI OCR to vote with ours (Apache-2.0) | 7.1 | Missing (own CRNN only) | L3, 2 pw |
| Devanagari / Tamil / Telugu text support from PP-OCRv5 for Indian labels | 7.1 | Missing | L3, 2 pw with real images |
| PatchCore / EfficientAD via anomalib export to OpenVINO | 10 anomaly | Missing | L3, 4 pw |
| ONNX Runtime with TensorRT / OpenVINO providers instead of OpenCV DNN | 10, engine | Missing | L3, 2 pw |
| RT-DETR as an AI locator (Apache-2.0, not Ultralytics) | 1.5 `locate.ai` | Missing | L2, 3 pw |
| SAM 2 to pre-label masks | dl.autolabel | Missing | L5, 1 pw |
| WeChat QR detector as a candidate finder (OpenCV contrib [S]) | 5.1 | Missing | L2, 1 pw; test only |
| Licence hygiene: keep LGPL (ZBar) and AGPL (Ultralytics) out of the signed package | LICENSING | Have (not used) | - |

---

## 9. Cross-product table

Legend: **Y** = product has it as a named tool; **P** = partial or only as part of another tool;
**-** = not found on the pages read (not proof it is absent); **[S]** = from search summary only.
Columns: DALSA = Sherlock / Sapera; Omron = FH + MicroHAWK; NI = Vision Builder / Development
Module; Keyence = CV-X / VS-G / IV4 / SR-X; Zebra = Aurora Vision / MIL / Design Assistant;
Basler = vTools; SICK = Nova / Inspector; Hik = VisionMaster; Euresys = Open eVision. The last
column is Vision_System today (engine inventory 2026-10-30).

| Function (layer) | DALSA | Omron | NI | Keyence | Zebra | Basler | SICK | Hik | Euresys | Vision_System today |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Edge-based geometric search (L2) | Y 1/50 px, 1/80 deg | Y Shape Search III [S] | Y | Y PatternTrax / ShapeTrax [S] | Y Model Finder [S], edge model | Y | P | Y [S] | Y EasyFind | Missing (1.2); own match for DM / code only |
| Grey correlation (L2) | Y 1/25 px | Y Search | Y | Y Shading Pattern | Y | Y template | P | Y [S] | Y EasyMatch | Have (`engine/src/fixture/locate_tool.cpp`, normalised correlation) |
| Multi-instance (L2) | Y 5 | Y | Y | Y blob | Y | P | P | P | Y | Missing (1.3); slots for codes Have |
| AI locate / detect (L2) | Y Astrocyte | Y | P | Y VS-G AI | Y | Y pylon AI | Y | Y | Y EasyLocate | Partial (`ai_locator.cpp` for codes) |
| Edge / caliper measure (L3) | Y | Y Edge items | Y | Y 10 edge tools | Y Metrology | P | P | Y | Y EasyGauge | Missing (2.x) |
| Circle / line fit (L3) | Y | P | Y | Y | Y | P | P | Y [S] | Y | Missing (2.2-2.5) |
| Pitch / gap (L3) | P | Y Edge Pitch | P | Y Edge Pitch | Y | - | - | P | P | Missing (2.7, 2.8) |
| Calibration, grid, lens (L1) | Y | Y | Y | P | Y | - | P | Y [S] | P | Partial (`calibration.cpp`, grading only) |
| Blob (L3) | Y | Y Labeling | Y | Y Blob | Y | Y | P | Y | Y EasyObject | Partial (internal) |
| 1D / 2D reading (L3) | Y Quick + Standard | Y 2D Code II | Y | Y | Y Code Reader | Y | Y Lector | Y | Y | Have; 92.2 % on real sample set |
| DPM reading (L3) | Y DPM licence | Y X-Mode | P | Y SR-X | P | P | P | P | P | Partial (filters built; user params spec) |
| AI-assisted decode (L3) | P | P | - | Y SR-X Drive | P | P | P | Y [S] | P | Have (enhancer, after normal decode) |
| Print grading ISO 15415 / 15416 (L4) | Y | Y LVS | Y DM grading | P | Y | - | P | - | P | Have (grade1d, dpm, pdf417, quality) |
| GS1 data validation (L4) | P | P | - | P | - | - | - | - | - | Have (6.4 spec; GS1 rules in CodeReader) |
| OCR (taught) (L3) | Y | Y | Y | Y | Y String Reader | - [S] | Y | Y | Y EasyOCR | Have (classic font) |
| AI OCR (L3) | Y pre-trained | Y [S] | P | Y IV4 AI OCR | Y DL OCR [S] | P | Y | Y [S] | Y EasyOCR2 | Have (CRNN ONNX, real laser 100 %) |
| OCV with learned limits (L4) | P | Y QUEST [S] | Y | Y IV4 [S] | Y | - | Y | P | Y | Have (0 wrong accepted; false rejects 31 % on laser) |
| Golden template compare (L3) | P | Y Fine Matching | Y | Y Auto-Teach | Y 2 variants | P | P | P | Y EChecker2 | Missing (8.2) |
| Surface defect, classic (L3) | Y | Y Defect items | P | Y Stain / Trend Edge | Y | P | Y | Y | Y | Missing (8.4) |
| Anomaly, trained on good images (L3 / L5) | Y Astrocyte | Y AI Defect | - | Y IV4 learns variation [S] | Y 2024 | Y [S] | Y | Y [S] | P | Missing (tried, 0 / 56 torn labels) |
| Classification DL (L3) | Y + continual | Y | P | Y | Y | Y | Y | Y | Y EasyClassify | Missing (10) |
| Segmentation DL (L3) | Y | Y AI Scratch | - | Y VS-G | Y | Y | P | Y | Y EasySegment | Missing |
| Photometric stereo / multi-light (L0 / L1) | - | Y | - | Y LumiTrax | - | - | - | - | - | Missing |
| Bead / glue (L3) | - | Y | - | P | Y Bead | - | - | P | - | Missing (8.5) |
| Colour (L3) | Y | Y | Y | Y 3 tools | Y | Y | Y | Y | Y EasyColor | Missing (9.x), engine is grey |
| State machine / flowchart job (L4 / L6) | P flow list | Y flow list | Y state diagram | P program flow | Y data-flow / flowchart | - | P | Y flow | - | Missing (12.3) |
| Script sandbox (L4) | Y .NET | Y Open FH (HALCON) | Y LabVIEW | - | Y | Y | Y Lua | Y | Y | Missing (12.1) |
| Auto-teach / auto-tune (L5) | P | P | P | Y Auto-Teach, AI Imaging | P | - | Y | Y [S] | - | Partial (learned.cpp, TuningAgent, Calibration.cs) |
| Re-judge stored images after change (L5) | - | - | - | Y VS-G | - | - | - | - | - | Partial (models only) |
| PLC: Modbus, EtherNet/IP, Profinet, OPC UA (L6) | Y all | Y EIP, PROFINET, EtherCAT | Y | Y | Y | P | Y | Y | P | Partial (Modbus TCP Delta DVP; others Missing) |
| Audit trail, access control, Part 11 (L6) | P | Y upgrade | P | P | - | - | - | - | - | Partial |
| Linked stations / traceability (L6) | - | - | - | Y SR-X linked readers | - | - | - | - | - | Partial (duplicate memory; ERP list Missing) |
| Licensing | per tool, Windows PC | perpetual | dev + deploy | per unit | per tool, dongle [S] | Starter / Basic / Pro | with device | with camera [S] | per library | VS-xxx codes (LICENSING.md) |
| Published price | none | none | none | none | none | none | none | none | none | - |

## 10. What to build first

Ranked from this chapter. "Gain" is a forecast; the bench decides.

| Rank | Item | Why (source) | Tool ids | Effort | Layer |
| --- | --- | --- | --- | --- | --- |
| 1 | Edge-model geometric search with sub-pixel refine, multi-instance | Every vendor has it; base of fixtures and measure | 1.2, 1.3, 1.4 | 4 pw | L2 |
| 2 | Edge, caliper, pitch, width, circle tools | Keyence 10 edge tools, Omron Edge items, DALSA, MIL Metrology | 2.1-2.9 | 4 pw | L3 |
| 3 | State diagram job and sandbox script | NI, Zebra flowchart, SICK Lua | 12.1, 12.3 | 5 pw | L4 / L6 |
| 4 | Golden template (edge and intensity variants) with learning from N good parts | Zebra, Keyence Auto-Teach, NI, Euresys | 8.2, 5 | 4 pw | L3 / L5 |
| 5 | Anomaly and classification with a trainer screen and tiling | DALSA, Omron, Zebra, anomalib | 10 | 8 pw | L3 / L5 |
| 6 | Second AI OCR (PP-OCRv5 or docTR) voting with ours, Indian scripts | Open source, Euresys OCR2 | 7.1 | 2 pw | L3 |
| 7 | OCR of the printed digits next to a damaged code as a cross-check | Scandit | 5.1, 7.4 | 1.5 pw | L4 |
| 8 | Multi-light capture (photometric stereo) with a strobe controller | Keyence LumiTrax, Omron | 8.4, 7.5, 11.2 | 4 pw + lights | L0 / L1 |
| 9 | Re-judge stored images after any change; image archive with compression | Keyence VS-G | 0.15 | 2 pw | L5 / L6 |
| 10 | ERP serial list as exact candidates, linked stations | Keyence SR-X linking, INDUSTRY-METHODS R8 | 5.5, 11.1 | 3 pw | L4 |
| 11 | OPC UA, MQTT, Ethernet/IP, Profinet | DALSA, Omron, Zebra | 11.3, 11.4 | 4 pw | L6 |
| 12 | ONNX Runtime with TensorRT / OpenVINO | Runtimes | engine | 2 pw | L3 |

Hardware to buy, from this chapter: an NVIDIA RTX 4060 / A2000 class GPU; a four-direction light
head and strobe controller for the 20 MP camera; a conformance card and fixed light for verifier-
grade grading.

## Not opened / limits

- **Not read (404, 403, timeout or binary):** Keyence CV-X product and functions pages; NI Vision
  Builder and Vision Development Module product pages (current); Zebra MIL 9 fact sheet; Zebra Design
  Assistant spec (binary PDF, only section titles); Zebra Aurora docs filter index; Omron FH and
  MicroHAWK brochures (binary PDF); Omron `ia.omron.com` FH feature pages (403); Basler pylon vTools
  (406); Hikrobot VisionMaster and MV-ID official pages; Stemmer CVB; Siemens SIMATIC MV;
  Neurocle; SuaKIT; Dynamsoft parameter reference (404); SICK Inspector83x Design News article
  (403); Zebra DL press release on Nasdaq (timeout).
- **Not searched:** Banner, Datalogic IMPACT feature text, Cognex and MVTec (chapter 01a).
- **Search-summary only [S]:** Omron processing item names beyond those on the FH page; Omron
  MicroHAWK and AutoVISION licences; LVS-9510; NI step library and geometric matching; MIL module
  list; SICK Inspector83x and Lector83x; Zebra FS40 / FS42; Hikrobot VM and MV-ID; Mech-DLK; Basler
  vTools; Keyence IV4 variation learning, ShapeTrax.
- **Not verified:** the OpenCV contrib module names in section 8.1 (README had no list); the NI
  Deep Learning model importer; the AutoVISION screen layout (written from the usual layout, marked
  in the text); CVB description.
- **Prices:** none of the vendor pages read published a price, except a reseller range for Mech-DLK
  (EUR 3,530 to 16,710 excl. VAT [S]) and Scandit's tier names without amounts.
- **Accuracy and speed figures** (DALSA 1/50 px, Scandit under 200 ms and "zero false positives",
  Omron 2,000 ppm and 99.9 %, Keyence 100,000 training images, 13x VS-G) are vendor claims with no
  test described. None is a basis for our targets.
- **Not tested here:** nothing was built or run; the "Have / Partial / Missing" words come from the
  inventory of 2026-10-30 and INDUSTRY-METHODS.md, not from a new code review.
- **Patents:** only US7864178 (NI) was opened in this chapter. Keyence US11854163 and US12039400 are
  cited from INDUSTRY-METHODS.md. India members of any patent were not checked. Owner's statement of
  2026-10-30 (legal clearance).
