# Vision Studio: Read text month-name languages setting, passed to Teach

- Status: done
- Session: studio-month-languages, 2026-10-30
- Pull request: #121
- Section 5 item: Vision Studio: a setting for the Read text tool's month-name languages
- Components/files: dotnet/VisionStudio (Teach page, Services/MonthLanguages.cs, i18n/en.json), dotnet/VisionStudio.Tests
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #121: Teach page, Read text mode, has a "Month names (MMM) in" row with a tick box per language of TextFormat.MonthNames, shown in the language's own name; Auto find passes the ticked languages to TextFormat.Propose and the format rows are checked with them) Vision Studio: a setting for the Read text tool's month-name languages (TextToolConfig.MonthLanguages, today set in the job file only) and Teach passing them to TextFormat.Propose.

## Timeline (section 3)
| 2026-10-30 | Vision Studio Teach: month-name languages of the Read text tool set with tick boxes; Auto find proposes MMM dates in those languages | TOOL-SPECIFICATIONS 7.4 |

## Decisions (section 4)
| Month language names in the Teach page | Each language shows its own name (Español, Deutsch) from .NET's culture data, so the row needs one new string only; unticking the last language leaves English, as an empty list reads English anyway; an unknown code in a job file is left out of Auto find (the setup check already reports it) so Teach never fails on it | TOOL-SPECIFICATIONS 7.4 |

## Update log (section 8)

## Pending
- The new Vision Studio string "text.month_languages" in the other 14 languages (Vision Studio has only en.json today).
- The Avalonia Studio (dotnet/Studio) has no Read text tool panel yet; when it gets one, add the month-name languages setting there too.
