# Vision Studio undo and redo in the job designer (AS-18, UI-UX-SPEC 5)

- Status: done
- Session: studio undo redo, 2026-11-03
- Pull request: #285
- Section 5 item: new
- Components/files: dotnet/Studio (new ViewModels/DraftHistory.cs and ViewModels/DesignerUndo.cs; small edits to DesignerViewModel.cs, DesignerWorkspace.cs, DesignerDrop.cs, DesignerFind.cs, Shared.cs, Theme/Icons.axaml, Views/DesignerView.axaml Edit menu and .axaml.cs keys), dotnet/Studio/i18n/en.json, dotnet/Studio.Tests (new UndoRedoTests.cs), docs/REQUIREMENTS-VISION-SYSTEM.md (AS-18)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #285: every create, move, resize, setting change and delete of the draft is one undo step; a dropped tool with its snapping and teaching, a Teach and Accept all are one step each, so one Ctrl+Z takes a dropped tool back whole; typing into one setting is one step; Ctrl+Z undoes, Ctrl+Y and Ctrl+Shift+Z redo, also in the Edit menu; a text box keeps its own Ctrl+Z; Remove tool says "Undo (Ctrl+Z) puts it back"; history of the job open, last 1000 steps, kept across Save, never undoing the saved version; a dropped tool that found nothing still shows "!" and stops Save after a redo) Vision Studio undo and redo (AS-18, UI-UX-SPEC.md 5 "Undo / Redo")

## Timeline (section 3)
| 2026-11-03 | Vision Studio job designer: undo and redo of every draft change (create, move, resize, setting, delete, drop, Teach, Accept all) with Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z and the Edit menu; history tests, view-model tests and a headless key test | 16 (AS-18, AS-05, AS-12) |

## Decisions (section 4)
| Undo by snapshots of the draft | Each step is the draft's JSON after it (without the saved version number), not a command object per edit: the property table changes the draft through bindings without any command, and a snapshot catches those edits (seen by Watch, or recorded at once before the next command or undo) with the commands' edits in one way. A job's JSON is a few kilobytes, so 1000 steps fit easily; the spec's "unlimited" is held at 1000 so a long session cannot grow without end. | 16 (AS-18), UI-UX-SPEC 5 |
| Typing is one step | A typed change that alters only the same single value as the last typed step, within 2 s of it, replaces that step: typing a name is one step, not one per letter, while two settings changed one after the other stay two steps. Commands (add, delete, area drawn) never merge. | UI-UX-SPEC 5 |
| A drop is one step | The drop, with its snapping teaches and the restore of an earlier teach, is recorded once when it ends, so Ctrl+Z takes the dropped tool back whole, as asked in PR #195's Pending. Accept all is one step too. Undo waits while the designer is busy. | 16 (AS-05, AS-18) |
| Save is not undone | The saved version number is kept out of the steps: undoing past a Save makes the draft "unsaved" again, and redoing to the saved state makes it saved again. Opening, making or saving as another job starts its own history. | 16 (AS-18) |
| Safety kept | Undo and redo only change the draft. A dropped tool that found nothing keeps its "!" after undo and redo (its area is compared as before), so Save still waits (AS-12). | 12, 16 (AS-12) |
| Edit menu, no toolbar buttons yet | Undo and Redo are in the Edit menu with Ctrl+Z / Ctrl+Y. The workspace toolbar at 1366 x 768 is already full on main (its left buttons reach under the job name box), and two more buttons push Save off the screen (the layout test caught it), so the toolbar buttons wait for the toolbar to be made to fit. | UI-UX-SPEC 4.19, 5 |

## Update log (section 8)
| 2026-11-03 | Requirement AS-18 (undo and redo in the job designer) added to section 16; docs/changes note for it | Claude (Gaurang's MacBook Air, branch claude/studio-undo-redo) |

## Pending
- Vision Studio workspace toolbar at 1366 x 768: the left-docked buttons reach under the job name, PLC number and Save (found in PR #285); make it fit (shorter labels or an overflow), then add the Undo and Redo toolbar buttons of UI-UX-SPEC 4.19
- Vision Studio undo history kept across a restart of the app for the draft (UI-UX-SPEC 5 "survives a UI restart")
- Vision Studio undo of Accept: an accepted auto find suggestion undone does not come back as a suggestion (Find again offers it)
