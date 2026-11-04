# Upgrade .NET 8 to .NET 10 LTS in every line-PC project and the Windows build before .NET 8's end of support on 2026-11-10

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #64
- Section 5 item: Upgrade .NET 8 to .NET 10 LTS in every line-PC project
- Components/files: all 15 dotnet/**/*.csproj (net10.0, packages), .github/workflows/windows-build.yml (10.0.x), README.md, docs/CODING-STANDARDS.md, docs/vision-system (ARCHITECTURE, SDK-API, PLANT-DASHBOARD-AND-MOBILE), tools/windows/build-windows.bat, dotnet/Dashboard/Program.cs (static web assets)
- Depends on: #63 (merge #63 first: it adds this item and DEP-03)

## Section 5
- [x] (done 2026-10-29, PR #64: all 15 projects on net10.0 with Microsoft.Data.Sqlite 10.0.14, Hosting.WindowsServices 10.0.14, Microsoft.NET.Test.Sdk 18.11.0, xunit 2.9.3 and xunit.runner.visualstudio 4.0.1; the Windows build on the 10.0.x SDK; the Dashboard serves the .NET 10 Blazor script from a build folder too; a NuGet security advisory warns instead of stopping the build) Upgrade .NET 8 to .NET 10 LTS in every line-PC project and the Windows build before .NET 8's end of support on 2026-11-10, with Microsoft.Data.Sqlite 10, Hosting.WindowsServices 10, Microsoft.NET.Test.Sdk 18 and xunit 2.9.3.

## Timeline (section 3)
| 2026-10-29 | The line PC software moves to .NET 10 LTS (supported to 2028-11-14) ahead of .NET 8's end of support on 2026-11-10 | REQUIREMENTS 13 DEP-03, 14 |

## Decisions (section 4)
| NuGet security advisories | Since .NET 10 NuGet audits every package, direct or not; in the projects that treat warnings as errors an advisory (NU1901-NU1904) stays a warning, so a new advisory never blocks an urgent line build; the monthly dependency watch acts on it | REQUIREMENTS 13 DEP-03 |
| VisionStudio /api/health without sign-in | .NET 10 answers 401 instead of redirecting to the login page for API endpoints; a monitor that followed the redirect saw a 200 login page as "healthy", so the new answer is kept | REQUIREMENTS 12 |
| .NET version | The line PC software targets the current .NET LTS release, .NET 10; the next move is to .NET 12 LTS after its first bug-fix release (DEP-03) | REQUIREMENTS 14 |

## Update log (section 8)
| 2026-10-29 | README, CODING-STANDARDS, ARCHITECTURE, SDK-API and PLANT-DASHBOARD-AND-MOBILE: .NET 8 to .NET 10 | Claude (cloud, branch claude/dotnet-10) |

## Pending
- 7-day soak on .NET 10: Server GC now adapts the heap (DATAS); check StationGuard's memory budget and leak floors, and if they misbehave set GarbageCollectionAdaptationMode 0 in VisionStudio and the Dashboard.
- Move the line PCs' installers and the Windows service install notes to the .NET 10 runtime (self-contained builds already carry it).
