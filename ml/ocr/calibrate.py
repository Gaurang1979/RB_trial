"""Confidence calibration of the OCR line model: temperature scaling folded into the output layer.

    python -m ocr.calibrate --pt models/ocrnet.pt --set cij=<dir>=<labels.tsv> ... [--out models/ocrnet.onnx]

The engine shows each character's probability as its score and passes a character at min_quality
(60 by default), so the probabilities must mean what they say: a model that gives 0.95 to characters
it reads wrong 30% of the time lets wrong reads through. The temperature T (logits / T) that minimises
the CTC loss on held-out lines (synthetic lines of every print type, plus real lines) is the standard
fix; it changes no read (the argmax is the same), only the scores. T is folded into the last
convolution (weights and bias / T), so the ONNX input and output stay as the engine expects.

--temp T folds a chosen temperature instead. The NLL-best T calibrates the average character; the line
is judged by its weakest character, so a T chosen for line safety is usually higher: the highest T at
which every good real line still reads above min quality and passes its check (ocr.eval_ocr --temp on
the validation sets, never the test sets). ocrnet 2026-10-30: NLL-best 1.05, chosen 1.5 (at 2.0 good
real lines start to fall below 60).
"""

import argparse
import os

import cv2
import numpy as np
import torch
import torch.nn.functional as F

from . import synth_text as st
from .crnn import OcrNet
from .eval_ocr import load_set
from .train_ocr import encode, export


def logits_of(model, items):
    out = []
    with torch.no_grad():
        for p, text in items:
            img = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
            x = torch.from_numpy(img.astype(np.float32)[None, None] / 255.0)
            out.append((model(x)[0, :, 0, :].T.contiguous(), encode(text)))   # T x C
    return out


def ctc_nll(cached, temp):
    tot = n = 0.0
    for z, tg in cached:
        if not tg:
            continue
        lp = F.log_softmax(z / temp, 1)[:, None, :]
        loss = F.ctc_loss(lp, torch.tensor([tg]), torch.tensor([z.shape[0]]), torch.tensor([len(tg)]),
                          blank=0, reduction="sum", zero_infinity=True)
        tot += float(loss)
        n += len(tg)
    return tot / max(1.0, n)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pt", required=True)
    ap.add_argument("--width", type=int, default=32)
    ap.add_argument("--set", action="append", default=[], help="name=<folder>=<labels.tsv>, held out from training")
    ap.add_argument("--out", default="", help="write the calibrated model here (.onnx and .pt)")
    ap.add_argument("--temp", type=float, default=0, help="fold this temperature instead of the NLL-best one")
    a = ap.parse_args()
    model = OcrNet(len(st.CHARSET) + 1, a.width)
    model.load_state_dict(torch.load(a.pt, map_location="cpu"))
    model.eval()
    cached = []
    for s in a.set:
        name, items = load_set(s)
        c = logits_of(model, items)
        print(f"{name}: {len(c)} lines, NLL per character at T=1: {ctc_nll(c, 1.0):.4f}")
        cached += c
    grid = np.round(np.arange(0.7, 3.01, 0.05), 2)
    nll = [ctc_nll(cached, t) for t in grid]
    best = float(grid[int(np.argmin(nll))])
    print("T  NLL: " + "  ".join(f"{t:.2f} {v:.4f}" for t, v in zip(grid, nll) if abs(t * 4 - round(t * 4)) < 1e-6))
    print(f"chosen temperature {best:.2f} (NLL {min(nll):.4f}, at T=1 {ctc_nll(cached, 1.0):.4f})")
    if a.temp:
        best = a.temp
        print(f"folding the chosen temperature {best:.2f} (NLL {ctc_nll(cached, best):.4f})")
    if a.out:
        with torch.no_grad():
            model.head.weight /= best
            model.head.bias /= best
        os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
        export(model, a.out)


if __name__ == "__main__":
    main()
