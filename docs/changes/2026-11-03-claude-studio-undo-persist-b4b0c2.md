# Vision Studio undo history kept across a restart of the app for the draft (UI-UX-SPEC 5 "survives a UI restart")

- Status: done
- Session: studio undo persist, 2026-11-03
- Pull request: #321
- Section 5 item: Vision Studio undo history kept across a restart
- Components/files: dotnet/Studio (new ViewModels/DraftHistoryStore.cs; DraftHistory.cs, DesignerUndo.cs, DesignerViewModel.cs, one line in DesignerWorkspace.cs), dotnet/Studio.Tests (new DraftHistoryKeptTests.cs), docs/REQUIREMENTS-VISION-SYSTEM.md (AS-18)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #321: the draft with its undo and redo steps and the "!" of a dropped tool that found nothing is kept per job in one DPAPI-protected, compressed file in the user's local application data, named by a hash of the job name, written at most once a second; opening the job again brings it back still unsaved; dropped on Save, Save as, Reload, Close, another job opened, or when the job changed outside Vision Studio; Req 16 AS-18) Vision Studio undo history kept across a restart of the app for the draft (UI-UX-SPEC 5 "survives a UI restart")

## Timeline (section 3)
| 2026-11-03 | Vision Studio job designer: undo history kept across a restart for the draft (one protected file per job, DPAPI this user on Windows, nothing kept where there is no protection); restored when the job is opened again, dropped on save, reload, close, another job, or a job changed outside Vision Studio; the "!" of an unsnapped drop survives and still stops Save; Studio.Tests 324 passed | 16 AS-18 |

## Decisions (section 4)
| Undo history kept across a restart | One file per job (not one for all) so a restart with another job open on the line keeps the first job's draft until it is opened; the file holds the draft, its steps and the unsnapped-drop areas, compressed (Brotli) and protected with DPAPI for this user (a draft holds taught settings: AGENTS.md IP rule 3); the file name is a hash of the job name. Where there is no platform protection (macOS, Linux) nothing is written, as for the commissioning secrets (MRG-09). Written at most once a second from the main window's watch tick, so typing does not write per key; at most the last second is lost on a crash. | 16 AS-18 |
| How a kept draft is matched | Before any Save here, by the SHA-256 of the job file as opened (strict: any change outside drops it). After a Save here, by the version that Save gave: the runtime writes the saved file its own way (stamps, its own serializer), so Studio's copy never hashes like it; a save anywhere else gives a new version. A mismatch opens the job as saved, never a draft on top of another file. | 16 AS-18 |
| Save drops the kept file | Nothing unsaved is left to bring back, so the file goes at Save; the history in memory goes on (AS-18 keeps it across a Save) and is kept again from the next step. | 16 AS-18 |

## Update log (section 8)
| 2026-11-03 | Requirement AS-18 extended: the history survives a restart of Vision Studio for the draft, with when it is dropped | Claude (Gaurang's MacBook Air, branch claude/studio-undo-persist-b4b0c2) |

## Pending
- Vision Studio: a New job never saved is not kept across a restart (it is not in the runtime's job list to open again); keep it too if wanted, with a way to reopen it
- Vision Studio: try the kept undo history once on a Windows PC (DPAPI): edit a job, quit Vision Studio, start it again, undo and redo; a copy of the file for another Windows user cannot be opened
- Vision Studio: undo of Accept after a restart: the accepted suggestions list is not kept, so an undone accepted tool does not come back as a suggestion after a restart (Find again offers it)
