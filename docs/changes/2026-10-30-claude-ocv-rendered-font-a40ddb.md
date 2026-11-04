# Check text with a rendered system font (no teaching from sample print)

- Status: done
- Session: ocv-rendered-font, 2026-10-30
- Pull request: #108
- Section 5 item: new (Pending in docs/changes/2026-10-30-claude-ocv-shift-plc-inkjet.md: "Rendered system font for Check text (no teaching): not built")
- Components/files: dotnet/VisionCore (Tools/FontRender.cs new, Tools/TextTool.cs, Jobs/Job.cs, VisionCore.csproj: SkiaSharp 2.88.9), dotnet/VisionCore.Tests/RenderedFontTests.cs, dotnet/VisionStudio (Components/Pages/Teach.razor, i18n/en.json), docs/vision-system/TOOL-SPECIFICATIONS.md 7.3. No engine change.
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-30, PR #108: system font drawn in VisionCore and taught to the taught-font reader; refused at save when it cannot print a character) Check text with a rendered system font: the operator types the expected text or format, picks a font and size, and the tool checks print without teaching from sample print (tool spec 7.3).`

## Timeline (section 3)
| 2026-10-30 | Check text with a rendered system font (PR #108): `TextToolConfig.Rendered` (font, bold, capital letter height, characters); VisionCore draws the characters with SkiaSharp and teaches them to the taught-font reader, so the engine is unchanged; drawn at save and kept with the job; the line never draws one itself (a font changed since the save fails every part). Refused at save: dot or inkjet print, font not installed, size outside 10–300 px, a character with no glyph, a line that does not cut at that size, a format needing characters outside the set. Small letters are drawn beside capitals so they are taught at their own height. Wrong, missing and extra characters NG in tests on drawn print, dark and light. Teach page: Font from Sample print / System font. Also fixed main: PLC- and tool-source lines were refused as "text is empty" (PRs #93 and #94 crossed). | 7.3, 12 |

## Decisions (section 4)
| Rendered font: drawn in .NET, taught to the existing font reader | VisionCore draws each character with SkiaSharp (already used by VisionStudio and VisionReports) and teaches the drawn lines with `vs_ocr_teach_font`, so the engine's classical reader, check and C API stay unchanged. The font text is made at save and kept with the job, so the line PC checks with exactly what was refused or accepted at save, without needing the font installed. | 7.3 |
| Rendered font: solid print only | Drawn shapes have no dots, so the engine's dot-by-dot comparison (8 from B, 0 from O on dot print) would not run; dot-matrix and inkjet tools are refused at save with a system font and stay taught from sample print. | 7.3, 7.5, 12 |
| Rendered font: the AI reader ignores it | The settings stay in the tool so switching back to the font reader keeps them; nothing reads with them under the AI reader. | 7.3 |
| Rendered font: no print quality limits from the drawn font | The drawn characters have perfect contrast and an ink amount no printer matches; limits from them (mean ± 4σ with σ near 0) would fail good parts. Until good parts are learned the recogniser's score against the drawn characters decides, as for a taught font; Learn good part adds the limits. | 7.3, 12 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS 7.3: rendered system font built (PR #108) | Claude (Mac, branch claude/ocv-rendered-font-a40ddb) |

## Pending
- Rendered system font: try on real laser and TTO print (which fonts and sizes match which printers, Bold for thick laser strokes). The feature and its NG cases are tested on drawn print; no sample print with a known font is in VS_IMAGES.
- Rendered font in the Avalonia Studio Teach screen (Hybrid 3); VisionStudio Teach has it.
- The new VisionStudio strings (`text.font_from*`, `text.render_*`) in the other 14 languages when VisionStudio gets its language tables (only en.json exists today).
