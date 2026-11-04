# Study 04d: Tools 8-12 and the platform — Inspect, Colour, AI deep learning, and what every job uses

2026-10-30. Part of the study in [00-INDEX.md](00-INDEX.md). This chapter is the complete list of
what must be built for defect inspection (Tool 8), colour (Tool 9), deep learning (Tool 10) and
the platform tools every job uses (Teach, auto-find / learn / tune / tolerance / light, learning
while running, logic, scripting, sequencing, SPC, multi-camera, PLC, outputs, logging, audit and
e-signatures, roles, job versions, the benchmark and the NG review loop; Tools 11 and 12 of
[TOOL-SPECIFICATIONS.md](../TOOL-SPECIFICATIONS.md) sit here). It extends, and does not repeat:
[TOOL-SPECIFICATIONS.md](../TOOL-SPECIFICATIONS.md) Tools 8-12 (TS),
[AI-DEEP-LEARNING.md](../AI-DEEP-LEARNING.md) (ADL),
[RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md](../RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md) (RDV),
[INDUSTRY-METHODS.md](../INDUSTRY-METHODS.md) section 15-16 (IM),
[UI-UX-SPEC.md](../UI-UX-SPEC.md) (UX), [PLC-INTERFACE.md](../PLC-INTERFACE.md) (PLC),
[NG-REVIEW.md](../NG-REVIEW.md) and the requirements
([REQUIREMENTS-VISION-SYSTEM.md](../../REQUIREMENTS-VISION-SYSTEM.md), sections 5, 8, 11-13, 16).
Hardware choices are in [05-HARDWARE.md](05-HARDWARE.md); patents in full in
[02-PATENTS.md](02-PATENTS.md); Part 11 and Annex 11 in
[03-STANDARDS-AND-DOCUMENTS.md](03-STANDARDS-AND-DOCUMENTS.md).

## 0. How to read this chapter

- **Status words** for our software: **Have** (built on main), **Partial** (some of it, named),
  **Missing**. Open pull requests are named but count as Missing until merged.
- **Layers** L0-L6 as in [00-INDEX.md](00-INDEX.md). "Auto" in an options column means auto-learn
  or auto-tune sets it; "Ess." = Essential (shown by default, at most 5 per tool), "Adv." =
  Advanced.
- **Effort** in person-weeks (pw): one experienced developer with an AI assistant, including the
  tool's tests, its Studio properties page and its explain strings in English.
- **Phase**: P1-P5 of requirements section 15; ROADMAP step 5 (classical tools) is P4, step 6 (AI)
  is P3. The owner's build order (requirements 17.4) puts codes and text first, so every phase
  below starts after the open reading work.
- **Sources**: [O] = page opened and read (in this session, or by the research sessions of
  2026-10-30 whose notes are the raw research files `research-ai.md` and `research-geometry.md`);
  [S] = seen only as a search result; [I] = our own inference, not checked. Vendor numbers are
  marketing unless a test is described. Patented methods are written as methods to adopt, with the
  patent cited (owner's statement of 2026-10-30, legal clearance).
- **T1 / T2**: hardware tiers of requirements section 11 (T1 = i5/i7 no GPU, OpenVINO on the iGPU;
  T2 = i7 + RTX 4060 / A2000 class). The owner's line PC today is a fanless i7 without a GPU
  (requirements section 8), so T1 numbers are the ones that hold on the line now.

### 0.1 What "almost 100 %" means for defects, and how many images prove it

Requirements section 13: false reject ≤ 0.5 % and escape ≤ 0.1 % after auto-tolerance; the brief
asks for 0 escapes on the owner's sets. A number is only proven with enough parts:

| Claim | Parts needed with 0 failures (95 % confidence, "rule of three": upper bound ≈ 3 / N) | With 1 failure allowed |
| --- | --- | --- |
| Escape ≤ 1 % | 300 bad parts | 474 |
| Escape ≤ 0.1 % | 3,000 bad parts | 4,742 |
| False reject ≤ 0.5 % | 600 good parts | 949 |
| False reject ≤ 0.1 % | 3,000 good parts | 4,742 |

(Exact one-sided binomial bounds; our arithmetic [I].) Real bad parts are rare, so every defect
tool is accepted on three sets, reported separately and never mixed:

1. **Real bad parts** collected from the line and the NG review loop (P.20); minimum 50 per defect
   type before a tool is called "proven" for that type.
2. **Staged bad parts**: good parts damaged on purpose by the owner's staff (torn label, cap left
   loose, tablet removed, smudged print), at least 300 per tool.
3. **Synthetic bad images** (10.11), at least 3,000 per tool. They stress-test the tool and are
   reported apart; they never count toward the escape bound.

The escape bound (≤ 0.1 %) is claimed only on real plus staged bad parts (sets 1 and 2 together:
3,000 with 0 escapes, or 4,742 with at most 1, as the table above). Until those exist the tool's escape
rate is written as "not yet proven", whatever the synthetic recall.

Good sets: at least 600 good parts per product from a full shift (all light and print variation),
never the images the tool was taught on. **Pass rule for every defect tool:** 0 escapes on all
three bad sets, false reject ≤ 0.5 % (aim 0.1 %) on the good set, at the line time on T1 or the
tier written in the job.

## 1. Summary table

Status "Missing" with a PR number means the work is open in that pull request. Speed is the tool's
own time for its typical region (TS cross-tool table), T1 / T2. "Escape / FR" = escape rate /
false-reject rate targets on the sets of 0.1.

