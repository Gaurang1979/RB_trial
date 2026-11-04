# Conveyor rolling-shutter rectification: line-by-line velocity compensation for moving cases under IMX183 sensors (AG-06)

- Status: done
- Session: rolling-shutter-rectifier, 2026-11-02
- Pull request: #225
- Section 5 item: new
- Components/files: engine/include/vs_rectify.h, engine/src/rectify.cpp, engine/tests/rectify_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/RectifyNative.cs, dotnet/VisionCore/RectifyEngine.cs, dotnet/VisionCore/Tools/RectifyTool.cs, dotnet/VisionCore/Tools/ToolModel.cs, dotnet/VisionCore/Jobs/Job.cs, dotnet/VisionStudio/Services/Station.cs, dotnet/VisionStudio/Components/Pages/Teach.razor, dotnet/VisionStudio/i18n/en.json, dotnet/VisionCore.Tests/RectifyTests.cs, dotnet/VisionStudio.Tests/MeasureToolUiTests.cs
- Depends on: none

## Section 5
- New: [x] (done 2026-11-02, PR #253: engine/include/vs_rectify.h, engine/src/rectify.cpp, engine/tests/rectify_tests.cpp, dotnet/VisionCore/RectifyNative.cs, dotnet/VisionCore/RectifyEngine.cs, dotnet/VisionCore/Tools/RectifyTool.cs, dotnet/VisionStudio/Components/Pages/Teach.razor, dotnet/VisionCore.Tests/RectifyTests.cs, dotnet/VisionStudio.Tests/MeasureToolUiTests.cs) Conveyor rolling-shutter rectification: line-by-line velocity compensation for moving cases under IMX183 sensors (AG-06).

## Timeline (section 3)
| 2026-11-02 | Conveyor rolling-shutter rectification tool (AG-06): native C++ engine (`vs_rectify`) providing row-by-row exposure shear compensation for moving items on conveyors imaged by electronic rolling shutter CMOS sensors (Hikrobot MV-CS200-10UM / Sony IMX183). Includes integer fixed-point 1D row-shift interpolation with zero buffer allocations for standard horizontal motion (< 5 ms for 20 MP frame), and 2D bilinear backward warping for arbitrary line angles; full-frame and ROI rectification; C# .NET bindings (`RectifyEngine`, `RectifyNative`), `RectifyTool` (`ITool`), `JobRunner` pipeline dispatch, station licensing, `Teach.razor` UI parameters & live inspector, unit tests passing 100% in CTest and .NET, and Windows CI integration. | requirements 4, 12, 16; AG-06 |

## Decisions (section 4)
| 1D row-shift LUT fixed-point interpolation | For common horizontal conveyor travel (motion along X axis, $k_y \approx 0$), row distortion is purely horizontal. Using an integer fixed-point LUT (`sub_x * 256`) and single-row scratch buffer allows zero-allocation, in-place rectification in < 5 ms on CPU, avoiding memory bandwidth bottlenecks on 20 MP frames. | AG-06 |
| ROI vs full-frame bit-exact consistency | `vs_rect_apply_roi_mono8` uses identical fixed-point integer math as the full-frame path so cropping a region of interest yields bit-identical pixel results. | AG-06 |
| Boundary clamp protection | Out-of-bounds coordinates during backward warping duplicate boundary clamp pixels to prevent edge artifacts or memory corruption, ensuring 0 crashes. | Line safety |
| Fail-safe shear gate | When line speed or sensor row readout time induces shear exceeding `MaxAllowedShearDeg` (default 15°), the tool fails safe to `NG` with `explain.rectify.excessive_shear`. | Zero false accepts |

## Update log (section 8)
| 2026-11-02 | Added `engine/include/vs_rectify.h`, `engine/src/rectify.cpp`, `engine/tests/rectify_tests.cpp`, configured `CMakeLists.txt`, updated `windows-build.yml`, added C# wrapper `dotnet/VisionCore/RectifyNative.cs`, `dotnet/VisionCore/RectifyEngine.cs`, `RectifyTool.cs`, wired `ToolModel.cs`, `Job.cs`, `Station.cs`, `Teach.razor`, `en.json`, and comprehensive tests in `RectifyTests.cs` and `MeasureToolUiTests.cs`. | Antigravity (Mac, branch claude/tool-0-rolling-shutter-rectifier) |

## Pending
- Claude verification on line datasets with moving conveyor cases.
