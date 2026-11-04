# Code Reader 5.2 outputs and checks: Codabar check digit, quiet-zone check, ScanAngle and CheckDigitOk outputs, EAN/UPC add-ons as their own output, explain.code1d.* keys

- Status: done
- Session: code1d-outputs, 2026-10-30
- Pull request: #79
- Section 5 item: Code Reader 5.2 outputs and checks
- Components/files: engine (codereader.h: cr_code_ext fields at the end and a new sized cr_stats_ext / cr_get_stats_ext, rules1d.cpp, linear.cpp, zxformats.cpp, decode.cpp, multiframe.cpp, c_api.cpp, engine_tests.cpp), dotnet/CodeReader (Native, DecodedCode, DecodeStats), dotnet/VisionCore CodeTool and CodeRead, VisionStudio Teach page and en.json, TOOL-SPECIFICATIONS.md 5.2 note
- Depends on: none (PR #78 merged first; per its rule cr_code and cr_stats stay fixed, so the outputs are at the end of cr_code_ext and in cr_stats_ext)

## Section 5
- [x] (done 2026-10-30, PR #79: angle, check_digit_ok, quiet_zone_ok and add-on per code, Codabar modulo 16, quiet-zone check on by default, explain.code1d.* keys) Code Reader 5.2 outputs and checks: Codabar check digit, quiet-zone check, ScanAngle and CheckDigitOk outputs, explain.code1d.* keys.
- Also built here: "EAN/UPC add-ons as their own output" from the Pharmacode and DotCode item (PR #79); the rest of that item stays open.

## Timeline (section 3)
| 2026-10-30 | Code Reader 5.2: reading angle, check-digit state, quiet-zone result and EAN/UPC add-on per code; Codabar modulo 16 check digit (verify / strip); quiet-zone check in the Read code tool (on by default) and explain.code1d.quiet_zone / check_digit / too_short with English strings. Engine tests 177 ok after merging main (new: Codabar check, quiet zone clear / mark / label edge / image edge, angle 0 / 90 / 180, EAN-13 with 2- and 5-digit add-ons); .NET 23 VisionCore code tests and 6 CodeReader format tests pass | 5.2 of TOOL-SPECIFICATIONS, requirements 12 |

## Decisions (section 4)
| Quiet zone in the tool, not the engine | The engine measures and reports quiet_zone_ok on every 1D read and keeps the read; the Read code tool fails the part. The operator then sees the code and its text with the reason, and cr_params is unchanged (PR #78 changes it). | 5.2, requirements 12 |
| Quiet zone in doubt is NG | A zone off the image, or one the tool could not measure, fails like a mark in it (line safety). The check is on by default, also for 1D jobs saved before it existed: an existing line whose labels have marks within 10 X will now fail them, as the specification asks. | 5.2, requirements 12 |
| Where the outputs live | cr_code and cr_stats are fixed since PR #78 (an older caller's struct would be overrun); angle, check_digit_ok, quiet_zone_ok and addon go at the end of cr_code_ext, the dropped-read counts in a new sized cr_stats_ext. | 5.2, SDK-API |
| Short read explanation order | A dropped read with a wrong check digit is named before a dropped partial read: partial scans of good barcodes are common, a full-length read with a wrong check digit points at the print. | 5.2 |
| Add-ons always read | EAN/UPC add-ons are read whenever present (zxing EanAddOnSymbol Read) and split from the text, so expected text and length rules see the main symbol as before; a missing add-on never fails a part (no "required" setting yet). | 5.2 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md 5.2: "Added 2026-10-30" note on the outputs and checks built | Claude (Mac, branch claude/code1d-outputs) |

## Pending
- Code Reader: an "add-on required" setting and an expected add-on text in the Read code tool, and the add-on shown in Vision Studio's result list.
- Code Reader: the explain.code1d.* strings in the other 14 languages (only en.json exists in Vision Studio today).