| ID | Name (operator words) | Layers | Status today | Accuracy target | Speed T1 / T2 (ms) | Effort (pw) | Phase | Depends on |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 8.0 | Inspect common core **NEW** ("Check defects") | L1-L6 | Missing | as each tool | ≤ 1 overhead | 2 | P4 | 1.1/1.2 Locate, 4.1 Blob |
| 8.1 | Presence / absence ("Is it there?") | L2-L5 | Missing | 0 escape / FR ≤ 0.05 % | ≤ 1 / ≤ 1 | 1.5 | P4 | 8.0 |
| 8.2 | Golden template and variation model ("Compare to good") | L1-L5 | Missing | 0 escape for defects ≥ min size / FR ≤ 0.5 % | ≤ 15 / ≤ 6 (2 MP) | 4 | P4 | 8.0, 3.2 |
| 8.3 | Edge flaw ("Check edge") | L2-L5 | Missing | 0 escape for flaws ≥ 2 px deep / FR ≤ 0.3 % | ≤ 3 / ≤ 3 | 2.5 | P4 | 2.1 caliper, 2.2/2.3 fit |
| 8.4 | Surface defect, classical ("Check surface") | L1-L5 | Missing | recall ≥ 99.9 % on scratches ≥ 3 × 50 px / FR ≤ 0.5 % | ≤ 20 / ≤ 10 (5 MP) | 3.5 | P4 | 8.0 |
| 8.4b | Surface defect, AI mode ("Check surface (AI)") | L3-L5 | Missing (study only) | as 10.1 | as 10.1 | 1 | P3 | 10.1 |
| 8.5 | Bead / glue ("Check glue line") | L2-L5 | Missing | 0 escape for gaps ≥ 1 mm / FR ≤ 0.3 % | ≤ 10 / ≤ 5 | 2.5 | P4 | 2.1, 3.4 scale |
| 8.6 | Label inspection ("Check label") | L1-L5 | Partial (codes and text on labels only) | 0 escape on missing / wrong / skewed / damaged label / FR ≤ 0.5 % | ≤ 25 / ≤ 12 | 4 | P4 | 8.2, 2.2, Tools 5, 7 |
| 8.7 | Cap and seal ("Check cap") | L0-L5 | Missing | 0 escape: missing, high > 0.5 mm, tilt > 2°, band broken / FR ≤ 0.3 % | ≤ 10 / ≤ 5 | 3 | P4 | 2.2, 2.3, 0.11 polar |
| 8.8 | Blister and foil **NEW** ("Check blister") | L0-L5 | Missing | 0 escape: empty, broken, wrong colour, foreign tablet; foil tear / FR ≤ 0.3 % | ≤ 30 / ≤ 15 per blister card | 4 | P4 | 8.1, 8.2, Tool 9, 4.1 |
| 8.9 | Print registration **NEW** ("Check print position") | L2-L5 | Missing | offset 0.1 mm 3σ; 0 escape over limit | ≤ 10 / ≤ 5 | 2 | P4 | 1.2, 3.4 |
| 8.10 | Contamination / foreign particle **NEW** ("Check for dirt") | L0-L5 | Missing | 0 escape for particles ≥ 0.3 mm / FR ≤ 0.5 % | ≤ 15 / ≤ 8 | 2.5 | P4 | 8.2, 8.4 |
| 8.11 | Fill level **NEW** ("Check fill") | L0-L5 | Missing | ± 0.5 mm 3σ; 0 escape over limit | ≤ 5 / ≤ 3 | 1.5 | P4 | 2.1 |
| 9.c | Colour image pipeline **NEW** (Bayer, colour image type in engine) | L0-L1 | Missing (engine is grey only) | bit-exact vs reference demosaic | ≤ 8 / ≤ 3 (20 MP) | 2 | P4 | camera |
| 9.0 | White reference ("Correct colour") | L1, L5 | Missing | residual ΔE00 ≤ 1.0 on 24 patches | ≤ 1 | 2 | P4 | 9.c |
| 9.1 | Colour match ("Check colour") | L3-L4 | Missing | repeatability ΔE00 ≤ 0.3 (3σ); 0 wrong at limit ≥ 2 | ≤ 1 | 1 | P4 | 9.0 |
| 9.2 | Colour presence ("Is the colour there?") | L3-L4 | Missing | area ± 1 % | ≤ 5 / ≤ 3 | 1 | P4 | 9.0 |
| 9.3 | Colour segmentation ("Find colour areas") | L3 | Missing | pixel agreement ≥ 99 % vs labelled masks | ≤ 10 / ≤ 5 | 1.5 | P4 | 9.0 |
| 9.4 | Colour sorting ("Sort by colour") | L3-L4 | Missing | 100 % right class, "not sure" when margin < limit | ≤ 1 | 1 | P4 | 9.0 |
| 9.5 | ΔE measurement ("Measure colour") | L3, L6 | Missing | matches the Sharma test data to 1e-4 | ≤ 1 | 0.5 | P4 | 9.0 |
| 9.6 | Best grey from colour **NEW** ("Use colour for contrast") | L1 | Missing | contrast gain ≥ 1.5× on coloured print | ≤ 3 | 1 | P4 | 9.c |
| 10.1 | AI anomaly, local ("Check defects (AI)") | L3-L5 | Missing (`ml/experiments/anomaly*.py` not shipped) | 0 escape / FR ≤ 0.5 % on owner sets; image AUROC ≥ 99 % | ≤ 150 / ≤ 20 (5 MP) | 6 | P3 | 10.16, 10.9 |
| 10.2 | AI anomaly, logical / global **NEW** ("Check arrangement") | L3-L5 | Partial (slot map for codes) | 0 escape on count / position / order errors | ≤ 50 / ≤ 10 | 4 | P3 | 10.1, slots |
| 10.3 | Classification with "Not sure" ("Sort by picture") | L3-L5 | Missing | ≥ 99.5 % right, 0 wrong above confidence limit; ECE ≤ 5 % | ≤ 30 / ≤ 5 | 3 | P3 | 10.13, 10.14 |
| 10.4 | Object detection ("Find and count items") | L2-L5 | Missing | 100 % exact count on test set; box IoU ≥ 0.7 | not on T1 / ≤ 15 | 4 | P3 | 10.16 |
| 10.5 | Segmentation ("Find defect area") | L3-L5 | Missing | 0 escape for defects ≥ min area; IoU ≥ 0.8 | ≤ 200 / ≤ 20 | 4 | P3 | 10.16, 4.1 |
| 10.6 | Dense counting **NEW** ("Count many") | L3-L4 | Missing | count error < 1 % at ≥ 500 items (DL-16); exact ≤ 100 items | ≤ 100 / ≤ 15 | 2.5 | P3 | 10.4 |
| 10.7 | AI locator ("Find the part (AI)") | L2, L5 | Partial (CodeNet for codes) | 100 % find; 0.05 px / 0.05° after refine | ≤ 80 / ≤ 25 | 4 | P3 | 1.2 |
| 10.8 | AI OCR fine-tune ("Teach the reader") | L3, L5 | Partial (`ml/ocr` scripts) | ≥ 99.5 % characters on owner prints | as 7.1 | 3 | P3 | Tool 7 |
| 10.9 | Training workflow (collect, label, train, validate, deploy) | L5 | Partial (scripts, `ml/retrain.py`) | reproducible within 1 score point | train ≤ 10 min T2 | 6 | P3 | 10.12, 10.17 |
| 10.10 | Auto-label ("Pre-label") | L5 | Partial (`ml/autolabel.py`) | ≥ 70 % labels accepted unchanged (DL-20) | n/a | 2 | P3 | 10.9 |
| 10.11 | Synthetic data ("Make test defects") | L5 | Partial (codes, text) | synthetic recall within 2 points of real | n/a | 3 | P3 | 10.9 |
| 10.12 | Model acceptance gate | L4-L5 | Partial (`ml/retrain.py`, `ModelRegistry`) | no model ships with a new escape | n/a | 2 | P3 | 10.9 |
| 10.13 | Confidence calibration | L4 | Partial (OCR temperature) | ECE ≤ 5 % (DL-02) | < 0.1 | 1 | P3 | 10.9 |
| 10.14 | Out-of-distribution "outlier" score | L4 | Missing | ≥ 95 % of ± 40 % light images flagged, ≤ 1 % normal (DL-01) | ≤ 1 | 2 | P3 | 10.13 |
| 10.15 | Drift monitor ("Watch the line") | L5-L6 | Missing | warn before first false reject in ≥ 90 % of staged drifts | off path | 3 | P3 | P.15 |
| 10.16 | Inference runtime (ONNX Runtime, OpenVINO, TensorRT, INT8/FP16, CPU fallback) | L3 | Partial (OpenCV DNN, FP32) | INT8 passes the gate; CPU = SDK bit-exact | per tool | 4 | P3 | — |
| 10.17 | Model versions, package, rollback | L5-L6 | Partial (`ModelRegistry.Promote`) | one-tap rollback ≤ 5 s; 100 % audit | n/a | 2 | P3 | P.16 |
| P.1 | Teach wizard ("New job") | L0-L6 | Partial (Studio Teach page for codes and text) | untrained user: label + date + code job ≤ 15 min | n/a | 4 | P2 | P.2-P.6 |
| P.2 | Auto-find, whole image (AS-01) | L2, L5 | Have, first version (PR #152, merged: codes and text with field types) | ≥ 99 % of codes and text blocks found and typed | ≤ 1,500 / ≤ 400 | 3 | P2 | Tools 5, 7, 10.4 |
| P.3 | Auto-learn for every tool (AS-02) | L5 | Partial (codes, text, OCV limits) | new tool passes the taught good part 100 % | ≤ 500 per tool | 4 | P2-P4 | each tool |
| P.4 | Auto-tune on OK / NG images (AS-03) | L5 | Have, first version (PR #154, merged: Locate and Check text) | 0 known bad passes; margin maximised | ≤ 10 min budget | 4 | P2 | P.18, P.19 |
| P.5 | Auto-tolerance ("Strictness") | L5 | Partial (OCV mean ± kσ) | predicted FR within 2× of measured | < 1 | 2 | P2 | P.3 |
| P.6 | Auto-light check ("Light and focus") | L0, L5 | Partial (`CalibrationSearch.cs`, `LineAdvisor.cs`) | right advice on ≥ 95 % of staged faults | ≤ 20 | 2 | P2 | — |
| P.7 | Learning while running (AS-04) | L5 | Partial (reader learned state, `TuningAgent.cs`) | never worse on golden images; 0 changes during a run | off path | 3 | P2 | 10.12, P.18 |
| P.8 | Result logic and logic blocks ("Decide") | L4 | Partial (fixed combine in runtime) | 100 % truth-table tests | ≤ 0.1 | 2.5 | P1 | — |
| P.9 | Scripting and formula ("Custom rules", "Calculate") | L4 | Missing | sandbox never stops the runtime | ≤ 2 / ≤ 0.02 | 4 | P4 | P.8 |
| P.10 | State machine / sequencing ("Sequence") | L4 | Missing | 100 % transition tests | ≤ 0.1 | 2.5 | P4 | P.8 |
| P.11 | SPC and trends ("Trends") | L5-L6 | Partial (Cp/Cpk in reports) | Western Electric rules exact | ≤ 0.1 | 2 | P4 | P.15 |
| P.12 | Multi-camera job and sync ("Add camera", "Fire together") | L0, L4 | Partial (child / parent cameras; PR #147 open: two-camera product) | 0 part mismatches in 1 million; skew ≤ 100 µs (GigE PTP) | ≤ 2 overhead | 5 | P2 / P4 | PLC |
| P.13 | PLC handshake and safety | L6 | Have (Modbus TCP, Delta DVP; link loss NG, PR #141) | 0 OK across a link loss; 0 lost results | ≤ 10 latency | 6 (other protocols) | P1 | — |
| P.14 | Outputs: OPC UA, MQTT, MES / SQL, web HMI | L6 | Partial (MQTT in plant server; view-only web) | at-least-once, 0 lost records | off path | 6 | P4 | P.15 |
| P.15 | Image and result logging | L6 | Have (NgImageStore, results stream, guard) | every part recorded; inspection never waits | off path | 2 | P1 | — |
| P.16 | Audit trail and e-signatures (21 CFR Part 11, Annex 11) | L6 | Partial (Dashboard audit page, approvals) | every GxP change recorded, signed, tamper-evident | n/a | 4 | P2 | P.17, P.18 |
| P.17 | User roles ("Sign in") | L6 | Partial (Studio roles, sign-in card) | 100 % of actions role-checked | n/a | 1.5 | P1 | — |
| P.18 | Job versioning and release | L5-L6 | Partial (`Job.cs` keeps every version) | rollback restores bit-identical job | n/a | 2 | P1 | P.16 |
| P.19 | Built-in benchmark ("Will it keep up?") | L5-L6 | Partial (`tools/bench`, not in the app) | p99 time within 10 % of the line | n/a | 3 | P1 | — |
| P.20 | NG review loop ("Review rejects") | L5-L6 | Have (NG-REVIEW, PR #71); training export partial | every NG reviewed within a shift | n/a | 2 | P2 | 10.9 |

**Total** (this chapter): about 164 pw, of which AI (10.x) about 56 pw, Inspect and Colour about
44 pw and the platform about 64 pw. With 3 developers in parallel (one AI, one classical tools, one
platform) about 14-16 calendar months, consistent with [06-ACCURACY-AND-TIMELINE.md](06-ACCURACY-AND-TIMELINE.md).

### 1.1 Best in class for this chapter, in one table

| Area | Best in class (what to learn from) | Link |
| --- | --- | --- |
| Golden template, variation model | HALCON variation model (ideal + variation image, absolute and k·σ thresholds, bright and dark separately) [O]; Cognex PatInspect (difference image, brighter / darker only) [O]; Cognex US 5,850,466 golden template indexed by pose (expired) [O] | [HALCON](https://www.mvtec.com/doc/halcon/13/en/prepare_variation_model.html), [PatInspect](https://docs.cognex.com/vpro_970/web/EN/help/html/N_Cognex_VisionPro_PatInspect.htm), [US5850466](https://patents.google.com/patent/US5850466) |
| Multi-layer print template | Cognex US 8,971,663 synthetic golden template from layer masks and poses [O] | [US8971663](https://patents.google.com/patent/US8971663) |
| Scratches | Cognex US 6,983,065 oriented band-pass bank (expired) [O]; HALCON `lines_gauss` (Steger) [O] | [US6983065](https://patents.google.com/patent/US6983065B1/en), [lines_gauss](https://www.mvtec.com/doc/halcon/13/en/lines_gauss.html) |
| Embossed / dents | Keyence LumiTrax shape + texture images from 4 directional lights [O]; HALCON `photometric_stereo` [O]; Omron FH 4-light PS [O] | [LumiTrax](https://www.keyence.com/landing/vision/lumitrax-lighting.jsp), [photometric_stereo](https://www.mvtec.com/doc/halcon/13/en/photometric_stereo.html), [Omron FH](https://industrial.omron.eu/en/products/fh-vision-system) |
| AI anomaly | Dinomaly 99.6 image AUROC MVTec AD, 98.7 VisA [O]; EfficientAD-S 98.8 / 97.5, 2.2 ms GPU [O]; PatchCore up to 99.6 [O]; MVTec AD 2 shows < 60 % AU-PRO under real shift [O] | [Dinomaly](https://arxiv.org/abs/2405.14325), [EfficientAD](https://arxiv.org/html/2303.14535), [PatchCore](https://arxiv.org/abs/2106.08265), [MVTec AD 2](https://arxiv.org/abs/2503.21622) |
| Logical anomaly | HALCON Global Context Anomaly Detection (local + global subnetworks) [O] | [HALCON doc](https://www.mvtec.com/doc/halcon/2511/en/toc_deeplearning_anomalydetection.html) |
| "Not sure" | HALCON 25.11 `fit_dl_out_of_distribution`, < 15 samples per class [O]; energy score [O]; temperature scaling [O] | [HALCON 25.11](https://www.mvtec.com/products/halcon/documentation/release-notes-2511-0), [energy](https://arxiv.org/abs/2010.03759), [Guo](https://arxiv.org/abs/1706.04599) |
| Few-image setup | Keyence VS-G auto image selector, mark-the-defect tuning, re-check of stored images after a change [O]; Landing AI visual prompting ≤ 25 images [O] | [VS-G](https://www.keyence.com/products/vision/vision-sys/vs-g/functions/), [Landing AI](https://landinglens.docs.landing.ai/visual-prompting) |
| Inference | ONNX Runtime ordered execution providers with fallback [O]; TensorRT EP engine and timing cache [O]; OpenVINO EP CPU / GPU / NPU, model cache [O]; NNCF PTQ ~300 images [O] | [ORT EPs](https://onnxruntime.ai/docs/execution-providers/), [TensorRT EP](https://onnxruntime.ai/docs/execution-providers/TensorRT-ExecutionProvider.html), [OpenVINO EP](https://onnxruntime.ai/docs/execution-providers/OpenVINO-ExecutionProvider.html), [NNCF](https://github.com/openvinotoolkit/nncf) |
| Colour | CIEDE2000 with the Sharma / Wu / Dalal test data [O] | [Sharma page](https://hajim.rochester.edu/ece/sites/gsharma/ciede2000/), [Colour difference](https://en.wikipedia.org/wiki/Color_difference) |
| Machine interface | OPC UA for Machine Vision (OPC 40100-1): state machine, recipe and result management [O] | [OPC 40100](https://reference.opcfoundation.org/MachineVision/v100/docs/8) |
| Records | 21 CFR 11.10, 11.50, 11.200 [O]; EU GMP Annex 11 clauses 9 and 14 [S] | [11.10](https://www.law.cornell.edu/cfr/text/21/11.10), [11.50](https://www.law.cornell.edu/cfr/text/21/11.50), [11.200](https://www.law.cornell.edu/cfr/text/21/11.200) |
| SPC | Western Electric run rules (NIST handbook) [O] | [NIST 6.3.2](https://www.itl.nist.gov/div898/handbook/pmc/section3/pmc32.htm) |

---

## Tool 8. Inspect — "Check defects"

Licence `VS-INS-`. Nothing of Tool 8 is built (inventory 2026-10-30). The only related work is the
per-slot anomaly study `ml/experiments/anomaly.py` / `anomaly2.py` (classical features, robust z):
at 0 % false alarms it caught 12 / 56 missing products, 0 / 56 torn labels, 0 / 56 foreign objects
and 23 / 53 other products (IM 15.3). That is the lesson for this tool: hand-picked global
features per slot do not see local damage; pixel-level comparison (8.2) and patch-level AI (10.1)
do.

### 8.0 Inspect common core (NEW) — "Check defects"

- **Purpose:** one shared engine for every Tool 8 sub-tool: fixture-aligned region, reference
  model per product, defect blob list, sensitivity slider with live preview over the image set.
  **Status:** Missing. Reuses `engine/src/fixture/locate_tool.cpp` (NCC locate) and
  `dotnet/VisionCore/Tools/LocateTool.cs` ("Find the part" moves other tools).
- **Best in class:** HALCON and VisionPro both split "align" from "compare" from "blob the
  difference" (PatInspect, variation model [O]); Keyence VS-G shows a live re-check of stored
  images when a setting moves [O].
- **Layers:**

| Layer | Step | Method | Options (default; auto; Ess./Adv.) | Output |
| --- | --- | --- | --- | --- |
| L1 | Undistort + flat field in the region | 3.2 undistort with valid mask; dark/flat per Euresys formula [O] | Flat field (taught / rolling / off; auto; Adv.) | clean region |
| L2 | Align | 1.1/1.2 fixture, sub-pixel; affine or local deformable (HALCON `find_local_deformable_model` [O]) for pouches and foil | Align mode (rigid / affine / deformable; auto from residual; Adv.) | pose, warp field |
| L3 | Compare | per sub-tool | — | defect map |
| L4 | Blob and judge | 4.1 connected components (Spaghetti, 8-way [O]); area, length, contrast, position class | Min size (px / mm²; auto; Ess.), Sensitivity (Ess.) | defect list, NG reason |
| L5 | Learn | robust median / MAD per pixel or feature; k from auto-tolerance | Strictness k (4; auto; Ess.) | reference model |
| L6 | Explain | heat map, red outline, first-defect crop | — | overlay, NG image |

- **Corrections:** lens distortion, perspective (3.2), scale (3.4), flat field, illumination drift
  (global gain/offset fit on the aligned region, automatic), rotation / skew / scale change (by the
  fixture, automatic), small deformation (warp field), noise (taught σ), timing jitter (the fixture
  absorbs position). Polarity and mirror are not corrected: a mirrored label is a defect.
- **Outputs:** `Defects[]` (type, area, length, contrast, centre, bounding box), `DefectCount`,
  `LargestArea`, `Score` 0-100, `AlignScore`, `DiffImage` (on demand).
- **Decide and safety:** an alignment score below its limit is **NG "could not line up"**, never
  "no defect found"; an inspected region partly outside the image is NG; a reference model older
  than the job version is refused at load.
- **Learn and tune:** auto-find proposes regions (label, cap, blister) from P.2; auto-learn builds
  the reference from 5-20 good parts; auto-tune sets sensitivity and min size from OK / NG; drift
  signals are mean residual and align score (10.15).
- **Accuracy and speed:** overhead ≤ 1 ms per tool besides its own compare.
- **Build plan:** (1) region + fixture binding + defect list types in the C API, 0.5 pw; (2)
  reference-model store per product in the job (JOB-FILE-FORMAT), 0.5 pw; (3) live preview over the
  image set in Studio, 1 pw. Total 2 pw.

### 8.1 Presence / absence — "Is it there?"

- **Purpose:** is a feature (leaflet, cap, tablet, label, scoop, dropper) there or not.
  **Status:** Missing. The reader's expected-count logic (`engine/src/decode.cpp`, slot map
  `engine/src/slots.cpp`) is presence by code only.
- **Best in class:** every vendor does it with the simplest measure that separates (Cognex
  In-Sight `ExtractHistogram` / blob, Keyence "area" tool) [I]; Omron FH lists "presence" among its
  defect items [O]. Nobody publishes a number; the method is trivially exact when the measure is
  chosen well.
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L1 | Normalise | divide by a reference patch (paper or case floor) beside the region | Reference patch (auto; Adv.) | brightness-free value |
| L3 | Measure | mean, edge density (Sobel energy), pattern score (1.1 NCC), pixel count above threshold, colour fraction (9.2) | Measure (Auto / one of 5; auto; Ess.) | value |
| L4 | Judge | one-sided limit; the two-class margin must be ≥ 6 σ of the good spread | Expect (Present / Absent; Ess.), Sensitivity (Ess.) | Present, Score |
| L5 | Learn | at least one absent image (real, or drawn with "Make test defects", 10.11) is required: the measure with the largest Fisher ratio; good-only teaching is not allowed for presence | — | limit |

- **Corrections:** illumination drift (reference patch, automatic), position (fixture), glare
  (saturated pixels excluded from the mean, automatic), background texture (edge density instead of
  mean when the background varies).
- **Outputs:** `Present` (bool), `Score`, `Measure`; PLC bit per presence check in the result
  block (PLC 3.2 "tool result bits").
- **Decide and safety:** if the chosen measure's margin between the taught good and absent sets is
  < 6σ, the tool refuses to save ("these two look too alike for this measure") — the job-setup
  check pattern of CN [job-setup-checks](../../changes/2026-10-30-claude-job-setup-checks.md).
  Saving is also refused without at least one absent image (real or drawn): good-only teaching is
  not allowed for presence. Saturated region → NG.
- **Learn and tune:** auto-learn from a box; auto-tune picks the measure; per product: limit and
  measure; drift: good-set mean moving towards the limit.
- **Accuracy target:** 0 escapes on ≥ 300 staged absent parts per product; FR ≤ 0.05 %.
  **Speed:** ≤ 1 ms.
- **Build plan:** 1.5 pw (measures 0.5, Fisher choice 0.25, tests and Studio 0.75). Reuses
  `diagnose.cpp` contrast measures.

### 8.2 Golden template and variation model — "Compare to good"

- **Purpose:** find any difference from good parts in a printed or moulded region (label artwork,
  carton panel, cap top, leaflet fold). **Status:** Missing. OCV already uses per-letter mean ± kσ
  limits (`engine/src/ocr/quality.cpp`, CN [ocv-print-quality](../../changes/2026-10-30-claude-ocv-print-quality.md)),
  which is the same idea per character.
- **Best in class:** HALCON variation model: ideal image and variation image from good parts;
  thresholds AbsThreshold (default 10) and VarThreshold (default 2, a factor on σ), separate for
  too bright and too dark; textbook rule |c − i| > max(abs, var·v) [O, the exact formula was not
  rendered on the page]. Cognex PatInspect adds brighter-only / darker-only difference modes [O].
  Cognex US 5,850,466 (expired 2015) keeps templates indexed by sub-pixel pose and picks the
  closest, which removes interpolation blur [O]. Cognex US 8,971,663 (active to about 2032; adopt
  under the owner's statement of 2026-10-30, legal clearance) builds a synthetic golden template for multi-layer
  print from layer masks and poses [O] — exactly the case of a label printed in one pass and a
  batch overprinted in another (8.9).
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L1 | Light normalisation | linear gain / offset fit of the aligned region to the ideal image (least squares on non-edge pixels) | Light tolerance (on; Adv.) | normalised image |
| L2 | Align | 8.0 fixture; sub-pixel; pose-indexed templates (US 5,850,466) at 1/4 px steps for small regions | Template bank (on for regions < 1 MP; Adv.) | aligned image |
| L3 | Compare | per-pixel: dark fail if i − c > max(A_d, k_d·σ); bright fail if c − i > max(A_b, k_b·σ); σ widened near edges by dilating the gradient map (edge tolerance) | A (10), k (3; auto), Edge tolerance (1.5 px; Adv.), Mode (both / bright / dark; Ess.) | defect mask |
| L3 | Multi-layer | per-layer masks and poses (base artwork, overprint), each layer aligned on its own, blended template (US 8,971,663) | Layers (1; Adv.) | template per part |
| L4 | Blob | 8.0; min area, min contrast, inside / outside keep-out masks | Min size (Ess.), Ignore areas (Adv.) | defects |
| L5 | Learn | median image + 1.4826·MAD σ image from 5-20 good parts; σ floor = camera noise | — | model |

- **Corrections:** rotation / scale (fixture), small print shift (edge tolerance), print growth
  (edge tolerance widened by the measured stroke-width change), illumination (gain/offset),
  vignetting (flat field), glare (saturated pixels masked and reported, never passed silently),
  noise (σ floor), variable data areas (batch, date, serial) masked automatically from the OCR and
  code tool regions of the same job.
- **Outputs:** `Defects`, `DefectCount`, `LargestArea`, `DiffImage`, `Score`.
- **Decide and safety:** any saturated blob larger than the minimum defect size inside the region
  → NG "glare"; 2 % of the region saturated is the warning level; masked area above 40 %
  of the region → setup refused; a variable-data mask never covers a static artwork element taught
  as "must match".
- **Learn and tune:** auto-learn from 5-20 good images (minimum 5, else refuse); auto-tune on OK/NG
  sets moves k and min size; kept per product: ideal, σ, masks; drift: mean |c − i| over good parts.
- **Accuracy target:** 0 escapes for defects ≥ 3 × 3 px with contrast ≥ 3 σ_noise; FR ≤ 0.5 %.
  **Speed:** ≤ 15 ms T1 / ≤ 6 ms T2 for a 2 MP region (SIMD per-pixel compare; GPU optional).
- **Hardware:** ≥ 3 px across the smallest defect to find (so 0.1 mm defect needs ≤ 0.033 mm/px);
  diffuse dome or flat-dome light on glossy cartons ([05-HARDWARE.md](05-HARDWARE.md) section 6).
- **Build plan:** (1) median / MAD model and compare, SIMD, 1.5 pw; (2) edge-tolerance σ, 0.5 pw;
  (3) pose-indexed bank, 0.5 pw; (4) multi-layer template, 1 pw; (5) variable-data auto-mask from
  job tools, 0.5 pw. Total 4 pw.

### 8.3 Edge flaw — "Check edge"

- **Purpose:** nicks, burrs, bumps on a straight, round or taught outline (tablet edge, cap rim,
  carton flap edge, blister cavity). **Status:** Missing; there are no calipers yet (Tool 2 is all
  spec only, see [04a](04a-TOOLS-BASE-GEOMETRY.md)).
- **Best in class:** Cognex InspectEdge "defect mode" [O]; HALCON `edges_sub_pix` and robust fits
  (Tukey default, clipping 2.0) [O]; sub-model gauging Cognex US 6,993,177 (expired) [O].
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L2 | Path | taught contour (4.2) or fitted line / circle / arc (2.2 / 2.3) | Edge path (Ess.) | path |
| L3 | Dense calipers | caliper every n px along the normal; DoG profile, sub-pixel peak | Spacing (2 px; Adv.), Sigma (1.0; Adv.), Polarity (auto) | edge points |
| L3 | Deviation | residual to the robust fit (Tukey) or to the taught contour; also missing-edge runs | — | deviation profile |
| L4 | Flaws | runs above max deviation and longer than min length | Max deviation (Ess.), Min length (Adv.) | flaws |
| L5 | Learn | per-position σ of deviation over good parts | — | limits |

- **Corrections:** scale (3.4 mm), rotation (fixture), blur (sigma tied to measured PSF from
  `psf.cpp`), noise (spacing and averaging), print growth (not applicable), lens distortion (undistort
  before fitting).
- **Outputs:** `Flaws[]` (position, depth, length, type nick / burr / missing edge), `MaxDeviation`.
- **Decide and safety:** fewer than 90 % of calipers finding an edge → NG "edge not seen".
- **Accuracy:** 0 escapes for flaws ≥ 2 px deep and ≥ 3 px long; FR ≤ 0.3 %. **Speed:** ≤ 3 ms for
  2,000 points.
- **Build plan:** 2.5 pw after 2.1 / 2.2 / 2.3 exist (path + deviation 1.5, taught contour 0.5,
  tests 0.5).

### 8.4 Surface defect, classical — "Check surface"

- **Purpose:** scratches, spots, dents and stains on plain or regular surfaces (foil lids, cap tops,
  white cartons, tablet faces). **Status:** Missing.
- **Best in class:** oriented band-pass filter bank keeping the strongest response and its angle,
  Cognex US 6,983,065 (expired) [O]; Steger ridge detector `lines_gauss` for thin lines (sigma 1.5,
  low = 0.25-0.5 × high) [O]; Laws texture energy for textured backgrounds [O]; LumiTrax / photometric
  stereo for dents and embossing [O].
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Directional light **NEW option** | 4 shots, one light direction each, shape image (photometric stereo, Woodham) + albedo image | Shape image (off; Adv.) | 2 images |
| L1 | Background | large median or 2-D polynomial fit; residual | Background size (auto = 4 × max defect; Adv.) | residual |
| L3 | Scratches | Steger ridge at the scratch width + oriented bank (8 angles) | Scratch width (auto from a clicked example) | ridge map |
| L3 | Spots / dents | difference of Gaussians at 2 scales; on the shape image for dents | — | blob map |
| L3 | Stains | low-pass residual above a contrast | — | region map |
| L3 | Texture **mode** | Laws energy 5×5 masks + mean filter, Mahalanobis to taught texture | Texture (Plain / Brushed + angle / Pattern; Adv.) | anomaly map |
| L4 | Classify | by shape: length/width > 5 → scratch; round → spot; large soft → stain | Defect types (Ess.), Min size (Ess.) | typed defects |

- **Corrections:** uneven light (background removal, automatic), glare (saturated mask; cross
  polarisation advised by P.6), brushed texture (orientation suppression), curvature (polar unwrap
  for round caps, 0.11).
- **Outputs:** `Defects[]` (type, area, length, contrast), `Count`; heat map.
- **Decide and safety:** any saturated blob larger than the minimum defect size inside the region
  → NG "glare" (2 % saturated area is the warning level); region partly off the part → NG.
- **Learn and tune:** good parts → background noise σ and texture model; click an example → type
  and min size; auto-tune → sensitivity per type.
- **Accuracy:** recall ≥ 99.9 % for scratches ≥ 3 px wide and 50 px long (DL-04 uses the same
  defect for AI) and 0 escapes on staged sets; FR ≤ 0.5 %. **Speed:** ≤ 20 ms on 5 MP T1.
- **Hardware:** for dents and embossing, a 4-segment ring light switched by the PLC
  (requirements 8: multi-light sequences are PLC steps) — [05-HARDWARE.md](05-HARDWARE.md) 6.1.
- **Build plan:** background + DoG + Steger 1.5 pw; oriented bank 0.5; texture mode 0.5;
  photometric stereo composite 1 pw (needs multi-light trigger in PLC 5.3). Total 3.5 pw.

### 8.4b Surface defect, AI mode — "Check surface (AI)"

- **Purpose:** the same tool and the same outputs with 10.1 as the compare step, for textured
  surfaces where 8.4 fails (TS 8.4 already sends the user there). **Status:** Missing.
- **Design:** the classical tool runs first; only regions it cannot decide (score between the
  "clearly good" and "clearly bad" limits) go to the AI model. This is the cascade of Keyence
  US 11,367,225 (active to about 2040; adopt under the owner's statement of 2026-10-30, legal clearance) [O]: it
  saves T1 time because most parts are clearly good. Safety rule: the cascade may only send a part
  to the AI to **add** an NG, never to turn a classical NG into OK.
- **Accuracy / speed:** as 10.1; average time ≈ classical time + (share of unclear parts × AI time).
- **Build plan:** 1 pw after 10.1.

### 8.5 Bead / glue — "Check glue line"

- **Purpose:** hot-melt on carton flaps, glue on shipper cases, sealant beads: continuous, right
  width, right place. **Status:** Missing.
- **Best in class:** Omron FH markets bead inspection (gaps, misalignment) with no method published
  [O]; Cognex and HALCON build it from paired calipers along a path [O]. Pair-caliper method
  published by HALCON `measure_pairs` family [I].
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Light | UV light + UV-fluorescent glue where possible (contrast), else low-angle | — | image |
| L2 | Path | taught by tracing; snapped to bead centre; fixtured | Sample spacing (5 px; Adv.) | path |
| L3 | Width | pair calipers perpendicular to the path, sub-pixel | Polarity (auto) | width, offset per sample |
| L4 | Judge | gap = run with no pair ≥ max gap; width outside tolerance; offset > max | Width tol., Max offset, Max gap (Ess.) | gaps, narrow, offset |
| L5 | Learn | nominal width and σ per sample from good parts | — | limits |

- **Corrections:** scale (calibrated mm, required), perspective (3.2), bead colour drift (polarity
  auto), carton print behind the bead (UV light removes it).
- **Outputs:** `Widths[]`, `Offsets[]`, `Gaps[]`, `MinWidth`, `MaxWidth`, `MaxOffset`.
- **Decide:** no calibration → setup refused (mm limits need it). **Accuracy:** 0 escapes for gaps
  ≥ 1 mm; width ± 0.2 mm 3σ; FR ≤ 0.3 %. **Speed:** ≤ 10 ms / 300 samples.
- **Hardware:** UV (365 nm) bar light and UV-tagged glue are the single biggest accuracy gain for
  hot melt [I]; see [05-HARDWARE.md](05-HARDWARE.md).
- **Build plan:** 2.5 pw after 2.1 (path tracing 1, pair calipers and judging 1, tests 0.5).

### 8.6 Label inspection — "Check label"

- **Purpose:** label present, in position, straight, the right label (artwork / variant), print
  not damaged, no smudge, codes and text readable. **Status:** Partial: the code and text on a
  label are read and verified today (Tools 5, 6, 7; OCV with 0 wrong texts accepted), but label
  position, skew, artwork identity and print defects are Missing.
- **Best in class:** pharma label checkers combine edge-found pose, artwork comparison and
  OCR / OCV / code grading in one tool [I]; the artwork comparison is the variation model or golden
  template (8.2); identity is a pattern score or a classifier (10.3).
- **Sub-checks (each can be switched on alone):**

| Sub-check | Method | Options | Output | NG reason |
| --- | --- | --- | --- | --- |
| Present | 8.1 on label paper vs container | — | Present | `explain.label.missing` |
| Position | label edges by 2.2 lines (top, left) vs the part fixture | Position tol. (mm; Ess.) | Offset x, y | `.position` |
| Skew | angle between label top edge and container axis | Skew tol. (2°; Ess.) | Skew | `.skew` |
| Wrong label **NEW** | NCC of a taught artwork key area (logo, product name) per variant; or 10.3 classifier; plus the GTIN / product code from Tool 5 must match the job's product | Variants (Ess.), Key area (auto) | `LabelId`, score | `.wrong` |
| Print defects | 8.2 inside the label fixture, variable-data areas masked | Print sensitivity (Adv.) | Defects | `.print` |
| Smudge **NEW** | 8.2 dark-only + low-frequency residual; for text, per-letter OCV ink spread (Tool 7) | Smudge size (Adv.) | Smudges | `.smudge` |
| Wrinkle / flag **NEW** | edge straightness residual (8.3) along label edges; lifted corner = missing edge run | Max wrinkle (Adv.) | Wrinkles | `.wrinkle` |
| Codes and text | Tools 5, 6, 7 placed in the label fixture | as those tools | reads, grades | their reasons |

- **Layers:** L1 flat field and undistort; L2 container fixture, then label fixture (two-level);
  L3 as the table; L4 any sub-check NG → NG; L5 auto-find proposes the label outline and its
  codes / text (P.2); L6 overlay of label outline, offset arrow, defect outlines.
- **Corrections:** curvature of round bottles (cylinder unwrap 0.11 for wrap labels, automatic
  when the job says "round container"), perspective, glare on glossy laminate (polariser; masked),
  print colour variation between print runs (light normalisation; 9.x ΔE for colour checks), label
  stretch (affine fixture).
- **Decide and safety:** wrong label is a **never-pass** NG; if the artwork key score is between the
  two best variants (margin < limit), NG "not sure which label".
- **Accuracy:** 0 escapes on missing, wrong, skew > limit, defects ≥ min size; offset ± 0.2 mm 3σ;
  skew ± 0.1° 3σ; FR ≤ 0.5 %. **Speed:** ≤ 25 ms T1 without codes and text.
- **Build plan:** two-level fixture 0.5 pw; position and skew 1 pw (after 2.2); wrong-label 1 pw;
  print / smudge / wrinkle on 8.2 and 8.3 1 pw; tests 0.5 pw. Total 4 pw.

### 8.7 Cap and seal — "Check cap"

- **Purpose:** cap present, fully screwed (height), straight (tilt / skew), tamper band intact,
  induction seal or foil present, cap colour right. **Status:** Missing.
- **Best in class:** filled-bottle inspectors measure cap height, skew, raised or missing cap and
  band integrity (Acquire Automation CapQ, Silgan, Mettler Toledo CI-Vision, Omron case study with
  four views 90° apart) [S]; CapQ claims AI ignores water droplets that cause false rejects [S].
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Views | side view with back light (silhouette) for height / tilt; top view with ring light for band and foil; 2-4 side cameras for full band (P.12) | View (Side / Top; Ess.) | images |
| L2 | Find | side: bottle shoulder and cap top lines (2.2); top: cap circle (2.3) | — | lines, circle |
| L3 | Height, tilt | distance and angle between the two lines, in mm | — | Height, Tilt |
| L3 | Band | polar unwrap of the band annulus (0.11); bridges as a periodic profile; gap = missing period or a break run | Band check (on; Adv.), Bridges (auto count) | BandGaps |
| L3 | Seal / foil | 8.1 presence on the foil ring or 8.2 inside the neck | — | SealOk |
| L3 | Colour | 9.1 on the cap top | — | ΔE |
| L4 | Judge | each against limits | Height tol., Tilt limit (Ess.) | Pass |

- **Corrections:** bottle position (fixture from the shoulder), bottle rotation (band is unwrapped,
  start angle free), droplets and condensation (back-light silhouette is insensitive; AI 8.4b
  filter as an option), scale (mm calibration at the cap plane).
- **Decide and safety:** cap missing and band broken are never-pass; one view missing in a 4-view
  band check → NG (P.12 rule).
- **Accuracy:** height ± 0.1 mm 3σ (needs ≤ 0.05 mm/px at the cap), tilt ± 0.2° 3σ; 0 escapes on
  staged loose caps (≥ 0.5 mm high) and cut bands; FR ≤ 0.3 %. **Speed:** ≤ 10 ms per view.
- **Build plan:** 3 pw after 2.2 / 2.3 / 0.11 (side 1, band 1.25, seal + colour 0.25, tests 0.5).

### 8.8 Blister and foil (NEW) — "Check blister"

- **Purpose:** every cavity filled with one whole tablet or capsule of the right colour and shape,
  no foreign tablet, no broken half; foil lid not torn, wrinkled or misprinted, seal pattern even.
  Not in TS; a core pharma check. **Status:** Missing.
- **Best in class:** blister inspectors check count, type, shape, colour, two-colour capsules,
  foreign objects and foil seal [S] (Optel BlisterSafe data sheet, trade articles). One opened case
  study is a warning: on a Pfizer line, visible-light vision and a checkweigher missed broken
  tablets and crushed blisters, and an X-ray unit was added (300 packs/min) [O,
  [vision-systems.com](https://www.vision-systems.com/factory/consumer-packaged-goods/article/16749235/x-ray-vision-system-checks-tablets-in-blister-packs)].
  So: forming side (before lidding, tablets visible) is where camera inspection works; lid side
  checks the foil only.
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | Light | forming side: diffuse dome, colour camera; foil side: polarised or low-angle light (glossy foil) | — | image |
| L2 | Cavity lattice | cavity grid fitted like the code slot map (`engine/src/slots.cpp`): rows × columns, pitch, angle | Layout (auto from first card) | cavity ROIs |
| L3 | Per cavity | 8.1 presence; area and outline vs taught tablet (4.1 blob shape: area, convexity, ellipse axes) for broken / half; 9.4 colour class for wrong / foreign tablet; two-colour capsule = two colour regions in order | Shape tol., ΔE limit (Ess.) | per-cavity state |
| L3 | Foil | 8.2 on printed foil, 8.4 for tears / wrinkles; seal pattern by 8.2 on the knurl | — | foil defects |
| L4 | Judge | any cavity empty / broken / wrong / double → NG with cavity row and column | — | NG reason + cavity |
| L5 | Learn | tablet shape and colour from 1 good card; foil from 10 cards | — | model |

- **Corrections:** card position and slight rotation (lattice fit), cavity shadow (dome light),
  tablet rotation in the cavity (rotation-free shape features), colour drift (9.0 white patch on the
  machine), glare on PVC (polariser).
- **Decide and safety:** a cavity the lattice cannot place → NG; foreign tablet is never-pass.
- **Accuracy:** 0 escapes on staged empty, half, wrong-colour and foreign tablets (≥ 300 each);
  FR ≤ 0.3 %. Broken tablets lying flat with the piece still in the cavity: state the limit — a
  camera sees ≥ 5 % area loss reliably [I]; finer needs X-ray.
- **Speed:** ≤ 30 ms per card T1. **Hardware:** colour camera (Tool 9), dome light; polarisation
  camera (Sony IMX250MZR class, [O] via research-geometry) for foil.
- **Build plan:** lattice reuse 0.5 pw, cavity checks 1.5, foil 1, tests and staged set 1. Total 4 pw.

### 8.9 Print registration (NEW) — "Check print position"

- **Purpose:** where the overprint (batch, date, MRP, code) sits relative to the pre-printed
  artwork box or carton edge; also colour-to-colour registration marks. **Status:** Missing; OCV has
  an "Allow shift" per character (CN ocv-shift-plc-inkjet) but no box-relative position.
- **Best in class:** layered template idea of Cognex US 8,971,663 [O] (each layer aligned on its
  own: the offset between layer poses is the registration error).
- **Layers:** L2 two fixtures (artwork by 1.2 on the pre-print; overprint by the text / code tool's
  found box); L3 offset and angle between them in mm; L4 limits; L5 nominal and σ from 10 good
  parts. Options: Reference (artwork box / carton edge / mark; Ess.), Tolerance x, y, angle (Ess.).
- **Corrections:** carton skew (both fixtures move together, so only the relative offset is used),
  scale (mm), perspective.
- **Outputs:** `OffsetX`, `OffsetY`, `Angle`, `InBox` (bool, the print fully inside the box).
- **Decide:** print touching the box edge → NG "print outside its box". **Accuracy:** 0.1 mm 3σ;
  0 escapes. **Speed:** ≤ 10 ms. **Build plan:** 2 pw after 1.2.

### 8.10 Contamination / foreign particle (NEW) — "Check for dirt"

- **Purpose:** dark specks, hairs, fibres on cartons, leaflets, white tablets, cap insides and in
  clear liquids. **Status:** Missing.
- **Layers:** L0 bright-field for surfaces; back light for clear bottles (with a spin-stop sequence
  for particles in liquid — 2-4 images, moving specks are particles, still specks are glass
  marks); L1 background removal (8.4); L3 dark-and-bright spot detection (DoG) and hair
  detection (Steger ridge); for liquids, frame difference after the bottle stops; L4 min size in mm;
  L5 noise from good parts.
- **Corrections:** print pattern behind (8.2 template subtraction first), glass defects vs particles
  (motion test), glare (mask).
- **Accuracy:** 0 escapes for particles ≥ 0.3 mm on surfaces (≥ 3 px), FR ≤ 0.5 %. Particles in
  liquid: report as "screening" only until validated on a Knapp-style test set [I].
- **Speed:** ≤ 15 ms. **Build plan:** 2.5 pw (surface 1, liquid sequence 1, tests 0.5).

### 8.11 Fill level (NEW) — "Check fill"

- **Purpose:** liquid level in clear or translucent bottles, powder level in transparent jars.
  **Status:** Missing. **Method:** back light; vertical caliper (2.1) for the meniscus edge between
  the shoulder line and the base; level relative to the shoulder (fixture). Options: Level and
  tolerance (mm; Ess.), Meniscus (top / bottom; Adv.). Corrections: bottle tilt (measure along the
  bottle axis), foam (strongest-edge rule plus foam band width output), label in the way (search
  window between labels). **Accuracy:** ± 0.5 mm 3σ; 0 escapes over the limit. **Speed:** ≤ 5 ms.
  **Build plan:** 1.5 pw after 2.1.

---

## Tool 9. Colour — "Check colour"

Licence `VS-COL-`. **Status of the whole tool: Missing.** The engine is grey only (no `cvtColor`
to Lab / HSV in `engine/src`; the BMP loader is 8-bit grey; `GrayImage` in
`dotnet/VisionCore/Tools/ToolModel.cs`), and the line camera MV-CS200-10UM is mono. The white-card
flat field in `engine/src/calibration.cpp` is for grading only. **Hardware:** a colour camera is
needed for every 9.x tool; the colour twin of the line camera (Bayer, same sensor family) or a
global-shutter colour camera at the station that checks colour ([05-HARDWARE.md](05-HARDWARE.md)
section 4.3). Colour costs resolution: a Bayer sensor has a quarter of the pixels per colour, so
codes and text keep a mono camera where both are on one station.

**Best in class (whole tool):** CIEDE2000 with its 5 corrections (hue rotation term, neutral
compensation; slightly discontinuous near 180° hue differences) and industry limits (print about
ΔE 2.0, strict 0.5; JND of ΔE*ab about 2.3) [O, [Colour difference](https://en.wikipedia.org/wiki/Color_difference)];
the reference implementation and test pairs of Sharma, Wu and Dalal, *Color Research and
Application* 30(1):21-30, 2005 (`ciede2000testdata.txt`) [O,
[page](https://hajim.rochester.edu/ece/sites/gsharma/ciede2000/)]; per-channel flat field with a
white target (Euresys formula) [O]. No vendor colour-tool manual was readable in the research
(IM 15.2); Teledyne Sherlock lists teach-and-measure colour tools [O].

### 9.c Colour image pipeline (NEW)

- **Purpose:** carry colour images through the engine and the C API (today `GrayImage` only).
  **Status:** Missing.
- **Layers:** L0 camera pixel format (BayerRG8 / RGB8 / Mono8), white balance off in the camera (we
  correct in 9.0, so the camera does not drift); L1 demosaic (edge-aware, OpenCV
  `COLOR_BayerRG2BGR_EA` [I]), dark frame, flat field per channel. Options: Pixel format (auto from
  camera), Demosaic (fast / edge-aware; Adv.).
- **Corrections:** per-channel flat field, dark frame, chromatic aberration of the lens (per-channel
  scale from the calibration plate: lateral colour shift up to 1 px at corners [I]).
- **Outputs:** `ColourImage` type in the C API and the job; every grey tool gets the luminance or
  9.6 channel automatically.
- **Accuracy:** bit-identical to the reference demosaic. **Speed:** ≤ 8 ms for 20 MP on T1 (SIMD).
- **Build plan:** 2 pw (C API type 0.5, demosaic + flat 1, Studio display 0.5).

### 9.0 White reference — "Correct colour"

- **Purpose:** remove the light's colour and drift so the same part gives the same Lab every day.
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L1 | White balance | per-channel gains to make a white / grey patch neutral; patch visible in every image (machine-mounted ceramic tile) or taught once | Reference mode (Fixed patch / Taught once; Ess.) | gains |
| L1 | Colour correction | 3×3 matrix (or 3×4 with offset) by least squares on the 24 patches of a colour chart in sRGB | Chart correction (off / 3×3 / 3×4; Adv.) | CCM |
| L1 | To Lab | linearise (sRGB gamma), XYZ D65, Lab | Illuminant (D65; Adv.) | Lab image or region means |
| L5 | Drift | gains vs taught gains | Drift limit (5 %; Adv.) | Drift % |

- **Corrections:** lamp colour and ageing (gains every image when a fixed patch is used —
  automatic), uneven light (per-channel flat field), camera temperature (dark frame), dirt on the
  patch (patch uniformity check: patch σ above taught → "clean the white tile").
- **Outputs:** `Gains`, `Drift`, `ResidualDeltaE` (chart fit quality).
- **Decide and safety:** patch not visible or saturated → every colour result of that image is NG
  "colour reference lost"; drift above the limit → warning, then NG at 2× the limit.
- **Accuracy:** residual ΔE00 ≤ 1.0 mean over the 24 patches after correction (strict 0.5 with a
  stable LED light) [I target]. **Speed:** ≤ 1 ms with a fixed patch.
- **Build plan:** 2 pw (gains 0.5, CCM 0.5, Lab LUT 0.5, tests with a printed chart 0.5).

### 9.1 Colour match — "Check colour"

- Method: mean Lab in the region (robust: trimmed mean 10 %), CIEDE2000 to each taught colour;
  options Taught colours (Ess.), Max ΔE (3.0; Ess.; auto-tolerance from good parts: μ + 4σ of ΔE),
  Formula (ΔE2000 / 94 / 76; Adv.), kL:kC:kH (1:1:1; Adv., 2:1:1 for textiles [I]).
- Corrections: as 9.0; glare (saturated pixels excluded); texture (trimmed mean).
- Outputs: `MeanLab`, `DeltaE`, `dL`, `dC`, `dH`, `BestMatch`. Decide: ΔE ≤ max; region with
  > 5 % saturated pixels → NG. Accuracy: repeatability ΔE00 ≤ 0.3 (3σ) on 100 captures of one part;
  0 wrong accept / reject with limits ≥ 2.0. Speed ≤ 1 ms. Build: 1 pw.

### 9.2 Colour presence — "Is the colour there?"

- Method: per-pixel ΔE to the taught colour through a 3-D LUT on quantised RGB (32³ bins, rebuilt
  when 9.0 gains change), count and area. Options: Colour, Tolerance ΔE, Min / max area (Ess.).
- Outputs: `Area`, `Fraction`; mask. Accuracy: area ± 1 % of the region vs hand-labelled masks.
  Speed ≤ 5 ms / 2 MP. Build: 1 pw.

### 9.3 Colour segmentation — "Find colour areas"

- Method: nearest taught colour per pixel within tolerance via LUT; optional morphological smoothing;
  masks feed 4.1 blob and 8.8 blister. Accuracy: pixel agreement ≥ 99 % against labelled masks away
  from edges. Speed ≤ 10 ms / 5 MP. Build: 1.5 pw.

### 9.4 Colour sorting — "Sort by colour"

- Method: nearest class by ΔE2000 with a margin rule: best ΔE ≤ max **and** second − best ≥ min
  separation; else "Not sure" (NG, always; same rule as 10.3). Used for variant checks, capsule
  colour, cap colour, wire order. Accuracy: 100 % right class on the taught variants, all others
  "Not sure". Build: 1 pw.

### 9.5 ΔE measurement — "Measure colour"

- Outputs `L`, `a`, `b`, `DeltaE76`, `DeltaE94`, `DeltaE2000`, `dL`, `da`, `db` for quality
  records and SPC (P.11). **Test:** the 34 pairs of `ciede2000testdata.txt` [O] to 1e-4 in a unit
  test (the data file is downloaded by the test script into a scratch folder, not committed, since
  its licence is not stated). Note: camera colour is relative unless 9.0 chart correction and a
  stable light are used; this is written on the result. Build: 0.5 pw.

### 9.6 Best grey from colour (NEW) — "Use colour for contrast"

- **Purpose:** for grey tools (codes, OCR, blob) on coloured print, pick the grey conversion with the
  most contrast: one channel, a weighted sum, or the first principal component of the region's
  colours (red ink on brown carton disappears in luminance but not in the blue channel).
- **Method:** on teach, Fisher ratio of ink vs background for R, G, B, Lab L, a, b and PCA-1; keep
  the best per tool region; auto-learned, Advanced. **Accuracy:** contrast gain ≥ 1.5× on coloured
  print sets [I target]. **Speed:** ≤ 3 ms. **Build:** 1 pw. Also the software form of a coloured
  light filter: on a mono camera the same gain comes from a coloured LED or a band-pass filter
  ([05-HARDWARE.md](05-HARDWARE.md) 6.1).

---

## Tool 10. AI deep learning — "Teach by example"

Licence `VS-DL-`. Phase P3. Built today: three models we train and ship ourselves — CodeNet code
locator (`ml/model.py`, `engine/src/ai_locator.cpp`), the code enhancer (`ml/enhance.py`) and the
OCR recogniser (`ml/ocr/crnn.py`, `ml/models/ocrnet.onnx`) — run by OpenCV DNN in FP32. Training is
by script (`ml/train.py`, `ml/retrain.py` with an old-vs-new gate, `ml/ocr/train_ocr.py`), with
promotion by a named approver in `dotnet/Dashboard/Services/ModelRegistry.cs` (`Promote`). No
user-trained AI tool, no ONNX Runtime, no INT8, no GPU path, no model encryption exists
(inventory). ADL is the design; this chapter adds the methods, the numbers and the build order.

**Patents (owner's statement of 2026-10-30, legal clearance; adopt, cite):** PatchCore-style kNN scoring is
claimed by Yissum US 12,182,721 (active to about 2042) [O]; student-teacher / decoder-regressor
disagreement by MVTec US 12,548,135 and EP 4 145 401 [O]; classical-first cascade by Keyence
US 11,367,225 [O]; "mark a defect as allowable and rebuild" by Seiko Epson US 11,562,479 [O];
run-time candidate images and confidence-ordered labelling by Cognex US 12,361,100 and
US 12,315,277 [O]; automatic subset selection by 3M US 12,482,244 [O]; auto-labels from similar
images by Mellanox US 11,836,909 [O]; example-based parameter estimation by MVTec EP 4 636 707 [O].
All are adoptable under the clearance. The line-safety rules DL-06 (no model change while a job
runs) and AS-04 (gate + named approver) stay: they were written as design-arounds but are also
safety rules. DL-07 (diversity ordering instead of confidence ordering) was only a design-around;
this study proposes offering both orderings (requirements change for the owner to confirm).
**Copyright and licences are not covered by the clearance:** Ultralytics YOLO models are AGPL-3.0
even when trained from scratch [O, [licence](https://www.ultralytics.com/license)]; DL-22 (no AGPL /
GPL / non-commercial weights) stands.

### 10.1 AI anomaly detection, local — "Check defects (AI)"

- **Purpose:** unknown defects (stains, scratches, dents, smudges, contamination) learned from good
  images only. **Status:** Missing; the classical per-slot study (`ml/experiments/anomaly.py`) was
  not good enough (0 / 56 torn labels at 0 false alarms, IM 15.3).
- **Best in class:** Dinomaly (DINOv2 ViT-B/14, noisy bottleneck, one model for many products):
  99.6 / 98.7 / 89.3 image AUROC on MVTec AD / VisA / Real-IAD, Apache-2.0 code [O]. EfficientAD-S:
  98.8 MVTec AD, 97.5 VisA, 90.0 MVTec LOCO, 2.2 ms on an RTX A6000 [O]. PatchCore: up to 99.6 image
  AUROC, needs only feature extraction and a coreset memory bank [O]. PaDiM: per-patch Gaussian on
  CNN embeddings [O, abstract]. The warning: MVTec AD 2 (dark-field, back light, tiny defects,
  lighting shift) puts state-of-the-art below 60 % mean AU-PRO [O] — public 99 % numbers do not
  transfer; our bench decides. Vendors: HALCON / MERLIC anomaly from 20-50 good images [S]; Omron FH
  AI Fine Matching (100-200 good images) [S]; Teledyne Astrocyte (train under 10 min, NVIDIA 8 GB)
  [O]; Zebra Aurora 20-50 images [S].
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L1 | Crop and normalise | fixtured ROI (8.0), resized to model size; tiles at native resolution for small defects | Tile size (auto from min defect: ≥ 8 px defect per 256 px tile; Adv.) | tensors |
| L3 | Score | **Fast** (T1): PatchCore with ResNet-18 layers 2-3, coreset 1-10 %, INT8 (US 12,182,721). **Balanced** (T1 / T2): PaDiM-style Gaussian per patch position (Mahalanobis), no kNN, very fast on CPU for fixtured parts [I]. **Accurate** (T2-T3): EfficientAD-S/M (US 12,548,135). **Best** (T3 / T4, many products): Dinomaly ViT-B | Model (auto from tier and cycle time; Adv.) | heat map |
| L4 | Normalise | score so that the 99.9th percentile of good validation images = 50; defect regions = heat map > limit, blobbed (4.1) with min area | Strictness (50; auto; Ess.), Min area (Ess.) | score 0-100, regions |
| L4 | Outlier | 10.14 score on the image embedding; outlier → NG "unlike anything taught" | — | outlier score |
| L5 | Train | 20-100 good images (DL-03: 20 images ≤ 2 min T2, ≤ 10 min T1) | — | model |

- **Corrections:** alignment (fixture before the model — the single biggest gain for fixtured
  parts [I]), light drift (augment ± 20 % brightness; 10.14 flags larger), position jitter (shift
  augmentation), rotation only if the part rotates, glare (augment glare only when the process has
  it), resolution (tiling).
- **Outputs:** `Score`, `HeatMap` (≥ 1/8 input resolution, DL-03), `Regions[]`, `Outlier`; PLC: tool
  bit + NG reason code.
- **Decide and safety:** score > limit → NG; outlier → NG; model file hash ≠ job manifest → job not
  ready (error 400 class); GPU failure → CPU path (10.16), never skip.
- **Learn and tune:** auto-learn picks the model from the tier benchmark (ADL 3.9); auto-tune sets
  strictness from OK / NG (P.4); kept per product: model, threshold, normalisation; drift: score
  distribution CUSUM and embedding two-sample test (10.15).
- **Accuracy target:** 0 escapes and FR ≤ 0.5 % on the owner's sets (0.1); image AUROC ≥ 99 % on
  our own "AD 2-style" shifted set (light ± 40 %, defocus, tilt) made from owner images; DL-04
  scratch recall ≥ 90 % at 3 × 50 px on 5 MP, else the training screen recommends 10.5.
- **Speed:** ≤ 150 ms T1 (OpenVINO INT8, ResNet-18, 256 px, one ROI) / ≤ 20 ms T2. Not measured
  yet on our PC: the first build step measures it.
- **Hardware:** for the Accurate / Best modes an RTX 4060-class GPU (T2) on the line PC; training on
  a T4 workstation or the plant server (DL-25).
- **Build plan:** (1) anomalib-based trainer (Apache-2.0) for PatchCore, PaDiM, EfficientAD,
  exported to ONNX, 1.5 pw; (2) C++ inference with tiling, normalisation, blobs, 1.5 pw; (3) owner
  sets: shifted-light set and staged defects on 3 products, 1 pw; (4) Dinomaly trainer on T4 and
  ViT INT8 test, 1 pw; (5) Studio screens via 10.9, 1 pw. Total 6 pw.

### 10.2 AI anomaly, logical / global (NEW) — "Check arrangement"

- **Purpose:** wrong count, wrong position, wrong order, a valid part in the wrong place (a leaflet
  folded the wrong way, a bottle of the wrong size in a case, two caps on one bottle) — errors where
  every local patch looks normal. **Status:** Partial: for code cases the slot map
  (`engine/src/slots.cpp`) and expected count find a missing code; nothing general.
- **Best in class:** HALCON Global Context Anomaly Detection: local and global subnetworks trained
  on good images, score normalisation step, either branch can be dropped for speed [O]; MVTec LOCO
  is the public benchmark (EfficientAD 90.0 / 90.7 image AUROC [O]).
- **Layers:** L2 detection or slot lattice for the parts (10.4 or classical); L3 two branches: (a)
  **rule branch** — count, lattice residual, order of classes along a row (from 10.3 per slot),
  relative positions within taught tolerances; (b) **global branch** — image-level embedding
  (DINOv2-S CLS token or a small autoencoder bottleneck) Mahalanobis to good images; L4 either
  branch over limit → NG with the reason ("3 items in row 2, expected 4"); L5 from 20-100 good
  images.
- **Corrections:** as 10.1; product-to-product variation handled by one model per product.
- **Accuracy:** 0 escapes on staged count / swap / misplacement sets (≥ 300 each); FR ≤ 0.5 %.
  **Speed:** ≤ 50 ms T1 for the rule branch, ≤ 10 ms T2 for both. **Build:** 4 pw (rule branch 1.5
  reusing slots, global branch 1.5, sets and tests 1).

### 10.3 Classification with "Not sure" — "Sort by picture"

- **Purpose:** OK / NG by picture, product variant, orientation, defect type for the Pareto.
  **Status:** Missing.
- **Best in class:** frozen DINOv2 features + small head trains in seconds on CPU from 3-10 images
  per class [O, [DINOv2](https://arxiv.org/abs/2304.07193)] (DL-05 quick mode); Cognex Few Sample
  "about five" bad samples [S]; HALCON 25.11 continual learning and `fit_dl_out_of_distribution`
  with < 15 samples per class [O]; MobileNetV4 backbones added in HALCON 25.11 [O].
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L3 | Features | **Quick**: frozen DINOv2-S/14 (or MobileNetV3 on T1) + logistic / prototype head; **Full**: fine-tuned EfficientNet-B0 / ConvNeXt-Atto (ADL 3.3) | Mode (Quick / Full; Ess.) | logits |
| L4 | Calibrate | temperature scaling (10.13) | — | probabilities |
| L4 | Not sure | energy score or Mahalanobis to class centres (10.14) + top-2 margin | Not sure → NG only (locked); a separate PLC "Not sure" bit is set in addition | class or "Not sure" |
| L5 | Train | stratified, time-ordered split; class balance by sampling | — | model |

- **Corrections:** augmentations of ADL 3.8 limited to what the process produces; light (10.14).
- **Outputs:** `Class`, `Probability`, `Scores[]`, `NotSure`, Grad-CAM on NG (ADL 8).
- **Decide and safety:** expected class with p ≥ limit → OK; anything else (wrong class, "Not sure",
  outlier) → NG, always; the PLC "Not sure" bit is set in addition, never instead. Requirement
  DL-02 offers a "pass" option for "Not sure", which conflicts with AS-12 (in doubt, NG); that is an
  owner decision (decision 8 in [chapter 06](06-ACCURACY-AND-TIMELINE.md)), and until it is made
  "Not sure" is NG.
- **Accuracy:** ≥ 99.5 % right on held-out owner sets, 0 wrong classes above the confidence limit;
  ECE ≤ 5 %; quick mode ≥ 95 % on the 5-class benchmark (DL-05). **Speed:** ≤ 30 ms T1 (MobileNetV3,
  224 px, INT8) / ≤ 5 ms T2. **Build:** 3 pw (quick head 1, full fine-tune 1, Not sure + tests 1).

### 10.4 Object detection — "Find and count items"

- **Purpose:** find and count items (bottles in a case, leaflets, scoops, ampoules in a tray);
  missing item at row / column. **Status:** Missing; CodeNet finds code regions only.
- **Best in class:** RT-DETR-R50 53.1 COCO AP, 108 FPS on a T4 GPU [O]; HALCON 25.11 imports YOLO
  ONNX [O]; licence rule: Apache-2.0 / MIT detectors (RT-DETR, YOLOX, D-FINE), not Ultralytics (AGPL)
  [O].
- **Layers:** L2 detector (nano on T1 only for ≤ 640 px; small / medium on T2-T3, ADL 3.9); rotated
  boxes option; L4 count rules per class; when items sit on a grid, the slot lattice names the
  missing slot (`slots.cpp`); L5 50+ labelled images, SAM 2 click labels (10.10).
- **Corrections:** scale (augment 0.9-1.1), occlusion (cut-out), light, rotation.
- **Decide:** count ≠ expected → NG with the missing slot; any box with confidence between the
  "certain" and "absent" limits → NG "not sure".
- **Accuracy:** 100 % exact count on held-out sets of ≥ 500 images per product; box IoU ≥ 0.7.
  **Speed:** T2 ≤ 15 ms; not recommended on T1 (TS). **Build:** 4 pw (trainer 1.5, C++ post-process
  and rules 1, lattice reuse 0.5, tests 1).

### 10.5 Segmentation — "Find defect area"

- **Purpose:** pixel masks of known defect types for area / length rules (crack length, stain area,
  seal channel width). **Status:** Missing.
- **Method:** U-Net with a pretrained ResNet-18 / EfficientNet-B0 encoder; SegFormer-B0 on T3;
  Dice + focal loss; outputs into 4.1 blob so limits use auto-tolerance (ADL 3.5). Labels by click
  (SAM 2, Apache-2.0, ~6× faster than SAM on images [O]) — DL-09 one-click mean IoU ≥ 0.75.
- **Decide:** defect area ≥ min → NG; mask touching the ROI edge → NG "defect may continue outside".
- **Accuracy:** 0 escapes for defects ≥ min area on owner sets; IoU ≥ 0.8 for area measurement.
  **Speed:** ≤ 200 ms T1 (256-512 px) / ≤ 20 ms T2. **Build:** 4 pw.

### 10.6 Dense counting (NEW) — "Count many"

- **Purpose:** count hundreds of small items (tablets on a tray, vials in a rack, cartons in a
  bundle) where boxes overlap. **Status:** Missing.
- **Best in class:** MVTec Deep Counting, trained from a few labelled objects, in the MERLIC basic
  package [S]; DL-16 asks ≥ 500 items with < 1 % count error.
- **Method:** point-supervised density / centre-heatmap head on the 10.4 backbone; count = sum or
  peaks; peaks matched to a lattice where one exists. **Decide:** count outside expected → NG;
  density map peak ambiguity (two peaks closer than half an item) → "not sure". **Accuracy:** exact
  for ≤ 100 items on held-out images; < 1 % at ≥ 500 (DL-16). **Speed:** ≤ 100 ms T1 / ≤ 15 ms T2.
  **Build:** 2.5 pw.

### 10.7 AI locator — "Find the part (AI)"

- **Purpose:** find a part whose look varies (shrink wrap, printed variants, reflections) where
  1.2 geometric matching fails; then refine classically for 0.05 px / 0.05°. **Status:** Partial:
  CodeNet (25 k parameters, 1/8 input, `engine/src/ai_locator.cpp`) proposes code regions only.
- **Method (ADL 3.7):** centre heatmap + sin / cos angle + scale at 1/4 resolution, then 1.2
  refinement on the best candidates; trained from 5-10 images by heavy augmentation of the taught
  part pasted on its own background. Cognex US 11,568,629 (pattern finder + classifier) adoptable
  under the clearance. **Decide:** AI peak without a classical refinement match ≥ limit → NG "part
  not found" (AI never places a fixture alone). **Accuracy:** 100 % find on held-out sets;
  0.05 px / 0.05° repeatability after refinement (requirements 13). **Speed:** ≤ 80 ms T1 / ≤ 25 ms
  T2. **Build:** 4 pw (after 1.2).

### 10.8 AI OCR fine-tune — "Teach the reader"

- **Purpose:** fine-tune the recogniser for a customer's font or print type (CIJ dots, embossed).
  **Status:** Partial: CRNN + CTC trained on synthetic and real laser lines (CN ocr-ai-reader:
  laser 92.6 %, TTO 98.0 %, CIJ 72.9 %, embossed 89.4 % characters synthetic; real laser 100 % of
  389 lines); fine-tuning by the user (`dl.ocr_tune`) is Missing. Details are in
  [04c-TOOLS-TEXT.md](04c-TOOLS-TEXT.md).
- **Method:** recogniser only, detector frozen; labels free from OCV matches ("every read is a
  label", ADL 3.6); CTC loss normalised by target length (HALCON 25.11 change [O]); 50+ line images.
- **Decide:** the fine-tuned model passes 10.12 on the product's golden lines with 0 wrong texts
  accepted by OCV. **Accuracy:** ≥ 99.5 % characters on owner prints (requirements 13; DL-11 for
  CIJ). **Build:** 3 pw.

### 10.9 Training workflow — Collect, Label, Train, Validate, Deploy

- **Purpose:** the one-button path of ADL 5 and UX 4.10. **Status:** Partial: scripts only
  (`ml/train.py`, `ml/retrain.py`, `ml/ocr/train_ocr.py`); no worker process, no training screens.
- **Best in class:** Keyence VS-G auto image selector and re-check of stored images after any
  change [O]; Landing AI visual prompting (partial labels, ≤ 25 images) [O]; Teledyne Astrocyte
  under-10-minute training [O]; Cognex OneVision cloud-to-edge training [O, press release].
- **Steps (as ADL 5.1, with additions):**

| Step | Method | Options | Output |
| --- | --- | --- | --- |
| Collect | Save good / Save bad / "Use for teaching" from the NG viewer / import folder; perceptual-hash de-duplication; native resolution + fixture pose | — | dataset |
| **Pick NEW** | auto image selector: k-centre (core-set, Sener and Savarese [O]) on embeddings over a stored stream proposes the 20-50 most varied good images (3M US 12,482,244 adoptable) | Count (30; Adv.) | proposed set |
| Label | pre-labels (10.10); "check me" first; ordering by diversity **or** by confidence (DL-07 change proposed) | Ordering (diversity; Adv.) | labels |
| Defect book **NEW** | named defect types with example crops and the agreed limit, stored in the job (Landing AI practice [S]) | — | defect book |
| Train | `vs_trainer` worker (Python + PyTorch, ADL 5.5), 80/20 time-ordered split, fixed seeds; automatic parameter search in a time budget (DL-18, Optuna-style [O]) | Budget (10 min; Ess.) | model + report |
| Validate | plain-word confusion (ADL 5.2), threshold slider; MVTec-AD-2-style shifted set; worst 3 good and worst 3 bad shown | — | report |
| Deploy | 10.12 gate → named approver (e-signature in Part 11 mode, P.16) → new model version in the job (10.17) | — | model vN |

- **Decide and safety:** training only on explicit user action (DL-06); nothing changes on a running
  job; a dataset without a held-out set cannot deploy.
- **Accuracy:** retraining with the same data and seed reproduces scores within 1 point on 0-100.
  **Speed:** anomaly from 20 images ≤ 2 min T2 / ≤ 10 min T1 (DL-03).
- **Build plan:** worker process and pipe protocol 1.5 pw; dataset store (ADL 5.3) 1 pw; image
  selector 0.5; Studio screens Collect / Label / Train / Validate / Models 2.5 pw; defect book 0.5.
  Total 6 pw.

### 10.10 Auto-label — "Pre-label"

- **Status:** Partial: `ml/autolabel.py` labels real images from engine reads, only complete
  images. **Methods:** (a) labels from classical truth (codes read, OCV-matched text) — strongest,
  free; (b) model pre-labels below confidence 0.6 marked "check me" (ADL 5.1); (c) click-to-mask
  with SAM 2 (Apache-2.0) [O]; (d) text-prompted boxes by Grounding DINO (52.5 zero-shot COCO AP) or
  OWLv2 on the trainer PC [O] (DL-20, ≥ 70 % accepted unchanged); (e) label-error check listing the
  2 % most suspect labels (DL-10; own implementation, cleanlab is AGPL). Mellanox US 11,836,909
  (auto-labels from similar images) adoptable. **Rule:** every pre-label is shown to a person before
  it trains; trainer-side only, never in the runtime. **Build:** 2 pw.

### 10.11 Synthetic data — "Make test defects"

- **Status:** Partial: codes (`ml/synth.py`, `ml/violations.py`) and text (`ml/ocr/synth_*.py`).
- **Methods:** procedural defects (scratch, stain, missing print, foreign particle, torn corner,
  smudge, missing tablet) pasted on good images with Perlin masks (DRAEM [O], CutPaste [O]);
  learned defects by AnomalyDiffusion on the T4 trainer [O] (weights licence to check, DL-22);
  rendering of the owner's artwork with print defects for label tools.
- **Rule:** synthetic images validate and stress tools; they never count toward the escape bound
  (≤ 0.1 %), which is claimed only on real plus staged bad parts (section 0.1). The report shows
  synthetic and real recall separately (DL-17).
- **Accuracy:** synthetic recall within 2 points of real recall on the same tool, else the generator
  is retuned. **Build:** 3 pw.

### 10.12 Model acceptance gate

- **Status:** Partial: `ml/retrain.py` (no regressions on a golden set, then a named approver) and
  `ModelRegistry.Promote`; ADL 4 and 5.4 design.
- **Gate (all must hold, on a frozen held-out set never used in Teach):**

| Check | Pass rule |
| --- | --- |
| Escapes | 0 labelled bad parts pass at the deployed threshold (line safety; tighter than ADL 5.4's "not higher") |
| False rejects | ≤ deployed version + 0.1 points, and ≤ the job's limit (0.5 % default) with its 95 % interval shown |
| Shifted set | outlier flag on ≥ 95 % of ± 40 % light images, ≤ 1 % of normal (DL-01) |
| Calibration | ECE ≤ 5 % (DL-02) |
| Reduced precision | INT8 / FP16 vs FP32: no new escape, FR + ≤ 0.1 points, 99 % of scores within 2 points (ADL 4) |
| Time | p99 latency on the target tier keeps the job within its cycle time with 20 % margin |
| Stored image set | job's OK / NG image set re-run: no verdict changes to OK on a bad image (Keyence VS-G re-check [O]) |
| Licence register | every weight and library on the DL-22 register |

- **Output:** `report.json` with the validation-set hash, signed into the model package (10.17).
- **Build:** 2 pw (gate runner shared by all tools and by P.4 / P.7).

### 10.13 Confidence calibration

- Temperature scaling, one parameter fitted on the validation set (Guo et al.: "surprisingly
  effective" [O]); already done for OCR (T = 1.5 folded into the last layer, `ml/ocr/calibrate.py`).
  Report ECE in every model report. **Build:** 1 pw to generalise.

### 10.14 Out-of-distribution "outlier" score

- **Purpose:** every AI tool says how far the image is from its training data (DL-01) so a new
  light, lens or product gives NG, not a confident wrong answer.
- **Methods:** energy score on logits (18 % lower FPR95 than softmax on CIFAR-10 [O]) for
  classifiers; Mahalanobis distance of the global embedding to the training set for all tools
  (anomaly, detection, segmentation, OCR); simple image statistics as a first gate (brightness,
  sharpness from P.6). HALCON's `fit_dl_out_of_distribution` is the vendor parity point [O].
- **Decide:** outlier > limit → NG "picture unlike the teaching pictures" and a warning to the
  engineer. **Accuracy:** DL-01. **Speed:** ≤ 1 ms. **Build:** 2 pw.

### 10.15 Drift monitor — "Watch the line"

- **Status:** Missing (ADL 2.5 design; plant charts exist: CN line-pc-live-charts, vision-alerts).
- **Method:** per signal (scores, measured values, brightness, sharpness, PSF sigma from
  `engine/src/psf.cpp`, cycle time, outlier rate, NG rate per reason) a two-sided CUSUM against the
  first 500 parts after release; margin watch (share within 25 % of a limit); for AI, a two-sample
  test on reduced embeddings (Rabanser et al.: best of those compared [O]) on 1,000-part windows;
  DL-24: warn when the outlier rate doubles.
- **Decide:** drift never changes a verdict or a limit; it raises warning 600 to the PLC and a
  "What do I do?" page ranked by which signals moved.
- **Accuracy:** on staged drifts (light −30 %, defocus, dirty lens, new print run) the warning comes
  before the first false reject in ≥ 90 % of cases; < 1 false warning per week on a stable line.
  **Build:** 3 pw.

### 10.16 Inference runtime — ONNX Runtime, OpenVINO, TensorRT, INT8 / FP16, CPU fallback

- **Status:** Partial: OpenCV DNN, FP32, CPU only (`engine/src/ai_locator.cpp`,
  `engine/src/ocr/recognize.cpp`).
- **Best in class:** ONNX Runtime takes an ordered list of execution providers; each node runs on
  the first that supports it, else falls to the next [O]. TensorRT EP: engine cache
  (`trt_engine_cache_enable`), timing cache (reported build 108.6 s → 9.4 s for YOLOv4), INT8 with
  calibration tables or QDQ models, FP16 [O]. OpenVINO EP: CPU, GPU, NPU and AUTO / HETERO / MULTI;
  precision hint f32 / f16 / bf16; model cache dir [O]. Static INT8 QDQ for CNNs, dynamic for
  transformers; U8S8 can saturate without VNNI [O]. NNCF PTQ with about 300 calibration images [O].
- **Design:** one `vs_ai` runtime in the engine; provider order chosen at job load: TensorRT →
  CUDA → OpenVINO (GPU / NPU) → OpenVINO CPU → default CPU; engines compiled at deploy and cached by
  model hash + provider + driver (ADL 6.2); 5 warm-up runs before `Ready`; CPU FP32 always shipped
  in the package (ADL 6.3); fixed thread count per tool for bit-identical CPU results with the SDK
  (ADL 6.4).
- **Decide and safety:** provider failure mid-run → CPU path for that part, warning 603; if the CPU
  path breaks the cycle time → error 401 and NG (never a skipped tool).
- **Speed targets:** as each tool; T2 GPU under 20 ms for 5 MP (requirements 13).
- **Build plan:** ORT integration + provider selection 1.5 pw; INT8 / FP16 pipeline with the 10.12
  check 1 pw; re-export CodeNet, enhancer and OCR under the gate 1 pw; warm-up and cache 0.5 pw.
  Total 4 pw.

### 10.17 Model versions, package and rollback

- **Status:** Partial: `ModelRegistry` history with approver; `model.deployed` plant event
  (`dotnet/PlantEvents/PlantEvent.cs`).
- **Design:** `.vsm` package (ONNX + FP32 fallback + manifest + gate report + dataset ID), AES-256-GCM
  bound to the customer licence (ADL 6.5); version list per tool; one-tap rollback with the deploy
  role; each inspection record carries the model version (ADL 8). Rollback restores the exact
  previous package and threshold; the job version advances (P.18) so the audit trail shows it.
- **Accuracy:** rollback ≤ 5 s, 100 % of deploys / rollbacks in the audit trail. **Build:** 2 pw.

---

## Platform tools that every job uses

These are not licensed parent tools (except Tools 11 and 12, noted), but every job depends on
them, and the 15-minute setup target (requirements 13) and line safety are won or lost here.

### P.1 Teach wizard — "New job"

- **Purpose:** an untrained operator builds a label + date + code check in ≤ 15 minutes (UX 4.6,
  steps Camera → Show part → What to check → Teach good / bad → Output). **Status:** Partial: the
  Teach page of `dotnet/VisionStudio` teaches codes and text from a dragged box with Auto-find (CN
  [studio-teach-ng-bars](../../changes/2026-10-30-claude-studio-teach-ng-bars.md)); the Avalonia
  Studio (`dotnet/Studio`) has Run, sign-in, NG viewer and Parts, but no wizard yet.
- **Best in class:** Keyence VS-G: pick the image, mark the defect, the tool tunes itself, stored
  images re-checked after the change [O]; Cognex In-Sight / Edge Learning and MERLIC use a guided
  sequence of the same kind [S]. Nobody publishes a setup-time number.
- **Layers:**

| Layer | Step | Method | Options | Output |
| --- | --- | --- | --- | --- |
| L0 | 1 Camera | camera tiles, Auto setup = P.6 exposure / gain / white balance; light and sharpness bars | — | camera settings per product |
| L2 | 2 Show part | Grab → part outline and locator (1.2 / 10.7) | — | fixture |
| L2/L5 | 3 What to check | P.2 suggestions on the image; tap = P.3 auto-learn; toolbox drag (AS-05) | — | tools, passing |
| L5 | 4 Teach | good parts (10-30) via button **or PLC trigger**; bad parts optional; P.5 strictness slider with predicted false rejects; P.4 runs when bad parts exist | Strictness (Ess.) | limits |
| L6 | 5 Output | reject / alarm / light tower bits (PLC map), image saving, name; "Test on saved pictures" (P.19 lite) | — | job v1, released or awaiting approval |

- **Decide and safety:** "Save & Run" refuses a job where a taught bad part passes (warning turns
  into a block), where a tool is in "needs help" state, or where the job-setup checks fail (CN
  job-setup-checks). Drafts are never runnable.
- **Accuracy target:** usability test with 10 shop-floor users (requirements 13): median ≤ 15 min,
  90 % ≤ 20 min, 0 jobs that pass a staged bad part. **Build plan:** 4 pw in Avalonia (steps 3
  and 4 1.5 pw each, steps 1, 2 and 5 together 1 pw), after P.2-P.6.

### P.2 Auto-find on the whole image (AS-01) — "What should I check?"

- **Purpose:** after Grab, with nothing drawn, propose every code (type, count), every text block
  with each line's field type, the part, labels, and later holes, edges, colour areas, caps,
  blisters. **Status:** Have, first version — **PR #152, merged** (`dotnet/VisionCore/Tools/AutoFind.cs`,
  `TextTeach.cs`): one Read code tool per code type locked to type, count, length and GS1; one Read
  text tool per text block with field types (production / expiry date, date, time, lot, price,
  number, text); 18 new tests; no "Find everything" button yet. Codes: `CodeTeach` probe read on
  main (`dotnet/VisionCore/Tools/CodeTool.cs`).
- **Best in class:** open-vocabulary detectors draft proposals (Grounding DINO 52.5 zero-shot COCO
  AP, OWLv2, Florence-2) [O] — trainer-side only; vendors' "auto image selector" and suggestions
  (Keyence, Omron [O] / [S]).
- **Layers:** L2 (a) our engine readers for codes and text (exact); (b) a small proposal detector
  (ADL 2.1: part outline, hole, circle, edge, label, cap, blister card) trained on synthetic +
  owner images, confirmed by a classical tool before showing; L4 de-duplicate (IoU > 0.5), max 12
  bubbles, codes and text first; L5 nothing enters the job until tapped.
- **Corrections:** inherits the reader's (skew, perspective, blur, polarity); proposals on the
  undistorted image.
- **Accuracy target:** ≥ 99 % of codes and text blocks found and typed correctly on the owner's
  58 sample images (AS-01); 0 suggestions added without a tap. **Speed:** ≤ 1.5 s T1 / ≤ 0.4 s T2
  for 5 MP (ADL 2.1). **Build plan (on top of #152):** Find-everything button and Studio bubbles
  1 pw; proposal detector for non-code classes 1.5 pw; acceptance-rate logging 0.5 pw. Total 3 pw.

### P.3 Auto-learn for every tool (AS-02) — "One tap makes the tool"

- **Purpose:** every tool implements `Propose(image, seedRegion) → (roi, parameters, confidence)`
  (ADL 2.2) and is created already passing. **Status:** Partial: codes (`CodeTeach.Lock`), text
  (formats, date rules, letter height), OCV print limits from 10 good parts, dot size learning
  (`TextTool.LearnDots`); Locate taught from a box. Missing for every Tool 2-4, 8, 9, 10 tool.
- **Best in class:** MVTec EP 4 636 707 (pending): parameters for shape models estimated from
  labelled examples over a coarse-to-fine pyramid with robust combination (quantile, Tukey, Huber) —
  min score, contrast, rotation and scale ranges, search region [O]; adopt under the owner's statement of
  2026-10-30 (legal clearance). MVTec US 7,953,290 (expired 2026-04-21 per Google): sweep a threshold, score
  each result, keep the best [O].
- **Per-family rule (what each Propose does):** presence → measure with best Fisher ratio;
  template → median / MAD model; edge / bead → path snapped to the strongest edge pair, polarity;
  colour → mean and spread of the clicked area; AI → model size from the tier benchmark;
  locate → EP 4 636 707-style parameters from the taught instances.
- **Decide:** no passing parameter set → tool created in "needs help" (yellow "!") with the reason,
  never saved as passing. **Accuracy:** the created tool passes the taught good part 100 % and the
  margin to its limit is ≥ 3 σ. **Build:** 4 pw spread over the tool builds (0.25-0.5 pw per family
  plus a shared proposal framework 1 pw).

### P.4 Auto-tune on OK / NG images (AS-03) — "Find best settings"

- **Purpose:** search tool settings (and camera exposure, gain, light) for the widest margin between
  good and bad within the line time; apply only on acceptance. **Status:** Have, first version —
  **PR #154, merged** (`dotnet/VisionCore/Jobs/AutoTune.cs`, Studio Auto-tune page, 14 tests): tunes Find the
  part (match score) and Check text (strictness): each threshold half-way between the best bad
  score the tool alone must reject and the lowest good score; proposal kept only if a proof run
  fails every bad image on a real check, no good image newly fails and it is not slower; floors
  (match 0.40, strictness 20); never less strict unless a good image fails; refused while the line
  runs; the job is saved as a new version. Reader settings are tuned on the line by
  `dotnet/CodeReader/TuningAgent.cs` (Thompson-sampling bandits per knob); exposure and light pulse
  by `CalibrationSearch.cs`.
- **Best in class:** Keyence VS-G auto parameter tuning from a marked defect [O]; Bayesian search
  with pruning (Optuna, MIT) [O]; HALCON / Cognex parameter search in DL tools [S].
- **Extension design:** (1) every tool exposes its tunable parameters with ranges; (2) objective
  in this order: **no labelled bad image passes** (hard constraint), then maximise the margin
  (worst good − best bad, in σ of the good spread), then minimise time; (3) search: exhaustive for
  ≤ 2 parameters, Optuna TPE for more, inside a time budget (DL-18, 10 min); (4) the result shows
  the confusion table, the worst 3 good and worst 3 bad images and the validation-set hash; (5) the
  10.12 gate runner re-runs the stored set and lists every changed verdict.
- **Decide and safety:** exactly the rules of PR #154, generalised: AS-03 "never keeps a setting
  that lets a known bad image pass". A camera or light change (exposure, gain, light, lens, camera
  swap) re-runs every tool of that camera on its stored OK / NG sets through the 10.12 gate before
  the line runs again; any changed verdict blocks the change until it is accepted.
- **Accuracy:** 0 known bad passes; margin ≥ the hand-tuned margin on 3 owner products.
  **Build:** 4 pw after #154 (per-tool parameter descriptors 1.5, Optuna search 1, camera / light
  in the loop 1, report 0.5).

### P.5 Auto-tolerance — "Strictness"

- **Status:** Partial: OCV per-letter limits mean ± kσ from 10+ good parts (`engine/src/ocr/quality.cpp`).
- **Method (ADL 2.3):** robust μ (median) and σ (1.4826 × MAD) from 10-30 good parts; limit μ ± kσ,
  k = 4 default (about 63 per million two-sided under normality); Shapiro-Wilk on ≥ 20 samples, else
  empirical tail labelled "rough estimate"; tighter of drawing tolerance and statistical band; with
  bad parts, the limit moves to the midpoint when tighter.
- **Addition:** show the 95 % interval of the predicted false-reject rate from the sample count
  (with 20 samples the σ itself is uncertain by about ± 30 % [I]), and ask for more parts when the
  interval spans the job's FR target.
- **Accuracy:** predicted FR within 2× of the measured FR on 600 later good parts. **Build:** 2 pw
  (one shared library for every numeric output).

### P.6 Auto-light check — "Light and focus"

- **Status:** Partial: exposure and light-pulse search with a test burst
  (`dotnet/CodeReader/Calibration.cs`, `CalibrationSearch.cs`, `TestBurst.cs`); plain-word advice
  for under / over exposure and glare (`LineAdvisor.cs`); miss reasons with measured sharpness,
  contrast, saturation (`engine/src/diagnose.cpp`, after expired Cognex US 9,552,506 /
  US 10,061,946); no live focus bar, no motion-blur measure.
- **Method (ADL 2.4):** histogram clipping, Michelson / RMS contrast in the part ROI, saturated
  blobs (glare), variance of Laplacian and edge width (focus), directional blur ratio (motion),
  brightness vs taught image; one message at a time, worst first; every Nth image in Run (50).
- **Accuracy:** right first message on ≥ 95 % of staged faults (too dark, too bright, glare, defocus,
  motion, dirty lens), 0 production stops caused by it (advice only). **Speed:** ≤ 20 ms.
  **Build:** 2 pw (focus bar and motion measure 1, Studio step-1 bars 1).

### P.7 Learning while running (AS-04)

- **Status:** Partial: the reader's learned state per product (step win / try tables, zone map,
  sizes, printer encoding; `engine/src/learned.cpp`, saved every 50 cases); `TuningAgent.cs`
  explores only on unlocked products; `ml/retrain.py` slow loop with gate and approver.
- **Rule (requirements AS-04, DL-06):** what decides OK / NG never changes during a running job.
  What may learn during the run: search order and effort (speed only), sizes and encodings used to
  *find* things, drift baselines. What learns only between runs: limits, thresholds, models — each
  through 10.12 on the product's golden images (no worse, no wrong read, no slower) plus a named
  approver; previous version one tap away.
- **Addition:** "learn candidates" queue: the runtime records proposed new limits / models
  (from the last N parts and NG review verdicts) as candidates; the engineer sees "a better setting
  is ready" and starts the gate. Cognex US 12,361,100 (run-time candidate images) is adoptable
  under the clearance; the safety rule above still applies.
- **Accuracy:** 0 verdict-deciding changes during a run (audit check); every promoted change no
  worse on golden images. **Build:** 3 pw.

### P.8 Result logic and logic blocks — "Decide"

- **Purpose:** combine tool results into the part's OK / NG and reason, per camera and per part,
  with configurable but safe rules. **Status:** Partial: fixed "all must pass" combination and the
  line-safety rules in `dotnet/VisionCore/Plc/PlcRuntime.cs` (link loss NG, CN
  [plc-link-loss-ng](../../changes/2026-10-30-claude-plc-link-loss-ng.md)); no logic-block editor.
- **Design:** typed blocks AND, OR, NOT, k-of-n, compare, range, count, "first NG reason" (priority
  list), latch; inputs are tool outputs (`Pass`, values, `NotSure`, `Error`); evaluated once per
  part after all tools. **Fixed safety layer underneath the user's logic:** any tool `Error`, any
  `NotSure` mapped to NG, any missing camera image, timeout or licence fault forces NG regardless of
  the user's blocks; the user's logic can only add NG conditions or choose among NG reasons, it
  cannot turn those into OK.
- **Outputs:** `Pass`, `NgReason` (code + text key), per-tool bits to PLC.
- **Accuracy:** 100 % of truth-table tests; mutation test that no block arrangement turns a forced
  NG into OK. **Speed:** ≤ 0.1 ms. **Build:** 2.5 pw.

### P.9 Scripting and formula (Tool 12.1, 12.2) — "Custom rules", "Calculate"

- **Status:** Missing. **Design (TS 12.1-12.2):** formula = expression parsed once to a typed tree
  (≤ 0.02 ms); script = C# (Roslyn, restricted load context) or Python (embedded, module
  allow-list) in a **separate sandbox process** with a typed message channel, time limit 20 ms,
  memory limit 256 MB, watchdog restart. Images read-only.
- **Decide and safety:** script throw, timeout or sandbox restart → that part NG with error 400;
  scripts sit under the P.8 safety layer (cannot make a forced NG OK); scripts are part of the job
  version and the audit trail (P.16) and need the Engineer role.
- **Accuracy:** 1 million-part soak with a random-failing script: 0 runtime stops, 100 % of those
  parts NG. **Speed:** call overhead ≤ 0.5 ms C#, ≤ 2 ms Python. **Build:** 4 pw (formula 1, sandbox
  and C# 2, Python 1).

### P.10 State machine and sequencing (Tool 12.3) — "Sequence"

- **Status:** Missing; PLC multi-light sequences are designed in PLC 5.3.
- **Design:** table-driven states and transitions evaluated once per part after all tools;
  built-in templates: "N NG in a row → stop request" (`explain.state.alarm`), "alternate recipes",
  "multi-light steps 1..n then judge", "first-off approval after job change"; graphical editor in
  the job designer; optional persistence across restart (off by default).
- **Decide and safety:** a sequence step out of order → error 113 and NG (PLC 9); unknown state
  after restart → reset to the start state and NG for the part in flight.
- **Build:** 2.5 pw.

### P.11 SPC and trends (Tool 12.4) — "Trends"

- **Status:** Partial: Cp / Cpk and trends in the report generator (`dotnet/VisionReports`,
  `ReportData.cs`); plant live charts (CN line-pc-live-charts); no per-part run rules.
- **Method:** Welford running statistics; X̄-R / X̄-S on subgroups (5); Western Electric rules: one
  point beyond 3σ; 2 of 3 beyond 2σ; 4 of 5 beyond 1σ; 8 in a row on one side; plus 6 in a row
  trending and 14 alternating [O, [NIST](https://www.itl.nist.gov/div898/handbook/pmc/section3/pmc32.htm)];
  Cp, Cpk from limits; slope per hour.
- **Decide:** never fails a part; raises warnings (600) and feeds 10.15. **Accuracy:** rule results
  identical to a reference implementation on the NIST examples. **Build:** 2 pw.

### P.12 Multi-camera job and sync (Tool 11.1, 11.2) — "Add camera", "Fire together"

- **Status:** Partial: product master child / parent cameras (`dotnet/CodeReader`), camera
  channels with one rejector (PLC 3.5); **open PR #147** (claim) builds the two-camera Vision Studio
  product end to end (camera 1 small QR / DM, camera 2 1D + 3-4 line laser batch code, live with
  PLC); **open PR #148** (claim) the 225 parts/min timing budget and soak. Action-command sync,
  stitched jobs: Missing.
- **Best in class:** GigE Vision scheduled action commands with PTP (device key, group key, mask)
  [O, [Basler](https://docs.baslerweb.com/action-commands)]; OPC 40100 treats a vision system as
  one state machine regardless of camera count [O].
- **Layers:** L0 per-camera acquisition thread; scheduled action command at T = now + 2 ms on PTP
  time (PLC 5.4) or sequential software triggers (USB3 — the current MV-CS200-10UM is USB3, so no
  action commands); L2-L3 one tool-chain worker per camera; L4 join by trigger ID / part ID / shift
  register; combine rule (all must pass); L6 one combined result per part.
- **Corrections:** timing jitter (exposure timestamp per camera; PLC encoder compensation, PLC 6.2),
  skew between cameras (measured; above the limit → error 304 and NG), different camera resolutions
  (each tool in its own camera's calibration).
- **Decide and safety:** one camera's image missing or late → part NG (`explain.multicam.missing`);
  results joined to the wrong part are the worst failure: each result carries trigger ID and part
  ID, and a mismatch is NG + alarm.
- **Accuracy:** 0 part mismatches in a 1-million-part soak; skew ≤ 100 µs with PTP GigE (≤ 10 µs
  target in PLC 5.4). **Speed:** ≤ 2 ms overhead per part; 225 ppm = 267 ms per part per camera
  budget. **Hardware:** for synchronised jobs, GigE cameras with PTP and a NIC per camera
  ([05-HARDWARE.md](05-HARDWARE.md) section 7). **Build:** 5 pw after #147 (sync 2, stitched 2,
  soak tools 1).

### P.13 PLC handshake and safety

- **Status:** Have for Modbus TCP with Delta DVP: blocks and runtime (`dotnet/VisionCore/Plc/`
  `PlcRuntime.cs`, `ModbusServer.cs`, `PlcBlocks.cs`, `LineDiagnostics.cs`, `LineSettings.cs`,
  `LineProfiles.cs`; [PLC-DELTA-DVP.md](../PLC-DELTA-DVP.md)); link and heartbeat loss → NG with
  error 100 (PR #141). Missing: PROFINET, EtherNet/IP, S7, MC, FINS (PLC 2), counter mode tests on a
  real line, latency measurement on the line (PLC 6.1).
- **Rules (PLC 1, 4, 5):** the part is NG unless vision says OK in time; four-phase handshake or
  counter mode (queue 4, fifth rejected with 110); two-way heartbeat, watchdog 1,000 ms; never OK
  across a link loss; trigger while busy → 110 and NG; results carry `TriggerIDEcho`.
- **Accuracy:** 0 OK across link loss in fault-injection tests (cable pull, switch power, runtime
  kill) × 1,000; 0 lost results in a 1-million-trigger soak; latency ≤ 10 ms typical,
  jitter ≤ 6 ms (PLC 6 targets). **Build:** other protocols 6 pw (S7 via Snap7 1, MC 1, FINS 1,
  EtherNet/IP adapter on OpENer 1.5, PROFINET stack choice and integration 1.5).

### P.14 Outputs: OPC UA, MQTT, MES / SQL, web HMI (Tool 11.3-11.6)

- **Status:** Partial: MQTT ingest and the plant event schema on the plant server
  (`plant_server/backend/mqtt_ingest.py`, CN pi1-event-schema); view-only browser dashboard and
  the mobile NG review; OPC UA server, SQL / MES writer: Missing.
- **OPC UA design:** implement the OPC UA Machine Vision companion specification (OPC 40100-1):
  `VisionStateMachineType` with Preoperational, Halted, Error, Operational; automatic-mode states
  Initialized, Ready, SingleExecution, ContinuousExecution; methods StartSingleJob, StartContinuous,
  Stop, Abort; recipe management (AddRecipe, PrepareRecipe, …, PrepareProduct) mapped to jobs and
  products; result management (GetResultById, GetResultListFiltered) [O,
  [OPC 40100](https://reference.opcfoundation.org/MachineVision/v100/docs/8)]; open62541 (MPL 2.0),
  Basic256Sha256 sign and encrypt. Triggering over OPC UA obeys the same "PLC has control" rule
  (error 120).
- **MQTT / SQL / MES:** local store-and-forward queue, at-least-once with a record ID for
  de-duplication; never blocks inspection (TS 11.4-11.5).
- **Accuracy:** 0 lost records across a 24 h broker / database outage with a full queue test;
  OPC UA conformance with the companion spec's nodeset. **Build:** 6 pw (OPC UA 3.5, SQL / MES 1.5,
  station-side MQTT 1).

### P.15 Image and result logging

- **Status:** Have: per-part results stream and Parts list in Studio (PR #151, CN
  [studio-part-list](../../changes/2026-10-30-claude-studio-part-list.md)); NG image store with quota
  and the line-PC guard that gives way to inspection
  (`dotnet/StationGuard/`, CN [line-pc-guard](../../changes/2026-10-29-claude-line-pc-guard.md));
  plant events and NG images to the server on request ([NG-REVIEW.md](../NG-REVIEW.md)).
- **Additions:** image-saving modes all / NG / every Nth / none per job (requirements 12); lossless
  storage for images used in training; record per part: job version, model versions, tool values,
  NG reason, times (for Part 11 records, P.16); traceability search by code or time.
- **Accuracy:** every triggered part has a record (gap rows "N parts not in this list" already in
  #151); inspection never waits on logging. **Build:** 2 pw.

### P.16 Audit trail and e-signatures — 21 CFR Part 11, EU GMP Annex 11

- **Status:** Partial: the old Dashboard has an audit trail page (`dotnet/Dashboard/Components/Pages/Audit.razor`,
  `CaseLog.AuditTrail`), approvals of locked products and AI models by the approver role
  (`UserStore.cs`: "Approves locked products and AI models (e-signature)"), and the plant event
  `job.changed` carries a `SignatureId` "in 21 CFR Part 11 mode"
  (`dotnet/PlantEvents/PlantEvent.cs`). Missing: the audit trail in Vision Studio and the runtime,
  tamper evidence, signature manifestation, two-component signing, reason-for-change prompts.
- **What the rules ask, mapped to the design:**

| Rule | Text (short) | Our design |
| --- | --- | --- |
| 21 CFR 11.10(a) | validate the system, detect invalid or altered records [O] | P.19 benchmark + IQ/OQ scripts; record hash chain |
| 11.10(b), (c) | accurate, complete copies; records retrievable for the retention period [O] | PDF / CSV export of records with images; retention setting |
| 11.10(d), (g) | access limited; authority checks [O] | P.17 roles, every action role-checked |
| 11.10(e) | secure, time-stamped audit trail of create / change / delete that does not obscure earlier entries [O] | append-only audit table, each row hashed with the previous row (hash chain), UTC + local time, user, old / new value, reason |
| 11.10(f) | operational system checks enforce the sequence of steps [O] | job lifecycle draft → test → approve → release (P.18); P.10 sequences |
| 11.10(h) | device checks on the source of inputs [O] | camera serial and PLC identity recorded; unknown camera → job not ready |
| 11.50 | signed record shows printed name, date and time, meaning (review, approval, responsibility) [O] | signature block on screen and in every report |
| 11.200(a) | non-biometric signatures: two components (ID + password); continuous session: first signing all components, later at least one private component [O] | sign dialog asks user ID + password the first time in a session, password afterwards; PIN-only sign-in is not a signature |
| Annex 11 clause 9 | audit trail of GMP-relevant changes and deletions with the reason, based on risk [S] | reason is mandatory for limit, model, job and user changes |
| Annex 11 clause 14 | electronic signatures equivalent to hand-written, permanently linked to the record, with time and date [S] | signature row linked by record hash |

- **Decide and safety:** in Part 11 mode, a job, limit or model change without a valid signature is
  not released; the audit store being unwritable stops the change (not inspection).
- **Accuracy:** 100 % of GxP-relevant actions recorded in a scripted test of every Studio action;
  tampering with any row detected by the hash-chain check. **Build:** 4 pw (audit store and chain 1.5,
  signature dialog and manifestation 1, reports 0.5, tests 1). Details of the regulations:
  [03-STANDARDS-AND-DOCUMENTS.md](03-STANDARDS-AND-DOCUMENTS.md).

### P.17 User roles — "Sign in"

- **Status:** Partial: Avalonia Studio roles `Operator, Qa, Supervisor, Engineer, Admin`
  (`dotnet/Studio/Runtime/RuntimeModels.cs`), sign-in card (CN
  [studio-signin-card](../../changes/2026-10-30-claude-studio-signin-card.md)); requirements 12 role
  table (Operator / Supervisor / Engineer / Admin; PIN, password, RFID, Windows account).
- **Design:** one permission matrix (requirements 12 plus QA: review NG, sign releases); every
  command in Studio and the remote API checks it in the runtime (not only in the UI); session
  timeout; lock-out after failed attempts (the plant server already does 5 per user per device in
  15 min, NG-REVIEW 5). **Build:** 1.5 pw.

### P.18 Job versioning and release

- **Status:** Partial: `dotnet/VisionCore/Jobs/Job.cs` keeps every saved version in
  `jobs/history/<name>/v<n>.json` and taught patterns per version; PR #154 saves tuned settings as a
  new version; job save and reload (requirements 8).
- **Design:** lifecycle draft → tested on stored images → awaiting approval → released → retired;
  only released jobs run (error 201 otherwise); diff view between versions; rollback = a new version
  equal to an old one (history never rewritten); version and hash in every result record.
- **Accuracy:** rollback restores a bit-identical job (hash test). **Build:** 2 pw.

### P.19 Built-in benchmark — "Will it keep up?"

- **Status:** Partial: developer bench (`tools/bench/batch_check.py`, `bench.cpp`, `ocr_bench.cpp`,
  `truth/batch-baseline.csv`; load-aware, "not judged" on a busy Mac, CN
  [batch-check-load](../../changes/2026-10-30-claude-batch-check-load.md)); Studio Test and Batch
  pages run a job over saved images. Missing: the in-app benchmark of requirements 11 ("runs the
  real job on the current PC and reports meets or misses").
- **Design:** run the job on its stored image set N times on this PC; report p50 / p99 / max per
  tool and per part against the cycle time (225 ppm → 267 ms per part per camera); recommend a tier
  (T1-T3) and the AI model size; record CPU load before the run and refuse to judge when busy (as
  the bench does); also the job's accuracy on its OK / NG set (the AS-06 "every result recorded").
- **Accuracy:** predicted p99 within 10 % of the line's measured p99. **Build:** 3 pw.

### P.20 NG review loop — "Review rejects"

- **Status:** Have: [NG-REVIEW.md](../NG-REVIEW.md), PR #71 — NG images (≤ 300 KB) pulled by the
  plant server, verdicts confirmed / overruled / unsure on web and mobile, history kept, overrule
  rate per line, CSV export "for the line PC's training export"; Studio NG viewer
  (`dotnet/Studio/Views/NgViewerView.axaml`). Partial: the import of verdicts into a training
  dataset (10.9 Collect) and into the auto-tolerance / auto-tune sets.
- **Design:** overruled NG → candidate good image for that tool's dataset, entered only after an
  Engineer or QA confirms it and the dataset passes the 10.12 gate; never directly into the
  auto-tolerance or auto-tune sets; confirmed NG → bad set with the reason as label; "unsure" → engineer queue (DL-08). Verdicts never change what the line
  did. Overrule rate per tool and reason becomes the measured false-reject rate on the dashboard,
  the number requirements 13 asks for.
- **Accuracy:** every NG reviewed within a shift on pilot lines; measured FR per tool shown weekly.
  **Build:** 2 pw.

---

## 2. Build order for this chapter

1. **P1 / P2 platform first** (needed by the reading products now): P.13 hardening, P.15, P.17,
   P.18, P.8 safety layer, P.19 in-app benchmark, then P.1 wizard with P.2 (first version in #152), P.3,
   P.4 (first version in #154), P.5, P.6, P.7, P.16 (pharma customers), P.20 import. About 40 pw.
2. **P3 AI runtime and gate** before any AI tool: 10.16, 10.12, 10.13, 10.14, 10.17, 10.9 (about
   17 pw); then 10.1 anomaly with 8.4b, 10.3, 10.8, 10.2, 10.4, 10.5, 10.6, 10.7, 10.10, 10.11, 10.15.
3. **P4 classical Inspect and Colour** after Tools 2-4 of [04a](04a-TOOLS-BASE-GEOMETRY.md): 8.0,
   8.1, 8.2, 8.6, 8.7 (pharma first), 8.8, 8.9, 8.10, 8.3, 8.4, 8.5, 8.11; 9.c, 9.0-9.6 when a colour
   camera is bought.
4. **P4 enterprise:** P.12 sync and stitching, P.14 OPC UA / SQL / MES, P.9, P.10, P.11.

Owner inputs that decide accuracy: real and staged bad parts per product (section 0.1), a colour
camera and chart for Tool 9, a GPU on the line PC for the accurate AI modes, and a T4 training PC or
the plant server for training.

## Not opened / limits

- EU GMP Annex 11: the official PDF was fetched but its text could not be extracted; clause 9 and
  14 wording is from search summaries only [S]. 21 CFR 11.10, 11.50 and 11.200 were opened (Cornell
  LII); 11.70, 11.100, 11.300 were not.
- HALCON `compare_variation_model`: page opened but the threshold formula did not render; the rule
  |c − i| > max(abs, var·v) is the textbook form, to be checked against `compare_ext_variation_model`.
- PaDiM: only the arXiv abstract was read (per-position Gaussian and Mahalanobis are from prior
  knowledge [I]).
- Cap inspection vendors (Acquire Automation CapQ, Silgan, Mettler Toledo CI-Vision, Omron case
  study) and blister vendors (Optel BlisterSafe, others) were seen only in search results [S]; the
  CapQ article returned HTTP 403. Only the Pfizer / Mettler Toledo X-ray article was opened.
- OPC 40100: section 8 opened (states and methods); the remaining 36 % of that page and the result
  data types were not read.
- From the earlier research files (opened there, not re-opened here): Dinomaly, EfficientAD,
  PatchCore, MVTec AD 2, VisA, SAM 2, Grounding DINO, OWLv2, Florence-2, RT-DETR, DRAEM, CutPaste,
  AnomalyDiffusion, temperature scaling, energy score, Rabanser drift, NNCF, ONNX Runtime
  quantisation, HALCON 25.11 notes, Keyence VS-G, Landing AI, Teledyne Astrocyte, and the patents
  US 12,182,721, US 12,548,135, EP 4 145 401, EP 4 636 707, US 7,953,290, US 12,361,100,
  US 12,315,277, US 11,367,225, US 11,562,479, US 12,482,244, US 11,836,909, US 8,971,663,
  US 5,850,466, US 6,983,065, US 6,993,177. Patent claim texts were mostly truncated in those reads.
- Search-only [S]: MVTec Deep Counting, Zebra Aurora image counts, Omron FH AI Fine Matching image
  counts, Cognex Few Sample, Cognex Edge Learning (HTTP 429).
- No timing in this chapter is measured on our hardware; all ms figures are targets (TS cross-tool
  table, ADL 3.9). CPU latency of PatchCore, Dinomaly and INT8 transformers is unknown until 10.1
  step 1 measures it.
- Effort figures are planning estimates; the escape-rate sample sizes in 0.1 are our arithmetic.
- Open PRs #147 and #148 were read from their descriptions only, not their diffs. PRs #152 and
  #154 are merged; their files (`AutoFind.cs`, `TextTeach.cs`, `AutoTune.cs`) are on main.
