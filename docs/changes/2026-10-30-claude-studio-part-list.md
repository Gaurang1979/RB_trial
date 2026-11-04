# Vision Studio: every part's own result listed, at full line speed

- Status: done
- Session: studio-part-list (Claude, Mac), 2026-10-30
- Pull request: #151
- Section 5 item: new (owner, 2026-10-30: each part's own result listed, "as report is required")
- Components/files: dotnet/Studio (Runtime: StreamResults in the client and the link; ViewModels: PartLog, PartsViewModel; Views: PartsView, a Parts button on the Run screen; i18n/en.json; Icon.List), dotnet/Studio.Tests, docs/vision-system/UI-UX-SPEC.md 4.1
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #151: Parts list in Vision Studio from StreamResults, gaps shown, 2,000 kept, redrawn at most once a second) Vision Studio: every part's own result listed, at full line speed.

## Timeline (section 3)
| 2026-10-30 | Vision Studio Parts list: every part's result from the runtime's StreamResults, newest first, Bad only, Hold the list, gaps where parts are missing; Studio.Tests 126 passed, 8 of them new | 6.1 |

## Decisions (section 4)
| Parts list never drops the link | The results stream is read apart from the status and live streams: a runtime without StreamResults, or a results stream that ends, never marks the runtime as not connected; it is opened again every 2 s | 6.1, 12 |
| Parts list never looks complete when it is not | An id jump adds a row "N parts are not in this list", a lost link adds "Not connected"; every part stays in the runtime's results log for the reports | 6.1 |
| Parts on the Run action bar | A fifth button, "Parts", for every role (owner wants each part listed); the action bar stays at five | UI-UX-SPEC 4.1 |

## Update log (section 8)
| 2026-10-30 | UI-UX-SPEC 4.1: "Parts" control on the Run screen | Claude (Mac, branch claude/studio-part-list) |

## Pending
- Vision Studio Parts list: tap a row to open that part's image and checks (NG image viewer with a part from StreamResults).
- Reports in Vision Studio: per-part export from the results log (UI-UX-SPEC 4.11 "Find a part").
