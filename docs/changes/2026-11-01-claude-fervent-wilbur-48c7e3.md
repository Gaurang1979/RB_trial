# Areas and values side panel (AS-15)

- Status: done
- Session: claude/fervent-wilbur-48c7e3, 2026-11-01
- Pull request: #214
- Section 5 item: Areas and values side panel (AS-15)
- Components/files: Vision Studio (`dotnet/Studio`): new `ViewModels/AreasPanel.cs`, `ViewModels/DesignerAreas.cs`, `Views/AreasPanelView.axaml`; small hooks in `DesignerViewModel.cs`, `DesignerWorkspace.cs`, `DesignerView.axaml(.cs)`, `MainViewModel.cs`, `MainWindow.axaml`, `Runtime/*` mapping, `i18n/en.json`. Runtime contract: `proto/.../runtime.proto` (TextRead `format` 7, `field` 8; CodeRead `field` 8, `tool` 9). Runtime.Host `JobDesigner.cs`. VisionCore `CodeTool.cs` (`CodeTeach.Gs1Data`). `docs/vision-system/UI-UX-SPEC.md` 4.19 row.
- Depends on: #194 (suggestions strip: its suggestions become the greyed rows), #216 (full picture: area maths use its ShownScale); #195 merged

## Section 5
- [x] (done 2026-11-01, PR #214: right-dock panel with one row per tool line of the camera shown, area numbers on the picture, selection both ways, suggested formats from the runtime, Use this format into the draft only, Ctrl+Shift+A, remembered per user) Areas and values side panel (AS-15): collapsible right-dock panel with one row per tool line (area, found value, suggested format, Use this format), for dropped, drawn and auto-found areas

## Timeline (section 3)
| 2026-11-01 | Vision Studio "Areas and values" panel (AS-15): every tool of the camera shown is a numbered area on the picture and has one row per line in the right dock (found value, suggested format with field type or a code's GS1 elements, a box to change it, Use this format); failing and empty rows first in red; auto find's suggestions greyed with Accept / Not this; row and picture select each other; rows follow each Try; folds with the chevron or Ctrl+Shift+A and is remembered. TryJob now returns each text line's suggested format and field type and each code's tool and GS1 elements | 16 AS-15, AS-05, UI-UX-SPEC 4.19, 5 |

## Decisions (section 4)
| Suggested formats come from the runtime | The try reply carries each text line's format as Teach would write it (`TextFormat.Propose` for Read text, the text itself for Check text) and its field type as auto find names it (`AutoFind.LineField`), and a GS1 code's elements (`CodeTeach.Gs1Data`, the same rule Teach uses); the Studio does not copy that logic. A Check text line keeps the template the engineer wrote: its suggestion is offered only while the template is empty. | 16 AS-15, AS-01 |
| Panel at the top of the right dock | Beside the picture the panel left the picture under 600 px wide at 1366 × 768; at the top of the right dock, above Properties, it folds to its header. Accepting a format changes the job draft only, never the running job, and nothing in the panel passes a part. | 16 AS-15, AS-12, UI-UX-SPEC 4.19 |

## Update log (section 8)
| 2026-11-01 | UI-UX-SPEC.md 4.19 "Right dock, Areas and values": the panel sits at the top of the right dock and folds to its header | Claude (Mac, branch claude/fervent-wilbur-48c7e3) |
| 2026-11-03 | Merged origin/main (116 commits) into the branch. Conflicts in `JobDesigner.cs`, `GrpcRuntimeClient.cs`, `DesignerViewModel.cs`, `DesignerView.axaml.cs` resolved keeping both main's changes (picture check AS-13, pixel check AS-14, grab/find tools, full-picture zoom AS-17) and the AS-15 feature. `GrpcRuntimeClient.Map(TryJobResponse)` now carries main's ToolTried.Pixels/ImageId/PictureCheck together with AS-15's code corners/GS1/field/tool and text line format/field. `FullPictureZoomTests` updated: the selected tool's area label is numbered ("1 · Area", AS-15 AreaLabel). | Claude (Mac, branch claude/fervent-wilbur-48c7e3) |
| 2026-11-03 | The two pieces that were waiting on #194 and #216, both now merged: auto find's suggestion list **is** the panel's greyed rows (`PendingAreas()` maps each `SuggestionItem`, and Accept / Not this call its own `AcceptSuggestion` / `DismissSuggestion`, not a second list), the suggestions are numbered after the camera's tools on the picture, and `DesignerAreas.cs` takes `ShownScale` in place of the try's JPEG scale for the area numbers, the tap and its 8 px threshold, so both stay registered above Fit. | Claude (Mac, branch claude/fervent-wilbur-48c7e3) |

## Verified (merge-up, 2026-11-03)
- `dotnet build` Studio and Runtime.Host: succeeded, 0 errors (Studio↔Runtime gRPC contract compiles against the merged proto).
- `dotnet test` Studio.Tests: 256 passed, 0 failed. Runtime.Host.Tests: 68 passed, 0 failed (OneClickJobTests exercise the merged GrabPicture/FindTools).
- Needed a local rebuild of the native `codereader` and `vs_ocr` libraries (the worktree's prebuilt dylibs predated main's `cr_load_model_buffer` / `vs_ocr_load_model_buffer`); after rebuild all affected .NET tests pass. My merge changed no `engine/` code.
- Engine ctest and the `bench` heavy test not run: this merge changed no `engine/`, `tools/bench/` or `truth/` code, so the reading path is unchanged (bench self-skips).
- CI: not run (billing). PR kept a draft; mergeable into main.

## Verified (the two remaining pieces, 2026-11-03)
- `origin/main` merged before the work: already up to date at head `2dac9ca`, so #194, #216, #146, #220, #222 and #223 are all in the branch. No conflicts to resolve.
- `dotnet build` Studio and Runtime.Host: succeeded, 0 warnings, 0 errors.
- `dotnet test` Studio.Tests (whole project, Studio changed): **258 passed, 0 failed**, including the two new `AreasPanelTests`:
  - `Auto_finds_suggestions_are_the_greyed_rows_and_Accept_and_Not_this_are_its_own` - a camera with 2 tools, auto find offers 2 more: the greyed rows are areas 3 and 4 with the read text and field types, and the picture carries the same numbers; Accept on the QR row adds that tool through auto find's own accept (one list, the rest renumbered), Not this drops the other, leaving no greyed row and no outline.
  - `The_area_numbers_and_the_tap_follow_the_picture_shown_above_fit` - at Fit the other tool's number sits on its half-size JPEG area and a 3 px drag is still a tap while 5 px is not; above Fit, with the full picture swapped in (`ShownScale` 1), the same area is drawn on camera pixels (400, 100)-(1000, 300) and a tap at camera (420, 120) selects it, with the 8 px threshold now in camera pixels.
  - The existing `OneClickJobTests` numbering test still passes: a camera with no tools numbers its first suggestion "1", as before.
- `dotnet test` Runtime.Host.Tests (`TryFormat`, `JobDesigner`): 15 passed, 0 failed. VisionCore.Tests (`CodeGs1`, `CodeTeach`, `CodePharmacode`, `AutoFind`): 39 passed, 0 failed.
- Engine ctest and the `bench` heavy test: **not run, not needed** - nothing under `engine/`, `tools/bench/` or `truth/` changed in this work (Studio view models and their tests only), so the reading path is untouched and the bench self-skips.
- CI: not run (billing). The red "Windows build" check is the known billing halt (CLAUDE.md rule 11), not a failure of this change.
- `scripts/coord.sh overlap`: #213 (changeover verify) and #219 (setup checks at job load) touch `Runtime/GrpcRuntimeClient.cs`, `Runtime/RuntimeModels.cs`, `i18n/en.json` and `runtime.proto`. This change touches none of those files, so the overlap is in already-merged hunks only. **Merge order: this one may merge in any order with #213 and #219**; #194, #195 and #216, which it depended on, are merged.
- Nothing is left before ready: both pieces named in "Left before ready" are built and tested, and no hardware, device, real images or judged bench run is needed for either.
