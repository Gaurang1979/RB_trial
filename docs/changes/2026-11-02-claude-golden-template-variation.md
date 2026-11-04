# Golden template and variation model inspection engine (Tool 8.2, AS-05, Item 16)

- Status: done
- Session: golden-template-variation, 2026-11-02
- Pull request: #229
- Section 5 item: new
- Components/files: engine/include/vs_inspect.h, engine/src/inspect.cpp, engine/tests/inspect_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/InspectNative.cs, dotnet/VisionCore/InspectEngine.cs, dotnet/VisionCore/Tools/InspectTool.cs, dotnet/VisionCore/Jobs/Job.cs, dotnet/VisionStudio/Components/Pages/Teach.razor, dotnet/VisionStudio/Services/Station.cs, dotnet/VisionStudio/i18n/en.json, dotnet/VisionCore.Tests/InspectTests.cs, dotnet/VisionStudio.Tests/MeasureToolUiTests.cs
- Depends on: none

## Section 5
- New: [x] Golden template and variation model inspection engine (Tool 8.2, AS-05, Item 16)

## Timeline (section 3)
- 2026-11-02: Claimed Tool 8.2 (Item 16) in coordination log.
- 2026-11-02: Implemented native C API `vs_inspect.h` and engine `src/inspect.cpp`:
  - Per-pixel mean $\mu(x, y)$ and variance $\sigma^2(x, y)$ learning across golden training reference images.
  - Edge-gradient dilation envelope widening ($U = \mu + k\sigma + C_{\text{offset}} + \alpha G_{\text{dilated}}$, $L = \mu - k\sigma - C_{\text{offset}} - \alpha G_{\text{dilated}}$) to prevent false edge rejections under mechanical vibration.
  - Strobe lighting drift normalization ($I_{\text{adj}} = I - \Delta\mu$).
  - 8-connected component defect blob extraction filtering by minimum defect area (`min_defect_area_px`), classifying polarity (bright scratch vs dark void/stain vs mixed), and computing bounding boxes.
  - Binary model persistence (`vs_inspect_save` / `vs_inspect_load`).
- 2026-11-02: Built test suite `inspect_tests.cpp` covering parameter defaults, golden teaching, defect detection, edge tolerance jitter immunity, model serialization, and $1024 \times 1024$ (1 MP) performance benchmark ($1.99\text{ ms}$ on CPU against budget $\le 15.0\text{ ms}$).
- 2026-11-02: Verified CTest 100% pass across all test suites.
- 2026-11-02: Implemented C# P/Invoke bindings (`InspectNative.cs`), high-level wrapper (`InspectEngine.cs`), and unit tests (`InspectTests.cs`).
- 2026-11-02: Integrated `InspectToolConfig` and `InspectTool` into `ITool` execution pipeline, `JobRunner`, and `Station.LicenceToolOf`.
- 2026-11-02: Added `🔍 Check defects` to `VisionStudio` Teach toolbox (`Teach.razor`) and localized strings in `en.json`.
- 2026-11-02: Packaged `vs_inspect.dll` and `vs_inspect.h` in Windows CI workflow.

## Decisions (section 4)
- Anisotropic edge tolerance: High-contrast edges undergo large intensity swings from sub-pixel mechanical jitter. Dilating Sobel gradient magnitude and scaling by $\alpha$ widens the tolerance envelope specifically along boundaries while keeping flat packaging surfaces tightly inspected.
- Lighting drift compensation: Global illumination shifts from strobe degradation or ambient changes are normalized prior to comparison, preventing false whole-part rejections.
- Line safety first: Any defect cluster exceeding `min_defect_area_px` immediately triggers `VS_INSPECT_DEFECT` with exact bounding box and contrast metrics. Invalid parameters or unfinalized models fail safe to `VS_INSPECT_ERROR` (never OK).

## Update log (section 8)
- 2026-11-02: Ready for Claude review and verification on plant line datasets.
