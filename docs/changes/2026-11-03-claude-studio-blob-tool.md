# Blob tool in the Avalonia Vision Studio: designer panel, outline unit (px / mm), "Auto tolerance" over stored pictures through a new runtime call

- Status: done
- Session: studio-blob-tool (Claude, side session), 2026-11-03
- Pull request: #307
- Section 5 item: new
- Components/files: dotnet/Studio (JobDraft, DesignerBlob, DesignerDrop, DesignerView, en.json, Icons), proto/visionstudio/remote/v1/runtime.proto (SuggestBlobTolerance, LensCalibration), dotnet/Runtime.Host (JobDesigner, ToolTeach, Services, csproj copies libvs_blob), dotnet/VisionCore/Tools/BlobTool.cs (AutoTolerance per-picture fixtures), tests, requirements 17.12 BLB-OUT-03..06
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #307: Blob tool in the Avalonia Vision Studio with its full settings panel, toolbox tile and drop, Teach of the outline, outline unit px / mm with a calibration, Auto tolerance with Accept / Not this through the new runtime call SuggestBlobTolerance; every edit undoable) Blob tool in the Avalonia Vision Studio: designer panel, outline unit (px / mm), Auto tolerance over stored pictures

## Timeline (section 3)
| 2026-11-03 | Avalonia Vision Studio: Blob tool (toolbox tile, drop without snapping, panel for BlobToolConfig, a new tool has `Presence: Present`); Teach on a Blob tool teaches the first spot's outline (start deviation 2 px, part required, Locate fixture used); outline unit px / mm, mm only with the camera's calibration, limits converted; new contract call `SuggestBlobTolerance` (BlobTool.AutoTolerance over 3-30 stored pictures, job designers only, refused while the line runs, returns the lens calibration); Studio "Auto tolerance" sends the pictures that passed Try all, Accept / Not this, Accept is one undo step. Studio.Tests 313, Runtime.Host.Tests 127, RuntimeContract.Tests 19, VisionCore Blob and job-load tests 145 pass | 17.12 BLB-OUT-03..06; 16 AS-18 |

## Decisions (section 4)
| Topic | Decision and reason | requirement sections |
| --- | --- | --- |
| Calibration travels in the job JSON, and the new call echoes it | The camera's calibration is already in the job file (`Cameras[].Calibration`), which Studio keeps as it is; Studio reads `MmPerPx` there to offer mm, and `SuggestBlobTolerance` returns the full `LensCalibration` the runtime used, so no existing message changes (backward compatible) | 17.12 BLB-OUT-05, BLB-OUT-06 |
| A taught outline always has a limit and a part | Teach sets a start deviation of 2 px when none is set, and `Present` when no part is required: VisionCore's setup check refuses a taught outline with no limit and outline rules without a part, and the tool must never pass any shape | 17.12 BLB-OUT-04; line safety |
| A unit change never loosens a limit | px / mm converts the dent and deviation limits with the camera's mm per px; without a calibration mm is refused, and mm to px is refused while a limit is set (dropping it would loosen the check) | 17.12 BLB-OUT-05; line safety |
| Auto tolerance measures good parts only | Studio sends the pictures that passed the last "Try all pictures" (every stored picture when none was run, with a tip to use good parts); the runtime suggests nothing when a picture has no single whole outline; Accept is the engineer's | 17.12 BLB-OUT-02, BLB-OUT-06 |
| The runtime ships libvs_blob itself | The retired VisionStudio project's native list has no vs_blob and is not changed, so Runtime.Host.csproj copies libvs_blob.dylib / vs_blob.dll | 17.12 BLB-OUT-06 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.12 BLB-OUT-03..06; runtime.proto: SuggestBlobTolerance and LensCalibration | Claude (Mac, branch claude/studio-blob-tool) |

## Pending
- Blob tool in Vision Studio: teach the outline from a clicked spot (needs a spot index on TryJob's teach), and draw the dent and deviation marks on the picture
- Blob tool in Vision Studio: the 14 other languages for the new design.blob.* strings
- Windows line PC package: include vs_blob.dll in the package's native file list (Runtime.Host.csproj copies it; the corert64 dispatcher route is not changed here)
