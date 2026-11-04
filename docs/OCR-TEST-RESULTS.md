# OCR / OCV test results on the real samples (2026-10-30)

Owner, 2026-10-30: "why ocv ocr results are only 92-93%? i need to read all failed samples stored in
mac at 100% readable." Pull request #142.

## Short answer

- The 92–93 % figures (full-system test, `docs/TEST-REPORT-2026-10-30.md`) come from **synthetic**
  sets made deliberately hard: 92.55 % of characters on generated laser lines, OCV 101 / 200 on
  generated laser images. The QR/DM bench figure of 92.8 % (1733 / 1868 codes) is a different tool.
- The only real OCR/OCV samples on this Mac are the **Harpic HWN laser line, 23 Oct 2026**: 489
  stored images. On them the reader was close to 100 % when PR #46/#77 measured it, and **PR #107**
  (line finder followed letter by letter, "a line cut short fails", 30 Oct) lowered it: main read
  452 of 475 readable images, against 472 before PR #107. It was measured only on synthetic images.
- This pull request brings main to **469 of 475** (plus the recovered image), reads every synthetic set
  exactly as main does (free read, wrong-read scores and check passes), and accepts no misprint anywhere. The 6 images still NOT
  OK are named below with their cause; each fails safe (NG).

## The real samples on this Mac

Searched: `~/code/Barcode_Reading/samples`, `VS_IMAGES` (not set), `ml/`, `truth/`, `~/Downloads`,
`~/Desktop`, `~/Documents`, and the dump folders named in `ml/ocr/label_real.py` and the OCR change
notes (by image type and by harpic, ocr, laser, batch, expiry).

