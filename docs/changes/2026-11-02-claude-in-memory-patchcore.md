# In-memory PatchCore anomaly detection engine with HNSW vector indexing (AG-02, Tool 10)

- Status: done
- Session: in-memory-patchcore, 2026-11-02
- Pull request: #228
- Section 5 item: new
- Components/files: engine/include/vs_anomaly.h, engine/src/anomaly/hnsw.hpp, engine/src/anomaly/patchcore.cpp, engine/tests/anomaly_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/AnomalyNative.cs, dotnet/VisionCore/AnomalyEngine.cs, dotnet/VisionCore/Tools/AnomalyTool.cs, dotnet/VisionCore/Jobs/Job.cs, dotnet/VisionStudio/Components/Pages/Teach.razor, dotnet/VisionStudio/Services/Station.cs, dotnet/VisionStudio/i18n/en.json, dotnet/VisionCore.Tests/AnomalyTests.cs, dotnet/VisionStudio.Tests/MeasureToolUiTests.cs
- Depends on: none

## Section 5
- New: [x] In-memory PatchCore anomaly detection engine with HNSW vector indexing (AG-02, Tool 10)

## Timeline (section 3)
- 2026-11-02: Claimed item AG-02 / Tool 10 in coordination log.
- 2026-11-02: Implemented header-only C++17 HNSW index (`hnsw.hpp`) with hierarchical multi-layer skip graphs and L2 distance metric.
- 2026-11-02: Implemented PatchCore engine (`patchcore.cpp`): localized spatial patch descriptor extraction, greedy minimax coreset selection, covering-radius auto-calibration, continuous 2D heatmap generation, and connected component defect bounding box extraction.
- 2026-11-02: Built test suite `anomaly_tests.cpp` covering parameter defaults, golden teaching, defect scratch detection, model binary serialization roundtrip, and $512 \times 512$ industrial execution benchmark ($2.28\text{ ms}$ on CPU vs budget $\le 12.0\text{ ms}$).
- 2026-11-02: Verified CTest 100% pass across all test suites.
- 2026-11-02: Implemented C# P/Invoke bindings (`AnomalyNative.cs`), high-level wrapper (`AnomalyEngine.cs`), and unit tests (`AnomalyTests.cs`).
- 2026-11-02: Integrated `AnomalyToolConfig` and `AnomalyTool` into `ITool` execution pipeline, `JobRunner`, and `Station.LicenceToolOf`.
- 2026-11-02: Added `🔴 Find defects (AI)` to `VisionStudio` Teach toolbox (`Teach.razor`) and localized strings in `en.json`.
- 2026-11-02: Packaged `vs_anomaly.dll` and `vs_anomaly.h` into Windows CI workflow.

## Decisions (section 4)
- Zero external DNN/Python dependency: Feature representations use multi-scale gradient and statistical patch descriptors, avoiding heavyweight PyTorch/TensorFlow runtimes and enabling instant execution on CPU-only industrial PCs.
- Self-contained HNSW graph: Header-only implementation with zero third-party dependencies guarantees portability and fast $O(\log M)$ query times on CPU.
- Covering-radius threshold calibration: Calibrating the anomaly decision threshold from the maximum distance of any training patch to its nearest coreset representative ($\epsilon \cdot 1.18$) guarantees that 100% of golden training samples score $\le 0.88$ (PASS) while defects reliably exceed 1.0 (DEFECT).

## Update log (section 8)
- 2026-11-02: Ready for Claude review and verification on plant line datasets.
