# Locator recall

How many codes the locator puts a region on before decoding, and why the unread codes did not read.
Item 8 of [INDUSTRY-METHODS.md](vision-system/INDUSTRY-METHODS.md) section 17. Measured 2026-10-31 on
main at a95b3d7 (PR #159).

## The tool

```
python3 tools/bench/locator_recall.py                      the 53 sample images, one line per batch
python3 tools/bench/locator_recall.py --model none         without CodeNet (as batch_check.py runs)
python3 tools/bench/locator_recall.py --locator classical  the classical locator first
python3 tools/bench/locator_recall.py --boxes boxes.csv    also: hand-placed code boxes, located or not
python3 tools/bench/locator_recall.py --via a.json --images DIR    a VIA-labelled set such as BarBeR
```

It runs the bench with `CR_CANDS=1`. The engine then lists every region it tried and which step
placed it: `ai` or `classical` (the locator's first pass), `ai-recall` (the search-harder AI pass
when a case is short) or `slot` (placed from the lattice of read codes, not by a locator). A code is
**located** when its centre is inside a locator region. It is **single** when that region holds at
most two codes, so the region does not merge a row of labels. Batches, carry and time budget are the
same as in `batch_check.py`. The run takes `/tmp/vs-heavy-test.lock` and prints the load before and
after.

`truth/samples.csv` gives only a count per image. The tool takes a read code's centre from the bench
(`--codes`). For the shortfall it takes the engine's list of unread regions (`--misses`):

- A listed miss inside a locator region counts as **located, not decoded**.
- The rest of the shortfall counts as **not located**.

The miss list can name background (see the module-contrast note under "Limits"). So "located, not
decoded" is an upper bound. `--boxes` gives the exact answer where someone has placed boxes on the
unread codes by hand.

**Not the same thing:** the miss reason `NO_FINDER` means the *decoder* found code texture but no QR
finder patterns or Data Matrix L. It says nothing about the locator.

## Results on the 53 sample images

1868 visible codes in 53 images. Same binary and same images in both runs; the only change is
whether `codenet.onnx` is loaded.

**With CodeNet, as the line station runs** (`Station.cs` loads `codenet.onnx`):

| Batch | Images | Codes | Read | Located (1st pass) | Single | Located, not decoded | Not located | Region precision |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 24 DM | 2 | 48 | 48 | 100.0 % | 100.0 % | 0 | 0 | 100.0 % |
| 24 QR | 16 | 384 | 383 | 100.0 % | 100.0 % | 1 | 0 | 48.4 % |
| 45 QR | 6 | 270 | 270 | 100.0 % | 100.0 % | 0 | 0 | 100.0 % |
| 48 DM | 2 | 95 | 91 | 98.9 % | 98.9 % | 3 | 1 | 81.9 % |
| 48 QR | 18 | 857 | 857 | 100.0 % | 100.0 % | 0 | 0 | 93.6 % |
| other QR (shrink wrap) | 9 | 214 | 210 | 99.5 % | 99.5 % | 4 | 0 | 79.7 % |
| **all** | 53 | 1868 | **1859** | **99.9 %** | 99.9 % | 8 | 1 | 76.0 % |

**Without CodeNet, as `batch_check.py` and `truth/batch-baseline.csv` run** (classical locator only):

| Batch | Read | Located (1st pass) | Single | Located, not decoded | Not located | Region precision |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 24 DM | 48 | 100.0 % | 100.0 % | 0 | 0 | 82.8 % |
| 24 QR | 369 | 100.0 % | 100.0 % | 15 | 0 | 16.5 % |
| 45 QR | 270 | 100.0 % | 100.0 % | 0 | 0 | 49.0 % |
| 48 DM | 59 | 100.0 % | 100.0 % | 36 | 0 | 55.7 % |
| 48 QR | 846 | 99.3 % | 99.3 % | 11 | 0 | 25.6 % |
| other QR | 129 | 100.0 % | 97.2 % | 85 | 0 | 51.0 % |
| **all** | **1721** | 99.7 % | 99.4 % | 147 | 0 | 27.4 % |

In the model-less run, 6 codes were read outside every locator region, all in 48 QR. In the CodeNet
run, 1 was, in other QR. These codes were read by a whole-image or slot step.

Why the unread codes did not read, as the engine diagnosed them:

- **With CodeNet:**
  - 8 located, not decoded: TOO_SMALL 2, UNKNOWN 2, BLUR 1, COVERED 1, LOW_CONTRAST 1, NO_FINDER 1.
  - 1 not located (48 DM, `Image_20230731102244944`): NO_FINDER.
- **Without CodeNet:** 147 located, not decoded: LOW_CONTRAST 47, UNKNOWN 39, NO_FINDER 22,
  COVERED 18, TOO_SMALL 12, BLUR 9.

**Hand-placed check.** Another session placed boxes by hand on 15 shrink-wrap labels that the
baseline does not read (`Image_20250125192045895`, 25 of 40 read in the baseline). The locator
covered all 15 in the first pass, with and without CodeNet:

- **With CodeNet:** every one of the 15 regions read.
- **Without CodeNet:** the classical regions there are 900-1160 px wide, each holding several labels,
  or small regions that did not decode.

**Load.** The runs above were at a load of 13-15 on 8 cores, after an hour's wait for a quiet Mac
timed out. An earlier run at a load of 7 gave the same picture: 1857 and 1722 read, 99.9 % and
99.7 % located. Timing-bound passes can change a few codes on a quiet Mac. The ranking below does not
depend on them.

## Ranked causes of locator misses, and the fix for each

1. **The bench measures without CodeNet (setup, 138 codes).** `batch_check.py` calls the bench
   without `--model`, so every baseline number uses only the classical locator, while the line uses
   CodeNet. Model loaded or not, the classical locator still covers the codes (99.7 %). It fails as a
   source of decodable crops:
   - Its regions carry no code size (`side 0`), so the size-driven and AI-only steps (`isAi` gates
     in decode.cpp) skip them.
   - On shrink wrap it merges rows of labels into one region.
   - Only 27 % of its regions hold a code, against 76 % for CodeNet.

   With CodeNet: 138 more codes read (1859 against 1721):

   | Batch | Codes gained |
   | --- | ---: |
   | other QR | 81 |
   | 48 DM | 32 |
   | 24 QR | 14 |
   | 48 QR | 11 |

   **Fix:** run `batch_check.py` with `--model ml/models/codenet.onnx` and refresh
   `truth/batch-baseline.csv` on a quiet Mac. Left as a follow-up: it changes the heavy test that
   every open pull request is judged against.
2. **Data Matrix with a damaged or missing L (1 code: the only true locator miss with CodeNet).**
   Cap 48 DM `Image_20230731102244944`: no locator region, diagnosed NO_FINDER. **Fix:** item 5 of
   section 17 (Data Matrix broken-finder hypotheses, PR #171). After that, a region from the slot
   lattice should get the lattice decode as well.
3. **Located but not decoded (8 codes with CodeNet).** These are decoder problems, not locator
   problems:
   - TOO_SMALL and BLUR on 48 DM and shrink wrap: the optics of item 1 and multi-frame
     super-resolution (item 6).
   - LOW_CONTRAST, COVERED and NO_FINDER: one each.
4. **Region precision on 24 QR (48 % with CodeNet).** About half of the AI regions on the 24-code
   QR cases hold no code: film wrinkles and second copies. This does not cost recall, but every extra
   region costs decode time inside the 275 ms budget. **Fix:** after the bench runs with the model,
   tune `ai_threshold` per product or use the learned code size. Measure it with this tool.

What CodeNet does not cost: no code that the classical locator found was lost when CodeNet ran first
(not located: 1 with CodeNet, 0 without, and that one is the cap of cause 2).

## BarBeR (public set)

[BarBeR](https://ditto.ing.unimore.it/barber/) is published by the University of Modena and Reggio
Emilia (ICPR 2024). It holds 8748 images with 9818 barcodes (8062 1D, 1756 2D) and VIA polygon
labels with the code type. The code repository (github.com/Henvezz95/BarBeR) is AGPL-3.0. The set
itself is gathered from 12 public datasets, each with its own terms. Its COCO export names
"Attribution-NonCommercial-ShareAlike".

**Not measured.** The download needs an account on the dataset site. A session cannot create one,
so the owner has to register and download it. Keep it in a local folder outside the repository,
then run:

```
python3 tools/bench/locator_recall.py --via <BarBeR>/annotations/*.json --images <BarBeR>/images
```

This prints recall per code type (1D and 2D) and the share of first-pass regions on a labelled code.
The `--via` path was checked on a VIA file made from two sample images (70 codes, all located,
98.9 % precision). It has not been run on BarBeR itself.

What to expect:

- The engine locates **2D codes**. On 1D codes, regions come only from the classical texture locator
  and the 1D scan passes, so BarBeR's 1D recall will measure those passes, not CodeNet.
- BarBeR's images are phone and scanner photos, unlike our case images. Its 2D recall shows how
  general CodeNet is, not how it does on the line.

## Limits

- Code positions in the sample images come from the reads and the engine's miss list, not from
  labelled boxes, except the 15 hand-placed boxes. The module-contrast session (PR #169) found that the
  miss list's LOW_CONTRAST boxes on shrink wrap are background. So "located, not decoded" without
  CodeNet is an upper bound. With CodeNet, the whole shortfall is 9 codes.
- A located code may sit in a region that is also on background; recall here does not judge how
  well a region fits the code.
- Both runs ran on a loaded Mac (see "Load" above).
