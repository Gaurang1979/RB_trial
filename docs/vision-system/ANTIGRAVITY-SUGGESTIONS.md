# Antigravity Suggestions: Advanced Tools & Methods for Vision Software Development

One dedicated technical reference of new tools, architectures, simulation methods, and developer tooling proposed by **Antigravity** for the **Vision_System (Sundaram plant platform)**.

Owner rule (2026-11-02): Prepare a separate suggestions document so subsequent Claude and engineering sessions can pick up, implement, and benchmark each method one by one against the recorded image sets (`truth/batch-baseline.csv`, `tools/bench/ocr_bench`, and synthetic testbenches).

---

## 0. Context & Architectural Constraints

Every suggestion in this document is designed specifically around the operational realities and technical constraints established in this repository ([docs/REQUIREMENTS-VISION-SYSTEM.md](../REQUIREMENTS-VISION-SYSTEM.md), [ARCHITECTURE.md](ARCHITECTURE.md), and [AGENTS.md](../../AGENTS.md)):

1. **Host Hardware**: Intel Core i5/i7 industrial PCs with **no discrete GPU**, running Windows 10/11 IoT Enterprise LTSC. Inference must leverage Intel AVX2, AVX-512, VNNI, or AMX instructions.
2. **Resolution & Latency**: 20 MP top-camera case aggregation (45–96 codes per case) must process within **$\le 200\text{ ms}$**; single-line OCR/inspection within **$\le 20\text{ ms}$**.
3. **Sensor**: Hikrobot MV-CS200-10UM (Sony IMX183 monochrome rolling shutter, USB3 Vision).
4. **Line Safety Gate**: **0 false accepts / 0 missed defects**. In doubt, the result is NG and the line is alerted immediately.
5. **Air-Gapped Operation**: Plant PCs operate without internet access; all models, engines, and tools run entirely locally.

---

## 1. Summary of Proposals & Roadmap Fit

| # | Proposal | Primary Tool Target | Gain | Effort | Acceptance Test | Safety Note |
| :-: | :--- | :--- | :---: | :---: | :--- | :--- |
| **AG-01** | **Reparameterized Backbones (RepViT / FastViT)** | Tool 10 (AI Deep Learning), Tool 5 | **High** | M | `bench` latency $\le 3\text{ ms}$/crop; accuracy $\ge$ baseline | Fails safe on low score $\to$ NG |
| **AG-02** | **In-Memory HNSW / PQ Indexing for PatchCore** | Tool 10 (Red Anomaly) | **High** | S–M | AUROC $\ge 98.5\%$; CPU search $\le 12\text{ ms}$/tile | Distance $>$ threshold $\to$ NG |
| **AG-03** | **OpenVINO NNCF INT8 Quantization Pipeline** | Tool 5 (Locator/Enhancer), Tool 7, 10 | **Med–High** | S | 0 lost codes vs `truth/batch-baseline.csv` | Lossy precision gated $\to$ FP16 |
| **AG-04** | **Procedural PBR Digital Twin (`ml/synth_3d.py`)** | Tool 0, Tool 7 (OCR), Tool 8 (Defects) | **High** | M | Unblocks items 1, 4, 5, 6, 9 without line wait | Validation only; never relaxes limits |
| **AG-05** | **Latent Defect Inpainting (AnomalyDiffusion)** | Tool 8 (Inspect), Tool 10 (Red/Blue) | **Med** | M | Defect recall on synthetic and real testbenches | Stress-testing only |
| **AG-06** | **Conveyor-Compensated Rolling-Shutter Rectification** | Tool 0 (Acquisition), Tool 5 | **Med** | S | Synthetic line-speed sweep; 0 aspect distortion | Hardware encoder mismatch $\to$ NG |
| **AG-07** | **Specular Highlight Gradient Inpainting** | Tool 0 (Image Prep), Tool 5 | **Med** | S | Shrink-wrap batch read rate $+2\text{ to }4\%$ | Decoder RS gate preserves safety |
| **AG-08** | **Virtual GenTL Producer & Camera Test Harness** | CI/CD, `dotnet/HikCamera`, `LineRunner` | **High** | M | Headless GitHub Actions end-to-end line run | Test/developer harness |
| **AG-09** | **Native Immediate Debug Visualizer (Rerun.io / ImGui)** | Engine & algorithm development | **Med** | S | C++ debug stream with zero Avalonia overhead | Dev-only; disabled in release |
| **AG-10** | **Metamorphic Testing Framework for CV Pipelines** | `engine/tests/` regression harness | **High** | S–M | Invariance under $\pm 2^\circ$ rot, $\pm 10\%$ brightness | Catches brittle heuristic breaks |
| **AG-11** | **Deterministic Flight Recorder & Step Replayer** | `dotnet/StationGuard`, `Runtime.Host` | **Med** | M | Replay recorded raw NG frames with bit-exactness | Standalone forensics tool |
| **AG-12** | **Mahalanobis / Energy-based OOD Drift Detector** | Tool 10, Station Diagnostics | **Med** | S | Flags 95% of lighting/dust drifts over 500 parts | Warns operator; does not alter OK/NG |

