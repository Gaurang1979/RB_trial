# Remote support portal at support.sundaramtech.com/<machine>: relay, line PC link, session flow (REQ-11, REQ-15, PROT-26, PROT-30)

- Status: done
- Session: remote-support-portal, 2026-11-04
- Pull request: #325
- Section 5 item: new
- Components/files: new `support_relay/` (cloud relay, Python/FastAPI); `dotnet/Runtime.Host/` (SupportLink.cs new; Access.cs, WebDashboard.cs, Web/app.js, Web/index.html, LineRuntime.cs, RuntimeHost.cs, RuntimeOptions.cs, README); `proto/visionstudio/remote/v1/runtime.proto` (Status.remote_service, additive); `dotnet/VisionCore/Licensing/CommissioningSecrets.cs` (VS_SUPPORT_LINK_TOKEN); `dotnet/VisionStudio/Program.cs` (usage line); `dotnet/VisionStudio/Services/OemSignIn.cs` (OtpAttempts file name); `docs/REQUIREMENTS-VISION-SYSTEM.md` (REQ-11 owner decisions, PROT-26 as built); `docs/vision-system/REMOTE-SUPPORT.md` new; README rows
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #325: relay, outbound line PC link, Service-code sign-in through the link only, read-only views, audit on both sides; Status.remote_service) Remote support portal at support.sundaramtech.com/<machine> without remote-desktop software (REQ-11, REQ-15, PROT-26, PROT-30)

## Timeline (section 3)
| 2026-11-04 | Remote support: `support_relay/` (engineer accounts, machine links, forwarding, audit) and the runtime's outbound `SupportLink`; engineers open the line PC's own read-only dashboard at support.sundaramtech.com/<machine>/ with today's Service code; the line's status carries a neutral remote-service flag | REQ-11, REQ-15, PROT-26, PROT-30, PROT-32 |

## Decisions (section 4)
| Remote support route | Owner, 2026-11-04: each line PC connects directly and outbound to support.sundaramtech.com (not through the plant server) | REQ-11 |
| Remote support approval | Owner, 2026-11-04: today's Service code is enough to start a session, with no approval on the line screen; the line shows only a neutral "Remote service active" badge (never who or what, REQ-08) | REQ-11, REQ-15, PROT-30 |
| Remote support scope, step 1 | Owner, 2026-11-04: relay, link, sign-in, session start/end, audit and read-only views first; settings changes in a later pull request | REQ-11 |
| Remote support design | The relay forwards and never decides: engineers get the line PC's own browser dashboard, which checks every call. The Service code is taken only through the link (a key held in memory, loopback only), a customer's password never through it; five wrong codes hold remote sign-in for every engineer for 15 minutes | REQ-11, PROT-26 |

## Update log (section 8)
| 2026-11-04 | New docs/vision-system/REMOTE-SUPPORT.md (row 22 of the design index); README rows for support_relay/ and the runtime's remote support | Claude (Mac, branch claude/remote-support-portal) |

## Pending
- Vision Studio Run screen: show the neutral "Remote service active" badge from Status.remote_service, with its text in the 15 languages (after PR #315, which changes Studio/i18n/en.json).
- Remote settings changes through the relay: staged changes, with the operator's confirmation where line safety needs it.
- Relay, before it is deployed: one origin per machine (<machine>.support.sundaramtech.com or equal isolation; owner to choose the address format), so a line PC with a stolen link token cannot read other machines open in the same browser.
- Relay: two-factor sign-in for engineers, a per-machine certificate instead of the link token, and deployment at support.sundaramtech.com (TLS proxy, backups of the data folder).
- Line audit: record a remote service session that ends by idle time (sign-out and link drop are recorded).
