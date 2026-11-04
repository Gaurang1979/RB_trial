# Vision Studio compact density and multi-panel Run screen with Overview charts (UI-UX-SPEC.md principles 2-4 and 15, section 4.1)

- Status: done
- Session: Claude desktop (studio-compact-run), 2026-10-31
- Pull request: #183
- Section 5 item: Vision Studio compact density
- Components/files: design/tokens/vision-tokens.json, dotnet/Studio (Theme, Views, ViewModels/Overview.cs, ViewModels/PartLog.cs, ViewModels/RunViewModel.cs, Controls/Charts.cs, i18n/en.json), dotnet/Studio.Tests
- Depends on: #173 (merged)

## Section 5
- [x] (done 2026-10-31, PR #183: tokens at one compact density, controls 32 px, toolbar actions 40 px with icon, label, shortcut and tooltip, text 13 px; Run screen with toolbar, banner, live image over Overview and Last bad parts tabs, result, counters, cycle time with a good % trend, this picture's tools and codes (failed first); Overview charts parts per minute, why parts were NG and cycle-time spread with p95; F5, Shift+F5, F2, F4, F1, never while a panel is open; sign-in, parts list, NG viewer and other panels compacted; 146 Studio tests) Vision Studio compact density: change design/tokens/vision-tokens.json sizes and fonts (touch 32, topBar 40, actionBar 40, body 13) and regenerate the Avalonia resources; Run screen to the multi-panel layout with the Overview charts; layout tests at 1366 x 768.

## Timeline (section 3)
| 2026-10-31 | Vision Studio at one compact density; multi-panel Run screen with Overview charts (parts per minute, why parts were NG, cycle-time spread), good % trend, last part's tools and keyboard shortcuts; checked headless at 1366 x 768 in dark, light and high contrast | 6, 7 |

## Decisions (section 4)
| Overview charts from the app | The charts are worked out in Vision Studio from the parts it already receives (StreamResults), so the runtime contract does not change. The parts list keeps the newest 2,000 parts, under ten minutes at 225 parts a minute, so it also keeps good and bad counts per minute for the last hour and bad counts per reason since the app started | 6 |
| Cycle budget line | Drawn only when the budget is known; the runtime does not send it yet, so today the chart shows the spread and the 95 % mark without the budget line | 6 |
| Shortcuts behind a panel | F5, Shift+F5, F2, F4 and F1 do nothing while a panel (sign-in, a question, NG viewer, product picker) is open: a tap cannot reach the toolbar then either, and a key must never start the line or replace a half-done sign-in or question. Stop by key still asks its question | 6, 12 |
| "This picture" card | Lists the tools and codes of the picture on screen, failed tools first, at most six lines with "+N more"; cleared with every new picture and on a stop, drop or product change, and dimmed with a stale picture, so it never shows old ✓ lines beside an NG | 6, 12 |
| Bad parts in the per-minute chart | Good parts as columns and a red dot above a minute with bad parts that grows with their count: one scale, and a few bad parts stay visible next to 200 good ones | 6 |

## Update log (section 8)
| 2026-10-31 | design/tokens/vision-tokens.json: compact sizes, spacing, radii and fonts; new chart colours | Claude (Mac, branch claude/studio-compact-run) |

## Pending
- Vision Studio Run screen: Alarms and Messages tabs beside Overview and Last bad parts (UI-UX-SPEC.md 4.1); the runtime has no alarm or message history call yet.
- Runtime contract: send the cycle budget (from the line speed) so the cycle-time chart draws its budget line.
- Vision Studio Run screen: one image tile per camera with its own verdict and "This picture" per camera, once the two-camera work (#147) is merged.
- Vision Studio Run screen: the line speed as a line in the parts-per-minute chart, a batch and PLC status panel, and the Parts list as a tab (UI-UX-SPEC.md 4.1); needs the line speed and batch in the runtime status.
- Runtime: NG reasons are sent as string-table keys (explain.*) that Vision Studio's table does not have yet, so the NG-reason chart and the Parts list show the key; add the explain.* words to dotnet/Studio/i18n.
