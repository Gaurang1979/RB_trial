# Measure tool: end edges on rows closer than 2.6 px and a lone pin at 1.5 px blur, 4 px wide, on a soft image (Req 4 MEA-01)

- Status: done
- Session: measure-close-rows, 2026-11-04
- Pull request: #341
- Section 5 item: Measure tool: end edges on rows closer than 2.6 px
- Components/files: engine/src/measure/measure_tool.cpp, engine/include/vs_measure.h (comment only), engine/tests/measure_tests.cpp, docs/REQUIREMENTS-VISION-SYSTEM.md (section 4, MEA-01)
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #341: the end edge is placed by a least-squares fit of the redrawn row; rows 2.6 and 2.4 px apart and a lone 4 px pin at 1.5 px blur within 0.1 px, 2.2 px apart 0.103 px) Measure tool: end edges on rows closer than 2.6 px with a soft image are still up to 0.22 px off (was 0.49); a lone pin at 1.5 px blur and 4 px width up to 0.11 px

## Timeline (section 3)
| 2026-11-04 | Measure tool end edges on close rows and soft pins (Req 4 MEA-01): the first and last edge of a row, and both edges of a lone pin, are placed where the row drawn again from the edge's free side fits the profile best (Gauss-Newton). Worst end-edge error over eight sub-pixel places, camera-like images, 1 px blur: 2.6 px apart 0.22 -> 0.046 px; 2.4 px 0.57 -> 0.092 px; 2.2 px 1.51 -> 0.103 px; 1.5 px defocus disk 2.4 px apart 0.52 -> 0.045 px; lone 4 px pin at 1.5 px blur 0.11 -> 0.069 px; 3.2 px apart 0.052 -> 0.041 px; lone 3.2 px pin 0.083 -> 0.056 px; 2 px defocus disk 4 px apart 0.025 -> 0.015 px. Also better, still above 0.1: 1.5 px blur 3.2 px apart 0.41 -> 0.11 px, 1.2 px blur 2.4 px apart 0.90 -> 0.13 px, 3.2 px pin at 1.5 px blur 0.27 -> 0.16 px, 4 px pin at 2 px blur 0.36 -> 0.28 px. Pin pitch rows of 4.5 px pins 16 px apart: widths 0.057 -> 0.003 px (1 px blur) and 0.25 -> 0.031 px (1.5 px blur). With noise (2 grey levels, box 10 px high): rows 2.6 px apart worst 0.50 -> 0.13 px, 2.4 px apart 1.23 -> 0.21 px | 4, 12 |

## Decisions (section 4)
| End edge placed by a least-squares fit | PR #317 matched only the window centroid of the redrawn row to the profile's. That leaves the place nearly free: a template taken from the free side about a wrong place leans almost as the profile does, so the rounds crept (0.78 of the error kept each round) towards a point 0.1 px off on soft pins and 0.2 px off on rows 2.6 px apart. Now the redrawn row (template from the free side, up to three neighbours, heights by least squares) is fitted to the profile sample by sample, and the edge, with the far edge of a lone pin or short row, is moved by Gauss-Newton steps on the residuals, each kept only while it lowers the misfit. The neighbours' tails on the free side are taken off the template (three rounds), and the template reaches as far as the free side keeps falling (R + 2 to 2R + 1 samples). Still no blur model: a defocus disk is fitted as exactly as a Gaussian. Rows 2.2 px apart stay at 0.103 px: that is the least-squares minimum (central and forward differences agree), not a stopping error | 4, 12 |
| Neighbours of an end edge | A neighbour must reach a tenth of the edge's own response as well as a fifth of the threshold: with 2 grey levels of noise, ripples 16 px past a lone pin were taken as neighbours, and the pin's far edge was then never fitted (it kept a 0.14 .. 0.18 px outward lean, also in PR #317). A gap wider than 2 (R + 1) samples ends the row: in a pitch row the next pin is not drawn (it leans itself, which put pin widths 0.6 px off at a 16 px pitch) | 4, 12 |
| End-edge move up to 1.5 px | A 2 px blur on a 4 px pin leans 1.1 px; the fit is kept when it lies within 1.5 px of the symmetric centroid (was 1 px). The edge stays within 1 px of its response's peak sample as before | 4 |
| Time | The far edge fitted with a lone pin's first edge is taken for that edge, not fitted again. A soft row's edge count 0.064 -> 0.077 ms, a soft lone pin's box 0.033 -> 0.042 ms, a pin pitch row of 26 soft pins 0.96 -> 0.52 ms (Mac, measured with the load under 5); the timing checks of measure_tests (sharp drawings) are unchanged | 4 |
| Cases that moved the other way, still within limits | On a row drawn sharp, area-sampled and then blurred (the edgeCount drawing of measure_tests) the first edge scatters about +-0.1 px with the sub-pixel place, as before (worst over 16 places 0.089 -> 0.100 px at 3.2 px apart, 0.49 -> 0.12 px at 2.6 px apart); the one place edgeCount checks moves 0.044 -> 0.068 px (limit 0.1). A nearly sharp row (0.3 px blur) 0.060 -> 0.077 px worst over eight places, the sampling limit of a sharp edge. A noisy lone pin (1.5 px blur, 4 px) loses its 0.18 px outward lean but scatters more: sd 0.13 -> 0.16 px, worst 0.43 -> 0.50 px | 4 |

## Update log (section 8)
| 2026-11-04 | Requirements section 4 Measure row: MEA-01 also holds for rows down to 2.4 px apart at a 1 px blur (0.11 px at 2.2 px) and a lone 4 px pin at 1.5 px blur. vs_measure.h edge-count comment: the fit and its limits | Claude (Mac, branch claude/measure-close-rows-91bbd0) |

## Pending
- Measure tool: a lone soft pin with noise (2 grey levels, box 10 px high, 1.5 px blur, 4 px wide) scatters sd 0.16 px on its edges, worst 0.50 px; the template is read from one free side only (both free sides averaged would cut the noise)
- Measure tool: end edges still above 0.1 px on very soft rows and pins: 2.2 px apart at 1 px blur 0.103 px, 1.5 px blur 3.2 px apart 0.11 px, 1.2 px blur 2.4 px apart 0.13 px, 3.2 px pin at 1.5 px blur 0.16 px, 4 px pin at 2 px blur 0.28 px
- Measure tool: pin pitch rows of 4.5 px pins 10 px apart at 1.5 px blur give widths 0.35 px off (0.19 px at 1 px blur); the inner edges of a row with unequal pins and gaps, placed by the symmetric centroid, not the end edges
