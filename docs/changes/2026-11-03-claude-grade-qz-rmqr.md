# 2D grading: calibrated quiet-zone ring in the threshold's units; rMQR graded (ISO/IEC 15415 with the rMQR fixed patterns)

- Status: done
- Session: grade-qz-rmqr, 2026-11-03
- Pull request: #289
- Section 5 item: new
- Components/files: engine/src/quality.cpp, engine/src/decode.cpp (rMQR added to the grading dispatch, one line), engine/tests/engine_tests.cpp, dotnet/VisionCore/Tools/CodeTool.cs (the rMQR grading refusal removed), dotnet/VisionCore.Tests/CodeGradeTests.cs, dotnet/CodeReader/CodeReader.cs (doc comment), docs/REQUIREMENTS-VISION-SYSTEM.md (17.1 GRD-01, GRD-02), docs/QUALITY.md, docs/vision-system/TOOL-SPECIFICATIONS.md
- Depends on: none

## Section 5
- New item: `[x] (done 2026-11-03, PR #289: the 2D quiet-zone ring is converted to flat-field or reflectance units before it is compared with the threshold) 2D grading with a calibration: the quiet-zone ring in quality.cpp is compared in the threshold's units (Req 17.1 GRD-01)`
- New item: `[x] (done 2026-11-03, PR #289: ISO/IEC 15415 with the ISO/IEC 23941 fixed patterns for every rMQR size, 2-module quiet zone; the Read code tool takes a lowest grade and recorded grades for rMQR) rMQR grading, so a lowest grade can be set for rMQR jobs (Req 17.1 GRD-02)`

## Timeline (section 3)
| 2026-11-03 | 2D grading: with a calibration the quiet-zone ring is sampled in the threshold's units (flat-field corrected grey or reflectance), as the modules and DotCode's dot centres already were; before, raw grey against a calibrated threshold graded a clear margin dark under a dim camera (QZ F) or a mark clear. rMQR graded by ISO/IEC 15415 with its own fixed patterns (finder and separator, finder sub-pattern, corner patterns, alignment patterns with the vertical timing, edge timing), the timing patterns for grid non-uniformity and a 2-module quiet zone; a size the standard has not is not graded. The Read code tool now takes a lowest grade for rMQR. | 17.1 GRD-01, GRD-02; 4 (tool 6) |

## Decisions (section 4)
| rMQR fixed patterns | Taken from ISO/IEC 23941 and checked against every one of the 32 rMQR sizes zint makes: clean, each grades A with no fixed module wrong. The alignment columns are a table per width (27: none; 43: 21; 59: 19, 39; 77: 25, 51; 99: 23, 49, 75; 139: 27, 55, 83, 111); a width not in it is not graded (no grade rather than a made-up A, line safety). | 17.1 GRD-02 |
| rMQR unused error correction | zxing's value where it gives one; otherwise the approximation with M 15 % and H 30 %. No misdecode protection is taken off yet: the ISO/IEC 23941 tables were not at hand to say which small symbols have it (listed under Pending). | 17.1 GRD-02 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.1 GRD-01 and GRD-02 registered; QUALITY.md: rMQR's 2-module quiet zone and the calibrated ring; TOOL-SPECIFICATIONS.md: rMQR graded | Claude (Mac, branch claude/grade-qz-rmqr) |

## Verified
- Engine (cmake Release, `engine/build`): `engine_tests` all passed (431 checks), with the new `testGradeRmqr` (every rMQR size clean grades A with no fixed module wrong; turned a quarter and a half still A; a sub-pattern and an alignment module printed dark lower FPD; a mark in the 2-module quiet zone lowers QZ, one beyond it does not; faded grades C; an 11 x 31 grid is not graded; read and graded end to end in an rMQR job: A) and `testGradeQuietZoneCalibrated` (dim camera with a reflectance card, light falling to 30 % with a flat field: a clear margin grades QZ A, a mark in it lowers it). With the quiet-zone conversion taken out both clear-margin checks fail (QZ 1.00 and 0.38, F), so the tests catch the old fault.
- An earlier full run on a busy Mac (load 30+) missed one SSCC read in "linear reader: tilted, defocused, moving and small SSCCs" (5-second shared deadline); it passed in the next run at lower load. Untouched by this change.
- .NET: `dotnet test dotnet/VisionCore.Tests` with the engine libraries on `DYLD_LIBRARY_PATH`: 836 of 836 passed (CodeGradeTests' `RMqrTakesALowestGrade` replaces `RMqrIsNotGradedYet`). Without the libraries on the path, 153 tests fail with DllNotFoundException only.
- Bench (`tools/bench/batch_check.py`, under the heavy-test lock): every batch ok, 1753/1868 read (93.8 %), 0 wrong, 81 images; baseline unchanged (grading only).
- After merging origin/main (8bedf72b; no engine change there): VisionCore.Tests builds; CodeGradeTests, CodeAnyTypeTests and LineGradingTests 22 of 22 passed.
- CI: not run (billing).

## Pending
- rMQR unused error correction: take off the misdecode protection codewords of the small single-block rMQR symbols from the ISO/IEC 23941 tables, as isoUec does for QR and Micro QR (Req 17.1 GRD-02)
- rMQR grading checked on real rMQR labels (the drawn symbols of every size grade as expected)
