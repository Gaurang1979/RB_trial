"""Retraining agent (slow loop): learn from the cases the line missed, without manual labels.

1. Label the hard cases the line saved (BMP + .json sidecar) offline: the engine re-reads them
   with the current model, a long time budget and every fallback. Cases it now reads in full
   become training images.
2. Fine-tune the current model on them plus the golden images.
3. Compare old and new model on the golden set and on the hard cases. A candidate passes only
   if it finds at least as many labelled codes on both (no regressions).
4. Save the candidate and a report. Production use needs approval:
   python retrain.py --promote <candidate.onnx> --approved-by <name>
   copies it to models/codenet.onnx and appends to models/history.jsonl (audit trail).

usage: python retrain.py --golden <folder>=<labels.json> --hard <hard-case folder>
                         [--current models/codenet.onnx] [--steps 1500]
"""

import argparse
import datetime as dt
import json
import os
import shutil
import subprocess
import sys
import tempfile

import cv2
import numpy as np
import onnxruntime as ort

from evaluate import regions

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(HERE, "models")


def recall(model_path, sets, threshold=0.4):
    """Labelled codes inside an AI region, over (folder, labels) sets."""
    sess = ort.InferenceSession(model_path)
    hit = total = 0
    for folder, labels in sets:
        for name, lab in labels.items():
            gray = cv2.imread(os.path.join(folder, name), cv2.IMREAD_GRAYSCALE)
            regs, _ = regions(sess, gray, threshold)
            for c in lab["codes"]:
                cx, cy = np.array(c["quad"]).mean(0)
                hit += any(x <= cx <= x + w and y <= cy <= y + h for x, y, w, h in regs)
                total += 1
    return hit, total


def retrain(golden, hard_folder, current, steps):
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M")
    cand_dir = os.path.join(MODELS, "candidates")
    os.makedirs(cand_dir, exist_ok=True)

    print("labelling hard cases offline ...")
    # the engine (OpenCV) and PyTorch each bring an OpenMP runtime that must not share a
    # process, so labelling runs in its own process and training is imported afterwards
    with tempfile.TemporaryDirectory() as tmp:
        out = os.path.join(tmp, "hard_labels.json")
        subprocess.run([sys.executable, os.path.join(HERE, "autolabel.py"), hard_folder, out, current],
                       check=True, stdout=subprocess.DEVNULL)
        with open(out, encoding="utf-8") as f:
            hard_labels = json.load(f)
    learnable = {k: v for k, v in hard_labels.items() if v["complete"]}
    print(f"{len(hard_labels)} hard cases, {len(learnable)} now fully read offline (learnable)")
    report = {"time": stamp, "current": current, "hard_cases": len(hard_labels), "learnable": len(learnable)}
    if not learnable:
        report["decision"] = "nothing to learn"
        return report

    import train  # imports PyTorch: only after the engine has run in its own process

    real = []
    for folder, labels in golden:
        real += train.load_labelled(folder, labels)
    real += train.load_labelled(hard_folder, learnable)

    cand = os.path.join(cand_dir, f"codenet-{stamp}.onnx")
    train.train(real, cand, steps=steps, init=os.path.splitext(current)[0] + ".pt", lr=1e-3)

    sets_golden = golden
    sets_hard = [(hard_folder, hard_labels)]
    old_g, n_g = recall(current, sets_golden)
    new_g, _ = recall(cand, sets_golden)
    old_h, n_h = recall(current, sets_hard)
    new_h, _ = recall(cand, sets_hard)
    report.update({
        "candidate": cand,
        "golden": {"labelled": n_g, "old": old_g, "new": new_g},
        "hard": {"labelled": n_h, "old": old_h, "new": new_h},
    })
    passed = new_g >= old_g and new_h >= old_h and (new_g + new_h) > (old_g + old_h)
    report["decision"] = "candidate passed: awaiting approval" if passed else "rejected: no gain or a regression"
    return report


def promote(candidate, approved_by):
    dst = os.path.join(MODELS, "codenet.onnx")
    if os.path.exists(dst):
        shutil.copy2(dst, os.path.join(MODELS, "candidates", f"previous-{dt.datetime.now():%Y%m%d-%H%M%S}.onnx"))
    shutil.copy2(candidate, dst)
    pt = os.path.splitext(candidate)[0] + ".pt"
    if os.path.exists(pt):
        shutil.copy2(pt, os.path.join(MODELS, "codenet.pt"))
    with open(os.path.join(MODELS, "history.jsonl"), "a", encoding="utf-8") as f:
        f.write(json.dumps({"time": dt.datetime.now().isoformat(timespec="seconds"), "promoted": candidate,
                            "approved_by": approved_by}) + "\n")
    print(f"promoted {candidate} (approved by {approved_by})")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--golden", action="append", default=[], help="<image folder>=<labels.json>")
    ap.add_argument("--hard")
    ap.add_argument("--current", default=os.path.join(MODELS, "codenet.onnx"))
    ap.add_argument("--steps", type=int, default=1500)
    ap.add_argument("--promote")
    ap.add_argument("--approved-by")
    a = ap.parse_args()

    if a.promote:
        if not a.approved_by:
            ap.error("--approved-by is required to promote a model")
        promote(a.promote, a.approved_by)
        return
    if not a.hard or not a.golden:
        ap.error("--golden and --hard are required")
    golden = []
    for g in a.golden:
        folder, labels = g.rsplit("=", 1)
        with open(labels, encoding="utf-8") as f:
            golden.append((folder, json.load(f)))
    report = retrain(golden, a.hard, a.current, a.steps)
    path = os.path.join(MODELS, "candidates", f"report-{report['time']}.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
