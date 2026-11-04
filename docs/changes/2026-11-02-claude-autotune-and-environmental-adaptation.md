# Autonomous Environmental Auto-Tuning, 9-Zone MTF Mount Tilt Diagnostics, and Closed-Loop Tooling Advisory

- Status: done
- Session: autotune-and-environmental-adaptation, 2026-11-02
- Components/files: engine/include/vs_autotune.h, engine/src/autotune.cpp, engine/tests/autotune_tests.cpp, engine/CMakeLists.txt, dotnet/VisionCore/AutotuneNative.cs, dotnet/VisionCore/AutotuneEngine.cs, dotnet/VisionCore/Tools/AutotuneTool.cs, dotnet/VisionCore/Tools/ToolModel.cs, dotnet/VisionCore/Jobs/Job.cs, dotnet/VisionCore.Tests/AutoTuneTests.cs, dotnet/VisionStudio/Services/Station.cs, dotnet/VisionStudio/Components/Pages/Teach.razor, dotnet/VisionStudio/i18n/en.json, dotnet/VisionStudio.Tests/MeasureToolUiTests.cs, .github/workflows/windows-build.yml
- Depends on: none

## Summary
Implements autonomous inline environmental adaptation and optical/tooling closed-loop diagnostics to overcome harsh factory production realities:
1. **Translucent Roof Sunlight & Circadian Lighting Drift**:
   - High-speed strobe-minus-ambient differential image subtraction (`vs_strobe_minus_ambient`) neutralizing ambient sunlight leakage, open plant doors/windows, and ceiling LED changes.
   - Real-time histogram percentile tracking and circadian lighting analyzer (`vs_lighting_analyze`) computing gain/exposure trim dynamically.
2. **Camera Scheimpflug Optical Tilt & Lens Defect Check**:
   - 9-zone spatial Tenengrad MTF sharpness matrix diagnostic (`vs_check_mount_tilt`) detecting half-sharp/half-blurred images caused by misaligned camera mounting brackets.
   - Automated directional advice informing operators precisely which screw axis (Pitch/Roll) requires adjustment.
   - Pinhole dark spot detector (`vs_check_lens_dust`) flagging physical lens dust, optical contamination, or water sweating.
3. **Upstream Tooling Drift & Hologram Applicator Advisor**:
   - Sub-pixel hologram/cap displacement tracker (`vs_calculate_applicator_guidance`) converting pixel drift into physical lead-screw rotations (CCW/CW) with pitch-aware metric calculation.
   - Closed-loop operator HMI prompts to adjust physical applicator screws before out-of-tolerance rejects occur.
4. **Vibration & Surface Sweat / Condensation Rejection**:
   - High-speed shutter synchronization and multi-exposure confidence thresholding fail-safe to NG on ambiguous condensation or refractive droplets.
5. **Blazor UI Studio & Job Tool Integration**:
   - `AutotuneTool` fully integrated into `JobRunner`, `Station`, and `Teach.razor` UI with 9-zone graphical HUD overlay and real-time operator alerts.

## Verification
- Native C++ engine tests (`autotune_tests`): 7/7 tests passed in 0.00s.
- Native CTest suite: 23/23 tests passed (100% pass).
- Managed Core test suite (`VisionCore.Tests`): 738/738 tests passed.
- Managed UI test suite (`VisionStudio.Tests`): 63/63 tests passed.
- Total: 824 tests passed across C++ and C# with zero regressions.
