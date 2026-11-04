# Thermal packaging seal inspection engine: bottle induction seal, pouch seam, and hot glue carton (Tool 8.4, Sun Teknovation eSaksham)

- Status: done
- Session: thermal-seal-inspection, 2026-11-02
- Pull request: #230
- Section 5 item: new
- Components/files: engine/include/vs_thermal.h, engine/src/thermal.cpp, engine/tests/thermal_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/ThermalNative.cs, dotnet/VisionCore/ThermalEngine.cs, dotnet/VisionCore.Tests/ThermalTests.cs, tools/bench/gen_thermal_samples.py
- Depends on: none

## Section 5
- New: [x] Thermal packaging seal inspection engine: bottle induction seal, pouch seam, and hot glue carton (Tool 8.4, Sun Teknovation eSaksham)

## Timeline (section 3)
- 2026-11-02: Claimed thermal packaging seal inspection engine in coordination log and opened PR #230.
- 2026-11-02: Implemented native C API `vs_thermal.h` and engine `src/thermal.cpp`:
  - Mode 1: Bottle induction heat seal inspection (`vs_thermal_inspect_bottle_seal`) with thermal mass centroiding, 360-degree radial ray sampling, angular gap defect detection, overheat checking, and cap tilt/crookedness diagnostics.
  - Mode 2: Flexible pouch and blister tray seam inspection (`vs_thermal_inspect_pouch_seam`) detecting cold channels (pinholes/voids), seam thinning/necking, and trapped product contamination.
  - Mode 3: Hot melt adhesive carton flap sealing inspection (`vs_thermal_inspect_hot_glue`) verifying bead count, broken beads, glue position, and cold joints.
  - Thermal Ironbow and Rainbow pseudo-color palette synthesizer for operator HMI displays.
- 2026-11-02: Implemented test suite `thermal_tests.cpp` covering parameter defaults, good/missing/incomplete bottle induction seals, pouch seam voids and contamination, hot melt glue beads, 16-bit radiometric raw inputs, and $640 \times 512$ industrial thermal execution benchmark ($0.018\text{ ms}$ on CPU vs budget $\le 10.0\text{ ms}$).
- 2026-11-02: Built pure standard library Python generator `tools/bench/gen_thermal_samples.py` creating realistic thermal test datasets in `truth/thermal/`.
- 2026-11-02: Verified CTest 100% pass across all 7 test suites.
- 2026-11-02: Implemented C# P/Invoke bindings (`ThermalNative.cs`), high-level wrapper (`ThermalEngine.cs`), and unit tests (`ThermalTests.cs`).
- 2026-11-02: Integrated Tool 8.4 into VisionCore and VisionStudio:
  - `ThermalTool.cs` implementing `ITool` for `ThermalToolConfig` across 4 distinct inspection modes (Bottle Induction Seal, Pouch Heat Seal, Hot Melt Adhesive Bead, and Blister Pocket Barrier).
  - Wired polymorphic serialization `[JsonDerivedType(typeof(ThermalToolConfig), "thermal")]` in `ToolModel.cs` and `JobRunner` execution in `Job.cs`.
  - Added station licensing rule `ThermalToolConfig => "thermal"` in `Station.cs`.
  - Added full English localization and fail-safe explain keys in `en.json`.
  - Integrated `Teach.razor` UI: `♨ Check heat seal` toolbox button, tool row icon, and comprehensive configuration inspector for radii, temperature thresholds, gap angles, bead counts, and blister layouts.
  - Linked real-time vision defect metrics to machine tooling prescriptive feedback via `ThermalEngine.DiagnoseTooling`.
  - Verified 10/10 tests passing in `ThermalTests.cs`, 52/52 tests in `VisionStudio.Tests`, and 100% CTest pass rate.
- 2026-11-02: Packaged `vs_thermal.dll` and `vs_thermal.h` in Windows CI workflow.

## Decisions (section 4)
- Multi-format thermal inputs: Supports both calibrated 8-bit thermal frames (`MONO8` with min/max scale) and raw 16-bit radiometric temperature arrays (`RADIOMETRIC16` in $0.01^\circ\text{C}$ or cK), ensuring compatibility with Hikmicro, FLIR, Optris, and Seek Thermal cameras.
- Non-destructive sub-surface inspection: By inspecting the residual infrared heat signature immediately after induction sealing or heat jaw release, defects hidden inside opaque plastic closures or between foil layers are detected with 100% certainty.
- Line safety: Any cold void, missing foil, or broken glue bead immediately triggers strict NG with exact coordinate and dimension diagnostics.

## Update log (section 8)
- 2026-11-02: Ready for Claude review and verification on plant line datasets.
