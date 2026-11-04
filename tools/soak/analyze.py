"""Soak verdicts against the reference verdict of each frame: no OK that should be NG.

analyze.py <results.db> <cam1 folder> <cam1 reference> <cam2 folder> <cam2 reference>

A reference file is `soakctl local` output (run.sh writes ref-cam1.txt and ref-cam2.txt):
"<file>,<True|False>,..." per frame, from a fresh reader. The runtime's own inspections
(results.db, per camera in id order) are matched with the frames: the simulator plays a camera's
folder in name order from Start, one frame per inspection. The PLC's part log is not used for
this: a part that times out or arrives while a channel is busy shifts its count against the
frames, and on a busy PC that showed OK-where-NG parts that were not (2026-10-31, run 2).
"""
import os, sqlite3, sys
from collections import Counter

db = sqlite3.connect(sys.argv[1])
bad = 0
for c in range(2):
    folder, ref_path = sys.argv[2 + 2 * c], sys.argv[3 + 2 * c]
    ref = {}
    for line in open(ref_path):
        p = line.strip().split(",")
        if len(p) > 2 and p[1] in ("True", "False"):
            ref[p[0]] = p[1] == "True"
    files = sorted(f for f in os.listdir(folder) if f.lower().endswith((".bmp", ".png")))
    seq = [bool(r[0]) for r in db.execute("select ok from inspections where camera = ? order by id", (c,))]
    tally = Counter()
    for n, ok in enumerate(seq):
        f = files[n % len(files)]
        want = ref.get(f)
        if want is None:
            tally["no reference"] += 1
        elif ok and not want:
            tally["OK where reference NG (UNSAFE)"] += 1
            bad += 1
            print(f"UNSAFE camera {c + 1} inspection {n + 1} frame {f}")
        elif ok:
            tally["OK = reference"] += 1
        elif want:
            tally["NG where reference OK"] += 1
        else:
            tally["NG = reference NG"] += 1
    print(f"camera {c + 1}: {len(seq)} inspections over {len(files)} frames: " + ", ".join(f"{k} {v}" for k, v in sorted(tally.items())))
print("NO OK THAT SHOULD BE NG" if bad == 0 else f"{bad} UNSAFE PARTS")
sys.exit(1 if bad else 0)
