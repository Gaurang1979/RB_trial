# Data Matrix with a damaged or missing L finder: hypotheses over the learned symbol size, modules outside the image as erasures (design around Cognex US10956697) (rank 5)

- Status: done
- Session: dm-damaged-finder, 2026-10-31
- Pull request: #171
- Section 5 item: Data Matrix with a damaged or missing L finder
- Components/files: engine/src/lattice.cpp (windows seen in part, grid fit to the seen L arm and clock track, serial-run guard), engine/src/dmmatch.cpp (`maskedSoftDecodeDm`), engine/src/decode.cpp (lattice pass on classical regions when there is no AI locator), engine/src/reader.hpp, engine/tests/engine_tests.cpp (`testDmSeenInPart`), truth/codes.csv, truth/batch-baseline.csv
- Depends on: none

## Section 5
- [x] (done 2026-11-01, PR #171: lattice windows of the learned size that run past the label or the image or over a covered patch, placed by the seen L arm and clock track; unseen modules as Reed-Solomon erasures; the lattice also runs on classical regions when there is no AI locator; every lattice read must continue the case's serial run. Bench 48 DM 62 -> 72 of 95, 0 wrong) Data Matrix with a damaged or missing L finder: hypotheses over the learned symbol size, modules outside the image as erasures (design around Cognex US10956697) (rank 5)

## Timeline (section 3)
| 2026-11-01 | Data Matrix seen in part: lattice windows past the sampled area (off the label, out of the image) or over covered or glared patches, placed by the seen L arm and clock track and fitted by their contrast; unseen modules are Reed-Solomon erasures with four check symbols in reserve. The lattice pass also runs on classical regions when there is no AI locator. Every lattice read must continue the serial run of the case's codes (within 64). Bench: 48 DM 62 -> 72 of 95 (worst of 3; 76 best), 0 wrong over 81 images; engine tests pass. | 5, 12 |

## Decisions (section 4)
| Data Matrix seen in part: line safety | A codeword with any module not seen is an erasure, and the decode keeps four check symbols in reserve (2e + f <= ecc - 4), as every Data Matrix path does. On top: at least half the data area seen; the corrected symbol, redrawn, may disagree with at most 12 % of the seen data modules; with two or more codes of the product read, the text must look like them (`plausibleAmong`). Tested on 40000 random grids (half with the true L and clocks around random data) and 400 codes of other products: 0 reads. | 12 |
| Lattice reads continue the serial run | With the lattice on every classical region, the known-content and hard decoders made far more attempts, and one miscorrection passed `plausibleAmong` ("31100017558" on a case of 31100016688-711 and 31100017404-438). Every lattice read now needs the same text around its last digit run, with the serial within 64 of a code already read (two or more read). A true code that starts a new run and is read only by the lattice is missed instead: the safe side. | 12 |
| Data Matrix seen in part: patents | Our own form, not the method of Cognex US10956697 / US11455482: the window is placed by its seen L arm and clock track and fitted by their contrast (no masked inverse-FFT centre map, no 5 x 5 seed grown 8 ways, no polynomial). It still treats modules outside the image as unknown and ranks hypotheses over the preferred size, which those patents describe (steps 5-7 in RESEARCH-COGNEX-PATENTS.md). For legal review before export to US/EP/JP/CN; the owner's statement of 2026-10-30 ("we can use all patents and process in our library") is recorded, no legal opinion given. | - |
| Truth texts added for 48 DM | 22 texts of the cap image (all in its serial run 180-226; the owner counted 47 caps) and 31100017405 (also read by deblur2 at the same place) and 31100016711 (next to 16710) of the other image, added by hand after the check. 31100017558 was not added: it is a wrong read. | 12 |

## Update log (section 8)
| 2026-11-01 | Change note for Data Matrix seen in part (lattice windows past the sampled area, unseen modules as erasures, lattice on classical regions, serial-run guard); truth texts and baseline. | Claude (Mac, branch claude/dm-damaged-finder) |

## Pending
- 48 DM time: the cap image's slowest run is 538 ms (baseline before 496 ms) against the 500 ms target; the other image 504 ms (was 755 ms).
- 48 DM: 23 codes still missed in the bench (cap image 28 of 47, other image 44 of 48), mostly low contrast on dark caps (study 04b: LOW_CONTRAST 39).
