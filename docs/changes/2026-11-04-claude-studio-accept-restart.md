# Vision Studio: undo of Accept after a restart brings the tool back as a suggestion

- Status: done
- Session: studio-accept-restart, 2026-11-04
- Pull request: #332
- Section 5 item: Vision Studio: undo of Accept after a restart
- Components/files: dotnet/Studio (ViewModels/DraftHistoryStore.cs, DesignerUndo.cs, DesignerFind.cs, three lines of DesignerViewModel.OpenAsync), dotnet/Studio.Tests (DraftHistoryKeptTests.cs), docs/REQUIREMENTS-VISION-SYSTEM.md (AS-18)
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #332: the accepted auto find suggestions are kept in the draft's protected file with the undo steps, with the camera and picture they were found on; opening the job shows that camera and picture, an undo of Accept offers the suggestion again and a redo takes it off, with no Find; dropped with the file under PR #321's rules, and forgotten when their picture is gone; Req 16 AS-18) Vision Studio: undo of Accept after a restart: the accepted suggestions list is not kept, so an undone accepted tool does not come back as a suggestion after a restart (Find again offers it)
## Timeline (section 3)
| 2026-11-04 | Vision Studio job designer: the accepted auto find suggestions are kept with the undo history across a restart (same protected file, same drop rules), so undo of Accept offers the suggestion again after a restart; Studio.Tests 5 new tests | 16 AS-18 |

## Decisions (section 4)
| Accepted suggestions after a restart | Kept inside the draft's one protected file (KeptDraft.Accepted, with the picture they were found on), not a file of their own: they are dropped exactly when the draft is, and get its DPAPI protection (they hold what was read and the tool's settings). Suggestions belong to the picture they were found on (a change of picture forgets them), so opening the job shows their camera and picture again; with that picture gone they are forgotten and the draft still comes back. Suggestions never accepted are not kept: Find offers them again. A file kept before this change opens as before. | 16 AS-18 |

## Update log (section 8)
| 2026-11-04 | Requirement AS-18 extended: the accepted auto-find suggestions are kept with the history across a restart | Claude (Gaurang's MacBook Air, branch claude/studio-accept-restart) |
