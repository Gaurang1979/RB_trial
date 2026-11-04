# Classical OCR on dot print: teach on the pitch grid, compare on the dot grid, narrow/wide test

- Status: done
- Session: ocr-dot-grid, 2026-10-30
- Pull request: #91
- Section 5 item: Classical OCR on dot print: teach on the pitch grid too
- Components/files: engine/src/ocr/font.cpp, engine/src/ocr/ocr.hpp, engine/tests/ocr_tests.cpp
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #91: dot print is taught on the pitch grid as it is read, one character per cell holding ink, so a teaching print with missing dots teaches; stroke cutting stays the fallback) Classical OCR on dot print: teach on the pitch grid too (teaching still cuts by strokes, so a teaching print with missing dots can be refused).
- "Classical OCR on dot print: 8, B, 0 and O": `[x] (done 2026-10-30, PR #91: each character is also compared dot by dot on the print's dot lattice, against the dots of all its taught prints together; the dots never raise a score: they widen the chosen character's lead only when every taught dot of it is read, and tie it with a character whose dots fit clearly better; 1 dot missing from every character: 9 of 10 prints OK (was 8), 2 dots: 6 (was 4), a wrong character never OK in 90 tries)`
- "Classical OCR on dot print: a test of narrow against wide": `[x] (done 2026-10-30, PR #91: I, l, 1 among T, 7, L, 4 with 0 to 2 dots missing from every character: a wrong character (narrow for wide, l for 1 or I) never OK in 210 tries; clean, 10 of 10 prints OK; with a dot missing I, l and 1 score low, as an I without a dot can be an l)`

## Timeline (section 3)
| 2026-10-30 | Classical OCR on dot print: taught on the print's pitch as it is read; each character also compared dot by dot on the dot lattice found on the print (dot rows, dot columns per pitch refined on the rows, the columns' lean), against the dots of all its taught prints together; teaching refuses a character whose dots are another's (an I typed as l). Fixed: the pitch grid's first edge could cut the line's first character; a font of many narrow characters looked for the pitch from a narrow width | TOOL-SPECIFICATIONS.md 7.2; requirements 12 |

## Decisions (section 4)
| Dots never raise a score | On dot print cut on its pitch, the comparison of melted strokes still sets how well a character fits (missing dots still lower it, so F never passes for E). The dot comparison (a dot missing costs log(0.98/0.15), extra ink log(0.85/0.02)) only lowers other characters: by how much worse their dots fit, and only when every taught dot of the chosen character is read (with one missing, a character never taught one dot short of it is as likely). When another character's dots fit clearly better, no character scores above the two (NG rather than a guess), unless the strokes are about as close to both and every dot of the other is read: then the dots choose it, at its own stroke score. Dots that fit no character well change nothing | Requirements 12 |
| A character's dots are those of all its taught prints together | A teaching print with a dot missing would otherwise teach a wrong shape (an I without its top right dot is an l); a near-identical print is kept when its dots differ, and a print whose dots fit another taught character clearly better than its own is refused | Requirements 12 |

## Found in review
- An Opus review found two false-pass paths in the first version, both fixed: the dots widened a lead with taught dots missing (an l never taught passed as I at score 100 in 5 of 80 tries with a dot missing), and a tie between two characters could leave a third on top. On its advice a taught count of dot columns is checked on the rows of dots, and teaching refuses a sample whose dots match another character. Lowering the chosen character's own score for each missing dot was tried and dropped: every print with a dot missing then failed (9 of 10 OK down to 0). A second Opus review of the fixes found no false pass: 3,200 tries of a wrong text expected (I, l, 1 swaps; 8, B, 0, O swaps; 0 to 3 dots missing), none OK.

## Update log (section 8)
| 2026-10-30 | Change note for dot print taught on the pitch grid and compared dot by dot in the classical OCR reader | Claude (macOS, branch claude/ocr-dot-grid) |

## Pending
- Classical OCR on dot print: a character never taught, one dot short of a taught one (an l in a font with I but no l), printed with another dot missing, passes as the taught one about 1 time in 20 (the comparison of strokes, as on main; the dots leave it as it is). Refusing it would also fail most prints with a dot missing: an owner's choice, or teach every character the job can print.
- Classical OCR on dot print: dot fonts other than 5 x 7 in a 6-column cell (7 x 9, 5 x 5) and real CIJ samples from VS_IMAGES for the dot lattice (dot columns per pitch, lean, refined spacing); the tests draw 5 x 7 only.
- Classical OCR teaching on dot print: an I typed as l in the same new line as a real l (neither taught before) is taught, and the l's dots then hold an I's; reads stay NG (an I printed for l never OK in 20 tries), but the dots no longer separate I from l in that font. Two prints of one character differ by a dot just as often from a missing dot, so the two-alike check cannot refuse it by one dot (found in the second review).
- Classical OCR teaching: a first line typed with one character per three printed (T7L4T7L4T7L4 typed as T7L4) is taught, on main as on this branch (found in review).
