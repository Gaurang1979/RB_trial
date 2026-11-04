# Wave 0: wrong-read counter and adversarial set before reads are raised

- Status: done
- Session: wrong-read-counter, 2026-10-31
- Pull request: #168
- Section 5 item: Wave 0: wrong-read counter and adversarial set
- Components/files: tools/bench (bench.cpp, batch_check.py, adversarial.cpp, sha256.hpp, batch_check_test.py), engine/CMakeLists.txt (adversarial target), truth/ (codes.csv, README.md)
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #168: `bench --texts` and `batch_check.py` count wrong reads, texts not in truth/codes.csv, and fail on any; true texts of the 53 sample images as SHA-256 hashes; adversarial batch of 28 synthetic cases from tools/bench/adversarial.cpp run by every batch check; truth format in truth/README.md) Wave 0: wrong-read counter and adversarial set before reads are raised (study 04b step 2); evidence budget and read-back before the ERP print list

## Timeline (section 3)
| 2026-10-31 | Wrong-read counter: `bench --texts truth/codes.csv` counts and lists every read whose text is not among its image's true texts (SHA-256, so no customer text in the repository); `batch_check.py` fails on any wrong read whatever the read rate, never forgives one on a second run, and adds checked texts with `--add-texts`. Adversarial batch (`engine/build/adversarial`, 28 cases, QR from qrcodegen and DM from zint, count set to 24): serial-run gap trap, one module off, codewords off towards a neighbour not on the case, t+1..t+4 codeword errors, another product clean and damaged, near-duplicates, damage, noise, print, random codewords behind real finders, checkerboard and halftone. truth/codes.csv seeded with 1,736 texts of the 53 sample images. Bench on main's engine: 0 wrong reads on the samples (1,730/1,868 read) and on the adversarial batch (362/476 printed codes read) | 12, 13 |

## Decisions (section 4)
| True texts in truth by hash | truth/codes.csv holds SHA-256 hashes of the true texts, not the texts: the codes on a customer's packs are customer data and stay out of the repository (CLAUDE.md); a wrong read matching a hash by chance is about 1e-77 | 12 |
| A text not in truth is a wrong read | Any read whose text is not among its image's true texts fails the bench, including a code read for the first time: a session that raises reads checks each new text (image, form, run, independent reader) and adds it with --add-texts, writing how in its pull request. Wrong reads of two runs are added, never forgiven | 12 |
| Adversarial batch drawn, not stored | The adversarial images are drawn from a fixed seed on each run (QR by qrcodegen, DM by zint, not by the engine's own encoders), so nothing is committed and the batch cannot drift from its truth; judged on wrong reads only | 12, 13 |

## Update log (section 8)
| 2026-10-31 | truth/README.md (new): samples.csv, codes.csv (true texts by SHA-256, wrong-read rule, how to add texts), batch-baseline.csv, the adversarial batch | Claude (Mac, branch claude/wrong-read-counter) |

## Pending
- Owner spot check of the 1,736 true texts in truth/codes.csv (seeded from main's reads in three carried runs; checked for count, form and run, not by eye)
- Batch check: the 5-label excise image Image_20250125201109413 reads 4 or 5 of 5 from run to run on main's engine, so the read-count check fails on it about half the time (reads 129-132 of 214 in "other QR", baseline 131)
- Adversarial set to >= 10,000 negatives with photographed foil, other products' real labels and Monte-Carlo per soft path (study 04b 5.0.4)
