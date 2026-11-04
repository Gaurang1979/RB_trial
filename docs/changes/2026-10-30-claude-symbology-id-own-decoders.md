# Symbology identifier and FNC1 from the engine's own decoders; CodeRead carries it to the grade report

- Status: done
- Session: symbology-id-own-decoders, 2026-10-30
- Pull request: #101
- Section 5 item: Symbology identifier from the engine's own decoders
- Components/files: engine/src/reader.hpp, zxformats.cpp/.hpp (OwnReadScope), gridfit.cpp, lattice.cpp, qrsoft.cpp, multiframe.cpp, linear.cpp, decode.cpp, engine/tests/engine_tests.cpp; dotnet/VisionCore (ToolModel CodeRead, CodeTool.Judge, Reports/GradeReport), dotnet/VisionCore.Tests (Gs1SymbologyTests, GradeReportTests)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #101: the engine's own decoders report the AIM identifier and GS1 flag of their final zxing grid decode; the fitted SSCC reports ]C1; a GS1 tool fails a code whose identifier is not known; CodeRead carries the identifier and the grade report checks FNC1 first with it; 5 engine checks, 4 .NET test methods changed or added) Symbology identifier from the engine's own decoders (lattice, grid fit, known content, frames, fitted SSCC), so their GS1 reads are checked for FNC1 too.
- [x] (done 2026-10-30, PR #101) Grade report PDF: check FNC1 with the code's symbology identifier (VisionCore CodeRead does not carry it yet).

## Timeline (section 3)
| 2026-10-30 | The engine's own decoders (lattice, grid fit, soft and known-content decodes, frames) report each code's AIM identifier and GS1 flag, the fitted SSCC ]C1; a GS1 tool fails a code whose identifier is not known; the grade report checks FNC1 first with CodeRead.SymbologyId | TOOL-SPECIFICATIONS.md 6.4, requirements 12 |

## Decisions (section 4)
| Identifier from the own decoders | Every own decoder ends by decoding a clean module grid with zxing, which reports the identifier. A per-thread scope (OwnReadScope) around each decoder call collects it by text and gives it to the code made from that text; no decoder signature changed. A text not decoded in the scope (a known text matched as a pattern, CR_STEP_MATCH) or decoded both with and without FNC1 stays unknown | TOOL-SPECIFICATIONS.md 6.4 |
| Fitted SSCC is ]C1 | The fit places Start C and FNC1, and the check symbol counts the FNC1 codeword, so a symbol without FNC1 would fail its check: the fitted read is reported ]C1, GS1 | TOOL-SPECIFICATIONS.md 6.4 |
| Unknown FNC1 fails a GS1 tool | Replaces "GS1 needs FNC1 first" (PR #78)'s fallback: a code without an identifier no longer borrows the worst one of its type on the image, nor passes on its data alone; it fails "could not be checked for GS1 (FNC1 first)". Only pattern-matched codes are left without one, and a doubtful result is NG. Texts judged with no engine read (identifiers null) are still checked on their data. A Digital Link URL is checked without its identifier as before | requirements 12 |
| Grade report FNC1 | With CodeRead.SymbologyId the report's GS1 data check includes FNC1 first and names the identifier; without one it says "FNC1 position not checked" as before. GS1 data in a symbol without FNC1 first is shown as GS1 data that fails | TOOL-SPECIFICATIONS.md 6.4 |

## Pending
- Data Matrix codes read by pattern match only (CR_STEP_MATCH) have no identifier, so a GS1 tool now fails them: verify the effect on GS1 lines' read rate; a verifying grid decode after the match (as QR has) would give them one.
