# VisionCore.Tests that fail on main or only under load: find each cause, fix (Req 17.9 AGR-05, TST-HYG-01)

- Status: done
- Session: beautiful-curie-5f86c3, 2026-11-03
- Pull request: #294
- Section 5 item: new
- Components/files: dotnet/VisionCore.Tests (BlobToolTests, CameraChannelTests, RenderedFontTests, SecureAssetVaultTests), product code only if a test shows a real bug
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #294: TST-HYG-01) VisionCore.Tests that failed on main or under load: causes found and fixed in the tests, no product change.

## Timeline (section 3)
| 2026-11-03 | The four VisionCore.Tests named as failing or flaky are fixed; each passes 10 runs in a row, and the four classes pass together (71) | 17.14 TST-HYG-01 |

## Decisions (section 4)
| VisionCore test hygiene | The Blob tool's 50 ms and the Text tool's 100 ms time budgets are line-speed limits. Past it, the tool correctly reports NG ("not checked"); on a loaded Mac the tests tripped it. The tests now set a generous budget (`TimeBudgetMs` 2000 / 5000), as other tests already did; the product defaults and the NG on overrun stay (`TimeBudgetOverrunIsNotChecked` still guards it). | 17.14 TST-HYG-01 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.14 TST-HYG-01 added | Claude (branch claude/beautiful-curie-5f86c3) |

## Verified
- Causes. `BlobToolTests.OutlineCutByTheAreaIsNotChecked`: with the default 50 ms budget the run came back `explain.blob.incomplete` (budget overrun, NG) instead of `explain.contour.not_closed`; all 9 outline tests that used the default budget now set 2000 ms. `RenderedFontTests.LightPrintOnADarkPartReadsWithTheSameFont`: the 100 ms Text budget cut the read short ("found empty"); the `Check` helper sets 5000 ms. `CameraChannelTests.CamerasAtDifferentPlacesTriggerIndependently`: already keeps the heartbeat running while it waits (on main); passed 10 of 10 at Mac load above 50. `SecureAssetVaultTests...TamperedPayload`: no longer exists; #269 replaced it with `TamperedBlob` (8 cases), which pass.
- Product bug: none. Both budget trips were correct NG results; the line-safety checks are not loosened.
- Ran: engine dylibs built (cmake), `dotnet build VisionCore.Tests` 0 errors; the four named tests (11 cases) 10 runs, all passed; BlobToolTests, RenderedFontTests, CameraChannelTests and SecureAssetVaultTests together: 71 passed. CI: not run (billing). Bench: not needed (no engine change).
