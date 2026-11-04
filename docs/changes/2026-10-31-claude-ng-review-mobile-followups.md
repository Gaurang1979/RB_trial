# Mobile NG review follow-ups: verdict note, open a part from the NG strip, account button, quiet-floor test

- Status: done
- Session: Claude desktop (ng-review-mobile-followups), 2026-10-31
- Pull request: #204
- Section 5 item: Mobile app: a note field with a verdict
- Components/files: plant_server/backend/vision (ingest.py, live.py), plant_server/tests/test_vision_ingest.py, mobile/plant_app/lib/modules/vision (vision_api, ng_api, ng_review_screen, line_screen) and its tests, docs/vision-system/NG-REVIEW.md
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #204: a note of up to 500 characters with each verdict in the mobile app; the live floor's last_ng carries result_id, set by the server at ingest and on the start-up replay, and the line view's NG strip opens that part for a verdict; an account button on the line view; the quiet-floor test's stale limit widened to 2 s) Mobile app: a note field with a verdict; open a part from the line view's NG strip (the live floor's last_ng has no result id yet); an account button on the line view.
- [x] (done 2026-10-31, PR #204: stale limit 2 s) Mobile app test "the line view notices by itself when the floor goes quiet" fails when run alone (its 300 ms stale limit is shorter than a cold start); widen it.

## Timeline (section 3)
| 2026-10-31 | Mobile NG review follow-ups: note with a verdict, the line view's NG strip opens a part by the server's result id (new result_id in the live floor's last_ng), account button on the line view | NG-REVIEW.md 6; PLANT-DASHBOARD-AND-MOBILE.md 6 |

## Decisions (section 4)
| result_id in last_ng | The server sets it from its own vision_result row (at ingest and on the start-up replay); a result_id in a station's event is ignored, so a station can never point the app at another line's part. Null for an OK part and for an old part the backfill has not reached | NG-REVIEW.md 6 |
| A part opened from the NG strip | Opens even when it already has a verdict; a new verdict is appended to its history (ng_review stays append-only, the newest counts) | NG-REVIEW.md 5, 6 |

## Update log (section 8)
| 2026-10-31 | NG-REVIEW.md section 6: the app's note and NG strip, result_id in the live floor's last_ng | Claude (Mac, branch claude/ng-review-mobile-followups) |

## Pending
- Translations of the new mobile strings ("Last NG parts", "NG part", "Note with the verdict (optional)", "Verdict saved.") with the other NG review strings: the app has no ARB files yet, so they wait for its l10n tables.
