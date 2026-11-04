# OCR: Check date / lot (format validation): lot and code patterns, real dates, expiry from production date, date near today

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #61
- Section 5 item: new
- Components/files: dotnet/VisionCore (Tools/TextFormat.cs new, Tools/TextTool.cs, Jobs/Job.cs), dotnet/VisionCore.Tests (TextFormatTests.cs new), dotnet/VisionStudio (Teach page, i18n/en.json), docs/vision-system/TOOL-SPECIFICATIONS.md 7.4
- Depends on: #60 merges first (both change Teach.razor, i18n/en.json and TOOL-SPECIFICATIONS.md; different sections)

## Section 5
- [x] (done 2026-10-29, PR #61: Read text lines checked against formats with digit, letter, code and date fields; real dates; expiry not before production and not past, expiry = production + N months, production date near the picture's time; teaching proposes formats and rules; 36 tests) OCR: Check date / lot (format validation): lot and code patterns, real dates, expiry from production date, date near today.
- [ ] Owner: in Read text mode, should a read line that no format claims (extra print, a double strike) make the part NG? Today it is not checked.
- [ ] Owner: should the production-date window (printer clock check) be on for every Read text tool with an MFD, not only when teaching sees today's date?

## Timeline (section 3)
| 2026-10-29 | OCR Read text mode checks what is printed: lot and code formats, real dates, expiry after production and not past, expiry = production + N months, production date near today; formats and rules proposed at teaching | TOOL-SPECIFICATIONS.md 7.4 |

## Decisions (section 4)
| Read text passes only on a format | A Read text tool with format lines passes only when every format fits its own line, read at or above the minimum quality, with real dates that keep the rules; a read character is never corrected (an O where a digit belongs is NG). Every line of a Read tool is a format, so a template left from Check mode fails as a setup error instead of going unchecked (upgrade note: such jobs must be taught again) | TOOL-SPECIFICATIONS.md 7.4 |
| Date rules always on | Expiry not before production and not before the picture's date are always checked; every date must be named MFD or EXP once per tool, so no date escapes the rules; YY means 20YY | TOOL-SPECIFICATIONS.md 7.4 |
| Review before ready | Opus review found teaching naming every line's date MFD (an expiry before production passed), no clock or past-expiry check, expiry compared only by month when both dates had a day, a {re:} field that could crash the Teach page, prices proposed as dates and labels lost in "MFD29/10/26"; all fixed with tests | TOOL-SPECIFICATIONS.md 7.4 |

## Pending
- Month names in other languages for MMM dates; format-constrained decoding in the OCR engine (lexicon-limited reading from the format).
- Job.Validate: refuse an ITF code tool without a length lock, check digit or expected text (CodeTool.SetupProblem from PR #60) once both are merged.
