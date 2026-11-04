# NG review: per-part vision results and the NG review screens

- Status: done
- Session: Claude desktop (ng-review), 2026-10-30
- Pull request: #71
- Section 5 item: new (PL2 follow-up: "Per-part vision_result table and NG review (ng_review), with the NG review screens")
- Components/files: plant_server (vision tables, ingest, images, review, api, dashboard/ng-review.html), dotnet (StationGuard NgImageStore, StationAgent link and agent, VisionStudio Station, JpegReducer, NgImageSource, new VisionStudio.Tests), mobile/plant_app (Vision NG review), .github/workflows/windows-build.yml, docs/vision-system/NG-REVIEW.md
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #71: per-part vision_result rows, NG images sent by the station as a JPEG of at most 300 KB when the plant server asks in its heartbeat answer, verdicts confirmed / overruled / unsure with history, summary with the overrule rate, CSV export for training, web page /vision/review and the mobile app's swipe queue; docs/vision-system/NG-REVIEW.md) NG review: per-part vision results and the NG review screens (Plant Insight F5).

## Timeline (section 3)
| 2026-10-30 | NG review: the line PC gives each part with kept images an image id and sends a reduced JPEG (≤ 300 KB) when the plant server asks; the plant server keeps a row per inspected part, asks for every NG image at once, and takes verdicts on the web page and in the mobile app | PLANT-DASHBOARD-AND-MOBILE.md 5, 6 (F5); PLANT-PLATFORM.md |

## Decisions (section 4)
| NG images to the plant server | Owner, 2026-10-30: "the station send when server asks, reduced image of 300 kb"; the full-resolution image stays on the line PC for training export. The server asks in its heartbeat answer (the station only connects out), for every new NG image at once, so review works while the line PC is off | NG-REVIEW.md 1 |
| Who gives a verdict | Admin, plant manager, production manager and quality manager for the line; a service key cannot, since a verdict names a person | NG-REVIEW.md 5 |
| Verdicts are kept, never changed | ng_review is append-only; the newest verdict counts and the history stays (Part 11 friendly). A verdict never changes what the line did | Requirements 12 |
| Mobile sign-in in this change | Owner, 2026-10-30: build the app's sign-in here. User name and password against the plant server's /auth/login; only the session token is kept (secure storage, per server); every Vision call then speaks for the person; without it the app reads with the plant key and gives no verdicts. PIN unlock and company sign-in stay later items | PLANT-DASHBOARD-AND-MOBILE.md 6 |
| Sign-in limit | Owner, 2026-10-30: per user and device. 5 wrong passwords for a user name from one device, or 20 from one device for any names, in 15 minutes block that device for the rest of the window; the real user still signs in from their own device. Checked and counted in one step; IPv6 counted by /64; in memory | Security |
| Image storage | JPEGs on disk under VISION_IMAGE_DIR, file names hashed from station, id and camera; size limit VISION_IMAGE_LIMIT_BYTES (2 GB), oldest first | NG-REVIEW.md 4 |

## Update log (section 8)
| 2026-10-30 | New docs/vision-system/NG-REVIEW.md (image travel, tables, endpoints, screens) | Claude (Mac, branch claude/ng-review) |

## Pending
- Plant server deploy: mount a volume for VISION_IMAGE_DIR in docker-compose and the deploy notes.
- Vision Studio: training export that reads the plant server's verdicts (/vision/ng/export.csv) and copies the overruled parts' full-resolution images.
- NG review strings (web page and mobile app) in the other languages, by the translator.
- Mobile app: a note field with a verdict; open a part from the line view's NG strip (the live floor's last_ng has no result id yet); an account button on the line view.
- Mobile app: PIN unlock of the stored sign-in on shared floor phones (PLANT-DASHBOARD-AND-MOBILE.md 6).
- Mobile app test "the line view notices by itself when the floor goes quiet" fails when run alone (its 300 ms stale limit is shorter than a cold start); widen it.
- Run the new .NET tests on .NET 10 (Windows build): they ran here on a net8.0 copy only.
