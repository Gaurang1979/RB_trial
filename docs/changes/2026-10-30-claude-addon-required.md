# Code Reader: an "add-on required" setting and an expected add-on text in the Read code tool

- Status: done
- Session: addon-required, 2026-10-30
- Pull request: #99
- Section 5 item: new
- Components/files: dotnet/VisionCore (CodeTool, its config and job serialization, English strings in dotnet/VisionStudio/i18n/en.json), docs/vision-system/TOOL-SPECIFICATIONS.md, dotnet/VisionCore.Tests
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #99: AddOnRequired and ExpectedAddOn in the Read code tool, explain.code1d.addon_missing / addon_wrong, 23 tests) Code Reader: an "add-on required" setting and an expected add-on text in the Read code tool for EAN/UPC 2- and 5-digit add-ons.

## Timeline (section 3)
| 2026-10-30 | Read code tool: EAN/UPC add-on check. `AddOnRequired` and `ExpectedAddOn` (2 or 5 digits, or a batch-field template; setting it makes the add-on required); a missing, unreadable or in-doubt add-on fails with `explain.code1d.addon_missing`, a different one with `explain.code1d.addon_wrong`; other types or a fixed text that is not 2 or 5 digits are setup errors. VisionCore.Tests: 23 new tests pass | Tool spec 5.2, requirements 12 |

## Decisions (section 4)
| Add-on in doubt is missing | The engine reports "" for an add-on that is absent, failed its parity check, or differed between reads of the same main text; the tool cannot tell these apart, so all three are NG with `explain.code1d.addon_missing`. Judging without the engine's add-ons (none reported) is NG too | Requirements 12 |
| Expected add-on implies required | An expected add-on text that is missing could only be NG, so setting the text switches the check on; `AddOnRequired` alone accepts any add-on | Tool spec 5.2 |
| Add-on cannot change the main code | The main EAN/UPC symbol is decoded with its check digit before zxing looks for the add-on, and the engine removes the add-on from the text only when the text ends with exactly its digits; a mis-segmented add-on gives a wrong or missing add-on, never a different main code. The add-on is checked after the main text and GS1 checks, so a wrong label is named as one | Requirements 12 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md 5.2: add-on check parameters and explain keys | Claude (Mac, branch claude/addon-required) |

## Pending
- Read code tool in the Avalonia app (Hybrid 2/3): the "Add-on required" and "Expected add-on" settings in the tool panel and the add-on shown in the result list (VisionStudio is not changed).
- Read code teaching: switch "Add-on required" on when every taught EAN/UPC read carries an add-on.
- Translations of `explain.code1d.addon_missing` and `explain.code1d.addon_wrong` into the 15 languages.
