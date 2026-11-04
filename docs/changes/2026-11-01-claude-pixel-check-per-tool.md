# Pixel check per tool (AS-14)

- Status: done
- Session: pixel-check-per-tool (jovial-nobel-d6479a), 2026-11-01
- Pull request: #217
- Section 5 item: Pixel check per tool (AS-14)
- Components/files: VisionCore (`Tools/PixelCheck.cs`, `ToolConfig.PixelsAccepted`, `CameraConfig.LensFocalMm`, `CodeTool.MeasureSize`, `JobRunner.MeasurePixels`), runtime contract (`runtime.proto`: `ToolResult.pixels`, `PixelCheck`, `PixelItem`), Runtime.Host (`JobDesigner`: Try sends the check, Save records the acceptance), Vision Studio Designer (`DesignerPixels.cs`, job tree, tool result panel, camera lens row, overlay labels), English strings, JOB-FILE-FORMAT.md, TOOL-SPECIFICATIONS.md, tests
- Depends on: none

## Section 5
- [x] (done 2026-11-01, PR #217: each Read code and text tool states its need and is checked during job setup with the factor and three remedies; "too few pixels" in the job tree until "Accept as is", recorded with the user and time) Pixel check per tool (AS-14)

## Timeline (section 3)
| 2026-11-01 | Pixel check per tool (AS-14): each Read code and text tool states the smallest detail it needs (2D 2.5 px per module, unsure below 1.8; 1D, PDF417 and Pharmacode 2.0 px per narrow bar, unsure below 1.5; text 20 px capital height, unsure below 12). During Try the runtime measures every code's module or narrow bar (the engine's quality measure, on only for setup) and every text line's capital height, and sends each tool's worst item with the factor needed and the remedies: the camera k times nearer, a lens of k times the focal length (in mm when the camera's lens focal length is set), or a camera with k² the pixels. Vision Studio shows each size in its label ("QR 2.1 px/module", "text 14 px high"), a yellow outline below the need, the advice in the tool result panel and the messages, and "too few pixels" in the job tree until an engineer presses "Accept as is"; the runtime records the signed-in user and its own time on save. Saving gives the open picture and pixel advice again (Messages and the save message) and saves. Advice only: a tool's result is never changed | 16 AS-14, 12; UI-UX-SPEC 4.19 |

## Decisions (section 4)
| Pixel needs not in the specifications | Proposed and built: 1D (and PDF417, Pharmacode) 2.0 px per narrow bar, unsure below 1.5; text tools 20 px capital height, unsure below 12 (the rendered font already refuses below 10). The 2D value (2.5, unsure below 1.8) is the requirement's. Common reader guidance puts 1D at 1.5-2 px per element and OCR at about 20 px per character; to be confirmed on the owner's line pictures | 16 AS-14 |
| Measuring sizes in setup only | The engine measures module size only with its quality measure on. Try switches it on for every Read code tool (`JobRunner.MeasurePixels`, about 1 ms a code) and judges the tool exactly as without it (grades count only when the tool grades that part). The line never measures, so cycle times do not change | 16 AS-14, 12 |
| Who accepted is the runtime's | "Accept as is" is written into the draft by Vision Studio (shown at once with the signed-in engineer), but on Save the runtime records the signed-in user and its own time for a new acceptance and keeps an unchanged one as stored, so a client cannot rewrite who accepted or when. An acceptance holds while the size found is at least 95 % of the size accepted; a clearly smaller size asks again | 16 AS-14 |
| Rounding of the remedies | The factor is need over found rounded up to 0.1 (never short); the pixels factor is that factor squared, rounded up to 0.1; the lens is the focal length times the factor, rounded up to a whole mm. Spec example: 2.1 px against 2.5 gives 1.2×, 30 mm instead of 25 mm, 1.5× the pixels | 16 AS-14 |
| When setup checks run (owner, 2026-11-01) | "lens, pixel and other checks to be done while starting job. in running job we can not disturb"; "advice only when tools are selected on image and before saving the job to run"; "each job load requires to capture 1-2 images before run". The picture and pixel checks advise when a tool is placed on the picture (a dropped or accepted tool is tried at once), again before a save (the open advice goes to Messages and the save message; the save goes ahead), and at job load before Run (one or two pictures captured; built separately). Never in a running batch, and never stopping a save, a Run or deciding a part. A tool area drawn by hand is not tried by itself: a new try drops the full-resolution picture the engineer is zoomed into (AS-17), so its advice comes with Try (F5) or the save | 16 AS-13, AS-14, 12 |
| Lens focal length | No lens setting existed. `Cameras[].LensFocalMm` (Camera properties, "Lens focal length (mm)", All settings) is added for the advice; without it the lens remedy is a factor of the focal length. The autofocus lens (AS-16) is separate and still waits for the owner's lens models | 8 Lenses, 16 AS-14 |

## Update log (section 8)
| 2026-11-01 | JOB-FILE-FORMAT.md section 7: `Cameras[].LensFocalMm` and `Tools[].PixelsAccepted`; TOOL-SPECIFICATIONS.md: pixel check lines for the Read code tool and the text tools with the needs; REQUIREMENTS-VISION-SYSTEM.md section 16 after AS-14: "When the picture and pixel checks run" (the owner's three statements of 2026-11-01) | Claude (Mac, branch claude/pixel-check-per-tool) |

## Pending
- Pixel needs of the 1D and text tools (2.0 px per narrow bar, 20 px capital height) confirmed on the owner's line pictures (PR #217 proposed them)
- Setup checks at job load: one or two pictures captured per camera when a job is loaded on the line PC, before Run, with the picture check (AS-13) and pixel check (AS-14) advice shown; advice only, never in a running batch (requirements 16, "When the picture and pixel checks run")
