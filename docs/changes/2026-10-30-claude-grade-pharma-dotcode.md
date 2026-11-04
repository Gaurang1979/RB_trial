# Grading (Tool 6) of Pharmacode and DotCode

- Status: done
- Session: Claude desktop session grade-pharma-dotcode, 2026-10-30
- Pull request: #117
- Section 5 item: new (Pending of docs/changes/2026-10-30-claude-pharmacode-dotcode.md)
- Components/files: engine/src/grade1d.cpp, decodability.cpp, quality.cpp, dotcode.cpp, decode.cpp, diagnose.cpp, c_api.cpp, reader.hpp, engine/include/codereader.h (comments; cr_code_ext grid for DotCode), engine/tests/engine_tests.cpp, docs/vision-system/TOOL-SPECIFICATIONS.md (6.1, 6.2)
- Depends on: none

## Section 5
- "Grading (Tool 6) of Pharmacode and DotCode": [x] (done 2026-10-30, PR #117: in the engine; Pharmacode by ISO/IEC 15416 scans with the engine's own decoder as reference decode, pitch decodability against its 15% tolerance (one-width codes also by bar width over pitch), 8 X quiet zones, scans over the bars' own height; DotCode by the ISO/IEC 15415 matrix method on its dot grid, no fixed pattern (-1), dot-placement grid non-uniformity, Reed-Solomon unused error correction per block, 3 X quiet zone; the Read code tool takes a grade on them with PR #150) Grading (Tool 6) of Pharmacode and DotCode.

## Timeline (section 3)
| 2026-10-30 | Grading of Pharmacode (1D scans, ISO/IEC 15416 method; its own decoder the reference decode) and DotCode (ISO/IEC 15415 matrix method on its dot grid, after AIM ISS DotCode) through the existing cr_quality fields; engine tests on drawn symbols, good and damaged (quiet zone mark, faded print, void, bar or dot off its place, codewords damaged to the correction limit) | 6.2, 12 |

## Decisions (section 4)
| Pharmacode grading method | Laetus defines no print quality: graded as verifiers do, by ISO/IEC 15416 scans, with the engine's Pharmacode decoder as each scan's reference decode (the reader that judges the line), decodability as the share left of that decoder's 15% bar + gap pitch tolerance (gain-free, edge to similar edge), and for a code of one width also each bar's width over its pitch against the decoder's 0.44 / 0.52 limits (ink spread there turns all narrow into all wide), quiet zones 8 X as the decoder needs, the reading direction from the code's corners (so a job's reading-direction setting carries over) | 6.2, 12 |
| DotCode grading method | AIM ISS DotCode refers print quality to ISO/IEC 15415's matrix method: graded on the decoder's dot grid; no fixed pattern damage (DotCode has none; grade -1, not an A); grid non-uniformity from each printed dot's centre against its grid place; unused error correction from its own Reed-Solomon blocks with the two spare check words counted as misdecode protection; quiet zone 3 X | 6.2, 12 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md 6.1 and 6.2: DotCode and Pharmacode grading | Claude (Mac, branch claude/grade-pharma-dotcode) |

## Pending
- 2D grading with a calibration: the quiet-zone ring in quality.cpp compares raw grey with a threshold in flat-field or reflectance units (found in review; DotCode's dot centres already convert).
- Grading of Pharmacode and DotCode on real line images (the benchmark set has none), against a verifier where one is available.
- Check the DotCode grade parameters against the print quality clause of AIM ISS DotCode Rev 4.0 itself (not available to this session; the method follows ISO/IEC 15415 less fixed pattern damage).
