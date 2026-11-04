# Vision Studio Windows only (Avalonia): compact Operator screens for touch, keyboard and mouse, a dense VisionPro-style Engineer workspace, and charts (design and clickable mock-up)

- Status: done
- Session: Claude desktop (studio-windows-dense), 2026-10-31
- Pull request: #173
- Section 5 item: new
- Components/files: design/prototypes/vision-studio/index.html (new), docs/vision-system/UI-UX-SPEC.md, docs/vision-system/ARCHITECTURE.md, docs/REQUIREMENTS-VISION-SYSTEM.md (design only, no code)
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #173: UI-UX-SPEC.md principles 2-4 and 15, sections 2, 4.1, 4.7, 4.19, 7 and 9; ARCHITECTURE.md 2.4 and 10; requirements sections 2, 6 and 14; mock-up design/prototypes/vision-studio/index.html, shared copy https://claude.ai/artifact/6iyprshYpS2WjnvEL4Y1Wo) Vision Studio design for Windows only: compact Operator Run screen, dense Engineer workspace, charts.
- "Windows only for two years, or a Linux / Jetson runtime too?": `[x] (done 2026-10-31, owner decision: Windows only; "I want pure windows all")`

## Timeline (section 3)
| 2026-10-31 | Vision Studio made Windows only on Avalonia; one compact density for every role (touch, keyboard and mouse); dense VisionPro-style Engineer workspace; chart rules; clickable mock-up of both screens | 2, 6, 14 |

## Decisions (section 4)
| Platform | Windows 10/11 x64 only (owner, 2026-10-31: "I want pure windows all ... Current design platform to work on mac and windows is not upto mark so drop that idea"). The Mac may still build and run the app with the runtime in console mode as a development aid while no Windows PC exists; nothing is released or accepted on it | 14 |
| UI toolkit | Avalonia kept (owner, 2026-10-31: "yes go ahead with avalonia"), for its rendering, docking and property-grid modules and the screens already built; no WPF rewrite | 14 |
| Later work | Browser dashboard, plant server and mobile app are developed later; they stay in the design (owner, 2026-10-31) | 3, 6 |
| Screen density | One compact density for every role: controls 32 px, rows 24-28 px, Run actions 40 px with icon, label and shortcut, text 13 px; no 64/96 px touch buttons (owner, 2026-10-31: "Do not keep too big buttons and size for operators also as they also need so many things in one screen (we may provide keyboard and mouse also with touch screen)") | 6 |
| Engineer workspace | Dense, VisionPro-style: job tree and toolbox, image buffers in tabs, property grid with search and Essential/All, tool result with a margin chart, docked Results, Image set, Cycle time, PLC I/O and Messages; layout saved per user (owner, 2026-10-31: "For engineers dense screen with all required option is good") | 6 |
| Charts | One scale per chart, thin marks, limits and budgets drawn as lines, current value marked, tooltip on every mark; Operator Overview tab with parts per minute, why parts were NG and cycle-time spread (owner, 2026-10-31: "more professional graphics and charts, graphs to be used for easy view and understanding") | 6 |

## Update log (section 8)
| 2026-10-31 | UI-UX-SPEC.md principles 2-4 and 15, sections 2, 4.1, 4.7 (superseded by 4.19), new 4.19, 7, 9; ARCHITECTURE.md 2.4 Windows only, 9, 10; requirements sections 2, 6, 14 and the platform open question; new design/prototypes/vision-studio/index.html | Claude (Mac, branch claude/studio-windows-dense) |

## Links (section 6)
| Vision Studio Workbench mock-up | design/prototypes/vision-studio/ (source); https://claude.ai/artifact/6iyprshYpS2WjnvEL4Y1Wo | Clickable: Operator Run and Engineer workspace, 2026-10-31 |

## Pending
- Vision Studio compact density: change design/tokens/vision-tokens.json sizes and fonts (touch 32, topBar 40, actionBar 40, body 13) and regenerate the Avalonia resources; Run screen to the multi-panel layout with the Overview charts; layout tests at 1366 x 768.
- Vision Studio Engineer workspace in Avalonia: docking (Dock.Avalonia), property grid, job tree, image buffers, bottom docks, saved layout per user; it replaces the separate job designer page.
- Vision Studio Windows only: target net10.0-windows, drop the macOS paths from the release build and the installer, keep the Unix socket only as a development aid.
