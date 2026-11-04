# Vision Studio Teach page: open an NG image from the Run page, and a "bars" field for a Pharmacode's bar-count lock

- Status: done
- Session: studio-teach-ng-bars, 2026-10-30
- Pull request: #133
- Section 5 item: new
- Components/files: dotnet/VisionStudio (Components/Pages/Run.razor and Teach.razor, Services/NgTeachLink.cs and PharmacodeBarsField.cs new, i18n/en.json), dotnet/VisionStudio.Tests (NgTeachLinkTests.cs, PharmacodeBarsFieldTests.cs new)
- Depends on: none

## Section 5
- New: [x] (done 2026-10-30, PR #133: each image in the Run page's NG image list has a Teach link that opens the Teach page with that picture loaded, in the job and on the camera that saved it (a new job when that job is gone); the link names only a .bmp under the NG image folder; 3 tests) Classical OCR: a link from the Run page's NG image list to the Teach page with that picture loaded (today the engineer loads the NG image's path on the Teach page).
- New: [x] (done 2026-10-30, PR #133: one number sets both bounds of the bar-count lock; a range taught from differing codes is shown until a number is typed; empty or outside 2-16 clears the lock, which stays a setup error; English string only; 6 tests) Vision Studio Teach page: a "bars" field for a Pharmacode's bar-count lock (teaching sets it today; the length fields show for 1D types only).

## Timeline (section 3)
| 2026-10-30 | Vision Studio: a Teach link on each NG image in the Run page's list opens that picture on the Teach page in its job and camera; a "bars" field on the Teach page for a Pharmacode's bar-count lock | TOOL-SPECIFICATIONS.md 5.0, 7.2 |

## Decisions (section 4)
| NG image link opens only NG images | The Teach page takes a path under the NG image folder (ng-images), a .bmp that is still kept, and nothing else: no "..", no rooted path, no other file type, so the link cannot open other files on the line PC. The job and camera come from the label the line saved beside the image ("<job>-<camera>", matched exactly against the jobs' own names, so a dash in a job name cannot pick another job) | Requirements 12 |
| Pharmacode bars is one number | The engine needs both bounds of the lock, and an exact count is the tightest lock, so the field sets both. Clearing it, or a count outside 2-16, sets no lock, which the tool refuses as a setup error, never "any number of bars" | TOOL-SPECIFICATIONS.md 5.0, requirements 12 |

## Update log (section 8)
| 2026-10-30 | New English strings run.ng.teach, run.ng.teach_hint, teach.ng_loaded, teach.ng_no_job, teach.ng_missing, code.bars in VisionStudio i18n/en.json (other languages deferred by the owner) | Claude (Mac, branch claude/studio-teach-ng-bars) |

## Pending
- Vision Studio strings run.ng.teach, run.ng.teach_hint, teach.ng_loaded, teach.ng_no_job, teach.ng_missing and code.bars in the other 14 languages (deferred by the owner, 2026-10-30).
