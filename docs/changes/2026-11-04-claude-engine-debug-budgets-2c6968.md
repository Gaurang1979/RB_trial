# Engine time-budget checks only in optimized builds; Debug ctest passes with every correctness check on

- Status: done
- Session: engine-debug-budgets, 2026-11-04
- Pull request: #336
- Section 5 item: Time-budget tests (inspect, anomaly, polar, photometric, wavelet, inpaint
- Components/files: engine/tests/check.h; budget lines and tool-create shims in engine/tests/*.cpp (19 files; measure_tests: only its 3 budget lines and the include); docs/REQUIREMENTS-VISION-SYSTEM.md 17.11 AUD-13
- Depends on: none (Pending of PR #295)

## Section 5
- [x] (done 2026-11-04, PR #336: VS_TIME_BUDGET and vs_test_ms in engine/tests/check.h; Debug ctest 23/23, Release still judges every budget) Time-budget tests (inspect, anomaly, polar, photometric, wavelet, inpaint, ...) fail in a Debug build (unoptimized); run them in Release only, or skip their budget checks in Debug

## Timeline (section 3)
| 2026-11-04 | Engine tests: a wall-clock budget is judged only in an optimized build (`VS_TIME_BUDGET(cond)` in engine/tests/check.h, keyed on NDEBUG); a Debug build prints the time and says it is not judged. Reads that a correctness check needs to finish (cr_create, vs_ocr_create, vs_loc_create budgets in engine, OCR and locate tests, and engine_tests' internal deadlines) get 10 times their time in Debug through `vs_test_ms()`; unchanged in Release. 23 budget comparisons wrapped and 10 read deadlines scaled in 19 test files; no correctness check changed. Debug ctest: 16 failing before, all 23 pass | 17.11 AUD-13, AUD-10 |

## Decisions (section 4)
| Engine test time budgets in Debug | Judged only under NDEBUG (Release, RelWithDebInfo, MinSizeRel): the budgets are line-speed limits of the shipped build, and an unoptimized build is 5-10 times slower. Keyed on NDEBUG rather than a new CMake option, so every generator and build type gets it without setup. Debug read deadlines are scaled (10x) instead of skipped, so the correctness checks behind them still run | 17.11 AUD-13 |

## Update log (section 8)
| 2026-11-04 | Requirements 17.11: new AUD-13 (engine time budgets judged only in an optimized build) | Claude (Mac, branch claude/engine-debug-budgets-2c6968) |
