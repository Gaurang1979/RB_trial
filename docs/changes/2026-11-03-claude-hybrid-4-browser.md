# Hybrid 4: browser dashboard view-only: Live, Trends, Reports, Audit trail; changing calls refused for browser sessions; REST control only with an operate token.

- Status: done
- Session: Claude cloud (hybrid-4-browser), 2026-11-03
- Pull request: #264
- Section 5 item: Hybrid 4: browser dashboard view-only (docs/changes/2026-10-30-claude-hybrid-app.md)
- Components/files: dotnet/Runtime.Host (WebDashboard.cs, Web/, Access.cs, RuntimeHost.cs, RuntimeOptions.cs, LineRuntime.cs, README.md, vs-runtime.json), dotnet/VisionReports/ResultLog.cs (two read queries), tools/tokens/gen_tokens.py, dotnet/Runtime.Host.Tests/WebDashboardTests.cs, docs/vision-system/UI-UX-SPEC.md 4.17
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #264: vs-runtime serves Live, Trends, Reports and Audit trail on Runtime:WebPort over HTTPS; password sign-in gives a read-only browser session; no changing web route and no gRPC on the dashboard port; REST control with an operate token is not built, see Pending) Hybrid 4: browser dashboard view-only: Live, Trends, Reports, Audit trail; changing calls refused for browser sessions; REST control only with an operate token.

## Timeline (section 3)
| 2026-11-03 | Browser dashboard (Hybrid 4) in vs-runtime: Live (status pill, last image per camera, OK / NG counters, yield ring, median cycle, alarms with "What do I do?", batch values, NG strip with a larger view), Trends (parts per hour OK / NG, yield, average cycle, NG reasons; Shift, Day, Week), Reports (shift, batch and grade report files), Audit trail (batches and line events with a filter); server-sent events for live updates; "Not connected" state with the last image greyed; SecureMe console look from design/tokens with Dark, Light and High contrast per browser; 11 tests | 3, 12, 14 |

## Decisions (section 4)
| Hybrid application | Owner, 2026-11-03: "I want hybrid application for this project. Hybrid is usually the best fit: run the vision processing as a local backend service and standalone app, and use a browser UI to view dashboard." Confirms the design of 2026-10-30 (PR #72): vs-runtime service, Vision Studio (Avalonia) application, view-only browser dashboard | 3, 14 |
| Dashboard port | The dashboard has its own port (Runtime:WebPort, HTTP/1.1 and HTTP/2 over TLS, the runtime's certificate), not the plant-software gRPC port: browsers need HTTP/1.1, and the web routes answer only on that connection (checked by local port, not the Host header) | 12, 14 |
| No changing web route | Every dashboard route is a GET except sign-in and sign-out, so "the browser only watches" does not rest on hidden buttons; a cross-site POST (foreign Origin) is refused; cookie HttpOnly, SameSite=Strict, Secure over HTTPS; strict Content-Security-Policy | 12 |
| Dashboard port serves only the dashboard | Any path other than / and /web/... on the dashboard connection is 404, so the gRPC contract (Start, Stop with an operate token) is never reachable there; the dashboard routes are 404 on the pipe, the socket and the plant-software port | 12 |
| Trends count parts | Parts per hour, yield and NG reasons come from the PLC's part log when it has parts in the range (as the shift report counts them), else one part per inspection; reasons are the part categories or the failing tool, never the free reason text. Each range is worked out at most once a minute | 14 |
| An open dashboard stays signed in | A shown dashboard keeps its session alive (wall and TV displays); sign-out or expiry ends its live stream within 15 s; at most four live streams per session | 12 |
| Browser sign-in | Password only: a PIN or a card number is short and fine at the line PC, too easy to guess over the network. Wrong tries count with the network sign-ins (five, then a minute's wait). A Vision Studio or plant token never opens the pages | 12 |
| Trends from the results log | Inspections per hour aggregated in SQLite (a week at line speed is a few hundred rows), shown in the browser's time; a week is shown per day | 14 |

## Update log (section 8)
| 2026-11-03 | dotnet/Runtime.Host/README.md: the browser dashboard; UI-UX-SPEC.md 4.17: where it is served; ARCHITECTURE.md 2.4: the browser dashboard row | Claude (cloud, branch claude/hybrid-4-browser) |

## Pending
- Browser dashboard audit trail: sign-ins, starts, stops, saves, signatures and job versions with field-by-field changes (UI-UX-SPEC.md 4.17); the runtime logs only batches and line events today.
- Browser dashboard Reports: the read-only tabs of UI-UX-SPEC.md 4.11 (Shift, Trends, Find a part, Operator flags) with Print, Save as PDF and Save as Excel; today it lists and downloads the report files.
- Browser dashboard: the 14 other languages (a per-browser language picker) and the result graphics as vectors over the image instead of burned in.
- Browser dashboard: print-quality (grade) trend from the graded parts' values, and a grade drift alarm (item B of the grading work).
- Browser dashboard: the role limits which lines and reports a user sees (UI-UX-SPEC.md 4.17); today every signed-in user sees every report.
- REST control for plant software with an operate token (Hybrid 4 item text); today plant software uses the gRPC contract on Runtime:NetworkPort.
- Browser dashboard on the line PC: open Runtime:WebPort in the installer's firewall rule and certificate setup, and try it from a tablet on the plant network.
