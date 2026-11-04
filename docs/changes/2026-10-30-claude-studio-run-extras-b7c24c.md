# Vision Studio Run screen follow-ups: result tooltips, NG image viewer, yield ring and cycle time, product pictures, "What do I do?"

- Status: done
- Session: studio-run-extras (Claude, worktree nostalgic-hermann-10ee88), 2026-10-30
- Pull request: #110
- Section 5 item: Vision Studio: tap a result graphic to show its tooltip
- Components/files: dotnet/Studio, dotnet/Studio.Tests, dotnet/RuntimeContract/Stub (StubRuntime, StubServices, StubPictures new), dotnet/RuntimeContract.Tests, dotnet/RuntimeStub/Program.cs, docs/vision-system/screens/hybrid-2-run-extras (synthetic screenshots); no proto change
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #110: a tap on the live image shows the tapped graphic's tooltip for 5 s, following the same spot on the next parts; a tap on an NG thumbnail opens the NG image viewer with the full image from GetImage (the live picture until it comes or when no longer kept), the failed checks in the runtime's words, Previous / Next through the last 20 bad parts) Vision Studio: tap a result graphic to show its tooltip, and tap an NG thumbnail to open the NG image viewer (UI-UX-SPEC.md 4.3) with the full image from GetImage.
- "Vision Studio: product pictures in Change product": `[x] (done 2026-10-30, PR #110: Change product tiles show JobInfo.picture_id pictures from GetImage, kept while the app runs; Good % with a yield ring glyph and a cycle-time panel (median and max) from GetCounters every 2 s)`
- "Vision Studio: What do I do? on the banner": "What do I do?" built in PR #110: a button on the banner when the alarm or warning has help text, opening its steps with Done per step; the speaker button with spoken guidance is still open.
- [ ] Vision Studio NG image viewer: zoom (two fingers, double-tap to fit), tap a check to zoom to its region, "Show good part", "Use for teaching" (Supervisor+) and "Was OK" (UI-UX-SPEC.md 4.3); the last three need runtime calls the contract does not have yet.
- [ ] Vision Studio "What do I do?": "Try again" and "Call supervisor" (UI-UX-SPEC.md 4.4) need runtime calls; the step picture or animation needs the alarm catalogue.

## Timeline (section 3)
| 2026-10-30 | Vision Studio Run follow-ups: tooltips on tap, NG image viewer with GetImage, yield ring and cycle time from GetCounters, product pictures in Change product, "What do I do?" with steps; stub runtime keeps the last 50 images, draws product and case pictures, sends alarms with help; 94 Studio tests and 19 contract tests pass, screens checked at 1366 x 768 dark and light | REQUIREMENTS 6, UI-UX-SPEC 4.1-4.4 |

## Decisions (section 4)
| Cycle time sits under the counters, in the last-bad-parts row | At 1366 x 768 with a banner and its "What do I do?" button the counters card holds four rows only; the yield ring is the glyph of the Good % row (spec 4.1 keeps Good % as a row) | UI-UX-SPEC 4.1, 7 |
| Counters keep the last numbers while the runtime cannot be reached; cycle time does not | As Good / Bad / Total already did; a cycle time of a batch that may have ended is not shown | UI-UX-SPEC 2.1 |

## Update log (section 8)
| 2026-10-30 | Synthetic screenshots of the Run follow-ups in docs/vision-system/screens/hybrid-2-run-extras | Claude (Mac, branch claude/studio-run-extras-b7c24c) |

## Pending
- Vision Studio: the 15 languages for the new run.cycle*, run.what_to_do, ng.* and alarm.help.* strings (English only in PR #110).
