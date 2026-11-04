# Engine C API robustness: sanitizer build and fuzzing of cr_*, vs_ocr, vs_locate against bad input

- Status: done
- Session: engine-api-fuzz, 2026-10-31
- Pull request: #174
- Section 5 item: new
- Components/files: engine/CMakeLists.txt (VS_SANITIZE, VS_FUZZ), engine/tests/fuzz/ (new), scripts/fuzz-engine.sh (new), engine/src (c_api.cpp, decode.cpp, dotcode.cpp, fixture/locate_tool.cpp, ocr/c_api_ocr.cpp, ocr/lines.cpp, ocr/ocr.hpp, ocr/verify.cpp), engine/include/codereader.h and vs_locate.h (comments only), engine/tests (engine, ocr, locate tests)
- Depends on: none (main merged in at 84d3731; still overlaps #158 and #171 textually: merge those first)

## Section 5
- New item: `[x] (done 2026-10-31, PR #174: ASan/UBSan build engine/build-asan, fuzzers engine/tests/fuzz for codereader.h, vs_ocr.h and vs_locate.h, scripts/fuzz-engine.sh; 12 crashes, hangs and undefined behaviours found by the fuzzers and 6 more found reviewing the same code, all fixed with regression tests; 10-minute runs then clean (52,900 cases), no leaks) Engine C API robustness against bad input: odd images, malformed settings and struct sizes, learned-state blobs, OCR fonts, print references, patterns and formats`

## Timeline (section 3)
| 2026-10-31 | Engine C API fuzzed under AddressSanitizer and UndefinedBehaviorSanitizer (engine/build-asan; scripts/fuzz-engine.sh, 10 minutes per API, under the heavy-test lock). Found and fixed: unbounded thread count (thousands of threads, process end on failure), NaN settings through std::clamp, expected-count overflow, chessboard calibration hang and escaping OpenCV exception, DPM closing disc of any size, DotCode lattice collapse cast to int, OCR date/time fields overflowing or hanging, OCR line + 1 overflow, OCR dot-print closing unbounded (42 s), OCR line finder without a deadline (minutes on noise), Locate ignoring its time budget (minutes), exceptions out of five C entry points. Regression tests in engine, OCR and Locate tests | 12 |

## Decisions (section 4)
| A broken setting never loosens a check | NaN in a float setting takes its default, except the pass limits (OCR min_quality, Locate min_score), which take their strictest value: before the fix a NaN limit failed every check, and a corrupt job must not turn that into passes | 12 |
| Hard stop for Locate and the OCR line finder | Both now stop at 10 times their time budget (Locate at least 500 ms, the OCR line finder at least 1 s) and report not found, so the check fails (NG). Normal Locate runs take about the budget (50-72 ms at a 50 ms budget), so a stop at the budget itself would fail good parts | 12 |
| Limits on settings | cr_params.threads 0..64, expected_count 0..4096, chessboard 3..64 corners each way, DPM closing up to 64 px, OCR date offsets up to 100 years, tolerances up to a year (dates) or a day (times), OCR dot-print closing radius up to twice the letter height | 12 |

## Update log (section 8)
| 2026-10-31 | codereader.h: cr_set_dpm refuses closing above 64 and non-finite calibration values; vs_locate.h: time_budget_ms stop at 10 times; scripts/fuzz-engine.sh documented in its header | Claude (Mac, branch claude/engine-api-fuzz) |

## Pending
- The AI enhancer's nets are picked by thread index modulo their count (decode.cpp, tryEnhance): with cr_params.threads above the core count, two workers run one cv::dnn::Net at once (a data race; for the engine thread-sanitizer item)
- Fuzz the Windows build of the engine (MSVC /fsanitize=address) on the line PC, and a coverage-guided libFuzzer run (needs Homebrew LLVM: Apple's clang has no libFuzzer)
- The bench fails on main itself (97fc1ef): other QR Image_20250125201205013.bmp takes 101 ms against its 100 ms budget, in both main's and #174's runs (baseline 68 ms); find which merge since the baseline slowed it, or refresh the baseline
