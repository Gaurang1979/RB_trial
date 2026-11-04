# Code Reader: Pharmacode (one-track Laetus, both directions, 3-131070) and DotCode decoders

- Status: done
- Session: Claude desktop session pharmacode-dotcode, 2026-10-30
- Pull request: #84
- Section 5 item: new
- Components/files: engine/include/codereader.h (cr_format CR_PHARMACODE, CR_PHARMACODE_REV, CR_DOTCODE, CR_ANY; cr_step 24, 25), engine/src/pharmacode.cpp and dotcode.cpp (new), decode.cpp (one pass after the 1D pass; zxing skipped when no zxing type is enabled), linear.cpp (finishLinear: a Pharmacode's quiet zone reported clear), c_api.cpp (sanitize), reader.hpp, CMakeLists.txt, engine/tests/engine_tests.cpp, dotnet/CodeReader (CodeFormat, ReadStep, CodeFormats.PharmacodeBars), dotnet/VisionCore (CodeTeach.Lock, CodeTool.SetupProblem), dotnet/HikCamera (sidecar names), ml/engine.py, tools/bench/bench.cpp, docs/vision-system/TOOL-SPECIFICATIONS.md 5.0
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #84: own Pharmacode decoder with bar-count lock and quiet zones, standard and reverse reading; own DotCode decoder with Reed-Solomon over GF(113); 14 engine checks on the specification's figures, codeword vectors and generated images, 4 new .NET tests, 1 updated) Code Reader: Pharmacode (one-track Laetus, both directions, 3-131070) and DotCode decoders.

## Timeline (section 3)
| 2026-10-30 | Code Reader reads Pharmacode (Laetus one-track, standard and reverse reading) and DotCode with decoders of the engine's own (zxing-cpp 3.1 reads neither); teaching locks a Pharmacode's bar count | TOOL-SPECIFICATIONS.md 5.0 |

## Decisions (section 4)
| Pharmacode without a check digit | Reported only with gaps alike, clean narrow / wide classes measured edge to like edge (bar plus gap, so ink spread does not move a bar between classes), a light quiet zone of 4 gaps each side (more than the 3.5 gaps a missing wide bar leaves) and a bar count inside the job's lock (min_length and max_length both set, counting bars: with only a top a missing end bar reads as a shorter code). Without a lock it is read only while teaching (CR_ANY). Bars alone cannot tell a one-width code under heavy ink spread from its all-wide twin, nor a pack turned round, so the Read code tool also needs the expected value: teaching sets the bar count and the value read, and a tool without either is a setup error. A code of one bar width needs a clear ratio: spread of a third to about half a unit on narrow bars gives no read; more reads as the all-wide value, which the expected value fails | TOOL-SPECIFICATIONS.md 5.0, requirements 12 |
| Pharmacode direction | The value depends on which end is read first, so it comes from the image: standard = the bar on the right weighs least; CR_PHARMACODE_REV for a code passing the camera turned round; both flags = standard. Bars more than 75 degrees off upright are not read, as their reading direction would hang on a small tilt | TOOL-SPECIFICATIONS.md 5.0 |
| DotCode line safety | Reed-Solomon accepts 2e + f + spare <= checks, spare = 2 plus one per 16 checks (invalid dot patterns are erasures); without erasures a random dot field decoded to the all-zero codeword, and with one spare the review measured random grids and smeared small symbols decoding to some text (about 1 in 700 on the smallest sizes; dot-matrix text seen at 45 degrees fits the lattice). The data length comes from the grid size alone. A symbol whose rotations give two texts, a Structured Append part and a reader-programming (FNC3) symbol are not reported. A W x H symbol and its quarter turn carry the same dot stream: the reading nearest upright gives the corners | TOOL-SPECIFICATIONS.md 5.0 |
| New cr_format flags | CR_PHARMACODE 0x8000, CR_PHARMACODE_REV 0x10000, CR_DOTCODE 0x20000; CR_ANY becomes 0x3FFFF and the engine reads the old 0xFFFF as CR_ANY, so stored settings keep their meaning. .NET CodeFormat.Any the same | SDK-API.md |
| DotCode reference | Layout, folding, masks and codeword sets after ISS DotCode Rev 4.0 as zint implements it (BSD-3-Clause); the tests decode the specification's figures 1, 5 and 6 as zint's tests print them, and zint's codeword vectors | TOOL-SPECIFICATIONS.md 5.0 |

## Found in review
- Opus review: one spare check word let small DotCodes and random dot grids decode (fixed: two spare); a one-width Pharmacode under heavy spread and a pack turned round read as another value with the same bar count, and a half-open bar lock let a missing end bar pass (fixed: both bounds required in the engine, and the tool needs the expected value, set by teaching).

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md 5.0: Pharmacode and DotCode built (rules, direction, line safety); open question on their decoders closed | Claude (Mac, branch claude/pharmacode-dotcode) |

## Pending
- Vision Studio Teach page: a "bars" field for a Pharmacode's bar-count lock (teaching sets it today; the length fields show for 1D types only), with its strings in the 15 languages.
- Pharmacode with its bars lying flat (a code turned 90 degrees): a reading-direction setting for it; such codes are not read today.
- Read rate of Pharmacode and DotCode on real line images (the benchmark set has none), and DotCode with perspective, dots of uneven size and light-on-dark (laser) print.
- Grading (Tool 6) of Pharmacode and DotCode.
