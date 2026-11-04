# Tools: text (Tool 7, OCR, OCV, print quality, formats)

Study chapter 04c, 2026-10-30. Part of the [study index](00-INDEX.md). Format: the tool catalogue
format shared by chapters 04a-04d; layers L0-L6 as in the index.

This chapter lists every text tool to develop, with its options, layers and corrections, and sets
the targets. It builds on, and does not repeat:
[TOOL-SPECIFICATIONS.md Tool 7](../TOOL-SPECIFICATIONS.md) (what each tool does today),
[INDUSTRY-METHODS.md section 14](../INDUSTRY-METHODS.md) (survey and ranked gaps),
[REQUIREMENTS-VISION-SYSTEM.md](../../REQUIREMENTS-VISION-SYSTEM.md) sections 13 and 17.2,
[TEST-REPORT-2026-10-30.md](../../TEST-REPORT-2026-10-30.md) section 3, and the change notes named in
each section.

**Open work this chapter does not redesign.** Draft PR
[#142](https://github.com/Gaurang1979/Vision_System/pull/142) "OCR/OCV real samples read at 100 %"
(session ocr-real-samples-100) holds the owner's request of 2026-10-30: "why ocv ocr results are only
92-93%? i need to read all failed samples stored in mac at 100% readable." It runs every real text
sample on the Mac, writes a per-image cause table (`docs/OCR-TEST-RESULTS.md`) and fixes the causes in
the line finder, preprocessing and model. Everything below that touches the same code (line finder,
model, real-sample truth) starts from that PR's result, after it merges.

**Patents.** Owner's statement of 2026-10-30 (legal clearance), after advice from his lawyer: all
patents and processes may be used. This chapter gives no legal opinion. Patented methods are
written below as methods to adopt, with status and expiry kept as information. Copyright is separate: no vendor font file, model, manual text or screenshot goes into the
repository; printer-maker fonts are drawn from the owner's own printers or bought (section 7.3.2).

---

## 1. Summary table

Status words: **Have** (built and measured), **Partial** (built, not to target or not measured on real
print), **Missing**. **NEW** = not in TOOL-SPECIFICATIONS.md today. Effort in person-weeks (pw), one
developer with an AI assistant. Phases as in [ROADMAP.md](../ROADMAP.md) (P2 Identify holds OCR/OCV).
Speed on T1 (i5/i7, OpenVINO on the iGPU) / T2 (i7 plus RTX 4060 class), per typical image.

| Id | Name (operator words) | Layers | Status today | Accuracy target | Speed target T1 / T2 | Effort | Phase | Depends on |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 7.0 | Text finder and straightener ("Find text") | L1-L2 | Partial | 100 % of text lines found, 0 lines cut short unseen | 8 / 4 ms per region | 3 | P2 | 0.x filters |
| 7.1 | AI OCR ("Read text") | L1-L4 | Partial | >= 99.5 % characters with no training; >= 99.95 % after product teach | 25 / 8 ms per 3-line block | 4 | P2 | 7.0, 7.10 |
| 7.1.1 | Print-type models **NEW** | L3, L5 | Missing (one model) | each print type >= 99.5 % characters | same as 7.1 | 3 | P2 | 7.10 |
| 7.1.2 | Second recogniser and agreement **NEW** | L3-L4 | Missing (two readers exist, no rule) | 0 wrong texts accepted by Read text | +6 / +2 ms | 4 | P2 | 7.1, 7.2 |
| 7.2 | Classical OCR from a taught font ("Read text") | L3, L5 | Have | >= 99.9 % characters on the taught font | 5 ms per 20 characters | 2 | P2 | 7.0 |
| 7.2.1 | OCR-A, OCR-B and SEMI fonts **NEW** | L3 | Missing | >= 99.95 % characters | 5 ms | 1 | P3 | 7.2 |
| 7.3 | OCV ("Check text") | L3-L4 | Partial | 0 wrong texts pass; false reject <= 0.5 % | 10 / 4 ms per 20 characters | 2 | P2 | 7.1 or 7.2 |
| 7.3.1 | OCV per-character freedom **NEW** | L1, L3 | Missing (shift only) | true-text pass >= 99.5 % on laser, TTO, CIJ | +4 ms | 3 | P2 | 7.3 |
| 7.3.2 | Printer-maker fonts as reference **NEW** | L5 | Missing (system fonts only) | reference fits real print within learned limits | at save | 2 | P2 | 7.3, owner's printers |
| 7.3.3 | Verification score (expected vs best rival) **NEW** | L4 | Missing | as 7.3 | +1 ms | 2 | P2 | 7.3 |
| 7.3.4 | Product teach (fast adaptation) **NEW** | L5 | Missing | false reject <= 0.5 % from 20 good prints | 60 s training on T2 | 2 | P2 | 7.10 |
| 7.4 | Format validation ("Check date / lot") | L4 | Have | 100 % of rule cases right on the rule test set | 0.1 ms | 1 | P2 | 7.1 / 7.2 |
| 7.4.1 | Dates and month names in Indian languages | L4 | Partial (6 Latin languages) | 100 % on the date test set | 0.1 ms | 1.5 | P2 | 7.4 |
| 7.4.2 | Lot / batch | L4 | Have | 100 % | 0.1 ms | 0.5 | P2 | 7.4 |
| 7.4.3 | MRP under Legal Metrology **NEW** | L4 | Missing (generic pattern only) | 100 % | 0.1 ms | 1 | P2 | 7.4 |
| 7.4.4 | Expiry vs production, shelf life, today | L4 | Have | 100 % | 0.1 ms | 0.5 | P2 | 7.4 |
| 7.4.5 | Serials and counters **NEW** | L4 | Missing | 100 % | 0.1 ms | 1 | P3 | 7.4, PLC |
| 7.4.6 | Block layout prior (multi-line) **NEW** | L2-L4 | Partial (PR #160) | cuts right on 100 % of good blocks | +0.5 ms | 1.5 | P2 | 7.0, 7.4 |
| 7.5.1 | CIJ / dot-matrix mode | L1-L3 | Partial | >= 99.5 % characters real CIJ | as 7.1 | 3 | P2 | 7.10 real CIJ |
| 7.5.2 | Laser mode (carton, label, PET, foil) | L0-L3 | Partial | >= 99.5 % | as 7.1 | 1.5 | P2 | 7.0 |
| 7.5.3 | Thermal transfer (TTO) mode | L1-L3 | Partial | >= 99.5 % | as 7.1 | 0.5 | P2 | 7.10 |
| 7.5.4 | Thermal inkjet (TIJ) mode | L1-L3 | Partial | >= 99.5 % | as 7.1 | 1 | P2 | 7.10 |
| 7.5.5 | Embossed / debossed mode | L0-L3 | Partial | >= 99.5 % with 4-direction light | 30 / 12 ms (4 images) | 3 | P3 | lights, 7.10 |
| 7.5.6 | Ink on foil (blister lidding) **NEW** | L0-L1 | Missing | >= 99.5 % | as 7.1 | 1.5 | P3 | coaxial light |
| 7.6 | Text print quality grade **NEW** | L4, L6 | Partial (own metrics) | grade repeatable within 1 grade step on 10 re-shots | 2 ms | 2 | P3 | 7.3 |
| 7.7 | Curved text on caps (polar unwrap) **NEW** | L1-L2 | Missing (synthetic bend only) | >= 99.5 % on cap tops and sides | +3 ms | 2.5 | P3 | 0.11 polar, 7.0 |
| 7.8 | Indian scripts (Hindi and others) **NEW** as tool | L3 | Missing | >= 98 % (DL-13), 99.5 % on dates and numbers | as 7.1 | 8 + 2 per script | P4 | 7.10 |
| 7.9 | Corrections (shared L1 for text) | L1 | Partial | see section 7.9 | in 7.0 | in 7.0 | P2 | 0.x |
| 7.10 | Model training pipeline | L5 | Partial | gate: no print type worse, safety sets 0 accepted | offline | 6 | P2 | owner images |
| 7.11 | Teach UX for text | L5 | Partial | a text tool set up in <= 3 minutes, no expert step | - | 2 | P2 | 7.1-7.4 |
| 7.12 | OCV false-reject fix (plan) | L3-L5 | Partial | laser true text >= 99.5 % (69 % today) | - | (in 7.3.x) | P2 | 7.3.1-7.3.4 |

Total about 52 pw for P2-P3 (the build plan, section 9; the rows above add up differently because
some rows share work), plus 8 pw for the first Indian script (Devanagari) and 2 pw for each further
script.

---

## 2. Where we stand (measured, 2026-10-30)

| Measure | Value | Source |
| --- | --- | --- |
| AI reader, synthetic lines, characters right | laser 92.6 %, TTO 98.0 %, CIJ dot 72.9 %, embossed 89.4 % | [ocr-ai-reader note](../../changes/2026-10-30-claude-ocr-ai-reader.md) |
| AI reader, real laser lines (Harpic, 389 held-out lines) | 100 % characters, 100 % lines, check passes 100 % | same |
| OCV, one character changed / look-alike accepted | 0 / 400 and 0 / 396 (laser), 0 on every print type | same; TEST-REPORT section 3 |
| OCV, true text passes (synthetic lines) | laser 69.0 %, TTO 77.5 %, CIJ 5.0 %, embossed 51.8 % | same |
| OCV, whole synthetic laser images (2-3 lines, up to 12 deg) | 0 / 200 misprints accepted, 101 / 200 good prints pass | [TEST-REPORT-2026-10-30.md](../../TEST-REPORT-2026-10-30.md) |
| Wrong free reads scoring >= 60 (laser lines) | 22 of 103 wrong lines | ocr-ai-reader note |
| Taught font, dot print: an untaught character one dot short of a taught one, printed with another dot missing | passes as the taught one about 1 in 20 | [ocr-dot-grid note](../../changes/2026-10-30-claude-ocr-dot-grid.md), Pending |
| Engine OCR tests | 154 / 154 | TEST-REPORT |

Two facts frame this chapter. First, every real image measured reads at 100 %, but there are only 389
real lines of one print type (laser, one product); CIJ, TTO, TIJ, embossed, foil and caps are
synthetic only. Second, the safety side holds (no wrong text accepted) and the cost is false rejects:
the reader is unsure of good print. Reaching the targets is mostly a data and a verification-score
problem, not a new architecture.

---

## 3. The text pipeline in layers (shared by 7.1-7.8)

| Layer | Step | What runs today | What is added in this study |
| --- | --- | --- | --- |
| L0 | Light, exposure | Job's camera settings; no text-specific light advice | Light recipe per print type (section 9); 4-direction light for embossed (7.5.5); coaxial plus polariser for foil (7.5.6); exposure from motion (section 9) |
| L1 | Correct | Percentile stretch, top-hat / black-hat strokes, polarity auto, rotation, slant, dot join, inkjet grain opening | Illumination flattening option, perspective, polar unwrap, per-character scale and shear, artwork suppression, stroke growth / shrink compensation (7.9) |
| L2 | Find | Letter-by-letter line finder, rotated blocks up to 12 deg, capital height, cut-short detection (`lines.cpp`) | Block layout prior (7.4.6), DB text detector as a second finder for scenes with artwork (7.0) |
| L3 | Read | CRNN-class conv CTC model (`ocrnet.onnx`), taught-font NCC reader (`font.cpp`), format-constrained Viterbi (`format.cpp`) | Print-type models (7.1.1), second recogniser (7.1.2), OCR-A/B (7.2.1), Indian scripts (7.8) |
| L4 | Verify, decide | Forced alignment of expected text, per-character score and quality, learned limits, format and date rules | Expected-vs-rival margin (7.3.3), reader agreement (7.1.2), test-time checks (7.10), MRP / serial rules (7.4.3, 7.4.5) |
| L5 | Learn, tune | Teach font, learn dots, learn print limits from 10+ good parts, propose formats | Auto-tune by known text (Cognex method, 7.11), product teach (7.3.4), auto-labelling from line OCV (7.10), drift (7.6) |
| L6 | Output, audit | Text, boxes, per-character quality, PrintQuality, explain keys, NG images | Grade per line (7.6), text drift trend, PLC registers for read text and grade (section 8) |

---

## 4. Tools

### 7.0 Text finder and straightener — "Find text"

- **Purpose.** Find every line of text in the region, straighten it (rotation, slant, curvature) and
  hand a 32 px high line image to the readers. **Status: Partial.** Built in `engine/src/ocr/lines.cpp`
  (letter-by-letter chaining, top-hat / black-hat strokes, rotation by row-profile sharpness, capital
  height estimate, slant by column profile, word gap as space, relief mask for embossed, cut-short
  lines fail) per notes [ocr-line-finder](../../changes/2026-10-30-claude-ocr-line-finder-1dd5f4.md),
  [ocr-wordgap-slant](../../changes/2026-10-30-claude-ocr-wordgap-slant.md),
  [ocr-cap-height](../../changes/2026-10-30-claude-ocr-cap-height.md),
  [ocr-cut-short-reason](../../changes/2026-10-30-claude-ocr-cut-short-reason.md). Whole synthetic
  images still lose many lines (laser whole-image check 101/200), so this is the first place PR #142
  will look.
- **Best in class.** Cognex OCRMax uses a fixed region with angle and skew ranges, local normalisation
  and auto-tune of segmentation ([OCRMax](https://docs.cognex.com/is_611/web/EN/ise/Content/Reference/OCRMax.htm)).
  MVTec HALCON `find_text` adds polarity, stroke width limits, border elimination and an expected
  line structure ([set_text_model_param](https://www.mvtec.com/doc/halcon/2505/en/set_text_model_param.html)).
  HALCON Deep OCR has a learned detector in front of the recogniser
  ([Deep OCR](https://www.mvtec.com/knowledge-base/technologies/deep-learning/methods/deep-ocr)).
- **Layers.**

| Layer | Step | Method (source) | Options (name, values, default, auto-learned, level) | Output |
| --- | --- | --- | --- | --- |
| L1 | Illumination flattening | Background estimate by large median or morphological closing, divide (as OCRMax "Local" normalisation) | `Normalise`: Off / Global / Local / Local strong; default Local; auto-tuned yes; Advanced | Flat region |
| L1 | Polarity | Both polarities scored, best kept (have); per-character local polarity for foil (HALCON `manual_is_imprinted`) | `Polarity`: Auto / Dark on light / Light on dark / Mixed; Auto; learned yes; Advanced | Stroke mask |
| L1 | Stroke mask | Top-hat / black-hat at letter size (have); relief mask for embossed (have) | `Print type` (7.5); auto | Mask |
| L2 | Line chaining | Letter-by-letter chain with rotation search (have) | `Max angle` 0-180 deg, 15; `Max lines` 1-16, 8; learned angle yes | Lines with corners |
| L2 | Second finder **NEW** | DB text detector (PP-OCR class) on the region when artwork is behind the text | `Finder`: Letters / AI / Both; Letters; Advanced | Line boxes |
| L2 | Layout prior **NEW** | Expected block structure (7.4.6) | from formats; auto | Line roles |
| L1 | Straighten | Rotation, slant shear (have), polar unwrap (7.7), smooth baseline curve **NEW** (fit a 2nd-order baseline through character bottoms, resample) | `Curved text`: Off / Bow / Cap ring; Off; Advanced | 32 px line image |
| L4 | Cut-short check | Ink at region edge or in line past a gap: line fails (have) | none | `cut_short` |

- **Corrections.** Illumination drift and gradients (flattening, automatic); polarity (automatic);
  rotation and skew (automatic up to the set angle, 180 deg flip by trying both); slant (automatic,
  `slantOf`); perspective (from the fixture's homography, 7.9); curvature (7.7); background texture
  and artwork (top-hat at letter size today; AI finder and artwork subtraction 7.9); occlusion (cut
  short fails; never guessed).
- **Outputs.** Line corners, angle, slant, capital height, cut-short flag, the straightened line
  images (`vs_ocr_get_line_image`), overlay of line boxes.
- **Decide and safety.** A line not found where a format or expected line needs one is NG
  (`explain.ocr.no_text`). A line cut short is NG whatever it reads. More lines than expected: the
  extra one is reported; on Check text, print in line with an expected line fails as extra.
- **Learn and tune.** Auto-learn the angle, capital height, polarity and line count from the taught
  print; auto-tune normalisation and finder from good images (highest weakest-character score).
  Drift: capital height and angle trend per shift (printer head moved, product skewed).
- **Accuracy target.** 100 % of lines found on the real bench sets (section 6), 0 lines cut short
  that pass. Measured with `ocr_bench` per print type: lines found / lines in truth, and lines whose
  box misses a character.
- **Speed.** <= 8 ms per region on T1, 4 ms on T2 (line finder runs on a reduced copy).
- **Hardware.** Capital height >= 20 px, 30-40 px preferred; stroke >= 2 px (chapter
  [05](05-HARDWARE.md)).
- **Build plan.** (1) After #142: take its per-image causes; fix finder causes first, 1 pw. (2)
  Illumination flattening option, 0.5 pw. (3) Baseline-curve resampling, 0.5 pw. (4) DB detector as
  a second finder (reuse the PP-OCR DB ONNX already named in the spec; licence check), 1 pw. Reuses
  `lines.cpp` whole.

### 7.1 AI OCR — "Read text"

- **Purpose.** Read printed text with no training, any print type. **Status: Partial.** OcrNet
  (`ml/ocr/crnn.py`): a convolution-only CTC recogniser, 32 px line height, dilated 1 x 3
  convolutions in place of an LSTM so OpenCV DNN runs it; charset `ml/models/ocr_charset.txt` (0-9,
  A-Z, a-z, space, `. , : / - # % ( ) + & * '`, rupee and yen signs); temperature 1.5 folded into the
  last layer (`ml/ocr/calibrate.py`); run by `engine/src/ocr/recognize.cpp`. Read through formats by
  a CTC Viterbi (`format.cpp`, PR #83). No OpenVINO path yet.
- **Best in class.** HALCON Deep OCR: detection plus recognition, per-character confidence,
  retrainable for rare fonts; 25.11 normalises CTC loss by target length and ships a compact and a
  default recognition model
  ([release notes 25.11](https://www.mvtec.com/products/halcon/documentation/release-notes-2511-0)).
  Cognex ViDi EL Read: pretrained omni-font, trainable from about 10 images [S]
  ([page](https://www.cognex.com/products/machine-vision/vision-tools/ai-tools/edge-learning-tools/vidi-el-read)).
  Research: CRNN ([arXiv 1507.05717](https://arxiv.org/abs/1507.05717)), PARSeq, 91.9 % synthetic
  / 96.0 % real-trained on scene text ([arXiv 2207.06966](https://arxiv.org/abs/2207.06966)).
  No vendor publishes a character accuracy for date codes.
- **Layers.**

| Layer | Step | Method (source) | Options | Output |
| --- | --- | --- | --- | --- |
| L1-L2 | Find, straighten | 7.0 | - | 32 px line |
| L3 | Recognise | OcrNet CTC (have); print-type model (7.1.1) | `Print type`: Auto / Solid / Dot / Inkjet / Embossed / Foil; Auto; auto-tuned yes; Advanced. `Fine-tuned model`: file; none; Advanced | T x C probabilities |
| L3 | Constrained decode | Format automaton under CTC Viterbi (have); word list (lexicon) for fixed words such as MFG, EXP, BATCH **NEW** | `Expected format` (7.4); from teach; Essential. `Character set limit`; from format; Advanced | Best path per line |
| L3 | Test-time checks **NEW** | Re-read at scale 0.95 / 1.05 and shear +-3 deg; and the other polarity for mixed print; the read must be the same (own design: no vendor source found, see research gap) | `Stability check`: Off / On; On for Read text with no expected text; Advanced | Stable / unstable |
| L4 | Confidence | Per-character probability after calibration (have); margin to second-best path (7.3.3); agreement (7.1.2) | `Min character confidence` 10-99, 60; Advanced | Character and line score |
| L4 | Decide | Every line read, every character >= min, format matches, readers agree | - | OK / NG with reason |

- **Corrections.** All of section 7.9; the model itself is trained to tolerate residual slant (+-4
  deg), blur, missing dots, grain, low contrast, texture and bow (`ml/ocr/synth_text.py`).
- **Outputs.** `Lines` (text, box, per-character confidence and box), `Text`, `CharCount`,
  per-line format index; overlays of character boxes coloured by confidence; PLC string register of
  the read text (section 8).
- **Decide and safety.** A character below min, an unstable read, a reader disagreement, a format
  mismatch, a line cut short, or a time-out: NG. The format constraint chooses but never vouches (a
  forced character keeps its own low probability). A read is never corrected to fit a format.
- **Learn and tune.** Auto-learn proposes the format from the confirmed read. Auto-tune picks the
  print type and normalisation by the highest weakest-character score over good images. Per product
  kept: print type, polarity, capital height, angle, formats, optional fine-tuned model (7.3.4).
  Drift: mean line score per hour falling (printer, light, focus).
- **Accuracy target.** >= 99.5 % of characters without training on every real print-type bench set
  (requirements section 13; DL-11 for CIJ); >= 99.95 % after product teach; wrong reads passing Read
  text: 0 (with agreement on). Measured: characters right / characters in truth, spaces ignored, per
  print type, on >= 20,000 real characters each (section 6).
- **Speed.** One 3-line block <= 25 ms on T1 (OpenVINO), <= 8 ms on T2 (spec 7.1). Today 14-17 ms
  per synthetic image on an Apple M3 with OpenCV DNN.
- **Hardware.** As 7.0. T1 needs the OpenVINO execution path; buy T2 (RTX 4060 class) only when the
  second recogniser and two cameras at 225 ppm do not fit in 267 ms per part on T1.
- **Build plan.** (1) Retrain with real data and length-normalised CTC (7.10), 2 pw. (2) OpenVINO
  inference path for OcrNet (shared with the AI runtime), 1 pw. (3) Lexicon of fixed label words,
  0.5 pw. (4) Stability check, 0.5 pw. Reuses recognize.cpp, format.cpp, verify.cpp.

### 7.1.1 Print-type models — **NEW**

- **Purpose.** One recogniser per print family, because one model for all trades accuracy between
  them (CIJ 72.9 % while TTO is 98.0 %). **Status: Missing** (one OcrNet for all).
- **Best in class.** HALCON ships separate pretrained classifiers per print family: Document,
  DotPrint (and 0-9, 0-9A-Z variants), Industrial, SEMI, OCRA, OCRB, Pharma, each with a `_Rej`
  rejection class ([create_text_model_reader](https://www.mvtec.com/doc/halcon/2505/en/create_text_model_reader.html)).
  Videojet [US10628934](https://patents.google.com/patent/US10628934) (active, est. 2037): a printer
  whose OCR is trained per print technology, ROI from product parameters; adopted as the method
  (owner's statement of 2026-10-30, legal clearance).
- **Layers.** L3: models `solid` (laser, TTO, offset), `dot` (CIJ 5x5 / 5x7 / 7x9, TIJ grain),
  `relief` (embossed, debossed), `foil`; same input and output shapes so the engine is unchanged.
  Options: `Print type` picks the model; Auto runs a small print-type classifier (a 4-class head on
  the line image, own design) and falls back to running two models when it is unsure. L5: each model
  trained and gated separately (7.10).
- **Corrections.** Each model's synthetic data carries its own defects: dot dropout, merged dots,
  dead row, speed jitter (dot); ribbon voids, wrinkles (TTO); thin strokes and burn halo (laser);
  one-side light and shadow (relief); specular flare (foil).
- **Outputs.** As 7.1, plus `PrintTypeUsed`.
- **Decide and safety.** If Auto's two candidate models disagree, NG with `explain.ocr.unsure`.
- **Learn and tune.** Print type learned at teach and kept per product; drift signal: classifier
  starts preferring another type (printer changed).
- **Accuracy target.** Each model >= 99.5 % characters on its real set, and no worse than the joint
  model on any set (gate).
- **Speed.** Same network size; Auto with two models doubles L3 time only when unsure.
- **Build plan.** Split training sets and heads, 1 pw; print-type classifier, 1 pw; gate and engine
  model selection, 1 pw. Depends on real CIJ, TTO, embossed images (owner).

### 7.1.2 Second recogniser and agreement — **NEW**

- **Purpose.** A free read is trusted only when two readers built differently give the same text.
  This is the text analogue of the code reader's cross-checks. **Status: Missing** as a rule; AI and
  taught-font readers both exist (`vs_ocr_params.reader`).
- **Best in class.** No vendor documents reader voting (research gap). Methods to combine: HALCON
  reject class; OCRMax confusion threshold (gap best to second, 0-40, default 0 = off)
  ([OCRMax](https://docs.cognex.com/is_611/web/EN/ise/Content/Reference/OCRMax.htm)); an explicit
  error predictor ([Mor and Wolf, arXiv 1805.11161](https://arxiv.org/abs/1805.11161)); Datalogic
  [US10810465](https://patents.google.com/patent/US10810465B2/en) (active to 2039): an auto-encoder
  rebuilds each detected character and the rebuild error decides whether the classifier's answer
  is a true character; Xerox [US9384423](https://patents.google.com/patent/US9384423B2/en) (active,
  est. 2034): confidence from the similarity of image and text embeddings. Both adopted (owner's statement of
  2026-10-30, legal clearance).
- **Layers.**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L3 | Reader B | An attention or transformer recogniser of a different family (PARSeq-tiny or SVTR-tiny class, ONNX), trained on the same data with different augmentation seeds | `Second reader`: Off / AI B / Taught font / Both; AI B when a model is installed; Advanced | Text, per-char probability |
| L3 | Reader C | Taught font (7.2), when the job has one | as above | Text |
| L4 | Character rebuild check | Auto-encoder per print family; rebuild error above the learned limit = "not a clean character" (US10810465 method) | `Rebuild check`: On / Off; On for Read text; Advanced | Per-char flag |
| L4 | Agreement | Same text from A and B (and C if set), and each at or above min | `Agreement`: All readers (default; locked when there is no expected text) / Off only with expected text (OCV) | OK / unsure |

- **Corrections.** None of its own; it catches what corrections missed.
- **Decide and safety.** Disagreement is NG (`explain.ocr.unsure`, naming the position). Agreement
  never raises a score, it only allows a pass.
- **Learn and tune.** Thresholds of reader B and the rebuild limit are calibrated on the safety sets
  so that 0 wrong lines pass (7.10).
- **Accuracy target.** On free reads: 0 wrong texts accepted on the safety sets (>= 3,000 altered
  lines per print type, section 6), with good-line acceptance >= 99.5 %.
- **Speed.** Reader B +6 ms on T1, +2 ms on T2, run only on lines A read above min.
- **Build plan.** Train reader B, 2 pw; ONNX through OpenCV DNN or OpenVINO (check attention ops
  export), 1 pw; agreement rule and rebuild check, 1 pw.

### 7.2 Classical OCR from a taught font — "Read text"

- **Purpose.** Read a fixed font taught from sample print or drawn from a system font; for fixed
  fonts, very fast lines and PCs without AI. **Status: Have.** `engine/src/ocr/font.cpp`: cut at ink
  gaps or fixed pitch, 16 x 32 cell, NCC against taught samples at shears up to +-4 deg, score = fit
  times lead over the next character (0.10 lead for full marks), dot-lattice compare for dot print,
  never-taught characters as Unknown; teaching asks only about unsure characters (PR #75); rendered
  system font (PR #108, `FontRender.cs`, `FontTeach.cs`).
- **Best in class.** Cognex OCRMax: segmentation then classification against a trained font; accept
  threshold default 80, confusion threshold, field strings, Auto-Tune trains the font and sets
  segmentation in one pass ([OCRMax](https://docs.cognex.com/is_611/web/EN/ise/Content/Reference/OCRMax.htm)).
  HALCON `create_text_model_reader` with pretrained or trained classifiers.
- **Layers.**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L1 | Melt dot print | Closing by half a dot (have); Kodak dot preprocessing (expired [US5212741](https://patents.google.com/patent/US5212741A/en)) | `Dot join` px; auto from dots; Advanced | Strokes |
| L2 | Cut | Ink-gap columns / fixed pitch / bridge cut where pieces match (have) | `Segmentation`: Projection / Connected parts / Fixed pitch; learned; Advanced | Cells |
| L3 | Match | NCC to taught samples with shear (have); **NEW** a small MLP on grey plus gradient histogram as a second opinion trained in seconds (spec text) | `Font` (Essential); `Classifier`: NCC / NCC + MLP; NCC; Advanced | T x C log-probabilities |
| L4 | Score | Fit x lead (have); rebuild check (7.1.2) | `Min character confidence` | Per char |

- **Corrections.** Slant (shear search), touching characters (bridge cut), broken characters (join
  when it fits better), dot gaps (dot lattice), stroke growth (several taught prints per character).
- **Outputs.** As 7.1, plus `Unknown` status.
- **Decide and safety.** Unknown character, low lead, or a timed-out read: NG. Open safety item:
  an untaught character one dot short of a taught one passes about 1 in 20 on dot print (dot-grid
  note). Fix chosen here: on dot print, require every character the job's formats can print to be
  taught (setup check refuses the job otherwise) and add the rebuild check; owner to confirm.
- **Learn and tune.** Teach by typing a line; Teach this character; several prints per character set
  the full-score limit. Cognex [US9317767](https://patents.google.com/patent/US9317767) (active to
  2033-04-17; abstract: receive the image and the known string, generate segmentation parameters,
  segment, keep the parameters when the result meets the criteria) is adopted for auto-tune
  (owner's statement of 2026-10-30, legal clearance): at teach the engineer has typed the text, so the tool tries
  a grid of join strength, polarity, pitch, height and threshold and keeps the set whose cut gives
  exactly the typed characters with the highest weakest score.
- **Accuracy target.** >= 99.9 % characters on the taught font's real prints; 0 wrong texts passed.
- **Speed.** <= 5 ms per 20-character line on T1 (measured 5-10 ms per 10 characters on the
  development PC; T1 not measured).
- **Build plan.** Known-text auto-tune, 1 pw; MLP second opinion, 0.5 pw; "teach every printable
  character" setup check, 0.5 pw.

### 7.2.1 OCR-A, OCR-B and SEMI fonts — **NEW**

- **Purpose.** Read the standard machine-readable fonts without teaching (OCR-B is common on
  pharma cartons, SEMI on wafers and some device marking). **Status: Missing.**
- **Best in class.** HALCON pretrained `OCRA`, `OCRB`, `SEMI` classifiers
  ([create_text_model_reader](https://www.mvtec.com/doc/halcon/2505/en/create_text_model_reader.html)).
  Standards: ISO 1073-2 (OCR-B shapes and three sizes) and ISO 1831 (print specifications for OCR:
  paper and ink optical properties, print quality tolerances) [S]
  ([ISO 1073-2 listing](https://infostore.saiglobal.com/en-gb/standards/iso-1073-2-1976-589406_saig_iso_iso_1350164/),
  [ISO 1831 listing](https://ccn-scc.ca/standardsdb/standards/2029172)).
- **Layers.** L5: ship taught fonts drawn from freely licensed OCR-A / OCR-B font files through
  FontRender (licence of each file checked; no vendor file copied); L3: the 7.2 reader; L4: ISO 1831
  print-quality tolerances as an optional grade (7.6).
- **Decide, learn, targets.** As 7.2. Target >= 99.95 % characters; speed 5 ms per line.
- **Build plan.** 1 pw (font sourcing and licence check, drawn-font tests, a SEMI character set).

### 7.3 OCV — "Check text"

- **Purpose.** Verify the printed text is exactly the expected text (from the job, recipe, PLC text
  block or another tool) and score print quality per character. **Status: Partial.** Built:
  `engine/src/ocr/verify.cpp` force-aligns the expected text to the recogniser's per-column
  probabilities (CTC Viterbi); `quality.cpp` scores contrast, ink and pitch against limits learned
  from 10+ good parts (mean +- 4 sigma); extra print fails; allow shift learned; expected text from
  PLC registers 500-627 stamped with the trigger ID (PR #81, #93, #108, #136). Safe (0 wrong texts
  accepted) but true laser text passes only 69 % on synthetic lines (section 7.12).
- **Best in class.** Cognex OCVMax: verification only; locates the string with PatMax; per-string
  degrees of freedom X/Y shift, angle, uniform and X/Y scale, shear, and per-character rotation and
  shear for curved surfaces; accept default 0.50; confusion check against look-alikes with a
  confidence margin of about 0.1; reference rendered from font files (printer vendor fonts,
  TrueType, Image Font Extractor); Clean Print Mode with angle within about 10 deg, scale 0.9-1.1,
  contrast 60-100 grey
  ([OCVMax](https://docs.cognex.com/vpro_930sr1/web/help/html/c2da0f57-4fd9-4938-9a5e-14f61d0edb9d.htm)).
  HALCON classic OCV compares row and column grey projections
  ([create_ocv_proj](https://www.mvtec.com/doc/halcon/2505/en/create_ocv_proj.html)), weaker under shift.
  Keyence AI OCV learns acceptable variation (marketing, no data)
  ([IV4 page](https://www.keyence.co.uk/landing/lpc/lp_ppc_ai_ocr_vision_sensor.jsp)).
- **Layers.**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L2 | Locate string | 7.0 finder, fixture, allow shift (have) | `Allow shift` px; 0; learned position; Advanced | Line |
| L1 | Per-character fit | 7.3.1 | `Character freedom`: None / Shift / Shift+scale / Full; Shift+scale; Advanced | Aligned cells |
| L3 | Align expected | CTC Viterbi of expected text (have) | `Expected text source`: Job / PLC / Tool; Essential | Per-char columns |
| L3 | Reference compare | Template of the taught or drawn character at the aligned place (spec 7.3 "NCC"); 7.3.2 printer fonts | `Font`: Taught / System / Printer; Advanced | Template score |
| L4 | Verification score | 7.3.3 margin of expected over best rival | `Min margin`; learned; Advanced | Per-char margin |
| L4 | Print quality | Contrast, ink, pitch vs learned limits (have); stroke width, dot count **NEW** (7.6) | `Min print quality` 0-100, 70; Essential | Quality, reason |
| L4 | Decide | Every expected char present, correct, margin and quality >= min; no extra print; line in place | - | Match, CharResults, PrintQuality |

- **Corrections.** Shift (allow shift), rotation and slant (7.0), per-character scale and shear
  (7.3.1), dot gaps (dot lattice and join), stroke growth (learned ink limits), faint print (contrast
  limits), smear (ink above limit), extra print (fails).
- **Outputs.** `Match`, `CharResults` (expected, found, quality, status OK / Unsure / Wrong /
  Missing / Extra / Unknown, reason Unclear / Faint / Broken / Smeared / Position), `PrintQuality`,
  shift per line; overlays per character; PLC bits OCV OK / NG and a reason code.
- **Decide and safety.** Missing or stale expected text from PLC or tool: NG
  (`explain.ocv.no_expected`). Never passes: a changed character, a look-alike, a missing or extra
  character, a date one day off (format plus expected), a line out of place. The OCV expected text
  always comes from the job or the PLC batch record, never from a code read on the pack. Cross-check:
  when the same text is in a code on the pack (GS1 AI 10 lot, 17 expiry), equality of the printed text
  with the decoded code is an additional check (any difference is NG); the code is never the source of
  the expected text.
- **Learn and tune.** Learn good parts (limits after 10), learn dots, learn position; product teach
  (7.3.4); auto-tolerance mean +- k sigma with spread floors (ink 6 %, contrast 5 %, pitch 1 px).
  Drift: per-character contrast and ink trend (nozzle drying, ribbon wear, laser power).
- **Accuracy target.** 0 wrong texts accepted on the safety sets (>= 3,000 altered lines per print
  type; 30,000 over the bench, giving < 0.01 % at 95 % confidence when 0 pass); false reject <= 0.5 %
  on >= 600 good real parts per print type (rule of three: 0 fails in 600, or <= 1 fail in 949,
  bounds the rate at 0.5 % with 95 % confidence). Section 6.
- **Speed.** <= 10 ms per 20-character line on T1, 4 ms on T2.
- **Build plan.** In 7.3.1-7.3.4; the base is built.

### 7.3.1 OCV per-character freedom — **NEW**

- **Purpose.** Let each character move, scale and shear a little against the reference, as CIJ
  speed jitter, shrink-wrap and curved packs do, without letting a wrong character through.
  **Status: Missing** (whole-line shift only; pitch is scored).
- **Best in class.** OCVMax per-string DOF (shift, angle, uniform / X / Y scale, shear) and
  per-character rotation and shear for curved surfaces; each DOF costs time, shear off by default
  ([OCVMax](https://docs.cognex.com/vpro_930sr1/web/help/html/c2da0f57-4fd9-4938-9a5e-14f61d0edb9d.htm)).
- **Layers.** L1: for each aligned character, search shift +-0.3 cell, scale 0.85-1.15 (X and Y
  apart), shear +-6 deg, rotation +-4 deg, by coarse-to-fine NCC against the reference glyph; L4:
  the fitted parameters are scored against learned limits (a character needing 1.15 scale where good
  parts needed 1.02 is Position or Broken). Options: `Character freedom` None / Shift / Shift+scale
  / Full; ranges learned from good parts (Advanced).
- **Decide and safety.** The look-alike check runs after the fit at the same freedom: the expected
  glyph must beat every rival glyph fitted with the same freedom by the margin (7.3.3), so freedom
  cannot turn a 3 into an 8.
- **Accuracy target.** True-text pass >= 99.5 % on real laser, TTO and CIJ; 0 altered texts
  accepted.
- **Speed.** +4 ms per 20 characters on T1 (coarse grid 3 x 3 x 3, then refine).
- **Build plan.** 3 pw (fit, limits, look-alike under freedom, tests on drawn and real print).
- **Measured on the AI reader (PR #166, 2026-10-31): not built.** The line image was re-read at x scale
  0.9 / 1.1, y scale 0.88 / 1.12 and shear +-0.1 (7 recogniser runs) and checked on the eval sets with
  every look-alike at every place (`eval_ocr --every`). Each letter taking its best warp mixes
  alignments from different warps: random changed texts pass (validation laser 2 -> 15 of 31,422; real
  Harpic 0 -> 11 of 31,374), even when the rival given the same freedom must lose by 3 nats. The whole
  line in one warp passes 6 points more laser text but accepts look-alikes 9 -> 25 of 9,320. The whole
  line in one warp with the rival under the same freedom 1 nat behind is safe but worth only +0.5 point
  (laser 69.0 -> 69.5 %, TTO 77.5 -> 78.0 %, test sets). Cause: of the laser characters that fail, 44 %
  have another character scoring above the expected one and 19 % the blank; the recogniser is unsure of
  the character, not of its shape. Freedom belongs with a reference glyph (taught or printer font,
  7.2, 7.3.2), not with the AI read. Notes: `docs/changes/2026-10-31-claude-ocv-letter-freedom.md`.

### 7.3.2 Printer-maker fonts as reference — **NEW**

- **Purpose.** The reference should be the printer's own font, not a look-alike system font.
  **Status: Missing** (system fonts only, `FontRender.cs`; refused for dot print because drawn shapes
  have no dots).
- **Best in class.** OCVMax takes fonts from the printer vendor (and lists formats such as .ttf,
  .fnt, .bdf) or an Image Font Extractor that builds a font from a sample image (OCVMax page above).
  Cognex [US10068153](https://patents.google.com/patent/US10068153) (active to 2032-08-21): a marker
  font file translated into a synthetic image to train the OCR; adopted (owner's statement of
  2026-10-30, legal clearance). Videojet print guides list CIJ fonts as 5x7 and 7x9 dot rasters in one to three lines
  [S] ([Videojet 1000 line guide](https://global.videojet.com/wp-content/uploads/dam/pdf/NA%20-%20English/Print%20Guides/pg-1000-line-metric-us.pdf)).
- **Layers.** L5: three sources: (a) a dot-raster font definition (5x5, 5x7, 7x9, 10x16, two and
  three line) drawn as dots at the learned dot size and pitch, so dot print can have a drawn
  reference; (b) printer-maker font files the owner holds a licence for (the owner sells EuroCod TIJ
  printers, requirements 17.2: get the font and the protocol from the maker); (c) an image font
  extractor: teach from one good print, characters cut and kept (that is today's taught font).
  The drawn font also seeds 7.3.4 fine-tuning (US10068153 method).
- **Decide and safety.** As 7.3; a font changed since save fails every part until saved again
  (as PR #108).
- **Build plan.** Dot-raster renderer and tests, 1 pw; printer font import (BDF and TTF), 0.5 pw;
  EuroCod integration of the font and the print values (with the printing module), 0.5 pw plus the
  maker's data.

### 7.3.3 Verification score: expected text against its best rival — **NEW**

- **Purpose.** Judge "is this the expected text?" by how much better the expected text explains the
  image than the best other text the recogniser can produce, not by the absolute probability of
  each character. **Status: Partial** (PR #166: the margin is measured and reported per character
  and per line, `vs_ocr_char_quality.margin`, `vs_ocr_line_ext.margin`, the Check text value
  `margin`; the pass rule still uses the probability: as a pass route it gained at most 1 point of
  laser true text on the eval sets while look-alike accepts crept up, see 7.12).
- **Method (own design, built on known parts).** For each expected character position, compute the
  log-probability of the forced path with the expected character and with the best rival character
  at that position, taken from the recogniser's full character set plus the CTC blank (a missing
  character). The rival set is never limited by the format: a format check may only add an NG, never
  remove a rival (the OCRMax confusion threshold and the OCVMax
  confidence margin are the same idea for template scores, pages above). Margin m_i =
  log P(expected) - log P(best rival). Line passes when every m_i >= M and the template score of
  7.3.1 and the print quality limits pass. M is set on the safety sets as the smallest margin with 0
  altered texts accepted, plus a safety step; it is calibrated per print type (Slossberg et al.:
  calibrate at word level, character-level calibration can make word-level error worse,
  [arXiv 2012.12643](https://arxiv.org/abs/2012.12643)).
- **Why it fixes false rejects.** A thin laser 8 read at p = 0.55 (fails 60 today), where the best
  rival over the full character set and the blank is B at 0.05, has a margin of 2.4 nats: clearly the
  expected text. If any other character or the blank scores close to the 8 (for example a 3 at 0.40,
  whatever the format allows), the margin is small and the line is Unsure (NG).
  A real misprint B gives a negative margin. The absolute score stays visible as print quality, not as identity.
- **Decide and safety.** Missing, extra and wrong are unchanged; low margin is Unsure (NG).
- **Build plan.** 2 pw (margin in verify.cpp, calibration script in `ml/ocr/calibrate.py`, gate).

### 7.3.4 Product teach (fast adaptation) — **NEW**

- **Purpose.** Bring a product's own print to near 100 % from 20 good prints, in about a minute on
  the line PC. **Status: Missing** (`dl.ocr_tune` is spec only).
- **Best in class.** ViDi EL Read trains from about 10 images [S]; HALCON Deep OCR recognition
  training for rare fonts and low contrast ([Deep OCR](https://www.mvtec.com/knowledge-base/technologies/deep-learning/methods/deep-ocr)).
- **Method.** Freeze OcrNet except the last two blocks; train on the product's good lines (labels =
  the confirmed expected text) plus their synthetic variants (dot dropout, shift, contrast) plus
  look-alike negatives drawn by swapping characters in the drawn font (7.3.2); 300-600 steps.
  Gate: on the product's held-out good prints true-text pass must rise and the product's altered
  set must stay at 0 accepted; otherwise the base model is kept. The tuned head is kept in the job
  (signed with the Vision Package).
- **Accuracy target.** False reject <= 0.5 % on that product; 0 accepted altered texts.
- **Speed.** <= 60 s on T2 GPU, <= 5 min on T1 CPU.
- **Build plan.** 2 pw (trainer in ml/ocr, job storage, Teach button).

### 7.4 Format validation — "Check date / lot"

- **Purpose.** Check the read text is a valid date, lot, price or serial, consistent with the clock
  and with each other. **Status: Have.** `dotnet/VisionCore/Tools/TextFormat.cs` and
  `engine/src/ocr/format.cpp`: fields `{D:n}`, `{A:n}`, `{X:n}`, `{?:n}`, `{MFD:..}`, `{EXP:..}`,
  `{re:..}`; real dates; expiry not before production and not past; expiry = production + N months;
  production within N days of the picture time; MMM in English, Spanish, French, German, Portuguese,
  Italian; format-constrained Viterbi in the engine (PR #61, #83).
- **Best in class.** OCRMax field strings (N, A, a, H per position); OCVMax key sets per position;
  HALCON `text_line_structure` and separators (pages above). None of them checks dates or Indian
  label rules: this is where we lead.
- **Layers.** L3: constrained decode (have). L4: rules per field (7.4.1-7.4.5). L5: format proposed
  from the confirmed text (have).
- **Outputs.** `Fields` (MFD, EXP, LOT, MRP, SERIAL, other), `Valid`, reasons; PLC registers for
  the fields as numbers (YYYYMMDD) for the host to log.
- **Decide and safety.** A field that is not a real value is NG; the constraint never corrects a
  read; a date label read without its date is NG.
- **Speed.** <= 0.1 ms.

### 7.4.1 Dates in every format, month names in Indian languages

- **Purpose.** Accept every date layout Indian packs use and reject every impossible one.
  **Status: Partial.**
- **Rules (sources).** Legal Metrology (Packaged Commodities) Rules 2011, Rule 6(1)(d): month and
  year of manufacture, packing or import; best before / use by with day, month and year where the
  product can become unfit ([iPleaders summary](https://blog.ipleaders.in/legal-metrology-packaged-commodities-rules-2011/),
  [S.S. Rana note](https://ssrana.in/articles/importance-of-writing-best-before-and-manufacturing-date-on-labels/)).
  Drugs Rules 1945, Rule 96: expiry in month and year, valid to the last day of that month,
  preceded by "Expiry date" [S] (search snippets; consultant summary
  [Agile Regulatory](https://www.agileregulatory.com/blogs/cdsco-drug-packaging-and-labelling)
  confirms month-year expiry, no rule text opened). Chapter 03 holds the standards in full.
- **Formats to accept (field grammar).**

| Group | Examples | Notes |
| --- | --- | --- |
| Numeric day-month-year | 05/11/26, 05.11.2026, 05-11-2026, 051126 | DD MM YY / YYYY; separator `/ . - space` or none |
| Month-year | 11/2026, 11.26, NOV 2026, NOV.26, NOV-26 | pharma expiry: last day of month |
| Month names, Latin | JAN-DEC, January-December; Spanish, French, German, Portuguese, Italian (have) | case free |
| Month names, Indian languages **NEW** | Hindi जनवरी, फरवरी / फ़रवरी, मार्च ...; Tamil ஜனவரி ...; Bengali জানুয়ারি ... (all 12 languages of [LOCALISATION.md](../LOCALISATION.md)) | Taken from Unicode CLDR wide and abbreviated forms ([CLDR Gregorian chart](https://www.unicode.org/cldr/charts/latest/by_type/date_&_time.gregorian.html)); both nukta spellings; native digits (Devanagari ०-९ etc.) mapped to 0-9 |
| Year-first | 2026-11-05, 26/11 (YY/MM) | ISO 8601; YY/MM only with a label, never guessed |
| Julian / day-of-year | 26309 (YYDDD) | used by some FMCG coders; `{JUL:YYDDD}` |
| Week codes | 2645 (YYWW) | `{WK:YYWW}` |
| Time | 14:35, 1435, 14:35:20 | with production date: within N minutes of picture time |
| Shift and line codes | A / B / C, L1 | literal sets |

  Indian-script month names need the Indian-script recogniser (7.8): until then they apply to
  bilingual text read by that model only, and are refused at save on a Latin-only job (as today).
  Label words in Indian languages (निर्माण तिथि, समाप्ति तिथि, बैच) are added to the label list used to
  name dates (MFD / EXP) when 7.8 ships.
- **Decide and safety.** Not a real date (31/02, month 13): NG. Two-digit year: 20YY only. Ambiguous
  DD/MM vs MM/DD: the format decides; never guessed. A month-year expiry compares as the last day
  of the month.
- **Accuracy target.** 100 % of cases on a date rule test set (>= 2,000 generated cases including
  leap years, month ends, every language) and on all real labels.
- **Build plan.** CLDR month table for the 12 Indian languages (data file, generated, licence
  Unicode), Julian and week fields, 1.5 pw.

### 7.4.2 Lot / batch

- **Purpose.** Check the lot field's shape and, where known, its value. **Status: Have** (fields
  and `{re:}`).
- **Additions.** Lot value from the PLC or ERP batch record (exact match, through OCV source); lot
  equal to the GS1 AI 10 in the pack's code (cross-check); labels B.No., Batch No., LOT, Lot No.
  recognised by teach. Effort 0.5 pw.

### 7.4.3 MRP under Legal Metrology — **NEW**

- **Purpose.** Check the MRP line as the rules require and as the batch record says. **Status:
  Missing** (generic digits only; teach refuses to turn a price into a date, PR #61).
- **Rules (source).** Legal Metrology (Packaged Commodities) Rules 2011: retail sale price
  inclusive of all taxes (Rule 2(m), Rule 6(1)(c) or (e) depending on the edition, summaries
  differ); printed as "MRP" or "Maximum Retail Price" with "inclusive of all taxes"; currency as the
  rupee sign, "Rs." or "INR"; up to two decimals; declaration height at least 1 mm, 2 mm when
  embossed or moulded (Rule 7(3)); stickers may not alter declarations
  ([iPleaders summary](https://blog.ipleaders.in/legal-metrology-packaged-commodities-rules-2011/)).
  Gazette text not opened (limits).
- **Field.** `{MRP}`: optional label (MRP / M.R.P. / Maximum Retail Price), currency (₹ / Rs. /
  INR), amount with optional thousands separators (Indian grouping 1,23,456.00) and 0-2 decimals,
  optional "(Incl. of all taxes)" / "inclusive of all taxes" phrase.
- **Decide and safety.** Amount must equal the batch record's MRP (from PLC text block or job
  recipe) to the paisa; a missing "inclusive of all taxes" phrase, when the job says the line holds
  it, is NG; character height below 1 mm (from calibration, 04a Tool 3) is a warning, NG when the
  job requires it.
- **Build plan.** 1 pw (field grammar, Indian grouping, height check, tests).

### 7.4.4 Expiry against production, shelf life, today

- **Status: Have** (expiry not before production, not past; expiry = production + N months to the
  day or day before; production within N days of picture time).
- **Additions.** Month-year expiry as last day of month (Rule 96 reading above); shelf life from the
  product master (ERP) rather than typed; a clock check: the line PC clock is checked against the
  PLC or server time at start, and a clock off by more than 2 minutes makes every date rule NG (a
  wrong clock must not pass a wrong date). Effort 0.5 pw.

### 7.4.5 Serials and counters — **NEW**

- **Purpose.** Printed serial numbers and counters (unit count, case number, shift counter) are
  checked as a sequence. **Status: Missing.**
- **Method.** `{SER:n}` field: value from the printer or the ERP print list (exact candidate, as
  the code reader's serial-run matching, PR #9); or monotonic: equal to previous + step, with a
  window for rejected parts; duplicates within the last N (the code reader keeps 10,000 child codes)
  flagged orange like duplicate codes; a check digit option (mod 10 / mod 11 / Luhn).
- **Decide and safety.** A duplicate, a skip beyond the window, or a value not in the print list:
  NG. Effort 1 pw, depends on the printing module and PLC text block.

### 7.4.6 Block layout prior (multi-line) — **NEW**

- **Purpose.** Use the known layout of the block ("BATCH line, MFG line, EXP line, MRP line", each
  with its own width) to cut lines and characters, not only to check them afterwards. **Status:
  Partial** (PR #160): `vs_ocr_set_layout` ("2 2 2-4 | 6"), learned at Teach (`vs_ocr_learn_layout`);
  the finder joins pieces of one line split by a wide gap when more lines are found than the layout
  lists; Read text lines are read with the layout's count and must fit it; a layout line not found
  or not fitting is NG with the reason (`vs_ocr_layout_reason`). Not built: splitting two lines the
  finder joined, line height and alignment roles, several alternative layouts.
- **Best in class.** HALCON `text_line_structure`, e.g. `'2 2 2-4'` (block lengths) with
  `text_line_separators` such as `/` and `:`, several indexed structures allowed
  ([set_text_model_param](https://www.mvtec.com/doc/halcon/2505/en/set_text_model_param.html)).
- **Method.** From the formats: number of lines, characters per line and block groups, relative
  line heights and left alignment learned from teach. L2: when the finder joins two lines or splits
  one, the prior chooses the cut that fits the layout. L3: character count per line feeds the
  Viterbi (already length-aware). L4: a line out of its learned order or alignment is Position NG.
- **Speed.** +0.5 ms. Effort 1.5 pw.

### 7.5 Print-type modes (shared by 7.1-7.3)

Each mode sets the L0 light advice, the L1 corrections, the 7.1.1 model and the 7.2 cutting. Status
from TOOL-SPECIFICATIONS 7.5 and the notes [ocr-dot-pitch](../../changes/2026-10-30-claude-ocr-dot-pitch.md),
[ocr-dot-grid](../../changes/2026-10-30-claude-ocr-dot-grid.md),
[ocv-shift-plc-inkjet](../../changes/2026-10-30-claude-ocv-shift-plc-inkjet.md).

#### 7.5.1 CIJ / dot-matrix

- **Status: Partial.** Learned dot size and pitch (`vs_ocr_measure_dots`), closing 1.2 dot
  diameters, join strength 0.5-3, fixed-pitch cutting, dot-lattice compare. AI on synthetic CIJ
  72.9 %; good CIJ lines pass their check only 5 %. No real CIJ images measured.
- **Best in class.** HALCON `dot_print`, `dot_print_max_dot_gap`, `dot_print_min_char_gap`,
  `dot_print_tight_char_spacing` (true when the gap between characters is smaller than the largest
  dot gap inside one) (page above). OCRMax fragment rules (min fragment size 15). Kodak
  [US5212741](https://patents.google.com/patent/US5212741A/en) (expired 2012: histogram, spatial
  averaging, contrast stretch, binarise) and [US5357581](https://patents.google.com/patent/US5357581A/en)
  (expired 2011: detect dot-matrix regions and filter only there). Nagai
  [US4872024](https://patents.google.com/patent/US4872024) (expired: print inspection by dot counts).
- **Options to add.** `Max dot gap` px (auto), `Min character gap` px (auto), `Tight spacing`
  (auto-detected when the learned character gap < learned dot gap), `Dot raster` 5x5 / 5x7 / 7x9 /
  10x16 / Auto, `Lines` 1-3 (twin and three-line rasters print smaller characters).
- **Corrections.** Missing dots (lattice: score the dots present against the raster; the grade
  counts missing dots, 7.6), merged dots and satellites (blob size filter), dead row (raster row
  empty across all characters: NG with "nozzle row missing"), speed jitter (per-character X scale
  in 7.3.1, pitch learned), stretched or squeezed line (X scale), slant from product skew (shear).
- **Accuracy target.** >= 99.5 % characters on real CIJ without teach (DL-11); true-text pass
  >= 99.5 % after product teach. **Effort** 3 pw (parameters, raster lattice per raster size, real
  data training) plus owner's CIJ samples.

#### 7.5.2 Laser (coated carton, label, PET, foil, plastic)

- **Status: Partial.** Real laser 100 % on one product (389 lines); synthetic 92.6 %.
- **Physics.** Laser either removes a coating (light text on dark ink patch: light-on-dark, have)
  or marks the substrate (dark or frosted text). Thin, even strokes; low contrast; halo of burned
  edge; on PET and foil, reflections flip polarity locally (HALCON `manual_is_imprinted`).
- **Corrections.** Polarity auto and mixed; halo as stroke growth (learned ink limits); low contrast
  (Mono12, flattening); specular (polariser, 7.5.6).
- **Light.** Diffuse dome or low-angle ring for coated carton; coaxial plus crossed polariser for
  PET and foil. **Effort** 1.5 pw (mixed polarity, laser-on-PET synthetic data).

#### 7.5.3 Thermal transfer (TTO)

- **Status: Partial.** Synthetic 98.0 %. Ribbon voids, wrinkles, bold strokes, sometimes light
  print at line start (head warm-up). Corrections: inkjet-like grain opening at low strength,
  stroke growth via ink limits. **Effort** 0.5 pw (wrinkle and warm-up synthetic data).

#### 7.5.4 Thermal inkjet (TIJ, e.g. EuroCod)

- **Status: Partial.** `VS_PRINT_INKJET`, grey opening fills grain; helps heavy grain (4 of 5 OK at
  40 % missing ink pixels) and hurts light grain, so chosen on good parts. Never chosen by Auto.
- **Additions.** Auto for Inkjet by grain measure (fraction of holes inside strokes) once real TIJ
  is measured; nozzle-column dropout (a vertical stripe missing through every character, typical of
  a blocked TIJ nozzle) detected as its own reason. **Effort** 1 pw plus the owner's EuroCod prints.

#### 7.5.5 Embossed / debossed

- **Status: Partial.** Relief mask in the line finder; AI on synthetic emboss 89.4 %, true-text pass
  51.8 %. No real images.
- **Best in class.** Photometric stereo: 3 or more images under known light directions (4-6
  recommended, slant 30-60 deg) give gradient, albedo and relative height, separating shape from
  print ([HALCON photometric_stereo](https://www.mvtec.com/doc/halcon/2505/en/photometric_stereo.html)).
  Keyence LumiTrax: directional lighting creating shadows to show height features and remove glare
  ([Keyence lighting guide](https://www.keyence.com/ss/products/vision/high-performance-lighting/)).
- **Layers.** L0: 4-segment low-angle ring, each quadrant strobed on its own frame (or a colour
  trick with an RGB camera); L1: photometric stereo to a curvature or height image, then the
  normal path reads it as solid print; single-image fallback: raking light from one side, relief
  mask (have).
- **Corrections.** Stamp depth variation (height normalised per line), surface texture of cast or
  moulded parts (albedo removed), glare.
- **Accuracy target.** >= 99.5 % characters with 4-direction light. **Speed** 30 ms T1 / 12 ms T2
  (4 images). **Hardware** 4-segment ring light with a controller the camera or PLC can step per
  frame. **Effort** 3 pw.

#### 7.5.6 Ink on foil (blister lidding, sachets) — **NEW**

- **Status: Missing.** Ink or laser on aluminium foil: mirror-like ground, embossed pocket pattern
  behind the text, glare that moves with foil wrinkles.
- **Method.** L0: coaxial light or a flat dome, crossed polarisers; L1: glare mask (saturated
  pixels) so glare reads as "unknown", never as ink; artwork and pocket pattern suppression by a
  reference background learned from good parts (7.9); polarity mixed.
- **Decide.** A character under glare is Unsure (NG) unless a second exposure (multi-exposure,
  L0) reads it. **Effort** 1.5 pw.

### 7.6 Text print quality grade — **NEW** as a grade

- **Purpose.** A repeatable grade of the print itself (as ISO/IEC 15415 grades a code), for printer
  maintenance and audits. **Status: Partial**: own per-character metrics (contrast, ink, pitch,
  position) with learned limits; no grade scale.
- **Best in class.** No numeric grading standard for OCV print is published (research); OCVMax uses
  contrast and stroke width tolerance; ISO 1831 defines print quality tolerances for OCR fonts
  [S] (listing above); Videojet [US10628934](https://patents.google.com/patent/US10628934) inspects
  print per technology in the printer.
- **Method (own).** Per character: contrast (grey levels), stroke width against reference (growth
  / shrink %), coverage (ink in reference strokes), extraneous ink (ink outside reference strokes),
  dot completeness (dots present / raster dots, dot print), edge raggedness, position and pitch.
  Each mapped to grade A-F by thresholds set per print type from good prints (A = within 1 sigma,
  C = within limits, F = fails). Line grade = lowest character grade. Calibrated on 10 re-shots of
  the same print: repeatable within one grade step.
- **Outputs.** Grade per character and line, trend chart per shift, PLC register with the line
  grade, report in the grade report PDF (reuses `GradeReport.cs`).
- **Learn.** Grade thresholds from the first 30 good parts; drift alarms when the shift mean drops
  a grade ("clean the nozzles", `explain.ocr.dots_missing`).
- **Effort** 2 pw.

### 7.7 Curved text on caps (polar unwrap) — **NEW**

- **Purpose.** Read text printed in an arc on a cap top or around a round cap side or bottle
  shoulder. **Status: Missing** (synthetic `_bend` only; spec 0.11 polar unwrap exists as a base
  tool, not built: no `warpPolar` in engine or dotnet).
- **Best in class.** HALCON `polar_trans_image_ext`: an annular arc (centre, start and end angle,
  start and end radius) mapped to a rectangle of given size, bilinear or nearest
  ([page](https://www.mvtec.com/doc/halcon/2505/en/polar_trans_image_ext.html)). OCVMax per-character
  rotation and shear for curved surfaces (page above).
- **Layers.**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L2 | Find cap | Circle fit on the cap rim (Tool 1 / 2 edge tools) | `Centre`: Fixed / Found; Found | Centre, radius |
| L1 | Unwrap top text | Polar unwrap of the ring between r1 and r2 (OpenCV `warpPolar`, bilinear), text along the circle becomes a straight line; arc start from the read itself (try the whole ring, cut at the widest gap) | `Inner / outer radius` (taught), `Direction` CW / CCW, `Text side` outer / inner (upside down) | Straight ring image |
| L1 | Unwrap side text | Cylinder unwrap: x' = R asin(x / R) from the fitted cap edges; 3 cameras at 120 deg or a turning station for 360 deg | `Radius` (found), `Cameras` 1-4 | Flat image |
| L3 | Read | 7.1 / 7.2 / 7.3 on the unwrapped line | - | Text |

- **Corrections.** Centre error (refine centre by maximising line straightness), radius change,
  foreshortening near the cylinder edge (only the middle 120 deg per camera trusted), rotation of
  the cap (any angle, found from the read).
- **Decide and safety.** Text spanning the seam between two cameras must be read whole from one
  camera or joined by overlap; otherwise NG.
- **Accuracy target.** >= 99.5 % on real cap images. **Speed** +3 ms. **Hardware** ring light for
  cap tops; for sides, 3 cameras or a rotating station. **Effort** 2.5 pw (polar base tool 0.11 is
  in chapter 04a; this is the text side).

### 7.8 Indian scripts (Hindi and others) — **NEW** as a tool option, later

- **Purpose.** Read and check text in Devanagari and the other scripts of
  [LOCALISATION.md](../LOCALISATION.md) (Hindi, Marathi; Gujarati, Tamil, Telugu, Kannada, Malayalam,
  Bengali / Assamese, Gurmukhi, Odia; Urdu Perso-Arabic). **Status: Missing**: Latin charset only;
  format.cpp recognises Devanagari digits. Requirement DL-13: Latin and Devanagari in P2, eight more
  in P4, each >= 98 % characters on its printed-label set.
- **Best in class.** IndicSTR12 (IIIT Hyderabad, ICDAR 2023): 12 Indian languages, over 27,000 real
  word images plus synthetic for 13 languages, baselines PARSeq, CRNN, STARNet; licence CC
  BY-NC-ND 4.0, so usable for measuring only, not for training a commercial model without
  permission ([arXiv 2403.08007](https://arxiv.org/abs/2403.08007v1)). PaddleOCR ships multilingual
  recognisers (licence check, spec 7.1).
- **Method.** Unicode output in grapheme clusters (a conjunct such as क्ष is one unit for scoring and
  boxes); synthetic data from Noto fonts (already embedded for the UI, OFL licence) with the print
  defects of 7.5; real data from the owner's bilingual labels; separate model per script family,
  chosen by the job's `Script` option (spec 7.1); format fields with native digits; OCV against
  expected text in that script (forced alignment works on any charset). Urdu needs right-to-left
  line order and joined letters: last.
- **Decide.** Same rules; a grapheme cluster is one character for scores.
- **Accuracy target.** >= 98 % characters (DL-13), >= 99.5 % on dates and numbers. **Effort** 8 pw
  for the first script (pipeline, Devanagari data and tests), 2 pw per further script. **Phase** P4
  (Devanagari may be pulled into P2 if a customer needs it).

### 7.9 Corrections for text (shared L1)

Every error source, how the text tools correct or tolerate it, and whether it is automatic.

| Error source | Correction or tolerance | Where | Automatic | Status |
| --- | --- | --- | --- | --- |
| Uneven illumination | Background flattening (Global / Local), percentile stretch | 7.0 | Yes (auto-tuned mode) | Partial (stretch have) |
| Low contrast | Mono12, top-hat strokes, contrast limits learned | L0, 7.0 | Yes | Partial |
| Polarity | Both scored; mixed per character for foil / PET | 7.0 | Yes | Have / mixed Missing |
| Mirror | Mirror flag for text read through film or from the inside of a bottle | 7.0 | Taught | Missing (0.25 pw) |
| Rotation | Search +-max angle, 180 flip | 7.0 | Yes | Have |
| Skew (line angle) | Row-profile sharpness | 7.0 | Yes | Have |
| Slant (italic, product skew) | Shear search, per-character shear (7.3.1) | 7.0, 7.3.1 | Yes | Have / per-char Missing |
| Perspective | Homography from the fixture or 4 taught corners, then straighten | 7.0, 04a 0.12 | From calibration | Missing |
| Curvature | Baseline curve resample (bow), polar and cylinder unwrap (7.7) | 7.0, 7.7 | Taught type, found centre | Missing |
| Scale change | Capital height estimate (have); per-character scale (7.3.1) | 7.0 | Yes | Have / Partial |
| Character spacing tight | `Tight spacing` (HALCON; `vs_ocr_set_tight_spacing`, PR #160), bridge cut, CTC needs no cut | 7.5.1, 7.2 | Set (not learned yet) | Partial |
| Character spacing wide / letter-spaced | Word-gap rule learned from the font, layout prior | 7.0, 7.4.6 | Learned | Have (word gap) |
| Proportional spacing | CTC reader needs no pitch; taught font cuts by ink gaps | 7.1, 7.2 | Yes | Have |
| Dot gaps | Closing by dot size, dot lattice, join strength | 7.5.1 | Learned | Have |
| Missing dots | Lattice scoring, grade counts them | 7.5.1, 7.6 | Yes | Have / grade Missing |
| Stroke width growth / shrink | Learned ink limits; several prints per taught character; growth % in grade | 7.3, 7.6 | Learned | Partial |
| Smudge / smear | Ink above limit = Smeared; extraneous ink in grade | 7.3, 7.6 | Learned | Have |
| Blur | Strobe and exposure (L0); model trained on blur; deblur with measured PSF only for reading, never for grading | L0, 7.1 | Yes | Partial |
| Background texture | Top-hat at letter size; texture-not-text stops reading (taught font) | 7.0, 7.2 | Yes | Have |
| Printed artwork behind text **NEW** | Learn the background from good parts with the text masked (median of aligned good images), subtract before reading; AI finder for scenes; Cognex [US8971663](https://patents.google.com/patent/US8971663) synthetic golden template for layered print (layer masks, poses, blended intensities; active to about 2032; adopted, owner's statement of 2026-10-30, legal clearance) | 7.0 | Learned | Missing (1 pw) |
| Partial occlusion (flap, tape, finger, label edge) | Never guessed: cut-short and missing characters are NG; the rebuild check flags partial characters | 7.0, 7.1.2 | Yes | Have / rebuild Missing |
| Glare | Saturation mask, polariser, multi-exposure | L0, 7.5.6 | Yes | Missing |
| Temperature / vibration | Learned position drift trend; refit fixture | 7.3, 7.6 | Yes | Partial |
| Timing jitter (part position) | Fixture plus allow shift | 7.3 | Learned | Have |
| Print growth over a run (ribbon, ink viscosity) | Drift trend on ink and contrast, grade alarm | 7.6 | Yes | Missing |

### 7.10 Model training pipeline

- **Purpose.** Train, calibrate and accept every text model so that accuracy rises without ever
  letting a wrong text pass. **Status: Partial**: `ml/ocr/synth_text.py` (laser, TTO, CIJ, emboss;
  faint, textured, bowed), `synth_scene.py`, `train_ocr.py` (synthetic plus real crops, gate: real
  held-out accuracy not lower), `label_real.py`, `calibrate.py` (T = 1.5 chosen for line safety),
  `eval_ocr.py` (free read, check, random and look-alike changes).
- **Best in class.** HALCON 25.11 normalises CTC loss and gradients by target length and adds
  `zero_infinity` to drop infinite-loss samples, giving better balance between short and long
  lines (release notes above). SynthText-style rendered data [S]
  ([arXiv 1604.06646](https://arxiv.org/abs/1604.06646)). Calibration at word level (Slossberg).
- **Steps.**

| Step | What | Detail | Effort |
| --- | --- | --- | --- |
| 1 Data: synthetic | Extend synth_text | Dot rasters 5x5 / 5x7 / 7x9 / twin and three line; TIJ grain and nozzle columns; foil glare and pocket pattern; laser on PET with polarity flips; artwork behind text; occlusion; per-character jitter; printer fonts from 7.3.2; Indian scripts later | 1.5 pw |
| 2 Data: real | Owner's images in VS_IMAGES, labelled with `label_real.py`; per print type >= 2,000 lines | Customer data stays out of the repo | owner + 0.5 pw |
| 3 Data: from the line **NEW** | Auto-labelling: every OCV pass with expected text from PLC or job is a labelled line for free; NG lines go to a review queue where an operator confirms the true print; only confirmed lines train | Like ml/autolabel.py for codes | 1 pw |
| 4 Loss | Length-normalised CTC with zero_infinity (HALCON method); class weights for rare characters (rupee sign, `&`) | | 0.25 pw |
| 5 Models | Print-type models (7.1.1), reader B (7.1.2), rebuild auto-encoders | | in 7.1.x |
| 6 Calibrate | Per print type: temperature for the score; margin threshold M for OCV (7.3.3) chosen on the validation safety sets, never on test sets | | 0.5 pw |
| 7 Test-time checks | Stability under scale and shear, agreement; measured as part of the gate | | in 7.1 |
| 8 Gate | Accept a new model only if: characters on every real set not lower; safety sets 0 accepted (random, look-alike, deletion, insertion, transposition, date +-1 day, neighbouring lot); true-text pass not lower; speed within budget | Extends eval_ocr.py | 1 pw |
| 9 Ship | ONNX, signed in the Vision Package; model version in every result and audit record | | 0.25 pw |
| 10 Bench | `ocr_bench` sets per print type in `truth/`, run like `batch_check.py` when engine/src/ocr or the model changes | | 1.5 pw |

### 7.11 Teach UX for text

- **Purpose.** An engineer sets up Read text or Check text in about three minutes with no expert
  step. **Status: Partial**: VisionStudio Teach page (font teach line by line, unsure characters
  only, Teach this character, rendered font, formats proposed; notes
  [ocr-teach-ux](../../changes/2026-10-30-claude-ocr-teach-ux.md),
  [ocv-rendered-font](../../changes/2026-10-30-claude-ocv-rendered-font-a40ddb.md)); the Avalonia
  Studio shows results only (Hybrid 3 tool editing pending,
  [studio-ocv-addon](../../changes/2026-10-30-claude-studio-ocv-addon.md)).
- **Best in class (screens described in words).** Cognex OCRMax (In-Sight): a region box on the
  image, a Train tab where each segmented character gets a typed label, Auto-Tune over a set of
  loaded images, and a Field String editor with per-position types; OCVMax: a font tab (load a font
  file or extract from image), the expected string, DOF checkboxes and a tuning step
  ([OCRMax](https://docs.cognex.com/is_611/web/EN/ise/Content/Reference/OCRMax.htm),
  [OCVMax](https://docs.cognex.com/vpro_930sr1/web/help/html/c2da0f57-4fd9-4938-9a5e-14f61d0edb9d.htm)).
- **Flow (target).**
  1. Auto-find shows "Check this text?" on each text block (requirements section 16).
  2. Tap: the tool reads with the AI, shows each character in a box, sure ones filled, unsure ones
     empty; the engineer confirms or types.
  3. The tool proposes per line: role (MFD, EXP, LOT, MRP, serial, free), format, source of the
     expected value (job fixed / PLC / ERP / code on the pack) and shows the rules it will apply in
     plain words ("Expiry must be 24 months after MFG").
  4. Auto-tune runs on the taught image(s) with the known text (US9317767 method): print type,
     polarity, join, normalisation; shows the weakest character score.
  5. "Learn good parts": grab or load 10-30 good parts; limits learned; the false-reject preview
     shows how many of them would fail at the current strictness.
  6. "Try a misprint": the tool makes altered copies of the taught text (look-alikes, date +1) by
     drawing them, and shows that each fails; setup is refused if one passes.
  7. Optional "Teach this product" (7.3.4), with the gate result shown.
- **Effort** 2 pw (Avalonia screens after Hybrid 3, false-reject preview, misprint try).

### 7.12 The OCV false-reject problem (true laser text passes 69 %)

**What the number is.** 69.0 % is the share of 400 synthetic laser lines whose true text passes
Check text with min quality 60; on the same set the reader gets 92.6 % of characters right, 22 right
reads score below 60, and 0 of 796 altered texts are accepted (ocr-ai-reader note). On whole
synthetic laser images 101 / 200 good prints pass (TEST-REPORT). On the 389 real laser lines the
check passes 100 %. So the false rejects are real on hard (synthetic) print and not yet seen on the
one real product.

**Why.** A line passes only when its weakest character scores >= 60. If one character is unsure
with probability q, a 10-character line passes with (1 - q)^10; 69 % means q is about 3.6 % per
character (0.69^(1/10) = 0.964). Causes, in order of size:

| Cause | Evidence | Fix | Section |
| --- | --- | --- | --- |
| Score is the absolute recogniser probability after T = 1.5, chosen for safety | 22 right reads below 60 on laser; calibration note | Judge identity by the margin over the best rival, not absolute probability | 7.3.3 |
| One model for every print type; laser strokes thin and faint | laser 92.6 % vs TTO 98.0 % | Laser / solid model; real laser data | 7.1.1, 7.10 |
| Whole image: lines lost or cut by the finder | 101 / 200 vs 69 % on lines | Finder fixes from #142's cause table, layout prior | 7.0, 7.4.6 |
| No per-character freedom | Pitch and scale jitter scored as faults | Per-character shift, scale, shear | 7.3.1 |
| Limits learned from synthetic or none | Print limits never run on real prints (ocv-print-quality Pending) | Learn on the line's own good parts; product teach | 7.3.4 |

**Measured 2026-10-31 (PR #166).** Margin as a pass route and per-letter freedom on the AI read were
built in a prototype and measured with every look-alike at every place (`eval_ocr --every 6`): neither
raises the true-text pass safely by more than about 1 point (7.3.1, 7.3.3). The same exhaustive set
shows today's check accepting look-alikes on the synthetic sets that the synthetic fonts draw alike
(laser 8 / 8,782, TTO 6 / 9,376, CIJ 3 / 9,275, embossed 5 / 9,027; mostly O / 0 and I / l / 1) and
none on real Harpic lines (0 / 10,182). The false rejects are the recogniser's: the model step (laser
model, real data) is the one that moves this number.

**Plan and expected effect (estimates, to be measured).** Margin score first (2 pw): most unsure
right reads have a clear lead over the rival, so q should fall several times. Then solid model and
real data (3 pw), per-character freedom (3 pw), product teach (2 pw). Target: true-text pass >= 99.5
% (q <= 0.05 % per character) on real laser, with 0 altered texts accepted. Every step is gated on
the safety sets: a change that accepts one altered text is not merged.

---

## 5. Decide and safety rules for all text tools (L4)

| Rule | Applies to |
| --- | --- |
| Doubt is NG: unsure character, low margin, unstable read, reader disagreement, time-out, cut-short line, glare over a character | 7.1-7.3 |
| A format constraint chooses but never vouches; a read is never corrected | 7.1, 7.2, 7.4 |
| Expected text missing, empty, stale (wrong TriggerID) or from a failed tool: NG | 7.3 |
| A date rule with an unchecked clock: NG | 7.4.4 |
| Teaching never fills in the expected character for a failed one; a character looking like another taught one is refused | 7.2, 7.11 |
| Setup refused when a drawn misprint would pass, a format needs a character the font lacks, or (dot print) a printable character is untaught | 7.11, 7.2 |
| A model is shipped only through the gate with 0 altered texts accepted | 7.10 |
| Text equal to a code's field (lot, expiry) is checked against the decoded code when both exist | 7.3, 7.4.2 |

---

## 6. How accuracy is measured

| Set | Content | Count per print type | Metric | Pass rule |
| --- | --- | --- | --- | --- |
| Real read sets | Owner's images in VS_IMAGES: laser, TTO, CIJ, TIJ, embossed, foil, caps; each line labelled | >= 2,000 lines, >= 20,000 characters | Characters right (spaces ignored), lines exact | >= 99.5 % characters without teach (a 20,000-character set resolves about 0.05 %) |
| Product sets | One product per print type, good prints from the line | >= 600 good parts | OCV true-text pass | >= 99.5 % (0 fails in 600, or <= 1 in 949) |
| Safety sets | Each good real line altered: random character, look-alike (0/O/D, 1/I/7, 5/S, 8/B, 2/Z, 6/G), deletion, insertion, transposition, date +-1 day, neighbouring lot, wrong MRP | >= 3,000 altered lines (30,000 over the bench) | Accepted altered texts | 0 (0 in 30,000 bounds the rate below 0.01 % at 95 %) |
| Real misprints | Captured NG prints from lines (nozzle out, smear, ribbon wrinkle) | all available | Detected | 100 % |
| Synthetic sets | `eval_ocr --make --n 400 --seed 900`, `synth_scene --seed 31` | 400 lines, 150 images | as today | No regression; trend only |
| Rule set | Generated dates, MRP, lots, serials | >= 2,000 cases | Right decision | 100 % |
| Speed | Same images on T1 and T2 | 200 images | Median and 99th percentile ms | Within section 1 targets at the 99th percentile |

The bench runs only when engine/src/ocr, ml/ocr models or truth change (AGENTS.md rule 11).

---

## 7. Hardware for text

| Need | Minimum | Good | Why |
| --- | --- | --- | --- |
| Capital height | 20 px | 30-40 px | Reader works at 32 px line height; chapter 05 |
| Stroke or dot width | 2 px | 3 px | OCRMax recommends stroke >= 2 px; dots need >= 3 px to count |
| Exposure | motion <= 0.3 stroke | strobe 50-150 us | At 225 ppm and 100 mm pitch the pack moves about 375 mm/s; a 0.2 mm stroke allows about 160 us |
| Bit depth | Mono8 | Mono12 on faint laser and foil | Low-contrast laser |
| Light, CIJ / TTO / TIJ on carton | Diffuse bar | Dome | Even ground, no glare on varnish |
| Light, laser on PET / foil | Coaxial | Coaxial plus crossed polariser | Removes specular flare |
| Light, embossed | One low-angle bar | 4-segment low-angle ring, strobed per frame | Photometric stereo (7.5.5) |
| Light, caps | Ring | Ring plus 3 side cameras for side text | 7.7 |
| PC | T1 i5/i7 with OpenVINO | T2 with RTX 4060 class when reader B, print-type Auto and two cameras exceed the cycle | 7.1 |

Purchases are ranked in [05-HARDWARE.md](05-HARDWARE.md).

---

## 8. Outputs to PLC and audit (L6)

| Output | Form | Notes |
| --- | --- | --- |
| Text OK / NG bit per tool | Coil | As other tools |
| Reason code | Register | No text / unsure / format / date / wrong / missing / extra / broken / faint / smeared / position / cut short / no expected / disagreement |
| Read text | String registers (as the PLC text block, registers 500-627, in reverse direction) | For the host to log |
| Fields | MFD and EXP as YYYYMMDD, MRP in paise | 32-bit registers |
| Print grade | Register A-F (1-6) | 7.6 |
| NG image | Stored with character overlays | Existing NG image store |
| Audit | Model and font version, expected text source, limits version | Part 11 trail (chapter 04d) |

---

## 9. Build plan in order

| Order | Work | pw | Depends on |
| --- | --- | --- | --- |
| 0 | PR #142 merged: every real sample on the Mac at 100 %, cause table | (in #142) | - |
| 1 | Verification margin score (7.3.3) and its calibration | 2 | 0 |
| 2 | Real data: owner's CIJ, TTO, TIJ, embossed, foil, cap images labelled; bench sets in truth | 2 | owner images |
| 3 | Length-normalised CTC, print-type models, gate with safety sets (7.10, 7.1.1) | 4.5 | 2 |
| 4 | OCV per-character freedom (7.3.1) | 3 | 1 |
| 5 | Second recogniser, agreement, rebuild check (7.1.2) | 4 | 3 |
| 6 | Finder: flattening, baseline curve, DB second finder, layout prior (7.0, 7.4.6) | 4.5 | 0 |
| 7 | CIJ parameters and rasters, TIJ auto, laser mixed polarity, TTO data (7.5.1-7.5.4) | 6 | 2, 3 |
| 8 | Product teach (7.3.4) and printer fonts (7.3.2) | 4 | 3 |
| 9 | Formats: Indian month names, MRP, Julian / week, serials, clock check (7.4.x) | 5 | - |
| 10 | Known-text auto-tune and teach UX in Avalonia (7.2, 7.11) | 3 | Hybrid 3 |
| 11 | OpenVINO path, stability check, lexicon (7.1) | 2 | - |
| 12 | Print quality grade (7.6), artwork subtraction, glare mask (7.9) | 4 | 4 |
| 13 | Embossed photometric stereo, foil mode (7.5.5, 7.5.6) | 4.5 | 4-segment light, coaxial light |
| 14 | Curved text on caps (7.7), OCR-A / B / SEMI (7.2.1) | 3.5 | 0.11 polar |
| 15 | Indian scripts: Devanagari first (7.8) | 8 (+2 per script) | 3 |

Steps 1-9 are P2 (Identify) and reach the section 13 target on the print types the owner runs today;
steps 10-14 complete P3; step 15 is P4.

---

## Not opened / limits

- Opened in this session: Cognex OCRMax and OCVMax help pages; HALCON `set_text_model_param`,
  `polar_trans_image_ext`, `photometric_stereo`, 25.11 release notes; Google Patents US9317767
  (abstract and status; claims not read); arXiv CRNN, PARSeq, Slossberg, IndicSTR12 abstracts;
  iPleaders and S.S. Rana Legal Metrology summaries; Agile Regulatory CDSCO labelling blog; Keyence
  lighting guide; Unicode CLDR Gregorian chart (first part only).
- Opened by the earlier OCR survey of 2026-10-30 (scratchpad research-ocr.md), not re-opened here:
  HALCON `create_text_model_reader`, `create_ocv_proj`, Deep OCR page; Keyence IV4 page; patents
  US10068153, US10810465, US9384423, US10628934, US5212741, US5357581, US4872024, US8971663
  (from INDUSTRY-METHODS section 16); Mor and Wolf arXiv 1805.11161.
- Search snippet only [S]: Cognex ViDi EL Read (HTTP 429 earlier); SynthText; ISO 1073-2 and ISO
  1831 (iso.org returned 403; facts from catalogue listings); Videojet print guides (PDF not
  readable); Drugs Rules 1945 Rule 96 wording ("Expiry date", last day of month): from snippets of
  secondary sources, no CDSCO or Gazette text opened.
- Legal Metrology rule numbers differ between summaries (MRP as 6(1)(c) or 6(1)(e)); the Gazette
  text was not opened. Chapter 03 should confirm before MRP rules ship.
- No vendor publishes character accuracy for dot-matrix, inkjet or embossed date codes; our targets
  rest on our own bench, which today has real images of one print type (laser, one product).
- The expected effect of the margin score (7.3.3) on the 69 % is an estimate from the per-character
  arithmetic, not a measurement.
- Hindi abbreviated month forms and the other Indian languages' month names were not read from CLDR
  in full (the page was cut); they are to be generated from CLDR data at build time.
- PaddleOCR and IndicSTR12 licences: IndicSTR12 is CC BY-NC-ND 4.0 (evaluation only); PaddleOCR model
  licences not re-checked here.
- No printer-maker font file (Domino, Videojet, Markem, EuroCod) was found publicly; they must come
  from the makers or the owner's printers.
