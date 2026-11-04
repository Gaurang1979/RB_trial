# Pharma required-AI preset (01, 17, 10, 21) on the Teach page's "Must have AIs"

- Status: done
- Session: pharma-ai-preset, 2026-11-03
- Pull request: #286
- Section 5 item: new (Pending of docs/changes/2026-10-30-claude-gs1-associations.md); Req 17.13 GS1-PH-01
- Components/files: dotnet/VisionStudio (Teach.razor, Services/Gs1RequiredPresets.cs, i18n/en.json), dotnet/Studio (ViewModels/JobDraft.cs, Views/DesignerView.axaml, i18n/en.json), tests in VisionStudio.Tests and Studio.Tests
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #286: a "Pharma" button beside the Teach page's "Must have AIs" (and "Pharma set" in the Avalonia Studio code panel) fills the existing required-AIs setting with 01, 17, 10, 21; the field stays editable; English strings only) GS1 check: offer the "pharma" required set (01, 17, 10, 21) on the Teach page's "Must have AIs".

## Timeline (section 3)
| 2026-11-03 | Pharma preset for the GS1 required AIs on the Teach page and the Avalonia code panel; 5 new tests, VisionStudio.Tests 91 passed, Studio.Tests designer/areas 12 passed | 17.13 GS1-PH-01 |

## Decisions (section 4)
| Pharma preset only fills the field | It writes the same `Gs1.Required` the engineer types, so checks and NG behaviour are unchanged and the set stays editable | 17.13 GS1-PH-01 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.13 GS1-PH-01 added | Claude (Mac, branch claude/wizardly-chebyshev-3d9fda) |

## Pending
- The 15 languages for the new strings `code.gs1_pharma`, `code.gs1_pharma_hint` (VisionStudio) and `design.gs1_pharma`, `design.gs1_pharma_hint` (Studio); English only here, as asked.
