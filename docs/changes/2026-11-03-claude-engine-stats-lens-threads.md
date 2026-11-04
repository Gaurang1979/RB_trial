# Engine: refused EAN reads in cr_stats, lens RMS limit, add-on reference check, enhancer nets per thread, CR_CV_THREADS before the first reader

- Status: done
- Session: claude engine-stats-lens-threads, 2026-11-03
- Pull request: #280
- Section 5 item: new
- Components/files: engine/include/codereader.h (cr_stats_ext.refused_ean, refused_addon; cr_set_lens doc), engine/src/c_api.cpp, engine/src/decode.cpp, engine/src/linear.cpp, engine/src/decodability.cpp, engine/src/multiframe.cpp, engine/src/reader.hpp, engine/src/calibration.cpp, engine/tests/engine_tests.cpp; docs/REQUIREMENTS-VISION-SYSTEM.md 17.1 (ENG-01..05)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #280: cr_stats_ext.refused_ean, grown at its end under struct_size) Engine C API: report EAN / UPC reads refused by the reference decode check (Collector refusedEan) in cr_stats, so the line and the bench see them
- [x] (done 2026-11-03, PR #280: a distortion model whose rms_px is not 0..0.5 px, NaN or unmeasured gives -1 and clears the lens and scale) cr_set_lens: refuse or flag a lens whose rms_px is over the calibration limit, so a stored poor model is never used
- [x] (done 2026-11-03, PR #280: the add-on is read again after the main symbol's reference decode, 6-13 modules past its last bar; one not confirmed is reported as none and counted in cr_stats_ext.refused_addon) Engine (read safety): the EAN-2 / EAN-5 add-on is still decoded by zxing alone; check it on the image after its reference decode as the main symbol is.
- [x] (done 2026-11-03, PR #280: each net is used under its own lock, uncontended while every thread has its own net) The AI enhancer's nets are picked by thread index modulo their count (decode.cpp, tryEnhance): with cr_params.threads above the core count, two workers run one cv::dnn::Net at once
- [x] (done 2026-11-03, PR #280: applied once, by the first cr_create, std::call_once) cr_create calls cv::setNumThreads when the diagnostic CR_CV_THREADS is set: a reader created while another camera decodes could rebuild OpenCV's TBB arena under it

## Timeline (section 3)
| 2026-11-03 | Engine read and thread safety follow-ups: refused EAN / UPC reads and add-ons counted in cr_stats_ext (refused_ean, refused_addon); cr_set_lens refuses a lens model over the 0.5 px calibration limit and clears the lens; EAN-2 / EAN-5 add-ons read again on the image by an ISO/IEC 15420-style reference decode past the main symbol (an unconfirmed add-on is reported as none); enhancer nets used under a lock per net; CR_CV_THREADS applied once at the first reader. engine_tests testEngineFollowUps | 17.1 ENG-01..05, 12 |

## Decisions (section 4)
| Unconfirmed EAN add-on | Reported as none, not as a refused code: the main symbol has already held on the image, and a job that requires the add-on gives NG on "none" (line safety); dropping the whole code would lose a good main read | 17.1 ENG-03, 12 |
| Refused lens | cr_set_lens clears the lens (no model, no scale) and returns -1 rather than keeping the previous one: the .NET host does not check the return today, so keeping an older lens could use stale optics silently; with none, nothing is reported in mm | 17.1 ENG-02, 12 |
| Enhancer nets with more threads than cores | A mutex per net per decode instead of capping decode threads or loading more nets: no extra memory, no slowdown while threads <= nets (the default) | 17.1 ENG-04 |

## Update log (section 8)
| 2026-11-03 | REQUIREMENTS 17.1: ENG-01..05 registered; codereader.h: cr_stats_ext.refused_ean, refused_addon, cr_set_lens refusal | Claude (Mac, branch claude/engine-stats-lens-threads) |

## Pending
- .NET CodeReader: mirror cr_stats_ext.refused_ean and refused_addon in Native.StatsExt and DecodeStats (the engine writes only the caller's struct_size, so the 5-field mirror keeps working), and make CodeReader.SetLens report the -1 of a refused lens (GradingCalibration.ApplyTo ignores it today)
- Stress test (stress_tests, thread sanitizer) with the enhancer loaded and cr_params.threads above the core count
