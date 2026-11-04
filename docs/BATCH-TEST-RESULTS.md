# Batch test results (2026-10-30)

Engine from branch `anomaly` at `a4c4edd` (cylinder-unwrap + polarity merged), Release build,
macOS on Apple M3 (8 cores). No engine code was changed for this test.

## How the batches were run

- Images come from `samples/Barcode Images` and counts from `truth/samples.csv`.
- **Batch = product**: case size × code type, as set in the product master. The operator sets
  the code type and the **case size** at the start of the batch (README, "How it reads a case").
  This means images that show only part of a case (42, 46 or 47 visible codes) run with the
  count set to 48. They are scored against the codes that are visible.
- The shrink-wrapped excise images (5, 11, 28 or 40 visible labels) don't match any
  product-master size. They run as a separate "other" batch, with the count set to the visible count.
- Read results come from `bench --carry --runs 1 --budget auto`: one reader per batch, carried
  across its images in name order, as on a line. Time is also measured with fresh readers
  (`--runs 3`), and both are shown. The time target is the bench's `--budget auto` (12: 100 ms,
  24: 275 ms, 45/48: 500 ms, 96: 600 ms).
- The duplicate check comes from the decoded texts (`--codes`). The engine returns each text once
  per image, and the orange duplicate flag (10 000 child / 1 000 case history) lives in the .NET
  layer, which the bench doesn't exercise.

## Per-batch results

| Batch (count set) | Images | Codes read / visible | Cases complete | Images with misses | Within-image dups | Time per case, carried: median / max | Fresh readers: median / max | Target | Time verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 12 (any) | 0 | — | — | — | — | — | — | 100 ms | **no sample images** |
| 24 QR (24) | 16 | 372 / 384 (96.9 %) | 12 / 16 | 4 | 0 | 221 / 232 ms | 222 / 229 ms | 250–300 ms | pass |
| 24 DM (24) | 2 | 48 / 48 (100 %) | 2 / 2 | 0 | 0 | 42 / 42 ms | 82 / 111 ms | 250–300 ms | pass |
| 45 QR (45) | 6 | 270 / 270 (100 %) | 6 / 6 | 0 | 0 | 59 / 403 ms | 57 / 405 ms | 500 ms | pass |
| 48 QR (48) | 18 | 847 / 857 (98.8 %) | 16 / 18 by visible (10 / 18 by count 48) | 6 short + 2 partial views | 0 | 327 / 409 ms | 336 / 414 ms | 500 ms | pass |
| 48 DM (48) | 2 | 61 / 95 (64.2 %) | 0 / 2 | 2 | 0 | 487 / 745 ms (per image) | 482 / 682 ms | 500 ms | **fail** (1 of 2 over) |
| 96 (any) | 0 | — | — | — | — | — | — | 600 ms | **no sample images** |
| other QR: shrink-wrapped (5/11/28/40) | 9 | 131 / 214 (61.2 %) | 2 / 9 | 7 | 0 | 153 / 227 ms | 153 / 218 ms | per count | 11-label image 107 ms fresh vs 100 ms; 5-label image 109 ms max vs 100 ms |
| **All** | 53 | **1729 / 1868 (92.6 %)** | | | **0** | | | | |

Not run:
- `set2-25--Raw.bmp` and `set2-27--Raw.bmp` (2nd image set, 46 and 48 cartons). The local samples
  folder has only the first-set `25--Raw.bmp` / `27--Raw.bmp` (24 cans, run in the 24 QR batch),
  and no copy of the second-set files was found on this Mac.
- `aa.bmp`, `Image_20220312160710251 - Copy.bmp`, `- Copy (2).bmp`, `Image_20250131105013973.bmp`,
  and `Image_20260519025459039.bmp`, because they have no row in `truth/samples.csv`.

Duplicates: no image returned the same text twice. Codes repeat across images within a batch
(24 QR: 62 texts, 45 QR: 45, 48 QR: 188, other: 34), which is expected, because the sample sets
photograph the same case more than once (for example `case 1`–`case 5`, and `14--Raw`…`27--Raw`).
On a line, these repeats would be flagged orange by the duplicate history.

Miss reasons (MissInfo, carried run, summed over each batch's short images; miss regions can
outnumber missing codes):

| Batch | Missing codes | BLUR | LOW_CONTRAST | NO_FINDER | COVERED | GLARE | TOO_SMALL | UNKNOWN |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 24 QR | 12 | 18 | 5 | 12 | 0 | 5 | 0 | 4 |
| 48 QR (short images only) | 10 | 15 | 14 | 6 | 4 | 0 | 0 | 7 |
| 48 DM | 34 | 4 | 39 | 16 | 26 | 0 | 0 | 7 |
| other QR | 83 | 0 | 114 | 4 | 11 | 0 | 19 | 28 |

