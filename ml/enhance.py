"""Code enhancer: a small network that turns a dark, noisy, blurred or glare-hit code crop into
a clean black-and-white image the decoder can read.

Trained on synthetic pairs (the same code degraded and clean) at full resolution, with real
QR / Data Matrix symbols from zxing-cpp. The engine runs it only on regions that every other
fix failed on.

usage:
  python enhance.py train --out models/enhancer.onnx [--steps 6000]
  python enhance.py eval models/enhancer.onnx          # decode rate before / after on held-out synthetic crops
"""

import argparse
import os
import random
import time

import cv2
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
import zxingcpp
from torch.utils.data import DataLoader, IterableDataset

from model import sep, conv_bn, stretch
from synth import _code_matrix

CROP = 192


class Enhancer(nn.Module):
    """U-Net-like, full resolution in and out, about 60k parameters."""

    def __init__(self, w=16):
        super().__init__()
        self.e1 = nn.Sequential(conv_bn(1, w), sep(w, w))                 # /1
        self.e2 = nn.Sequential(sep(w, 2 * w, 2), sep(2 * w, 2 * w))      # /2
        self.e3 = nn.Sequential(sep(2 * w, 4 * w, 2), sep(4 * w, 4 * w),  # /4, context
                                sep(4 * w, 4 * w, dilation=2), sep(4 * w, 4 * w, dilation=4))
        self.l3 = nn.Conv2d(4 * w, 2 * w, 1)
        self.d2 = sep(2 * w, 2 * w)
        self.l2 = nn.Conv2d(2 * w, w, 1)
        self.d1 = sep(w, w)
        self.head = nn.Conv2d(w, 1, 1)

    def forward(self, x):
        f1 = self.e1(x)
        f2 = self.e2(f1)
        f3 = self.e3(f2)
        u2 = self.d2(F.interpolate(self.l3(f3), size=f2.shape[-2:], mode="bilinear", align_corners=False) + f2)
        u1 = self.d1(F.interpolate(self.l2(u2), size=f1.shape[-2:], mode="bilinear", align_corners=False) + f1)
        return self.head(u1)  # logits: >0 = paper (white), <0 = ink (black)


