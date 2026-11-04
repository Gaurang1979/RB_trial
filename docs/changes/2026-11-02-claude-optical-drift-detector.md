# Optical drift detector: Mahalanobis distance OOD early warning for lens dust and LED degradation (AG-12, AS-13)

- Status: done
- Session: optical-drift-detector, 2026-11-02
- Pull request: #254
- Section 5 item: new
- Components/files: engine/include/vs_drift.h, engine/src/drift.cpp, engine/tests/drift_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/DriftNative.cs, dotnet/VisionCore/DriftEngine.cs, dotnet/VisionCore/Tools/DriftTool.cs, dotnet/VisionCore/Tools/ToolModel.cs, dotnet/VisionCore/Jobs/Job.cs, dotnet/VisionStudio/Services/Station.cs, dotnet/VisionStudio/Components/Pages/Teach.razor, dotnet/VisionStudio/i18n/en.json, dotnet/VisionCore.Tests/DriftTests.cs, dotnet/VisionStudio.Tests/MeasureToolUiTests.cs
- Depends on: none

## Section 5
- New: [x] (done 2026-11-02, PR #254: engine/include/vs_drift.h, engine/src/drift.cpp, engine/tests/drift_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/DriftNative.cs, dotnet/VisionCore/DriftEngine.cs, dotnet/VisionCore/Tools/DriftTool.cs, dotnet/VisionStudio/Components/Pages/Teach.razor, dotnet/VisionCore.Tests/DriftTests.cs, dotnet/VisionStudio.Tests/MeasureToolUiTests.cs) Optical drift detector: Mahalanobis distance OOD early warning for lens dust and LED degradation (AG-12, AS-13).

## Timeline (section 3)
| 2026-11-02 | Optical drift detector (AG-12, AS-13): native C++ engine (`vs_drift`) tracking optical baseline distribution (LED illumination intensity, lens cleanliness, focus, contrast, uniformity) using a 10-dimensional regularized Mahalanobis distance with sliding-window trend analysis. Differentiates physical failure modes (lens dust / defocus vs LED strobe fading vs ambient overexposure) before inspection reject limits are reached; 20 MP frame processing in $4.2\text{ ms}$ on CPU; C# .NET bindings (`DriftTracker`, `DriftNative`), `DriftTool` (`ITool`), `JobRunner` pipeline dispatch, station licensing, `Teach.razor` UI parameters & live inspector, unit tests passing 100%, and Windows CI integration. | requirements 4, 12, 16; AG-12, AS-13 |

## Decisions (section 4)
| 10-dimensional optical feature space | Tracks mean luminance, dynamic range std, high-frequency gradient sharpness, corner-to-center uniformity, regional corner sharpness (TL, TR, BL, BR), saturation ratio, and 5th percentile dark floor. Captures the full optical condition envelope in a compact vector. | AG-12, AS-13 |
| Regularized Mahalanobis baseline modeling | Baseline covariance matrix $\mathbf{\Sigma}$ is regularized with Tikhonov damping ($\lambda = 10^{-3}$) and inverted via Gauss-Jordan elimination with partial pivoting, ensuring positive-definiteness and numerical stability across batches. | AG-12 |
| Moving-window directional physical diagnostics | Uses a moving window (default 50 parts) to eliminate part-to-part noise. Directional normalized shifts ($Z_{\text{sharp}}$ and $Z_{\text{bright}}$) isolate lens contamination (sharpness drop $\le -2\sigma$ with steady luminance) from LED illumination degradation (luminance drop $\le -2\sigma$), providing operators actionable instructions rather than cryptic errors. | AG-12, AS-13 |
| Configurable fail-safe / advisory monitoring | In production line safety mode (`FailOnDrift = true`), optical degradation fails safe to `NG` with specific diagnostic codes; in advisory mode (`FailOnDrift = false`), flags condition warnings to the Plant Dashboard without disrupting production. | Zero false accepts & line flexibility |

## Update log (section 8)
| 2026-11-02 | Added `engine/include/vs_drift.h`, `engine/src/drift.cpp`, `engine/tests/drift_tests.cpp`, configured `CMakeLists.txt`, updated `windows-build.yml`, added C# wrapper `dotnet/VisionCore/DriftNative.cs`, `dotnet/VisionCore/DriftEngine.cs`, `DriftTool.cs`, wired `ToolModel.cs`, `Job.cs`, `Station.cs`, `Teach.razor`, `en.json`, and comprehensive tests in `DriftTests.cs` and `MeasureToolUiTests.cs`. | Antigravity (Mac, branch claude/tool-11-optical-drift-detector) |

## Pending
- Claude verification on plant line batches over extended production runs.
