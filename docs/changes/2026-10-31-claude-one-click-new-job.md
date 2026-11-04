# One-click new job: owner's setup flow recorded; runtime calls to grab a picture into the job and auto-find on it; Grab and "Find everything" with approve in the Engineer workspace

- Status: done
- Session: one-click-new-job, 2026-10-31
- Pull request: #194
- Section 5 item: new (also covers the open items "Job designer: grab a picture from the camera for teaching" and "\"Find everything\" on the Teach page and in the Avalonia Vision Studio")
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (17.5), docs/vision-system/UI-UX-SPEC.md (4.19), proto/visionstudio/remote/v1/runtime.proto (additive: GrabPicture, FindTools), dotnet/VisionCore (AutoFind area), dotnet/VisionStudio (Station.GrabOne), dotnet/Runtime.Host (JobDesigner, Services, Access comment), dotnet/Studio (runtime client, JobDraft.AddToolJson, DesignerFind.cs, DesignerViewModel, DesignerView, en.json), tests in VisionCore.Tests, Runtime.Host.Tests, Studio.Tests
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #194: the runtime's GrabPicture takes one picture from a job camera while the line is stopped and keeps it with the camera's stored pictures, making a pictures folder under the data folder when the camera has none) Job designer: grab a picture from the camera for teaching
- [x] (done 2026-10-31, PR #194: the runtime's FindTools runs auto find on a stored picture, whole or in a dragged window, and returns ready tools with the picture; the Engineer workspace has Grab, Find everything, Find in a window and a suggestions strip with Accept / Not this; accepted tools are added to the camera shown and tried at once) "Find everything" on the Teach page and in the Avalonia Vision Studio

## Timeline (section 3)
| 2026-10-31 | One-click new job, first part (owner's flow, requirements 17.5): Grab a picture from the camera, auto find on the whole picture or a dragged window, accept or leave out each suggested tool, accepted tools tried at once (runtime GrabPicture and FindTools, Engineer workspace) | 16 (AS-01, AS-12), 17.5, 12 |

## Decisions (section 4)
| Grab keeps the picture with the camera's stored pictures | A grabbed picture is written to the camera's stored-pictures folder (a camera with none gets `pictures/<job>/camera <n>` under the data folder), so Try, Try all and Find tools use it like any stored picture and it becomes part of the teaching set | 17.5, UI-UX-SPEC 4.6 |
| Grab only while the line is stopped | The station opens, fires and closes the camera under its own lock; while the line runs it has the cameras and the CPU, so Grab and Find tools are refused like Try | 12 |
| Suggestions only | FindTools returns tools; the runtime never adds one to a job. Vision Studio adds a tool only when the engineer accepts it, then tries it so its result is checked before Save | AS-12, 12 |
| A dragged window asks auto find when no tool is selected | With a tool selected a dragged window stays its area (as before); "Find in a window" or no selection makes the window an auto find request, matching the owner's "drag a window on image" | 17.5 |
| The picture comes with the find | TryJob refuses a camera with no tools, so FindTools also returns the picture (JPEG and scale) and a new job's first picture shows before it has any tool | 17.5 |

## Update log (section 8)
| 2026-10-31 | Requirements 17.5 (owner's job-setup flow and the learning rule), UI-UX-SPEC 4.19 Grab, Find everything, Find in a window and the suggestions strip | Claude (Mac, branch claude/one-click-new-job) |

## Pending
- Teach good parts in the Engineer workspace: grab 10 to 30 good pictures (bad optional) with a check of each picture (blurred, too dark or bright, part cut off), learn every tool's limits from them and a strictness slider (UI-UX-SPEC.md 4.6 step 4, requirements 17.5).
- New job wizard joining Camera, Grab, What to check, Teach and Save and Start (UI-UX-SPEC.md 4.6), on top of GrabPicture and FindTools.
- Learning during the first parts of a run: only from parts that passed, only tightening on its own; a loosening change waits for a named approval or e-signature (requirements 17.5, PT11-30).
- Grab on a real Hikrobot camera on the Windows line PC (only the simulator camera was used).
