# Truth files

What the sample images really hold, for `tools/bench/batch_check.py` (the `bench` heavy test)
and `engine/build/bench`. The images themselves are customer data and stay out of the
repository, in the folder `VS_IMAGES` (AGENTS.md). These files hold only names, counts and
hashes.

| File | Holds | Written by |
| --- | --- | --- |
| `samples.csv` | the codes visible per image | hand (owner's counts) |
| `codes.csv` | the true texts per image, as SHA-256 hashes | `batch_check.py --add-texts`, after a check |
| `batch-baseline.csv` | what main reads per image, and its slowest time | `batch_check.py --update` |

## samples.csv

```
file,format,expected,note
14--Raw.bmp,qr,24,by eye: 24 cans; each label carries the same QR twice
```

- `file`: the image's name in `VS_IMAGES`.
- `format`: the code type the operator sets: `qr`, `dm`, or a 1D name as in `bench.cpp`
  (`code128`, `ean13`, ...).
- `expected`: the distinct codes visible in the image. A code printed twice on one label
  counts once, because the engine reports each text once per image. `batch_check.py` sets the
  operator count from it (42-47 visible run as a 48 case; sizes that are not in the product
  master run as "other").
- `note`: free text without commas. Write who counted and how ("by eye", "owner 2026-10-23").
- Lines starting with `#` are comments.

## codes.csv: true texts (the wrong-read counter)

```
file,sha256,text
11.bmp,3f1c0e...64 hex digits,
```

- One row per distinct true text per image. An image with 48 codes has up to 48 rows.
- `sha256`: SHA-256 of the text exactly as the engine returns it (`cr_code.text`: the bytes of the
  decoded content; a GS1 FNC1 is ASCII 29, GS), as 64 hex digits. Case does not matter.
- `text`: empty for customer images, so the codes printed on a customer's packs never sit in the
  repository. Synthetic images may fill it in (the adversarial batch does).
- Lines starting with `#` are comments.

**A wrong read is a decoded text that is not among its image's rows.** That includes a true
text of another image, and every read on an image with no rows. `bench --texts truth/codes.csv`
counts and lists them; `batch_check.py` fails on any wrong read, whatever the read rate gained,
and never forgives one because a second run did not make it. A text read in the same image as
another code printed there (one code read as its neighbour) is not caught by the hash: it shows as
a miss.

### Adding texts

A change that reads a code for the first time reads a text that is not in the file yet, so the
check counts it as wrong until it is added. Add a text only after checking it:

1. Run `python3 tools/bench/batch_check.py --force` and look at the `WRONG READ` lines: image,
   text, the step that read it, its centre, and the start of its hash. Don't paste customer
   texts into pull requests or notes; name the image, the step and the hash.
2. Check each text: is the code there at that centre (the image), does the text fit the case
   (same product prefix and length, its serial inside the case's run and not already read), and
   does an independent reader agree (zxing-cpp or libdmtx on the crop, or a phone)? A text that
   only a soft path (`match`, `known`, `lattice`, `gridfit`, `frames`, `ai-enhance`) reads needs
   the independent reader or the owner.
3. Run `python3 tools/bench/batch_check.py --add-texts`. It runs each batch three times and
   appends every text read that is not in the file (hash only), so take this step only when
   step 2 cleared all of them. It saves no pass: run the check again afterwards.
4. Commit `codes.csv` with the change and write in the pull request how many texts were added
   per image and how they were checked.

Never remove a row to make the check pass. A row found to be wrong is removed in its own pull
request, with the owner's check.

The first rows (2026-10-31, PR #168) are what main read on the 53 local sample images in three
carried runs per batch (1,731 texts). Checks made: no image has more texts than codes visible in
`samples.csv`; per image every text has the same form (length, and letters and digits in the same
places); every text came from zxing-cpp or libdmtx after Reed-Solomon correction on a plain or
corrected image (plain, flat, deblur, upscale, full-image passes), none from a soft path. Where the
serials form a run (DM caps, bottles, the excise cases) they lie inside a short run; most cartons
and cans carry 9-digit serials that are not a run, so no run check applies to them. The owner's
spot check of the texts is still to come.

## The adversarial batch

`batch_check.py` also runs a batch of synthetic cases made by `engine/build/adversarial`
(source `tools/bench/adversarial.cpp`), drawn afresh from a fixed seed on each run and never
committed: product A's clean cases, a serial-run gap trap, codes one module off, codes a few
codewords off towards a neighbour that is not on the case, damage beyond error correction,
another product's codes, near-duplicates, scratches and blots, and textures, print and symbols
with random codewords but no code. Every case runs with the count set to 24, so the reader
searches as hard as on a short case. Its `codes.csv` has the same format, with `text` filled in,
and lists the codes printed on each image. The batch passes with 0 wrong reads; its read count
is shown but not judged.
