# Code Reader: fail a case with more codes than expected

- Status: done
- Session: count-over, 2026-10-31
- Pull request: #163
- Section 5 item: Code Reader: fail a case with more codes than expected
- Components/files: dotnet/VisionCore/Tools/CodeTool.cs (Judge), dotnet/CodeReader/CodeReader.cs (DecodeStats.Extra), dotnet/Dashboard/Services/Station.cs and HardCases.cs, dotnet/LineRunner/Program.cs, docs/vision-system/TOOL-SPECIFICATIONS.md; tests in VisionCore.Tests, CodeReader.Tests, Dashboard.Tests
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #163: more codes than expected is NG in the Read code tool ("13 codes read, 12 expected: a foreign or extra pack in the case", explain.code.extra), the Dashboard aggregation station (reason codes_extra) and LineRunner (plant reason child_codes/extra); expected count 0 sets no upper limit; a per-code problem such as a wrong text is still named first) Code Reader: fail a case with more codes than expected (today only fewer fails, dotnet/VisionCore/Tools/CodeTool.cs) (study 04b)

## Timeline (section 3)
| 2026-10-31 | Over-count is NG: Read code tool, Dashboard aggregation station and LineRunner fail a case with more codes than the product master or tool expects (foreign or extra pack); DecodeStats.Extra; explain.code.extra in the tool specification. VisionCore.Tests CodeCountTests (6), CodeReader.Tests DecodeStatsTests (1), Dashboard.Tests reasons test extended | 12 |

## Decisions (section 4)
| Over-count | More codes than expected fails the case (line safety: an extra or foreign pack must not pass). It is red NG, not orange: orange stays for a duplicate code (owner, 2026-10-23), and the extra code cannot be told from the right ones, so no single code is marked. A per-code problem (wrong text, GS1, grade) is named first, as it says more. Expected count 0 means unknown and sets no upper limit (Auto-find probes). DecodeStats.Complete keeps its meaning (every expected code read) for trends and the advisor; Extra is a separate flag | 12 |
| Two copies of one code | Not counted as extra: the engine merges reads of the same text (Copies), and some labels carry the code twice by design (learned symbolsPerCode) | 12 |

## Update log (section 8)
| 2026-10-31 | TOOL-SPECIFICATIONS Read code pass/fail: count read = expected count; explain.code.extra added | Claude (Mac, branch claude/count-over) |

## Pending
- Engine: decoding stops once the expected count is read (Found::done in engine/src/decode.cpp), so an extra pack is caught only when its code is read before the count is reached (the whole-image pass reads up to 2 x count + 4). Catch it every time: after the count is reached, still decode plausibly sized regions left, within the time budget, or fail when more such regions are found than codes expected
- An extra pack carrying a copy of a code already in the same case merges with it in the engine (Copies) and is not seen; flag a case whose copies per code exceed the product's learned symbolsPerCode
