# Engine: an extra pack is seen after the count is read (count over, copies per code)

- Status: done
- Session: count-over-engine, 2026-10-31
- Pull request: #197
- Section 5 item: Engine: decoding stops once the expected count is read
- Components/files: engine/src/decode.cpp (extra check after the count, copies at every place seen, copies over, libdmtx corners), engine/src/multiframe.cpp, engine/include/codereader.h (cr_stats_ext.copies_over, extra_unchecked), engine/src/c_api.cpp, engine/src/reader.hpp, engine/tests/engine_tests.cpp; dotnet/CodeReader (Native.StatsExt, DecodeStats.CopiesOver, ExtraUnchecked, Extra), dotnet/VisionCore/Tools/CodeTool.cs (Judge copiesOver, explain.code.extra_copy); docs/vision-system/TOOL-SPECIFICATIONS.md; truth/codes.csv (one text added)
- Depends on: none

## Section 5
- [x] (done 2026-11-01, PR #197: with the expected count read, the engine decodes the code-sized regions left (those the first pass did not finish), with plain and the two fixes that read the case's codes, so an extra pack's code that those fixes read is read wherever it lies; regions left undecoded when the time runs out are reported (cr_stats_ext.extra_unchecked), not judged; see Pending for the products and paths it does not cover yet) Engine: decoding stops once the expected count is read (Found::done in engine/src/decode.cpp), so an extra pack is caught only when its code is read before the count is reached
- "An extra pack carrying a copy": `[x] (done 2026-11-01, PR #197: cr_stats_ext.copies_over counts matrix codes seen more often than the label carries; judged from hint_symbols_per_code, or on a product learned over 20 cases or more to carry each code once (1.05 at most), else not judged; DecodeStats.Extra and the Read code tool fail such a case (explain.code.extra_copy); a copy is counted 0.6 code sides from every place the code was seen (1D: 1.5 long sides), so a copy next to its twin counts and a copy read again does not count twice; multi-frame cases keep the most copies over of any frame)`

## Timeline (section 3)
| 2026-11-01 | Extra pack in the engine: code-sized regions left after the expected count are decoded; copies per code over the label (cr_stats_ext.copies_over, extra_unchecked) fail the case in DecodeStats.Extra and the Read code tool; libdmtx corners fixed to image rows (were mirrored top to bottom). engine_tests testExtraPack: an extra code read in 13/13 slots, a copy flagged 13/13, doubled labels not flagged, a third copy flagged, no hint judged only on a once-only learned product, libdmtx corners. Samples: no good case flagged (81 images, CR_COPIES=1) | 12 |

## Decisions (section 4)
| Extra pack check after the count | Decoding still stops at the expected count (line time); then only the code-sized regions not yet finished are decoded, with plain and the two fixes that read this case's codes (an extra pack is printed and lit like the others), within the time budget. Unread regions are not NG: a wrinkle or flag the locator proposes would fail good cases. Regions left untried when the time runs out are counted (extra_unchecked), not judged, until the owner decides | 12 |
| Copies over the label | A code seen more often than the label carries is an extra pack (the engine merges one text, so the count never sees it). Judged only where the label is known: the product's hint_symbols_per_code, or a product learned over 20 cases or more to carry each code once. A doubled label's second print is not always read, so its learned average can sit anywhere from 1 to 2 and the case's own copies cannot tell it from an extra pack: then nothing is judged (a false NG on every good case would stop the line). Only a hint-judged case stops teaching symbols per code. Matrix codes only (a 1D read's place moves along its bars) | 12 |
| Copy counting | A copy is a read 0.6 code sides from every place that text was seen (the regions' rule), replacing "farther than the code's diagonal from the first", which missed a copy on the next pack and could count a re-read copy twice. Doubled-label products now learn about two symbols per code (the AI region cut keeps their second prints) | 12 |
| libdmtx corners | readDmtx returns corners in image rows: libdmtx counted y up from the bottom (DmtxFlipNone), so a libdmtx read's place, the region it closed and its grading were mirrored top to bottom | 12 |

## Update log (section 8)
| 2026-11-01 | codereader.h: cr_stats_ext.copies_over and extra_unchecked; TOOL-SPECIFICATIONS Read code pass/fail: the extra check after the count and copies over the label, explain.code.extra_copy added | Claude (Mac, branch claude/count-over-engine) |

## Pending
- Owner decision: should regions left undecoded after the count (cr_stats_ext.extra_unchecked > 0, the time budget ran out) make the case NG? Today they are reported, not judged
- Owner decision: an extra pack carrying a copy of a code in the case is red NG (explain.code.extra_copy) with no code marked; should that code be marked orange as a duplicate (owner, 2026-10-23, duplicates orange)?
- Vision Studio and the product master: set hint_symbols_per_code from the product (labels that carry their code twice), so copies over is judged from the first case; without it a product is judged only after 20 cases, and doubled-label products never
- Extra check after the count: add the dot filter, lattice and libdmtx for products those steps read (dot print, Data Matrix caps), and count regions whose winning steps were not tried as unchecked; 1D-only jobs (no regions) and multi-frame cases where no single frame reads the full count get no extra check yet
- Log cr_stats_ext.extra_unchecked with each case (Station, LineRunner, hard cases), so the owner can judge the decision above from line data
