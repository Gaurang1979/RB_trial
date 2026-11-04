# Roadmap: how the final Vision Studio is built

2026-10-30. The order in which the remaining work is built, with what "done" means for each
step. It follows the owner's goal in [requirements section 16](../REQUIREMENTS-VISION-SYSTEM.md)
(automatic setup, learning from the best products, near-100% accuracy, line safety first) and the
product phases P1 to P5 of requirements section 15. When this file and the requirements disagree,
the requirements win. Update the status columns when a step's pull request merges.

The ranked list of **which methods to build and test per tool** to reach near-100 % (all 13 tools,
vendor + patented, each with its acceptance test and the ViDi-style AI plan) is in
[TOOL-METHODS-BACKLOG.md](TOOL-METHODS-BACKLOG.md); development picks from its §15 list one at a
time.

## 1. In plain words

**What the product is.** One Windows application, **Vision Studio**, where the line is run, taught
and set up, and one **runtime service** underneath it that does the inspection and keeps running
even if the application is closed. Both use the same **Vision Package** (the reading engine,
cameras, AI and licence), which the aggregation software also loads. A browser page shows the live
line and reports but cannot change anything. Owner decisions of 2026-10-30.

**How it is built.** The reading and checking (codes, text, finding the part) are written in our
own C++ engine for speed. Vision Studio is written in C# (.NET) with Avalonia, so it looks the
same on the line PC and on the development Mac. AI models are trained in Python and run on the
line PC through ONNX. Every change is tested once by the session that made it, and every change to
the reading code is run on all of the owner's sample images and compared with the recorded result
before it (`tools/bench/batch_check.py` and `truth/batch-baseline.csv`), so a change that reads
less is not merged.

**Where we are.** Reading codes and reading or checking printed text work today, at line speed,
with automatic help for the setup. The new Windows application shows the Run screen and sign-in;
its setup screens, the drag-and-drop designer and most non-reading tools (measuring, defects,
colour, AI defect tools) are designed but not built yet. The table in section 2 says exactly what
is built for each thing the owner asked for.

## 2. The owner's list: built, partly built, not built

Checked against the code on main on 2026-10-30.

| Capability | State | What exists today | What is missing |
| --- | --- | --- | --- |
| Auto find of fields and their type | Partly built | In Teach (browser Vision Studio, `dotnet/VisionStudio`), the user drags a box and presses **Auto-find**: for codes it reads every code of any type and locks the tool to the type and count found; for text it finds the text block, sizes the letters, and proposes a format per line (date, expiry, lot, time, month names), the expiry rule and the date check; for Find the part it learns the pattern. | Auto-find on the whole image without drawing a box first, offered as suggestions ("Read this code?", "Check this text?") (AS-01); suggestions for holes, edges, labels and colour (needs those tools); the same in the new Windows application. |
| Auto learning | Partly built | The code reader learns per product while the line runs (sizes, encodings, which steps help) and keeps it per product. Check text learns print-quality limits (ink, contrast, spacing) from 10 good parts. The code-finding AI model can be retrained from the cases the line missed, kept only if it is no worse on the golden images, and used only after a named approval (`ml/retrain.py`). | One-tap auto-learn for every tool type (AS-02); auto-tolerance from 10–30 good parts for every tool; AI training screens for operators. |
| Auto tuning | Partly built | Camera exposure and gain are searched automatically (Calibrator, `dotnet/CodeReader/CalibrationSearch.cs`). A tuning agent tries reader settings while the line runs and keeps what reads most codes in the least time (`dotnet/CodeReader/TuningAgent.cs`). Auto-find for text tries several letter heights and keeps the best. | Auto-tune of every tool on saved good and bad images for the widest margin (AS-03); automatic search of AI training settings (DL-18). Today's tuning covers the code reader only. |
| Drag-and-drop tools in the inspection window | Not built | Only drawing the tool's area by dragging on the image in Teach. | Toolbox dragged onto the image, the area snapping to what was found, and the flow view with wired blocks (AS-05, UI-UX-SPEC.md 4.7 and 5), in the new Windows application. |
| Every test and tool result recorded | Built for reading | Each sample image's reads and time are recorded and every reading change is compared with them; Vision Studio's Test and Batch pages run a job over saved images; the line keeps per-part results and NG images. | The same recorded comparison for every new tool as it is built (AS-06), and per-release accuracy reports (AS-11). |
| AI tools | Partly built | AI models that find codes, sharpen faint codes and read text (`ml/`, ONNX) run in the engine. Anomaly detection has only been tried as an experiment (`ml/experiments/`). | Deep-learning inspection tools (anomaly, classification, detection, segmentation, AI locator), training screens and the drift monitor (requirements section 5, DL-01 to DL-26). |
| Methods of Cognex, HALCON and others | Started, for code reading | Cognex and HALCON code-reading methods and Cognex patents studied and several adopted in our own form ([RESEARCH-COGNEX-HALCON.md](../RESEARCH-COGNEX-HALCON.md), [RESEARCH-COGNEX-PATENTS.md](../RESEARCH-COGNEX-PATENTS.md), [PATENTS.md](../PATENTS.md)); Cognex ViDi and SuaKIT studied for AI ([RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md](RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md)); grading follows Cognex's verification white paper. | Studies of Teledyne DALSA, Omron, National Instruments, Keyence and Zebra (Matrox), and of every tool family other than code reading (AS-07); a side-by-side run against a bought Cognex licence (AS-10). |

