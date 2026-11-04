"""Label real straightened lines (from `ocr_bench --dump`) with known text, for training and testing.

For a product whose print is known except for a few fields, every line crop is scored against each
candidate text with the current recogniser (CTC likelihood): the batch alternatives, and the print time
from the image's file name (YYYYMMDD_HHMMSSmmm.bmp) within a window. A crop gets a label only when one
candidate clearly wins; everything else is listed for review.

    python -m ocr.label_real --crops <dump folder> --model models/ocrnet.onnx \
        --line "₹ 46.00, HWN{881|937}" --line "₹ 0.23 per ml" --line "¥ 10/26, # 09/28 {TIME}" \
        --time-window -5,2 --out-crops labels.tsv --out-truth ../truth/ocr_harpic.tsv --review review.png

{a|b|c} lists alternatives; {TIME} is HH:MM from the file name, shifted by each minute of --time-window.

--time-offset S: when the print time is the capture time plus a constant (printer clock and conveyor delay,
found by fitting the labels: 79 s on the first line tested), {TIME} is that one value, not a window, and
crops within --time-guard seconds of a minute change get no label. A recogniser that misreads a faint
last digit as a neighbouring minute in the window then cannot label its own mistake.
--out-truth writes one row per image (file, then its lines top to bottom) for ocr_bench, with only the
images whose every --line was found once.
"""

import argparse
import datetime as dt
import glob
import itertools
import os
import re

import cv2
import numpy as np
import onnxruntime as ort
import torch
import torch.nn.functional as F

from . import synth_text as st
from .train_ocr import decode_greedy

IDX = {c: i + 1 for i, c in enumerate(st.CHARSET)}


def expand(pattern, when, window, offset=None, guard=5):
    parts = re.split(r"(\{[^}]*\})", pattern)
    opts = []
    for p in parts:
        if p.startswith("{") and p.endswith("}"):
            body = p[1:-1]
            if body == "TIME" and offset is not None:
                t = when + dt.timedelta(seconds=offset)
                if t.second < guard or t.second >= 60 - guard:
                    return []                      # too close to a minute change: no label
                opts.append([t.strftime("%H:%M")])
            elif body == "TIME":
                opts.append(sorted({(when + dt.timedelta(minutes=m)).strftime("%H:%M") for m in range(window[0], window[1] + 1)}))
            else:
                opts.append(body.split("|"))
        else:
            opts.append([p])
    return ["".join(x) for x in itertools.product(*opts)]


def nll_per_char(logp, text):
    """logp: T x C log-probabilities. Returns CTC negative log-likelihood / characters (spaces dropped)."""
    t = [IDX[c] for c in text if c in IDX and c != " "]
    if not t or len(t) > logp.shape[0]:
        return float("inf")
    lp = torch.from_numpy(logp)[:, None, :]
    loss = F.ctc_loss(lp, torch.tensor([t]), torch.tensor([logp.shape[0]]), torch.tensor([len(t)]),
                      blank=0, reduction="sum", zero_infinity=False)
    return float(loss) / len(t)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--crops", required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--line", action="append", required=True)
    ap.add_argument("--time-window", default="-5,2")
    ap.add_argument("--time-offset", type=float, default=None, help="print time = capture time + this many seconds")
    ap.add_argument("--time-guard", type=float, default=5)
    ap.add_argument("--max-nll", type=float, default=2.5, help="largest NLL per character that is accepted")
    ap.add_argument("--margin", type=float, default=0.5, help="NLL per character the best must beat the next by")
    ap.add_argument("--out-crops", required=True)
    ap.add_argument("--out-truth", default="")
    ap.add_argument("--review", default="")
    a = ap.parse_args()
    win = tuple(int(x) for x in a.time_window.split(","))
    sess = ort.InferenceSession(a.model, providers=["CPUExecutionProvider"])

    crops = sorted(glob.glob(os.path.join(a.crops, "*_L*.png")))
    by_image = {}
    for p in crops:
        stem, li = os.path.basename(p).rsplit("_L", 1)
        by_image.setdefault(stem, []).append((int(li.split(".")[0]), p))

    labels, truth, review = [], [], []
    stats = {"images": 0, "complete": 0, "crops_labelled": 0}
    for stem, items in sorted(by_image.items()):
        stats["images"] += 1
        m = re.match(r"(\d{8})_(\d{6})(\d{3})?", stem)
        when = dt.datetime.strptime(m.group(1) + m.group(2), "%Y%m%d%H%M%S") if m else dt.datetime.now()
        if m and m.group(3):
            when += dt.timedelta(milliseconds=int(m.group(3)))
        found = {}
        for li, p in sorted(items):
            img = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
            logits = sess.run(None, {"line": img.astype(np.float32)[None, None] / 255.0})[0][0, :, 0, :]  # C x T
            logp = torch.log_softmax(torch.from_numpy(logits.T), 1).numpy()
            read = decode_greedy(torch.from_numpy(logits)).replace(" ", "")
            scored = []
            for k, pat in enumerate(a.line):
                for cand in expand(pat, when, win, a.time_offset, a.time_guard):
                    scored.append((nll_per_char(logp, cand), k, cand))
            scored.sort()
            best = scored[0]
            nxt = next((s for s in scored[1:] if s[2] != best[2]), (float("inf"), -1, ""))
            # a label is accepted when the free read is exactly one candidate (spaces aside), or when
            # one candidate is clearly more likely than every other
            exact = [x for x in scored if x[2].replace(" ", "") == read]
            if exact:
                best = exact[0]
            if exact or (best[0] <= a.max_nll and nxt[0] - best[0] >= a.margin):
                labels.append((os.path.basename(p), best[2]))
                stats["crops_labelled"] += 1
                if best[1] not in found:
                    found[best[1]] = best[2]
                else:
                    found[best[1]] = None          # the same line twice: not a clean image
            elif best[0] <= 3 * a.max_nll:
                review.append((p, best[2], best[0], nxt[2], nxt[0]))
        if len(found) == len(a.line) and all(found.values()):
            stats["complete"] += 1
            truth.append((stem + ".bmp", [found[k] for k in range(len(a.line))]))

    with open(a.out_crops, "w", encoding="utf-8") as f:
        f.write("# file\ttext   (ml/ocr/label_real.py)\n")
        for name, text in labels:
            f.write(f"{name}\t{text}\n")
    if a.out_truth:
        with open(a.out_truth, "w", encoding="utf-8") as f:
            f.write("# file\tline 1\tline 2\t...   (ml/ocr/label_real.py; spaces are not checked)\n")
            for name, lines in truth:
                f.write(name + "\t" + "\t".join(lines) + "\n")
    print(stats, "review", len(review))
    for p, b, bn, n, nn in review[:30]:
        print(f"  review {os.path.basename(p)}: {b!r} {bn:.2f}  vs {n!r} {nn:.2f}")
    if a.review and review:
        rows = []
        for p, b, *_ in review[:40]:
            im = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
            rows.append(im)
        W = max(r.shape[1] for r in rows)
        cv2.imwrite(a.review, np.vstack([np.pad(r, ((2, 2), (0, W - r.shape[1])), constant_values=128) for r in rows]))


if __name__ == "__main__":
    main()
