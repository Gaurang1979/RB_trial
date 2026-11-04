# Blob tool: outline values in mm with a calibration, auto-tolerance from good images

- Status: done
- Session: blob-outline-mm (Claude, side session), 2026-11-03
- Pull request: #281
- Section 5 item: Blob tool: outline values in mm with a calibration, and auto-tolerance of max deviation over good parts (tool spec 1)
- Components/files: dotnet/VisionCore/Tools/BlobTool.cs, dotnet/VisionCore/Jobs/Job.cs (passes the camera's calibration to the tool and its setup check), dotnet/VisionCore.Tests/BlobToolTests.cs, requirements 17.12 BLB-OUT-01, BLB-OUT-02, TOOL-SPECIFICATIONS.md 4.2
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #281: `outlineUnit` Px or Mm for the largest dent and deviation and the reported values, Mm needs the camera's calibration; `BlobTool.AutoTolerance` suggests the limits from good images; tests) Blob tool: outline values in mm with a calibration, and auto-tolerance of max deviation over good parts (tool spec 1)

## Timeline (section 3)
| 2026-11-03 | Blob tool: `outlineUnit` (`Px` default, `Mm`); limits and `max_dent` / `max_deviation` in that unit, Mm refused at setup without a calibration, px values say "the camera has no real size"; `AutoTolerance` (worst good value x 1.5 + 0.3 px, needs 3+ good images each with one closed found outline, never applied by itself); BlobToolTests 38 and JobLoadTests pass | 17.12 BLB-OUT-01, BLB-OUT-02; tool spec 4.2 |

## Decisions (section 4)
| Topic | Decision and reason | requirement sections |
| --- | --- | --- |
| Limits follow the unit, the engine stays in px | The engine measures in px; VisionCore brings the engineer's limit to px (limit / mm per px), so no C API or struct change and the taught outline stays in px | 17.12 BLB-OUT-01 |
| Auto-tolerance only suggests | A limit learned from good parts could loosen a check, so the tool returns a suggestion (null when any image is not a single closed found outline) and the engineer accepts it | 17.12 BLB-OUT-02; 17 line safety |

## Update log (section 8)
| 2026-11-03 | Requirements 17.12 (BLB-OUT-01, BLB-OUT-02); TOOL-SPECIFICATIONS.md 4.2: what is built | Claude (Mac, branch claude/blob-outline-mm) |

## Pending
- Blob tool: Vision Studio control for the outline unit and an "Auto tolerance" button using `BlobTool.AutoTolerance` (Accept / Not this)
