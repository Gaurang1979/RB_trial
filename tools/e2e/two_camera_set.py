"""Pictures for the two-camera end-to-end test (Vision Studio + vs-runtime + a simulated PLC).

    ml/.venv/bin/python -I tools/e2e/two_camera_set.py <out dir> --cam1 "<VS_IMAGES>/carton image.bmp" [...] \
        --parts 100 --seed 7

Camera 1 sees one carton with its small QR code: frames cut from real line pictures (customer data:
keep them and the frames out of the repository). Camera 2 sees a dark pack with a printed EAN-13
label and a 4-line laser batch code (B.NO, MFG, EXP, MRP), drawn here, light print on a dark pack.

Camera 1's part moves by shifting its window on the real picture; camera 2's pack moves, turns and
changes light as drawn. Parts are numbered 1..N in the order the PLC feeds them. Most are good; bad ones are mixed in, one
kind each: "wrong_batch" (B.NO printed with another lot), "missing_line" (EXP line not printed),
"misread_qr" (the QR smudged so it cannot be read), "missing_1d" (no barcode label). Written:

    <out>/cam1/part_NNN.bmp, <out>/cam1-teach/good_NN.bmp   8-bit grey BMP (the simulator reads BMP)
    <out>/cam2/part_NNN.bmp, <out>/cam2-teach/good_NN.bmp
    <out>/truth.csv   part, expected (OK/NG), kind, qr text
    <out>/batch.json  the batch values the operator enters (BATCH, MFG, EXP, MRP) and the GTIN

The batch print is drawn with ml/ocr's laser renderer (synth_scene._line_ink); the EAN-13 is drawn
from its tables. Each frame moves a little (position, turn, light), as on a running line.
"""

import argparse
import csv
import json
import math
import os
import random
import sys

import cv2
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "ml"))
import engine  # noqa: E402  (ml/engine.py: the C engine through ctypes)
from ocr import synth_scene  # noqa: E402

BAD_KINDS = ["wrong_batch", "missing_line", "misread_qr", "missing_1d"]

# EAN-13 (ISO/IEC 15420): L codes, G = L read backwards and inverted, R = L inverted.
L_CODES = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"]
PARITY = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"]


def ean13_check(d12):
    s = sum(int(c) * (3 if i % 2 else 1) for i, c in enumerate(d12))
    return str((10 - s % 10) % 10)


def ean13_modules(code):
    """95 modules, 1 = bar."""
    first, left, right = int(code[0]), code[1:7], code[7:]
    inv = {"0": "1", "1": "0"}
    out = "101"
    for c, p in zip(left, PARITY[first]):
        l = L_CODES[int(c)]
        out += l if p == "L" else "".join(inv[b] for b in l)[::-1]
    out += "01010"
    for c in right:
        out += "".join(inv[b] for b in L_CODES[int(c)])
    return out + "101"


def draw_label(code, x_px, margin=14):
    """A white label with the EAN-13 (dark bars, <margin> X of label on each side: more than the 11 X
    quiet zone, as printed labels have) and its digits under it."""
    mods = ean13_modules(code)
    bar_h = int(55 * x_px)
    w, h = int((95 + 2 * margin) * x_px), bar_h + int(14 * x_px)
    lab = np.full((h, w), 225, np.uint8)
    for i, m in enumerate(mods):
        if m == "1":
            x0 = int(round((margin + i) * x_px))
            x1 = int(round((margin + 1 + i) * x_px))
            lab[int(4 * x_px):int(4 * x_px) + bar_h, x0:x1] = 30
    cv2.putText(lab, f"{code[0]}  {code[1:7]}  {code[7:]}", (int((margin - 5) * x_px), h - int(2 * x_px)),
                cv2.FONT_HERSHEY_SIMPLEX, 0.09 * x_px, 30, max(1, int(x_px / 2.5)), cv2.LINE_AA)
    return lab.astype(np.float32)


