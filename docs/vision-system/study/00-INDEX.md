# Vision System study: software, patents, documents, tools, hardware and timeline

Owner, 2026-10-30: "i discussed with lawyer and we can use all patents and process in our library.
Start working on detailed study report of all softwares, all patents, all documents, tools, screen
shots. I need almost 100% accuracy in all tools with required timeline. I can upgrade hardware if
required. Prepare list of tools to be developed with all options, layers, corrections and others.
Nothing must left in this study."

## In short

- **Where we are.** Codes: 1,723 of 1,868 read (92.2 %) on the owner's sample images with 0 wrong
  reads (inferred from counts; the truth files hold counts, not texts). The misses are low contrast under shrink wrap and on dark Data Matrix caps, blur, codes
  below about 1.8 px per module, and covered finders: mostly the image, not the decoders. Text:
  real laser lines 100 % (one product), synthetic CIJ 72.9 %; OCV never accepted a wrong text but
  rejects too many good ones. Read code, Read text and Find the part are built; Measure, Blob,
  Inspect, Colour and the AI tools are not.
- **What the leaders do.** Cognex, HALCON, Keyence and the rest win on light, module-scaled
  correction, per-job tuning and many fall-back paths, not on bigger sensors: the DataMan 260 reads
  with a 0.4-1.2 MP sensor. No vendor publishes a measured read rate on pharma cases, so our own
  bench is the proof (chapters 01a, 01b, 01c).
- **The tools.** About 190 tools and sub-tools across chapters 04a-04d, each with its layers,
  options, corrections, outputs, safety rules, accuracy target, test and build plan. About 55 are
  new against TOOL-SPECIFICATIONS.md, among them HDR, polarisation, photometric stereo, cylinder
  unwrap, deformable matching, sub-pixel metrology, rMQR, composite codes, OCV per-letter freedom,
  a second text reader, blister and foil, logical anomaly and the AI acceptance gate.
- **Hardware.** Light and a faster line PC first, cameras last: shroud, filter and polariser; strobe
  with overdrive; dark-field ring for caps; dome or coaxial for film; a T2 PC. About ₹3.5-8 lakh
  per station (estimates), chapter 05.
- **Effort and time.** About 430 person-weeks (one developer-week each) in all. Reading and text to
  target (codes ≥ 99.9 % with 0 wrong, OCR ≥ 99.5 %, OCV ≤ 0.5 % false reject, locator 0.05 px) is
  waves 0 and 1, about 84 pw: from 2026-11-02 about 3.5 months with six developers or 7 months with
  three, gated by line time, real images and the light and PC purchases. Everything in section 13 of
  the requirements: about 1.4 years with six developers, 2.8 years with three (chapter 06).
- **Decisions for the owner** are in chapter 06 section 5: hardware trigger on moving lines, untaught
  dot characters, the scope of the patent clearance with AS-08 and DL-07 wording, the false-accept
  budget, team size, the Sun Teknovation machine families, and DL-02's "not sure may pass" option.
- **Engine facts found** while writing (chapter 06 section 6): the QR quiet zone is graded on 2
  modules instead of 4, rMQR is dropped, the locator test allows 1 px and 0.5°, and calibration
  accepts 1 px RMS.

## How sure the sources are

Many vendor and standards sites blocked automated reading (cognex.com, iso.org, gs1.org and others).
Each chapter marks every fact: opened (no mark or [O]), search snippet only [S], known but not
re-opened [K], carried from earlier repo research [P]. Four key Cognex pages were later read in full
in the built-in browser (01a section 14). Numbers that decide a grade or a legal rule should be
checked against the bought standard or the Gazette text before they go into a customer document.
Videos were studied from titles and descriptions only.

## Chapters

