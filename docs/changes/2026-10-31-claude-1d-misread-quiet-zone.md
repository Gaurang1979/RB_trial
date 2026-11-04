# 1D read safety: EAN-13 misread under heavy print gain is never reported; quiet zone of Code 128, Code 39, ITF, Codabar and Code 93 follows the pixels

- Status: done
- Session: 1d-misread-quiet-zone (from quiet-zone-same-verdict, PR #157, Pending), 2026-10-31
- Pull request: #196
- Section 5 item: new (from docs/changes/2026-10-31-claude-quiet-zone-same-verdict.md Pending: "Engine (read safety): a synthetic EAN-13" and "Engine (line safety): the 1D quiet zone of types other than EAN/UPC")
- Components/files: engine (engine/src/linear.cpp quietZoneOk, eanReadHolds and readLinear; decodability.cpp eanReferenceDecode; decode.cpp Collector::eanHolds; reader.hpp), engine tests
- Depends on: none

## Section 5
- [x] (done 2026-11-01, PR #196: every EAN-13, UPC-A, EAN-8 and UPC-E read is read again on the image after the ISO/IEC 15420 reference decode and dropped unless a scan line gives its number and none another; the misread 0961466152002 came from zxing's bright passes (steps 10 and 11), 800 of 800 refused, the printed number still read at 0, 1 and 2 px of gain; engine test) Engine (read safety): a synthetic EAN-13 (8901396152002, X 6 px, 2 px print gain: bars 8 px, spaces 4 px) is also read as a second EAN-13, 0961466152002, with a valid check digit. Find which pass misreads it under heavy print gain.
- [x] (done 2026-11-01, PR #196: the ends are the edge pairs with a bar count the type can have between them and the type's start / stop pattern at each end after print gain; clear zones pass for corners up to a module off with 0 and 2 px of gain, a mark touching an end bar, 0.5 X past it, or past a symbol a bar short fails, for Code 39, Codabar, ITF, Code 93 and Code 128; engine test) Engine (line safety): the 1D quiet zone of types other than EAN/UPC (Code 128, Code 39, ITF, Codabar, Code 93) gives NG when a read's corner is about a module off the end bar, and a mark touching the end bar up to about 1.5 X wide is taken for the bar (as before this change). Find their ends from the bar count the decoded text gives (Code 39: 5 bars per character, Codabar 4, ITF and Code 93 by length; Code 128 needs the codeword count), as done for EAN/UPC.

## Timeline (section 3)
| 2026-11-01 | EAN / UPC reads checked on the image by the ISO/IEC 15420 reference decode (edge to similar edge; 1 / 7 and 2 / 8 by their bars after the print gain measured on the guards, with half a module of margin): a read is reported only when a scan line gives its number and none another. The synthetic EAN-13 under 2 px of gain, misread by zxing's bright passes as 0961466152002: 800 of 800 misreads refused, the printed number read in 200 of 200 decodes at 0, 1 and 2 px of gain; EAN-8, UPC-A, UPC-E 12 of 12. 1D quiet zone of Code 39, Codabar, ITF, Code 93 and Code 128: ends from the bar count and the start / stop pattern, the same verdict for corners up to a module off | Requirements 12; Code Reader 5.2 |

## Decisions (section 4)
| A wrong EAN / UPC number is never reported | zxing decodes each character from its bar and space widths, which print gain throws; the reference decode measures edge to similar edge, which print gain does not move. Every EAN / UPC read is read again on the image (9 scan lines across the bars, both directions) and kept only when one line gives its number and none another; a read that does not hold is dropped (no read: NG), never replaced by the reference decode's own number. UPC-A and UPC-E compared as their 13-digit GTIN, as zxing-cpp 3 reports them | Requirements 12 |
| 1D quiet zone of Code 39, Codabar, ITF, Code 93, Code 128: where the ends are (replaces PR #157's "one bar edge within 1.5 X of each corner, else NG" for these types) | The ends are the edge pairs within 1.5 X of the corners whose bar count the type can have (Code 39 5 a character, Codabar 4, ITF 4 + 5 a pair, Code 93 3 a character + 1, Code 128 3 a character + 4) and whose first three elements at each end are the type's start / stop pattern after print gain. A count step, not the exact count from the text: zxing's text does not give it (Code 39 / Code 93 full ASCII, a stripped check digit, Code 128's code sets); the candidate ends change the count by 2 bars at most, never a whole step, and the pattern check tells a mark past the end bar from the bar. Every such pair must have clear zones. Codabar without its start / stop letters, and a symbol whose bars do not count up, keep #157's rule | Requirements 12; Code Reader 5.2 |

## Update log (section 8)
| 2026-11-01 | This note: EAN / UPC reference decode check and the 1D quiet zone of the other types | Claude (Mac, branch claude/1d-misread-quiet-zone) |

## Pending
- Engine (read safety): the EAN-2 / EAN-5 add-on is still decoded by zxing alone; check it on the image after its reference decode as the main symbol is.
- Engine C API: report EAN / UPC reads refused by the reference decode check (Collector refusedEan) in cr_stats, so the line and the bench see them.
- Engine (recall): an EAN-13 with 3 px of print gain at X 6 px (bars 9 px, spaces 3 px) gives no read (before and after this change); the reference decode reads it, but may not choose the number (decision above).
