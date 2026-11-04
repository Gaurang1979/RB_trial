# Job setup: picture check per corner (blur, light), pixel check per tool, several areas with an "Areas and values" side panel (drag and drop too), autofocus lens option, full-size picture with zoom and pan (owner, 2026-10-31)

- Status: done
- Session: fervent-napier-908912, 2026-10-31
- Pull request: #211
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (section 8 "Lenses", section 16 AS-13 to AS-17), docs/vision-system/UI-UX-SPEC.md (4.19 rows, section 5 "Values after a drop")
- Depends on: none (PR #194 also edits both files; no conflict, either may merge first)

## Section 5
- New item: `[x] (done 2026-10-31, PR #211: requirements AS-13 to AS-17 and the Engineer workspace design written; each part is built in its own pull request) Owner's job setup requirements of 2026-10-31 recorded: picture check per corner, pixel check, areas and values panel, autofocus lens, full-size zoom and pan`

## Timeline (section 3)
| 2026-10-31 | Owner's five job setup requirements written into the requirements (section 8 "Lenses", section 16 AS-13 to AS-17) and the Engineer workspace design (UI-UX-SPEC 4.19, section 5): picture check per corner and centre with advice (focus, clean the lens, light), pixel check per tool with the factor needed (nearer, longer lens, more pixels), several areas with an "Areas and values" side panel that also lists dropped tools, autofocus lens beside fixed C/CS-mount lenses, full-size picture with zoom, slider, pan and overview | 8, 16, UI-UX-SPEC 4.19, 5 |

## Decisions (section 4)
| Setup checks only advise | The picture check (AS-13) and pixel check (AS-14) never change a tool or pass a part. On the line a picture worse than the taught ones makes no part OK; the tools decide and the drift monitor warns. A tool below its pixel need shows "too few pixels" until a named engineer accepts it. | 12, 16 AS-12 to AS-14 |
| One panel for every area | The "Areas and values" panel lists each tool line however the area was made: dropped from the toolbox, drawn, or accepted from auto find (owner: "this is required for drag n drop also"). Suggestions not yet accepted show greyed with Accept / Not this. | 16 AS-05, AS-15 |
| Autofocus lens never moves during a batch | Focus is set when the job loads, at Auto calibrate or on request, and saved with the product's camera settings; a lens that does not report reaching its position puts the camera in doubt, so parts are NG (section 12). No lens is integrated before the owner names the models. | 8, 12, 16 AS-16 |
| Zoom shows real pixels | Above Fit, the designer fetches the full-resolution picture without loss (GetImage, PNG) instead of enlarging the reduced JPEG, so clarity is judged on camera pixels. | 16 AS-17 |

## Update log (section 8)
| 2026-10-31 | REQUIREMENTS-VISION-SYSTEM.md: section 8 "Lenses: fixed or autofocus"; section 16 "Picture, optics and areas during job setup" AS-13 to AS-17. UI-UX-SPEC.md 4.19: zoom and pan, Picture check, Pixel check, "Areas and values" panel, Camera > Lens rows; section 5: "Values after a drop" | Claude (Mac, branch claude/fervent-napier-908912) |

## Pending
- Picture check per corner and centre (AS-13): sharpness and brightness of the four corners and the centre on each grabbed and taught picture, with advice (focus, tilt, clean the lens, aperture, light), live while the lens is turned (UI-UX-SPEC 4.19 "Picture check")
- Pixel check per tool (AS-14): pixels per module, per narrow bar and letter height of what was found against each tool's need, with the factor needed (nearer, longer focal length, more pixels) and "Accept as is" (UI-UX-SPEC 4.19 "Pixel check")
- Areas and values side panel (AS-15): collapsible right-dock panel with one row per tool line (area, found value, suggested format, Use this format), for dropped, drawn and auto-found areas; after PR #194 and PR #195 merge
- Full-size picture zoom and pan (AS-17): 100 % button, zoom slider, scroll bars, overview box, one-finger pan, full-resolution picture fetched above Fit (UI-UX-SPEC 4.19 "Centre, zoom and pan")
- Autofocus lens (AS-16): the owner to give the lens models first; then focus control through the camera (Hikrobot SDK / GenICam) or the lens controller, Auto focus on the job's areas, focus saved per product
