# Mobile app: PIN unlock of the stored sign-in on shared floor phones

- Status: done
- Session: mobile-pin-unlock (Claude), 2026-10-31
- Pull request: #209
- Section 5 item: new (from the ## Pending list of docs/changes/2026-10-30-claude-ng-review.md: "Mobile app: PIN unlock of the stored sign-in")
- Components/files: mobile/plant_core (new pin.dart; PcSession, PcServers in modules.dart), mobile/plant_app (ApiAuth, account screen, Vision module, app shell), docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md 6. No plant_server change
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #209: per-person PIN on shared floor phones; one sign-in open at a time; lock when hidden or 2 min untouched; 5 wrong PINs or 15 min unused end the sign-in on the phone and the server) Mobile app: PIN unlock of the stored sign-in on shared floor phones (PLANT-DASHBOARD-AND-MOBILE.md 6).

## Timeline (section 3)
| 2026-10-31 | Mobile app PIN unlock on shared floor phones: each person signs in once with the password and chooses a 4-8 digit PIN, kept only as a salted PBKDF2-HMAC-SHA256 hash in secure storage; one sign-in open on the phone, locked when the app is hidden or after 2 min untouched (calls then go with the plant key, no verdicts); 5 wrong PINs, 15 min unused or a clock gone back end it on the phone and the server; old PIN-less sessions wiped and ended. plant_core 18 and plant_app 74 tests pass; reviewed | PLANT-DASHBOARD-AND-MOBILE 6, PT11-03, PT11-06, PT11-07 |

## Decisions (section 4)
| Mobile PIN unlock | The PIN only reopens a sign-in made with name and password on that phone; it never signs in alone (PT11-03). It is checked on the phone (works offline; no server PIN endpoint); 5 wrong PINs drop the sign-in instead of a timed lock, since the PIN guards a live token. Safe defaults pending the owner: 4-8 digits (no repeats or straight runs), lock after 2 min, sign-out after 15 min (PT11-06) | Security, PT11 |

## Update log (section 8)
| 2026-10-31 | PLANT-DASHBOARD-AND-MOBILE.md 6: how the PIN on a shared floor phone works | Claude (Mac, branch claude/mobile-pin-unlock) |

## Pending
- Owner: is reopening a password sign-in with only a PIN on a shared phone acceptable under PT11-03 (built so; e-signatures still ask the password), and are 4-8 digits, lock after 2 min and sign-out after 15 min the right defaults?
- Plant server audit of mobile PIN events (PT11-05, PT11-21): wrong PINs and the 5-wrong drop are only seen by the server as a sign-out today; add an audit endpoint and an Admin alert for repeated drops.
- Mobile NG review: clear the queue and images when the phone locks, and reload when another person opens their sign-in (it watches signedIn, not who).
- Mobile PIN check: run the PBKDF2 hash off the UI isolate (Isolate.run), counting the wrong try before the await.
- Plant server sessions last 7 days with no idle limit (backend/auth.py): end server sessions after PT11-06 idle time too.