def pair(rng: random.Random, size=CROP):
    """(degraded uint8, clean float 0..1, payload text, format) for one code centred in a crop."""
    fmt = rng.choice(["qr", "dm"])
    mat = _code_matrix(rng, fmt)                      # 0/255 incl. quiet zone
    n = mat.shape[0]
    module = rng.uniform(2.2, 7.0)
    side = n * module
    if side > size * 0.95:
        module = size * 0.95 / n
        side = n * module
    SS = 4
    big = cv2.resize(mat, (int(side * SS), int(side * SS)), interpolation=cv2.INTER_NEAREST).astype(np.float32) / 255

    # pose (rotation + mild perspective) about the crop centre, drawn supersampled
    c = size * SS / 2
    src = np.float32([[0, 0], [big.shape[1], 0], [big.shape[1], big.shape[0]], [0, big.shape[0]]])
    ang = np.deg2rad(rng.uniform(0, 360))
    half = big.shape[0] / 2
    corners = np.float32([[-half, -half], [half, -half], [half, half], [-half, half]])
    R = np.float32([[np.cos(ang), -np.sin(ang)], [np.sin(ang), np.cos(ang)]])
    dst = corners @ R.T + c
    dst += np.float32([[rng.normalvariate(0, 1), rng.normalvariate(0, 1)] for _ in range(4)]) * half * rng.uniform(0, 0.06)
    P = cv2.getPerspectiveTransform(src, dst.astype(np.float32))
    # paper everywhere around the code (the label), clean target is ideal ink/paper
    clean_big = cv2.warpPerspective(big, P, (size * SS, size * SS), flags=cv2.INTER_NEAREST, borderValue=1.0)
    clean = cv2.resize(clean_big, (size, size), interpolation=cv2.INTER_AREA)

    # degraded: print contrast, defocus, glare, haze, exposure, noise
    paper, ink = rng.uniform(0.5, 1.0), rng.uniform(0.0, 0.45)
    ink = min(ink, paper - 0.12)
    img = ink + (paper - ink) * clean
    if rng.random() < 0.5:  # uneven light across the label
        gx, gy = np.meshgrid(np.linspace(-1, 1, size), np.linspace(-1, 1, size))
        img = img * (1 + rng.uniform(-0.4, 0.4) * gx + rng.uniform(-0.4, 0.4) * gy)
    sigma = rng.uniform(0.3, 0.45 * module) if rng.random() < 0.7 else rng.uniform(0.0, 0.3)
    if sigma > 0.05:
        img = cv2.GaussianBlur(img.astype(np.float32), (0, 0), sigma)
    if rng.random() < 0.3:  # motion blur
        k = rng.randint(2, max(3, int(module)))
        kernel = np.zeros((k, k), np.float32)
        kernel[k // 2, :] = 1.0 / k
        M = cv2.getRotationMatrix2D((k / 2 - 0.5, k / 2 - 0.5), rng.uniform(0, 180), 1)
        img = cv2.filter2D(img, -1, cv2.warpAffine(kernel, M, (k, k)))
    if rng.random() < 0.35:  # glare / tape reflection
        g = np.zeros((size, size), np.float32)
        cv2.ellipse(g, (rng.randint(0, size), rng.randint(0, size)), (rng.randint(5, size // 2), rng.randint(3, size // 5)),
                    rng.uniform(0, 180), 0, 360, 1.0, -1)
        img = img + cv2.GaussianBlur(g, (0, 0), rng.uniform(2, 10)) * rng.uniform(0.2, 1.5)
    if rng.random() < 0.3:  # film haze
        h = rng.uniform(0.1, 0.4)
        img = img * (1 - h) + h * rng.uniform(0.3, 0.9)
    k = float(np.exp(rng.uniform(np.log(0.015), np.log(1.0))))
    x = img * k * 255 + np.random.default_rng(rng.randint(0, 1 << 30)).normal(0, rng.uniform(0.3, 2.5), img.shape)
    degraded = np.clip(np.round(x), 0, 255).astype(np.uint8)
    return degraded, (clean > 0.5).astype(np.float32), fmt


class Pairs(IterableDataset):
    """70% quality-violation samples (violations.py), 30% the original degradations."""

    def __iter__(self):
        import violations
        info = torch.utils.data.get_worker_info()
        rng = random.Random((info.id if info else 0) * 104729 + int(time.time() * 1000))
        while True:
            if rng.random() < 0.7:
                deg, clean, _, _, _ = violations.sample(rng, CROP)
            else:
                deg, clean, _ = pair(rng)
            yield torch.from_numpy(stretch(deg))[None], torch.from_numpy(clean)[None]


def train(out, steps, batch=24, workers=6, lr=3e-3, init=None):
    dev = torch.device("mps" if torch.backends.mps.is_available() else "cpu")
    model = Enhancer()
    if init:
        model.load_state_dict(torch.load(init, map_location="cpu"))
    model = model.to(dev)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=lr, total_steps=steps, pct_start=0.1)
    loader = DataLoader(Pairs(), batch_size=batch, num_workers=workers, persistent_workers=True)
    t0, run = time.time(), 0.0
    for step, (x, t) in enumerate(loader, 1):
        x, t = x.to(dev), t.to(dev)
        logits = model(x)
        loss = F.binary_cross_entropy_with_logits(logits, t) + 0.5 * F.l1_loss(torch.sigmoid(logits), t)
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
    torch.onnx.export(model, torch.zeros(1, 1, CROP, CROP), out, input_names=["image"], output_names=["logits"],
                      dynamic_axes={"image": {2: "h", 3: "w"}, "logits": {2: "h", 3: "w"}}, opset_version=17, dynamo=False)
    print("saved", out)


def run_enhancer(sess, crop_u8):
    """Enhance an 8-bit crop with an onnxruntime session -> 8-bit black/white image."""
    h, w = crop_u8.shape
    H, W = (h + 3) // 4 * 4, (w + 3) // 4 * 4  # two stride-2 stages
    x = np.pad(stretch(crop_u8), ((0, H - h), (0, W - w)), mode="edge")[None, None]
    logits = sess.run(None, {"image": x})[0][0, 0, :h, :w]
    return np.where(logits > 0, 255, 0).astype(np.uint8)


def evaluate_violations(model_path, per=120, seed=777):
    """Decode rate per quality violation: decoder alone vs with the enhancer (held-out seed)."""
    import onnxruntime as ort
    import violations
    sess = ort.InferenceSession(model_path)
    fmts = {"qr": zxingcpp.BarcodeFormat.QRCode, "dm": zxingcpp.BarcodeFormat.DataMatrix}
    print(f"{'violation':10s} {'decoder':>8s} {'enhanced':>9s} {'either':>7s}")
    tot = [0, 0, 0, 0]
    for name in violations.ALL:
        rng = random.Random(seed + hash(name) % 1000)
        a = b = e = 0
        for _ in range(per):
            deg, _, _, _, fmt = violations.sample(rng, CROP, violations=[name])
            f = fmts[fmt]
            ra = bool(zxingcpp.read_barcodes((stretch(deg) * 255).astype(np.uint8), formats=f, try_rotate=True, try_downscale=True))
            rb = bool(zxingcpp.read_barcodes(run_enhancer(sess, deg), formats=f, try_rotate=True, try_downscale=True))
            a, b, e = a + ra, b + rb, e + (ra or rb)
        tot = [tot[0] + a, tot[1] + b, tot[2] + e, tot[3] + per]
        print(f"{name:10s} {100 * a / per:7.0f}% {100 * b / per:8.0f}% {100 * e / per:6.0f}%")
    print(f"{'all':10s} {100 * tot[0] / tot[3]:7.0f}% {100 * tot[1] / tot[3]:8.0f}% {100 * tot[2] / tot[3]:6.0f}%")


def evaluate(model_path, n=400, seed=12345):
    import onnxruntime as ort
    sess = ort.InferenceSession(model_path)
    rng = random.Random(seed)
    fmts = {"qr": zxingcpp.BarcodeFormat.QRCode, "dm": zxingcpp.BarcodeFormat.DataMatrix}
    base = enh = both = 0
    for _ in range(n):
        deg, _, fmt = pair(rng)
        f = fmts[fmt]
        a = bool(zxingcpp.read_barcodes(stretch(deg).__mul__(255).astype(np.uint8), formats=f, try_rotate=True, try_downscale=False))
        b = bool(zxingcpp.read_barcodes(run_enhancer(sess, deg), formats=f, try_rotate=True, try_downscale=False))
        base, enh, both = base + a, enh + b, both + (a or b)
    print(f"{n} held-out synthetic crops: decoder alone {base} ({100 * base / n:.1f}%), "
          f"enhanced {enh} ({100 * enh / n:.1f}%), either {both} ({100 * both / n:.1f}%)")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    t = sub.add_parser("train")
    t.add_argument("--out", default="models/enhancer.onnx")
    t.add_argument("--steps", type=int, default=6000)
    t.add_argument("--workers", type=int, default=6)
    t.add_argument("--init")
    t.add_argument("--lr", type=float, default=3e-3)
    e = sub.add_parser("eval")
    e.add_argument("model")
    v = sub.add_parser("eval-violations")
    v.add_argument("model")
    a = ap.parse_args()
    if a.cmd == "train":
        train(a.out, a.steps, workers=a.workers, lr=a.lr, init=a.init)
    elif a.cmd == "eval-violations":
        evaluate_violations(a.model)
    else:
        evaluate(a.model)
