# Vision Studio: drag tools from the toolbox onto the picture, snapping to the nearest code or text (AS-05)

- Status: done
- Session: toolbox drag and snap, 2026-10-31
- Pull request: #195
- Section 5 item: new
- Components/files: dotnet/Studio (new ViewModels/DesignerDrop.cs and Controls/ToolDrag.cs; small edits to Controls/AreaDrag.cs, ViewModels/DesignerWorkspace.cs MarkTree, Views/DesignerView.axaml toolbox tab and .axaml.cs), dotnet/Studio/i18n/en.json, dotnet/Studio.Tests (new ToolDropTests.cs, a TryWith hook in Doubles.cs)
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #195: tiles drag onto the picture with the tool's area dashed under the pointer; the area starts centred on the drop point in full-image pixels, also when zoomed, and is taught there by the runtime's Teach: Read code snaps to the code nearest the drop point, Read text and Check text to the block of text there; the taught tool is tried at once and its area flashes; where nothing is found the area stays where it was dropped, the tool shows "!" with Teach's reason and a hint, is tried there, and Save waits until its area is drawn or it is taught; tap-to-add stays; Delete removes the selected tool) Vision Studio: drag tools from the toolbox onto the picture, snapping to the nearest code or text (AS-05, UI-UX-SPEC.md 5 "Drop on image")

## Timeline (section 3)
| 2026-10-31 | Vision Studio Engineer workspace: drag-and-drop from the toolbox onto the picture with snapping by Teach (nearest code, text block), immediate try, "!" where nothing is found, Delete removes the selected tool; view-model tests and a headless drag at 1366 x 768 with the picture zoomed | 16 (AS-02, AS-05, AS-12) |

## Decisions (section 4)
| Snapping uses the existing Teach | A dropped tool is snapped with the runtime's Teach inside TryJob (the Teach button's call), so no new runtime call was needed while PR #194 changes the contract. Teach looks first in the starting area about the drop point (a square a quarter of the picture's short side for a code; a third of the width by a sixth of the height for text), then, when nothing is there, in twice that area, instead of UI-UX-SPEC 5's "within 80 px": a code or a print block is often larger than 80 px, and Teach reads only what lies inside its area. Looking near first keeps a code of another type close by from deciding the type. | 16 (AS-05) |
| Nearest code, one code | When the area holds several codes, the tool is taught again on the one nearest the drop point alone (its GS1 rules cleared first, so it gets that code's own), so it reads exactly one code (count 1), as UI-UX-SPEC 5 asks. A tool for all codes of a case is still made by drawing the area round them and pressing Teach. | 16 (AS-05, AS-12) |
| Text cut at the edge | A dropped text tool is taught again in the area Teach gave it while that area reaches past where it looked (a line cut at the search edge), three teaches at most. | 16 (AS-05) |
| Nothing found is not OK | Where nothing is found, the area stays where the tool was dropped, the tree shows "!" (until an area is drawn or the tool is taught), the message gives Teach's reason (a missing reading model too), and the tool is tried there. Save and Save as wait while a tool shows "!": its area is a guess, and a Check text tool without lines passes any clean print today (the review found it), so it must not reach the line that way. | 12, 16 (AS-12) |
| Drag without system drag and drop | The tile keeps the pointer while pressed and follows it itself, so a finger works as a mouse does and the drag runs in headless tests. A press released without moving stays the tile's click (tap-to-add, also for the keyboard). | UI-UX-SPEC 5 |
| Delete before undo | The workspace has no undo yet, so Delete (outside a text box) removes the selected tool, as Remove tool in the Edit menu does. | UI-UX-SPEC 5 |

## Update log (section 8)
| 2026-10-31 | docs/changes note for toolbox drag and drop with snapping (AS-05) | Claude (Gaurang's MacBook Air, branch claude/toolbox-drag-snap) |

## Pending
- VisionCore: a Check text tool with no lines must be refused like a Read text tool without a rule (`TextTool.SetupProblem`); today it passes any part whose print reads cleanly (found in the PR #195 review; tap-to-add makes such a tool too)
- Vision Studio drop snapping: switch from Teach to the auto-find call of PR #194 (`FindToolsAsync` with an area) once it is merged, so the nearest text block is chosen from every block found, not from Teach's best-read block
- Vision Studio undo and redo (UI-UX-SPEC 5): every create, move, resize, setting change and delete one step; then Ctrl+Z takes a dropped tool back
- Vision Studio "Draw instead of drop" (UI-UX-SPEC 5): after tap-to-add, the area drawn snaps to the features inside it
