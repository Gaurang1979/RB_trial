# Code Verify: GS1 data validation (tool spec 6.4): required AIs, check digits, real dates, expiry rules on decoded codes

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #67
- Section 5 item: new
- Components/files: dotnet/CodeReader (Gs1Check.cs new), dotnet/CodeReader.Tests (Gs1CheckTests.cs new), dotnet/VisionCore (Tools/CodeTool.cs, Jobs/Job.cs), dotnet/VisionCore.Tests (CodeGs1Tests.cs new), dotnet/VisionStudio (Teach page, i18n/en.json), docs/vision-system/TOOL-SPECIFICATIONS.md 6.4
- Depends on: none

## Section 5
- [x] (done 2026-10-29, PR #67: "Check GS1 data" on the Read code tool: GS1 AI table, value kinds and lengths, separators, check digits, real dates, expiry not before production and not past, required AIs, expected AI values from batch fields; switched on by teaching for GS1 codes; a job with an unprotected ITF or bad GS1 rules is not saved; 23 + 8 tests) Code Verify: GS1 data validation (tool spec 6.4): required AIs, check digits, real dates, expiry rules on decoded codes.
- [ ] Owner: which licence carries GS1 data validation: Code Verify (VS-VER-, as the specification lists it) or Read code (VS-COD-, where it is built today)?

## Timeline (section 3)
| 2026-10-29 | GS1 data validation on the Read code tool: every decoded code's GS1 data checked (AIs, lengths, check digits, dates, expiry, required AIs, batch values); a job with unsafe code tool settings is not saved | TOOL-SPECIFICATIONS.md 6.4 |

## Decisions (section 4)
| GS1 check in .NET first | Built on the Read code tool in dotnet/CodeReader (Gs1Check) so lines get it now; the move into the C++ core comes with the Code Verify parent tool | TOOL-SPECIFICATIONS.md 6.4 |
| GS1 problems fail | Any problem in a code's GS1 data fails the code (never repaired); an expiry already past fails by default; the human-readable "(01)..." form inside a symbol is a print defect and fails | Requirements 12 |
| Review before ready | Opus review found printed "(01)..." text in a symbol, a NUL in a value and an expired 7003 all passing, 410/412/414 wrongly needing a separator, day 00 wrongly refused for production dates, teaching switching the check on for EAN-13s and ITF-14s, and a rejected Teach entry looking applied; all fixed with tests | TOOL-SPECIFICATIONS.md 6.4 |

## Pending
- Engine C API: report the symbology identifier (or zxing's GS1 content flag), so the GS1 check fails a symbol that does not start with FNC1.
- GS1 check: association rules (for example 10 or 17 only with 01 or 02), Digital Link path order, and the move into the C++ core with the Code Verify parent tool.
