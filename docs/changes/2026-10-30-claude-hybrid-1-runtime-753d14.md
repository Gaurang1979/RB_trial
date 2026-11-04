# Hybrid 1: vision runtime service (vs-runtime host)

- Status: done
- Session: Claude desktop (hybrid-1-runtime), 2026-10-30
- Pull request: #112
- Section 5 item: Hybrid 1: vision runtime service
- Components/files: new dotnet/Runtime.Host (vs-runtime) and dotnet/Runtime.Host.Tests; proto/visionstudio/remote/v1/runtime.proto (Status.health added, backward compatible); dotnet/RuntimeContract.Tests (HealthFieldTests). Not touched: dotnet/VisionStudio/Services/Station.cs (draft #70), dotnet/Studio.
- Depends on: none

## Section 5
- Update: vs-runtime host built (PR #112): one process (Windows service, or --console; runs on macOS) serving the runtime contract on the named pipe / Unix socket with the PR #88 access rules (token-less reads only on the local endpoint, network sign-ins read-only, control only signed in or with an operate token, PIN lock-out per endpoint); real GetImage (recent parts, full resolution when replaying) and SetInput (batch values, only while stopped); cameras through FrameIntake; jobs, counters, PLC link, licence, NG images, results and shift reports, plant link with health.sample cpu_pct and frame_drops; Status.health with the ResourceGuard limits. VisionStudio's Station is reused by project reference; the item stays open for the move below.

## Timeline (section 3)
| 2026-10-30 | vs-runtime host (Hybrid 1): runtime contract on the local endpoint with the access rules, the VisionStudio station behind it (replay, camera through FrameIntake, simulator camera), real GetImage and SetInput, counters, alarms, health with cpu_pct and the guard limits (Status.health, health.sample); runtime users and plant tokens as salted hashes; 18 runtime tests and 1 contract test; Avalonia Studio run against it on the Mac with the stub frames | Requirements 12, 13 |

## Decisions (section 4)
| Runtime reuses the station by reference | Until draft #70 merges, vs-runtime uses VisionStudio's Station, PlcService, licence and reports through a project reference instead of moving them, so #70 keeps its file; the move follows | Requirements 12 |
| Network sign-in is read-only | A user signed in over the network is a browser session whatever client it claims; only the local endpoint (pipe or socket) gives a Vision Studio session that may change the line | Requirements 12 |
| Hybrid 1 ready before the line-PC soak | Owner, 2026-10-30: "Mark ready, soak later". PR #112 is marked ready after its Mac tests; the Windows line-PC soak is deferred by this decision, not skipped, and stays open under Pending | Requirements 12 |
| Runtime refusals for line safety | Trigger while stopped or on a PLC-triggered line, and Start of a job with no enabled tool, are refused instead of producing an OK | Requirements 12 |

## Update log (section 8)
| 2026-10-30 | dotnet/Runtime.Host/README.md (new): what vs-runtime runs, its endpoint, access rules and commands | Claude (Mac, branch claude/hybrid-1-runtime-753d14) |

## Pending
- Move Station, PlcService and the line services from dotnet/VisionStudio into dotnet/Runtime.Host after #70 merges, and the Dashboard's code-reader station after it; then drop the VisionStudio project reference.
- Live frames without burned-in graphics (vectors only) and full-resolution GetImage for camera parts: needs the station to hand out each part's image and image id (after the move).
- One user store for vs-runtime, VisionStudio and the Dashboard (vs-runtime keeps runtime-users.json with PINs for now).
- Soak of vs-runtime on a Windows line PC (deferred by the owner, 2026-10-30: "Mark ready, soak later"): service install, named-pipe ACL with Vision Studio as the signed-in user, real cameras through FrameIntake, PLC link, TLS network port.
