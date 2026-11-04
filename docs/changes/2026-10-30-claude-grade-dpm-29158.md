# ISO/IEC TR 29158 (AIM DPM) grading for Data Matrix and QR marked on parts

- Status: done
- Session: grade-dpm-29158, 2026-10-30
- Pull request: #96
- Section 5 item: new (Pending in docs/changes/2026-10-29-claude-grade-report.md: "ISO/IEC TR 29158 (AIM DPM) grading and its report")
- Components/files: engine/src/grade_dpm.cpp (new), engine/include/codereader.h, engine/src/c_api.cpp, engine/src/reader.hpp, engine/CMakeLists.txt, engine/tests/dpm_tests.cpp (new); dotnet/CodeReader (DpmGrade.cs, NativeDpm.cs new; Native.cs partial; CodeReader.cs), dotnet/VisionCore/Tools/CodeTool.cs, tests
- Depends on: none

## Section 5
<!-- When done (forms in the README's roll-up rules): "- [x] (done YYYY-MM-DD, PR #n: ...) <the item's text>" replaces the item named above; "- [ ] ..." adds a new item; any other line is added under the item as an update. -->

- New item: `[x] (done 2026-10-30, PR #96: ISO/IEC TR 29158 computation, C API, Read code tool grade standard) ISO/IEC TR 29158 (AIM DPM) grading for Data Matrix and QR marked on parts: cell contrast, cell modulation, minimum reflectance from the image-based calibration, fixed pattern and distributed damage, the 50 % and 80 % apertures, dot-peen gap closing, light-on-dark marks and the lighting options (90, 30Q, 30T, 30S, D).`

## Timeline (section 3)
<!-- | YYYY-MM-DD | what was built and verified | requirement sections | -->
| 2026-10-30 | ISO/IEC TR 29158 (AIM DPM) grading of Data Matrix and QR in the engine (`engine/src/grade_dpm.cpp`): cell contrast, cell modulation against the error correction, minimum reflectance (Rcal x SRcal / SRtarget x ML / MLcal), fixed pattern damage per segment and distributed damage, 50 % / 80 % apertures, dot-peen closing, light-on-dark polarity; C API `cr_set_dpm` / `cr_get_dpm` (growing structs); .NET `DpmGrade` and the Read code tool's grade standard (ISO 15415 or TR 29158). 30 engine checks on synthetic laser and dot-peen marks, 7 .NET tests | tool spec 6.3 |

## Decisions (section 4)
<!-- | Topic | Decision and reason | requirement sections | -->
| DPM aperture | The overall grade comes from the 80 % aperture by default; 50 % or "the better of the two" only when set. Both overall grades are always reported. The stricter default keeps a doubtful mark from passing on the more forgiving aperture | 12, tool spec 6.3 |
| DPM approximations | Thresholds from vendor documentation of TR 29158 (CC 30/25/20/15 %, MR 20/15/10/5 % as the 2020 edition; the 2011 pass mark of 5 % is the D/F line); a wrong cell counts as about one codeword (as quality.cpp does for UEC); segment damage after ISO/IEC 16022 (0/9/13/17 %) and finder cells by count (0..3). Process control from camera grey levels, like the 15415 grades | tool spec 6.3 |
| DPM in the C API | Its own `cr_set_dpm` / `cr_get_dpm` with struct_size, not new cr_params / cr_code_ext fields, so it does not collide with the other grading pull requests; graded in c_api.cpp after decode, decode.cpp untouched | C API rules |
| Part mark not graded | Under ISO 29158 a Data Matrix / QR that cannot be graded fails the Read code tool (`explain.verify.not_graded`), as with 15415. Mean light outside 70..86 % is reported, not failed: cell contrast is a ratio and holds | 12 |

## Update log (section 8)
<!-- | YYYY-MM-DD | what this pull request changed in the docs | Claude (<machine>, branch <branch>) | -->
| 2026-10-30 | change note for ISO/IEC TR 29158 grading | Claude (Mac, branch claude/grade-dpm-29158) |

## Pending
- ISO/IEC TR 29158 grade report: the DPM page in the PDF grade report and the Hybrid 2 / Vision Studio settings (grade standard, lighting, aperture, the calibration symbol step that records MLcal and SRcal).
- ISO/IEC TR 29158 with real dot-peen and laser part images, and the codeword table instead of the one-cell-one-codeword approximation for cell modulation.
