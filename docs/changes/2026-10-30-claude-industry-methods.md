# Industry methods survey: every tool against Cognex, HALCON, DALSA, Omron, NI, Keyence, Zebra, open source, AI and patents

- Status: done
- Session: industry-methods, 2026-10-30
- Pull request: #149
- Section 5 item: new
- Components/files: docs/vision-system/INDUSTRY-METHODS.md (new), this note; no product code
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-30, PR #149: docs/vision-system/INDUSTRY-METHODS.md, one table per tool with process step, method or filter, who uses it with links, have / partial / missing, expected gain and effort; patents to check; ranked list of missing methods) Industry methods survey for every tool: Cognex, HALCON, Teledyne DALSA, Omron, NI, Keyence, Zebra, open source, recent AI and patents, compared with our engine`

## Timeline (section 3)
| 2026-10-30 | Industry methods survey (docs/vision-system/INDUSTRY-METHODS.md): vendor documents, papers and patents for every tool from acquisition to Teach, compared with the engine; ranked list of 15 missing methods for QR, Data Matrix, 1D and laser OCR / OCV. Documentation only | 4, 5, 13, 15 |

## Decisions (section 4)
| Our own bench is the read rate | No vendor publishes a measured rate on pharma aggregation cases; Dynamsoft's own benchmark shows tuned templates, not the default reader, reach 100 % on Data Matrix. Every method in the survey is accepted only when `batch_check.py` or `ocr_bench` shows more reads with 0 wrong at the line time targets | 13 |
| Misses are mostly in the image | On the 53 sample images the misses are low contrast (shrink wrap, dark caps), blur, codes below about 1.8 px per module and covered or missing finders, so lighting, optics and contrast normalisation lead the ranked list ahead of new decoders | 4, 13 |
| AI enhancer needs a legal check before export | Keyence US11854163 (active to 2042) claims a reader that restores a code image with a net trained on defective and ideal pairs and decodes the result, which is how our enhancer works; keep it after normal decoding, measure its real rescues, and consider a territory switch like `CR_UNDERRES`. Engineering note for counsel, not legal advice | 4, 15 |

## Update log (section 8)
| 2026-10-30 | New docs/vision-system/INDUSTRY-METHODS.md: industry methods per tool, patents to check, ranked missing methods | Claude (Mac, branch claude/industry-methods) |

## Pending
- Lighting and optics per product, measured on the line: cross-polarised light for shrink-wrapped cases, dark-field for dark Data Matrix caps, case filling the frame for at least 2.5 px per module, stored per product (INDUSTRY-METHODS rank 1)
- Module-scaled local contrast normalisation in place of fixed-tile CLAHE, in our own form (Cognex US10607047 active) (rank 2)
- ERP print list as exact candidates for known-content and serial-run decoding from the first code (R8) (rank 3)
- Bracketed exposures on one trigger, fused by the multi-frame reader with saturation masks (rank 4)
- Data Matrix with a damaged or missing L finder: hypotheses over the learned symbol size, modules outside the image as erasures (design around Cognex US10956697) (rank 5)
- Multi-frame super-resolution from the burst (expired Scanbuy US8150163), and the under-resolved solver switched on for India (rank 6)
- Deblur with the PSF measured per zone, not only re-scoring with it (rank 7)
- Measure CodeNet locator recall on our misses and on the public BarBeR set (rank 8)
- Fewest-error-correction ranking of candidate reads and two-frame confirmation of low-evidence reads (rank 9)
- Real 1D images from the side camera, then joining intact segments of several scan lines for torn bars (rank 10)
- Laser OCV: per-letter scale and shear freedom, and printer-maker fonts (Domino, Videojet, Markem) as the reference (rank 11)
- OCV limits learned on real good prints from the line (rank 12)
- AI OCR trained on real line images with length-normalised CTC loss, and print-type models (dot print, pharma) (rank 13)
- Two independent text readers must agree, plus word-level confidence calibrated on our OK / NG set (rank 14)
- Text-line layout prior ("2 2 2-4") and a tight-spacing setting for dot print (rank 15)
- Legal review of the patents in INDUSTRY-METHODS section 16.1 (Keyence US11854163 / US12039400 against the AI enhancer; Yissum US12182721 and MVTec US12548135 before any anomaly tool ships) and an India (InPASS) search
