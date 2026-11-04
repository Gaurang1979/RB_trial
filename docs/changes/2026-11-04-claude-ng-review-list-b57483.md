# NG review: bad parts waiting shown on the Run screen, the reviewed pictures listed (change or remove a decision), the operator's "Was OK" flag in the supervisor's list

- Status: done
- Session: claude/ng-review-list-b57483, 2026-11-04
- Pull request: #330
- Section 5 item: new
- Components/files: proto/visionstudio/remote/v1/runtime.proto (additive: LearningState.bad_to_review, flagged_ok; SortItem.flagged_ok_by/at; new service NgReviews: ListReviewed, RemoveReview, FlagWasOk), dotnet/Runtime.Host (Reviews.cs, Sorting.cs, LineRuntime.cs status counts and GetImage of reviewed copies, ReviewListService.cs new, RuntimeHost.cs two lines), dotnet/Studio (IReviewClient, GrpcRuntimeClient, RuntimeModels, NgReview.cs, Sorting.cs, RunViewModel.cs, NgViewerView, SortView, RunView, en.json), tests (Runtime.Host.Tests/ReviewListTests.cs, Studio.Tests/ReviewListTests.cs, Doubles.cs), docs/REQUIREMENTS-VISION-SYSTEM.md AS-04i..k, docs/vision-system/UI-UX-SPEC.md 4.3, 4.11. Not Services.cs, Access.cs, the sign-in code or VisionCore/Jobs/AutoTune.cs.
- Depends on: none

## Section 5
- New item: `[x] (done 2026-11-04, PR #330: the Run screen's Bad parts tab tells a supervisor how many saved bad pictures wait for a review and how many an operator marked Was OK, whatever the learning setting; the Learning screen lists the reviewed pictures (filter Reviewed) where a decision is changed by the same rules or removed, audited, and learning uses only the decisions kept now; operators mark a bad part Was OK in the NG viewer, shown to the supervisor (NG viewer, filter Was OK, the count) and cleared by the decision, never learned from)` NG review: bad parts waiting, the reviewed pictures listed (change or remove a decision), the operator's "Was OK" flag (AS-04i..k).

## Timeline (section 3)
| 2026-11-04 | NG review follow-ups: bad parts waiting shown on the Run screen, the reviewed list with change and remove (audited, Learn and Apply see only the decisions kept now), the operator's "Was OK" flag for the supervisor | 4, 12 (AS-04i, AS-04j, AS-04k) |

## Decisions (section 4)
| Review follow-ups in their own gRPC service | The reviewed list, Remove and the Was OK flag are a new service NgReviews beside VisionRuntime (same channel and sign-in), so Services.cs (gRPC roles, worked on by the OEM sign-in session) is not touched. A change of a decision is ReviewPart with the reviewed picture's id. | AS-04j, AS-04k |
| A changed decision keeps the rules | The review now keeps the fault keys and the software's verdict with the decision, so a change after the saved bad picture is gone is still judged as a first review (clearly bad accepted: a record only; unreadable: the second validation). A review kept before this change has no fault keys: while the line's own picture is kept it is used, else the cause counts as not known and the picture is a record only when accepted. | AS-04j, AS-12, section 12 |
| Removed decisions and learning | Remove deletes the kept copy: the picture is no longer learned from, waits for a review again while its saved bad picture is kept, and a proposal learned before cannot be applied (the reviewed pictures changed). Change, removal and the Was OK flag are audit-trail events (review_changed, review_removed, was_ok_flag). A possible escape is raised once per part, not again when a decision is changed back; a review kept before this change that was accepted as good or has no fault may have been a passed part, so rejecting it is told as a possible escape (the safe side). | AS-04e, AS-04j |
| Was OK flag | Anyone who may run the line marks a bad part not yet decided; kept under data/review-flags/<job> (outside data/reviews, which the browser's Operator flags tab, PR #340, reads as decisions) with who and when (at most 2000 per job); only a part with a saved picture name (an old-style id starts again after a restart); Vision Studio users only, not plant software; never in the learning set; the supervisor's decision deletes it. The count and the "Was OK" filter show it; the Reports tab "Operator flags" (UI-UX-SPEC 4.11) can read the same list later. | AS-04k |
| Count shown whatever the setting | The Bad parts tab count is the NG review's (the setting only turns off good pictures and the sorting). With the session off, the count opens the NG viewer on the newest bad part, since the sorting lists nothing then. | AS-04b, AS-04i |

## Update log (section 8)
| 2026-11-04 | Requirements AS-04i..k (bad parts waiting, reviewed pictures, Was OK flag); UI-UX-SPEC.md 4.3 rows and 4.11 Operator flags | Claude (Mac, branch claude/ng-review-list-b57483) |

## Pending
- NG review follow-up strings (run.review.*, ng.was_ok*, sort.flagged, sort.filter.was_ok*, sort.filter.reviewed*, sort.reviewed.*) in the 14 other languages: Vision Studio ships only en.json today.
- Reports screen "Operator flags" tab (UI-UX-SPEC 4.11) reading the Was OK list (NgReviews.ListReviewed flags), with Agree / No.
- NG review follow-ups on a line PC: the Bad parts count button, the Learning screen's Reviewed and Was OK filters and the NG viewer's Was OK button at 1366 x 768 and on touch.
