# Runtime.Host tests no longer lose their saved images on a nearly full development disk (ImageNameLineTests)

- Status: done
- Session: Claude (Mac, imagename-test-date), 2026-11-04
- Pull request: #344
- Section 5 item: new
- Components/files: dotnet/Runtime.Host/RuntimeHost.cs (internal test seam only), dotnet/Runtime.Host.Tests/RuntimeFixture.cs, dotnet/Runtime.Host.Tests/ImageNameLineTests.cs, docs/REQUIREMENTS-VISION-SYSTEM.md (17.14 TST-HYG-02)
- Depends on: none

Requirements 17.14, TST-HYG-02 (registered in this pull request; owner, 2026-11-04: "Add test seam (a)"). From the Pending
lists of `2026-11-03-claude-sim-frame-delay.md` and `2026-11-03-claude-grade-trail-persist-f93d50.md`.

## Cause

The Pending lists blamed the test image's fixed date (2026-10-30). That was not it: the test failed the same with
today's date. The Mac's disk was 93% used (7% free), under the resource guard's disk "High" mark (10% free), so the
runtime's `ng-images-trim-disk` step deleted the oldest NG images toward 15% free, and the test's saved image went
first. With the original date and the mark lowered, all 3 `ImageNameLineTests` passed. The guard is right for a
line PC and is unchanged.

## Change

- `RuntimeHost.Build` gets an `internal` overload taking `Func<GuardOptions, GuardOptions>` and
  `Func<NgImageOptions, NgImageOptions>`, applied to the resource guard and to both image stores (NG images and
  learning-ok). The public `Build(args, baseDir)` passes none, so production defaults and behaviour are the same.
  Not a Studio setting; only `Runtime.Host.Tests` (InternalsVisibleTo) can use it.
- `RuntimeFixture` sets the disk marks (`DiskHighFreePct`, `DiskCriticalFreePct`, `TargetFreeDiskPct`) to 0.
- `ImageNameLineTests` dates its saved NG image yesterday at 08:00, so it never ages out.

## Section 5
- New: [x] (done 2026-11-04, PR #344: internal guard/image-store seam in RuntimeHost; fixture turns off disk trimming) Runtime.Host.Tests: `ImageNameLineTests.GetImage_opens_a_saved_image_by_its_name` failed from 2026-11-04 because a nearly full development disk made the guard delete its saved image (Req 17.14 TST-HYG-02)

## Timeline (section 3)
| 2026-11-04 | Runtime.Host tests independent of the development disk: internal seam for the guard and image store limits, fixture turns off disk trimming, ImageNameLineTests dated from today | 17.14 TST-HYG-02 |

## Decisions (section 4)
| Test seam for disk limits | Owner, 2026-11-04 ("Add test seam (a)"): an internal Build overload passes the guard's and image stores' limits from the tests; production defaults unchanged, no user-facing setting. The guard's disk trimming stays as it is on line PCs. | 17.14 TST-HYG-02 |

## Update log (section 8)
| 2026-11-04 | Requirements 17.14: TST-HYG-02 added | Claude (Mac, branch claude/imagename-test-date-0dc6cb) |
