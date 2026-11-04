# Engineer workspace: zoom, pan and pixel readout on the picture; search and Essential / All in the property table (UI-UX-SPEC.md 4.19)

- Status: done
- Session: Claude desktop (studio-workspace-tools), 2026-10-31
- Pull request: #190
- Section 5 item: Engineer workspace: zoom, pan and pixel readout
- Components/files: dotnet/Studio (Controls/ZoomBox.cs new, Views/DesignerView.axaml and .cs, ViewModels/DesignerWorkspace.cs, i18n/en.json), dotnet/Studio.Tests (WorkspaceToolsTests.cs new, LayoutTests.cs)
- Depends on: #188 (merged)

## Section 5
- [x] (done 2026-10-31, PR #190: zoom 100-1600 % with the mouse wheel about the pointer and with − / + / whole-picture buttons, the right or middle button drags the picture, graphics and area drawing zoom with it; pixel readout x, y in full-image pixels and grey value in the status bar; property table search and Essential / All, advanced settings marked; 182 Studio tests) Engineer workspace: zoom, pan and pixel readout (x, y, grey value) on the picture
- "Engineer workspace: search box and Essential / All switch": `[x] (done 2026-10-31, PR #190)`
- New: [ ] Engineer workspace: Last NG image tab per camera (needs the runtime's last bad part image per camera in the designer)
- New: [ ] Engineer workspace: result graphics keep their line width and text size when zoomed (today they grow with the zoom), and a graphics on/off switch (UI-UX-SPEC.md 4.19)
- New: [ ] Engineer workspace: the grey value from the full camera picture, not the smaller JPEG of the try (say which in the readout until then)

## Timeline (section 3)
| 2026-10-31 | Engineer workspace: zoom and pan on the picture with the graphics, pixel readout with grey value, property search and Essential / All | 6 |

## Decisions (section 4)
| Zoom and pan | The picture, its result graphics and the area drawing are scaled together, so an area drawn on a zoomed picture lands in the right pixels. Wheel zooms about the pointer, 100 % (whole picture) to 1600 %; the right or middle button drags, the left button keeps drawing areas; the picture never moves off its box | 6 |
| Essential settings | Essential: code type, codes expected, expected value, GS1, lowest grade; lowest quality and the expected lines of a text tool; tool name, area, camera name and pictures folder. Advanced (All only): length, check digit, GS1 AIs, grade every N, print type, print colour, letter height, camera serial. Engineers start on All | 6 |
