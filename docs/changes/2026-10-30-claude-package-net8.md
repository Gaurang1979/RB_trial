# Vision Package client library for .NET 8 and .NET 10 hosts

- Status: done
- Session: Claude desktop (reader-package), 2026-10-30
- Pull request: #116
- Section 5 item: Requirements: other applications such as the aggregation software
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (section 10, section 15 open question), docs/vision-system/SDK-API.md (section 0.1, open questions); design only, no code
- Depends on: #114, #115 (merged)

## Section 5
- Follow-up 2026-10-30: the aggregation software runs on .NET 8 or newer, so the Vision Package's client library is built for net8.0 and net10.0 and no .NET Framework build is needed; the worker runs self-contained on .NET 10.

## Decisions (section 4)
| Vision Package .NET targets | Owner, 2026-10-30: the aggregation software runs on .NET 8 or newer. The client library `VisionStudio.Package.dll` is built for `net8.0` and `net10.0` in one NuGet package; the `net8.0` build is dropped once the aggregation software moves to .NET 10 (.NET 8 support ends 2026-11-10). The worker is self-contained on .NET 10, so the host's .NET never changes how parts are read | 10, 15; SDK-API 0.1 |

## Update log (section 8)
| 2026-10-30 | Requirements section 10 (delivery line) and section 15 (open question answered); SDK-API.md section 0.1 (package tree, .NET targets) and open questions | Claude (Mac, branch claude/package-net8) |

## Pending
- Drop the Vision Package's net8.0 client build once the aggregation software runs on .NET 10 (.NET 8 support ends 2026-11-10).
