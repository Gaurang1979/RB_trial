# Setup checks: refuse at save a job that can never pass (unreadable Read text format, ITF without a length lock, other unpassable tool settings)

- Status: done
- Session: job-setup-checks, 2026-10-30
- Pull request: #94
- Section 5 item: new
- Components/files: dotnet/VisionCore (Jobs/Job.cs, Tools/CodeTool.cs, Tools/TextTool.cs, Tools/TextFormat.cs), dotnet/VisionStudio (Program.cs, i18n/en.json), dotnet/VisionCore.Tests (JobSetupTests.cs new)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #94: Job.Validate refuses at save a Read text format the reader can never read (a printed character not in the AI charset or taught font, a digit field without all ten digits, a month with no readable spelling, a format too big for the engine's decoder), fixed Check text characters the reader lacks, empty template lines, strictness outside 1-99, no code type, a reversed or negative length lock, and a Find the part lowest match outside 0-1; the ITF rule was already on main from PR #60; 9 tests) Setup checks: refuse at save a job that can never pass

## Timeline (section 3)
| 2026-10-30 | A job that can never pass is refused at save with a sentence for the engineer: Read text formats the reader cannot read or the decoder cannot hold, Check text fixed characters the reader lacks, out-of-range strictness, code length lock and type, Find the part match | TOOL-SPECIFICATIONS.md 5.1, 7.4; requirements section 12 |

## Decisions (section 4)
| Reader characters checked at save only | The AI charset (ModelPaths, given to JobStore) and a taught font's characters are checked in Job.Validate, not when a Text tool starts: the Teach page runs a font still being taught, and a character not yet taught is explained on the part (explain.ocr.unknown_char). A digit field needs all ten digits, as a lot or date may print any of them | TOOL-SPECIFICATIONS.md 7.4 |
| Decoder size at the widest line | A format is refused when its decoder nodes times 512 columns (a 2048 px line at 32 px high) exceed the engine's 16e6 cells, or it has more than 1024 places: a line the format allows could otherwise never be read | TOOL-SPECIFICATIONS.md 7.4 |

## Update log (section 8)
| 2026-10-30 | VisionStudio i18n/en.json: explain.code.setup and explain.format.setup texts added | Claude (Mac, branch claude/job-setup-checks) |

## Pending
- Refuse at save a Pharmacode code tool without a value range once PR #84 (Pharmacode and DotCode) is merged.
- The Avalonia Studio (dotnet/Studio, PR #86) should create JobStore with ModelPaths, so the AI reader's characters are checked when a job is saved there.
