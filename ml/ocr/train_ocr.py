"""Train the OCR line recogniser (ocrnet) with CTC on synthetic lines, plus labelled real lines.

    python -m ocr.train_ocr --out models/ocrnet.onnx --steps 30000
    python -m ocr.train_ocr --out models/ocrnet.onnx --real <crops folder>=<labels.tsv> --init models/ocrnet.pt

Real lines are straightened crops written by `ocr_bench --dump`; labels.tsv has `file<TAB>text`
(see ocr/label_real.py). The gate: a new model is saved only when its character accuracy on the
held-out real lines (--test) is not lower than the current model's, as in ml/retrain.py.
"""

import argparse
import math
import os
import random
import time

import cv2
import numpy as np
import torch
import torch.nn.functional as F
from torch.utils.data import DataLoader, IterableDataset

from . import synth_text as st
from .crnn import OcrNet

CHARS = st.CHARSET
IDX = {c: i + 1 for i, c in enumerate(CHARS)}


def encode(text):
    return [IDX[c] for c in text if c in IDX]


def decode_greedy(logits):
    """logits: C x T -> text"""
    best = logits.argmax(0).tolist()
    out, prev = [], 0
    for b in best:
        if b != prev and b != 0:
            out.append(CHARS[b - 1])
        prev = b
    return "".join(out)


