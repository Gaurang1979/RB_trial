# OCR line finder: word gap reads as a space; slant estimate on diagonal letters

- Status: done
- Session: ocr-wordgap-slant, 2026-10-30
- Pull request: #92
- Section 5 item: new
- Components/files: engine/src/ocr/font.cpp (word gap), engine/src/ocr/lines.cpp (slant), engine/tests/ocr_tests.cpp
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #92) Classical OCR: the word gap reads as a space in proportional print, and the line finder's slant estimate no longer runs to max_skew_deg on lines of mostly diagonal letters (V W X Y Z /)

## Timeline (section 3)
| 2026-10-30 | Classical OCR: the taught-font reader judges a word gap against the line's own pitch and gap, so the word space of proportional print reads as a space ("the word gap reads as a space" failed on main on the macOS PC). The line finder's slant estimate tells the print's slant from diagonal strokes: lines of V W X Y Z / are read upright or at their real slant instead of at the search limit. 14 new slant checks; the max_skew_deg = 8 workarounds in the font tests are gone | TOOL-SPECIFICATIONS.md 7.2; requirements 12 |

## Decisions (section 4)
| Word gap against the line's own pitch | The taught-font reader put a space only where the centres were 1.7 taught widths apart and the blank between 0.5 widths. A proportional font's word space adds only about half a character to the pitch: in the test print the blank measured 9 px against a 9.5 px bar, a real reader fault (OpenCV 5's drawing tipped it under), not a wrong test. A space now needs centres 0.35 taught widths over the line's median pitch and a blank 0.3 widths over its median gap; never a higher bar than before (a line spaced throughout, or of fewer than 4 characters, keeps the old one). Fixed-pitch 1 1 and a wide pair (M W) still get no space. The check ignores spaces, so this changes the free read and the text shown, not OK / NG | TOOL-SPECIFICATIONS.md 7.2 |
| Slant from strokes, not diagonals | The shear search now runs 20 degrees past max_skew_deg on a smoothed curve: a strongest peak past the limit is diagonal strokes (/ Z X lean 20 to 35 degrees), never a slant at the limit. Two about equal peaks either side of a lower middle are the arms of V A W, and the slant is their middle. A peak must stand at least 5% of its height out of the curve. A line that cannot tell takes the slant of the clearest line in its region (one printer prints them all), and so does a line that disagrees with it by over 4 degrees on far weaker evidence; with no such line it is read upright. A slant past the job's limit is no longer straightened to the limit: set max_skew_deg to cover the print. Soft (split) binning makes the curve smooth in the angle, so stem print is found to about half a degree (was 1.5 to 3). A wrong slant can only lower scores (NG), never pass a part | TOOL-SPECIFICATIONS.md 7.2; requirements 12 |

## Update log (section 8)
| 2026-10-30 | Change note for the OCR word gap and slant fixes (PR #92) | Claude (macOS, branch claude/ocr-wordgap-slant) |

## Pending
- A single line of only one-way diagonals (/ Z 7) that is really slanted, with no other line in the region, is read upright: nothing in its strokes tells its slant. Try on real CIJ prints.
