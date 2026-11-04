# Classical OCR on dot print with several missing dots per character: fixed-pitch cutting

- Status: done
- Session: Claude (ocr-dot-pitch), 2026-10-30
- Pull request: #80
- Section 5 item: Classical OCR on dot print with several missing dots per character
- Components/files: engine/src/ocr/font.cpp (pitchCells new, Font::run), engine/src/ocr/ocr.hpp, engine/tests/ocr_tests.cpp
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #80: on dot print the taught-font reader finds the print's pitch and cuts one character per cell, so characters with missing dots no longer split or vanish; 1 to 3 dots missing from every character: all characters found in 30 of 30 drawn prints (stroke cutting: 20), checks OK 8, 4 and 0 of 10, a wrong character never OK in 90 tries; no new parameter) Classical OCR on dot print with several missing dots per character: fixed-pitch cutting (CIJ prints at a fixed pitch).

## Timeline (section 3)
| 2026-10-30 | Classical OCR: fixed-pitch cutting for dot print (CIJ). The pitch is found on the dots as printed, each cell read as one character; characters with missing dots keep their place and score, a damaged character that cannot be told apart scores low | TOOL-SPECIFICATIONS.md 7.2; requirements 12 |

## Decisions (section 4)
| Dot print cut on the print's pitch | With dot print (set, or the AUTO retry as dots) the taught-font reader looks for a steady pitch on the line as printed (before the dots are melted, as the melt often closes the one-column gap between characters): the grid that puts the most cell edges in gaps between characters, clear of ink. Each cell holding ink is one character, compared at its ink extent and a few nearby extents, each character keeping its best so the margin to the next best stays fair; an empty cell is a word gap. Without a steady pitch (touching characters, text not on one pitch) the stroke cutting runs as before. No new job parameter | TOOL-SPECIFICATIONS.md 7.2; requirements 12 |
| Missing dots are not forgiven | Cutting finds the characters, but a character with dots missing is still compared with the whole taught character: with 3 of about 14 dots missing from every character no check passes (scores low), and a wrong character, printed or expected, was never OK in any test. Tolerating missing dots in the comparison would let F pass for E | Requirements 12 |

## Found in review
- An Opus review found no false pass. On its advice the grid is now refused when any cell edge runs through a character (not only when the edges hold much ink on average), so text of another pitch (proportional print in the AUTO retry) is cut by strokes as before; and a cell with any ink of its own now always reads as a character, however little is left of it.

## Update log (section 8)
| 2026-10-30 | Change note for fixed-pitch cutting of dot print in the classical OCR reader | Claude (macOS, branch claude/ocr-dot-pitch) |

## Pending
- Classical OCR on dot print: teach on the pitch grid too (teaching still cuts by strokes, so a teaching print with missing dots can be refused).
- Classical OCR on dot print: 8, B, 0 and O stay close after the dots are melted, so a damaged 8 or 0 often scores low; a comparison on the dot grid (dots present at the found pitch) would separate them.
- Classical OCR on dot print: a check at the default 100 ms budget, timed on the T1 PC (the fixed-pitch path costs about 30 ms for two 10-character lines on the development PC under load; the tests give it 5 s).
- Classical OCR on dot print: a test of narrow against wide characters (I, l, 1) with dots missing, as each character's best over nearby extents eases the width penalty.
- ocr_tests: "the word gap reads as a space" fails on main (taught-font free read of drawn solid print), found while testing this change.
