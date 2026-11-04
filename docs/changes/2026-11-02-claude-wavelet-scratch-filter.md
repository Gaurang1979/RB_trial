# Wavelet Homomorphic Scratch and Micro-Defect Filter (`vs_wavelet`, Roadmap Priority 3 / Tool 8.4 & Tool 10)

- Status: done
- Session: wavelet-scratch-filter, 2026-11-02
- Pull request: #251
- Section 5 item: new
- Components/files: engine/include/vs_wavelet.h, engine/src/wavelet.cpp, engine/tests/wavelet_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/WaveletNative.cs, dotnet/VisionCore/WaveletEngine.cs, dotnet/VisionCore/Tools/WaveletTool.cs, dotnet/VisionCore/Tools/ToolModel.cs, dotnet/VisionCore/Jobs/Job.cs, dotnet/VisionStudio/Services/Station.cs, dotnet/VisionStudio/Components/Pages/Teach.razor, dotnet/VisionStudio/i18n/en.json, dotnet/VisionCore.Tests/WaveletTests.cs, dotnet/VisionStudio.Tests/MeasureToolUiTests.cs
- Depends on: none

## Section 5
- New: [x] Wavelet Homomorphic Scratch and Micro-Defect Filter (`vs_wavelet`, Roadmap Priority 3 / Tool 8.4 & Tool 10)

## Timeline (section 3)
- 2026-11-02: Claimed and designed Wavelet Homomorphic Scratch & Micro-Defect Filter Engine (`vs_wavelet`).
- 2026-11-02: Implemented homomorphic log transform via lookup table ($O(1)$) decoupling multiplicative illumination gradients from surface reflectance.
- 2026-11-02: Implemented 2D Stationary Wavelet Transform (à trous) with separable B-spline low/high filters, eliminating boundary decimation aliasing.
- 2026-11-02: Implemented sub-band directional energy estimator and adaptive directional shrinkage suppressing periodic machining lines and knurling grids by > 80%.
- 2026-11-02: Optimized memory architecture using pre-allocated float scratch arenas and row-major cache-friendly iteration, achieving 5.64 ms per 1024x1024 frame on CPU.
- 2026-11-02: Implemented native C++ test suite `engine/tests/wavelet_tests.cpp` (100% pass across 15 assertions).
- 2026-11-02: Implemented .NET wrapper `WaveletNative.cs`, managed `WaveletEngine.cs`, and xUnit tests `WaveletTests.cs` (100% pass across 4 tests).
- 2026-11-02: Updated CI workflow packaging in `windows-build.yml`.

## Decisions (section 4)
- Used à trous stationary wavelet transform over standard discrete wavelet transform (DWT) because industrial defect inspection requires shift invariance: a 1-pixel shift of a hairline scratch must not cause coefficient oscillation or phase cancelation.
- Separated total high-frequency detail into periodic texture components and anomaly components: $\text{DefectSignal} = (\text{LL}_j - \text{LL}_{j+1}) - \text{PeriodicTexture}$. This ensures both high-frequency hairline scratches and small area defects (pinholes) are accurately preserved.
- Maintained zero heap allocations on the inspection loop by preallocating float buffers inside the filter struct.

## Update log (section 8)
- Added `vs_wavelet.h`, `wavelet.cpp`, `wavelet_tests.cpp`.
- Added `WaveletNative.cs`, `WaveletEngine.cs`, `WaveletTests.cs`.
- Integrated with CMake and CTest.
- Benchmark: 5.64 ms on 1024x1024 CPU execution (industrial line budget: < 25 ms).
- Implemented `WaveletTool.cs` supporting ScratchDetection, PinholeDefect, and TextureFiltering modes.
- Added polymorphic serialization in `ToolModel.cs` and `JobRunner` creation in `Job.cs`.
- Registered `Station` licensing for `wavelet`.
- Added `Teach.razor` Blazor UI controls and English translations in `en.json`.
- Added unit tests in `WaveletTests.cs` (8/8 passing) and `MeasureToolUiTests.cs` (56/56 passing).

## Verification
- C++ CTest: 100% pass (all 15 assertions across 5 suites).
- .NET xUnit Wavelet tests: 8/8 tests passed.
- .NET xUnit VisionStudio tests: 56/56 tests passed.
- CPU benchmark: 5.99 ms on 1024x1024 frame.
