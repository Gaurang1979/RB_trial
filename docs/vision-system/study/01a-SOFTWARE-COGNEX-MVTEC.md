# Study 01a: Cognex and MVTec software

Date: 2026-10-30. Branch: claude/tool-study. Part of the owner's request of 2026-10-30
("detailed study report of all softwares ... Nothing must left in this study").
Patents: owner's statement of 2026-10-30 (legal clearance). Patented methods named here are methods to adopt.
Copyright is separate: nothing of the vendors' code, models, manuals or screenshots is copied here.
Screens are described in words, with a link to the page where the vendor shows them.

Read with: [INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md), [TOOL-SPECIFICATIONS.md](../TOOL-SPECIFICATIONS.md),
[RESEARCH-COGNEX-HALCON.md](../../RESEARCH-COGNEX-HALCON.md), [AI-DEEP-LEARNING.md](../AI-DEEP-LEARNING.md).
This chapter does not repeat the read-rate and patent tables there. It adds the product view:
what each product contains, how it is used, and what Vision_System must still build.

## 0. How to read this chapter

Source marks used in every table:

| Mark | Meaning |
| --- | --- |
| [O] | Page opened in this session and read. |
| [S] | Seen only in a search snippet. |
| [K] | Known from the vendor's public documentation or earlier project research ([RESEARCH-COGNEX-HALCON.md](../../RESEARCH-COGNEX-HALCON.md)); the page was not re-opened today (cognex.com answered HTTP 429 "too many requests" for most pages on 2026-10-30). Treat as "probably right, verify before relying on it". |

Our status words: **Have** (built and tested), **Partial** (inside the engine or one tool only), **Missing**
(spec only or nothing). The status comes from the inventory of 2026-10-30 and
[TOOL-SPECIFICATIONS.md](../TOOL-SPECIFICATIONS.md); file names are given where a status is Have or Partial.
Vendor numbers are marketing unless a test is described.

Layer names: L0 Acquire, L1 Correct, L2 Find, L3 Read or measure, L4 Verify and decide, L5 Learn and tune, L6 Output and audit.

Our tool ids (from TOOL-SPECIFICATIONS.md): base.* (0.x), locate.* (1.x), measure.* (2.x), calib.* (3.x),
blob.* (4.x), code.read (5.x), verify.* (6.x), ocr.* (7.x), inspect.* (8.x), colour.* (9.x), AI Tool 10,
mce.* (11.x), script.* (12.x).

---

## 1. Cognex VisionPro

### 1.1 What it is

