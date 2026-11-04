# Hybrid 2: Vision Studio shell on Avalonia (theme, frame, sign-in, runtime link with reconnect, Run screen)

- Status: done
- Session: Claude desktop (hybrid-2-studio-shell), 2026-10-30
- Pull request: #86
- Section 5 item: new
- Components/files: dotnet/Studio (new, exe vs-studio), dotnet/Studio.Tests (new), design/tokens/vision-tokens.json (new), tools/tokens/gen_tokens.py (new), docs/vision-system/screens/hybrid-2 (new, synthetic screenshots)
- Depends on: #85 and #88 (Hybrid 0 runtime gRPC contract and stub runtime; merged), #72 (design source: ARCHITECTURE.md 2.2-2.4 and 9, UI-UX-SPEC.md)

## Section 5
- [x] (done 2026-10-30, PR #86: dotnet/Studio `vs-studio` on Avalonia 11.3.22 and net10.0; one token file design/tokens/vision-tokens.json generated into Avalonia resources with Dark, Light and High contrast; top bar with product, status pill, language and user; Run screen with live image, result graphics drawn by the app, the big OK / NG block, counters, last 20 bad parts, banner and an action bar of Start, Stop, Change product, Help; PIN sign-in; link to the runtime over the pipe or Unix socket with reconnect forever and a status probe; 79 unit and headless layout tests, 3 opt-in tests against the stub runtime and for screenshots) Hybrid 2: Vision Studio shell on Avalonia: theme from the shared tokens, frame, sign-in, runtime link with reconnect, Run screen with live image and OK / NG.

## Timeline (section 3)
| 2026-10-30 | Vision Studio shell (Hybrid 2): Avalonia app `vs-studio` against the Hybrid 0 contract; design tokens with three themes; Run screen, sign-in, change product; reconnecting runtime link; checked headless at 1366 x 768 and against the stub runtime on macOS | 6, 7, 12 |

## Decisions (section 4)
| Vision Studio talks to the runtime through one app-side interface | `IRuntimeClient` with app models; `GrpcRuntimeClient` maps the contract onto it. View-models and tests never see protobuf types, and a contract change touches one file. | 6, 10 |
| Reconnect | One loop connects, reads status and live frames, and on any failure or ended stream retries forever: 250 ms doubling to 5 s, plus up to 20 % jitter. The session token survives a dropped pipe; WhoAmI after reconnect says whether the user is still signed in. While not connected the result block says "Not connected", never the last result. | 6, 12 |
| Result block timing | A bad part (NG or same code) stays up at least 1 s; at high rates the worst result of the last 200 ms is shown (UI-UX-SPEC.md 4.1). With no new part for 5 s, a stop, a start, a product change or an alarm coming or going, the block shows "Waiting for a part" or the line state instead of the last result, and the old image is dimmed until a new part arrives. | 6, 12 |
| A bad part whose frame was dropped | Live frames are dropped when the app cannot keep up, the runtime's Bad count is not: when Bad rises with no NG frame shown in the last second, the block shows NG. Preview frames (no inspection) are never a result. | 12 |
| Link liveness | Besides the streams, the link asks for the status every 2 s and needs an answer within 2 s; a runtime that stops answering while its streams hang shows "Not connected". A connection that ends within 5 s counts as a failed attempt, so retries keep slowing down. Only transport failures (Unavailable, Internal, Unknown, Cancelled) drop the link; a refusal or "no answer in time" is shown as such. Start, Stop and Change product wait up to 30 s. | 6, 12 |
| Actions never hidden | Start, Stop and Change product stay on the bar; when not possible now they are faded and a tap says why. The runtime decides every changing call; its refusal key is shown in our words when the string table has it, else the runtime's own text. | 6 |
| High contrast theme key | Avalonia converts only Dark and Light from XAML text, so the generated resources key High contrast with `App.HighContrast`, which falls back to the Dark Fluent resources. | 7 |
| Screens for the pull request | Rendered headless (Avalonia.Headless with Skia) at 1366 x 768 from synthetic frames, so no customer image and no desktop capture is needed. | 6 |

## Update log (section 8)
| 2026-10-30 | New design/tokens/vision-tokens.json (the one source of colours, sizes, spacing, radii and fonts) and its generator tools/tokens/gen_tokens.py | Claude (Mac, branch claude/hybrid-2-studio-shell) |

## Pending
- Vision Studio: tap a result graphic to show its tooltip, and tap an NG thumbnail to open the NG image viewer (UI-UX-SPEC.md 4.3) with the full image from GetImage.
- Vision Studio: "What do I do?" on the banner (UI-UX-SPEC.md 4.4) from the alarm's help text, and the speaker button with spoken guidance.
- Vision Studio: card reader and "Use password" sign-in, auto sign-out after the idle time, and the side menu for the setup screens (Hybrid 3).
- Vision Studio: product pictures in Change product (JobInfo.picture_id) and the yield ring and cycle time from GetCounters on the Run screen.
- Vision Studio: WatchFrames asks for at most 10 frames a second; a line faster than that relies on the Bad-count rule above. Ask the runtime (Hybrid 1) for the inspection result stream next to the frames if the owner wants each part's own verdict shown.
- Vision Studio: the 14 other languages of LOCALISATION.md in dotnet/Studio/i18n (only English ships today), with Indic and Urdu fonts.
- Vision Studio on the line PC: Windows full screen at 1366 x 768 and 1920 x 1080, touch with gloves, and the named pipe to the real runtime service.
