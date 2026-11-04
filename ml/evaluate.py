"""Locator recall on real images: does the AI locator put a region on every labelled code?

For each image: labelled codes (from autolabel.py), how many have their centre inside an AI
region, and how many regions the AI proposed (fewer = less decode work). Images marked
"train" were used in training; the rest are unseen.

usage: python evaluate.py <image-folder> <labels.json> <codenet.onnx> [--threshold 0.4] [--overlay <folder>]
"""

import argparse
import json
import os

import cv2
import numpy as np
import onnxruntime as ort

from model import stretch

IN_SCALE, OUT_SCALE = 8, 16


def regions(session, gray, threshold):
    small = cv2.resize(gray, None, fx=1 / IN_SCALE, fy=1 / IN_SCALE, interpolation=cv2.INTER_AREA)
    logits = session.run(None, {"image": stretch(small)[None, None]})[0][0, 0]
    mask = (1 / (1 + np.exp(-logits)) > threshold).astype(np.uint8)
    n, _, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
    out = []
    for i in range(1, n):
        x, y, w, h, a = stats[i]
        if a < 2:
            continue
        pad = max(w, h) * OUT_SCALE // 4 + OUT_SCALE
        out.append((x * OUT_SCALE - pad, y * OUT_SCALE - pad, w * OUT_SCALE + 2 * pad, h * OUT_SCALE + 2 * pad))
    return out, logits


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("images")
    ap.add_argument("labels")
    ap.add_argument("model")
    ap.add_argument("--threshold", type=float, default=0.4)
    ap.add_argument("--overlay")
    ap.add_argument("--holdout", default="", help="comma-separated test images (never trained on)")
    a = ap.parse_args()

    labels = json.load(open(a.labels, encoding="utf-8"))
    sess = ort.InferenceSession(a.model)
    holdout = {n.strip() for n in a.holdout.split(",") if n.strip()}
    tot_hit = tot_lab = tot_reg = 0
    unseen_hit = unseen_lab = 0
    hold_hit = hold_lab = 0
    print(f"{'image':38s} {'set':6s} {'labelled':>8s} {'found':>6s} {'regions':>7s}")
    for name, lab in labels.items():
        gray = cv2.imread(os.path.join(a.images, name), cv2.IMREAD_GRAYSCALE)
        regs, logits = regions(sess, gray, a.threshold)
        hit = 0
        for c in lab["codes"]:
            cx, cy = np.array(c["quad"]).mean(0)
            if any(x <= cx <= x + w and y <= cy <= y + h for x, y, w, h in regs):
                hit += 1
        n = len(lab["codes"])
        tot_hit, tot_lab, tot_reg = tot_hit + hit, tot_lab + n, tot_reg + len(regs)
        if not lab["complete"]:
            unseen_hit, unseen_lab = unseen_hit + hit, unseen_lab + n
        if name in holdout:
            hold_hit, hold_lab = hold_hit + hit, hold_lab + n
        kind = "TEST" if name in holdout else ("full" if lab["complete"] else "part")
        print(f"{name[:38]:38s} {kind:6s} {n:8d} {hit:6d} {len(regs):7d}")
        if a.overlay:
            os.makedirs(a.overlay, exist_ok=True)
            vis = cv2.cvtColor(cv2.resize((stretch(gray) * 255).astype(np.uint8), None, fx=0.25, fy=0.25), cv2.COLOR_GRAY2BGR)
            for x, y, w, h in regs:
                cv2.rectangle(vis, (x // 4, y // 4), ((x + w) // 4, (y + h) // 4), (0, 255, 0), 2)
            for c in lab["codes"]:
                q = (np.array(c["quad"]) / 4).astype(np.int32)
                cv2.polylines(vis, [q], True, (0, 0, 255), 1)
            cv2.imwrite(os.path.join(a.overlay, os.path.splitext(name)[0] + ".jpg"), vis)
    print(f"\nall: {tot_hit}/{tot_lab} labelled codes inside an AI region ({100 * tot_hit / max(1, tot_lab):.1f}%), "
          f"{tot_reg} regions")
    print(f"partly-read images: {unseen_hit}/{unseen_lab} ({100 * unseen_hit / max(1, unseen_lab):.1f}%)")
    if holdout:
        print(f"held-out test images: {hold_hit}/{hold_lab} ({100 * hold_hit / max(1, hold_lab):.1f}%)")


if __name__ == "__main__":
    main()