| Set | Where (not in the repository) | Images | Print |
| --- | --- | ---: | --- |
| Harpic HWN937_110, camera 1 | `~/Downloads/HWN937_110/Camera 1/Fail` | 489 BMP, 2048 × 1536 | laser, light print in a dark box: "₹ 46.00, HWN881" (HWN937 from 16:55), "₹ 0.23 per ml", "¥ 10/26, # 09/28 HH:MM" |
| Its line crops (PR #77) | `~/Downloads/HWN937_110/ocr_lines` | 3,839 PNG, labels for 1,306 | the same print |
| Barcode samples | `~/code/Barcode_Reading/samples/Barcode Images` (and a copy in `~/Downloads`) | 58 BMP | QR / Data Matrix only, no OCR job |

No real CIJ, TTO or embossed print is stored on this Mac.

Of the 489 images:

- **475 readable**, labelled by eye: the batch and the time of every image were checked on line crops,
  and on the full image wherever the reader disagreed. The old labels (`ocr_lines/labels.tsv`, made
  from the capture time + 79 s) were wrong for 10 images: the printer's clock lags, and "16:01" was
  printed as 16:00 (the training and test files of PR #77 already had them right).
- **9 not readable** (correctly NOT OK): 6 show no print (the cap, a dark frame, a bottle turned with
  the box out of view); 3 show the bottle turned 90° with a glare stripe hiding characters.
- **5 files cut short on disk.** Four hold only the file header (54 bytes, nothing to read). One,
  `20261023_160924359.bmp`, holds the bottom 924 rows, which contain the whole print; recovered to
  `~/Downloads/HWN937_110/recovered/` (read OK on main and on this branch: HWN881, 16:10).

Images captured before 15:58 gave the real training lines of PR #77; the 148 readable images from 15:58
on are held out (never trained on).

## Method

`engine/build/ocr_bench` on the folder, truth file outside the repository (one row per image: the
three lines, checked by eye), the job's settings: `--roi 400,600,1250,750 --char-h 38 --polarity light
--budget 5000`, AI model `ml/models/ocrnet.onnx`. `--mutate` checks each image once more against its
text with one character changed: it must be NOT OK. The time budget is raised as in PR #77 because ten
sessions share this Mac; reads do not change with it. Synthetic sets: `ocr.synth_scene --n 150 --seed
31` (PR #107's set, four print types) and `--kind laser --n 200 --seed 31` (the full-system report's).

## Results: main → this branch

Real Harpic laser images (0 misprints accepted on main and on the branch in every row):

| Test | Before PR #107 | Main | This branch |
| --- | ---: | ---: | ---: |
| All 475 readable, check against the printed text | 472 | 452 | **469** |
| Held out (148, from 15:58), check against the text | 147 | 139 | **146** |
| Line job "HWN881 + `{TIME:HH:mm+-3}`", clock from the file name: HWN881 bottles OK | 452 / 455 | 438 / 455 | **450 / 455** |
| Same job: HWN937 bottles (wrong batch) OK | 0 / 20 | 0 / 20 | 0 / 20 |
| Same job: not readable images OK | 0 / 9 | 0 / 9 | 0 / 9 |
| Free read, characters right | 99.94 % | 99.96 % | 99.96 % |

Synthetic whole images (main → branch, both with PR #131 merged):

| Set | Characters right | Wrong reads ≥ min quality | Check passes | One character changed accepted |
| --- | --- | --- | --- | --- |
| laser, 150 | 92.46 → 92.46 % | 14 → 14 | 92 → 92 | 0 → 0 |
| TTO, 150 | 95.70 → 95.70 % | 10 → 10 | 97 → 97 | 0 → 0 |
| CIJ dot, 150 | 63.07 → 63.07 % | 2 → 2 | 7 → 7 | 0 → 0 |
| embossed, 150 | 48.17 → 48.17 % | 16 → 16 | 17 → 17 | 0 → 0 |
| laser, 200 (full-system report) | 92.99 → 92.99 % | 16 → 16 | 125 → 125 | 0 → 0 |

## What was wrong, and the fix

PR #107 made a line **cut short** (scored min quality − 1, so 59, NOT OK) when stroke ink lies just past
its ends. On this product the time line ends about one capital before the edge of the dark print box,
and the label's bright margin beyond that edge gives the same stroke response as a letter. 17 good
images failed on it.

Fix (`engine/src/ocr/lines.cpp`, `inkPastEnds`): ink past the end is not the line's when it is a
region border, told from print by its ground. Within a capital from where it starts, 80 % of the band
over a letter's width, and again over the next letter's width, lies beyond the line's own ink level;
text never fills that much. It must start at least the line's usual letter gap plus 0.3 capitals from
the end (0.5 at least), so a next letter at the line's spacing would have shown before it. Nothing
else in the line finder changes, so every line not next to such a border is found and boxed exactly
as on main (the synthetic sets read the same).

Tried and not kept (each raised the wrong reads scoring at or above min quality on the synthetic laser
sets, or broke the dot-print tests): taking the band per quarter of the line, the region's angle from
text lines only, and widening a line to hold one faint letter past its end (the reader then ignored a
faint "." at a score of 100).

## Still NOT OK (6), with cause

| Image | Cause |
| --- | --- |
| 20261023_154234728 | The print box lies past the fixed text area: a job setting. With the area over the box (`--roi 550,600,1250,750`) it reads OK (time line 88). On the line, Find the part moves the area with the bottle. |
| 20261023_155236729 | A thin thread lies across "09/28 15:52". A person can read it; the reader cannot see through it. Fix: train the model with thin lines across print. |
| 20261023_154551559 | "15:47": the faint 7 scores as "/" (check 48). Read OK before PR #107: the line is boxed differently since. |
| 20261023_155747908 | "15:59" read "15:39": the line bows up toward its end and the band clips the top of the last digits. Read OK before PR #107. |
| 20261023_165816198 | The faint "l" of "ml" is not part of the found line, so the line is cut short (59): a safe NOT OK. Read OK before PR #107. |
| 20261023_160446549 | "16:06" read "16:0b", and the faint "." of 0.23 scores low. Failed before PR #107 too. |

All six fail safe: NOT OK, never a wrong OK. The three lines that read before PR #107 depend on how a
line is boxed (band height on a bowed label, a faint end letter); changing that moved synthetic wrong
reads above min quality, so it is left for a measured follow-up together with retraining the model on
line crops from the current finder.

## Re-run

```bash
cd engine && cmake -S . -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build --target ocr_bench
build/ocr_bench "$HOME/Downloads/HWN937_110/Camera 1/Fail" <truth.tsv> --model ../ml/models/ocrnet.onnx \
    --charset ../ml/models/ocr_charset.txt --roi 400,600,1250,750 --char-h 38 --polarity light --budget 5000 --mutate
```

The truth file (475 rows checked by eye) is kept outside the repository with the images.

## Per image (the 484 stored images that open; the 5 cut-short files are described under "The real samples")


| Image | Batch, time (by eye) | Main | Branch | Cause when not OK |
| --- | --- | :---: | :---: | --- |
| 20261023_153835456.bmp | HWN881, 15:39 | OK | OK |  |
| 20261023_153836671.bmp | HWN881, 15:39 | OK | OK |  |
| 20261023_153842131.bmp | HWN881, 15:39 | OK | OK |  |
| 20261023_153908881.bmp | not readable | NG | NG | no print in view (camera saw the cap / nothing) |
| 20261023_153918582.bmp | HWN881, 15:40 | OK | OK |  |
| 20261023_153936232.bmp | not readable | NG | NG | no print in view (dark frame) |
| 20261023_154140122.bmp | HWN881, 15:41 | OK | OK |  |
| 20261023_154146643.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154147056.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154147448.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154148317.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154149104.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154151096.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154151588.bmp | not readable | NG | NG | bottle turned 90 deg, glare stripe hides characters (W of HWN, #) |
| 20261023_154152424.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154153709.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154155286.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154155660.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154209764.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154216715.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154217857.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154220376.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154222025.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154222851.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154225694.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154227322.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154231399.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154232196.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154234728.bmp | HWN881, 15:43 | NG | NG | print box lies past the fixed text area (x 400-1650): job setting; reads OK with the area over the box (x 550-1800), as Find the part would place it |
| 20261023_154237131.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154238395.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154238753.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154240820.bmp | HWN881, 15:43 | OK | OK |  |
| 20261023_154243725.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154247356.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154247629.bmp | HWN881, 15:44 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_154247873.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154255231.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154256052.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154257671.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154258496.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154302212.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154303823.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154305899.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154307213.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154307569.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154308194.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154309256.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154312582.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154315418.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154317463.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154318740.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154319532.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154319911.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154320752.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154322007.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154322368.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154323657.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154324023.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154325272.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154327365.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154328074.bmp | HWN881, 15:44 | OK | OK |  |
| 20261023_154333803.bmp | HWN881, 15:44 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_154439187.bmp | HWN881, 15:45 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_154441247.bmp | HWN881, 15:45 | OK | OK |  |
| 20261023_154443265.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154444504.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154454711.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154457594.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154501031.bmp | not readable | NG | NG | no print in view (cap) |
| 20261023_154513367.bmp | not readable | NG | NG | no print in view (dark frame) |
| 20261023_154518785.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154521214.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154522430.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154527749.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154528578.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154529041.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154529856.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154537634.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154540909.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154541283.bmp | HWN881, 15:46 | OK | OK |  |
| 20261023_154542543.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154543793.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154546214.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154547027.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154547442.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154551559.bmp | HWN881, 15:47 | NG | NG | "15:47": the faint 7 is scored as "/" (check 48); the line is boxed differently since PR #107 (read OK before it) |
| 20261023_154552123.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154554876.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154559485.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154603262.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154606961.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154608948.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154610220.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154615091.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154616757.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154617707.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154618462.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154618877.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154619228.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154620924.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154621285.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154623788.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154624618.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154627474.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154629930.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154631500.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154638501.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154640605.bmp | HWN881, 15:47 | OK | OK |  |
| 20261023_154643459.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154657810.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154658187.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154700659.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154704385.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154706387.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154708447.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154715384.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154716613.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154718690.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154719077.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154719709.bmp | HWN881, 15:48 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_154720394.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154722014.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154723287.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154723678.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154724049.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154726316.bmp | not readable | NG | NG | no print in view (bottle turned, print box out of frame) |
| 20261023_154729066.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154729548.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154732003.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154735290.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154736099.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154740595.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154741001.bmp | HWN881, 15:48 | OK | OK |  |
| 20261023_154741831.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154742197.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154745501.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154747973.bmp | HWN881, 15:49 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_154750408.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154753226.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154801045.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154805046.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154806615.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154807321.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154808908.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154810161.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154811761.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154812143.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_154814567.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_155002192.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_155005867.bmp | HWN881, 15:49 | OK | OK |  |
| 20261023_155010057.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155016992.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155017373.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155018164.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155020234.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155030650.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155031543.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155032290.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155033089.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155034771.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155036024.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155036378.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155036863.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155038852.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155039684.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155040851.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155041333.bmp | HWN881, 15:51 | OK | OK |  |
| 20261023_155041737.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155042111.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155042967.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155043383.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155043744.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155044205.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155044588.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155044986.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155045464.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155045812.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155047890.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155048302.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155048696.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155049063.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155050796.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155051206.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155053584.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155054041.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155054865.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155057758.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155058356.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155100660.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155107900.bmp | not readable | NG | NG | no print in view (cap) |
| 20261023_155111886.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155204157.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155207814.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155215337.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155216591.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155224702.bmp | HWN881, 15:53 | OK | OK |  |
| 20261023_155227237.bmp | HWN881, 15:53 | OK | OK |  |
| 20261023_155231298.bmp | HWN881, 15:53 | OK | OK |  |
| 20261023_155234419.bmp | HWN881, 15:53 | OK | OK |  |
| 20261023_155235099.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155236729.bmp | HWN881, 15:52 | NG | NG | a thin thread lies across "09/28 15:52": readable by eye, the reader cannot see through it (model; train with occlusions) |
| 20261023_155249814.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155253457.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155253806.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155254655.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155256379.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155257620.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155300565.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155301292.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155303006.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155304262.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155305456.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155305833.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155306301.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155306664.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155308727.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155309539.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155311624.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155311999.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155313709.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155314887.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155316147.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155323516.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155324209.bmp | HWN881, 15:54 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_155324609.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155324869.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155325343.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155331453.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155332698.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155333510.bmp | HWN881, 15:52 | OK | OK |  |
| 20261023_155337609.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155340066.bmp | HWN881, 15:54 | OK | OK |  |
| 20261023_155341701.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155343717.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155347487.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155348089.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155349635.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155350280.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155350560.bmp | HWN881, 15:55 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_155351399.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155351779.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155352595.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155357561.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155358341.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155358816.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155359166.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155359646.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155400042.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155401249.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155401615.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155404537.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155404903.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155406165.bmp | HWN881, 15:55 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_155408645.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155409814.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155411107.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155412682.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155418899.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155420940.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155425017.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155428285.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155429949.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155432385.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155432747.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155434815.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155437646.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155438884.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155440586.bmp | HWN881, 15:55 | OK | OK |  |
| 20261023_155443031.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155445486.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155445865.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155446327.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155446689.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155447145.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155447934.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155448768.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155450012.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155453672.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155456552.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155457812.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155458169.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155458990.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155459817.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155500638.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155500998.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155501463.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155507617.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155508037.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155508418.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155509235.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155510053.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155510436.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155511293.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155513363.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155518663.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155521914.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155526804.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155527793.bmp | not readable | NG | NG | bottle turned 90 deg, glare stripe hides characters (H of HWN, per, #) |
| 20261023_155528523.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155528944.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155542523.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155542944.bmp | HWN881, 15:56 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_155543364.bmp | HWN881, 15:56 | OK | OK |  |
| 20261023_155552834.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155554639.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155555658.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155556664.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155557049.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155557709.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155557978.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155600014.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155600904.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155602134.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155602504.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155604094.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155609439.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155611544.bmp | HWN881, 15:57 | OK | OK |  |
| 20261023_155722974.bmp | HWN881, 15:58 | OK | OK |  |
| 20261023_155725032.bmp | HWN881, 15:58 | OK | OK |  |
| 20261023_155726589.bmp | HWN881, 15:58 | OK | OK |  |
| 20261023_155730230.bmp | HWN881, 15:58 | OK | OK |  |
| 20261023_155738474.bmp | HWN881, 15:58 | OK | OK |  |
| 20261023_155738860.bmp | HWN881, 15:58 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_155743009.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155747908.bmp | HWN881, 15:59 | NG | NG | "15:59" read "15:39": the line bows up toward its end and the band clips the top of the last digits (read OK before PR #107) |
| 20261023_155750008.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155750443.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155752035.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155756946.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155758198.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155758590.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155759067.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155759445.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155759809.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155800644.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155801517.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155801875.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155803127.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155804385.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155806856.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155808049.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155809715.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155810570.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155825263.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155827949.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155828689.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155833650.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155834010.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155836097.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155838886.bmp | HWN881, 15:59 | OK | OK |  |
| 20261023_155848528.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_155911549.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_155915820.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_155917896.bmp | not readable | NG | NG | bottle turned 90 deg, glare stripe hides characters (H of HWN, per, #) |
| 20261023_155947809.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_155950647.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_155951922.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_155957866.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_160002022.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_160002292.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_160002536.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_160002796.bmp | HWN881, 16:00 | OK | OK |  |
| 20261023_160010196.bmp | HWN881, 16:01 | OK | OK |  |
| 20261023_160012577.bmp | HWN881, 16:01 | OK | OK |  |
| 20261023_160013590.bmp | HWN881, 16:01 | OK | OK |  |
| 20261023_160015355.bmp | HWN881, 16:01 | OK | OK |  |
| 20261023_160023729.bmp | HWN881, 16:01 | OK | OK |  |
| 20261023_160025709.bmp | HWN881, 16:01 | OK | OK |  |
| 20261023_160032248.bmp | HWN881, 16:01 | OK | OK |  |
| 20261023_160038417.bmp | HWN881, 16:01 | OK | OK |  |
| 20261023_160131724.bmp | HWN881, 16:02 | OK | OK |  |
| 20261023_160133286.bmp | HWN881, 16:02 | OK | OK |  |
| 20261023_160151460.bmp | HWN881, 16:03 | OK | OK |  |
| 20261023_160201944.bmp | HWN881, 16:03 | OK | OK |  |
| 20261023_160216595.bmp | HWN881, 16:03 | OK | OK |  |
| 20261023_160227820.bmp | HWN881, 16:03 | OK | OK |  |
| 20261023_160228090.bmp | HWN881, 16:03 | OK | OK |  |
| 20261023_160235915.bmp | HWN881, 16:03 | OK | OK |  |
| 20261023_160239542.bmp | HWN881, 16:03 | OK | OK |  |
| 20261023_160247893.bmp | HWN881, 16:04 | OK | OK |  |
| 20261023_160257826.bmp | HWN881, 16:04 | OK | OK |  |
| 20261023_160304324.bmp | HWN881, 16:04 | OK | OK |  |
| 20261023_160305210.bmp | HWN881, 16:04 | OK | OK |  |
| 20261023_160310126.bmp | HWN881, 16:04 | OK | OK |  |
| 20261023_160320143.bmp | HWN881, 16:04 | OK | OK |  |
| 20261023_160328607.bmp | HWN881, 16:04 | OK | OK |  |
| 20261023_160333191.bmp | HWN881, 16:04 | OK | OK |  |
| 20261023_160433780.bmp | HWN881, 16:04 | OK | OK |  |
| 20261023_160434149.bmp | HWN881, 16:04 | OK | OK |  |
| 20261023_160446549.bmp | HWN881, 16:06 | NG | NG | "16:06" read "16:0b" and the faint "." of 0.23 scores low (failed before PR #107 too, on the ".") |
| 20261023_160447766.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160449543.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160451542.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160451787.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160453693.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160454046.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160455783.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160456575.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160458182.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160459863.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160510859.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160519927.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160531054.bmp | HWN881, 16:06 | OK | OK |  |
| 20261023_160548569.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160558453.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160600962.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160603059.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160605218.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160612483.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160615404.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160619492.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160625213.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160629810.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160634851.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160642876.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160644407.bmp | HWN881, 16:07 | OK | OK |  |
| 20261023_160644661.bmp | HWN881, 16:07 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_160650800.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160652443.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160652903.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160659099.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160704772.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160708878.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160711964.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160712222.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160713480.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160715845.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160717558.bmp | HWN881, 16:08 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_160724843.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160734722.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160736687.bmp | HWN881, 16:08 | OK | OK |  |
| 20261023_160745129.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160746202.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160748276.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160749899.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160752855.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160754889.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160755278.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160813297.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160820202.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160820594.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160822649.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160823912.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160826738.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160835754.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160837771.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160838665.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160839512.bmp | HWN881, 16:09 | OK | OK |  |
| 20261023_160841932.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160845047.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160846511.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160859233.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160903295.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160903893.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160907457.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160909960.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160914066.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160915694.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160917329.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160922207.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_160923655.bmp | HWN881, 16:10 | OK | OK |  |
| 20261023_162051691.bmp | HWN881, 16:22 | OK | OK |  |
| 20261023_162052131.bmp | HWN881, 16:22 | OK | OK |  |
| 20261023_165544046.bmp | HWN937, 16:57 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_165553301.bmp | HWN937, 16:57 | OK | OK |  |
| 20261023_165613536.bmp | HWN937, 16:57 | OK | OK |  |
| 20261023_165631066.bmp | HWN937, 16:57 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_165637582.bmp | HWN937, 16:57 | OK | OK |  |
| 20261023_165741257.bmp | HWN937, 16:58 | OK | OK |  |
| 20261023_165754277.bmp | HWN937, 16:59 | OK | OK |  |
| 20261023_165759482.bmp | HWN937, 16:59 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_165816198.bmp | HWN937, 16:59 | NG | NG | the faint "l" of "ml" is not part of the found line: the line is cut short (59), a safe NG (read OK before PR #107) |
| 20261023_165819867.bmp | HWN937, 16:59 | OK | OK |  |
| 20261023_165820228.bmp | HWN937, 16:59 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_165821480.bmp | HWN937, 16:59 | OK | OK |  |
| 20261023_165822558.bmp | HWN937, 16:59 | OK | OK |  |
| 20261023_165823607.bmp | HWN937, 16:59 | OK | OK |  |
| 20261023_165824438.bmp | HWN937, 16:59 | OK | OK |  |
| 20261023_165828098.bmp | HWN937, 16:59 | NG | OK | read right, scored 59: "line cut short" (PR #107) fired on the label margin beside the print box |
| 20261023_165830198.bmp | HWN937, 16:59 | OK | OK |  |
| 20261023_165846948.bmp | HWN937, 17:00 | OK | OK |  |
| 20261023_165858938.bmp | HWN937, 17:00 | OK | OK |  |
| 20261023_165904623.bmp | HWN937, 17:00 | OK | OK |  |