**Accuracy today** (recorded result on main, line time targets, on the development Mac): 1,723 of
1,868 visible codes read (92%). By batch: 45 QR 270/270, 24 DM 48/48, 48 QR 843/857,
24 QR 372/384, 48 DM 61/95, and the shrink-wrapped excise labels 129/214. With more time per case
the reader reaches 99.7% ([RESEARCH-NEXT-GENERATION.md](../RESEARCH-NEXT-GENERATION.md) section 1);
the hardest images were taken with the older camera and are to be re-shot. "Almost 100%" is reached
when every batch meets AS-11.

## 3. The steps, in order

Each step ends in pull requests that are tested once and merged by the owner. Steps 1 and 2 run
side by side; step 7 runs alongside all of them.

| Step | What is built | Done means | Phase | State |
| --- | --- | --- | --- | --- |
| 1. Reading accuracy at line speed | Close the gaps in the accuracy table above: 48-code Data Matrix cases, faint and shrink-wrapped labels (re-shoot on the current camera first, [RESEARCH-NEXT-GENERATION.md](../RESEARCH-NEXT-GENERATION.md) R0, R13, R14), single codes lost to time; OCR and OCV on the owner's real text samples. | Every code batch at least 99.9% of visible codes, no wrong read; text at least 99.5% of characters; recorded on the owner's images (AS-11). | P2 | In progress |
| 2. Vision Studio Windows application | Runtime service with the line's work moved into it (Hybrid 1), the Avalonia application (Hybrid 2), its setup screens: Change product, Teach, Jobs, Batch, Test, Products, Tuning, Calibrate, AI models, Users, Settings (Hybrid 3), the view-only browser dashboard (Hybrid 4), one installer (Hybrid 5). | Everything the browser Vision Studio does today is done in the Windows application; the old browser setup pages are retired; a week on a Windows line PC without a stop. | P1, P2 | Runtime contract and Run screen built; setup screens not built |
| 3. Automatic setup | Auto-find on the whole image with field types (AS-01), one-tap auto-learn for every tool (AS-02), auto-tune on good and bad images (AS-03), auto-tolerance from good parts for every tool, auto-light check. | AS-01 to AS-03 met on the owner's images; a new label, date and code job made by a supervisor in 15 minutes (requirements section 13). | P2, P3 | Partly built for codes and text |
| 4. Drag-and-drop job designer | Toolbox, drop onto the image with snapping, flow view with wiring, properties with essential and Advanced settings, live re-run with times, image-set panel, templates, undo and versions (AS-05, requirements 6.3). | An engineer builds a two-camera job only by dragging and tapping; ten shop-floor users pass the usability test of UI-UX-SPEC.md section 8. | P1, P2 | Not built |
| 5. The other tools | Measure, Calibrate (real size), Blob and Count, Inspect (defects, labels, caps), Colour, each with auto-learn and auto-tune from its first version (TOOL-SPECIFICATIONS.md). | Each tool meets its targets in requirements section 13 on recorded image sets, with no bad part passing. | P1, P4 | Not built (Find the part is built) |
| 6. AI inspection tools | Anomaly detection, classification, detection, segmentation, AI locator, training screens with auto-label, the drift monitor (requirements section 5). | DL-01 to DL-24 met; beats the Cognex result on three customer defect sets. | P3 | Not built (experiments only) |
| 7. Learning from the best | A written study per tool family of Cognex, MVTec HALCON, Teledyne DALSA, Omron, National Instruments and others, from their published material and patents (AS-07, AS-08); a bought Cognex licence and the 5,000-image benchmark library (AS-10); a watch on new public AI methods (AS-09). | Each tool family has its study and a recorded side-by-side result before its release. | All | Done for code reading only |
| Later | The 14 other languages, the mobile app and the plant server (deferred by the owner, 2026-10-30: English only for now); multi-camera enterprise output; Linux and Jetson. | As in requirements section 15. | P4, P5 | Deferred |

## 4. Rules that hold at every step

- **Line safety first.** A step never lets a bad part pass: when the camera, the PLC link, the job
  or a model is in doubt, the result is NG and the line is told (requirements section 12, AS-12).
- **Learning is guarded.** Nothing that decides OK or NG changes while a job runs; a new limit or
  model is used only after it is no worse on the golden images and a named person approves it
  (AS-04).
- **Our own form.** Ideas come from published methods; no other company's code or models are
  copied, and patents in force are designed around or licensed (AS-08).
- **Recorded and compared.** Every change to a tool is run on the recorded image sets and compared
  with the result before it (AS-06; AGENTS.md rules 10 and 11).