VisionPro is Cognex's PC-based vision library and environment: a .NET and C++ class library
(CogXxxTool classes) plus two builders, QuickBuild (drag-and-drop job builder) and Cognex Designer
(flowchart builder with HMI). VisionPro 10.0 and VisionPro Deep Learning 2.0 were announced together
on 2021-07-28; Cognex says VisionPro 10 is faster and added "SmartLine", described as a hybrid
(rule-based plus AI) tool [S] ([press release](https://www.cognex.com/company/press-releases/2021/visionpro-10-and-visionpro-deep-learning),
search snippet; page itself returned 429). Cognex Deep Learning 2.0 integrates with VisionPro 10 and does
not support Cognex Designer, per the release notes [S]
([release notes PDF](https://docs.cognex.com/deep-learning_200/EN/CognexDeepLearningReleaseNotes.pdf)).
Product page: [VisionPro](https://www.cognex.com/products/machine-vision/vision-software/visionpro-software) [K].

### 1.2 Workflow (QuickBuild) [K]

1. Plug in the camera (GigE Vision, USB3 Vision, Camera Link, CoaXPress through frame grabbers, or Cognex acquisition boards).
2. Open QuickBuild. Add a job. Drag the image source ("CogAcqFifoTool") into the job.
3. Add tools by dragging them from a toolbox tree. Drag the output terminals of one tool to the input terminals of the next (for example, PatMax pose into a Caliper's "InputImage" space).
4. Run once or run continuously. Check each tool's result in its own tab.
5. Add a "Results Analysis" or script step for the final pass or fail.
6. Set the job's outputs (digital I/O, serial, EtherNet/IP, PROFINET, Modbus TCP, TCP/IP) in the job manager.
7. Save the .vpp file. Deploy on a runtime PC with a licence dongle or a software licence.

### 1.3 UI screens in words (QuickBuild) [K]

Screenshots: Cognex's VisionPro help site shows the QuickBuild windows ([docs.cognex.com](https://docs.cognex.com), VisionPro section; the exact page depends on the version).

| Screen | Layout | What the user does |
| --- | --- | --- |
| Main window | Left: tree of jobs and tools (toolbox above, job below). Centre: image display with overlays in a record display. Right or bottom: tool editor with tabs. Top: run, run once, continuous, stop, "Jobs" menu. | Builds the sequence of tools, sees all results drawn on the image. |
| Tool editor tabs | Tabs named for the tool: "Training Image", "Train Region", "Search" parameters, "Results", "Region/Mask", "Advanced". | Teaches the pattern or region, sets thresholds, sees score and pose. |
| Terminal view | Each tool shows small input and output terminals; links are drawn as lines. | Connects outputs (pose, image, number) to inputs. |
| Job manager dialog | List of jobs with trigger mode (free run, camera trigger, user), output channel table. | Sets how the job starts and where results go. |
| Display and "Results" panel | Table with pass or fail per tool, score, time in ms. | Reads the timing per tool; finds the slowest. |

### 1.4 Tool groups (VisionPro tool catalogue)

The class names are in the VisionPro API (CogXxx). Source for the list: Cognex tool pages and the project's
earlier research [K]; the user-facing docs are at [docs.cognex.com](https://docs.cognex.com).

#### 1.4.1 Pattern finding: PatMax, PatMax RedLine, PatFlex, PatInspect, SearchMax, CNLSearch

| Tool | What it does | Main parameters | Our id | Our status |
| --- | --- | --- | --- | --- |
| PatMax (CogPMAlignTool) | Geometric (edge-based) pattern find, sub-pixel pose with x, y, angle, scale. Patented ([US 7,016,539 family](https://patents.google.com/patent/US7016539B1) [K]). | Train region, origin; search: angle range (+-180), scale range, accept threshold (0-1), confidence threshold, number to find, overlap, "Grain limit" (coarse-to-fine), contrast threshold, timeout, "Elasticity" (allowed local deformation), "Score using clutter", fit to ideal / measured geometry. Result: pose, score, clutter score, coverage. | locate.geometric (1.2) | Missing (locate.ncc 1.1 is Have: engine/src/fixture/locate_tool.cpp, +-15 to 180 deg, no scale) |
| PatMax multi-model / "PatMax RedLine" (CogPMRedLineTool) | Very fast find using a hardware-friendly version of PatMax, for high-speed lines. Cognex claims up to ~10x faster than PatMax (marketing) [K]. | Same as PatMax with reduced options (angle range, scale off or narrow, number to find), "Training mode" sets a search granularity. | locate.geometric speed mode | Missing |
| PatFlex (CogPatFlexTool) | Finds patterns that bend (curved labels, flexible parts); learns a mesh of control points and returns a deformation field. | Training image, grid (cells), accept threshold, deformation limits, number of nodes. | locate.geometric + deform | Missing |
| PatInspect (CogPatInspectTool) | Compares a live image with a golden image after PatMax alignment: a difference image plus blob analysis of what is different. | Reference image, alignment tool, "Statistical" (mean and sigma image), sensitivity, minimum blob area, region masks. | inspect.template (8.2) | Missing |
| SearchMax / SmartLine (VisionPro 10) | Hybrid find that combines geometric search with a learned line or shape check. | Training, accept threshold. | locate.ai (1.5) | Missing |
| CNLSearch (CogCNLSearchTool, Cognex "normalized correlation") | Grey-scale normalised correlation; fast, tolerant of focus and lighting changes. | Train region, angle range, scale, accept threshold, number, "Zone" search. | locate.ncc (1.1) | Have |
| Multi-instance (PatMax number to find, SearchMax "all") | Finds up to n instances with overlap suppression. | Number to find, overlap threshold, sort by score or position. | locate.multi (1.3) | Missing |
| Geometric pattern description (CogGeometricDescriptionTool, "PMAlign geometric") | Builds a model from CAD or hand-drawn shapes instead of an image. | Shapes (line, circle, arc, rectangle), weights. | locate.geometric (model from DXF) | Missing |

Adopt: PatMax-style model = edge points with direction, pyramid search, sub-pixel least-squares refine,
score with coverage and clutter, and a "grain" setting. The corresponding methods are already ranked as
missing methods in [INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md) section 4. Effort: locate.geometric 4 pw (including scale and
multi-instance), PatFlex-style deformation 3 pw, PatInspect-style statistical compare 3 pw.

#### 1.4.2 Measurement: Caliper, Find Line/Circle/Corner/Ellipse, Edge tools, geometry

| Tool | What it does | Main parameters | Our id | Status |
| --- | --- | --- | --- | --- |
| Caliper (CogCaliperTool) | Projects a rectangle region along a search direction, finds edges by the derivative of the projection profile, returns edge pairs and width. | Region (centre, length, width, angle), edge mode (single / pair), polarity (dark-to-light, light-to-dark, either), contrast threshold, filter half-size (smoothing), "Max results", pair width and tolerance, position-of-edge scoring (weight by contrast, position, polarity), "Edge scoring" functions. | measure.caliper (2.1) | Missing |
| Find Line (CogFindLineTool) | Runs several calipers along a line, fits a line with outlier removal. | Number of calipers, search length, projection length, direction, caliper spacing, "ignore" (outliers removed by number or by RMS), polarity, contrast, fit residual limit. | measure.line (2.2) | Missing |
| Find Circle / Arc (CogFindCircleTool) | Calipers radiating from an expected centre; circle fit. | Expected centre and radius, arc span, number of calipers, search direction (inward/outward), polarity, ignore count. | measure.circle (2.3) | Missing |
| Find Ellipse (CogFindEllipseTool) | Ellipse fit from calipers. | As above. | measure.ellipse (2.4) | Missing |
| Find Corner (CogFindCornerTool) | Two lines and their intersection with angle. | As Find Line, twice. | measure.corner (2.5) | Missing |
| Distance, angle, intersect, create line / circle tools (CogDistancePointPointTool, CogDistancePointLineTool, CogDistanceLineLineTool, CogAngleLineLineTool, CogIntersectLineLineTool, CogIntersectLineCircleTool, CogCreateLineTool ...) | Geometry between results. | Inputs: points, lines, circles. | measure.geometry (2.6) | Missing |
| Edge Width / gap, Pin pitch (done with Caliper pairs and results analysis) | Gap and pitch checks. | Caliper pair parameters, nominal and tolerance. | measure.gap (2.7), measure.pitch (2.8) | Missing |
| "LineMax" / "CogLineMaxTool" [K] | Newer line finder that works on the image directly with a Hough-like accumulate and a model of line quality. | Line length, angle, polarity. | measure.line (alt) | Missing |

Cognex published accuracy: sub-pixel edge accuracy "1/50 pixel" class claims exist for Caliper marketing [K]; no test is
described. Our requirement is 0.1 px at 3 sigma ([REQUIREMENTS](../../REQUIREMENTS-VISION-SYSTEM.md) section 13).
Adopt: the whole Caliper family is the biggest missing block (Tool 2 is entirely spec only). Effort: 5 pw for 2.1-2.6,
2 pw for 2.7-2.9.

#### 1.4.3 Blob and region analysis

CogBlobTool [K]: segment (hard threshold, soft threshold, "relative threshold" for uneven light, fixed and
auto Otsu-like), connectivity analysis, then measure per blob (area, perimeter, centre of mass, bounding box,
principal axes, elongation, "Acircularity", moments, inertia, number of holes, "Chain code"), filter by any measure,
sort, and "Connectivity cleanup" (minimum size, fill holes, morphology). Plus CogBlobTool "Pixel count" and
"Gap detection". Our ids: blob.analyse (4.1), blob.filter (4.4), blob.count (4.3), blob.contour (4.2). Status: Missing as tools
(connectedComponentsWithStats is used internally in locate.cpp, dotcode.cpp, microqr.cpp, dmmatch.cpp, linear.cpp). Effort 3 pw.

#### 1.4.4 Calibration tools

| Tool | What it does | Main parameters | Our id | Status |
| --- | --- | --- | --- | --- |
| Checkerboard calibration (CogCalibCheckerboardTool) | Finds a printed checkerboard and builds a nonlinear transform (lens distortion + perspective) to physical units. | Tile size, origin mode, number of fields of view, "Computation" (linear, projective, radial). Output: RMS residual in physical units, transform "Raw to Calibrated". | calib.grid (3.1) | Partial (engine/src/calibration.cpp uses findChessboardCorners + calibrateCamera, only for module size mm) |
| N-point to N-point (CogCalibNPointToNPointTool) | Pairs of (pixel, real) points give a transform; used for non-grid targets. | Point lists, linear / affine / projective. | calib.npoint (3.3) | Missing |
| Hand-eye calibration (CogCalibHandEyeTool, "Cognex hand-eye") | Links the camera frame to a robot frame, fixed or moving camera. | Robot poses, camera observations, mode (fixed camera, camera on arm), 2D "n-point". | calib.handeye (3.5) | Missing |
| Fixture tool (CogFixtureTool) | Takes a pose from a Find tool and builds an image in the part's frame (the "fixtured image") so later tools do not move. | Run parameters, "Unfixtured from fixtured" transform, an image-mapping interpolation mode (bilinear, high-quality). | locate.fixture (1.4) | Partial (a Locate result shifts code and OCR regions) |
| Image calibration with "Calibrated Image" outputs | Every tool reads and returns in a named coordinate space ("@", "@\\Fixture", "@\\Fixture\\Calibration"). | Space name selection. | base (spaces concept) | Missing |
| Multi-camera "Non-linear stitching" | Joins fields of view with a calibration plate. | Plate, overlap. | calib.stitch (3.6) | Missing |

Cognex's idea of named coordinate spaces ("tree of spaces") is worth adopting: every result carries the space it was
measured in, so a later tool can never mix pixels and mm. Effort: spaces 1 pw, rest per TOOL-SPECIFICATIONS.

#### 1.4.5 ID tools (barcode and 2D codes)

| Tool | What it does | Main parameters | Our id | Status |
| --- | --- | --- | --- | --- |
| IDTool (CogIDTool) with 1DMax | 1D reading, patented "1DMax" uses Cognex's "ScanBars" with deblur and 2-D fitting for damaged codes. | Symbologies (Code 128, Code 39, Code 93, Codabar, I2/5, UPC/EAN, DataBar, Pharmacode, PostNet and others), "Number to find", timeout, ROI mode (full / region), check-digit rules, "Quiet zone" on or off, "Sub-sample". | code.read 1D (5.2) | Have (engine/src/linear.cpp, rules1d.cpp; synthetic 500/500 plus rules) |
| IDTool with 2DMax / PowerGrid | 2D reading (Data Matrix, QR, Micro QR, Aztec, PDF417, MaxiCode, DotCode, Han Xin, GS1 DM / QR). "PowerGrid" reads from the inside using the grid so missing finders are tolerated. | Symbology list, "Polarity", "Mirror", "Quality" levels ("Fast", "Normal", "Enhanced"), DPM tuning ("Contrast: dark on light, light on dark"), "Max symbol size", "Aim Time Out", "Cell size range". | code.read 2D (5.3) | Have (lattice.cpp is the PowerGrid-style method for Data Matrix; the whole reader measures 92.2 % of visible codes on the real samples, not lattice.cpp alone) |
| DM "HotBars" / DMtools [K] | Read dot-peen / low-contrast DM using pre-processing. | Pre-processing filter selection. | code.read DPM (5.4) | Partial |
| ID verifiers: ISO 15415, ISO 15416, AIM DPM (ISO/IEC TR 29158), SEMI T10, AS9132 | Quality grading A-F (4.0-0.0) with ISO parameters. Needs calibrated light and aperture. Cognex's verifier mode in DataMan (section 4). | Aperture, light angle, wavelength, calibration card ("calibrated conformance"), the standards listed above. | verify.* (6.x) | Have (grade1d.cpp, grade_dpm.cpp, quality.cpp; not tested against a calibrated verifier card) |
| Check Digit, GS1 parse | GS1 AI parse and validation. | AI table, rules. | verify.gs1 (6.4) | Have (dotnet/CodeReader/Gs1Check.cs) |

Cognex adds "ID tool results": decoded string, symbology, quality grade, position, cell pitch, "Search time", and
"Decode time". Verified numbers by Cognex: none that describe a test [K].
Adopt (small): ID results should expose the stage times and "number of tries" exactly as in the Cognex results
table; see our `docs/vision-system/` diagnose.cpp reasons (we are ahead there with NO_FINDER, TOO_SMALL etc).

#### 1.4.6 OCR tools: OCRMax and OCVMax

| Tool | What it does | Main parameters | Our id | Status |
| --- | --- | --- | --- | --- |
| OCRMax (CogOCRMaxTool) | Reads text by segmenting characters then classifying with a font trained by the user; patented segmenter (can split touching and broken characters). | Region, text type (line, multi-line), polarity, "Segmentation: character pitch, height, width, stroke width, spacing", "Fielding": "Field definition" with regular expressions or character classes, "Tuning", "Font: Train, Add, Import", "Reject threshold", "Accept threshold", "Max chars". | ocr.classic (7.2) | Have (taught-font cells, engine/src/ocr/font.cpp) |
| "Cognex AI OCR" (VisionPro Deep Learning Blue Read; also the DataMan and In-Sight "OCRMax AI" tool) | Pre-trained network reads text without font training; fine-tune on own images. | Region, max chars, "Regular expression", font type hints (dot matrix, industrial). | ocr.ai (7.1) | Have (CRNN + CTC, ml/ocr, engine/src/ocr/recognize.cpp; laser 92.6 % synthetic, 100 % real laser) |
| OCVMax (CogOCVMaxTool) | Checks that the expected string is present and printed well; scores each character on position, contrast and "Print quality". | Expected string, character set, "Spacing", "Score: Match, Contrast, Quality", "Allow shift", "Accept" threshold, "Contrast" thresholds. | ocr.ocv (7.3) | Have (verify.cpp, quality.cpp; true laser text passes 69 %: false reject issue) |
| Format check, date and lot rules | OCRMax field patterns; "Custom field format". | Pattern grammar. | ocr.format (7.4) | Have (format.cpp automaton) |

Adopt: OCRMax "fielding" UI (each text line with its own pattern and character set) is already in our format
automaton; what is Missing is Cognex's trained-segmentation tool with live per-character pitch and height
sliders (Partial: lines.cpp auto). Effort 1 pw for the UI.

#### 1.4.7 Colour tools

CogColorMatchTool (trained colour reference regions, compares in a colour space, outputs distance), CogColorSegmenterTool
(trained colour "bins", output region), CogColorExtractorTool, CogImageConvertTool (colour to mono: HSI, plane, "Weighted" mix),
"Colour Match": a trained colour, a "tolerance" per channel, a "Pass percent" [K]. Our ids: colour.match (9.1), colour.segment (9.3),
colour.presence (9.2), colour.sort (9.4), colour.deltae (9.5), colour.white (9.0). Status: Missing (the engine is grey only;
engine has no colour conversion). Effort 4 pw; hardware: a colour camera (Hikrobot MV-CS200 colour variant) and a stable
white reference.

#### 1.4.8 Image processing tools

CogIPOneImageTool (an ordered list of operations: filters, morphology, histogram equalise, threshold, "Gradient",
"Sobel", "Laplacian", "Rotate", "Flip", "Scale", "Mean", "Median", "Gaussian"), CogIPTwoImageSubtractTool, CogIPTwoImageAddTool,
CogIPTwoImageMultiplyTool ("two-image arithmetic"), CogPixelMapTool (lookup table), CogHistogramTool (histogram, mean, sigma, percentile),
CogImageSharpnessTool (focus score), CogCopyRegionTool (crop), CogAffineTransformTool, CogFFT, "CogImageFileTool" (file source),
CogAcqFifoTool (camera: exposure, gain, trigger, strobe, "Output pins", "ROI") [K].
Our ids: base.filter (0.7), base.morph (0.8), base.threshold (0.9), base.arith (0.10), base.histogram (0.5), base.crop (0.4),
base.affine (0.12), base.source (0.1), base.camera (0.2). Status: Partial to Missing as user tools (filters, morphology, threshold used
internally in decode.cpp, locate.cpp; no user-facing tool). Effort 3 pw to expose them with a "Clean image" operator list.

#### 1.4.9 Results analysis, tool groups and scripting

| Part | What it is | Notes | Our id | Status |
| --- | --- | --- | --- | --- |
| CogResultsAnalysisTool | Table of rules: input value, comparison, limit, and-or logic; produces "Pass or fail" and a text summary. | Rule list in a grid; each row has "Name", "Operator", "Value". | base.logic (0.14) | Partial (PlcRuntime logic only) |
| CogToolGroup | A group of tools run as one unit with a shared image and inner terminals. | Run mode "run all", "stop on first fail". | job block / tool group | Partial (job file) |
| Script (CogJob script, C# or VB, "CogJobScript") | Hooks "GroupRun", "Initialize", "Shutdown" with .NET code. | Full .NET access. | script.code (12.1) | Missing |
| Data binding / "Image Source Terminal" | Wire any output to any input. | Typed. | job file wiring | Partial |
| CogSerialTool / "CogIO" | Digital IO, serial and network. | Pin map, trigger, protocol. | base.log, PLC | Have for Modbus TCP (dotnet/VisionCore/Plc) |

### 1.5 Cognex Designer [K]

Cognex Designer is a flowchart-style application builder shipped with VisionPro (VisionPro 9 / 10 era). The user draws a flow of
"Cognex Designer tools" (VisionPro tools exposed as flow blocks, plus logic, math, string, comms), and builds the operator HMI
on a second screen with drag-in controls (image view, result lamps, buttons, charts, tables, text boxes). Output goes to the
runtime "Cognex Designer Runtime". Screens (words): left toolbox, centre flowchart canvas, right property grid; HMI tab with a
page canvas, a control palette and a property grid. See [Designer docs](https://docs.cognex.com) [K]. Deep Learning 2.0
is not supported in Designer [S]. Our equivalent: Vision Studio (job editor) and the planned Tool 12; Missing: the user-built
HMI page editor (we have fixed pages). Effort 4 pw for a "custom operator page" editor.

### 1.6 Cognex OneVision [K]

Cloud platform to see many Cognex systems: dashboards for read rates, pass or fail, images, remote changes, job transfer,
user rights. Subscription. Page: [OneVision](https://www.cognex.com/products/machine-vision/vision-software/onevision) [K].
Our equivalent: plant_server + the plant dashboard and mobile app (docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md). Gap:
fleet job deployment with version and rollback (Partial in LICENSING / Vision Package notes).

### 1.7 Licensing, price, performance (VisionPro)

| Item | Value | Source |
| --- | --- | --- |
| Licence model | Per PC; software licence key or USB dongle; separate licence for each tool group "VisionPro Base", "PatMax", "ID", "OCRMax", "Deep Learning"; Runtime vs Development. | [K] |
| Price | Not published by Cognex (quotes through distributors). | no public price |
| Published speed | Marketing only. PatMax RedLine: "up to 10x faster". No timed test described. | [K] |
| Hardware | Windows 10/11, i5/i7, GPU for deep learning (NVIDIA with CUDA), GigE or CXP cameras. | [K] |

---

## 2. Cognex VisionPro Deep Learning (ViDi)

### 2.1 What it is

Cognex Deep Learning (VisionPro Deep Learning, "VPDL", from the 2017 ViDi Systems purchase, and the SuaKIT-origin "High Detail" mode)
has four tools: Red Analyze, Green Classify, Blue Locate, Blue Read [S]
([docs: deep learning tools](https://docs.cognex.com/deep-learning_110/web/en/deep-learning/Content/deep-learning-Topics/overview/deep-learning-tools.htm),
[What is VPDL](https://docs.cognex.com/DEEP-LEARNING_311/WEB/EN/DEEP-LEARNING/Content/deep-learning-Topics/what-is-vpdl/what-is-vpdl.htm)).
Details of this product are in [RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md](../RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md); this section only adds the tool list.

| Tool | Colour | Use | Training | Main parameters | Our status |
| --- | --- | --- | --- | --- | --- |
| Red Analyze (Focused / High Detail / High Detail Quick; Supervised and Unsupervised) | Red | Surface defects, anomalies, segmentation of defect pixels | Unsupervised: only good images; Supervised: pixel masks for defects | Mode (Focused trains and runs faster; High Detail more accurate, slower [S]; High Detail Quick trains much faster and can be switched without relabelling [S]), feature size, "Sampling density", "Perimeter / area" thresholds on the heat map, "Class" of defect | Missing (AI Tool 10 specified; ml/experiments only) |
| Green Classify (Focused / High Detail) | Green | Classify a whole image or a region into labelled classes (OK / NG types) | Supervised with labelled images | Mode, "Feature size", "Max classes", confidence threshold, "Region of interest" | Missing |
| Blue Locate | Blue | Locate and count objects, feature landmarks, "Locate by many models" | Supervised: label the features | Feature size, "Overlap", "Score threshold", "Orientation/scale range" | Missing |
| Blue Read | Blue | Read text with a pre-trained net (no training needed in many cases), optional fine-tuning | Pre-trained; label strings to refine | Region, "Max chars", "Regular expression", "Text orientation" | Have (ocr.ai CRNN; no Cognex-style fine-tune UI) |
| "Few Sample" ideas | – | Train from 1-10 images ("Focused" Red unsupervised) | Few images | "Training set size", "Augmentations" | Missing |

### 2.2 Workflow and UI (Cognex Deep Learning Studio) [K]

1. Create a project; add a workspace; choose a stream (a tool chain: one or more tools in a workspace).
2. Import images (file, folder, or camera). They appear as thumbnails in a bottom filmstrip.
3. Label: draw pixel masks or regions (Red supervised), assign a class (Green), draw features (Blue Locate), or type the string (Blue Read).
4. Split into training and test set ("Train / Test" tags).
5. Train: a button; a live loss curve and a "Progress" bar. GPU required.
6. Test: a "Statistics" panel with confusion matrix, per-class precision and recall, ROC-like threshold curves, a "Heat map" view over images.
7. Set thresholds. Export the stream into VisionPro (VisionPro Deep Learning tool "CogDeepLearning..."), or run in the Cognex runtime.

Screens (words): left tree of tools and databases, centre image view with mask overlay and drawing tools (brush, rectangle,
polygon), right inspector (parameters), bottom thumbnail strip with tags and labels, a "Statistics" tab with the graphs. Screens can be seen in
the docs links above [S] and the [Cognex Deep Learning Studio page](https://www.cognex.com/products/deep-learning) [K].

### 2.3 Licensing and performance

Licence: Cognex Deep Learning licence (dongle or software); training needs an NVIDIA GPU, run can use the same GPU; price not published.
Claims such as "train with 5-10 images" are Cognex marketing; no test is described [K]. Adopt: model "stream" concept (a
chain of AI tool then rule tool), the heat-map review panel, "High Detail vs Focused" speed or accuracy switch, and
"switch mode without relabelling". Our AI layer already stores labelled data per product; Missing: the labelling UI.
Effort: labelling and training UI 6 pw (inside Vision Studio), training backend per
[AI-DEEP-LEARNING.md](../AI-DEEP-LEARNING.md).

---

## 3. Cognex In-Sight and Edge Learning

### 3.1 Products

| Product | Notes | Source |
| --- | --- | --- |
| In-Sight 2800 | Smart camera mixing Edge Learning with rule-based tools, error-proofing aims, EasyBuilder. | [S] [In-Sight 2800 / 3800 pages](https://www.cognex.com/products/machine-vision/2d-machine-vision-systems/in-sight-3800) (429 today) |
| In-Sight 3800 | 2D smart camera, model numbers IS3801, IS3803, IS3805 in mono and colour; EtherNet/IP, Modbus TCP, PROFINET, RS-232, SFTP, TCP/IP; fanless IP67; Cognex says twice the processing speed of previous systems and combines Edge Learning with rule-based tools. | [S] |
| In-Sight 8000 | Ultra-compact standalone system with the form factor of a GigE camera; PoE; two performance tiers. | [S] |
| In-Sight D900 | Deep-learning smart camera, up to 5 MP, mono or colour, IP67, HDR+, autofocus; ViDi runs on the camera with no PC. | [S] |
| In-Sight Vision Suite | One software for all In-Sight, with EasyBuilder and Spreadsheet modes. | [S] |

### 3.2 Software modes

**Spreadsheet (In-Sight Explorer, classic)** [K]: a cell grid. Each tool is a function in a cell (for example
`PatMax`, `EdgePair`, `Blob`, `ReadDM`, `OCRMax`, `Calibrate`); the cell's inputs are other cells. Layout: top toolbar and "Live" button, left
palette of function categories (Image, Geometry, Vision tools: PatMax, PatFind, Edge, Caliper, Blob, Histogram, ID, OCR, Colour, Calibration,
"Inspection" tools, Formula, Communications), middle image with graphics, bottom the sheet. A "Job" has up to 1,000 rows [K];
scripting uses Cell formulas with "Event" triggers and "Task" sequences (Spreadsheet script, Cognex "Scripting" language in other versions).

**EasyBuilder** [K]: a wizard. Four steps: 1 "Set up image" (trigger, exposure, focus, light), 2 "Locate part" (PatMax pattern or "Find Pattern"), 3
"Add inspection tools" (Detect Edge, Detect Blob, Read Code, OCR, Colour, Count Pixels, "Measure"), 4 "Set outputs" (pass or fail,
digital outputs, EtherNet/IP assembly). Each tool shows a parameter panel with sliders, on-image drag boxes and a live score. Screens are in the
[In-Sight EasyBuilder docs](https://docs.cognex.com) [K].

**In-Sight Vision Suite** [S]: combined desktop software that runs EasyBuilder and Spreadsheet jobs and in later releases Edge Learning tools and
ViDi (D900 / 3800) workflows.

### 3.3 Edge Learning tools [K]

Edge Learning (from the 2022 In-Sight 2800) is "learn from a handful of good images" running on the camera: tools are **Presence/Absence**,
**Classify**, **Count**, **Measure (Linear Distance)**, **Locate**, **Detect Defects**, **OCR / Read text**, **Read Barcode**, **Sort**, **Colour**. Each has a few knobs:
"Sensitivity" (slider), "Region", "Edges Learned" or "Tolerance". Training: capture 5-20 images tagged Good, add Bad examples if available;
model and thresholds adapt. Our AI Tool 10 fits this idea; our auto-learn exists as OCV limits, per-product learned state (learned.cpp) and TuningAgent.cs.
Adopt: single-slider "Sensitivity" per tool (maps to threshold sigma), a "Good / Bad" tagging film strip, a "Learn again" on a drifted lot.
Effort: 3 pw on the UI plus per-tool wiring.

### 3.4 Communication, licensing and speed

Protocols: EtherNet/IP, PROFINET, Modbus TCP, TCP/IP, RS-232, FTP, SFTP, SLMP (Mitsubishi), "Native mode" ASCII [S, K]. No Delta DVP-specific driver;
Delta through Modbus TCP. Price: not published; reseller price lists exist (not reliable). Speed: Cognex claims fast; no test published.

---

## 4. Cognex DataMan readers and verifiers

| Product | What | Facts | Source |
| --- | --- | --- | --- |
| DataMan 475V | Fixed-mount verifier: label model grades 1D and 2D to ISO 15416 and 15415; DPM model grades to ISO/IEC TR 29158 (AIM DPM). Grades up to 20 codes a second inline. ISO-compliant verification needs the LabelLight 45-degree light accessory. | Reference manual lists reading, setup, grading reports. | [S] [DataMan](https://www.cognex.com/dataman-475v), [DM475 Verifier Reference Manual](https://docs.cognex.com/dmst_623/EN/DM475_Verifier_Reference_Manual.pdf) |
| DataMan 8072V | Handheld or desk verifier; reseller lists all three light angles (30, 45, 90 degrees) for DPM, ISO 15415, 15416, 29158; field of view about 27 x 20 mm. Cognex's own 8072V sheet was not read. | Reseller claims only. | [S] [qviro listing](https://qviro.com/product/cognex-2/dataman-8072-barcode-verifier/specifications) |
| DataMan 300 / 360 / 380 / 470 / 580 | Fixed readers (not verifiers). | Hybrid Cognex 2DMax / "IDMax" decode, AI-assisted "Hotbars". | [K] |

DataMan Setup Tool [K]: a Windows app with a live image on the left, a tabbed settings panel (Image: exposure, light, focus; Symbology: tick list;
"Format Data"; "Communications": serial, Ethernet, EtherNet/IP, PROFINET, OPC UA, Modbus; "Verification": standard, aperture, calibration). Screens appear in the
manual above [S]. Features worth adopting: "Verify" printouts per code with a barcode "ISO grade card", **AutoTune** of image settings (exposure + gain + light
mode sweep), "Training of symbology" so a reader reads only expected types and length, "Match string" check on read.
Our status: grade report is Have (dotnet/VisionCore/Reports/GradeReport.cs); AutoTune is Have (CodeReader/Calibration.cs, CalibrationSearch.cs);
calibrated verifier hardware is Missing (see owner hardware note in section 11).
Price: not published; DM475V class verifiers are typically quoted at several thousand USD [K, unverified].

---

## 5. MVTec HALCON

### 5.1 What it is

HALCON is a library of about two thousand operators for machine vision (C, C++, C#, .NET, Python, Delphi, with the HDevelop IDE and the newer
HDevelopEVO) [O: [HALCON product page](https://www.mvtec.com/products/halcon/news), which says "from image acquisition to deep learning in one package", multi-core and
GPU, deep learning acceleration with Intel OpenVINO and NVIDIA TensorRT, sub-pixel measurement and calibration]. Documentation index
[O]: [HALCON documentation](https://www.mvtec.com/products/halcon/documentation) (Reference Manual, HDevelop and HDevelopEVO guides, Solution Guides I, II, III-A/B/C, Programmer's
Guides, Technical Notes, release notes from HALCON 12 to 26.05, system requirements: Windows 10/11 and Windows Server 2016-2025 x64, Linux x86_64 and aarch64, CUDA 12.8 for
deep learning for 26.05).

### 5.2 Versions and news (opened)

| Release | News relevant to us | Source |
| --- | --- | --- |
| HALCON 25.11 (12 Nov 2025) | Continual learning for classification (`init_dl_continual_learning`, `extend_dl_continual_learning`); MobileNetV4 classification models and detection backbones; YOLO v8/v9/v11 import for detection (axis-aligned and oriented); Deep OCR updated plus a compact model ("up to 50x faster on embedded" per the press release); QR reading with `deformation_tolerance` = `curved`; Code 128 / GS1-128 with `element_size_variable` for irregular element widths; print quality per ISO/IEC 15415:2024 and ISO/IEC 29158:2025 via `get_data_code_2d_results`, with intermediate results for QR, Micro QR and Aztec; shape-matching score visualisation (which contour parts contribute); `fit_dl_out_of_distribution` with fewer than 15 samples; hyperspectral filters (`convol_channels`, `gen_savitzky_golay_filter`, `gen_canonical_variates_trans`); GenICam chunk data; SBOMs (SPDX JSON); HDevelopEVO script engine preview | [O] [release notes 25.11](https://www.mvtec.com/products/halcon/documentation/release-notes-2511-0), [press release](https://www.mvtec.com/application-areas/press-room/press-releases/article/halcon-2511-increased-speed-for-deep-learning-applications) [S] |
| HALCON 26.05 | Automatic contour pruning in shape models from sample images (`contour_pruning_threshold`, `sample_based_training`, `num_matches_global_enable`); Data Matrix `deformation_tolerance` with a rectification step; `string_encoding` auto and gb2312; `strict_ean13_upc_a`; GS1 AIs 8040-8043 and 717; `edges_sub_pix` suppresses small spurious contours; DL data augmentation pipeline (`create_dl_transform_pipeline` with resize, random geometric, crop, HSV, blur, flip, normalise); `pretrained_dl_detection_rectangle1` small/medium/large; segmentation `tiling` and `tiling_overlap` for large images; Deep OCR with whitespace recognition and INT8 compatibility; image acquisition parameter persistence; JPEG 2000 via OpenJPEG | [O] [release notes 26.05](https://www.mvtec.com/products/halcon/documentation/release-notes-2605-0) |
| HALCON 24.11, 25.05 | Not opened. | not opened |

Why this matters to us: the HALCON code-reading team added "curved / deformed" QR (25.11) and Data Matrix (26.05) tolerance, and 2024 / 2025 ISO grading
editions, in the last year. Both are directly on our list: shrink-wrapped QR is our weakest class (60.3 %), and our grading follows the older editions.
Missing for us: curved-code rectification (partly covered by cylinder experiments, ml/experiments/cylinder.py), ISO/IEC 15415:2024 and 29158:2025 updates (verify.iso15415 Partial).

### 5.3 Operator families (2D) and our mapping

Operator names are from the HALCON Reference Manual ([reference](https://www.mvtec.com/doc/halcon/)) [K] unless marked [O].

| Family | Key operators | Main parameters / options | Our id | Status |
| --- | --- | --- | --- | --- |
| Image acquisition | `open_framegrabber`, `grab_image_async`, `set_framegrabber_param`; interfaces GigEVision2, USB3Vision, GenICamTL, DirectShow, File, HWindows, many vendor interfaces (Basler, Hikrobot via GenTL) | Trigger mode, exposure, gain, ROI, pixel format, chunk data (25.11 [O]), parameter persistence (26.05 [O]) | base.source / base.camera | Partial (Hikrobot MVS only: dotnet/HikCamera; no GenICam generic) |
| Filters | `gauss_filter`, `median_image`, `mean_image`, `anisotropic_diffusion`, `bilateral_filter`, `emphasize`, `sobel_amp`, `edges_image`, `edges_sub_pix` (Canny, Deriche, Lanser, Shen, Sobel), `fft_generic`, `gabor_filter`, `invert_image`, `scale_image`, `illuminate`, `shade_model` (flat-field), `equ_histo_image`, `deconv_wiener`, `deconv_gauss` | Sigma, mask sizes, alpha, filter type, low/high hysteresis | base.filter, base.flatfield | Partial (internal in decode.cpp; deblur Have) |
| Morphology | `dilation_circle`, `erosion_rectangle1`, `opening_circle`, `closing`, `gray_opening`, `skeleton`, `top_hat`, `bottom_hat`, `boundary` | Radius, structuring element, gray vs region | base.morph | Partial |
| Segmentation | `threshold`, `binary_threshold` (auto), `var_threshold` (local), `dyn_threshold`, `auto_threshold`, `watersheds`, `connection`, `select_shape`, `region_features`, `fill_up`, `partition_dynamic` | Gray range, mask width, offset, shape features, min/max | base.threshold, blob.analyse | Partial / Missing |
| Matching: shape-based | `create_shape_model`, `create_scaled_shape_model`, `create_aniso_shape_model`, `find_shape_model(s)`, generic `create_generic_shape_model` + `find_generic_shape_model` | Num levels, angle start/extent/step, scale min/max/step, optimization (none, point_reduction, auto), metric (use_polarity, ignore_global_polarity, ignore_local_polarity), contrast (hysteresis, min size), min score, num matches, max overlap, sub-pixel (least squares), greediness, "timeout", score visualisation (25.11 [O]), contour pruning (26.05 [O]) | locate.geometric, locate.multi | Missing (spec) |
| Matching: deformable | `create_planar_deformable_model`, `find_planar_deformable_model`, `create_local_deformable_model`, `find_local_deformable_model` | Perspective (planar) vs smooth local deformation, scale, angle, deformation smoothness, min score | locate.geometric + deform | Missing |
| Matching: descriptor | `create_uncalib_descriptor_model`, `create_calib_descriptor_model`, `find_calib_descriptor_model` (interest points, rich textures, perspective) | Detector (lepetit, harris...), number of points, pose output | locate.geometric (texture) | Missing |
| Matching: correlation | `create_ncc_model`, `find_ncc_model` | Num levels, angle range, metric, min score, sub-pixel | locate.ncc | Have |
| Matching: component | `create_component_model`, `find_component_model` | Parts and allowed relative motion (articulated assemblies) | locate.multi (parts) | Missing |
| Measuring: 1D | `gen_measure_rectangle2`, `gen_measure_arc`, `measure_pos`, `measure_pairs`, `measure_thresh` | Length1, length2, phi, sigma, threshold, transition (positive/negative/all), select (first/last/all) | measure.caliper | Missing |
| Measuring: metrology | `create_metrology_model`, `add_metrology_object_line/circle/ellipse/rectangle2_measure`, `apply_metrology_model` | Measure length, threshold, num measures, min score, "instances" count, fit method (huber, tukey), distance threshold; combined with camera parameters to give mm | measure.line / circle / ellipse | Missing |
| Subpixel contours (XLD) | `edges_sub_pix`, `fit_circle_contour_xld`, `fit_line_contour_xld`, `fit_ellipse_contour_xld`, `segment_contours_xld`, `smooth_contours_xld`, `contour_to_world_plane_xld` | Algorithm (huber, tukey, ransac), clipping, iterations | measure.* family | Missing |
| Calibration | `create_calib_data`, `set_calib_data_cam_param`, `set_calib_data_calib_object`, `find_calib_object`, `calibrate_cameras`, `get_calib_data`, `calibrate_hand_eye`, `change_radial_distortion_cam_par`, `image_to_world_plane`, `gen_image_to_world_plane_map`, `map_image` | Camera model (area scan division, polynomial, telecentric), calibration plate description file (caltab), reference images, residuals | calib.grid, calib.npoint, calib.handeye, calib.undistort | Partial / Missing |
| 2D data codes | `create_data_code_2d_model` (model types: 'Data Matrix ECC 200', 'QR Code', 'Micro QR Code', 'Aztec Code', 'PDF417', 'GS1 DataMatrix', 'GS1 QR Code', 'DotCode', 'Han Xin' and others), `find_data_code_2d`, `set_data_code_2d_param`, `get_data_code_2d_results`, `get_data_code_2d_objects`, `train_data_code_2d` | Modes: 'standard_recognition', 'enhanced_recognition', 'maximum_recognition'; `polarity`, `symbol_size`, `module_size`, `contrast_tolerance`, `finder_pattern_tolerance`, `mirrored`, `strict_quiet_zone`, `persistence`, `timeout`, `deformation_tolerance` (25.11 and 26.05 [O]), `slant_max`, `module_grid` (aligned / fixed / variable), `small_modules_robustness` (high) for tiny modules, `symbol_shape`. Quality: `quality_isoiec15415`, `quality_aimdpm_1_2006`, `quality_aimdpm_1_2006` etc | code.read 2D | Have (zxing + libdmtx + own; Partial for HALCON's trained-range mode: learned.cpp) |
| Bar codes | `create_bar_code_model`, `find_bar_code`, `set_bar_code_param` (`element_size_min`, `majority_voting`, `num_scanlines`, `min_identical_scanlines`, `merge_scanlines`, `check_char`, `composite_code` (EAN-UCC), `start_stop_tolerance`, `strict_ean13_upc_a` (26.05 [O]), `element_size_variable` (25.11 [O])), `get_bar_code_result`, `get_bar_code_object` | Types: 2/5 Industrial, 2/5 Interleaved, Codabar, Code 39, Code 93, Code 128, GS1-128, EAN-13, EAN-8, UPC-A, UPC-E, GS1 DataBar (Omni, Truncated, Stacked, Expanded, Limited), Pharmacode, MSI, PLESSEY, Add-on 2 / 5. Print quality ISO 15416 via `get_bar_code_result` `'quality_isoiec15416'` | code.read 1D | Have |
| OCR classic | `create_text_model_reader`, `find_text`, `segment_characters`, `create_ocr_class_mlp`, `trainf_ocr_class_mlp`, `do_ocr_multi_class_mlp`, `create_ocr_class_cnn`, `do_ocr_word_cnn`, built-in fonts 'Universal_Rej', 'Industrial', 'DotPrint', 'SEMI', 'Document', 'Pharma' (reads Pharma fonts), 'OCR-A', 'OCR-B' | Text line model: `min_char_height`, `max_char_height`, `min_stroke_width`, `max_stroke_width`, `text_line_structure`, `eliminate_horizontal_lines`, `return_separators`; dot print: `dot_print` flag in the model | ocr.classic | Have |
| Deep OCR | `create_deep_ocr`, `set_deep_ocr_param`, `apply_deep_ocr` (detection and recognition, `recognition_model` 'compact' or 'default' from 25.11 [O]; whitespace recognition 26.05 [O]); `train_dl_model` for fine-tuning with the MVTec Deep Learning Tool | `detection_image_width/height`, `detection_orientation`, `detection_min_character_score`, `recognition_alphabet`, `recognition_char_confidence` | ocr.ai | Have (CRNN; no detection-network mode) |
| OCV | `create_ocv_proj`, `train_ocv_proj`, `do_ocv_simple` | `adaption` (shape, pos, size, contrast) | ocr.ocv | Have |
| Variation model | `create_variation_model`, `train_variation_model`, `prepare_variation_model`, `compare_variation_model`, `compare_ext_variation_model` | `abs_threshold`, `var_threshold` (mean ± sigma image), mode 'direct' or 'standard' | inspect.template | Missing |
| Texture | `texture_laws`, `gen_gabor`, `cooc_feature_image`, `entropy_image`, `gray_features`, `edges_sub_pix` | Laws kernels, Gabor angles, co-occurrence | inspect.surface | Missing |
| Colour | `trans_from_rgb`, `trans_to_rgb`, `decompose3`, `rgb3_to_gray`, `create_color_trans_lut`, `classify_image_class_gmm`, `linear_trans_color`, `color_trans` | Colour space (HSV, HSI, YUV, CIELab), GMM | colour.* | Missing |
| Classification | `create_class_mlp`, `create_class_svm`, `create_class_gmm`, `create_class_knn`, `classify_class_*` | Feature vectors, hidden units, kernel, rejection | AI Tool 10 | Missing |
| Deep learning | `create_dl_model_detection`, `read_dl_model`, `train_dl_model`, `apply_dl_model`, `evaluate_dl_model`, `set_dl_model_param`, `gen_dl_model_heatmap`, `fit_dl_out_of_distribution` (OOD), `init_dl_continual_learning`, DL pretrained models (see 5.4) | Batch size, learning rate, momentum, `device` (cpu, gpu, ai2 accelerators), `precision` (float32, float16, int8 via `convert_dl_model_specific`), `runtime` ('cpu','gpu','openvino','tensorrt') | AI Tool 10 | Missing (ONNX through OpenCV DNN: ml/) |
| Image / region utilities | `reduce_domain`, `crop_domain`, `rotate_image`, `affine_trans_image`, `polar_trans_image_ext`, `mirror_image`, `zoom_image_size`, `gen_rectangle2`, `union1`, `difference`, `complement`, `connection` | Interpolation modes (constant, bilinear, bicubic, weighted) | base.crop / affine / polar | Partial / Missing |

### 5.4 HALCON deep learning list (2D)

| Method | What | Training data | Our status |
| --- | --- | --- | --- |
| Anomaly detection (global context, local) | Finds anything unlike the good images; output anomaly score image and score; Global Context Anomaly Detection handles logical anomalies (wrong position, wrong count) as well as local defects | 20-100+ good images (MVTec states few good samples [S]) | Missing (ml/experiments) |
| Classification | Whole-image classes (backbones: ResNet, MobileNetV4 from 25.11 [O]), heat map | Labelled images, tens per class | Missing |
| Continual learning classification (25.11 [O]) | Add classes or samples later without full retraining and "without catastrophic forgetting" (MVTec claim) | Few images per class | Missing; strong fit for new products on a line |
| Object detection | Axis-aligned (rectangle1) and oriented (rectangle2) boxes; anchor-free detectors; YOLO v8/v9/v11 import (25.11 [O]); `pretrained_dl_detection_rectangle1` small, medium, large (26.05 [O]) | Boxes | Missing |
| Instance segmentation | Mask per object | Masks | Missing |
| Semantic segmentation | Pixel class map; tiling for large images (26.05 [O]) | Masks | Missing |
| Deep Counting | Counts objects from point annotations, for dense stacks | Points | Missing; relevant to carton and tablet counts |
| Out-of-distribution (OOD) | Flags inputs unlike training data (`fit_dl_out_of_distribution`, works with under 15 samples per class from 25.11 [O]) | Few | Missing; maps to our "NG on doubt" rule |
| Deep OCR | See 5.3 | pretrained | Have (own model) |
| Deep 3D | Not needed (3D) | – | out of scope |
| Augmentation pipeline (26.05 [O]) | Resize, random geometric, crop, HSV, blur, flip, normalise, sample batches | – | Partial (ml/synth.py has synthetic generation, no augmentation pipeline abstraction) |

Performance: MVTec publishes inference-time tables per model and device in the DL documentation [K]; no number is quoted here without opening that page.

### 5.5 HDevelop and HDevelopEVO (screens in words) [O/K]

HDevelop (classic IDE): top menus and toolbar; left, the **Program window** with the HDevelop script (one operator per line,
`read_image(Image, 'x')`); the **Graphics window** shows the current image and regions in colour; **Variable window** (iconic variables:
images and regions as thumbnails; control variables: numbers as text); **Operator window** where an operator name is typed and its parameters are filled in
a dialog with tooltips and links to the reference page; **Assistants** wizards (Image Acquisition Assistant, Calibration Assistant, Matching Assistant, Measure Assistant, Data Code, OCR/OCV Training File Browser, Deep Learning
training helper). The user steps with F5/F6 and sees all variables after each step. HDevelopEVO (newer) has redesigned syntax highlighting, a histogram in the threshold operator, grey value on mouse hover, and
a script converter for old HDevelop code, plus a preview of the HALCON Script Engine (C++ API) [O: release notes 25.11].
Screens: [HDevelop User's Guide](https://www.mvtec.com/products/halcon/documentation) [O, index page] and the [Assistants section of the HDevelop guide](https://www.mvtec.com/doc/halcon/) [K].
Our nearest: Vision Studio's step view with diagnose output. Adopt: **Assistants as wizards that write the job**, and a "variable window" that shows every tool's output after a step.
Effort 3 pw.

### 5.6 Licensing and price (HALCON)

MVTec does not publish fixed prices; costs depend on edition, number of development and runtime licences, and project scope [S: [buy a licence](https://www.mvtec.com/products/halcon/buy-a-halcon-license) page seen in results; search summary]. Progress is a subscription, Steady is a one-time purchase; third-party notes say Progress includes deep learning and Steady may need a deep learning increment [S, third-party]. Runtime licences are separate from development. A 30-day evaluation comes through a regional partner [S]. Dongle or host-id binding may apply [S].
Adopt: our licence model already follows ERPNext only (see LICENSING.md); no change. Note: HALCON's separate "runtime licence per PC" is the model the owner's customers already know.

### 5.7 What Vision_System should adopt from HALCON (concrete)

| # | Adopt | Our id | Status | Effort |
| --- | --- | --- | --- | --- |
| H1 | Trained ranges for code reading (`train_data_code_2d`; limit symbol size, module size, polarity from taught codes) | code.read | Partial (learned.cpp, +-15% pitch) | 0.5 pw |
| H2 | Three effort modes (standard / enhanced / maximum) mapped to our time budget per case size | code.read | Partial (budget auto) | 0.5 pw |
| H3 | `deformation_tolerance` curved QR and DM with a rectification step | code.read | Missing for shrink-wrap; cylinder only | 3 pw |
| H4 | `small_modules_robustness` for modules below 2 px | code.read | Partial (underres.cpp, TOO_SMALL still 19 misses) | 2 pw |
| H5 | Strict quiet zone and `module_grid` modes (aligned / variable) | code.read | Partial | 1 pw |
| H6 | Metrology model (many calipers per shape, robust fit, combined results with camera parameters) | measure.* | Missing | 5 pw |
| H7 | Generic shape model with scale, aniso scale, score visualisation, contour pruning from sample images | locate.geometric | Missing | 5 pw |
| H8 | Variation model (mean and sigma images of good parts, compare with thresholds `abs` and `var`) | inspect.template | Missing | 3 pw |
| H9 | Deep OCR detection plus recognition, compact model for iGPU | ocr.ai | Partial | 3 pw |
| H10 | Anomaly detection with Global Context (logical anomalies) | AI Tool 10 / inspect.surface | Missing | 6 pw |
| H11 | Continual learning classification and OOD (NG on doubt) | AI Tool 10 | Missing | 5 pw |
| H12 | Deep Counting for dense packs | blob.count | Missing | 4 pw |
| H13 | DL augmentation pipeline abstraction (config list of steps) | ml/ training | Partial | 1 pw |
| H14 | GS1 AI table additions (8040-8043, 717) and `strict_ean13_upc_a` | verify.gs1, code.read 1D | Partial (check table in dotnet/CodeReader/Gs1Data*) | 0.2 pw |
| H15 | ISO/IEC 15415:2024 and 29158:2025 revisions | verify.iso15415, iso29158 | Partial (built against the older editions) | 1.5 pw |
| H16 | Calibration object description and residual reporting, telecentric camera model | calib.grid | Partial | 1.5 pw |
| H17 | Edge sub-pixel contours with spurious-contour suppression | measure.* | Missing | 1 pw |
| H18 | Interface parameter persistence for cameras; GenICam chunk data | base.camera | Partial | 1 pw |

---

## 6. MVTec MERLIC

### 6.1 What it is

MERLIC is MVTec's no-code product: an all-in-one environment for image acquisition, processing, analysis, communication interfaces and result display
([MERLIC](https://www.mvtec.com/products/merlic)) [O]. Tools cover rule-based processing and deep learning for classification, measuring and counting, inspection,
OCR, bar and 2D codes, position finding and height-map 3D. A Frontend Designer builds the operator screens. PLC integration works with "recipes" that let a PLC
switch applications. MERLIC on Edge runs applications on Siemens Industrial Edge. A 45-day trial is offered; editions and prices are on a separate page and not given [O].

### 6.2 Versions seen in search snippets [S]

| Version | News | Source |
| --- | --- | --- |
| MERLIC 5.3 | Plug-in for Mitsubishi Electric PLCs; training functions in the Frontend during runtime | [release 5.3](https://www.mvtec.com/products/merlic/product-information/release-notes/530) [S] |
| MERLIC 5.5 | TCP socket plug-in with a text protocol the user defines; "Recognize Color" tool; HDR in "Merge Images"; OPC UA server logs more meta data | [what's new 5.5](https://www.mvtec.com/doc/merlic/5.5/manual/en-us/Content/About_merlic/whats_new.html) [S] |
| MERLIC 5.7 | Frontend for Linux; remote Frontend | [S] |
| MERLIC 5.8 | Better error handling and speed in the Recipes tab; Siemens IE Databus plug-in; server selection dialog in the Frontend | [S] (iconnect007 item) |

### 6.3 Tool list and Tool Flow UI [K]

MERLIC's tool catalogue (names as in the MERLIC manual; verify per version in the [MERLIC manual](https://www.mvtec.com/doc/merlic/)): Acquire (Acquire Image, Read Image, Camera Control), Image processing (Transform Image, Filter Image, Merge Images,
Convert Color Space, Enhance Image, "Image Mapping"), Geometry (Rotate, Crop), Find (Find Shape, Find Model/Pattern, Find Image, Find Blobs, Find Circle, Find Line, "Find Corner"), Measure (Measure Distance, Measure Angle, Measure Circle, Measure Rectangle, Metrology tools, Calibrate Camera and Plate), Inspect (Check Presence, Compare Images (variation), Detect Anomalies (deep learning), "Detect Defects"), Colour (Recognize Color, Check Colour, Colour Segment), Identify (Read Barcode, Read 2D Code, Read Text (OCR), Read Text with Deep OCR, Verify code print quality), Classification (Classify Image deep learning, Count Objects deep learning, Detect Objects), 3D (Height-map tools, "Check Surface"), Communication (Digital IO, TCP socket, OPC UA, PROFINET, EtherNet/IP, Modbus TCP, Siemens S7, Mitsubishi MC, "Beckhoff ADS", Rockwell and Siemens IE Databus), Logic ("Expression", "Compare", "Switch", Loop and Group tools, "Procedure"), and Data (Write to file, save images, "Result Collection").

Layout (Tool Flow UI) [K]: a **Tool Flow** page. The left side lists the tool palette by category. The centre shows the flow as a vertical list of tool "cards" in run order, with the current image
below each card; each card has parameters, an "Image Display" with overlays and a "Results" box. A top bar has "Run once", "Run continuous", "Stop", "Save". The right side has a **Tool Parameters** panel with the selected card's input fields
and bindings (a link icon to bind an input to an earlier tool's output). Other workspaces: **Image Source** (cameras), **Recipes**, **Frontend Designer**, **Communication** (plug-in settings), **Result and Data** (what to log), **Runtime** (monitor). Tutorials with screens are on the
[MERLIC product pages](https://www.mvtec.com/products/merlic) [O, text only] and the manual above [K].

Frontend Designer [O/K]: build an operator view with widgets (image display with chosen overlay, result text, pass or fail lamp, charts, input fields for recipe values and training buttons), with page layouts; the Frontend can be a remote client (Windows and Linux from 5.7 [S]).

Communication plug-ins [S, K]: Siemens IE Databus (5.8), TCP socket with user-defined text protocol (5.5), Mitsubishi (5.3), OPC UA server, PROFINET, EtherNet/IP, Modbus TCP, Digital IO. No Delta DVP driver; Modbus TCP is the route.
Licensing: free 45-day trial; editions by function (Runtime, Development, Deep Learning options); price not published [O].

### 6.4 Adopt from MERLIC

| # | Adopt | Our id | Status | Effort |
| --- | --- | --- | --- | --- |
| M1 | Bind any tool input to an earlier tool output with a link icon (job wiring editor) | Studio job editor | Partial | 1.5 pw |
| M2 | Recipes: PLC picks a product by number; training functions in the operator front end during runtime | Product master + PLC product number | Partial (product master; PLC product switch: PLC-INTERFACE) | 1 pw |
| M3 | Frontend Designer for custom operator pages | Studio / Tool 12 | Missing | 4 pw |
| M4 | Remote frontend (view-only on web, control with API key already decided) | mce.webhmi (11.6) | Partial | per VISION-HYBRID plan |
| M5 | TCP socket plug-in with a user-defined text protocol (serial printers, scanners, other devices) | mce.* | Missing | 1 pw |
| M6 | Siemens, Mitsubishi, Rockwell native PLC plug-ins | PLC-INTERFACE | Missing (Delta DVP Modbus only) | 2 pw each |
| M7 | Tool "Recognize Color" and "Merge Images with HDR" | colour.match, base.camera (multi-exposure) | Missing / Partial (multiframe.cpp) | 2 pw |
| M8 | Edge deployment (Siemens Industrial Edge) | n/a | out of scope | 0 |

---

## 7. MVTec Deep Learning Tool

MVTec Deep Learning Tool is a desktop app to label data, train, evaluate and export deep learning models with no programming; export goes to HALCON and MERLIC
([page](https://www.mvtec.com/products/deep-learning-tool)) [O]. It offers smart labelling, instance-segmentation labelling, oriented rectangles, labelling of datasets for Deep OCR,
integrated metrics and evaluation reports, training classification from scratch and Global Context Anomaly Detection, and optimisation of trained networks. Editions: Free (evaluation and entry), Professional
(permanent licence with resale rights) and OEM (white label) [O]. Price: not published [O].
Screens in words [K]: left image list with labels as tags; centre canvas with labelling tools (brush, polygon, rectangle, oriented rectangle, smart-label "click to select an object"); right panel with class list and properties; a **Training** tab
with dataset split, model type, augmentation, hyper-parameters, a live loss and accuracy chart; an **Evaluation** tab with confusion matrix, precision/recall, heat maps and a "Report" export. Videos and tutorials
are listed on the [Deep Learning Tool page](https://www.mvtec.com/products/deep-learning-tool) [O].
Adopt: one UI to label, train, evaluate and export, with a report; smart labelling (click to segment); Deep OCR labelling of text lines; active "optimise network" (quantise to INT8/FP16 for our iGPU).
Our status: Missing in the UI (ml/ has scripts: train.py, retrain.py, autolabel.py, evaluate.py, diagnose.py). Effort 6 pw (shared with section 2).

---

## 8. Licensing and price overview

| Product | Model | Published price |
| --- | --- | --- |
| VisionPro | Per-PC key or dongle; Dev and Runtime; per tool group | No |
| VisionPro Deep Learning | Licence plus NVIDIA GPU | No |
| In-Sight | Camera hardware includes the software (Vision Suite) | No (reseller quotes) |
| DataMan | Hardware | No |
| OneVision | Subscription | No |
| HALCON | Progress (subscription), Steady (one-time); Dev and Runtime separate; 30-day trial | No ([S]) |
| MERLIC | Editions; 45-day trial | No ([O]) |
| MVTec Deep Learning Tool | Free / Professional / OEM | No ([O]) |

Conclusion: no vendor publishes a price. Do not write prices into any customer document from this study.

---

## 9. Published performance (marketing, not tests)

| Claim | Product | Source | Test described? |
| --- | --- | --- | --- |
| "Faster application performance" in VisionPro 10 | VisionPro | [S] press release | No |
| In-Sight 3800 twice the processing speed of its predecessors | In-Sight | [S] | No |
| DM475V grades up to 20 codes per second | DataMan | [S] | No |
| Deep OCR "up to 50x faster on embedded devices, no accuracy loss" (compact model) | HALCON 25.11 | [S] press release | No test seen |
| OpenVINO and TensorRT acceleration | HALCON | [O] product page | No numbers |

None gives us a number to beat. Our own target (code read >= Cognex) is therefore measured on our images (section 13 of REQUIREMENTS), never against these claims.

---

## 10. UI patterns across these products, to copy

| Pattern | Seen in | What it means for Vision Studio | Status |
| --- | --- | --- | --- |
| Tool list left, image centre, parameters right | QuickBuild, MERLIC, HDevelop | Our Studio layout is the same | Have |
| Overlay on the image for every tool result, with score | all | Have (ResultOverlay.cs) | Have |
| Per-tool time in ms in the result table | VisionPro, MERLIC | Show time per tool | Partial |
| Wizard (Assistant) that writes tool settings | HDevelop Assistants, EasyBuilder 4 steps | Teach flows for each tool | Partial (CodeTeach) |
| Sensitivity slider that hides several parameters | Edge Learning | "Simple" vs "Expert" parameter view | Missing |
| Filmstrip of saved images with Good / Bad tags | Deep Learning Studio, DL Tool | NG review gallery with labels | Partial (NG-REVIEW.md) |
| Named coordinate spaces | VisionPro | Carry space with every result | Missing |
| Recipe / product switch by PLC number | MERLIC | Product master | Partial |
| Results Analysis rule grid | VisionPro | base.logic table editor | Partial |
| Custom operator page designer | Cognex Designer, MERLIC Frontend Designer | Tool 12 | Missing |

---

## 11. What to buy (hardware that raises accuracy or speed, from this study)

| Item | Why | Helps |
| --- | --- | --- |
| Calibrated verifier grade reference (calibrated conformance card) and 45-degree LabelLight style ring | ISO grades are only valid with a defined light and aperture; the DM475V manual says its ISO verification needs the 45-degree light [S] | verify.* acceptance |
| Telecentric lenses for measurement stations | HALCON's calibration models have a telecentric camera type; removes perspective error | measure.*, 0.1 px target |
| GPU (RTX 4060 / A2000 class, as in REQUIREMENTS section 11) | Needed for DL training and for Deep OCR detection at speed; Cognex and MVTec both require NVIDIA GPUs for training | AI Tool 10 |
| Colour camera variant for colour tools | Colour tools cannot run on mono | colour.* |
| Polarised and multi-angle light kits (30/45/90 deg) | Same angles that AIM DPM grading uses | DPM reading and grading |

---

## 12. Cross-product table: function x product

Legend: Y = included, P = partial or option, N = no, ? = not verified. VS = Vision_System today (Have / Partial / Missing).

| Function | VisionPro | In-Sight | DataMan | HALCON | MERLIC | Vision_System today |
| --- | --- | --- | --- | --- | --- | --- |
| GigE / USB camera acquisition | Y | Y (own camera) | Y (own) | Y (many interfaces) | Y | Partial (Hikrobot) |
| Lens distortion and mm calibration | Y (checkerboard, N-point) | Y (calibration functions) | N | Y (full camera model) | Y | Partial (calibration.cpp) |
| Hand-eye calibration | Y | P | N | Y | P | Missing |
| Correlation find | Y (CNLSearch) | Y (PatFind) | N | Y (NCC) | Y | Have |
| Geometric find (scale, angle) | Y (PatMax) | Y (PatMax) | N | Y (shape, scaled, aniso) | Y (Find Shape) | Missing |
| Deformable find | Y (PatFlex) | P | N | Y (planar, local) | ? | Missing |
| Caliper, line, circle, metrology | Y | Y | N | Y (metrology model) | Y | Missing |
| Blob analysis | Y | Y | N | Y | Y | Missing (internal) |
| 1D reading | Y | Y | Y | Y | Y | Have |
| 2D reading (QR, DM, Aztec, PDF417) | Y | Y | Y | Y | Y | Have (92.2 % on real) |
| DPM reading | Y | Y | Y | Y | Y | Partial (untested on hardware) |
| Curved / deformed code tolerance | P | P | P | Y (QR 25.11, DM 26.05) | Y (via HALCON) | Missing (cylinder only) |
| ISO 15415 / 15416 / 29158 grading | Y | Y (ID verify) | Y (DM475V, 8072V) | Y | Y | Have (not calibrated) |
| GS1 data validation | Y | Y | Y | Y (AI table) | Y | Have |
| Classic OCR | Y (OCRMax) | Y (OCRMax) | Y | Y | Y | Have |
| Deep OCR | Y (Blue Read) | Y (OCR AI) | Y | Y (Deep OCR) | Y | Have (own CRNN) |
| OCV with print quality | Y (OCVMax) | Y | N | Y | Y | Have (false rejects 31 %) |
| Golden-template and variation | Y (PatInspect) | Y | N | Y (variation model) | Y | Missing |
| Texture and surface | P (Red) | Y (Edge Learning) | N | Y | Y | Missing |
| Colour tools | Y | Y | N | Y | Y | Missing |
| Anomaly detection | Y (Red) | Y (D900, Edge Learning) | N | Y (Global Context) | Y | Missing |
| Classification | Y (Green) | Y | N | Y (and continual) | Y | Missing |
| Object detection / locate | Y (Blue Locate) | Y | N | Y (rectangle1/2, YOLO import) | Y | Missing |
| Segmentation | Y (Red supervised) | P | N | Y | Y | Missing |
| Deep counting | P | P | N | Y | Y | Missing |
| OOD | P | N | N | Y | ? | Missing |
| Few-sample / edge learning | Y (Focused modes) | Y (Edge Learning) | N | P (continual) | P | Partial (learned.cpp) |
| Labelling and training tool | Y (Deep Learning Studio) | Y | N | Y (DL Tool) | Y (DL Tool export) | Missing UI (scripts only) |
| Scripting | Y (.NET) | Y (spreadsheet formulas) | P | Y (HDevelop, many languages) | P (expressions) | Missing |
| PLC: Modbus TCP | Y | Y | Y | via third party | Y | Have (Delta DVP) |
| PLC: EtherNet/IP, PROFINET | Y | Y | Y | N | Y | Missing |
| OPC UA | Y | Y | Y | N | Y | Missing |
| Operator HMI designer | Y (Designer) | Y (Vision Suite) | N | N | Y (Frontend Designer) | Partial (fixed pages) |
| Cloud fleet view | Y (OneVision) | Y | Y | N | P | Partial (plant_server) |

---

## 13. Ranked adoption list from this chapter

Top items by value to the line (pharma aggregation and coding):

| Rank | Item | Why | Our id | Effort |
| --- | --- | --- | --- | --- |
| 1 | Curved and deformed QR and DM rectification (H3) | Shrink-wrap QR is 60.3 % | code.read | 3 pw |
| 2 | Sub-2 px module robustness (H4) | TOO_SMALL misses | code.read | 2 pw |
| 3 | Geometric find with scale and multi-instance (H7) | PatMax / shape model is the base of fixture and every measure | locate.geometric / multi | 5 pw |
| 4 | Metrology and caliper family (H6) | Tool 2 is empty; requirement 0.1 px | measure.* | 7 pw |
| 5 | Variation model and PatInspect-style compare (H8) | Print and label defects with false reject <= 0.5 % | inspect.template | 3 pw |
| 6 | Anomaly detection (global context) and OOD (H10, H11) | NG on doubt | AI Tool 10 | 11 pw |
| 7 | Labelling and training UI, filmstrip, report | Without this the customer cannot teach models | AI Tool 10 | 6 pw |
| 8 | OCV false-reject cut (true laser passes 69 %) | Cognex OCVMax score design: Match, Contrast, Quality with Allow shift | ocr.ocv | 2 pw |
| 9 | ISO 2024 / 2025 grading editions and calibrated verifier set (H15) | Verifier acceptance | verify.* | 1.5 pw |
| 10 | Named coordinate spaces (1.4.4) and tool-group wiring (M1) | Prevents unit mistakes | base / Studio | 2.5 pw |
| 11 | Colour tools | Cap and foil colour checks | colour.* | 4 pw |
| 12 | PLC plug-ins for Siemens, Mitsubishi, Rockwell and OPC UA | Sales outside Delta lines | mce.* | 8 pw |

---

## 14. Cognex pages checked in the built-in browser (2026-10-30)

cognex.com refused the web fetcher (HTTP 429) but opened in the app's built-in browser. These four
pages were read in full; they confirm or correct the [S] and [K] items above.

| Page | What it says | Effect on this chapter |
| --- | --- | --- |
| [VisionPro](https://www.cognex.com/en/products/machine-vision-software/visionpro-software) | QuickBuild drag-and-drop tool blocks plus scripting; .NET, C# and C++ libraries; rule-based groups: location and pattern matching, edge and blob, measurement, **bead inspection**, identification (1D/2D and text); embedded AI segmentation and classification trained "using as few as 5 to 10 images"; any camera type incl. line scan; CC24 I/O card with encoder input and EtherNet/IP, PROFINET, SLMP, TCP/IP | Confirms sections 1 and 2. Bead inspection is a named VisionPro group (our 8.5) |
| [DataMan 260](https://www.cognex.com/en/products/fixed-mount-barcode-scanners/dataman-260) | Core technologies: 1DMax with Hotbars II for damaged 1D; 2DMax with "patent-pending" PowerGrid for severely damaged or poorly marked 2D; 0.4 MP / 60 fps and 1.2 MP / 45 fps | Confirms section 4; the sensors are far smaller than ours: Cognex wins by algorithms and light, not pixels |
| [DataMan 290](https://www.cognex.com/en/products/fixed-mount-barcode-scanners/dataman-290) | AI in three stages: **Find** (AI positioning feedback and automated tuning), **Enhance** (AI-enhanced decoding of damaged or low-quality codes), **Read**; multi-code reading; multi-reader sync; curved and reflective DPM; 1.6 MP, 45 fps; integrated light with 4 red LEDs and a diffuse white light, **polarised** and direct/diffuse front covers | The same find / enhance / read split as our locator, enhancer and decoders. Polarised covers are standard on Cognex readers: supports chapter 05's polariser item |
| [VisionPro Deep Learning](https://www.cognex.com/en/products/machine-vision-software/visionpro-deep-learning) | Tools Blue Locate, Red Analyze, Green Classify, Blue Read; Few Sample Mode; Robust Mode (camera tilt, lighting and white-balance shifts); **Outlier Score** comparing each image with the training set to flag drift; multi-class segmentation; AI-assisted labelling ("tag a few examples ... auto-label the rest"); tool chaining; Lab features | Confirms section 2. The Outlier Score is the drift monitor of our AS-04 / DL drift item |

The Edge Learning page has moved (404 at the old address) and was not found again.

## Not opened / limits

- cognex.com returned HTTP 429 for the VisionPro, In-Sight 3800 and DM475V pages and the VisionPro 10 press release on 2026-10-30. Everything from those pages is [S] or [K]. Re-open them before using any detail in a customer document.
- docs.cognex.com tool-by-tool parameter pages (CogPMAlignTool, CogCaliperTool, CogIDTool, OCRMax, OCVMax, CogColorMatchTool, calibration tools): not opened. Parameter lists are from known documentation [K] and may differ per version.
- In-Sight Explorer, EasyBuilder and Edge Learning manuals, Cognex Designer, OneVision pages: not opened. [K].
- DataMan 8072V Cognex datasheet: not opened; only a reseller listing [S].
- HALCON Reference Manual operator pages (https://www.mvtec.com/doc/halcon/): not opened; operator names and parameter lists in 5.3 are from known documentation [K] except those marked [O] from release notes 25.11 and 26.05. HALCON 24.11 and 25.05 release notes not opened.
- MERLIC manual (tool list per version) and MERLIC release notes: not opened; version news is from search snippets [S]. The tool list in 6.3 is a reconstruction [K] and must be checked against the MERLIC manual.
- MERLIC and HALCON editions and prices: no public price found. Third-party comparison sites were seen only as snippets.
- No screenshots were viewed; UI descriptions are written from the vendors' documentation as known and the pages linked. The links to the manuals point to the index pages where the screenshots are found.
- Patent numbers: the PatMax family link is given from earlier project research [K]; see [PATENTS.md](../../PATENTS.md) and [RESEARCH-COGNEX-PATENTS.md](../../RESEARCH-COGNEX-PATENTS.md) for status and expiry. Owner's statement of 2026-10-30 (legal clearance).
- Nothing was built or run. Effort numbers are estimates in person-weeks for one developer with an AI assistant.
