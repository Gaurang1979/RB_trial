# Line PC: live charts on the Run screen (yield, cycle time, NG reasons, NG images with counts, PC health)

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #53
- Section 5 item: new
- Components/files: dotnet/VisionStudio (Run page, Components/Shared charts, MainLayout, App.razor, app.css, i18n/en.json)
- Depends on: #51

## Section 5
- [x] (done 2026-10-29, PR #53: four live panels below the verdict and camera cards — yield ring and per-minute yield, cycle-time trend with median, why-NG bars with saved/skipped image counts and an "Open images" gallery of the latest 12, PC health gauges with the guard's level, leak warning, last clean-up steps and the restart banner; SVG/HTML charts without JavaScript; light, dark and auto themes; 48 px touch targets) Line PC live charts on the Run screen.

## Timeline (section 3)
| 2026-10-29 | Vision Studio Run screen: live yield, cycle-time, NG-reason and PC-health panels, NG image gallery with counts, light/dark/auto theme | UI-UX-SPEC.md; PLANT-DASHBOARD-AND-MOBILE.md 6.2 |

## Decisions (section 4)
| Run screen refresh | The verdict and counters stay at up to 10 redraws a second; the chart panels redraw at most once a second and only when the station changed (or the guard took a new sample), so the screen never costs the inspection time | UI-UX-SPEC.md |
| PC health wording | Guard levels shown as "running well", "getting busy", "busy", "overloaded", each with ✓ ! ✗; CPU shown without a level (the guard does not judge it) | UI-UX-SPEC.md |

## Update log (section 8)
| 2026-10-29 | i18n/en.json: theme, chart and Run panel words (English; other languages by the translator) | Claude (cloud, branch claude/line-pc-live-charts) |

## Pending
- Run screen strings in the other 14 languages (i18n), by the translator.
- PC health gauges should read the running guard's thresholds (expose GuardOptions) instead of the defaults.
- See the Run screen live on a line PC with a running job at 1366×768: charts with data, the gallery, the theme toggle.
