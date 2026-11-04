# Infrared Inspection Engine: No-IR-Filter CMOS, NIR 750-1050nm & SWIR 900-1700nm Defect Inspection (`vs_infrared`)

- Status: done
- Session: infrared-inspection-engine, 2026-11-02
- Pull request: #233
- Section 5 item: new
- Components/files: docs/vision-system/INFRARED-INSPECTION-AND-DEFECT-ANALYSIS-MASTER-GUIDE.md, engine/include/vs_infrared.h, engine/src/infrared.cpp, engine/tests/infrared_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/InfraredNative.cs, dotnet/VisionCore/InfraredEngine.cs, dotnet/VisionCore.Tests/InfraredTests.cs
- Depends on: none

## Section 5
- New: [x] Infrared Inspection Engine (`vs_infrared`): See-through amber liquid level, metameric security ink discrimination, moisture barrier breach detection, and solar cell EL micro-crack extraction.

## Timeline (section 3)
- 2026-11-02: Authored master engineering guide on No-IR-filter CMOS & SWIR industrial defect inspection (`docs/vision-system/INFRARED-INSPECTION-AND-DEFECT-ANALYSIS-MASTER-GUIDE.md`).
- 2026-11-02: Claimed and implemented `vs_infrared.h` and `infrared.cpp`.
- 2026-11-02: Implemented see-through liquid fill-level profiler with foam rejection derivative filter.
- 2026-11-02: Implemented Normalized Difference Metameric Index (NDMI) separating organic dye inks from carbon-black security markings.
- 2026-11-02: Implemented SWIR 1450nm moisture barrier breach analyzer identifying trapped liquid channels across seal seams.
- 2026-11-02: Implemented 2D Hessian eigenvalue filter isolating dendritic micro-cracks in solar cell electroluminescence (EL).
- 2026-11-02: Implemented native verification test suite `infrared_tests.cpp` (21 assertions passed, benchmark: 3.27 ms for 1024x1024 frames).
- 2026-11-02: Implemented .NET wrapper `InfraredNative.cs`, managed `InfraredEngine.cs`, and xUnit tests `InfraredTests.cs` (100% pass across 4 tests).
- 2026-11-02: Integrated Tool 8.5 into VisionCore and VisionStudio:
  - `InfraredTool.cs` implementing `ITool` for `InfraredToolConfig` supporting FillLevel, MetamericInk, MoistureBarrier, and CrackDetection modes.
  - Polymorphic serialization `[JsonDerivedType(typeof(InfraredToolConfig), "infrared")]` in `ToolModel.cs` and `JobRunner` execution in `Job.cs`.
  - Station licensing `InfraredToolConfig => "infrared"` in `Station.cs`.
  - Localization and fail-safe explain keys in `en.json`.
  - `Teach.razor` UI: `🔬 Check infrared / NIR` toolbox button, tool row icon, and inspector configuration controls.
  - 7/7 tests passing in `InfraredTests.cs` and 53/53 tests in `VisionStudio.Tests`.
- 2026-11-02: Integrated into CMake, CTest, and CI packaging in `windows-build.yml`.

## Decisions (section 4)
- Leveraged physical transmission properties of amber glass and PET in NIR (850-940nm) to measure liquid fill levels through opaque bottles, adding a secondary inflection filter that ignores up to 25 mm of surface foam.
- Designed pixel-wise Normalized Difference Metameric Index (NDMI) to differentiate organic dyes (transparent in NIR) from carbon-black security ink (opaque in NIR) for anti-counterfeiting verification.
- Modeled seal seam moisture penetration via column-wise unbroken barrier thickness, guaranteeing fail-safe NG on any through-channel leak.

## Update log (section 8)
- Added `INFRARED-INSPECTION-AND-DEFECT-ANALYSIS-MASTER-GUIDE.md`.
- Added `vs_infrared.h`, `infrared.cpp`, `infrared_tests.cpp`.
- Added `InfraredNative.cs`, `InfraredEngine.cs`, `InfraredTests.cs`.
- Passed all native and .NET tests with 0 failures.

## Pending
- None. Ready for Claude review and verification.