---

## 2. Detailed Tool & Method Specifications

### AG-01: Structurally Reparameterized Backbones (RepViT / FastViT)
* **Problem**: Multi-branch networks (e.g., standard ResNet, MobileNetV3 with squeeze-and-excitation) have memory-access overhead and branch synchronization stalls on CPU execution, limiting feature extraction throughput across 20 MP tiled crops.
* **Method**:
  * Adopt structural reparameterization architectures such as **RepViT** or **FastViT**.
  * During training, blocks consist of multi-branch linear convolutions ($3\times 3$, $1\times 1$, identity).
  * During ONNX export, apply mathematical reparameterization: collapse convolution weights and batch-normalization parameters into an equivalent single-branch $3\times 3$ standard convolution kernel.
* **Implementation Plan**:
  1. Add `ml/models/repvit.py` (Apache-2.0 clean implementation).
  2. Implement `export_reparameterized(model, out_path)` which folds branches prior to ONNX serialization.
  3. Validate layer execution in OpenCV DNN / OpenVINO on Intel Core i5/i7.
* **Acceptance Test**:
  * CPU feature extraction under $3\text{ ms}$ for a $224\times 224$ crop on single-core Intel 12th/13th Gen i5.
  * Zero drop in classification or locator accuracy compared to the uncompressed multi-branch training model.

---

