# Blob tool: 4-connected hole count fix and contour analysis (tool spec 4.2)

- Status: done
- Session: blob-contour (Claude, side session), 2026-10-31
- Pull request: #208
- Section 5 item: Blob tool: hole count for 4-connected blobs
- Components/files: engine/include/vs_blob.h, engine/src/blob/blob_tool.cpp, engine/tests/blob_tests.cpp, dotnet/VisionCore/Tools/BlobTool.cs, dotnet/VisionCore.Tests/BlobToolTests.cs, dotnet/VisionStudio/i18n/en.json, docs/vision-system/JOB-FILE-FORMAT.md 3.6.1, docs/vision-system/TOOL-SPECIFICATIONS.md 4.2
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #208: holes are background regions not reached from outside, the background 8-connected for 4-connected blobs, so a pocket open across a diagonal is no hole; test) Blob tool: hole count for 4-connected blobs counts a hole that leaks diagonally to the outside (findContours sees background 4-connected)
- "Blob tool: contour analysis": `[x] (done 2026-10-31, PR #208: engine vs_blob contour calls and VisionCore CheckOutline with dent, convexity and taught-outline deviation rules, job file fields, tests)`

## Timeline (section 3)
| 2026-10-31 | Blob tool: 4-connected hole count fixed (a pocket open across a diagonal is no hole); contour analysis ("Check outline", tool spec 4.2): engine `vs_blob` contour calls (subpixel outline by moving each boundary point along its normal to the grey edge level, length, enclosed area, convexity, defects, deviation from a taught outline after centroid/axis placement and trimmed closest-point ICP), VisionCore `BlobTool` `CheckOutline` with dent, convexity and deviation rules, teach from a clicked blob (refused for a cut or broken outline, or a dark or white frame), outline drawn simplified; blob_tests 84 checks and VisionCore BlobToolTests 33 pass; a 2 288-point taught outline takes 4.2 ms (target 5) on the development Mac | 4.1, 4.2 tool spec; 12 |

## Decisions (section 4)
| Contour analysis lives in the Blob tool | The outline is measured on the Count / spot tool's kept blobs (`CheckOutline`), not as a separate node: the blobs, region, fixture and time budget are already there. The engine measures, VisionCore decides (pass rules), as for the other blob rules. New C structs carry `struct_size`; `vs_blob_params` and `vs_blob_info` are unchanged, so the existing P/Invoke layout holds | tool spec 4.2; ARCHITECTURE.md |
| Subpixel outline by normal search, not marching squares | Each boundary-chain point moves along the outline's normal to where the smoothed grey crosses the edge level: one ordered outline per blob, tied to the blob found, no iso-line selection; on an exactly anti-aliased disc the mean radius is within 0.1 px and every point within 0.3 px | tool spec 4.2 |
| Outline rules need a part | Outline rules are refused unless a lowest count of 1 or more is set (`Present` or `minCount`): a missing part, or one the filters leave out, would otherwise pass them unchecked. A part left out for touching the area's edge fails `not_closed`. The dent rule uses the deepest indent even when it is shallower than `minDefectDepth` (which only shortens the listed defects) | 12 |
| A broken outline is not checked | With an outline rule set, an outline cut by the area's edge or with under 90% (`minEdgeShare`) of its points on a grey edge fails with `explain.contour.not_closed`; a taught outline needs a largest deviation, and a largest deviation needs a taught outline (validator), so a set-up rule can never pass any shape | 12 |

## Update log (section 8)
| 2026-10-31 | JOB-FILE-FORMAT.md 3.6.1: outline fields and rules; TOOL-SPECIFICATIONS.md 4.2: what is built, two new explain keys; vs_blob.h: contour API and the hole rule | Claude (Mac, branch claude/blob-contour) |

## Pending
- VisionCore.Tests on main: JobLoadTests "Wrong_types_unknown_fields_and_empty_entries_are_refused" still expects a `"kind": "blob"` tool to be refused (stale since the Blob tool, PR #180), and ReadAgreementTests "ReadTextNeedsASecondReader" and "ReadTextPassesOnlyWhatBothReadersRead" fail on main too (found 2026-11-01, not caused by PR #208)
- Blob tool: Vision Studio controls for the outline check (switch, teach from a clicked blob, rules) and drawing the dent marks
- Blob tool: outline values in mm with a calibration, and auto-tolerance of max deviation over good parts (tool spec 1)
