# Wave 0 engine fixes: QR quiet zone graded on 4 modules, rMQR behind its own format flag, calibration over the RMS limit refused

- Status: done
- Session: wave0-engine-fixes, 2026-10-31
- Pull request: #158
- Section 5 item: new
- Components/files: engine/src/quality.cpp, engine/src/zxformats.cpp, engine/src/decode.cpp, engine/src/c_api.cpp, engine/include/codereader.h (CR_RMQR, CR_ANY, cr_calibrate_chessboard -3), engine/src/calibration.cpp, engine/tests/engine_tests.cpp, tools/bench/bench.cpp, dotnet/CodeReader (CodeFormat.RMqr, calibration message), dotnet/HikCamera (sidecar name), dotnet/VisionCore/Tools/CodeTool.cs (rMQR not graded yet), dotnet tests
- Depends on: none

## Section 5
- "Grade the QR quiet zone on 4 modules": `[x] (done 2026-10-31, PR #158: the QR quiet-zone grade samples a 4-module ring; Micro QR stays 2, DotCode 3, Data Matrix 1; a sample off the image counts dark)`
- "Enable rMQR, which zxing-cpp reads": `[x] (done 2026-10-31, PR #158: CR_RMQR = 0x40000 and CodeFormat.RMqr; an rMQR is reported only in a job that enables it, never in a QR job; CR_ANY is 0x7FFFF and an older caller's "any" (0xFFFF, 0x3FFFF) is read as CR_ANY; grading refused at setup until rMQR has a grader)`
- "Calibration acceptance at 0.1 px RMS": three or more chessboard views whose lens model reprojects worse than 0.5 px are now refused (-3, nothing written) instead of silently keeping the scale alone; fewer than three views stay scale only. The 0.1 px target and image undistort stay open.

## Timeline (section 3)
| 2026-10-31 | Wave 0 engine fixes (study chapter 06): QR quiet zone graded on a 4-module ring (ISO/IEC 18004); rMQR read under its own format flag CR_RMQR, never counted in a QR job; a chessboard calibration of three or more views that reprojects worse than 0.5 px refused instead of quietly dropping its lens model. Engine tests, the .NET tests of every project using the reader and the bench run | 4, 12, 13 |

## Decisions (section 4)
| QR quiet zone 4 modules | ISO/IEC 18004 asks for 4 modules round a QR (Micro QR 2). The grade ring was 2, so a QR printed with a 2-3 module margin graded A. Codes closer than 4 modules to a neighbour or a label edge now grade lower, as the standard says, and a zone the image cuts grades F (a sample off the image counts dark, as in the PDF417, 1D and DPM graders). Reading is unchanged (the grade is computed after the read); jobs with a lowest grade on tightly packed QR cases NG more often | 6, 13 |
| rMQR as its own flag | zxing-cpp read rMQR and the engine dropped it. It gets its own bit so a QR or Micro QR job never counts an rMQR (line safety: a code of a type the job did not ask for is never reported). CR_ANY grows to 0x7FFFF; an older caller's "any" (0x3FFFF, like the older 0xFFFF) is read as CR_ANY, so teaching and miss reasons keep working for it; "any" is for teaching and testing. rMQR has no grader yet, so a lowest grade or recorded grades are refused at setup | 4, 12 |
| Calibration refused, not downgraded | A lens model from three or more views that reprojects worse than 0.5 px was silently replaced by the scale alone, so the user believed the lens was corrected. It is now refused (-3, nothing written) and the user retakes the views. 0.5 px, not the study's 0.1 px target: no real calibration of the line camera has been measured yet, and a limit that refuses every real calibration would leave grading without millimetres; tighten once the MV-CS200's real RMS is known. The limit is applied when the calibration is made; a lens set directly with cr_set_lens is not checked, and the Dashboard calibrates from one view (scale only) today | 6, 13 |

## Update log (section 8)
| 2026-10-31 | codereader.h: CR_RMQR, CR_ANY 0x7FFFF, cr_calibrate_chessboard -3 documented; QUALITY.md: the grade's quiet-zone widths; TOOL-SPECIFICATIONS.md 5.0: rMQR as its own type | Claude (Mac, branch claude/wave0-engine-fixes) |

## Pending
- Calibration: measure the real reprojection RMS of the MV-CS200 chessboard calibration and tighten the refusal limit towards 0.1 px; add image undistort (study 04a 3.2, 3.9)
- rMQR grading (ISO/IEC 15415 with the rMQR fixed patterns) so a lowest grade can be set for rMQR jobs (study 04b)
- rMQR checked on real rMQR labels (it already appears in the Teach code-type list and in auto-find suggestions)
- Read code tool: refuse "any type" outside teaching, so a line job never counts a stray code of a type nobody chose (a saved "Any" job now also reads rMQR)
- cr_set_lens: refuse or flag a lens whose rms_px is over the calibration limit, so a stored poor model is never used
