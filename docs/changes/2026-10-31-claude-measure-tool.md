# Measure tool: calipers, line and circle fit, distances and angles, px and mm, placed on the Locate result

- Status: done
- Session: measure-tool, 2026-10-31
- Pull request: #182
- Section 5 item: P1 Foundation: tool framework and C API
- Components/files: engine (new vs_measure.h, src/measure/measure_tool.cpp, tests/measure_tests.cpp, CMakeLists.txt), dotnet VisionCore (Tools/MeasureTool.cs new, ToolModel.cs, Jobs/Job.cs), VisionCore.Tests/MeasureToolTests.cs, VisionStudio (i18n/en.json, VisionStudio.csproj), Windows packaging (build-windows.bat, windows-build.yml), docs/vision-system/JOB-FILE-FORMAT.md
- Depends on: none

## Section 5
Measure built (tool spec 2.1 caliper, 2.2 line, 2.3 circle, 2.6 distance and angle): engine library `vs_measure` (vs_measure.h, additive: no change to codereader.h or vs_locate.h), VisionCore `MeasureTool` (kind `measure`: features and checks with Min/Max tolerances, NG reason keys), mm through the camera's lens calibration, features placed on the Find the part result. Engine tests: 0.026 px worst on clean drawn edges, 0.041 px 3σ on soft noisy ones, 0.011° line angle, 0.02 px circle radius.

## Timeline (section 3)
| 2026-10-31 | Measure tool ("Check size"): calipers along a turned box (single edge or pair, polarity, auto or set threshold, score by contrast, position or width) with sub-pixel edges from the centroid of a Gaussian derivative; line fit (calipers across the line, RANSAC then total least squares) and circle fit (radial calipers, RANSAC on three-point circles, then algebraic and geometric least squares); distance (point-point, point-line, line-line) and angle between features; all in undistorted "measure space", mm = px × the camera's scale; features follow the Locate result. Engine library vs_measure with C API vs_measure.h; VisionCore MeasureTool with tolerance checks, values to the part result and explain keys; job file `kind: "measure"` and `Cameras[].Calibration`. Engine measure_tests 35/35 (worst 0.026 px on clean drawn edges at 0°, 10°, 33°, 90°; 3σ 0.041 px on a soft edge with noise; line angle 0.011°, rms 0.023 px with a dirt blob left out; circle centre 0.005 px, radius 0.02 px; 0.002 ms per 40 × 20 px caliper); VisionCore MeasureToolTests 16/16 (width 120.5 px exact, hole diameter within 0.01 px, 90° corner, mm with a scale, fixtured part turned 12°) | 13, 15 |

## Decisions (section 4)
| Measure in its own engine library | `vs_measure` (vs_measure.h) is a separate library like vs_locate, with stateless thread-safe functions, so the tool licenses and loads apart (`VS-MEA-`, ARCHITECTURE.md) and does not touch codereader.h, which other open pull requests change | 13, 15 |
| Sub-pixel edge by derivative centroid | The parabola through the peak (TOOL-SPECIFICATIONS 2.1) and a parabola through its logarithm both miss the 0.05 px goal on a sharp axis-aligned edge (0.089 px); the centroid of the smoothed derivative over its reach is exact for an area-sampled step whatever the smoothing, and gives 0.026 px worst (the 8-bit rounding) and 0.041 px 3σ with noise. The parabola stays as the fallback | 13, 15 |
| Measure space and mm | Edges are undistorted with the camera's lens model (the same Brown-Conrady model as calibration.cpp, read only) before fitting, so a lens does not bend a straight edge or bias a distance; mm are measure-space px × MmPerPx. A check in mm on a camera without a scale is refused when the job is saved and fails at run time | 12, 15 |
| Doubt is NG | A missing or faint edge, two candidates within 90 % of the best score (the choice in doubt), a box off the image, a line or circle with fewer points agreeing than its minimum share, a value not measured, or a value out of tolerance fails the tool, with its explain key | 12 |
| Angles against the part | A line's Angle check is against the part (the Locate turn taken off) when the camera has a Find the part tool, so a turned part does not fail a squareness check | 15 |
| Straightness and roundness count every point | Straightness and Roundness are measured over every point found, also those the robust fit left out, so a dent or bow up to 40 % of the edge (MinShare 0.6) cannot hide as outliers and pass; the fit itself still ignores them | 12 |
| A caliper in doubt gives no point | Inside a line or circle, a caliper with two candidates within 90 % gives no point, so a double edge counts against the share used instead of being fitted silently | 12 |
| Last line refit by the noise | A caliper only partly over a dent gives a point about a pixel off, inside the 1.5 px outlier limit, which turned a 7° line by 0.08°; with RANSAC the last refit of a line also leaves out points beyond three robust sigmas of the others (at least 0.25 px). Circles keep the plain limit: round a small hole the radial calipers' spread is systematic, and cutting it moves the centre | 12, 15 |
| No blind box ends | The profile is sampled 2 R + 2 px past each end of the box (R the filter's reach), so an edge at the box's end is found and a weaker inner edge is never picked in its place; that margin must lie inside the image | 12, 15 |
| Review before ready | An Opus review found straightness and roundness capped by the outlier distance, ambiguous calipers fitted silently inside lines and circles, blind box ends, Position counted from a different start than documented and angle limits round 180° impossible; all fixed with tests | 12 |
| One tool, several features | A Measure tool holds its features (caliper, line, circle) and the checks on them, so distances and angles between features need no wiring between tools; on the target format each becomes a node (JOB-FILE-FORMAT.md section 7) | 15 |

## Update log (section 8)
| 2026-10-31 | JOB-FILE-FORMAT.md: new section 7, today's job file for the Measure tool (features, checks, units, `Cameras[].Calibration`); open questions become section 8. en.json: 15 English explain keys for the Measure tool | Claude (Mac, branch claude/measure-tool) |

## Pending
- Vision Studio panel for the Measure tool: draw a caliper box, line or ring on the teach image, pick polarity, add checks with tolerances, auto-learn (click an edge) and the tolerance bands drawn green and red (Studio is held by #147 and #173)
- Measure tool: the remaining tools of spec 2: ellipse (2.4), corner (2.5), gap (2.7), pin pitch (2.8) and edge count (2.9); intersections, perpendiculars and constructions of 2.6; Tukey reweighting for line fit
- Measure tool: auto-tolerance from good parts and auto-tune of threshold and smoothing (spec 2 "Auto-tolerance applies to every numeric output"), and a confidence per check
- Measure tool: translate the 15 new explain.* keys into the other 14 languages once the Studio string tables carry them
- Measure tool: repeatability (0.1 px 3σ target) and timing on real parts on the line PC
- vs_measure.h result structs have no struct_size (as vs_locate.h): add one before they ever grow
