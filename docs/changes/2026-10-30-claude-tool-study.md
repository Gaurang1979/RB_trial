# Complete study and development plan: every vision software, patent and document, every tool with all options, layers and corrections, accuracy targets, hardware and timeline

- Status: done
- Session: tool-study, 2026-10-30
- Pull request: #155
- Section 5 item: new
- Components/files: docs/vision-system/study/ (new folder, 12 chapters), docs/vision-system/INDUSTRY-METHODS.md (under-resolved solver status corrected), this note; no product code
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-30, PR #155: docs/vision-system/study/, 12 chapters: Cognex and MVTec, other vendors and open source, Sun Teknovation, patents by tool family, standards and documents, the tool catalogue for Tools 0-12 and the platform with layers L0-L6, options, corrections, safety, targets and build plans, hardware per station, accuracy targets and a timeline in waves) Complete study of vision software, patents, documents and tools, with the development plan for almost 100 % accuracy`

## Timeline (section 3)
| 2026-10-30 | Complete study (docs/vision-system/study/): software of Cognex, MVTec, Teledyne DALSA, Omron, NI, Keyence, Zebra, Basler, SICK, Hikrobot, Euresys, open source and Sun Teknovation; about 190 patents by tool family; standards and pharma rules; about 190 tools and sub-tools with layers, options, corrections, targets and build plans; hardware per station; targets and a timeline of about 430 person-weeks in four waves. Documentation only | 4, 5, 11, 13, 15, 16 |

## Decisions (section 4)
| Patents usable | Owner, 2026-10-30: "i discussed with lawyer and we can use all patents and process in our library." The study lists patented methods as methods to adopt, citing each patent, and records this as the owner's statement, not as a legal or freedom-to-operate opinion; the scope (territories, export) is for the owner to confirm. Copyright is separate: no vendor code, models, files, manual text or screenshots are copied; vendor screens are described in words with links | 16 |
| Accuracy means proved on the owner's images | Each tool's target is met only on the owner's images with a stated sample size, at the line time target on the line PC, without letting a bad part pass: codes >= 99.9 % with 0 wrong; OCR >= 99.5 % characters untrained; OCV 0 wrong accepted and <= 0.5 % false reject; locator 0.05 px / 0.05 deg; defect tools 0 escapes on three bad sets | 13 |
| Light and PC before cameras | The misses are mostly image contrast and blur; the leaders read with small sensors and good light. Buy shroud, filter, polariser, strobe, dark-field and dome / coaxial lights and a T2 line PC first; cameras only if TOO_SMALL still dominates | 11 |

## Update log (section 8)
| 2026-10-30 | New docs/vision-system/study/ (12 chapters); INDUSTRY-METHODS.md: the under-resolved solver is on by default (`CR_UNDERRES` ON) | Claude (Mac, branch claude/tool-study) |

## Pending
- Owner decision: hardware trigger and strobe wired to the camera, with GigE scheduled action commands and PTP, for moving lines such as the 225 ppm line (study 05 section 7, 06 section 5)
- Owner decision: refuse dot-print jobs unless every character their formats can print is taught (an untaught character one dot short of a taught one passes about 1 time in 20) (study 04c, 06 section 5)
- Owner to confirm the scope of the patent clearance of 2026-10-30 (territories, export), then reword requirement AS-08 and DL-07 to record it, keeping "never copy code, models, files or screenshots" (study 06 section 5)
- Grade the QR quiet zone on 4 modules as ISO/IEC 18004 asks, not 2 (engine/src/quality.cpp:457)
- Enable rMQR, which zxing-cpp reads and zxformats.cpp drops (engine/src/zxformats.cpp:42)
- Locator to 0.05 px / 0.05 deg with a test at the requirement instead of 1.0 px / 0.5 deg (engine/tests/locate_tests.cpp:80), with the geometry truth set and rig (study 04a steps 1-3)
- Calibration acceptance at 0.1 px RMS instead of 1.0 px, and image undistort (engine/src/calibration.cpp:111, study 04a)
- Wave 0: one hour on the line for re-shoots, bursts, 12- and 96-code cases and 1D frames; real text images per print type labelled into truth/ (study 06 section 3)
- Wave 0: order lights, strobe and a T2 line PC per chapter 05 section 11, and prove each with batch_check / ocr_bench
- Wave 0: wrong-read counter and adversarial set before reads are raised (study 04b step 2); evidence budget and read-back before the ERP print list
- Wave 1 codes: evidence budget and read-back first, module-scaled contrast, lighting recipes, PSF deblur, ERP print list (accepted only after read-back), scheduler, Data Matrix after #153 (study 04b steps 3-9)
- Wave 1 text: verification margin score, real-data training with length-normalised CTC and print-type models, OCV per-character freedom, second recogniser, layout prior, print-type settings, product teach, Indian month names and MRP (study 04c steps 1-9)
- Wave 2: geometric match, Measure, Blob, Base tools, Studio platform with safety layer, in-app benchmark, Teach with auto-find and auto-tune, Part 11 pack, verifier correlation (study 06 section 3)
- Wave 3: AI runtime and acceptance gate, anomaly and other AI tools, Inspect (labels, caps, blister and foil), Colour with a colour camera, remaining symbologies and DPM, Devanagari OCR, enterprise outputs (study 06 section 3)
- Buy the ISO standards listed in study 03 section 13 and check the [K] grading numbers against them
- Measure every time target on the line PC (all were measured on an Apple M3)
- Code Reader: fail a case with more codes than expected (today only fewer fails, dotnet/VisionCore/Tools/CodeTool.cs) (study 04b)
- Owner decision: remove requirement DL-02's option to pass a "not sure" AI result (conflicts with AS-12) (study 06 section 5)
