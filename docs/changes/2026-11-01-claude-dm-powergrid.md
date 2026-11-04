# Data Matrix lattice, full PowerGrid method (Cognex US10956697): masked inverse-FFT module-centre map, 5 x 5 seed grown 8 ways, polynomial grid for wrinkled caps; owner's patent rule recorded

- Status: done
- Session: dm-powergrid, 2026-11-01
- Pull request: #218
- Section 5 item: new
- Components/files: engine/src/lattice.cpp (warped grid), engine/tests/engine_tests.cpp (`testDmWarpedGrid`), truth/batch-baseline.csv, CLAUDE.md (patent rule), docs/PATENTS.md
- Depends on: none

## Section 5
- [x] (done 2026-11-01, PR #218: warped grid after the straight lattice fails: module-centre map by masked inverse FFT of the edge image, 5 x 5 seed grown 8 ways, cubic or quadratic polynomial refitted without outliers; bench 48 DM 72 -> 75 of 95, 0 wrong) Data Matrix lattice, full PowerGrid method (Cognex US10956697) for wrinkled and curved cap labels

## Timeline (section 3)
| 2026-11-01 | Data Matrix warped grid (the full PowerGrid method of Cognex US10956697 with our changes): module-centre map from the edge image's spectrum kept near its pitch peaks, 5 x 5 seed at the nearest minimum grown 8 ways (blank modules crossed by prediction), polynomial grid refitted without nodes a quarter module off, then the lattice's windows and decoders with the serial-run guard. Wrinkled label (0.35 module over 12 modules) 8/8, straight lattice alone 5/8; 0 reads from 20 random wrinkled labels. Bench 48 DM 72 -> 75 of 95, 0 wrong. | 5 |
| 2026-11-01 | Owner's patent rule recorded in CLAUDE.md and docs/PATENTS.md: build from patented methods with or without a twist, no validity or country checks, never stop development for patents. | - |

## Decisions (section 4)
| Patents (owner, 2026-11-01, after advice from his lawyer) | "Do not lookup for patent validity and valid countries. Simply build the tools, take ideas from patent and improve tools. We are not copying their exact model or codes and as per my discussion with lawyer we can use patented methods with additional or changed or improved approach. Always use patented methods with twist or without twist. In future do not stop development due to patent issue." Sessions build the best method from any patent or vendor, cite the source, and never check validity or territories, design around, or hold work for a patent. The status and territory columns of docs/PATENTS.md are history. | - |
| Warped grid: our changes to US10956697 | Gaussian bands a quarter of the pitch frequency wide in the centre map (follows a bend of about a module over four); the seed starts at the map's minimum nearest the patch centre instead of a straight grid (which is off by more than the snap radius on a wrinkled label); a node with no clear minimum keeps its prediction so growth crosses blank modules, but only clear nodes enter the fit; one refit without nodes a quarter module off; cubic with 60 nodes or more. Reads go through the same decoders, Reed-Solomon reserve and serial-run guard as the straight lattice. | 5, 12 |

## Update log (section 8)
| 2026-11-01 | Change note for the Data Matrix warped grid and the owner's patent rule; CLAUDE.md and docs/PATENTS.md record the rule. | Claude (Mac, branch claude/dm-powergrid) |

## Pending
- Data Matrix warped grid on strong wrinkles: 0.6 module over 9 modules (local pitch +-42 %) reads 0/8; a local (piecewise) grid instead of one polynomial may reach it.
- 48 DM time: the cap image's slowest run 543 ms, the other 532 ms, against 500 ms.
