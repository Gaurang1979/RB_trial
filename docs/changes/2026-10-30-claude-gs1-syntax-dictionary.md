# GS1 check: the AI table and its association rows extended to the full GS1 Syntax Dictionary

- Status: done
- Session: gs1-syntax-dictionary, 2026-10-30
- Pull request: #123
- Section 5 item: GS1 check: the AI table covers the common AIs only
- Components/files: dotnet/CodeReader (Gs1Syntax.cs and Gs1Dictionary.g.cs new; Gs1Check.cs, Gs1Associations.cs, Gs1Data.cs), dotnet/CodeReader.Tests (Gs1DictionaryTests.cs new; Gs1AssociationTests.cs, Gs1CheckTests.cs), tools/gs1 (new: generator, dictionary, code lists), docs/vision-system/TOOL-SPECIFICATIONS.md 6.4
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #123: the AI table and its rules generated from the GS1 Barcode Syntax Dictionary, release 2026-01-27 (224 rows, every AI of the General Specifications), with each AI's components (format, fixed or variable length, optional parts), their checks (check digits, GMN check pair, dates, times, country and currency codes, IBAN, piece of total and the rest; coupon codes 8110/8112 type and length only), all mandatory associations and invalid pairs, Digital Link qualifier sequences and data attributes; `tools/gs1/gen_gs1_table.py` refreshes it; 59 new test cases) GS1 check: the AI table covers the common AIs only; extend it (and its association rows) with the full GS1 Syntax Dictionary (for example 235, 253, 255, 401, 402, 426, 8010, 8017, 8026) when a customer needs them.

## Timeline (section 3)
| 2026-10-30 | GS1 check: AI table, component checks, association rules and Digital Link keys generated from the GS1 Barcode Syntax Dictionary (release 2026-01-27, all 224 rows); product matching (Gs1Data) reads the same AIs; CodeReader tests 204 pass | TOOL-SPECIFICATIONS.md 6.4 |

## Decisions (section 4)
| GS1 table from GS1's dictionary | The AI table and its rules are generated from GS1's own Barcode Syntax Dictionary (Apache-2.0) by `tools/gs1/gen_gs1_table.py` into `Gs1Dictionary.g.cs`, not typed by hand: a refresh is `fetch --release <tag>`, generate, test. The dictionary file and the code lists (ISO 3166, ISO 4217, package types, from GS1's syntax engine) are kept in `tools/gs1/` so generating needs no network; the release is pinned (2026-01-27), not GS1's moving main | TOOL-SPECIFICATIONS.md 6.4 |
| Stricter rules from the dictionary | The dictionary's rules apply as written, also where they are stricter than the hand-made table: for example 37 needs an SSCC (00) as well as 02 or 8026, 21 also accepts 03, a Digital Link query takes only AIs marked as data attributes. A code breaking them fails (doubt is NG) | TOOL-SPECIFICATIONS.md 6.4, Requirements 12 |
| A check the C# does not know fails | A dictionary check ("linter") the C# code does not make fails the code ("cannot be checked") and fails the test GeneratedTableIsComplete, so a refresh that adds one is caught before a line runs it. Only the North American coupon codes (8110, 8112) are left at type and length, by name | TOOL-SPECIFICATIONS.md 6.4, Requirements 12 |
| GS1 Company Prefix not looked up | gcppos1/gcppos2 check that four digits are there, as GS1's syntax engine does without its online prefix list | TOOL-SPECIFICATIONS.md 6.4 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md 6.4: the AI table from the GS1 Barcode Syntax Dictionary (components, checks, rules, Digital Link sequences) | Claude (Mac, branch claude/gs1-syntax-dictionary) |
