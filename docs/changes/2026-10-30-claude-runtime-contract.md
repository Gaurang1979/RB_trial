# Hybrid 0: runtime gRPC contract (proto/) for Vision Studio and the runtime service, with a stub runtime

- Status: done
- Session: Claude desktop (hybrid-app), 2026-10-30
- Pull request: #85
- Section 5 item: new
- Components/files: proto/visionstudio/remote/v1/runtime.proto (new), dotnet/RuntimeContract (new), dotnet/RuntimeStub (new), dotnet/RuntimeContract.Tests (new), README.md (layout rows, one section)
- Depends on: #72 (design wording only)

## Section 5
- [x] (done 2026-10-30, PR #85: proto/visionstudio/remote/v1/runtime.proto, dotnet/RuntimeContract, vs-runtime-stub, 14 tests) Hybrid 0: runtime gRPC contract between the runtime service and Vision Studio, the browser dashboard and plant software, with a stub runtime for building Vision Studio on the Mac.

## Timeline (section 3)
| 2026-10-30 | Runtime gRPC contract (Session and VisionRuntime services, SDK-API 11.1 kept field for field plus WatchStatus, Start, Stop, WatchFrames, GetCounters, ListUsers, error keys), local endpoint over named pipe or Unix socket, access rules in one place, stub runtime with made-up inspections and a connection-drop switch; 14 unit and socket tests | ARCHITECTURE 2.1-2.4, SDK-API 11 |

## Decisions (section 4)
| Runtime contract shape | One package, visionstudio.remote.v1, for Vision Studio, the dashboard and plant software, extending SDK-API 11.1 without renumbering; products are a job's variants (ListJobs, ChangeJob); refusals carry error_key for the string table; colours are design-token keys; session token in `authorization: Bearer` | ARCHITECTURE 2.1-2.2 |
| Access rules | RuntimeAccess: browser never operates or changes setup; plant software operates only with an operate token and never changes setup; in Vision Studio every role operates and Supervisor, Engineer, Admin and QA change setup. The runtime applies them; clients only grey out buttons | ARCHITECTURE 2.2 |
| .NET 10 on the Mac | .NET 10 SDK 10.0.403 installed in ~/.dotnet next to 8.0.426 with Microsoft's dotnet-install script (owner approved, 2026-10-30); use DOTNET_ROOT=$HOME/.dotnet | 14 |

## Update log (section 8)
| 2026-10-30 | README: layout rows for proto/, RuntimeContract and RuntimeStub; section "Runtime contract and stub runtime" | Claude (Mac, branch claude/runtime-contract) |

## Pending
- ListUsers without a token must be answered only on the local endpoint in the real runtime (Hybrid 1); the stub listens only locally.
- GetImage (full resolution) and SetInput are stubs; the real runtime serves them in Hybrid 1.
