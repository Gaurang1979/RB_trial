# Multi-frame super-resolution from the trigger burst for TOO_SMALL codes (INDUSTRY-METHODS section 17, rank 6); under-resolved solver on for India (section 16)

- Status: done
- Session: burst-super-resolution, 2026-10-31
- Pull request: #175
- Section 5 item: new
- Components/files: engine/src/multiframe.cpp, engine/src/reader.hpp, engine/tests/engine_tests.cpp, docs/PATENTS.md, docs/vision-system/INDUSTRY-METHODS.md
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #175: `superResolve` / `superResolveQrFrames` in multiframe.cpp, the last step of `cr_decode_frames` for a QR region below 2.6 px per module that the fused modules do not read; drawn 3-frame bursts 18 -> 23 of 30 at 1.0-1.2 px per module, 0 wrong; real bursts not yet recorded) Multi-frame super-resolution from the trigger burst for TOO_SMALL codes (INDUSTRY-METHODS section 17, rank 6)

## Timeline (section 3)
| 2026-10-31 | Multi-frame super-resolution for TOO_SMALL QR (after Scanbuy US8150163, expired): the code region of each frame registered to a tenth of a pixel (phase correlation, then Lucas-Kanade), levels matched, clipped pixels left out, reconstructed 2-4x finer by iterative back-projection and read by the grid fit and the soft decode; runs in `cr_decode_frames` only after the fused modules fail and only with budget left. Drawn bursts (blur 0.5 px, noise sd 12, no known content): 1.2 px per module 10 -> 10 of 10, 1.1: 8 -> 9, 1.0: 0 -> 4; 0 wrong; 0 reads from 40 random or shuffled bursts; about 135 ms per region (max 216). The under-resolved solver (`CR_UNDERRES`) was already on by default, as the owner set for India | Requirements 4, 12 |

## Decisions (section 4)
| Super-resolution only below the solver's reach | The single-frame under-resolved solver already reads drawn codes down to about 1.2 px per module, so super-resolution gains only near 1.0-1.1 px per module; it is therefore the last step of multi-frame reading, after the fused modules, bounded by the case's remaining budget (at most 200 ms) | 4, 12 |
| Super-resolution's form for legal review | Registration then classic iterative back-projection (Irani-Peleg 1991) on the code region, after Scanbuy US8150163 (expired); no area-weighted module hypotheses are tested against the frames (that is the US11151346 form, used only by `underres.cpp`). Engineering note in docs/PATENTS.md for counsel, not legal advice | 4 |

## Update log (section 8)
| 2026-10-31 | docs/PATENTS.md: US8150163 row (super-resolution, expired, design note for counsel); INDUSTRY-METHODS.md section 17 rank 6: built, with the measured gain | Claude (Mac, branch claude/burst-super-resolution) |

## Pending
- Measure multi-frame super-resolution on real trigger bursts (2-3 frames per case of shrink-wrap labels below 2 px per module) once the owner records them on the line (Wave 0); keep it only if it reads more there with 0 wrong
- Check the two first-time reads the 2026-10-31 bench counted as wrong, both from single-frame steps unchanged by PR #175, and add them to truth/codes.csv if true: Image_20220312160710251.bmp hash 04a89a4404eb (step full; inside the owner's serial range for that image) and Image_20250125194744613.bmp hash 1b464392fd01 (step full-bright)
