# Research: ViDi, SuaLab and Cognex deep learning

This note studies the deep-learning software that Cognex bought (ViDi Systems in 2017, SuaLab in 2019) and what Cognex has since built on it. It then lists what our product should add, and proposes new requirement bullets. It feeds [section 4](../REQUIREMENTS-VISION-SYSTEM.md#4-tool-library) and [section 5](../REQUIREMENTS-VISION-SYSTEM.md#5-ai-and-deep-learning) of the requirements and [AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md).

Method and limits:

- Public sources only: Cognex help pages (docs.cognex.com), press releases, trade press, Google Patents. No software was installed or downloaded. Several Cognex marketing pages refused automated access (HTTP 429), so some Edge Learning facts come from search-result summaries of those pages; these are marked "(search summary)".
- Each fact is marked **[S]** stated by a public source, or **[I]** inferred by us. Inferences are our engineering reading, not Cognex statements.
- Patent notes are an engineering triage, not legal advice. A freedom-to-operate (FTO) opinion from a patent attorney is needed before P3 ships.
- Version numbers drift. Cognex help exists for ViDi 3.4/4.1, VisionPro Deep Learning (VPDL) 1.0 to 4.1 and In-Sight ViDi 1.x. Names changed between versions (see 2.4).

## 1. Summary

1. ViDi's core idea [S] was a family of four colour-coded tools: Red Analyze (defect segmentation or anomaly), Green Classify, Blue Locate and Blue Read (OCR). Each tool is one neural network, trained from labelled images on an NVIDIA GPU, and tools can be chained.
2. The ViDi "Focused" architecture [S] samples the image in patches whose size is the user's "feature size" and whose spacing is set by "sampling density". The network sees about five times the feature size as context, with more detail at the centre. [I] This is a foveated, patch-based CNN run as a sliding window, which explains its speed and its known weakness: small or thin defects are missed if the feature size is wrong.
3. SuaLab's SuaKIT [S] added full-image ("High Detail") segmentation and classification, detection, a visual labeller that suggests defect areas, a visual debugger (heatmap), one-class learning, continual learning, multi-image input and an uncertainty score. Cognex says High Detail mode came from SuaKIT.
4. VPDL 4.x [S] reorganised the tools into "Standard" (Fast, Accurate, Robust, Few Sample) and "Legacy" (the old Focused) types, added NVIDIA TensorRT, an outlier score, a few-sample classifier (one image per class) and a validation-loss view.
5. Edge Learning [S] (In-Sight 2800/3800) trains pre-trained networks on the camera from 5 to 10 images in minutes. OneVision (2025) [S] moves training and model management into the cloud.
6. Known limits [S, from Cognex's own docs]: NVIDIA GPUs only, a USB dongle always connected, no virtual machines, sensitive to GPU driver versions, High Detail cannot run on CPU, unsupervised mode is weak on line defects and on part variation, the pretrained OCR covers only digits, upper-case Latin (without "O") and five symbols, and TensorRT disables the heatmap and outlier score.
7. Patents: the Cognex families that matter most to us cover **(a)** a GUI that offers candidate images for incremental training based on confidence and **(b)** runtime images suggested as new categories that update the model (US 12,315,277 B2 and US 12,361,100 B1), **(c)** a pattern finder combined with a neural classifier (US 11,568,629 B2) and **(d)** a SuaLab Siamese "master versus test image" comparison network (US 10,937,141 B2). Our auto-label, "Use for teaching" and AI locator designs need care against (a) to (c).
8. Our current stack (PyTorch to ONNX, ONNX Runtime with TensorRT/OpenVINO, PatchCore/EfficientAD, CTC OCR) already matches or exceeds the public Cognex architecture on runtime openness (CPU path, Intel iGPU). Our gaps are: an outlier/"not sure" score on every AI tool, a few-shot quick-train mode, foundation-model assisted labelling, OCR for dot-matrix and Indian scripts with per-character boxes, and a multi-image (multi-light) input.

## 2. ViDi Systems and VisionPro Deep Learning

### 2.1 Company and timeline

| Date | Event | Source type |
| --- | --- | --- |
| 2012 | ViDi Systems SA founded in Switzerland by Dr Reto Wyss [S] | Trade press |
| 4 April 2017 | Cognex closes the purchase of ViDi; team and Swiss site kept; terms not material [S] | Business Wire release, EMVA, Vision Systems Design |
| 2017–2019 | Product sold as "Cognex ViDi Suite" / "VisionPro ViDi", versions 2.x to 3.4 [S] | docs.cognex.com |
| April 2020 | In-Sight D900 smart camera runs ViDi Read, ViDi Check, ViDi Detect on the camera; training on a GPU PC [S] | Press release copies, datasheet |
| c. 2020 | VPDL 1.0, called "direct successor to VisionPro ViDi" (Cognex docs) [S]; exact release date not confirmed | docs.cognex.com |
| July 2021 | VPDL 2.0 integrated with VisionPro 10.0; deep-learning OCR added to the VisionPro library [S] | Press release, trade press |
| 2023–2025 | VPDL 3.x and 4.x; TensorRT, Standard/Legacy types, Few Sample mode [S] | docs.cognex.com |

### 2.2 Workflow (ViDi Suite / VPDL)

| Step | What happens [S unless marked] |
| --- | --- |
| Collect | Images go into a **workspace**. A workspace has one or more **streams**; each stream has its own image database and tool chain, for example one per camera view. |
| Label | Per tool: good/bad per image (unsupervised), defect regions painted (supervised), class tags (classify), points or boxes (locate), characters (read). |
| Set sampling | Feature size, masking, colour channels, border type. Changing sampling after training invalidates the model. |
| Train | On an NVIDIA GPU. Training set and test set are split inside the database. Focused tools sample "informative" areas more heavily during training. High Detail tools use a separate validation set and show training/validation loss (Loss Inspector). |
| Validate | Database overview with a confusion matrix; thresholds T1/T2; auto-threshold maximises F1 on the database. Changing the threshold does not need retraining. |
| Deploy | Export a "runtime workspace" (tools without images) and load it through the C++/C# API or as a VisionPro tool block. Client/server mode with the Advanced licence. |

### 2.3 Tools and what each solves

| Tool | Modes | Solves | Training data [S] |
| --- | --- | --- | --- |
| Red Analyze | Focused Unsupervised (later "Unsupervised") | Unknown cosmetic defects, anomalies | Good images only; bad images are ignored in training but used for testing |
| Red Analyze | Focused Supervised (later "Legacy") | Segment known defect types | Images with painted defect regions |
| Red Analyze | High Detail (later "Standard": Fast, Accurate, Robust) | Pixel-accurate defect segmentation, size and shape measurement, multi-class, line defects | Defect and good images, painted |
| Green Classify | Focused (Legacy), High Detail, High Detail Quick; in 4.x Standard Fast/Accurate/Robust/Few Sample | Whole-view classes: variant, OK/NG, defect type after Red | Labelled images; 4 per class (Standard), 1 per class (Few Sample) |
| Blue Locate | Focused | Find one or many features, count, pose; used as a fixture for later tools | Points/features marked |
| Blue Read | Focused | OCR of deformed, skewed, poorly etched text | None for the pretrained set; characters labelled otherwise |

Chaining [S]: the result of one tool crops a rectangular view for the next. Typical chains are Locate → Analyze, Classify → route to different tools, Analyze → Classify the defect type.

Other parameters [S]: "Centered" for round parts (the model is virtually unrolled around the centre), masking modes (transparent, zero, mask as extra channel), Detail 1–4 for Blue Locate (centre versus context weight), a 2-D feature size (width × height) for Blue Read, "Parameter Search" utility (Standard and Advanced licences).

### 2.4 Neural-network approach: stated facts and our reading

| Topic | Stated [S] | Our reading [I] |
| --- | --- | --- |
| Focused sampling | Feature size = typical feature diameter in pixels; network sees about 5× that area with more detail at the centre; runtime samples the image exhaustively; density 3 (default) steps by one third of the feature size; time scales with the square of the feature size; sizes below 15 px work poorly | A foveated multi-resolution patch CNN applied as a sliding window. Each sample yields a local class score or defect probability; the samples are assembled into a heatmap. Speed is set by the number of samples, not the image size. |
| Focused training | Training samples more heavily in areas that add information | Hard-example or importance sampling of patches. |
| Unsupervised | Learns good appearance; uses a sampling region; sensitive to part type and orientation; weaker on line defects | A patch-level normal model (reconstruction or feature density) on the same sampler. Line defects thinner than the sampling step are averaged away, which matches the stated weakness. Equivalent public ideas: PaDiM, PatchCore, student-teacher. |
| High Detail (from SuaKIT) | Samples the whole view; slower and more accurate; validation set; loss curve; heatmap for classify; processing output reconstructed to a 128 × 128 heatmap; needs a GPU, cannot run on CPU | A conventional encoder–decoder segmentation network and a standard CNN classifier trained by epochs, with class-activation style heatmaps. The SuaLab patent (section 4) describes a U-Net-style decoder. |
| High Detail Quick / Few Sample | Same architecture, no validation step, much faster; Few Sample works from 1 image per class | A frozen pretrained backbone with a light head or prototype (nearest class centre) classifier. |
| Robust mode (4.x) | Works across lines and product variants without retraining; no TensorRT; time grows with image size | A larger pretrained backbone, possibly transformer-based, chosen for invariance over speed. |
| Outlier score (4.x) | Shows how far a view is from the training set, e.g. after a lighting change | Feature-space distance to the training distribution (kNN or Mahalanobis style out-of-distribution score). |
| Uncertainty (API) | A per-view "Uncertainty" property exists in the runtime API | Possibly from SuaKIT's uncertainty feature; method not published. |
| Blue Read | Pretrained on many fonts and backgrounds; set 0–9, A–Z except O, and - + & : / | Character detection plus per-character classification (each character treated as a "feature"), not a line-level CTC model. Dropping "O" avoids O/0 confusion by design. |
| Sample limit | Image size, feature size and density together must stay under a "maximum samples" limit | A fixed GPU buffer per image. |

### 2.5 Hardware, runtime and licensing

| Item | Public facts [S] |
| --- | --- |
| GPU | NVIDIA only. CUDA compute capability 5.0+ (3.2), 6.1+ (3.3, 4.1). Tested: GTX 1080 Ti, RTX 2080 Ti, 3080/3090, 4080/4090, Quadro P4000. GTX 1650/1660 not recommended. |
| GPU memory | 10 GB or more recommended for training; 8 GB for runtime; High Detail needs at least 8 GB. |
| CPU / RAM | Better than Core i7; 32 GB RAM or more; SSD with 100 GB free; 50 GB+ for 15,000 images of 1K × 1K. |
| OS | Windows 10/11 Pro 64-bit, Windows Server 2019. No virtual machines. |
| Drivers | A narrow tested driver range in 3.2.1; newer drivers can slow High Detail; drivers above 546.01 need the NVIDIA "Sysmem Fallback" setting turned off. |
| Licences | Base: CPU only, inference only, not speed-optimised. Standard: one GPU, Parameter Search, options for 1-year training, runtime with training, runtime only. Advanced: training on up to 4 GPUs, runtime on up to 2 GPUs, client/server. |
| Protection | A Cognex USB security dongle must stay connected for training and runtime. |
| TensorRT | Supported in Fast, Accurate, Few Sample; not in Robust. With TensorRT the heatmap and outlier score are off, so the user chooses at export. |

### 2.6 Strengths and weaknesses

| Strengths [S or I as marked] | Weaknesses [S or I as marked] |
| --- | --- |
| One consistent tool model (Red/Green/Blue) easy to explain to engineers [I] | NVIDIA-only GPU; High Detail needs GPU even at runtime [S] |
| Tool chaining and streams cover multi-view parts [S] | Dongle must stay connected; no VM [S] |
| Focused tools are fast and need few images [S] | Feature size is a hard, expert parameter; wrong value misses small defects; retrain after any change [S] |
| Pretrained OCR works without training [S] | Pretrained OCR set is narrow: no lower case, no "O", no non-Latin script [S]. No public mention of Indian scripts [I] |
| Auto-threshold on F1, threshold change without retrain [S] | Unsupervised mode weak on line defects and part variation [S] |
| 4.x outlier score and Few Sample mode [S] | TensorRT turns off heatmap and outlier score [S] |
| Integrates with VisionPro rule-based tools [S] | Driver-version sensitivity [S]; licence tiers split training and runtime [S] |
| | We found no independent user reviews or benchmark studies in public forums; claims of "few images" and "minutes" are vendor statements [I] |

## 3. SuaLab and SuaKIT

### 3.1 Company and timeline

| Date | Event |
| --- | --- |
| 2013 | SuaLab founded in Seoul by Kiyoung Song (CEO) [S] |
| Sept 2017 | SuaKIT launched: segmentation and classification library, CUDA GPU, "no coding" [S] |
| April 2018 | SuaKIT 2.0: image comparison, detection mode, Visual Labeler (suggests defect areas), Visual Debugger (shows where the network looked) [S] |
| March 2019 | SuaKIT 2.1: continual learning (start from a pre-trained model), multi-image analysis (a set of several images as one input), uncertainty score to sort vague data [S] |
| 2019 | SuaKIT 2.3 headline seen; no public details found [S] |
| 16 Oct 2019 | Cognex announces the purchase; team and IP join Cognex; focus on electronics in Asia; price not disclosed by Cognex; trade press reports USD 195 million [S] |
| c. 2020 | SuaKIT "High Detail" mode added to VPDL 1.0; SuaKIT classification projects importable, other SuaKIT projects not yet [S] |

### 3.2 Functions

| Function | What it solves [S] | Our reading of the method [I] |
| --- | --- | --- |
| Segmentation | Pixel defect maps on electronics, displays, etc. | Encoder–decoder CNN (U-Net family) |
| Classification | Image-level classes | CNN classifier with transfer learning |
| Detection (2.0) | Several object types in one image | One- or two-stage box detector |
| One-class learning | Defects are rare; learn from normal images, helped by augmentation (decolour, flip, rotate, brightness) | Anomaly detection with heavy augmentation |
| Image comparison (2.0) | Inspect when the background or product pattern changes often | Siamese master-versus-test network; matches SuaLab patent US 10,937,141 |
| Visual Labeler (2.0) | Suggests defect areas so users do not mark every image | Model-assisted pre-labelling |
| Visual Debugger (2.0) | Shows the area and basis of the decision | Class-activation heatmaps |
| Continual learning (2.1) | Start from an earlier model for a new product | Fine-tuning / domain adaptation |
| Multi-image (2.1) | Several images of one part (e.g. different lights) treated as one sample | Channel stacking or multi-branch network |
| Uncertainty (2.1) | Score to find vague or doubtful images | Bayesian approximation (e.g. dropout sampling) or ensemble disagreement |
| OCR | Not found in any public SuaKIT source | Cognex OCR comes from ViDi Blue Read |

Hardware [S]: NVIDIA GPU with CUDA. One article claims 1,000 images of 2048 × 2048 trained in 30 minutes (another figure in the same article says 5 minutes, so the claim is inconsistent). Library with API; operating system and licence model not found publicly.

Strengths [I]: full-image segmentation suited to small, thin defects; good data tooling (labeller, debugger, uncertainty) that reduced labelling effort. Weaknesses [I]: GPU-heavy; required defect samples for the supervised modes; little public documentation after 2019.

## 4. Follow-on Cognex AI products

| Product | Public facts [S] | Notes [I] |
| --- | --- | --- |
| In-Sight D900 (2020) | Smart camera running ViDi Read, ViDi Check (assembly), ViDi Detect (defects) in the In-Sight spreadsheet; trained on a GPU PC and deployed to the camera | First on-camera deep-learning inference. |
| VisionPro 10 + VPDL 2.0 (2021) | ViDi-based deep OCR in the VisionPro library; High Detail segmenter for medical/electronics | Merges rule-based and DL in one job. |
| Edge Learning: In-Sight 2800 (2021), 2800 Detector (2023), 3800 (2023) | ViDi EL Classify, EL Segment, EL Read (multi-line text, reflective surfaces); pre-trained algorithms; 5–10 images; training on the device in minutes; confidence over 80% and stable used as a rule for "ready" (search summary) | A frozen pretrained backbone with a small trainable head, trained on the camera's processor. The "two-stage" description (factory pre-training, customer fine-tuning) supports this. |
| VPDL 3.x/4.x | TensorRT; Standard and Legacy types; Few Sample; outlier score; Loss Inspector | See section 2.4. |
| OneVision (June 2025) | Cloud platform to build, train and manage AI vision apps across sites; first on In-Sight 3800/8900; more devices in 2026 | Cloud training and fleet model management. No model types published. |

## 5. Patents

### 5.1 Families found

Dates from Google Patents; "expiry" is Google's estimate, not a legal conclusion. Territories are those listed on Google Patents; national-phase status abroad was not verified. No Indian (IN) family member was listed for any of these.

| Patent (family) | Owner | Priority | Status | Est. expiry | Core idea in our words | Risk to us |
| --- | --- | --- | --- | --- | --- | --- |
| US 12,361,100 B1 | Cognex (Wyss, Petry) | 9 Dec 2019 | Granted 15 Jul 2025, active; US only listed | 23 May 2043 | Camera system picks a **runtime** image as a candidate, **suggests a categorisation** (defect type, class or feature location), **updates the learning model** with a new or refined category and uses it for later images; a GUI shows a **candidate box** with the image and suggestion. Dependents: candidates from statistics or CNN pass/fail; training on a remote processor, inference on the camera; threshold adjustment in the GUI. | **High.** Overlaps "Use for teaching" from the NG strip plus retraining, and any automatic suggestion of new classes. |
| US 12,315,277 B2 (CIP of the above); WO 2023/075825 A1; EP 4 427 204 A1 (pending); CN 118511204 A; JP 2024-541040 A; KR 2024-0125918 A; continuation US 2026/0080702 A1 pending | Cognex (Wyss, Petry) | 9 Dec 2019 | US granted 27 May 2025, active; others pending or unknown | 9 Dec 2040 | A GUI candidate box **automatically offers unlabeled images** for **incremental training** of several DL tool types; the user labels them in a scoring process that depends on a **per-tool confidence score**. | **High.** Overlaps our auto-label "check me" list sorted by confidence. EP/CN/JP/KR matter for export customers. |
| US 11,568,629 B2; US 12,147,895 B2; WO 2019/236885; CN 112567384 B; JP 7316731 B2, JP 7649350 B2; KR 102649930 B1, KR 102915961 B1; DE 11 2019 002848 T5 | Cognex (Wang, Anand, Jacobson, Li) | 6 Jun 2018 | Granted in US, CN, JP, KR | 14 Aug 2039 | A trained **pattern-finding tool** combined with a **neural classifier** trained on the same images; the classifier either picks which templates the finder searches with, or checks the finder's matches. The network may also output a weighted mask for the matcher. | **Medium.** Our AI locator (CNN proposal then geometric refine) and auto-find (detector plus classical confirmer) are near this. |
| US 10,937,141 B2 | SuaLab → Cognex Ireland (assigned 2024) | 13 Oct 2017 | Granted 2 Mar 2021, active | 26 Apr 2039 | Two correlated images (a **master** good image and a **test** image) pass through **shared-weight sub-networks**; a comparison module (U-Net-style decoder) outputs defect information and pixel locations. | **Medium.** Avoid a learned Siamese golden-image comparator. A Korean parent is likely [I]; not verified. |
| US 10,068,153 B2 (parent US 8,873,892 B2) | Cognex (Negro, Gerst) | 21 Aug 2012 | Active | 21 Aug 2032 | A **handheld** scanner converts a **font file into synthetic images** to train its OCR, with a live-image ROI aid. | **Low.** Claims are tied to a handheld scanner. We render fonts for OCR training on a PC for fixed cameras; still note it. |
| US 11,017,259 B2 | Utechzone (Taiwan) | 29 Jun 2018 | Active | 23 Oct 2039 | Train a network on **pairs** of a normal image with and without a **randomly pasted synthetic defect**, then inspect an **edge-enhanced** image. | **Low–medium.** Only the combination is claimed. Our synthetic defects are for validation and augmentation, not this paired edge-image scheme. |

Searched but not found: any patent by ViDi Systems SA in its own name; patents on PatchCore (Amazon research code is Apache 2.0, no filing found); an MVTec patent on student-teacher anomaly detection (EP 4 145 401 A1 by MVTec exists, subject unclear; to be checked by counsel because EfficientAD comes from MVTec). "Not found" is not proof of freedom.

### 5.2 Design-around guidance versus public-domain techniques

| Idea | Status | What we do |
| --- | --- | --- |
| Sliding-window patch CNN, feature size, sampling density | Public technique (decades of literature) | Free to use; we prefer full-image networks anyway. |
| U-Net segmentation, Grad-CAM, CTC, transfer learning, augmentation, coreset, kNN anomaly scoring | Public papers (2006–2022), open code | Free to use; respect code and weight licences. |
| Confidence-ranked candidate box that feeds incremental training (US 12,315,277) | **Design around** | Labelling queue is ordered by **diversity and time**, not by model confidence, by default. Confidence sorting is an engineer's manual filter, not an automatic offer. Training only on an explicit "Train" press, never incremental in the background. Get FTO before P3. |
| Runtime image suggested as a new category and model updated (US 12,361,100) | **Design around** | Runtime never suggests a new category or updates a model. An operator may only flag an NG image "for review". A trained engineer later labels it offline; any new model passes the gate and needs a named approver. No automatic retrain on the line. |
| Pattern finder + NN classifier selecting templates or verifying matches (US 11,568,629) | **Caution** | AI locator outputs a pose directly; geometric refinement only polishes that one pose, with one template. Auto-find confirmers are classical shape fits, not trained template finders. Counsel to review. |
| Siamese master-versus-test network (US 10,937,141) | **Design around** | Anomaly detection uses a memory bank of many good patches (PatchCore) or student-teacher; no learned pairwise comparison with a single master image. The classical golden-template tool stays non-learned. |
| Font file to synthetic images for OCR training (US 10,068,153, handheld) | Low risk, note | Our synthetic OCR text is rendered on the training PC for fixed cameras. |
| Paired synthetic defect + edge-enhanced input (US 11,017,259) | Avoid the combination | Synthetic defects used as augmentation or validation; no edge-enhanced paired training. |

## 6. Comparison with our design

| Area | Cognex (public) | Ours today (design) | Gap |
| --- | --- | --- | --- |
| Anomaly | Unsupervised Red, patch sampler, weak on lines | PatchCore/EfficientAD with tiling | Add a line-defect check: run a thin-defect supervised or high-resolution tile mode |
| Segmentation | High Detail / Standard, GPU only | U-Net; SegFormer-B0 planned | SegFormer weights licence (see 7.2) |
| Classification | Standard, Few Sample, outlier score | Transfer learning, "Not sure" | Few-shot mode; outlier score on every tool |
| Detection/count | Blue Locate (points) | YOLO-class or RT-DETR | Licence choice; point-based counting for dense items |
| OCR | Blue Read pretrained, narrow set | PP-OCR + our CTC line recogniser | Dot-matrix/CIJ, Indian scripts, per-character boxes for OCV |
| Locate | Blue Locate + PatMax | Heatmap net + geometric refine | Patent caution |
| Runtime | NVIDIA only, dongle | ONNX Runtime CPU, OpenVINO, TensorRT | Our advantage; keep CPU parity |
| Data tools | Labeller, debugger, uncertainty (SuaKIT) | Auto-label, plain-word validation | Foundation-model click labelling, label-error finder |
| Multi-image | SuaKIT multi-image | Not planned | Multi-light input |
| Fleet / cloud | OneVision | Local only, T4 workstation | Plant-server option later |

## 7. What we add to our product

### 7.1 Prioritised feature table

Phases follow [section 15 of the requirements](../REQUIREMENTS-VISION-SYSTEM.md#15-roadmap-acceptance-risks-and-open-questions). Tiers T1–T5 follow section 11.

| # | Feature | Why | Model / approach (licence) | Training data | CPU/GPU fit | Phase | Patent caution |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Unsupervised anomaly detection with heatmap | Unknown defects with no bad samples; Cognex's flagship | PatchCore (anomalib, Apache 2.0) default; EfficientAD for speed; evaluate newer anomalib models (e.g. Dinomaly-class, multi-class unified). Backbone weights must allow commercial use | 20–100 good images; 10+ bad for validation | T1 with ResNet-18 + INT8; T2+ WRN-50 | P3 | Avoid Siamese master comparison (US 10,937,141); check MVTec EP 4 145 401 before EfficientAD ships |
| 2 | Supervised defect segmentation incl. thin/line defects | Measure size and shape; fixes the line-defect weakness of anomaly | U-Net / FPN via segmentation_models_pytorch (MIT) with timm encoders (Apache 2.0); tiled full-resolution mode for scratches | 20+ painted images | T2+; T1 only at small input | P3 | Public technique |
| 3 | Classification with "Not sure" and outlier score | Variant sorting, OK/NG, defect-type after segmentation | EfficientNet/ConvNeXt/MobileNetV3 (timm, Apache 2.0); temperature scaling; feature-distance outlier score | 30+ per class | All tiers | P3 | Public |
| 4 | Few-shot quick train ("Teach in 2 minutes") | Edge-Learning-style speed for unskilled operators; works on CPU | Frozen DINOv2 (Apache 2.0) or ImageNet backbone; prototype / logistic-regression head; trains in seconds | 3–10 per class | T1 CPU training in < 60 s | P3 | Training must be operator-initiated, offline; no runtime candidate-box loop (US 12,361,100) |
| 5 | Out-of-distribution and uncertainty on every AI tool | Catches new lighting, new product, dirty lens before wrong answers | Mahalanobis/kNN distance on embeddings, energy score, ensemble or MC-dropout on demand; calibration check (ECE) | Uses training set only | All tiers; < 1 ms extra | P3 | Public |
| 6 | Deep OCR: dot-matrix, CIJ inkjet, embossed, laser | Most Indian packaging dates are CIJ dot-matrix | PP-OCRv4/v5 det + rec (Apache 2.0) and our CTC line recogniser; synthetic dot-matrix renderer with dot dropout, ink spread, curved substrates | None for base; 50+ lines to fine-tune | T1 mobile models; T2 server rec | P2 | Synthetic font rendering on PC, fixed mount (US 10,068,153 is handheld only) |
| 7 | Indian-script OCR (Devanagari, Tamil, Telugu, Kannada, Bengali, Gujarati, Gurmukhi, Malayalam, Odia) | Market need; Cognex publishes no Indian scripts | PaddleOCR multilingual recognisers (Apache 2.0) where they exist; our own CTC models trained on rendered text with open fonts (OFL fonts e.g. Noto) | Synthetic + 200+ real lines per script | T1 CPU | P2 (Devanagari), P4 (others) | Public |
| 8 | Per-character boxes and confidence for OCV | OCV must say which character is wrong | CTC alignment peaks or attention maps; split line into character cells | Same as OCR | All | P2 | Public |
| 9 | Object detection and counting | Missing components, counting in trays | RT-DETR (Apache 2.0, PaddleDetection), D-FINE (Apache 2.0), YOLOX (Apache 2.0), RF-DETR (Apache 2.0 for most sizes, check each size). Not Ultralytics (AGPL-3.0). Point-based counting head for dense small items | 50+ labelled images | T2+; nano on T1 | P3 | Public |
| 10 | AI locator | Parts where geometric match fails | Centre heatmap + angle/scale maps; classical sub-pixel polish of the single pose | 5–10 images + augmentation | All | P3 | US 11,568,629: no template selection or match verification by a trained classifier |
| 11 | Click-to-mask labelling (foundation model) | Cuts painting time by about 5–10× [I] | SAM 2 / SAM 2.1 (Apache 2.0); EfficientSAM or MobileSAM (Apache 2.0) for CPU. SAM 3 under a custom Meta licence: legal review first | None | T4 / T2 GPU in the trainer only; not in the runtime | P3 | Public |
| 12 | Text-prompted pre-labels ("find all screws") | Faster first labels for detection; drafts for auto-find | Grounding DINO (Apache 2.0), OWLv2 (Apache 2.0), Florence-2 (MIT). DINOv3: custom licence, review | None | Trainer GPU only | P4 | Public |
| 13 | Auto-labelling and active learning (patent-safe) | Less labelling | Model pre-labels after the user presses "Pre-label"; queue ordered by embedding diversity (k-centre) and time, not by confidence; engineer can filter by confidence manually | Existing labels | Trainer | P3 | Core design-around for US 12,315,277 / 12,361,100; FTO required |
| 14 | Label-error finder | Wrong labels are the commonest cause of poor models [I] | Our own implementation of confident-learning ideas (published paper); do not ship cleanlab (AGPL-3.0) | Labelled set | Trainer | P3 | Public |
| 15 | Synthetic defect generation | Rare defects; validation of anomaly tools | Procedural scratches, dents, stains, CutPaste-style patches, Poisson blending; diffusion inpainting only with permissive weights (check OpenRAIL terms) | Good images + a few real defects | Trainer GPU | P3 (procedural), P4 (diffusion) | Avoid US 11,017,259 combination |
| 16 | Model compression for CPU | T1 entry PCs without GPU | INT8 PTQ with OpenVINO NNCF (Apache 2.0); knowledge distillation from T4 teacher to small student; structured pruning | 100–500 calibration images | T1 | P3 | Public |
| 17 | Explainability heatmaps | Operator trust; Cognex loses heatmaps under TensorRT | Native anomaly maps; Grad-CAM (pytorch-grad-cam, MIT) on NG only; three nearest training images | None | Off the cycle-time path | P3 | Public |
| 18 | Drift monitoring for AI | Warn before rejects rise | Already designed (CUSUM, KS); add embedding drift (MMD on features) and rising outlier rate | Baseline window | All | P3 | Public |
| 19 | Multi-image / multi-light input | Photometric stereo, dark-field + bright-field; SuaKIT had it | Stack aligned images as channels; or shared encoder with late fusion | Same as base tool | T2+ | P4 | Public |
| 20 | Tool chaining and routing | Classify → route to the right inspection | Existing job flow; crop by locator pose | – | All | P3 | Public |
| 21 | Automatic hyper-parameter search | Replaces expert tuning (Cognex Parameter Search) | Small grid / Bayesian search (Optuna, MIT) under a time budget | Validation set | T4 | P3 | Public |
| 22 | Continual learning for new variants | New SKUs every month in Indian plants | Fine-tune from the previous model with replay of old images; gate prevents regressions | New + replay | T4 | P4 | Offline, approved; not a runtime loop |
| 23 | Masking and round-part unwrap before AI | Ignore printed logos, edges; round parts | Mask as zero or extra channel; polar unwrap (Base tool) | – | All | P3 | Public |
| 24 | Natural-language and vision-language assistant | "Check the cap is on" drafts | Local small VLM (permissive licence only) on T3/T4; online optional | – | T3/T4 | P5 | Public |
| 25 | Plant-server training and fleet models | Many lines, one model library (answers OneVision) | Trainer as a service on a plant server; signed model packages | – | Server GPU | P4 | Public |

### 7.2 Licence notes found during this study

| Item | Licence | Action |
| --- | --- | --- |
| Ultralytics YOLO (v5/v8/11) | AGPL-3.0; paid enterprise licence | Do not ship. Trained weights stay under AGPL per vendor. |
| SegFormer official weights (NVIDIA) | NVIDIA source code licence, non-commercial [I: from our prior knowledge, check] | Our AI-DEEP-LEARNING.md lists SegFormer-B0 on T3. Replace or train the encoder from permissive weights. |
| SAM 3 | Custom Meta "SAM License", royalty-free, use restrictions, revocable terms | Legal review; SAM 2 (Apache 2.0) is the safe default. |
| DINOv3 | Custom Meta licence | Legal review; DINOv2 (Apache 2.0) is the safe default. |
| RF-DETR | Apache 2.0 for most sizes; some sizes under a platform licence | Use Apache sizes only. |
| cleanlab | AGPL-3.0 | Re-implement from the paper. |
| anomalib, PatchCore reference code, PaddleOCR, PaddleDetection, timm, ONNX Runtime, OpenVINO | Apache 2.0 / MIT | Fine; still check each weight file. |
| ImageNet-pretrained weights | Weights licence often unclear; ImageNet terms are research-only | Legal review of every shipped backbone; prefer weights with an explicit commercial licence or self-supervised on our own data. |

## 8. Proposed additions to the requirements

Ready to paste into section 5 (or a new 5.x). Each is testable and carries a phase.

1. **DL-01 (P3)** Every AI tool returns, besides its result, an outlier score 0–100 that measures distance from its training data. On the benchmark set with a changed light level (±40% brightness), at least 95% of images are flagged as outliers above the default limit, while no more than 1% of in-distribution images are flagged.
2. **DL-02 (P3)** Every AI tool can return "Not sure". The PLC result for "Not sure" is configurable (pass, fail, or separate bit). The expected calibration error of reported confidence is below 5% on the validation set.
3. **DL-03 (P3)** Anomaly detection trains from 20 good images in under 2 minutes on T2 and under 10 minutes on T1, and gives a heatmap at no less than 1/8 of input resolution.
4. **DL-04 (P3)** Anomaly detection detects scratches 3 px wide and 50 px long on a 5 MP image with tiling, with recall of at least 90% on the line-defect benchmark set, or the training screen recommends the segmentation tool when it cannot.
5. **DL-05 (P3)** A quick-train classification mode learns from 3 to 10 images per class, trains in under 60 s on a T1 CPU, and reaches at least 95% accuracy on the 5-class benchmark set.
6. **DL-06 (P3)** Training is started only by an explicit user action. No model, class or threshold changes while a job is running. (Design-around for US 12,361,100.)
7. **DL-07 (P3)** The labelling queue is ordered by image diversity and time by default. Confidence ordering is available only as a manual filter in Advanced view. (Design-around for US 12,315,277; subject to FTO.)
8. **DL-08 (P3)** An operator can mark a Run-screen image "for review". Marked images go to a review list for an engineer; they are never used for training until an engineer labels them and the model gate passes.
9. **DL-09 (P3)** Click-to-mask labelling: one click on a defect produces a mask that the user can accept; on the segmentation benchmark the mean IoU of one-click masks is at least 0.75. Runs on the trainer only, never in the runtime.
10. **DL-10 (P3)** A label-error check lists the 2% of labels most likely to be wrong; on a benchmark with 5% deliberately flipped labels it finds at least 60% of them in that list.
11. **DL-11 (P2)** AI OCR reads CIJ dot-matrix dates and lot codes (5×7 and 7×9 matrices) with a character accuracy of at least 99.5% on the CIJ benchmark set without customer training.
12. **DL-12 (P2)** AI OCR reports a box and a confidence for every character, so OCV can name the wrong character position.
13. **DL-13 (P2/P4)** Pretrained OCR ships for Latin and Devanagari in P2, and for Tamil, Telugu, Kannada, Bengali, Gujarati, Gurmukhi, Malayalam and Odia in P4. Each script reaches at least 98% character accuracy on its printed-label benchmark.
14. **DL-14 (P3)** Every AI tool has a CPU path. On T1, INT8 models pass the accuracy gate of AI-DEEP-LEARNING.md section 4 and the AI tools meet their T1 timing targets.
15. **DL-15 (P3)** Heatmaps and outlier scores remain available when the GPU-optimised engine is used (computed on NG images or on demand if needed). The user never has to choose between speed and explanation at export.
16. **DL-16 (P3)** Detection supports a point-count mode for dense small items, counting at least 500 items per image with count error below 1% on the dense-count benchmark.
17. **DL-17 (P3)** A synthetic defect generator (scratch, dent, stain, missing print, foreign particle) creates validation images, and the validation report shows recall on synthetic and real defects separately.
18. **DL-18 (P3)** Training offers automatic parameter search within a user time budget (default 10 minutes) and reports the chosen settings in plain words.
19. **DL-19 (P4)** A tool can take 2–4 aligned images of one part (for example different lights) as one input.
20. **DL-20 (P4)** Text-prompted pre-labelling: the engineer types a class name and the trainer proposes boxes on all images; at least 70% of proposals are accepted unchanged on the detection benchmark.
21. **DL-21 (P4)** A new product variant model can be fine-tuned from an existing model with at least 50% fewer labelled images than training from scratch for the same validation result, with no regression on the old variant.
22. **DL-22 (P3)** Every shipped model, weight file and training library is on a licence register. No AGPL/GPL code or non-commercial weights are in the runtime or the trainer installer. Release fails if the register is incomplete.
23. **DL-23 (P3)** A freedom-to-operate opinion covering US 12,315,277, US 12,361,100, US 11,568,629, US 10,937,141 and their EP/CN/JP/KR family members is obtained before the AI Deep Learning licence is sold outside India.
24. **DL-24 (P3)** The AI drift monitor tracks the outlier score and the embedding distribution, and warns when the outlier rate doubles against the baseline over 1,000 parts.
25. **DL-25 (P4)** Optional plant-server training: datasets and models can be managed centrally for several lines, with signed model packages and the same gate and approval rules as local training.
26. **DL-26 (P3)** AI tools run with no dongle requirement on the line PC (software licence), no NVIDIA-only restriction for runtime, and support virtual-machine use for training where a GPU is passed through.

## 9. Sources

Pages opened during this study (fetched):

| URL | Note |
| --- | --- |
| https://docs.cognex.com/deep-learning_110/web/en/deep-learning/Content/deep-learning-Topics/overview/deep-learning-tools.htm | VPDL 1.1 tool list: Red/Green/Blue and modes |
| https://docs.cognex.com/DEEP-LEARNING_311/WEB/EN/DEEP-LEARNING/Content/deep-learning-Topics/what-is-vpdl/ws_stream_tool.htm | Workspace, stream, chaining; Focused vs High Detail vs Quick; supervised vs unsupervised trade-offs |
| https://docs.cognex.com/DEEP-LEARNING_311/WEB/EN/DEEP-LEARNING/Content/deep-learning-Topics/what-is-vpdl/what-is-vpdl.htm | VPDL as ViDi successor; High Detail from SuaKIT; runtime workspace and API |
| https://docs.cognex.com/deep-learning_400/web/EN/deep-learning/Content/Topics/overview/feature-sampling.htm | Feature size, sampling density, masking, sample limit |
| https://docs.cognex.com/vidi_410/web/EN/vidisuite/Content/ViDi-Topics/overview/tool-parameters-sampling.htm | ViDi 4.1 sampling: 5× context, centre detail, colour, border, masking modes, Centered |
| https://docs.cognex.com/vidi_410/web/EN/vidisuite/Content/ViDi-Topics/overview/red-analyze-tool-unsupervised.htm | Unsupervised labelling and validation workflow |
| https://docs.cognex.com/deep-learning_400/web/EN/deep-learning/Content/Topics/tools/red.htm | VPDL 4.0 Red Analyze: Standard (Fast/Accurate/Robust), Unsupervised, Legacy; strengths and weaknesses |
| https://docs.cognex.com/deep-learning_400/web/EN/deep-learning/Content/Topics/tools/green.htm | VPDL 4.0 Green Classify: Few Sample, outlier score, TensorRT trade-off |
| https://docs.cognex.com/deep-learning_332/web/EN/deep-learning/Content/deep-learning-Topics/tools/green.htm | Green Classify Focused vs High Detail feature table |
| https://docs.cognex.com/deep-learning_400/web/EN/deep-learning/Content/Topics/tools/blue-read.htm | Blue Read pretrained character set |
| https://docs.cognex.com/deep-learning_101/web/en/deep-learning/content/deep-learning-Topics/overview/blue-read-tool-parameters.htm | Blue Read parameters overview |
| https://docs.cognex.com/deep-learning_330/web/EN/deep-learning/Content/deep-learning-Topics/getting-started/licenses.htm | Base/Standard/Advanced licences, GPUs, dongle |
| https://docs.cognex.com/deep-learning_410/web/EN/deep-learning/Content/Topics/getting-started/gpu-recommendations.htm | GPU guidance 4.1, compute capability 6.1, driver note |
| https://docs.cognex.com/deep-learning_321/web/EN/deep-learning/Content/deep-learning-Topics/preparation/preparation.htm | PC, OS, RAM, GPU memory, no VM, High Detail needs GPU |
| https://www.vision-systems.com/boards-software/article/16750714/deep-learning-based-machine-vision-software-library-released-by-sualab | SuaKIT launch 2017 |
| https://www.vision-systems.com/boards-software/article/16748190/sualab-will-show-latest-iteration-of-machine-vision-software-at-automate-2019 | SuaKIT 2.0 and 2.1 features |
| https://invision-news.de/?p=21729 | SuaKIT 2.0: comparison, detection, labeller, debugger |
| https://www.vision-systems.com/factory/manufacturing/article/16736126/identifying-and-addressing-the-limitations-of-deep-learning-software | SuaLab on data scarcity, one-class learning, debugger, domain adaptation |
| https://imveurope.com/news/cognex-strengthens-deep-learning-ip-sualab-acquisition | SuaLab purchase, reported price, focus on electronics |
| https://mvpromedia.com/cognex-launches-onevision-a-cloud-platform-for-ai-vision-systems/ | OneVision 2025 |
| https://patents.google.com/patent/US12315277B2/en | Cognex candidate-box incremental training patent, family list |
| https://patents.google.com/patent/US12361100B1/en | Cognex runtime categorisation and model update patent, claims |
| https://patents.google.com/patent/US20200005069A1/en | Cognex pattern finder + classifier (US 11,568,629 B2) family |
| https://patents.google.com/patent/US10937141B2/en | SuaLab Siamese image comparison patent, now Cognex Ireland |
| https://patents.google.com/patent/US10068153B2/en | Cognex handheld trainable OCR from font files |
| https://patents.google.com/patent/US11017259B2/en | Utechzone synthetic defect pairs + edge image |
| https://github.com/amazon-science/patchcore-inspection | PatchCore reference code, Apache 2.0 |
| https://github.com/open-edge-platform/anomalib | anomalib, Apache 2.0, model list, OpenVINO export |

Seen in search results only (page not opened, or access refused); used for dates and context:

| URL | Note |
| --- | --- |
| https://businesswire.com/news/home/20170405005796/en/Cognex-Acquires-Maker-of-Deep-Learning-Software-for-Industrial-Machine-Vision | ViDi purchase release, 5 April 2017 |
| https://www.emva.org/news/cognex-acquires-vidi-systems/ | ViDi purchase, trade copy |
| https://www.nasdaq.com/press-release/cognex-acquires-sualab-to-advance-its-leadership-in-deep-learning-based-machine | SuaLab purchase release, 16 Oct 2019 |
| https://www.cognex.com/what-is/edge-learning/how-edge-learning-works | Edge Learning pre-training, 5–10 images (429 on fetch) |
| https://www.cognex.com/blogs/machine-vision/how-to-train-edge-learning | Edge Learning training rule of thumb (429 on fetch) |
| https://baa.vn/document/3800-series-cognex-thi-giac-may-2d-8001-catalog.pdf | In-Sight 3800 catalogue: EL Classify, Segment, Read |
| https://www.bertelkamp.com/media/documents/Datasheet%20IS%20D900.pdf | In-Sight D900: ViDi Read/Check/Detect, PC training |
| https://www.vision-systems.com/directory/software/deep-learning-software/press-release/14233485/cognex-cognex-visionpro-software-breaks-down-barriers-between-deep-learning-and-traditional-vision | VisionPro 10 + VPDL 2.0, 2021 |
| https://data.epo.org/publication-server/rest/v1.2/patents/EP4427204NWA1/document.html | EP family member of US 12,315,277 |
| https://data.epo.org/publication-server/rest/v1.2/patents/EP4145401NWA1/document.html | MVTec EP application, subject to be checked |
| https://roboflow.com/model-licenses/segment-anything-3 | SAM 3 custom licence summary |
| https://ai.meta.com/resources/models-and-libraries/dinov3-license | DINOv3 custom licence |
| https://roboflow.com/model-licenses/rf-detr | RF-DETR per-size licences |
| https://openaccess.thecvf.com/content/CVPR2022/papers/Roth_Towards_Total_Recall_in_Industrial_Anomaly_Detection_CVPR_2022_paper.pdf | PatchCore paper |
| https://arxiv.org/abs/1911.02357 | Student-teacher anomaly detection (MVTec) |
