# Deblur with the PSF measured per zone, not only re-scoring with it (rank 7)

- Status: done
- Session: psf-deblur, 2026-10-31
- Pull request: #164
- Section 5 item: new
- Components/files: engine/src/decode.cpp (deblur step for missed QR regions); truth/codes.csv (3 true texts)
- Depends on: none (#189 for a bench with no wrong reads: the two it fixes are main's own)

## Section 5
- [x] (done 2026-10-31, PR #164: +3 true reads on the 24 QR cans, 0 new wrong reads; the rest of the BLUR misses are mostly not codes) Deblur missed QR regions with the measured lens blur and a local level (industry methods rank 7, study 04b)

## Timeline (section 3)
| 2026-10-31 | QR deblur step for missed regions: a region of unknown size (the classical locator's) takes the side of the codes read in the image, so the measured blur width and the module size apply to it; after the Wiener and Richardson-Lucy deblur, a local level (mean over 3 modules, on the 2x upscale) is tried after the fixed levels. Data Matrix keeps main's path. Bench against main, same Mac and lock: 24 QR 375 vs 373 true reads (one 24-can case now reads all 24), 48 QR 847 = 847, other batches equal within ±1; slowest 24 QR image 229 vs 230 ms | 7, 12 |

## Decisions (section 4)
| Deblur of missed regions | Most BLUR misses are not codes: of 38 BLUR crops on the 24 and 48 QR batches, about 30 are cap rims, carton edges or can edges, and several of the 8 codes were already read in another region or frame. Only 3 more were readable, and only by a Wiener deblur plus a local level; no deblur width alone and no local level alone read one. So the gain is small by nature (rank 7 was an estimate) | 7 |
| PSF from the decoder's grid | Tried and dropped: measuring the blur on GS1 QR codes (FNC1, which encodeQr does not write) from the decoder's module grid cost 1868.bmp 153-276 ms (target 275, main about 100) and read no more codes. The 24 QR cans therefore still deblur at the fixed widths | 7, 12 |
| Data Matrix | The local level and the borrowed QR size are QR only: on the DM caps a run with them showed 4 texts not in truth/codes.csv at the deblur steps; Data Matrix keeps #153's second try unchanged | 12 |

## Update log (section 8)
| 2026-10-31 | truth/codes.csv: 3 true texts added by hand (Image_20250311101601937: 2, Image_20250311101614724: 1). Checked: same product prefix and form as the case; one is already a true text of the other frame of the same case; with the other the case's two frames hold 24 distinct codes for 24 cans; read by zxing-cpp after Reed-Solomon correction on a deblurred crop, and matching the printed serial on the crop. Not by --add-texts, which would also add main's 2 wrong reads (#189) | Claude (Mac, branch claude/psf-deblur) |

## Misses by cause (24 and 48 QR, main vs this branch, same lock)
| Batch | Reads main → branch | BLUR | NO_FINDER | GLARE | LOW_CONTRAST | UNKNOWN | COVERED |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 24 QR (384) | 373 → 375 (bench, true texts) | 16 → 15 | 15 → 12-13 | 6 → 6 | 7 → 7 | 9 → 9-10 | - |
| 48 QR (951 linked, set2 images missing here) | 845 → 845 | 23 → 23 | 7 → 7 | - | 46 → 46 | 7 → 7 | 4 → 4 |

The miss diagnosis counts regions, including regions of codes read elsewhere, so the causes add to more than the codes missed.

## Pending
- GS1 QR (FNC1 first) in encodeQr, so the lens blur is measured on the 24 QR cans and their deblur uses it instead of the fixed widths
- Miss diagnosis: leave out regions whose code was read in another region or frame, so BLUR counts only unread codes
- Engine test "UEC from the decoder after ISO 15415: Micro QR M4-L 2 errors" fails on main too (UEC 0.00, want 0.33; zxing-cpp 3.1.1)
