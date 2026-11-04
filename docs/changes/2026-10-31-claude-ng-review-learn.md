# Supervisor NG review on the running line (zoom, accept or confirm NG) and learning from the reviews (auto-tune proposal, approved before it applies)

- Status: done
- Session: Claude desktop (ng-review-learn), 2026-10-31
- Pull request: #206
- Section 5 item: new
- Components/files: proto/visionstudio/remote/v1/runtime.proto (additive: ReviewPart, GetReviews, LearnFromReviews, ApplyLearning), dotnet/Runtime.Host (Reviews.cs new: ReviewStore, ReviewDesk, Learning; LineRuntime.cs bad parts' picture sources and SavedPicture; Access.cs Review; Services.cs; RuntimeHost.cs; csproj InternalsVisibleTo), dotnet/Studio (IReviewClient, GrpcRuntimeClient, NgReview.cs, NgViewerView zoom and review card, MainViewModel, RunViewModel, i18n), tests, docs/REQUIREMENTS-VISION-SYSTEM.md (AS-04), docs/vision-system/UI-UX-SPEC.md 4.3
- Depends on: #154 (AutoTune), #147, #190 (ZoomBox), all merged

## Section 5
- New item: `[x] (done 2026-10-31, PR #206: Supervisor, QA, Engineer and Admin review any NG image from the Run screen while the line runs, zoomed, as Accept or Confirm NG; the part's full picture is kept with the decision; a part the software found clearly bad asks for a double-check and, accepted all the same, is kept as a record only, never learned from; Learn from reviews (line stopped) runs AutoTune on the reviewed pictures and Apply (line stopped) saves a new job version only while the job and the reviews are as learned)` Supervisor NG review on the running line and learning from the reviews.

## Timeline (section 3)
| 2026-10-31 | Supervisor NG review on the running line (zoom, Accept or Confirm NG, double-check of clearly bad parts, kept out of learning) and learning from the reviews (AutoTune proposal proved on the reviewed pictures, applied by a person with the line stopped) | 4, 6, 12, 16 (AS-03, AS-04, AS-12) |

## Decisions (section 4)
| Supervisor review on the running line | Owner, 2026-10-31: "on supervisor screen there must be option in running line : each NG image can be selected, zoomed and practically decided that it is accepted or NG. based on that Ai should start auto tuning and learning to match the operator requirement." Supervisor, QA, Engineer and Admin decide, while the line runs (a review only copies a picture); operators see the image only. The runtime keeps the sources of the last 500 bad parts so a part can be reviewed minutes later | 6, 16 |
| Clearly bad parts are never learned from | Owner, 2026-10-31: "if vision software find image is really bad can ask supervisor for double check and mention not to add in good records to avoid bad images teaching." When a tool found a defect (wrong or missing code data, GS1 errors, a wrong check digit, fewer or more codes than the product has, a wrong, missing, broken or shifted character, extra print, a bad date or price, a size out of tolerance, a foreign mark), Accept asks to look again and lists what was found. Accepted all the same, the picture is kept as a record only; the runtime enforces it whatever the app sends. A code not found at all and a low print grade (often faint print) are left to the supervisor's one tap | 12, 16 |
| Learning and applying | Learn runs AutoTune (AS-03) on the camera's reviewed pictures with the line stopped, and stops if the line starts. Apply needs the line stopped, the job not saved since, and the reviewed pictures unchanged since (a part confirmed bad later was never proved against); it saves a new version. A setting that accepts more parts needs a confirmed bad picture that setting alone rejects, and at least 5 confirmed bad pictures rejected by the tuned settings alone | 12, 16 (AS-04, AS-12) |
| Reviewed pictures | Kept under data/reviews/<job>/cam<n>/good, bad or accepted-record-only, named by the picture's image name (line, camera, job, time, serial), so a runtime restart never overwrites an older decision; learning uses only pictures reviewed under the camera's current name; at most 2000 pictures and 4 GB per camera and decision | 12 |

## Update log (section 8)
| 2026-10-31 | Requirements AS-04: supervisor NG review and learning (owner's words); UI-UX-SPEC.md 4.3: zoom, supervisor review, Learn from reviews | Claude (Mac, branch claude/ng-review-learn) |

## Pending
- NG review: the 14 other languages for the review.* strings, and the explain.* reason keys in words in Vision Studio (the review lists them in English from the runtime today).
- NG review: tell the supervisor on the Run screen how many bad parts wait for a review, and a list of the reviewed pictures with their decisions (change or remove one).
- NG review: learning also from accepted code pictures (AutoTune tunes only Find the part and Check text today; Read code's lowest grade and time budget are pending from #154).
- NG review: the operator's "Was OK" flag (UI-UX-SPEC.md 4.3) feeding the supervisor's list.
- Runtime.Host.Tests: IntakeCameraTests.Buffers_are_reused_only_after_the_next_grab fails in the full run while other test classes run lines (a 1 ms simulated frame thread races the assertion) and passes alone; make it wait for the grab instead of racing it.
