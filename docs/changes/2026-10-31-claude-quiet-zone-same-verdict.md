# 1D quiet-zone check gives the same verdict for the same image, whatever the reader saw before

- Status: done
- Session: quiet-zone-same-verdict (from two-camera soak finding, PR #148), 2026-10-31
- Pull request: #157
- Section 5 item: Engine (line safety): the EAN-13 quiet-zone verdict depends
- Components/files: engine (engine/src/linear.cpp quietZoneOk and readLinear, decode.cpp and reader.hpp: the decoder's 1D polarity on each code), engine tests
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #157: the 1D quiet zone is measured from the bars found in the image, not from the read's corners; EAN/UPC ends by their fixed bar count; the Harpic frame gives quiet zone OK on a fresh reader and in 40 of 40 passes of a carried reader, with and without the job's ROI; engine test over corner variants, marks and 24 learned histories) Engine (line safety): the EAN-13 quiet-zone verdict depends on the reader's history; the same Harpic frame (20261023_154745501) passes in 2 of 40 passes of one reader over the line's images and fails on a fresh reader. Measure the quiet zone where it does not depend on which pass found the code, and add a test that the verdict is the same for the same pixels.

## Timeline (section 3)
| 2026-10-31 | 1D quiet-zone check (Code Reader 5.2) gives the same verdict for the same pixels whichever pass read the code: scan lines through the middle of the bars found in the image, bar polarity from the decoder, zones from the end bar's own edge (EAN/UPC: the edges with exactly 30, 22 or 17 bars between them and even guard bars; other types: the one bar edge within 1.5 X of each corner, else NG). Cause of PR #148's soak mismatch: a pass read the code on a strip at the top of the bars with its end a module short. Harpic frame OK fresh and 40 of 40 carried; all 61 soak frames the same fresh and carried; engine test | Requirements 12; Code Reader 5.2 |

## Decisions (section 4)
| 1D quiet zone: where it is measured from | From the pixels, not the read's corners: passes give corners up to a module apart and some only a strip of the bars, which flipped the verdict. EAN/UPC ends come from their fixed bar count (every matching pair of ends must pass, so a mark beside an end cannot; a mark touching the end bar widens it past its guard neighbour and fails). Other types take the one bar edge within 1.5 X of each corner and give NG when there are two (a mark just past the end bar, or a corner a module off): toward NG, as a mark's edge and the symbol's next bar cannot be told apart there | Requirements 12; Code Reader 5.2 |
| 1D quiet zone: the bars' polarity | From the decoder that read the code (zxing reports inverted print), never from the zone: a label just the symbol's width on a darker carton would take the carton for its spaces. Codes no zxing read gave (the SSCC fit): the end bars and the ground must agree, else NG | Requirements 12 |

## Update log (section 8)
| 2026-10-31 | This note: the quiet-zone verdict fix and its checks | Claude (Mac, branch claude/quiet-zone-same-verdict) |

## Pending
- Engine (line safety): the 1D quiet zone of types other than EAN/UPC (Code 128, Code 39, ITF, Codabar, Code 93) gives NG when a read's corner is about a module off the end bar, and a mark touching the end bar up to about 1.5 X wide is taken for the bar (as before this change). Find their ends from the bar count the decoded text gives (Code 39: 5 bars per character, Codabar 4, ITF and Code 93 by length; Code 128 needs the codeword count), as done for EAN/UPC.
- Engine: a quiet-zone mark right at the detection limits (a bar-side run of about 0.5 X, or about 4 X tall) can still flip with the read's corners, as the profile is sampled on a grid tied to the corners; measure edges and zones in image coordinates.
- Engine (read safety): a synthetic EAN-13 (8901396152002, X 6 px, 2 px print gain: bars 8 px, spaces 4 px) is also read as a second EAN-13, 0961466152002, with a valid check digit (step 10, with or without an ROI). Find which pass misreads it under heavy print gain.
- Engine tests: "UEC from the decoder after ISO 15415: Micro QR M4-L 2 errors" (PR #100) is flaky on main: UEC 0.00 instead of 0.33 in 4 of 6 runs of the same build (b195f95). Find what varies between runs and make it give the same UEC each time.
