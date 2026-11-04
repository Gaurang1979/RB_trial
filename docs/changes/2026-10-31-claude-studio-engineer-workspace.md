# Vision Studio Engineer workspace (dense, VisionPro-style): job tree, toolbox, image tabs, property table, tool result, docked results, image set and cycle time (UI-UX-SPEC.md 4.19)

- Status: done
- Session: Claude desktop (studio-engineer-workspace), 2026-10-31
- Pull request: #188
- Section 5 item: Vision Studio Engineer workspace in Avalonia
- Components/files: dotnet/Studio (Views/DesignerView.axaml and .cs, ViewModels/DesignerWorkspace.cs new, DesignerViewModel.cs, MainViewModel.cs, Controls/Charts.cs, Controls/Ui.cs, Theme/Icons.axaml, ViewModels/Shared.cs, Views/MainWindow.axaml, i18n/en.json), dotnet/Studio.Tests (WorkspaceTests.cs new, LayoutTests.cs)
- Depends on: #147 and #183 (merged)

## Section 5
- [x] (done 2026-10-31, PR #188: the job designer of #147 becomes the Engineer workspace: menu bar (File, Edit, View, Run), toolbar with Try F5, Try all pictures F6, Teach F7, Save Ctrl+S, pictures Page Up / Page Down; job tree with each tool's ✓ / ✗ and time; toolbox; camera tabs over the tried picture; property table by category; tool result with the lines read and a margin chart of the tool's quality over the image set against its limit; docked Results, Lines read, Image set (thumbnails, a click tries that picture), Tool times (stacked bar) and Messages; status bar; panels resize with splitters and hide from the View menu; unsaved edits shown and asked about on Back to Run; 179 Studio tests) Vision Studio Engineer workspace in Avalonia: docking (Dock.Avalonia), property grid, job tree, image buffers, bottom docks, saved layout per user; it replaces the separate job designer page.

## Timeline (section 3)
| 2026-10-31 | Vision Studio Engineer workspace (UI-UX-SPEC.md 4.19) over the #147 job designer: menu bar, toolbar with shortcuts, job tree and toolbox, camera tabs, property table, tool result with a margin chart, docked results, image set, tool times and messages; checked headless at 1366 x 768 | 6 |

## Decisions (section 4)
| Docking without a new package | The workspace uses Avalonia's own grid and splitters: panels resize and hide from the View menu. Floating panels, moving a panel to another side and a saved layout per user need a docking package (Dock.Avalonia, MIT); that waits for the open-source dependency review | 6, 14 |
| One view-model for the designer and the workspace | The workspace (DesignerWorkspace.cs) only arranges and summarises; opening, teaching, trying and saving jobs stay in the #147 designer view-model, so the job logic and its tests are unchanged | 6 |
| Margin chart | After "Try all pictures" each text tool shows its print quality over the pictures against its lowest quality; "safe margin" when the worst picture is at least a tenth of the scale above the limit, else "close to the limit". Code tools get theirs when the runtime sends a code grade score per try | 6 |
| Unsaved changes | An edit in the property table changes the draft through its binding, with no command; the workspace compares the draft with the saved job about twice a second, shows "Unsaved changes", and Back to Run asks "Drop the changes since the last save?" first | 6 |
| Image set and margin belong to one draft and one camera | Any edit, Teach, another job or another camera clears them: a "safe margin" from other settings would mislead. A run stopped part way (not connected, refused) shows "Stopped after i of n" and no margin. A text tool that read nothing on a picture counts as not safe | 6 |
| Keys in the workspace | F5 tries (the Run screen's Start key never starts the line from the workspace), F6 tries all, F7 teaches, Ctrl+S saves, Page Up / Page Down change the picture. A key is taken only where it does something: on the Run screen Page Up and Page Down still scroll lists | 6, 12 |

## Update log (section 8)
| 2026-10-31 | "Job designer" renamed "Engineer workspace" on screen (top bar and title) | Claude (Mac, branch claude/studio-engineer-workspace) |

## Pending
- Engineer workspace: floating panels, moving panels to another side and the layout saved per user, with a docking package (Dock.Avalonia) after the open-source dependency review.
- Engineer workspace: search box and Essential / All switch in the property table (UI-UX-SPEC.md 4.19).
- Engineer workspace: zoom, pan and pixel readout (x, y, grey value) on the picture; Last NG image tab per camera.
- Engineer workspace: PLC I/O tab with the live signals (needs a runtime call for the PLC registers).
- Engineer workspace: each stored picture marked good or bad, so the image set shows expected against actual ("Caught 11 of 12 bad").
- Runtime: a code grade score per tool in TryJob, so code tools get the margin chart too.
- Engineer workspace: keep the margin when only the tool's pass limit is edited (redraw the limit line), instead of clearing the image set; mark the picture shown on the margin chart.
- Engineer workspace: the runtime link state in the status bar, and check marks on the View menu's panel items.
