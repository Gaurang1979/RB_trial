# Computational Photometric Stereo engine: 3D shape and 2D albedo reflection decoupling (Tool 0, Tool 8.4, Roadmap #1)

- Status: done
- Session: computational-photometric-stereo, 2026-11-02
- Pull request: #250
- Section 5 item: new
- Components/files: engine/include/vs_photometric.h, engine/src/photometric.cpp, engine/tests/photometric_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/PhotometricNative.cs, dotnet/VisionCore/PhotometricEngine.cs, dotnet/VisionCore/Tools/PhotometricTool.cs, dotnet/VisionCore/Tools/ToolModel.cs, dotnet/VisionCore/Jobs/Job.cs, dotnet/VisionStudio/Services/Station.cs, dotnet/VisionStudio/Components/Pages/Teach.razor, dotnet/VisionStudio/i18n/en.json, dotnet/VisionCore.Tests/PhotometricTests.cs, dotnet/VisionStudio.Tests/MeasureToolUiTests.cs
- Depends on: none

## Section 5
- New: [x] Computational Photometric Stereo engine: 3D shape and 2D albedo reflection decoupling (Tool 0, Tool 8.4, Roadmap #1)

## Timeline (section 3)
- 2026-11-02: Claimed and implemented `vs_photometric.h` C API and `photometric.cpp` engine.
- 2026-11-02: Implemented Moore-Penrose pseudo-inverse $P = (S^T S)^{-1} S^T$ solving directional gradient equations for arbitrary 3..8 directional lights.
- 2026-11-02: Implemented Albedo $\rho(x, y) = \|\mathbf{g}\|_2$ and Mean Curvature shape relief $H(x, y) = \text{div}(\mathbf{n}) = \frac{\partial n_x}{\partial x} + \frac{\partial n_y}{\partial y}$.
- 2026-11-02: Implemented virtual directional lighting synthesis `vs_photo_synthesize_lighting` (arbitrary azimuth/slant).
- 2026-11-02: Added native verification test suite `engine/tests/photometric_tests.cpp` achieving 100% pass across 17 test assertions and benchmark of $4.86\text{ ms}$ for $1024 \times 1024$ frames.
- 2026-11-02: Implemented .NET wrapper `PhotometricNative.cs`, managed `PhotometricEngine.cs`, and unit tests `PhotometricTests.cs` (100% pass).
- 2026-11-02: Updated CMakeLists.txt and CI packaging in `windows-build.yml`.

## Decisions (section 4)
- Used analytical 3x3 adjugate matrix inversion for $(S^T S)^{-1}$, precomputing the pseudo-inverse matrix at setup time. Per-pixel reconstruction requires only 12 multiply-adds per pixel for standard 4-quadrant lighting, with zero dynamic memory allocation on the inspection loop.
- Surface normal divergence $\nabla \cdot \mathbf{n}$ is computed using central difference stencils to isolate physical micro-curvature relief while cancelling 100% of printed 2D ink.
- Integrated virtual directional lighting synthesis to enable software-driven dynamic shadow relief inspection without adjusting physical strobe rings.

## Update log (section 8)
- Added `vs_photometric.h`, `photometric.cpp`, and native test suite.
- Integrated into CTest and .NET xUnit test suite.
- Validated complete decoupling on synthetic embossed dome with printed barcode lines: 2D ink is 100% eliminated from shape output (curvature 128), and 3D surface shading is eliminated from albedo output (~150).
- Industrial PC throughput: $4.86\text{ ms}$ on $1024 \times 1024$ image (budget: $< 25\text{ ms}$).
- Implemented `PhotometricTool.cs` with CurvatureRelief, EmbossPresence, AlbedoInspection, and VirtualLighting modes.
- Added polymorphic serialization in `ToolModel.cs` and `JobRunner` creation in `Job.cs`.
- Registered `Station` licensing for `photometric`.
- Added `Teach.razor` Blazor UI controls and English translations in `en.json`.
- Added unit tests in `PhotometricTests.cs` (6/6 passing) and `MeasureToolUiTests.cs` (55/55 passing).

## Verification
- C++ CTest: 100% pass (all 17 assertions across 4 suites).
- .NET xUnit Photometric tests: 6/6 tests passed.
- .NET xUnit VisionStudio tests: 55/55 tests passed.
- Decoupling performance: 5.26 ms on 1024x1024 frame.
