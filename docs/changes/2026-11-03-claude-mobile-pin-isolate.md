# Mobile: PIN check off the UI isolate; NG review queue follows who is signed in

- Status: done
- Session: Claude desktop (pin-isolate-ngq), 2026-11-03
- Pull request: #275
- Section 5 item: new (## Pending of docs/changes/2026-10-31-claude-mobile-pin-unlock.md: "Mobile PIN check: run the PBKDF2 hash off the UI isolate" and "Mobile NG review: clear the queue and images when the phone locks"; of 2026-10-30-claude-ng-review.md: the quiet-floor test)
- Components/files: mobile/plant_core (pin.dart, PcServers.unlock in modules.dart), mobile/plant_app (ng_review_screen.dart, tests). Serves PT11-06, PT11-07 (requirements already registered; no new requirement)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #275: PIN hash runs on another isolate with the wrong try counted and saved before the await; the NG review queue and images clear when the phone locks or another person signs in, and the queue is read again) Mobile PIN check off the UI isolate; Mobile NG review queue per person.

## Timeline (section 3)
| 2026-11-03 | Mobile: PcPinHash.matchesAsync (Isolate.run); PcServers.unlock counts and saves the wrong try before awaiting the hash (a kill mid-check cannot skip it; the last try decides drop or unlock; a sign-in ended during the hash opens nothing); NG review screen watches who is signed in, clears queue, images and note on a lock or change and reads again, discarding replies of the old person. The quiet-floor test was already widened on main (PR #204), no change. plant_core 48 tests and plant_app sign_in + ng_review 59 tests pass; analyze clean in both | PT11-06, PT11-07 |

## Decisions (section 4)
| Count before the await | A wrong try is saved before the slow hash, so killing the app mid-check cannot buy a free try; a right PIN resets the count | PT11-05, PT11-07 |

## Update log (section 8)
| 2026-11-03 | Change note only | Claude (branch claude/interesting-dubinsky-57e08e) |

## Pending
- Plant server audit endpoint for mobile PIN events: not wired here (another session adds it in backend/auth.py).