def load_labelled(spec):
    folder, tsv = spec.split("=", 1)
    items = []
    with open(tsv, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                continue
            name, text = line.split("\t", 1)
            p = os.path.join(folder, name)
            if os.path.exists(p):
                items.append((p, text))
    return items


def augment_real(img, rng):
    """Mild changes to a real straightened line: scale, shift, blur, noise, contrast."""
    h, w = img.shape
    sx = rng.uniform(0.88, 1.12)
    if rng.random() < 0.3:
        sx *= rng.uniform(0.8, 1.25)            # CIJ-like stretch
    sy = rng.uniform(0.92, 1.06)
    t = np.tan(np.deg2rad(rng.uniform(-3, 3)))
    W = max(16, int(math.ceil(w * sx / 4)) * 4)
    M = np.array([[sx, -t * sy, t * h / 2 * sy], [0, sy, (1 - sy) * h / 2 + rng.uniform(-1.5, 1.5)]], np.float32)
    img = cv2.warpAffine(img, M, (W, h), borderMode=cv2.BORDER_REPLICATE)
    f = img.astype(np.float32)
    if rng.random() < 0.4:
        f = cv2.GaussianBlur(f, (0, 0), rng.uniform(0.3, 1.0))
    f = f * rng.uniform(0.7, 1.0) + rng.uniform(0, 60)
    f += np.random.default_rng(rng.randint(0, 1 << 30)).normal(0, rng.uniform(0, 8), f.shape)
    lo, hi = np.percentile(f, (1, 99))
    return np.clip((f - lo) / max(hi - lo, 8) * 255, 0, 255).astype(np.uint8)


class Lines(IterableDataset):
    """Real lines (real_frac of the time), else synthetic: drawn from pre-rendered lines when given
    (rendering is the slow part of training: ocr.eval_ocr --make writes them), else rendered now."""

    def __init__(self, real, real_frac, seed, rendered=()):
        self.real, self.real_frac, self.seed, self.rendered = real, real_frac, seed, list(rendered)

    def __iter__(self):
        info = torch.utils.data.get_worker_info()
        rng = random.Random(self.seed + (info.id if info else 0) * 7919 + int(time.time()))
        cache = {}
        while True:
            if self.real and rng.random() < self.real_frac:
                p, text = rng.choice(self.real)
                if p not in cache:
                    cache[p] = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
                img = augment_real(cache[p], rng)
            elif self.rendered:
                p, text = rng.choice(self.rendered)
                img = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
                if rng.random() < 0.5:
                    img = augment_real(img, rng)
            else:
                img, text = st.sample(rng)
            if img.shape[1] > MAX_W:
                continue
            yield img, text


BUCKETS = (128, 192, 256, 320, 384, 448, 512, 640)   # fixed widths: MPS recompiles on every new shape
MAX_W = BUCKETS[-1]


def collate(batch):
    W = next(b for b in BUCKETS if b >= max(x[0].shape[1] for x in batch))
    x = np.full((len(batch), 1, st.LINE_H, W), 1.0, np.float32)
    targets, lens = [], []
    for i, (img, text) in enumerate(batch):
        x[i, 0, :, :img.shape[1]] = img.astype(np.float32) / 255.0
        t = encode(text)
        targets += t
        lens.append(len(t))
    return torch.from_numpy(x), torch.tensor(targets, dtype=torch.long), torch.tensor(lens, dtype=torch.long), [b[1] for b in batch]


def char_accuracy(model, items, device):
    """Character accuracy (1 - edit distance / length, spaces ignored) on labelled real lines."""
    if not items:
        return float("nan")
    model.eval()
    errs = tot = 0
    with torch.no_grad():
        for p, text in items:
            img = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
            x = torch.from_numpy(img.astype(np.float32)[None, None] / 255.0).to(device)
            pred = decode_greedy(model(x)[0, :, 0].cpu()).replace(" ", "")
            truth = text.replace(" ", "")
            errs += _edit(pred, truth)
            tot += len(truth)
    model.train()
    return 1.0 - errs / max(1, tot)


def _edit(a, b):
    d = list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        prev, d[0] = d[0], i
        for j in range(1, len(b) + 1):
            cur = d[j]
            d[j] = min(d[j] + 1, d[j - 1] + 1, prev + (a[i - 1] != b[j - 1]))
            prev = cur
    return d[len(b)]


def export(model, out):
    model.eval().cpu()
    torch.save(model.state_dict(), os.path.splitext(out)[0] + ".pt")
    torch.onnx.export(model, torch.zeros(1, 1, st.LINE_H, 256), out, input_names=["line"], output_names=["logits"],
                      dynamic_axes={"line": {3: "w"}, "logits": {3: "t"}}, opset_version=17, dynamo=False)
    print("saved", out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="models/ocrnet.onnx")
    ap.add_argument("--steps", type=int, default=30000)
    ap.add_argument("--batch", type=int, default=64)
    ap.add_argument("--lr", type=float, default=2e-3)
    ap.add_argument("--width", type=int, default=32)
    ap.add_argument("--real", action="append", default=[], help="<crops folder>=<labels.tsv> for training")
    ap.add_argument("--test", action="append", default=[], help="<crops folder>=<labels.tsv> held out for the gate")
    ap.add_argument("--real-frac", type=float, default=0.3)
    ap.add_argument("--rendered", action="append", default=[],
                    help="<folder>=<labels.tsv> of pre-rendered synthetic lines, used instead of rendering")
    ap.add_argument("--init", default="", help="start from these weights (.pt)")
    ap.add_argument("--workers", type=int, default=6)
    ap.add_argument("--force", action="store_true", help="save even if the gate fails")
    ap.add_argument("--ckpt-every", type=int, default=2000, help="save <out>.partial.pt every n steps (0: never)")
    a = ap.parse_args()

    device = "mps" if torch.backends.mps.is_available() else ("cuda" if torch.cuda.is_available() else "cpu")
    model = OcrNet(len(CHARS) + 1, a.width)
    if a.init:
        model.load_state_dict(torch.load(a.init, map_location="cpu"))
    model.to(device)
    real = [x for s in a.real for x in load_labelled(s)]
    test = [x for s in a.test for x in load_labelled(s)]
    print(f"device {device}, params {sum(p.numel() for p in model.parameters())}, real {len(real)}, test {len(test)}")

    baseline = None
    pt = os.path.splitext(a.out)[0] + ".pt"
    if test and os.path.exists(pt):
        old = OcrNet(len(CHARS) + 1, a.width)
        try:
            old.load_state_dict(torch.load(pt, map_location="cpu"))
            baseline = char_accuracy(old.to(device), test, device)
            print(f"current model on test: {baseline:.4f}")
        except Exception as e:
            print("current model not comparable:", e)

    rendered = [x for s in a.rendered for x in load_labelled(s)]
    dl = DataLoader(Lines(real, a.real_frac if real else 0, 1234, rendered), batch_size=a.batch, num_workers=a.workers,
                    collate_fn=collate, persistent_workers=a.workers > 0, prefetch_factor=2 if a.workers else None)
    opt = torch.optim.AdamW(model.parameters(), lr=a.lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=a.lr, total_steps=a.steps, pct_start=0.05)
    ctc = torch.nn.CTCLoss(blank=0, zero_infinity=True)
    model.train()
    t0 = time.time()
    step = 0
    run = 0.0
    for x, tg, lens, texts in dl:
        x = x.to(device)
        logits = model(x)[:, :, 0, :]                      # N x C x T
        lp = F.log_softmax(logits, 1).permute(2, 0, 1)     # T x N x C
        T = lp.shape[0]
        loss = ctc(lp.cpu(), tg, torch.full((x.shape[0],), T, dtype=torch.long), lens)
        opt.zero_grad()
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
        opt.step()
        sched.step()
        run = 0.98 * run + 0.02 * loss.item() if step else loss.item()
        step += 1
        if step % 200 == 0:
            msg = f"step {step}  loss {run:.3f}  {time.time() - t0:.0f}s"
            if step % 2000 == 0:
                msg += f"  sample: {texts[0]!r} -> {decode_greedy(logits[0].detach().cpu())!r}"
                if test:
                    msg += f"  test acc {char_accuracy(model, test, device):.4f}"
            print(msg, flush=True)
        if a.ckpt_every and step % a.ckpt_every == 0:
            torch.save(model.state_dict(), os.path.splitext(a.out)[0] + ".partial.pt")
        if step >= a.steps:
            break

    acc = char_accuracy(model, test, device) if test else float("nan")
    print(f"final test character accuracy {acc:.4f}" + (f" (current {baseline:.4f})" if baseline is not None else ""))
    if baseline is not None and acc < baseline and not a.force:
        cand = os.path.splitext(a.out)[0] + ".candidate.onnx"
        print("gate: worse than the current model, saved as candidate only")
        export(model, cand)
        return
    export(model, a.out)


if __name__ == "__main__":
    main()
