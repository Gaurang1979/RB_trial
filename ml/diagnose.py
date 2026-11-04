"""Where and why codes are missed: decode every sample image, save a crop of every region that
looked like a code but did not read, and a contact sheet per image.

usage: python diagnose.py <image-folder> <out-folder> [--budget 200]
"""

import argparse
import os

import cv2
import numpy as np

import engine

HERE = os.path.dirname(os.path.abspath(__file__))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("images")
    ap.add_argument("out")
    ap.add_argument("--budget", type=int, default=200)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    truth = engine.load_truth()
    model = os.path.join(HERE, "models", "codenet.onnx")
    enh = os.path.join(HERE, "models", "enhancer.onnx")
    for name in sorted(os.listdir(a.images)):
        if not name.lower().endswith(".bmp"):
            continue
        g = cv2.imread(os.path.join(a.images, name), cv2.IMREAD_GRAYSCALE)
        fmt, exp = truth.get(name, (engine.QR, 0))
        # expected count unknown -> ask for "many" so nothing stops early and misses are reported
        codes, st = engine.decode(g, fmt, expected=exp or 999, budget_ms=a.budget, model=model, enhancer=enh)
        misses = st.miss_rects
        print(f"{name[:36]:36s} exp {exp:3d} read {len(codes):3d} unread-regions {len(misses):3d} {st.total_ms:6.0f} ms", flush=True)
        tiles = []
        for i, (x, y, w, h) in enumerate(misses[:60]):
            crop = g[y:y + h, x:x + w]
            lo, hi = np.percentile(crop, (1, 99.5))
            vis = np.clip((crop.astype(np.float32) - lo) * 255 / max(4, hi - lo), 0, 255).astype(np.uint8)
            vis = cv2.resize(vis, (160, 160), interpolation=cv2.INTER_AREA)
            why = st.miss_info[i]["reason"] if i < len(st.miss_info) else ""
            cv2.putText(vis, f"{i} {why}", (3, 14), cv2.FONT_HERSHEY_SIMPLEX, 0.45, 255, 1)
            tiles.append(vis)
            cv2.imwrite(os.path.join(a.out, f"{os.path.splitext(name)[0]}_miss{i:02d}_{x}_{y}.png"), crop)
        if tiles:
            while len(tiles) % 8:
                tiles.append(np.zeros((160, 160), np.uint8))
            rows = [np.hstack(tiles[r:r + 8]) for r in range(0, len(tiles), 8)]
            cv2.imwrite(os.path.join(a.out, f"_sheet_{os.path.splitext(name)[0]}.png"), np.vstack(rows))


if __name__ == "__main__":
    main()
