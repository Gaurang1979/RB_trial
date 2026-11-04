# Data Matrix read by pattern match: symbology identifier and FNC1 from a verifying grid decode

- Status: done
- Session: dm-match-symid, 2026-10-30
- Pull request: #113
- Section 5 item: Data Matrix codes read by pattern match only (CR_STEP_MATCH) have no identifier
- Components/files: engine (engine/src/decode.cpp, dmmatch.cpp, match.cpp, learned.cpp, reader.hpp, tests/engine_tests.cpp)
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #113: a Data Matrix matched to the serial run is checked by a grid decode of its own modules, which gives its identifier and FNC1; the GS1 encodation is learned so GS1 Data Matrix products are matched at all) Data Matrix codes read by pattern match only (CR_STEP_MATCH) have no identifier, so a GS1 tool now fails them: verify the effect on GS1 lines' read rate; a verifying grid decode after the match (as QR has) would give them one.

## Timeline (section 3)
| 2026-10-30 | Serial-run pattern match for Data Matrix: a verifying grid decode at the matched corners (decodeDmAt: module means, half-module and 3 % size jitter, soft Reed-Solomon with check symbols in reserve) confirms the match and reports ]d1 / ]d2 and FNC1 from the symbol's own modules; a different text rejects the match; an unconfirmed match keeps no identifier. The printer's GS1 encodation (FNC1 first, GS as FNC1) is learned, so GS1 Data Matrix products can be matched. Engine tests: matched GS1 DM gives ]d2, without FNC1 ]d1, damaged and unconfirmed none. | 5.1, 12 |

## Decisions (section 4)
| Matched Data Matrix identifier | A Data Matrix found only by pattern match takes its identifier and FNC1 from a real decode of its own modules at the matched corners (as QR's decodeQrAt); without that decode it has none, so a GS1 tool fails it: a GS1 code never passes without FNC1 evidence from the symbol itself. A decode giving another text drops the match. | 5.1, 12 |
| GS1 Data Matrix encodation | Data Matrix encoding kind 1 = ASCII with FNC1 first and every GS as FNC1, learned like kind 0 by re-encoding a decoded code (95 % of modules equal); saved reader state accepts kind 0 or 1. | 5.1 |
