# Remote service step 4a: vs-runtime hosts the remote-service agent (submodule) in place of SupportLink

- Status: done
- Session: remote-service-agent, 2026-11-04
- Pull request: #345
- Section 5 item: new
- Components/files: third_party/remote-service (new git submodule, Gaurang1979/remote-service); dotnet/Runtime.Host (RemoteService.cs new, SupportLink.cs removed; WebDashboard.cs, Access.cs, RuntimeHost.cs, LineRuntime.cs, Runtime.Host.csproj, Web/app.js, Web/index.html, README); dotnet/VisionReports/ServiceLog.cs new; tests (Runtime.Host.Tests/RemoteServiceTests.cs replaces SupportLinkTests.cs, SupportSignInTests.cs adapted, VisionCore.Tests/ServiceLogTests.cs); support_relay/ removed (now in remote-service); docs/vision-system/REMOTE-SUPPORT.md; docs/REQUIREMENTS-VISION-SYSTEM.md (REQ-11 decision 5); README row
- Depends on: none (remote-service at its main, 12d81c2)

## Section 5
- [x] (done 2026-11-04, PR #345: vs-runtime hosts the remote-service agent from the submodule; sign-in with today's Service code (service) or Factory code (oem); every remote event chained in the service log with its head sent to the relay and ERPNext; read-only folders, diagnostics, data views, ledger check and the dashboard; support_relay and SupportLink removed) Remote service step 4a: vs-runtime hosts the remote-service agent

## Timeline (section 3)
| 2026-11-04 | Remote service 4a: vs-runtime hosts the remote-service agent (git submodule third_party/remote-service). Engineers sign in on the service console with today's Service or Factory code; the dashboard opens with the same session; every remote event is chained in service-log.db (its own HMAC chain and head, ledger key) and its head goes to the relay and ERPNext; read-only reports, NG images and jobs, diagnostics, views of inspections, batches, events and NG reasons, a ledger check. No ledger key, or a service log that fails its check: remote service off, the line runs | REQ-11, REQ-15, PROT-26, PROT-30, PROT-32, REQ-16, PROT-31 |

## Decisions (section 4)
| Remote service module | Owner, 2026-11-04: the remote service module is its own repository (Gaurang1979/remote-service), taken into Vision_System as a git submodule; step 4 comes in three pull requests (4a runtime, 4b Vision Studio confirmations and badge, 4c Windows service installs) | REQ-11 |
| Remote service user log | Every remote event goes into service-log.db, its own HMAC chain with the results ledger's key and row hash and its own head file, not into results.db: the results ledger and its check at start stay exactly as they are, so remote service can never stop the line through them. The events table (Audit trail page) gets a copy; it is not chained | REQ-16, PROT-31 |
| Remote OEM role | The remote role oem needs today's Factory code, the role service today's Service code (PROT-32: service personnel never open factory-tier capabilities) | PROT-32 |
| Remote service start | The agent is built after the runtime has started and its badge is read from memory only, so remote service changes neither the start-up order (line, PLC link, integrity check) nor the inspecting thread's work | Req 12 |
| One sign-in | The engineer signs in once, on the service console; the dashboard trusts the agent's forwarded session (key over loopback) instead of its own Service-code sign-in, which is removed | REQ-11 |

## Update log (section 8)
| 2026-11-04 | REMOTE-SUPPORT.md rewritten for the module (how Vision System hosts it, 4a, next steps); REQ-11 owner decision 5 (one module, its own repository as a submodule, PackageSign key); README row third_party/remote-service in place of support_relay; Runtime.Host README; AGENTS.md step 1: fetch submodules in a new worktree | Claude (Mac, branch claude/remote-service-agent) |

## Pending
- Remote service 4b: Vision Studio shows the neutral "Remote service active" badge (Status.remote_service, 15 languages) and asks the operator to confirm and a supervisor to approve (PIN or card) through the runtime contract; then uploads to jobs and corrections of batch details are switched on.
- Remote service 4c: software installs for the vs-runtime Windows service (stop, swap, start, rollback), packages signed with the PackageSign key; confirm an installed version passes the start-up integrity check.
- Remote service on a Windows line PC: try the link, sign-in, service log and dashboard with the real certificate and DPAPI-protected secrets.
