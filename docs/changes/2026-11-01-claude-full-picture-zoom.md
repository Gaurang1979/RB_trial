# Full-size picture zoom and pan in job setup (AS-17)

- Status: done
- Session: claude/full-picture-zoom (laughing-hopper), 2026-11-01
- Pull request: #216
- Section 5 item: Full-size picture zoom and pan (AS-17)
- Components/files: proto runtime.proto (TryJobResponse.image_id = 19); dotnet/Runtime.Host (JobDesigner.cs, Services.cs, LineRuntime.cs); dotnet/Studio (Controls/ZoomBox.cs, Controls/ZoomOverview.cs new, ViewModels/DesignerFullPicture.cs new, DesignerViewModel.cs, DesignerDrop.cs (drops in the shown pixels), DesignerWorkspace.cs, Shared.cs, Views/DesignerView.axaml and .cs, Runtime/IJobDesignClient.cs, GrpcRuntimeClient.cs, Theme/Icons.axaml, i18n/en.json); tests in Studio.Tests and Runtime.Host.Tests
- Depends on: none

## Section 5
- [x] (done 2026-11-01, PR #216: 100 % button with Ctrl+1 and Fit with Ctrl+0, a zoom slider from fit to 16 times, scroll bars, arrow keys, a Pan tool for the left button and one finger, two-finger pinch and touch-pad zoom, an overview box in the bottom-right corner that moves the view when dragged; above Fit the tried picture is fetched at full resolution without loss and swapped in, with "Loading full picture…" until it arrives and "full picture" in the zoom label; graphics, area drawing and the pixel readout stay on the same image pixels) Full-size picture zoom and pan (AS-17): 100 % button, zoom slider, scroll bars, overview box, one-finger pan, full-resolution picture fetched above Fit (UI-UX-SPEC 4.19 "Centre, zoom and pan")

## Timeline (section 3)
| 2026-11-01 | Vision Studio job setup: full-size picture with zoom (Fit to 16 times, 100 %, slider, pinch) and pan (drag, Pan tool, arrow keys, scroll bars, overview); the runtime returns an id for each tried picture and sends it at full resolution as PNG, which the designer swaps in above Fit | 16 AS-17 |

## Decisions (section 4)
| Full picture for a try | TryJob returns `image_id`; the runtime keeps the pictures tried lately (up to 96 MB, always the newest) and GetImage sends them as PNG or raw. The designer asks once per picture, only above Fit, and keeps the reduced JPEG when there is no id (older runtime), the picture is no longer kept, its size differs from the try's, or it cannot be read, and then says "Full picture not available" so clarity is not judged on enlarged JPEG pixels | 16 AS-17 |
| Coordinates after the swap | Once the full picture is shown, the picture, its graphics, the area drawing and the readout are all in camera pixels (shown scale 1); before, all in JPEG pixels. The graphics are converted from the try's JPEG pixels, so nothing moves on screen when the picture is swapped | 16 AS-17 |
| 100 % | One camera pixel per screen (device) pixel, display scaling included; within Fit to 16 times, so a picture smaller than the box stays at Fit. The zoom label is in the same camera pixels, so the button and the label agree; scroll bars and the overview lie over the picture so showing them never changes its size | 16 AS-17 |
| Tried pictures and the line | While the line runs the runtime sends and keeps no tried picture (encoding a full PNG takes the CPU a part needs). "Try all pictures" asks with `not_shown`, so its pictures are not kept and the one on screen stays available. A tried picture may be an engineer's upload, so GetImage of a `try-` id needs the same access as TryJob | 12, 16 AS-17 |

## Update log (section 8)
| 2026-11-01 | runtime.proto: TryJobResponse.image_id (19), TryJobRequest.not_shown (12); GetImage of a tried picture needs design access | Claude (Mac, branch claude/full-picture-zoom) |

## Pending
- Grab picture for a new job (PR #194): send an image id with the grabbed picture, so it zooms at full resolution before any tool is tried (AS-17)
