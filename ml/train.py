"""Train CodeNet on synthetic scenes plus the fully-labelled real images, export ONNX.

usage: python train.py --data <folder>=<labels.json> [--data ...] --out models/codenet.onnx [--steps 5000]
                      [--init models/codenet.pt] [--lr 3e-3]
"""

import argparse
import json
import os
import random
import time

import cv2
import numpy as np
import torch
import torch.nn.functional as F
from torch.utils.data import DataLoader, IterableDataset

import synth
from model import CodeNet, stretch

SCALE = 8      # network input = full resolution / 8
CROP = 256     # training crop at network input scale


def load_real(folder, labels_path):
    """Fully-labelled real images at 1/8 scale with code masks."""
    with open(labels_path, encoding="utf-8") as f:
        labels = json.load(f)
    return load_labelled(folder, labels)


IGNORE = 255


def certain_background(small):
    """Pixels that cannot be a code: almost no local texture (codes always have texture)."""
    f = cv2.GaussianBlur(stretch(small), (0, 0), 1.0)
    m = cv2.blur(f, (7, 7))
    sd = np.sqrt(np.maximum(cv2.blur(f * f, (7, 7)) - m * m, 0))
    return sd < 0.035


def load_labelled(folder, labels, partial=False, exclude=()):
    """Real images at 1/8 scale with masks: 1 = code, 0 = background, 255 = unknown.

    Complete images: every pixel outside the codes is background. With partial=True, images
    the engine read only in part are used too: read codes are 1, texture-free areas are 0,
    and the rest (where an unread code may sit) is ignored by the loss.
    """
    out = []
    for name, lab in labels.items():
        if name in exclude or not (lab["complete"] or partial) or not lab["codes"]:
            continue
        gray = cv2.imread(os.path.join(folder, name), cv2.IMREAD_GRAYSCALE)
        small = cv2.resize(gray, None, fx=1 / SCALE, fy=1 / SCALE, interpolation=cv2.INTER_AREA)
        if lab["complete"]:
            mask = np.zeros(small.shape, np.uint8)
        else:
            mask = np.where(certain_background(small), 0, IGNORE).astype(np.uint8)
        for c in lab["codes"]:
            q = (np.array(c["quad"], np.float32) / SCALE).round().astype(np.int32)
            cv2.fillConvexPoly(mask, q, 1)
        out.append((name, small, mask))
    return out


class Scenes(IterableDataset):
    def __init__(self, real, real_fraction=0.3, seed=0):
        self.real, self.real_fraction, self.seed = real, real_fraction, seed

    def __iter__(self):
        info = torch.utils.data.get_worker_info()
        rng = random.Random(self.seed + (info.id if info else 0) * 7919 + int(time.time()))
        while True:
            if self.real and rng.random() < self.real_fraction:
                _, img, mask = rng.choice(self.real)
                y = rng.randint(0, img.shape[0] - CROP)
                x = rng.randint(0, img.shape[1] - CROP)
                img, mask = img[y:y + CROP, x:x + CROP], mask[y:y + CROP, x:x + CROP]
                img = synth.darken_real(img, rng)
                k = rng.randint(0, 3)
                img, mask = np.rot90(img, k).copy(), np.rot90(mask, k).copy()
                if rng.random() < 0.5:
                    img, mask = img[:, ::-1].copy(), mask[:, ::-1].copy()
            else:
                img, mask = synth.scene(rng, CROP, CROP)
            x = torch.from_numpy(stretch(img))[None]
            # target at output resolution (1/2 of input): a cell is code when mostly covered,
            # unknown (-1, ignored by the loss) when mostly unknown
            pos = cv2.resize((mask == 1).astype(np.float32), (CROP // 2, CROP // 2), interpolation=cv2.INTER_AREA)
            ign = cv2.resize((mask == IGNORE).astype(np.float32), (CROP // 2, CROP // 2), interpolation=cv2.INTER_AREA)
            t = np.where(pos > 0.5, 1.0, np.where(ign > 0.5, -1.0, 0.0)).astype(np.float32)
            yield x, torch.from_numpy(t)[None]


def loss_fn(logits, target):
    valid = (target >= 0).float()
    t = target.clamp(min=0)
    bce = F.binary_cross_entropy_with_logits(logits, t, pos_weight=torch.tensor(3.0, device=logits.device), reduction="none")
    bce = (bce * valid).sum() / valid.sum().clamp(min=1)
    p = torch.sigmoid(logits) * valid
    inter = (p * t).sum((1, 2, 3))
    dice = 1 - (2 * inter + 1) / (p.sum((1, 2, 3)) + t.sum((1, 2, 3)) + 1)
    return bce + 0.5 * dice.mean()


def train(real, out, steps=5000, batch=32, workers=6, init=None, lr=3e-3):
    """Train (or fine-tune from init .pt) on synthetic scenes + real images; writes out (.onnx) and .pt."""
    dev = torch.device("mps" if torch.backends.mps.is_available() else "cuda" if torch.cuda.is_available() else "cpu")
    print(f"device {dev}, {len(real)} real images: {', '.join(n for n, _, _ in real)}")

    model = CodeNet()
    if init:
        model.load_state_dict(torch.load(init, map_location="cpu"))
    model = model.to(dev)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=lr, total_steps=steps, pct_start=0.1)
    loader = DataLoader(Scenes(real), batch_size=batch, num_workers=workers, persistent_workers=True)

    t0, run = time.time(), 0.0
    for step, (x, t) in enumerate(loader, 1):
        x, t = x.to(dev), t.to(dev)
        loss = loss_fn(model(x), t)
        opt.zero_grad()
        loss.backward()
        opt.step()
        sched.step()
        run = 0.98 * run + 0.02 * loss.item() if step > 1 else loss.item()
        if step % 200 == 0:
            print(f"step {step:5d}  loss {run:.4f}  {time.time() - t0:5.0f}s", flush=True)
        if step >= steps:
            break

    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    model.eval().cpu()
    torch.save(model.state_dict(), os.path.splitext(out)[0] + ".pt")
    torch.onnx.export(model, torch.zeros(1, 1, 456, 684), out, input_names=["image"], output_names=["logits"],
                      dynamic_axes={"image": {2: "h", 3: "w"}, "logits": {2: "h2", 3: "w2"}}, opset_version=17,
                      dynamo=False)
    print("saved", out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", action="append", required=True, help="<image folder>=<labels.json>")
    ap.add_argument("--partial", action="store_true", help="also train on partly-read images (unknown areas ignored)")
    ap.add_argument("--holdout", default="", help="comma-separated image names kept out of training (test set)")
    ap.add_argument("--out", default="models/codenet.onnx")
    ap.add_argument("--steps", type=int, default=5000)
    ap.add_argument("--batch", type=int, default=32)
    ap.add_argument("--workers", type=int, default=6)
    ap.add_argument("--init")
    ap.add_argument("--lr", type=float, default=3e-3)
    args = ap.parse_args()
    real = []
    exclude = {n.strip() for n in args.holdout.split(",") if n.strip()}
    for d in args.data:
        folder, labels_path = d.rsplit("=", 1)
        with open(labels_path, encoding="utf-8") as f:
            real += load_labelled(folder, json.load(f), partial=args.partial, exclude=exclude)
    train(real, args.out, args.steps, args.batch, args.workers, args.init, args.lr)


if __name__ == "__main__":
    main()
