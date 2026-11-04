# Hybrid line PC software: vision runtime service, Avalonia Vision Studio app (VisionPro-like), view-only browser dashboard

- Status: done
- Session: Claude desktop (hybrid-app), 2026-10-30
- Pull request: #72
- Section 5 item: new
- Components/files: docs/vision-system/ARCHITECTURE.md, docs/vision-system/UI-UX-SPEC.md, docs/vision-system/SDK-API.md, docs/REQUIREMENTS-VISION-SYSTEM.md (design only, no code)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #72: design in ARCHITECTURE.md sections 2.2-2.4 and 9, UI-UX-SPEC.md principle 14 and section 4.17, SDK-API.md section 11.3) Hybrid line PC software design: one runtime service, one Windows application (Vision Studio, Avalonia), view-only browser dashboard.
- [ ] Hybrid 1: vision runtime service: move Station, cameras, jobs, the plant link and the code-reader station out of dotnet/VisionStudio and dotnet/Dashboard into one vs-runtime host (Windows service or --console) with gRPC on a named pipe (Unix socket on macOS). Starts after #70 and #71 merge (they change VisionStudio/Services/Station.cs).
- [ ] Hybrid 2: Vision Studio shell in Avalonia: shared design tokens (SecureMe console look, dark default), sign-in, runtime link with reconnect, Run screen with live image and OK / NG.
- [ ] Hybrid 3: setup screens in Vision Studio (Avalonia): Change product, Teach, Jobs, Batch, Test, Products, Tuning, Calibrate, AI models, Users, Settings.
- [ ] Hybrid 4: browser dashboard view-only: Live, Trends, Reports, Audit trail; changing calls refused for browser sessions; REST control only with an operate token.
- [ ] Hybrid 5: one installer for the runtime service and Vision Studio, with a tray icon for the service state.

## Timeline (section 3)
| 2026-10-30 | Hybrid application designed: runtime service, Vision Studio Windows application (Avalonia), view-only browser dashboard, control only from the application or an operate token, shared SecureMe console tokens; build split into five pull requests | 3, 6, 10, 14 |

## Decisions (section 4)
| Application shape | Hybrid, like VisionPro and most camera products: the inspection runs in a local service (Windows service, or a plain program with --console); a full Windows application runs, teaches and sets up the line; the browser shows only the live dashboard and reports (owner, 2026-10-30) | 3, 6 |
| UI toolkit | Avalonia, not WPF: keeps Linux open and runs on the Mac where the application is developed and checked (owner, 2026-10-30) | 14 |
| Control from outside | Browser sessions are read-only whatever the role; plant software (MES, ERP) may start, stop, change job or product, trigger and set inputs with an operate token (the API key); no token edits jobs, products, settings or users (owner, 2026-10-30) | 10, 12 |
| One application | One Windows application and one runtime service for every tool: the code-reader Dashboard and the browser Vision Studio fold into them; the code reader becomes the Code Reader tool (owner, 2026-10-30) | 3, 4 |
| Look | SecureMe console look in the application and the browser: dark default, Light and High contrast, one set of design tokens for both (owner, 2026-10-30) | 6 |

## Update log (section 8)
| 2026-10-30 | ARCHITECTURE.md sections 1, 2 (2.2 control, 2.3 look, 2.4 macOS), 9 and 10; UI-UX-SPEC.md principles 5 and 14, section 4.17 browser dashboard, section 7, open questions; SDK-API.md section 11.3; requirements section 14 and the web HMI line | Claude (Mac, branch claude/hybrid-app) |