def laser_block(lines, coder, ch):
    """Ink 0..1 of the batch code, lines left aligned, as one block (light laser marks). One coder prints
    every line of every part with the same font and settings: each line is drawn from the coder's seed."""
    inks = []
    for t in lines:
        while True:
            li = synth_scene._line_ink(t, "laser", random.Random(coder), ch)
            if li is not None:
                inks.append(li[0])
                break
    gap = int(ch * 0.7)
    W = max(i.shape[1] for i in inks) + 4
    H = sum(i.shape[0] for i in inks) + gap * (len(inks) - 1) + 4
    block = np.zeros((H, W), np.float32)
    y = 2
    for i in inks:
        block[y:y + i.shape[0], 2:2 + i.shape[1]] = np.maximum(block[y:y + i.shape[0], 2:2 + i.shape[1]], i)
        y += i.shape[0] + gap
    return block


def paste(img, patch, x, y, mode):
    h, w = patch.shape
    h = min(h, img.shape[0] - y)
    w = min(w, img.shape[1] - x)
    if mode == "replace":
        img[y:y + h, x:x + w] = patch[:h, :w]
    else:  # light ink: brighten the pack where the laser marked it
        img[y:y + h, x:x + w] += patch[:h, :w] * mode


def cam2_frame(rng, coder, batch, gtin, kind, W=1280, H=960):
    """A dark pack, the barcode label on the left, the batch code on the right."""
    nr = np.random.default_rng(rng.randint(0, 1 << 30))
    bg = rng.uniform(38, 55)
    img = np.full((H, W), bg, np.float32)
    # a light falling off across the pack, as from a ring light a little off centre
    gx = np.linspace(-1, 1, W)[None, :]
    gy = np.linspace(-1, 1, H)[:, None]
    img += rng.uniform(4, 10) * (1 - (gx * gx + gy * gy) / 2)
    dx, dy = rng.randint(-25, 25), rng.randint(-20, 20)
    if kind != "missing_1d":
        label = draw_label(gtin, 3.2)
        paste(img, label, 90 + dx, 300 + dy, "replace")
    lines = [f"B.NO {batch['BATCH']}", f"MFG {batch['MFG']}", f"EXP {batch['EXP']}", f"MRP RS.{batch['MRP']}"]
    if kind == "wrong_batch":
        lines[0] = f"B.NO {batch['WRONG']}"
    if kind == "missing_line":
        lines.pop(2)
    block = laser_block(lines, coder, 34)
    paste(img, block, 640 + dx, 330 + dy, rng.uniform(95, 130))
    ang = rng.uniform(-1.5, 1.5)
    M = cv2.getRotationMatrix2D((W / 2, H / 2), ang, 1.0)
    img = cv2.warpAffine(img, M, (W, H), borderMode=cv2.BORDER_REPLICATE)
    img = cv2.GaussianBlur(img, (0, 0), rng.uniform(0.5, 0.9))
    img += nr.normal(0, rng.uniform(1.5, 3), img.shape)
    return np.clip(img, 0, 255).astype(np.uint8)


def carton_crops(paths, size=(640, 520), budget_ms=100, move=12):
    """Frames of one carton each around a real QR code: (frame, text, quad in the frame). Only cartons whose
    code reads on its own within the line's time budget are kept: a code
    the engine read in the whole case only with its neighbours' help (a fold, a crease) is no good part."""
    out = []
    for p in paths:
        img = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
        if img is None:
            print("cannot read", p, file=sys.stderr)
            continue
        codes, _ = engine.decode(img, engine.QR, 0, 8000, model=os.path.join(ROOT, "ml", "models", "codenet.onnx"))
        for text, quad, _, _ in codes:
            cx, cy = quad[:, 0].mean(), quad[:, 1].mean()
            x0 = int(np.clip(cx - size[0] / 2, move, img.shape[1] - size[0] - move))
            y0 = int(np.clip(cy - size[1] / 2, move, img.shape[0] - size[1] - move))
            crop = img[y0:y0 + size[1], x0:x0 + size[0]].copy()
            q = quad - np.array([x0, y0], np.float32)
            # one code per frame: a neighbour's code cut at the edge would make two
            others = [c for c in codes if c[0] != text and
                      (c[1][:, 0].max() > x0 and c[1][:, 0].min() < x0 + size[0] and c[1][:, 1].max() > y0 and c[1][:, 1].min() < y0 + size[1])]
            if others:
                continue
            model = os.path.join(ROOT, "ml", "models", "codenet.onnx")
            if [c[0] for c in engine.decode(crop, engine.QR, 1, budget_ms, model=model)[0]] != [text]:
                continue
            out.append(((img, x0, y0, size), text, q))
        print(f"{os.path.basename(p)}: {len(codes)} codes read, {len(out)} frames so far", file=sys.stderr)
    return out