## Failing images

Per image: read / visible (carried run; fresh-reader read in brackets where it differs), the main
MissInfo reasons, and the likely cause.

**24 QR**
- `Image_20250311152948003.bmp`: 17 / 24 (18). BLUR 13, LOW_CONTRAST 4, GLARE 3. The QR is on
  the curved side of the cans and edges are soft, so this is curvature or focus blur.
- `Image_20250311101601937.bmp`: 22 / 24. NO_FINDER 5, BLUR 2, GLARE 1. Finder patterns are lost
  to label curvature and glare.
- `Image_20250311101614724.bmp`: 22 / 24. NO_FINDER 4, BLUR 2, GLARE 1. Same case and cause as above.
- `Image_20250311153603230.bmp`: 23 / 24. Mixed reasons, with UNKNOWN 2.

**48 QR**
- `Image_20260619102417807.bmp`: 45 / 48. BLUR 4, NO_FINDER 3. Envelopes, so this is soft
  print or a bent surface.
- `Image_20260619102411326.bmp`: 46 / 48. BLUR 4, NO_FINDER 2. Envelopes.
- `Image_20260619102444090.bmp`: 47 / 48 (48 with fresh readers). LOW_CONTRAST 3, BLUR 2. A
  borderline code that reads on some runs and not others.
- `11.bmp`: 46 / 48. UNKNOWN 3, COVERED 2, LOW_CONTRAST 2.
- `5.bmp`: 47 / 48. LOW_CONTRAST 5.
- `30--Raw.bmp`: 47 / 48. BLUR 3.
- `Image_20231008113153221 - Copy.bmp` (42 visible) and `Image_20260605100429695.bmp` (47 visible)
  read every visible code, but they come out as short cases because the count is set to 48.
  That's correct behavior. Their miss regions (26 LOW_CONTRAST, 5 BLUR/LOW_CONTRAST) are the
  cropped or face-down cartons.

**48 DM** (fails on reads and on time)
- `Image_20220312160710251.bmp`: 19 / 47 (22 fresh, 21 with the count set to 47, 22 with a 5 s
  budget). LOW_CONTRAST 26, COVERED 21, NO_FINDER 14. These are dark Data Matrix caps.
  **This doesn't match the CSV note "46 now read".** The older build in the main checkout
  (`engine/build/bench`, 2026-10-29) also reads 19, so this isn't new on `anomaly`. The 46 was
  probably reached with a different setup (learned state, model or serial-run matching), so it
  needs checking.
- `Image_20230731102244944.bmp`: 42 / 48 (45 fresh). LOW_CONTRAST 13, COVERED 5. It is **over
  time**: 745 ms carried and 682 ms max fresh, against a 500 ms target. The carried reader is
  slower than a fresh one on this image (fresh median 258 ms), so the state carried over from
  the previous DM image seems to cost time here.

**Other QR (shrink-wrapped excise cases)**
- `Image_20250125192045895.bmp`, `…194126243.bmp`, `…194253018.bmp`: 25 / 40 each (the same case
  three times). LOW_CONTRAST 20 each. The QR codes sit under shrink wrap, which flattens contrast.
- `Image_20250125194723873.bmp`: 26 / 40. Same cause.
- `Image_20250125194659743.bmp`: 17 / 28. LOW_CONTRAST 26. Same cause.
- `Image_20250125194744613.bmp`: 1 / 11 (3 fresh). TOO_SMALL 18. The labels are too small in
  this frame (below about 1.8 px per module). The known 499 / 502 / 507 duplicates can't be
  checked with this few reads. It is also over time with fresh readers (107 ms against the 100 ms
  target for ≤ 12 codes).
- `Image_20250125201205013.bmp`: 2 / 5. LOW_CONTRAST 7, COVERED 2.
- `Image_20250125201109413.bmp`: 5 / 5 carried, but 4 / 5 and a 109 ms max with fresh readers
  (100 ms target).

## Engine tests

`engine/build/engine_tests`: **all passed** (73 `ok` checks, 0 failures, exit code 0).

## Reproduce

```bash
cmake -S engine -B engine/build -G Ninja -DCMAKE_BUILD_TYPE=Release && cmake --build engine/build
# per batch: a folder of links to that batch's images + a truth file with the operator's count
engine/build/bench <batch-dir> <batch>.operator.csv --budget auto --runs 1 --carry \
    --csv out.csv --codes codes.csv --misses misses.csv
engine/build/bench <batch-dir> <batch>.operator.csv --budget auto --runs 3 --csv fresh.csv
(cd engine/build && ./engine_tests)
```
