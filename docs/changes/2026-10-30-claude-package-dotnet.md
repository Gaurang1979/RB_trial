# Vision Package host language: .NET (C#) first; C API later on request

- Status: done
- Session: Claude desktop (reader-package), 2026-10-30
- Pull request: #115
- Section 5 item: Requirements: other applications such as the aggregation software
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (section 10, section 15 open question), docs/vision-system/SDK-API.md (section 0, open questions), docs/changes/2026-10-30-claude-reader-package.md (two pending lines); design only, no code
- Depends on: #114 (merged)

## Section 5
- Follow-up 2026-10-30: the aggregation software is written in .NET (C#), so the Vision Package ships for .NET only; a C API for other languages comes later, on request. Still open: which .NET the aggregation software runs on (the client library targets .NET 10).

## Decisions (section 4)
| Vision Package host language | Owner, 2026-10-30: the aggregation software is .NET (C#). The package ships as a NuGet package for .NET hosts only; the C API (`vs_package.dll`) is built later, on request. A .NET Framework 4.x host would need a `netstandard2.0` build of the client library | 10, 15; SDK-API 0.1 |

## Update log (section 8)
| 2026-10-30 | Requirements section 10 (delivery, options table, package requirements) and section 15 open question; SDK-API.md section 0.1, 0.2 and open questions; pending lines of the reader-package note: .NET (C#) host first | Claude (Mac, branch claude/package-dotnet) |
