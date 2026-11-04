# Measure tool: end edges of a row of edges on a soft image lean outwards; joint fit of neighbouring edge responses

- Status: done
- Session: measure-end-edges, 2026-11-03
- Pull request: #317
- Section 5 item: new (pending from docs/changes/2026-10-31-claude-keen-neumann-195cbc.md)
- Components/files: engine/src/measure/measure_tool.cpp, engine/include/vs_measure.h (comments only), engine/tests/measure_tests.cpp, dotnet/VisionCore/Tools/MeasureTool.cs, dotnet/VisionCore.Tests/MeasureToolTests.cs, docs/REQUIREMENTS-VISION-SYSTEM.md (section 4, MEA-01), docs/vision-system/TOOL-SPECIFICATIONS.md (2.9), docs/vision-system/JOB-FILE-FORMAT.md (edges)
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #317: the end edges' lean is taken off; the inner-spacing mean and the 0.5 px end allowance are gone) Measure tool: end edges of a row of edges on a soft image lean outwards (0.35 px at 1 px blur, 3.2 px spacing)

## Timeline (section 3)
| 2026-11-04 | Measure tool end edges (Req 4 MEA-01): the first and last edge of a row (and either edge of a lone narrow pin) no longer lean outwards on a soft image. Worst end-edge error over eight sub-pixel places, camera-like images: 1 px blur and 3.2 px apart 0.46 -> 0.052 px; lone 3.2 px pin 0.34 -> 0.083 px; 2 px defocus disk 4 px apart 0.22 -> 0.025 px; the drawn soft thread of measure_tests 0.35 -> 0.044 px; a nearly sharp row (0.3 px blur) 0.117 -> 0.060 px. Inner edges and sharp pins unchanged. Edge count's mean spacing is again first to last, and the .NET Pitch check holds the end spacings to the limits with no allowance | 4, 12 |

## Decisions (section 4)
| End-edge lean taken off by redrawing the row | The joint fit was tried first: a Gaussian or blurred-step model fitted to the edge and its neighbours is exact on a Gaussian blur but 0.1-0.2 px off on a defocus disk, so no blur model is assumed. The edges of one profile share one blur and an end edge's free side shows it clean; mirrored, it is the template. The row is drawn again sample by sample from it at the measured places and heights, the same windowed centroid is taken on the drawing and on the profile, and the drawing's lean is taken off (a few rounds). An edge whose response does not reach halfway to its neighbour (sharp edges) is left as measured | 4, 12 |
| End allowance removed | With the lean down to a few hundredths of a pixel, the 0.5 px allowance on an edge count's end spacings would only let a crest that is really out of place pass (a false accept); the Pitch check now treats every spacing alike, and a first crest 0.375 px out fails (the .NET fine-thread test now draws every edge on its 1/8 px grid, as the drawing's own quantization had put an end spacing 0.05 px off) | 12 |
| Noise on end edges | The correction reads the edge's free side, so with noise the end edges scatter about three times more than before (sd 0.036 px at 2 grey levels on a 160-level step, box 10 px high) but without the 0.3-0.4 px bias | 4 |

## Update log (section 8)
| 2026-11-04 | Requirements section 4 Measure row: MEA-01 (end edges placed alike). TOOL-SPECIFICATIONS 2.9 and JOB-FILE-FORMAT edges: the end-edge lean and the 0.5 px allowance replaced by the correction | Claude (Mac, branch claude/measure-end-edges-4d4f4d) |

## Pending
- Measure tool: end edges on rows closer than 2.6 px with a soft image are still up to 0.22 px off (was 0.49); a lone pin at 1.5 px blur and 4 px width up to 0.11 px