| # | Chapter | What it holds |
| --- | --- | --- |
| 01a | [Software: Cognex and MVTec](01a-SOFTWARE-COGNEX-MVTEC.md) | VisionPro, ViDi, In-Sight, DataMan, Designer, OneVision; HALCON, MERLIC, Deep Learning Tool: every tool, its options, the screens, AI, PLC, licensing, what we adopt |
| 01b | [Software: everyone else](01b-SOFTWARE-OTHERS.md) | Teledyne DALSA, Omron, NI, Keyence, Zebra (Aurora, MIL), Basler, SICK, Datalogic, Hikrobot, Euresys, Stemmer, and the open-source libraries and SDKs |
| 01c | [Sun Teknovation](01c-SOFTWARE-SUNTEKNOVATION.md) | The Gandhinagar pharma vision and track-and-trace maker: website, brochure, YouTube videos, every product and how it works, compared with ours (owner's request, 2026-10-30) |
| 02 | [Patents](02-PATENTS.md) | Every patented method by tool family, with its steps and parameters and the tool that adopts it |
| 03 | [Standards and documents](03-STANDARDS-AND-DOCUMENTS.md) | ISO / GS1 / pharma serialisation / India rules / 21 CFR Part 11 / camera and PLC standards, key papers, what to buy |
| 04a | [Tools: base, locate, measure, calibrate, blob](04a-TOOLS-BASE-GEOMETRY.md) | Tools 0-4 and the shared correction layer |
| 04b | [Tools: code reading and verification](04b-TOOLS-CODES.md) | Tools 5-6, every symbology, DPM, aggregation |
| 04c | [Tools: text](04c-TOOLS-TEXT.md) | Tool 7: OCR, OCV, print quality, formats |
| 04d | [Tools: inspect, colour, AI, platform](04d-TOOLS-INSPECT-AI-PLATFORM.md) | Tools 8-12, Teach, auto-find / learn / tune, logic, multi-camera, PLC, audit |
| 05 | [Hardware](05-HARDWARE.md) | Cameras, lenses, lights, triggers and PCs per station; purchase list by accuracy gain |
| 06 | [Accuracy targets and timeline](06-ACCURACY-AND-TIMELINE.md) | Target per tool, today's number, the gap, the plan in weeks, what the owner provides |

Earlier studies this one builds on: [INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md) (survey per tool,
ranked missing methods), [TOOL-SPECIFICATIONS.md](../TOOL-SPECIFICATIONS.md),
[ROADMAP.md](../ROADMAP.md), [PATENTS.md](../../PATENTS.md),
[RESEARCH-COGNEX-PATENTS.md](../../RESEARCH-COGNEX-PATENTS.md),
[RESEARCH-NEXT-GENERATION.md](../../RESEARCH-NEXT-GENERATION.md),
[AI-DEEP-LEARNING.md](../AI-DEEP-LEARNING.md).

## Owner decisions this study works under

| Decision | Effect on the study |
| --- | --- |
| Patents: "we can use all patents and process in our library" (owner, after advice from his lawyer, 2026-10-30) | Patented methods are listed as methods to adopt, with the patent cited and its status as Google Patents shows it. This records the owner's statement; the study gives no legal opinion and makes no freedom-to-operate claim. The scope (territories, export) is for the owner to confirm, and requirement AS-08 ("used only after a design-around or a licence") needs rewording to match: a follow-up, not done here. |
| Copyright is not covered by that decision | No vendor code, model, file, manual text or screenshot is copied into the repository. Each vendor screen is described in words with a link to the page where it can be seen. |
| "I can upgrade hardware if required" | Chapter 05 says what to buy where it raises accuracy or speed, ordered by gain per rupee. |
| "Almost 100 % accuracy in all tools" | Chapter 06 sets a number per tool, with the image set and the pass rule that proves it. Line safety stays first: when a tool is in doubt the part is NG. |

## The layer model

Every tool in chapters 04a-04d is described in the same seven layers, so a method from one tool can be
reused by another.

| Layer | What happens | Examples |
| --- | --- | --- |
| L0 Acquire | Camera, lens, light, trigger, exposure | Exposure search, bracketed exposures, polariser, strobe, action commands |
| L1 Correct | Remove what the optics, light and print do to the image | Dark and flat field, lens distortion, perspective, real-size scale, illumination normalisation, deblur with the measured blur, glare, polarity, mirror, rotation, curvature unwrap, print growth, dot gaps, drift |
| L2 Find | Where the part, the codes, the text and the features are | Pattern match, code locator, text-line finder, fixture |
| L3 Read or measure | The tool's own work | Decode, OCR, gauge, blob, compare, classify |
| L4 Verify and decide | Is the result trustworthy, and OK or NG | Check digits, format rules, confidence, two readers agree, limits; NG on doubt |
| L5 Learn and tune | Set the tool up and keep it right | Teach, auto-find, auto-learn, auto-tune, auto-tolerance, drift |
| L6 Output and audit | Tell the line and keep the record | PLC bits and registers, overlays, NG images, logs, reports, audit trail |