### AG-02: In-Memory HNSW / Product Quantization Indexing for PatchCore
* **Problem**: PatchCore anomaly detection (Tool 10 Red) stores thousands of patch feature vectors from good products. At 20 MP resolution with tiled patches, exhaustive Euclidean distance search against the coreset memory bank requires 80–150 ms on CPU, exceeding the per-camera cycle budget.
* **Method**:
  * Replace naive brute-force kNN with an in-memory **Hierarchical Navigable Small World (HNSW)** graph index or **Product Quantization (PQ)** vector index (via embedded [USearch](https://github.com/unum-cloud/usearch) or header-only [hnswlib](https://github.com/nmslib/hnswlib), both MIT/Apache-2.0).
  * Feature vectors from frozen WideResNet-50 or RepViT are indexed at teach time into the HNSW structure.
  * Runtime queries find the $k=1$ or $k=3$ nearest neighbor in $\mathcal{O}(\log N)$ time with SIMD AVX2 acceleration.
* **Implementation Plan**:
  1. Integrate `usearch` or `hnswlib` into `engine/src/anomaly/` as a C++ dependency.
  2. Implement `build_memory_bank(const std::vector<float>& embeddings)` during Teach / Auto-learn.
  3. Query patch distances during `vs_anomaly_inspect()`.
* **Acceptance Test**:
  * Anomaly distance query across a 5,000-vector memory bank drops from $>80\text{ ms}$ to $\le 12\text{ ms}$ per tile on CPU.
  * AUROC on injected scratch and dent defects remains $\ge 98.5\%$.

---

### AG-03: OpenVINO NNCF INT8 Quantization with Automated Gate
* **Problem**: FP32/FP16 models consume significant memory bandwidth and compute cycles on industrial PCs lacking GPUs.
* **Method**:
  * Integrate Intel’s [Neural Network Compression Framework (NNCF)](https://github.com/openvinotoolkit/nncf) into the offline training pipeline (`ml/retrain.py` and `ml/ocr/train_ocr.py`).
  * Run Post-Training Quantization (PTQ) with MinMax or Percentile calibration using 100 recorded golden validation crops.
  * Target Intel VNNI / AVX-512 INT8 dot-product instructions.
* **Safety & Accuracy Gate**:
  * The quantized INT8 model is run through `tools/bench/batch_check.py` against `truth/batch-baseline.csv` and `tools/bench/ocr_bench.py`.
  * **Gate Rule**: If the INT8 model causes even **1 lost code** or **1 character misread**, it is automatically rejected, and the engine retains FP16/FP32.
* **Acceptance Test**:
  * Inference latency drops by $1.8\times$ to $2.4\times$ on Intel CPU.
  * Verified 0 regressions on all golden batches.

---

### AG-04: Procedural PBR Digital Twin (`ml/synth_3d.py`)
* **Problem**: Progress on high-priority backlog items (e.g., Tool 0 lighting recipes, Tool 7 CIJ/TTO OCR, Tool 8 cosmetic defects) is blocked waiting for physical line time or customer images (`docs/vision-system/TOOL-METHODS-BACKLOG.md` §15).
* **Method**:
  * Create a standalone procedural 3D synthesis pipeline using Blender's headless Python engine (`bpy`) or Mitsuba 3.
  * Programmatically generate photorealistic package scenes:
    * **Geometry**: Bottles with neck rings, bottle caps (curved, knurled, indented), cartons with folds, corrugated boxes.
    * **Materials**: PBR materials for glossy cartons, brushed aluminum, transparent PET, and shrink-wrap film with procedural wrinkles and Fresnel highlights.
    * **Lighting Rigs**: Virtual dark-field low-angle rings, cross-polarized diffusers, high-angle coaxial strobes.
    * **Markings**: Synthetic Data Matrix, QR codes, CIJ dot-matrix strings, and laser-etched batch lines rendered directly onto the 3D meshes before optical simulation.
* **Output**:
  * Generates 10,000+ photorealistic 20 MP scenes with pixel-perfect ground truth (segmentation masks, 3D poses, exact character strings, and bounding boxes).
* **Acceptance Test**:
  * Models trained on synthetic PBR data achieve $\ge 95\%$ zero-shot accuracy when first tested on real field images.

---

### AG-05: Controlled Latent Defect Inpainting (AnomalyDiffusion)
* **Problem**: Rare critical defects (e.g., seal pinholes, cap cracks, missing tamper-evident bands, foreign particles) almost never appear during normal operation, making it hard to validate defect detection before line deployment.
* **Method**:
  * Implement an offline script (`ml/synth_defects.py`) using a controlled latent diffusion model (conditioned on masks and defect prompts).
  * Given an image of a real good part from the plant, inpaint realistic industrial defects into designated regions (scratches on caps, ink spatter on labels, seal deformities).
* **Safety Protocol**:
  * Synthetic defect images are used **strictly for testing, validation, and offline calibration**; they are never used to loosen rejection thresholds on the production line.
* **Acceptance Test**:
  * Successfully detects $\ge 95\%$ of synthetic test anomalies at false-reject rate $\le 0.05\%$.

---

### AG-06: Conveyor-Compensated Rolling-Shutter Rectification
* **Problem**: The Sony IMX183 sensor in the Hikrobot MV-CS200-10UM camera employs an electronic rolling shutter. If cases move on the conveyor during exposure, non-uniform vertical-to-horizontal shear occurs, degrading 2D barcode decode rates.
* **Method**:
  * In `engine/src/linear.cpp` / `engine/src/calibration.cpp`, add a rolling-shutter rectification pass prior to region localization:
    $$\Delta x(y) = v_{\text{conveyor}} \cdot t_{\text{row}} \cdot y$$
    where $t_{\text{row}}$ is the sensor line readout period (retrieved via Hikrobot GenICam chunk data) and $v_{\text{conveyor}}$ is the line speed transmitted by the PLC encoder over Ethernet.
  * Apply a fast 1D row-shift lookup table (LUT) over the ROI.
* **Acceptance Test**:
  * Decodes 100% of QR / Data Matrix codes under simulated conveyor speeds up to $1.2\text{ m/s}$ where uncompensated images fail due to skewed finder patterns.

---

### AG-07: Specular Highlight Gradient Inpainting
* **Problem**: Shrink-wrap reflections and glossy carton tape produce blown-out white patches ($\text{pixel} \ge 252$) that obliterate Data Matrix clock tracks and QR finder patterns.
* **Method**:
  * Compute a binary glare mask ($I(x, y) \ge 252$).
  * For masked regions inside candidate barcode bounding boxes, apply an anisotropic gradient-preserving diffusion inpainting filter (Telea or Navier-Stokes formulation, optimized via AVX2).
  * Recover the edge continuity across the saturated glare patch prior to grid fitting.
* **Acceptance Test**:
  * Yields $+2\text{ to }4\%$ read-rate gain on the shrink-wrapped excise and bottle-cap benchmark sets with zero increase in false reads.

---

### AG-08: Virtual GenTL Producer & Camera Test Harness
* **Problem**: Testing multi-camera coordination, trigger timing, and packet handling currently requires physical hardware or a Windows PC with connected Hikrobot cameras. CI/CD runs on macOS or headless Linux runners cannot exercise the real camera acquisition pipeline.
* **Method**:
  * Build a lightweight virtual GenTL Producer (`vs_gentl_virtual.cti`) or loopback DLL matching the Hikrobot MVS C API (`dotnet/HikCamera/`).
  * Reads folders of test images (e.g., 20 MP raw/mono TIFF/BMP) and simulates camera acquisition:
    * Accurate frame-rate timing and timestamp generation.
    * Configurable simulated transport errors (dropped packets, incomplete frames).
    * Hirose Line0 hardware trigger emulation triggered via software sockets.
* **Acceptance Test**:
  * Full integration tests for `LineRunner`, `StationGuard`, and `Runtime.Host` run completely headless inside GitHub Actions on macOS, Windows, and Linux.

---

### AG-09: Native Immediate Debug Visualizer (Rerun.io / ImGui)
* **Problem**: Debugging complex spatial algorithms (e.g., FFT module-centre maps in PowerGrid, CTC alignment paths, Hough accumulator peaks) requires dumping intermediate BMP files to disk or building custom UI screens in Avalonia, which slows down algorithm development.
* **Method**:
  * Integrate [Rerun.io](https://rerun.io) (C++ / Python SDK) or an immediate-mode ImGui debug overlay into `engine/tools/bench/`.
  * Stream 2D spatial overlays, 3D FFT surfaces, and multi-stage filter tensors directly from native C++ memory with zero disk I/O and zero UI latency.
* **Safety Rule**:
  * Strip visualizer symbols and endpoints in production release builds (`#ifdef VS_DEBUG_VISUALIZER`).
* **Acceptance Test**:
  * Developers can step through PowerGrid 8-way seed expansion and CTC forced alignments frame-by-frame with zero performance overhead.

---

### AG-10: Metamorphic Testing Framework for Vision Algorithms
* **Problem**: Classical unit tests test fixed inputs against fixed outputs. Industrial vision algorithms often break subtly under minor physical variations (e.g., lighting jitter, slight box rotation, sub-pixel vibration).
* **Method**:
  * Add a metamorphic testing suite in `engine/tests/metamorphic_tests.cpp`.
  * Assert metamorphic relations:
    1. *Rotational Invariance*: Rotating an image by $\pm 1^\circ$ must not drop barcode read count.
    2. *Photometric Stability*: Adjusting gamma by $\pm 8\%$ must produce identical OCR text output.
    3. *Scale Stability*: Scaling an image by $\pm 3\%$ with bicubic interpolation must preserve line bounding boxes within $1\text{ px}$.
    4. *Monotonicity*: Adding a blank quiet zone around a verified code must never cause a decode failure.
* **Acceptance Test**:
  * Runs automatically in `ctest` during CI to catch fragile heuristics before they reach the main branch.

---

### AG-11: Deterministic Flight Recorder & Step Replayer
* **Problem**: When a false reject occurs on a high-speed production line, diagnosing the root cause after the batch is finished is difficult if only downscaled preview JPEGs are kept.
* **Method**:
  * Implement a rolling in-memory ring buffer (using memory-mapped files or Apache Arrow Feather format) in `dotnet/StationGuard`.
  * Upon an NG verdict, the full uncompressed 20 MP raw frame, camera chunk metadata, PLC register states, and intermediate tool outputs are flushed to an encrypted `.vsrec` file.
  * Provide a replay CLI (`tools/replayer`) that loads the `.vsrec` file and feeds the exact buffer into `vs_runtime`, guaranteeing bit-identical offline reproduction.
* **Acceptance Test**:
  * Verifies bit-exact reproduction of the inspection verdict and execution timing down to the millisecond.

---

### AG-12: Latent-Space Out-Of-Distribution (OOD) & Early Drift Detection
* **Problem**: Ambient lighting drift, dust accumulation on camera lenses, and LED strobe fading occur gradually over weeks. Today, these are noticed only after parts begin failing inspections.
* **Method**:
  * Extract intermediate feature representations from the locator or OCR backbone.
  * Fit a lightweight Gaussian Mixture Model (GMM) or calculate the Mahalanobis distance relative to the golden batch feature cluster.
  * When the distance metric shifts consistently over a moving window of 500 parts, raise an early-warning diagnostic flag on the Plant Dashboard: *"Optical drift detected: check lens cleanliness or LED illumination before reject limits are breached."*
* **Safety Rule**:
  * The drift detector issues warnings only; it **never silently alters an inspection threshold or overrides an OK/NG verdict**.
* **Acceptance Test**:
  * Flags $>95\%$ of simulated optical degradation events (simulated dust, $-20\%$ light intensity) at least 500 parts before any false reject is triggered.

---

## 3. Guide for Claude & Engineering Sessions

When picking up an item from this document:

1. **Check Coordination**: Run `scripts/coord.sh taken "<item name>"` to verify no other session has claimed the work.
2. **Claim & Branch**: Follow the standard process in [AGENTS.md](../../AGENTS.md):
   ```bash
   git fetch origin
   git worktree add ../vs-work -b claude/<topic> origin/main
   scripts/coord.sh note
   ```
3. **Benchmark First**: Before making algorithmic changes, record baseline metrics using `tools/bench/batch_check.py` or `tools/bench/ocr_bench.py`.
4. **Enforce Line Safety**: Ensure that all OK/NG decisions adhere to Section 14 of [TOOL-METHODS-BACKLOG.md](TOOL-METHODS-BACKLOG.md): in doubt, fail safe to NG and report the clear diagnostic reason.
5. **Verify & Roll Up**: Mark ready only when all acceptance tests pass with **0 false reads and 0 regressions**.
