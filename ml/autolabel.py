"""Label real images automatically with the engine.

Every code the engine reads gives its exact corners. An image is "complete" when the engine
read the expected count: then every code in it is labelled and it can be used for training.
Incomplete images are labelled too (for evaluation), but must not be trained on, because the
codes the engine missed would be taught as background.

Expected count and code type come from truth/samples.csv, or from the .json sidecar that the
line writes next to each hard-case image.

usage: python autolabel.py <image-folder> <labels.json> [codenet.onnx]
"""

import json
import os
import sys

import cv2

import engine


def label_folder(folder, model=None, verbose=True):
    truth = engine.load_truth()
    labels = {}
    for name in sorted(os.listdir(folder)):
        if not name.lower().endswith((".bmp", ".png", ".tif", ".tiff", ".jpg")):
            continue
        gray = cv2.imread(os.path.join(folder, name), cv2.IMREAD_GRAYSCALE)
        fmt, expected = truth.get(name, (engine.QR, 0))
        sidecar = os.path.join(folder, os.path.splitext(name)[0] + ".json")
        if os.path.exists(sidecar):
            with open(sidecar, encoding="utf-8") as f:
                meta = json.load(f)
            fmt, expected = engine.FORMATS.get(meta.get("format"), engine.QR), int(meta.get("expected", 0))
        # a long budget: labelling is offline, so let every fallback run
        codes, stats = engine.decode(gray, fmt, expected=0, budget_ms=5000, model=model)
        # the same text printed twice (e.g. both ends of a can label) keeps one position only,
        # so its other copy would be unlabelled: such images are not complete
        repeated = any(c[3] > 1 for c in codes)
        complete = expected > 0 and len(codes) >= expected and not repeated
        labels[name] = {
            "width": gray.shape[1], "height": gray.shape[0], "format": fmt, "expected": expected,
            "complete": complete,
            "repeated": repeated,
            "codes": [{"text": t, "quad": q.round(1).tolist()} for t, q, _, _ in codes],
        }
        if verbose:
            print(f"{name[:40]:40s} read {len(codes):3d} / {expected:3d} {'complete' if complete else ''}", flush=True)
    return labels


def main(folder, out_path, model=None):
    labels = label_folder(folder, model)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(labels, f)
    n = sum(v["complete"] for v in labels.values())
    print(f"\n{len(labels)} images, {n} complete (usable for training)")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
