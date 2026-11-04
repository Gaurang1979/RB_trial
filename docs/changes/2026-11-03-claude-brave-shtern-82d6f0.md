# Vision Studio workspace toolbar fits 1366 x 768 with Undo and Redo buttons; undo of Accept brings the suggestion back (AS-18)

- Status: done
- Session: studio toolbar undo, 2026-11-03
- Pull request: #298
- Section 5 item: new
- Components/files: dotnet/Studio (Views/DesignerView.axaml toolbar, ViewModels/DesignerFind.cs, DesignerUndo.cs), dotnet/Studio/i18n/en.json, dotnet/Studio.Tests, docs/REQUIREMENTS-VISION-SYSTEM.md (AS-18)
- Depends on: none (#285 merged)

## Section 5
- [x] (done 2026-11-03, PR #298: the Vision Studio workspace toolbar fits 1366 x 768: Save, name and PLC number are placed first and the left buttons have short labels (Run, Try, Try all, Teach, Find all, Window, Check) with the keys in their tooltips, the duplicate Save icon is gone; Undo and Redo toolbar buttons of UI-UX-SPEC 4.19 follow the history; an accepted auto-find suggestion that is undone is offered again, a redo takes it off) Vision Studio toolbar fit and Undo/Redo buttons, undo of Accept (AS-18)

## Timeline (section 3)
| 2026-11-03 | Vision Studio workspace toolbar fits 1366 x 768 with Undo and Redo buttons; undo of Accept brings the suggestion back; layout test and two view-model tests | 16 (AS-18, AS-01) |

## Decisions (section 4)
| Toolbar fit by priority | Name, PLC number and Save are docked first so they are never covered; the left buttons are shortened (labels in `ws.*_short`, full text and the key in the tooltip) and the key chips are dropped, since Edit/View menus and tooltips carry them. | 16 (AS-18), UI-UX-SPEC 4.19 |
| Undo of Accept | The designer remembers which tool came from which suggestion; an undo that removes that tool puts the suggestion back on the list, a redo takes it off. Forgotten when auto find runs again or the list is dismissed. The undo never adds a tool (AS-12). | 16 (AS-18, AS-01) |

## Update log (section 8)
| 2026-11-03 | AS-18 states the toolbar buttons, the 1366 x 768 fit and undo of Accept | Claude (Gaurang's MacBook Air, branch claude/brave-shtern-82d6f0) |

## Verified
- `dotnet test` of dotnet/Studio.Tests (the only changed project's tests; nothing else references Studio): 305 passed, 0 failed, 2 new view-model tests (undo of Accept, hand-added tool) and 1 headless 1366 x 768 toolbar test (left buttons end before the name box, Save inside the screen, Undo/Redo follow the history).
- Engine, bench: not affected, not run. CI: not run (billing).

## Pending
- Vision Studio undo history kept across a restart of the app for the draft (UI-UX-SPEC 5 "survives a UI restart")
