# Blob tool in Vision Studio: teach the outline from a clicked spot, and draw the dent and deviation marks on the picture

- Status: done
- Session: studio-blob-teach-marks (Claude, side session), 2026-11-04
- Pull request: #343
- Section 5 item: Blob tool in Vision Studio: teach the outline from a clicked spot
- Components/files: proto/visionstudio/remote/v1/runtime.proto (TryJobRequest.teach_spot, Graphic.tool), dotnet/Runtime.Host (JobDesigner, ToolTeach, new BlobMarks), dotnet/Studio (DesignerBlob, DesignerAreas, DesignerViewModel, DesignerView, GrpcRuntimeClient, IJobDesignClient, RuntimeModels, en.json), tests, requirements 17.12 BLB-OUT-07, BLB-OUT-08
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #343: "Teach a spot" in the Blob panel teaches the outline of the spot clicked on the picture through TryJob's new teach_spot; a try marks each spot's deepest dent and largest deviation with a cross, red over its limit) Blob tool in Vision Studio: teach the outline from a clicked spot (needs a spot index on TryJob's teach), and draw the dent and deviation marks on the picture

## Timeline (section 3)
| 2026-11-04 | Avalonia Vision Studio, Blob tool: "Teach a spot" toggle in the panel, the next tap on one of the selected tool's numbered outlines teaches that spot (one undo step, as Teach); a tap beside every spot teaches nothing and says so. Contract: `TryJobRequest.teach_spot` (1 = first, 0 = first as before; a spot not there is refused, never another taught) and `Graphic.tool` on TryJob graphics. Runtime: a try draws a cross at each spot's deepest dent and largest deviation, labelled in the tool's unit, NG over its limit, in place of the rule's diamond; the line's graphics unchanged. Studio.Tests 378, RuntimeContract.Tests 19, Runtime.Host.Tests Blob classes 12 (7 new) pass | 17.12 BLB-OUT-07, BLB-OUT-08 |

## Decisions (section 4)
| Topic | Decision and reason | requirement sections |
| --- | --- | --- |
| The spot travels as its number on the picture | Studio sends the number the runtime drew on the spot (`teach_spot`, 1-based, 0 = first so older requests keep their meaning) and the runtime teaches the same counted spot in the same sort order; a number past the spots found is refused with how many were found, so a stale click never teaches another spot | 17.12 BLB-OUT-07; line safety |
| Try graphics name their tool | Studio finds the clicked spot only among the selected tool's numbered outlines, so `Graphic.tool` is added to the contract (TryJob only, empty elsewhere); from an older runtime no tool is named and no spot is guessed | 17.12 BLB-OUT-07 |
| Marks are designer advice, in the runtime | The runtime knows each spot's dent and deviation points and the tool's unit and calibration, so the designer's TryJob builds the crosses (BlobMarks); the line's result graphics (VisionCore BlobTool) are not changed, and no verdict depends on a mark | 17.12 BLB-OUT-08 |

## Update log (section 8)
| 2026-11-04 | Requirements 17.12 BLB-OUT-07, BLB-OUT-08; runtime.proto: TryJobRequest.teach_spot, Graphic.tool | Claude (Mac, branch claude/studio-blob-teach-marks-ebc6eb) |

## Pending
- Blob tool in Vision Studio: the 14 other languages for design.blob.teach_spot, teach_spot_tip, pick_spot, try_first, no_spot_here and the longer outline_hint
