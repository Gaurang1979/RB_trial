# GS1 check: association rules (invalid pairs, mandatory associations) and GS1 Digital Link URI parsing with path order checks

- Status: done
- Session: gs1-associations (Claude, side session of local_9493bc10), 2026-10-30
- Pull request: #73
- Section 5 item: new
- Components/files: dotnet/CodeReader (Gs1Associations.cs new, Gs1Check.cs), dotnet/CodeReader.Tests (Gs1AssociationTests.cs new, Gs1CheckTests.cs), docs/vision-system/TOOL-SPECIFICATIONS.md 6.4
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #73: GS1 General Specifications association rules (mandatory associations such as 10/17 only with 01, 02 or 8006, 21 only with 01 or 8006, 02 and 37 together; invalid pairs 01+02, 01+37, 01+8006, one measure with two decimals) and GS1 Digital Link structure (primary key first, its qualifiers in order, attributes only in the query, short GTINs padded); named required set "pharma" (01, 17, 10, 21); all as tables in Gs1Associations.cs; 39 new test cases) GS1 check: association rules (invalid pairs, mandatory associations) and GS1 Digital Link URI parsing with path order checks.
- "GS1 check: association rules": replace `association rules (for example 10 or 17 only with 01 or 02), Digital Link path order, and the move` with `the move` (association rules and Digital Link path order done in PR #73)

## Timeline (section 3)
| 2026-10-30 | GS1 check: association rules between AIs and GS1 Digital Link path order checked on every decoded code; a code breaking either fails | TOOL-SPECIFICATIONS.md 6.4 |

## Decisions (section 4)
| GS1 associations always on | The General Specifications' association rules are not a job option: with "Check GS1 data" on, a code breaking them fails (`explain.gs1.association`), like any other GS1 problem; Digital Link structure problems fail as syntax | TOOL-SPECIFICATIONS.md 6.4, Requirements 12 |
| GS1 rules as tables | Mandatory associations, invalid pairs and Digital Link keys with their qualifier order are data tables (Gs1Associations.cs), so the move into the C++ Code Verify parent tool copies them unchanged | TOOL-SPECIFICATIONS.md 6.4 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md 6.4: association rules, Digital Link structure, the "pharma" set and `explain.gs1.association` | Claude (Mac, branch claude/gs1-associations) |

## Pending
- GS1 check: offer the "pharma" required set (01, 17, 10, 21) on the Teach page's "Must have AIs".
- GS1 check: the AI table covers the common AIs only; extend it (and its association rows) with the full GS1 Syntax Dictionary (for example 235, 253, 255, 401, 402, 426, 8010, 8017, 8026) when a customer needs them.
