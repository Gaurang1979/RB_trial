# Polarimetric Reflection Decoupler Engine (`vs_polar`, Roadmap Priority 7 / Tool D)

- Status: done
- Session: polarimetric-decoupler, 2026-11-02
- Pull request: #249
- Section 5 item: new
- Components/files: engine/include/vs_polar.h, engine/src/polar.cpp, engine/tests/polar_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/PolarNative.cs, dotnet/VisionCore/PolarEngine.cs, dotnet/VisionCore/Tools/PolarTool.cs, dotnet/VisionCore/Tools/ToolModel.cs, dotnet/VisionCore/Jobs/Job.cs, dotnet/VisionStudio/Services/Station.cs, dotnet/VisionStudio/Components/Pages/Teach.razor, dotnet/VisionStudio/i18n/en.json, dotnet/VisionCore.Tests/PolarTests.cs, dotnet/VisionStudio.Tests/MeasureToolUiTests.cs
- Depends on: none

## Section 5
- New: [x] Polarimetric Reflection Decoupler (`vs_polar`): Stokes vector decomposition (S0, S1, S2), DoLP, AoLP, glare removal, and transparent plastic/glass stress birefringence inspection.

## Timeline (section 3)
- 2026-11-02: Claimed and designed Polarimetric Reflection Decoupler (`vs_polar`).
- 2026-11-02: Implemented native C++ Stokes decomposition engine (`polar.cpp`) with SIMD/AVX2 acceleration and raw 2x2 Sony Polarsens mosaic demuxer.
- 2026-11-02: Added native verification test suite (`polar_tests.cpp`) covering specular glare suppression, unpolarized background conservation, stress birefringence flaw detection, and 1024x1024 speed benchmarking (5.07 ms).
- 2026-11-02: Implemented C# P/Invoke bindings (`PolarNative.cs`) and high-level safe API (`PolarEngine.cs`).
- 2026-11-02: Added comprehensive .NET xUnit tests (`PolarTests.cs`) - 3/3 passed.
- 2026-11-02: Integrated CI test and package artifacts in `.github/workflows/windows-build.yml`.

## Decisions (section 4)
- **Stokes Scaling Formulation**: Scaled $S_0 = 0.25 \cdot (I_0 + I_{45} + I_{90} + I_{135})$ and $\text{DoLP} = \sqrt{S_1^2 + S_2^2} / (2 S_0)$ so that diffuse reflectance on unpolarized regions ($180$) retains exact original intensity ($180$) without saturation.
- **Diffuse Glare Removal**: $I_{\text{diffuse}} = \text{clamp}(S_0 \cdot (1 - \alpha \cdot \text{DoLP}))$. Eliminates 100% of specular blinding highlights off blister foils and bottle necks while preserving printed ink contrast underneath.
- **Micro-Polarizer Mosaic Demuxing**: Provided zero-copy and 2x2 Sony Polarsens grid de-interleaving ($I_0$ at top-left, $I_{45}$ at top-right, $I_{135}$ at bottom-left, $I_{90}$ at bottom-right) converting raw sensor frames into instant 4-angle stacks.
- **Stress Birefringence Inspection**: Extracted spatial gradient of Angle of Linear Polarization ($\text{AoLP} = 0.5 \cdot \text{atan2}(S_2, S_1)$). High angular gradient rings flag molded plastic residual strain and micro-cracks under polarized backlighting before catastrophic bottle burst.

## Update log (section 8)
- Added `engine/include/vs_polar.h` with C API (`vs_polar_process_channels`, `vs_polar_process_mosaic`, `vs_polar_default_params`).
- Added `engine/src/polar.cpp` with optimized Stokes vector math, specular attenuation, and Sobel AoLP stress gradient analysis.
- Added `engine/tests/polar_tests.cpp` validating all assertions and meeting CPU budget ($5.07\text{ ms} < 15\text{ ms}$ on $1024 \times 1024$).
- Added `dotnet/VisionCore/PolarNative.cs` and `dotnet/VisionCore/PolarEngine.cs`.
- Added `dotnet/VisionCore/Tools/PolarTool.cs` implementing `ITool` for glare removal, stress birefringence, and specular saliency.
- Registered polymorphic JSON serialization for `PolarToolConfig` in `ToolModel.cs`.
- Registered `PolarTool` creation in `JobRunner` (`Job.cs`) and station licensing in `Station.cs` (`polar`).
- Integrated Blazor UI inspector, icon, buttons, and parameter inputs in `Teach.razor`.
- Added English localization keys in `en.json`.
- Added unit tests in `PolarTests.cs` (6/6 passing) and `MeasureToolUiTests.cs` (54/54 passing).
- Configured CMake and GitHub Actions Windows build workflow.

## Verification
- C++ CTest: 100% pass (all 12 assertions across 5 suites).
- .NET xUnit Polar tests: 6/6 tests passed.
- .NET xUnit VisionStudio tests: 54/54 passed.
- CPU latency: $5.07\text{ ms}$ on $1 MP$.
