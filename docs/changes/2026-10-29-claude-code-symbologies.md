# Code Reader: the remaining symbologies (EAN-8, UPC-A/E, Code 39, Code 93, ITF/ITF-14, Codabar, GS1 DataBar, Micro QR, PDF417, Aztec) and 1D rules (length lock, check digit)

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #60
- Section 5 item: new
- Components/files: engine/include/codereader.h (cr_format flags, cr_check, cr_params min_length/max_length/check_digit), engine/src/zxformats.cpp and rules1d.cpp (new), decode.cpp, linear.cpp, c_api.cpp, reader.hpp, CMakeLists.txt, engine/tests/engine_tests.cpp, dotnet/CodeReader (CodeFormat flags, CheckDigitRule, CodeFormats, Native.Params), dotnet/VisionCore (CodeTool, CodeTeach), dotnet/VisionStudio (Teach page, i18n/en.json), dotnet/HikCamera (sidecar names), ml/engine.py, tools/bench/bench.cpp, docs/vision-system/TOOL-SPECIFICATIONS.md 5.0
- Depends on: none

## Section 5
- [x] (done 2026-10-29, PR #60: eleven more symbologies through zxing-cpp and the 1D scan at any angle; reads of a type the job does not enable dropped; 1D length lock and Code 39 / ITF check digit; teaching locks type, count and length; an unprotected ITF tool refused; 14 engine checks, 10 .NET tests) Code Reader: the remaining symbologies (EAN-8, UPC-A/E, Code 39, Code 93, ITF/ITF-14, Codabar, GS1 DataBar, Micro QR, PDF417, Aztec) and 1D rules (length lock, check digit).

## Timeline (section 3)
| 2026-10-29 | Code Reader reads 15 symbologies (EAN-8, UPC-A/E, Code 39, Code 93, ITF/ITF-14, Codabar, GS1 DataBar, Micro QR, PDF417, Aztec added); 1D length lock and check digit; Vision Studio code type picker and auto-find for every type | TOOL-SPECIFICATIONS.md 5.0-5.2 |

## Decisions (section 4)
| Code types as flags | cr_format is a flag set; a read whose exact type (zxing symbology and variant) is not enabled is dropped, because zxing reads Micro QR and rMQR with the QR decoder: a QR job never reports them (a behaviour change: QR jobs that used to report a Micro QR now fail that part, which is the safe side). Flags only of unknown types read nothing | TOOL-SPECIFICATIONS.md 5.0 |
| 1D partial reads | Length lock counted as printed (check digit included, in characters, not for EAN/UPC) so a lock taught from a read holds whatever the check-digit setting; the rules apply before a read counts anywhere. An ITF tool with no length lock, check digit or expected text fails every part as a setup error: a partial ITF read is a valid shorter code | TOOL-SPECIFICATIONS.md 5.2 |
| Code 39 text | Read as standard and full ASCII only: the Code 32 and PZN readers would turn a pharma code into its number, so the text stays as printed | TOOL-SPECIFICATIONS.md 5.0 |
| cr_params grows | Three int32 fields appended (24 x 4 bytes); every caller in the repository (.NET Native.Params, ml/engine.py, bench) updated together. A binary built against the old header must be rebuilt: the struct has no size field yet | SDK-API.md |
| Review before ready | Opus review found an unprotected ITF, a taught length off by the stripped check digit, a hidden lock left after a type change, rejected reads using up the 1D scan's quota, the plausibility check comparing unstripped text, a probe stopping at the first code, UTF-8 bytes counted as characters and meaningless grades for Micro QR / PDF417 / Aztec; all fixed with tests | TOOL-SPECIFICATIONS.md 5.0-5.2 |

## Pending
- Code Reader: Pharmacode and DotCode decoders, PDF417 row fusion for damaged rows, Micro QR grid fit, EAN/UPC add-ons as their own output.
- Code Reader 5.2 outputs and checks: Codabar check digit, quiet-zone check, ScanAngle and CheckDigitOk outputs, explain.code1d.* keys.
- Grading (Tool 6) of the new types: ISO 15416 decodability for EAN-8/UPC/Code 39/ITF/Codabar, a grader for Micro QR, PDF417 and Aztec.
- cr_params: a struct_size field so a binary built against an older header keeps working.
- Read rate of the new types on real line images (the testing session's benchmark set has none yet).
