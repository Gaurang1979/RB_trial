# Blob tool: threshold, connected components, measures, filters, pass rules (P1 Foundation)

- Status: done
- Session: blob-tool, 2026-10-31
- Pull request: #180
- Section 5 item: P1 Foundation
- Components/files: engine (new include/vs_blob.h, src/blob/blob_tool.cpp, tests/blob_tests.cpp, CMakeLists.txt: vs_blob library and blob_tests), dotnet VisionCore (Tools/BlobTool.cs new, ToolModel.cs: "blob" kind and ToolResult.Blobs, Jobs/Job.cs: validate and run), VisionCore.Tests/BlobToolTests.cs, VisionStudio/i18n/en.json (explain.blob.* keys), docs/vision-system/JOB-FILE-FORMAT.md 3.6.1, TOOL-SPECIFICATIONS.md 4.1
- Depends on: none

## Section 5
- Blob done (2026-10-31, PR #180): engine `vs_blob` library with an additive C API (vs_blob.h: fixed, Otsu, band and local threshold, connected components, area, perimeter, centroid, boxes, ellipse, holes, compactness, filters, sort, region, time budget); VisionCore Count / spot tool ("kind": "blob") with presence, count, per-blob area and total area rules; job file fields (JOB-FILE-FORMAT.md 3.6.1); English explain keys. Studio panel and line PC packaging pending

## Timeline (section 3)
| 2026-10-31 | Blob tool: engine `vs_blob` (threshold fixed / Otsu / band / local mean, open or close 3x3, hole filling, 4- or 8-connected labelling; per blob area, centroid, perimeter by Vossepoel-Smeulders chain plus pi, circularity, compactness, same-moment ellipse, elongation, upright and turned box, holes, mean grey, edge contact; filters on area, circularity, elongation, edge; sort and keep first N; rejected blobs with their reason). VisionCore `BlobTool` ("kind": "blob") with presence, count range, per-blob area and total area pass rules, dark-frame and time-budget NG, area cut by the picture NG, edge blobs counted against upper limits, confidence per tool spec 4.1. 5 MP with 1 000 blobs: 6.9 ms on this Mac (target 10 ms on T1; histogram in one parallel pass, blobs measured on OpenCV's thread pool). Engine blob_tests 51/51, VisionCore BlobToolTests 18 and JobTests 6 pass | 15 |

## Decisions (section 4)
| Blob runs that cannot finish are NG | A blob run past its time budget, or with more than 20 000 blobs past the cheap filters, reports `truncated` and an empty list; the tool fails the part (`explain.blob.incomplete`) instead of reporting fewer spots. An area darker than mean grey 8 or brighter than 250 fails as a dark or white frame, and an area cut by the picture's edge (also after the part moved it) fails as not checked, so a "no spots allowed" check never passes on a failed light, camera or position. Blobs left out only for touching the area's edge still count against "none allowed", the highest count and the highest total area; keep first N is refused together with those rules. Auto threshold keeps its level at least 15 grey levels from the background's mean instead of reporting "nothing" on low contrast, so one small spot on a clean area is found and noise is never split | 12, 15 |
| Blob tool is its own library | `vs_blob` is a separate shared library with its own header, as `vs_locate`, matching ARCHITECTURE.md (`vs_blob.dll`, licence `VS-BLB-`), so it does not touch codereader.h | 15 |
| Filters versus pass rules | Filters (area, circularity, elongation, edge, keep first N) leave a blob out and show it grey with its reason; pass rules (presence, count, per-blob area, total area) fail the part and mark the offending blob red | 15 |

## Update log (section 8)
| 2026-10-31 | engine/include/vs_blob.h (new C API); JOB-FILE-FORMAT.md 3.6.1: Blob node parameters, pass rules, validation and the VisionCore "kind": "blob" form; TOOL-SPECIFICATIONS.md 4.1: what is built, the further explain keys, line-safety rules | Claude (Mac, branch claude/blob-tool) |

## Pending
- Vision Studio panel for the Count / spot tool (threshold, polarity, filters, pass rules, the blobs drawn green / red / grey), Station.cs naming the tool "blob" in plant events, and VisionStudio.csproj copying libvs_blob.dylib / vs_blob.dll (VisionStudio is held by #147 and #173)
- Line PC package: build vs_blob.dll and run blob_tests in tools/windows/build-windows.bat and the Windows workflow, and ship vs_blob.dll (held by #146; #182 adds vs_measure to the same files)
- Blob tool: auto-learn from a clicked sample blob and auto-tune (tool spec 4.1, 4.4), areas in mm² with a calibration, and the Count tool's split of touching items by distance transform and watershed (tool spec 4.3)
- Blob tool: hole count for 4-connected blobs counts a hole that leaks diagonally to the outside (findContours sees background 4-connected)
- Blob tool: contour analysis (tool spec 4.2: subpixel outline, convexity defects, deviation from a taught outline)
