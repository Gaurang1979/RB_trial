# Measure CodeNet locator recall on our misses and on the public BarBeR set (rank 8)

- Status: done
- Session: locator-recall, 2026-10-31
- Pull request: #159
- Section 5 item: Measure CodeNet locator recall on our misses and on the public BarBeR set (rank 8)
- Components/files: tools/bench/locator_recall.py (new), tools/bench/bench.cpp (image name before the CR_CANDS list, format "any"), engine/src/decode.cpp (CR_CANDS diagnostic names the step that placed each region; stderr only, no reading change), docs/LOCATOR-RECALL.md (new)
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #159: tools/bench/locator_recall.py prints locator recall per batch; docs/LOCATOR-RECALL.md. With CodeNet the locator covers 1866 of 1868 codes and 1859 read; without it, as the bench runs, 1721 read; BarBeR not run, the download needs an account) Measure CodeNet locator recall on our misses and on the public BarBeR set (rank 8)

## Timeline (section 3)
| 2026-10-31 | Locator recall measured on the 53 sample images with the new tools/bench/locator_recall.py. With CodeNet (as the line runs): 99.9 % of the 1868 codes located in the first pass, 1859 read, 8 located but not decoded, 1 not located (a 48 DM cap, NO_FINDER). Without CodeNet (as batch_check.py runs): 1721 read, 27 % of regions on a code. Fifteen hand-boxed shrink-wrap misses are all located; with CodeNet all 15 read. Measured at load 13-15 on 8 cores (a run at load 7 agreed). Engine tests and the bench once, see the pull request | 4, 13 |

## Decisions (section 4)
| Shrink-wrap and cap misses are not locator recall | On the samples, with CodeNet, the locator covers 1866 of 1868 codes (one more read by a whole-image step). The 138 codes the bench baseline misses beyond the line come from running without CodeNet: classical regions carry no code size, merge rows of labels and are mostly background, so the size-driven and AI-only steps skip them. Fix the bench setup before tuning the locator | 4, 13 |
| NO_FINDER is a decoder reason | The miss reason NO_FINDER means no QR finder or Data Matrix L in a region; whether the locator found the code is a separate measurement (locator_recall.py) | 13 |

## Update log (section 8)
| 2026-10-31 | New docs/LOCATOR-RECALL.md: locator recall per batch with and without CodeNet, ranked causes of locator misses and their fixes, BarBeR licence and how to run it | Claude (Mac, branch claude/locator-recall) |

## Pending
- Run the bench heavy test with CodeNet loaded, as the line does: batch_check.py passes --model ml/models/codenet.onnx and truth/batch-baseline.csv is refreshed on a quiet Mac (138 more codes on the samples, 1859 against 1721)
- BarBeR locator recall per code type: the owner registers at ditto.ing.unimore.it/barber and downloads the set to a local folder, then run tools/bench/locator_recall.py --via (docs/LOCATOR-RECALL.md)
- Fewer AI regions on the 24 QR cases (48 % hold a code): ai_threshold per product or the learned code size, measured with locator_recall.py, once the bench runs with CodeNet
- Run locator_recall.py once on a quiet Mac (load under 4) to confirm the numbers measured at load 13-15