def smudge(frame, quad, rng):
    """Most of the QR under a smear of ink: it can no longer be read."""
    f = frame.copy()
    x0, y0 = quad.min(0)
    x1, y1 = quad.max(0)
    w, h = x1 - x0, y1 - y0
    cx, cy = x0 + w * rng.uniform(0.35, 0.65), y0 + h * rng.uniform(0.35, 0.65)
    cv2.ellipse(f, (int(cx), int(cy)), (int(w * 0.42), int(h * 0.36)), rng.uniform(0, 180), 0, 360,
                int(np.median(f)), -1, cv2.LINE_AA)
    return f


def jitter(source, rng, move=12):
    """The carton as the next part on the line: the picture's window moved a few pixels and the light a
    little brighter or darker. The real picture is not turned or resampled: that would blur a code of
    4 pixels per module more than any camera does. Returns the frame and its shift."""
    img, x0, y0, (w, h) = source
    dx, dy = rng.randint(-move, move), rng.randint(-move, move)
    f = img[y0 + dy:y0 + dy + h, x0 + dx:x0 + dx + w].astype(np.float32)
    f = f * rng.uniform(0.92, 1.08) + rng.uniform(-4, 4)
    return np.clip(f, 0, 255).astype(np.uint8), (dx, dy)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--cam1", action="append", required=True, help="a line picture with small QR codes (customer data)")
    ap.add_argument("--parts", type=int, default=100)
    ap.add_argument("--bad", type=int, default=3, help="parts of each bad kind")
    ap.add_argument("--teach", type=int, default=6, help="good pictures per camera for teaching")
    ap.add_argument("--seed", type=int, default=7)
    a = ap.parse_args()
    rng = random.Random(a.seed)
    coder = rng.randint(0, 1 << 30)   # the laser coder's font and settings, the same for the batch
    batch = {"BATCH": "A2610307", "MFG": "10/2026", "EXP": "09/2028", "MRP": "145.00", "WRONG": "A2610308"}
    gtin = "890123456789"
    gtin += ean13_check(gtin)

    crops = carton_crops(a.cam1)
    if len(crops) < 4:
        sys.exit("too few QR codes read in the camera 1 pictures")
    for d in ("cam1", "cam2", "cam1-teach", "cam2-teach"):
        os.makedirs(os.path.join(a.out, d), exist_ok=True)

    kinds = ["good"] * a.parts
    slots = rng.sample(range(5, a.parts), a.bad * len(BAD_KINDS))
    for i, s in enumerate(slots):
        kinds[s] = BAD_KINDS[i % len(BAD_KINDS)]
    with open(os.path.join(a.out, "truth.csv"), "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["part", "expected", "kind", "qr"])
        for n, kind in enumerate(kinds, 1):
            source, text, quad = crops[(n - 1) % len(crops)]
            f1, (dx, dy) = jitter(source, rng)
            if kind == "misread_qr":
                f1 = smudge(f1, quad - np.array([dx, dy], np.float32), rng)
            cv2.imwrite(os.path.join(a.out, "cam1", f"part_{n:03d}.bmp"), f1)
            cv2.imwrite(os.path.join(a.out, "cam2", f"part_{n:03d}.bmp"),
                        cam2_frame(rng, coder, batch, gtin, kind if kind != "misread_qr" else "good"))
            w.writerow([n, "OK" if kind == "good" else "NG", kind, text])
    for i in range(a.teach):
        source, _, _ = crops[(i * 7) % len(crops)]
        cv2.imwrite(os.path.join(a.out, "cam1-teach", f"good_{i + 1:02d}.bmp"), jitter(source, rng)[0])
        cv2.imwrite(os.path.join(a.out, "cam2-teach", f"good_{i + 1:02d}.bmp"), cam2_frame(rng, coder, batch, gtin, "good"))
    with open(os.path.join(a.out, "batch.json"), "w") as fh:
        json.dump({**{k: v for k, v in batch.items() if k != "WRONG"}, "GTIN": gtin}, fh, indent=2)
    print(f"{a.parts} parts ({sum(k != 'good' for k in kinds)} bad) in {a.out}; GTIN {gtin}")


if __name__ == "__main__":
    main()
