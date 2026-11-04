# Setup checks: refuse a Pharmacode code tool without a value range, and an OCR format that can never read

- Status: done
- Session: setup-checks-pharmacode-ocr, 2026-10-30
- Pull request: #124
- Section 5 item: Refuse at save a Pharmacode code tool without a value range; Refuse at setup a format that can never read (pending items of docs/changes/2026-10-30-claude-job-setup-checks.md and docs/changes/2026-10-30-claude-ocr-format-decoding.md, not yet rolled up)
- Components/files: dotnet/VisionCore/Tools/CodeTool.cs (SetupProblem), dotnet/VisionCore/Tools/TextTool.cs (constructor), dotnet/VisionCore.Tests/CodePharmacodeTests.cs, dotnet/VisionCore.Tests/JobSetupTests.cs
- Depends on: none

Most of both items was already on main: PR #84 refuses a Pharmacode tool without its bar count or its expected value, and PR #94 refuses at save a Read text format with a character the reader lacks or too big for the engine's format decoder. This change closes what was left.

## Section 5
- [x] (done 2026-10-30, PR #124: a Pharmacode tool's bar count and expected value were already required (PR #84); now a fixed expected value outside 3-131070, written with a leading zero or spaces, or with a bar count outside the tool's lock is refused at save; 1 test) Refuse at save a Pharmacode code tool without a value range once PR #84 (Pharmacode and DotCode) is merged.
- [x] (done 2026-10-30, PR #124: refused at save since PR #94; now also when the line builds the tool: an AI-reader Read text format or Check text fixed text with a character the model's charset lacks is a setup error with its reason (explain.format.setup), so a job saved without the models is not NG on every part unexplained; the decoder-size limit was already a setup error there; 1 test) Refuse at setup a format that can never read (a literal the reader's charset lacks, or so many {?} places that the line exceeds the decoder's size): today it is NG on every part.

## Timeline (section 3)
| 2026-10-30 | Setup checks: a Pharmacode expected value the engine can never report (out of 3-131070, leading zero, bars outside the lock) is refused at save; an AI-reader format or fixed text with a character outside the model's charset is a setup error when the line builds the tool, not only at save | TOOL-SPECIFICATIONS.md 5.0, 7.4; requirements section 12 |

## Decisions (section 4)
| AI reader's characters checked at setup too | The AI model's charset is fixed, so the tool checks it when built (a job saved by a store without the models, or before PR #94, reaches the line unchecked); a taught font is still checked at save only, as the Teach page runs a font still being taught. The part fails with explain.format.setup and the reason, as before NG, never read under the format | TOOL-SPECIFICATIONS.md 7.4; requirements section 12 |
| Pharmacode expected value checked only when fixed | A value with a batch field is filled at batch start and compared when read; a fixed value must equal the decimal the engine reports, so "012345" or " 12345" can never pass and is refused | TOOL-SPECIFICATIONS.md 5.0 |

## Update log (section 8)
| 2026-10-30 | No new explain keys or strings: the refusals use explain.code.setup and explain.format.setup (VisionStudio i18n/en.json, the only string table there today) | Claude (Mac, branch claude/setup-checks-pharmacode-ocr) |
