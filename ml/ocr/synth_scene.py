"""Whole synthetic images of printed lines, per print type, for tools/bench/ocr_bench (line finder and
reader together, unlike the straightened lines of synth_text).

    python -m ocr.synth_scene <out dir> --kind cij --n 200 --seed 31
    ../engine/build/ocr_bench <out dir>/cij <out dir>/cij/truth.tsv --model models/ocrnet.onnx \
        --charset models/ocr_charset.txt --mutate

Each image is 1 to 3 lines of date / batch / price print (synth_text.gen_text), 18 to 36 px capital
height, turned up to +-12 degrees, sometimes bowed as on a bottle, on a plain or textured surface with
uneven light, blur and noise. truth.tsv lists each image's lines top to bottom (ocr_bench's format).
"""

import argparse
import os
import random

import cv2
import numpy as np

from . import synth_text as st


def _line_ink(text, kind, rng, ch):
    """Ink (and the relief shading for embossed print) of one line at final scale, tightly cropped."""
    font = rng.choice(st.FONTS)
    signal = None
    if kind == "cij":
        r = st.render_dots(text, rng, ch, rng.uniform(0.0, 0.6), font)
    elif kind == "emboss":
        r = st.render_emboss(text, rng, ch, rng.uniform(0.05, 0.4), font)
        if r is not None:
            signal, r = r[0], (r[2], r[1])
    else:
        tracking = rng.uniform(0.0, 0.7) if kind == "laser" else rng.uniform(-0.05, 0.25)
        bold = 0 if kind == "laser" else rng.choice([0, 1, 1, 2])
        thin = rng.choice([0, 0, 1]) if kind == "laser" else 0
        r = st.render_solid(text, rng, ch, tracking, bold * st.K // 2, thin, font)
    if r is None:
        return None
    ink = r[0]
    ys, xs = np.nonzero(ink > 0.2)
    if len(xs) == 0:
        return None
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    out = []
    for m in (ink, signal):
        if m is None:
            out.append(None)
            continue
        m = cv2.GaussianBlur(m[y0:y1, x0:x1], (0, 0), 0.45 * st.K)
        out.append(cv2.resize(m, ((x1 - x0) // st.K, (y1 - y0) // st.K), interpolation=cv2.INTER_AREA))
    return out


def scene(rng, kind):
    """-> (uint8 image, [line texts]) or None."""
    n = rng.choice([1, 2, 2, 3])
    ch = rng.uniform(18, 36)
    texts, inks = [], []
    for _ in range(n):
        t = "".join(c for c in st.gen_text(rng) if c in st.CHARSET)
        t = " ".join(t.split())[:28].strip()
        if not t:
            return None
        li = _line_ink(t, kind, rng, ch)
        if li is None:
            return None
        texts.append(t)
        inks.append(li)
    gap = ch * rng.uniform(0.6, 1.0)
    W = int(max(i[0].shape[1] for i in inks) + 6 * ch)
    H = int(sum(i[0].shape[0] for i in inks) + gap * (n - 1) + 6 * ch)
    ink = np.zeros((H, W), np.float32)
    sig = np.zeros((H, W), np.float32) if kind == "emboss" else None
    y = int(3 * ch)
    align = rng.choice(["left", "centre"])
    for a, s in inks:
        h, w = a.shape
        x = int(3 * ch) if align == "left" else (W - w) // 2
        ink[y:y + h, x:x + w] = np.maximum(ink[y:y + h, x:x + w], a)
        if sig is not None:
            sig[y:y + h, x:x + w] += s
        y += h + int(gap)
    # turn, and bow as on a curved surface (vertical shift varying along the line)
    ang = rng.uniform(-12, 12)
    M = cv2.getRotationMatrix2D((W / 2, H / 2), ang, 1.0)
    maps = [cv2.warpAffine(m, M, (W, H)) if m is not None else None for m in (ink, sig)]
    if rng.random() < 0.3:
        u = np.linspace(-1, 1, W, dtype=np.float32)
        dy = rng.uniform(0.15, 0.6) * ch * rng.choice([1, -1]) * (u ** 2 - 0.4)
        mx = np.ascontiguousarray(np.repeat(np.arange(W, dtype=np.float32)[None, :], H, 0))
        my = np.ascontiguousarray(np.arange(H, dtype=np.float32)[:, None] + dy[None, :], np.float32)
        maps = [cv2.remap(m, mx, my, cv2.INTER_LINEAR) if m is not None else None for m in maps]
    ink, sig = maps
    nr = np.random.default_rng(rng.randint(0, 1 << 30))
    bg = rng.uniform(40, 210)
    contrast = rng.uniform(15, 120) if rng.random() > 0.2 else rng.uniform(8, 15)
    light = np.linspace(rng.uniform(-1, 1), rng.uniform(-1, 1), W)[None, :] * rng.uniform(0, 0.5) * contrast
    if sig is not None:
        img = bg + light + 0.6 * contrast * sig
    else:
        sign = 1 if (kind == "laser" and rng.random() < 0.6) else -1
        img = bg + light + sign * contrast * ink
    if rng.random() < 0.3:
        tex = cv2.GaussianBlur(nr.normal(0, 1, img.shape).astype(np.float32), (0, 0), rng.uniform(1, 3))
        img = img + tex * contrast * rng.uniform(0.05, 0.2) / max(1e-6, tex.std())
    img = cv2.GaussianBlur(img.astype(np.float32), (0, 0), rng.uniform(0.4, 1.2))
    img = img + nr.normal(0, rng.uniform(1, 4), img.shape)
    return np.clip(img, 0, 255).astype(np.uint8), texts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--kind", action="append", default=[], help="print type (default: all)")
    ap.add_argument("--n", type=int, default=200)
    ap.add_argument("--seed", type=int, default=31)
    a = ap.parse_args()
    for kind in a.kind or st.KINDS:
        d = os.path.join(a.out, kind)
        os.makedirs(d, exist_ok=True)
        rng = random.Random(a.seed + 1000 * st.KINDS.index(kind))
        with open(os.path.join(d, "truth.tsv"), "w", encoding="utf-8") as f:
            i = 0
            while i < a.n:
                r = scene(rng, kind)
                if r is None:
                    continue
                name = f"{kind}_{i:04d}.png"
                cv2.imwrite(os.path.join(d, name), r[0])
                f.write(name + "\t" + "\t".join(r[1]) + "\n")
                i += 1
        print("made", d, a.n)


if __name__ == "__main__":
    main()
