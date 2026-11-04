#!/usr/bin/env python3
"""Locator recall: how many codes the locator puts a region on before decoding, per batch.

  python3 tools/bench/locator_recall.py                         the sample images, one line per batch
  python3 tools/bench/locator_recall.py --locator classical     the classical locator alone
  python3 tools/bench/locator_recall.py --via a.json [b.json] --images DIR
                                                                a labelled set in VIA format (BarBeR)

Runs the bench with CR_CANDS=1 (the engine lists every region it tried, and which step placed it:
"ai" and "classical" are the locator's first pass, "ai-recall" the search-harder pass that runs
when a case is short, "slot" a region placed from the case's lattice of read codes, not by a
locator). A code counts as located when its centre lies inside a region of the locator (ai,
classical or ai-recall).

Sample images: truth/samples.csv gives only the count per image, not where each code is. A read
code's centre comes from the bench (--codes). For a code that did not read, the engine lists the
unread regions it judges to be codes (--misses, with the reason it did not read): a listed miss
inside a locator region is "located, not decoded"; the rest of the shortfall (a miss placed only
by the slot lattice, or none listed at all) is "not located". Batches as batch_check.py: the
reader is carried across a batch's images (--carry), the time budget is the line's (--budget
auto), and the AI model is loaded (as the station does; batch_check.py runs without it).

VIA (BarBeR, https://ditto.ing.unimore.it/barber/): each annotated polygon is a code with its
type; the count per image is the number of polygons and the format the image's types ("any"
when mixed). Recall per type = polygons whose centre is inside a locator region; precision =
locator regions whose centre is inside a polygon's box (grown by a quarter). The images and the
annotations stay in a local folder, out of the repository.

Timing changes what the locator tries (the search-harder pass runs only while time is left), so
the numbers hold for a quiet machine: the run takes /tmp/vs-heavy-test.lock (as the other heavy
runs on the shared Mac) and prints the load before and after.
"""
import argparse
import csv
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from batch_check import batch_name, git, load_truth, operator_count  # noqa: E402

LOCK = "/tmp/vs-heavy-test.lock"
LOCATOR = ("ai", "classical", "ai-recall")
FIRST = ("ai", "classical")
FILE = re.compile(r"file (.+) run \d+$")
CAND = re.compile(r"cand \d+ box (-?\d+) (-?\d+) (\d+)x(\d+) ai \d read (\d) covered \d side \S+ score \S+ src (\S+)")
# BarBeR's type names -> the bench's format names (bench.cpp parseFormat)
VIA_FORMATS = {"QR": "qr", "DATAMATRIX": "dm", "AZTEC": "aztec", "PDF417": "pdf417", "EAN13": "ean13", "EAN8": "ean8",
               "UPC": "upca", "UPCA": "upca", "UPCE": "upce", "CODE128": "code128", "GS1-128": "code128",
               "CODE39": "code39", "CODE93": "code93", "I25": "itf", "ITF": "itf", "CODABAR": "codabar"}
TWO_D = ("QR", "DATAMATRIX", "AZTEC", "PDF417", "2D")


def take_lock(label):
    while True:
        try:
            os.mkdir(LOCK)
            break
        except FileExistsError:
            if time.time() - os.stat(LOCK).st_mtime > 45 * 60:
                subprocess.run(["rm", "-rf", LOCK])
                continue
            time.sleep(60)
    with open(os.path.join(LOCK, "owner"), "w") as f:
        f.write(f"{label} {time.ctime()}\n")


def run_bench(bench, folder, truth_csv, model, locator, budget, carry, tmp):
    """One bench run with the region list: (regions per file, read centres per file, misses per file)."""
    codes, misses, stats = (os.path.join(tmp, n) for n in ("codes.csv", "misses.csv", "stats.csv"))
    cmd = [bench, folder, truth_csv, "--budget", budget, "--runs", "1", "--codes", codes, "--misses", misses,
           "--csv", stats, "--locator", locator]
    if model:
        cmd += ["--model", model]
    if carry:
        cmd.append("--carry")
    p = subprocess.run(cmd, env={**os.environ, "CR_CANDS": "1"}, capture_output=True, text=True)
    if p.returncode != 0:
        sys.exit(f"bench failed ({p.returncode}): {p.stderr[-2000:]}")
    regions, cur = defaultdict(list), None
    for line in p.stderr.splitlines():
        if m := FILE.match(line):
            cur = m.group(1)
        elif cur and (m := CAND.match(line)):
            x, y, w, h, read, src = m.groups()
            regions[cur].append((int(x), int(y), int(w), int(h), read == "1", src))
    decoded = {}
    with open(stats, newline="") as f:
        for r in csv.DictReader(f):
            decoded[r["file"]] = int(r["decoded"])
    # one centre per text (a code can be listed once per symbol or copy), as many as the reader counted
    read, seen = defaultdict(list), set()
    with open(codes, newline="") as f:
        for r in csv.DictReader(f):
            if (r["file"], r["text"]) not in seen and len(read[r["file"]]) < decoded.get(r["file"], 0):
                seen.add((r["file"], r["text"]))
                read[r["file"]].append((float(r["x"]), float(r["y"])))
    missed = defaultdict(list)
    with open(misses, newline="") as f:
        for r in csv.DictReader(f):
            x, y, w, h = (int(r[k]) for k in "xywh")
            missed[r["file"]].append((x + w / 2, y + h / 2, r["reason"]))
    return regions, read, missed


def inside(pt, box, grow=0.0):
    x, y, w, h = box[:4]
    gx, gy = w * grow, h * grow
    return x - gx <= pt[0] <= x + w + gx and y - gy <= pt[1] <= y + h + gy


def located(pt, regions, sources):
    return any(r[5] in sources and inside(pt, r) for r in regions)


def alone(pt, regions, sources, others):
    """Inside a locator region that holds no more than one other code: the region singles it out
    (a classical region can merge a row of labels into one, which a decoder crop then has to split)."""
    return any(r[5] in sources and inside(pt, r) and sum(inside(o, r) for o in others) <= 2 for r in regions)


def score_samples(a, bench, model):
    repo = git("rev-parse", "--show-toplevel", cwd=os.getcwd())
    samples = a.samples or os.environ.get("VS_IMAGES") or os.path.join(
        os.path.dirname(os.path.abspath(os.path.join(repo, git("rev-parse", "--git-common-dir", cwd=repo)))),
        "samples", "Barcode Images")
    truth = [t for t in load_truth(os.path.join(repo, "truth", "samples.csv")) if os.path.exists(os.path.join(samples, t[0]))]
    batches = defaultdict(list)
    for name, fmt, visible in truth:
        batches[batch_name(fmt, visible)].append((name, fmt, visible))

    rows, why_unlocated, why_unread, all_regions = [], Counter(), Counter(), []
    with tempfile.TemporaryDirectory(prefix="locator-recall-") as tmp:
        for bname, items in sorted(batches.items()):
            d = tempfile.mkdtemp(prefix=bname.replace(" ", "_") + "-", dir=tmp)
            op = os.path.join(d, "operator.csv")
            with open(op, "w") as f:
                f.write("file,format,expected,note\n")
                for name, fmt, visible in items:
                    os.symlink(os.path.join(samples, name), os.path.join(d, name))
                    f.write(f"{name},{fmt},{operator_count(visible)},\n")
            regions, read, missed = run_bench(bench, d, op, model, a.locator, a.budget, True, d)
            for name, fmt, visible in items:
                regs = regions.get(name, [])
                all_regions += [(name, *r) for r in regs]
                centres = read.get(name, [])[:visible]
                n_read = len(centres)
                short = visible - n_read
                loc_read = sum(located(c, regs, LOCATOR) for c in centres)
                first_read = sum(located(c, regs, FIRST) for c in centres)
                # the listed misses, the located ones first: at most the shortfall counts
                ms = sorted(missed.get(name, []), key=lambda m: not located(m, regs, LOCATOR))[:max(short, 0)]
                loc_miss = [m for m in ms if located(m, regs, LOCATOR)]
                first_miss = sum(located(m, regs, FIRST) for m in ms)
                for m in ms:
                    (why_unread if m in loc_miss else why_unlocated)[m[2]] += 1
                why_unlocated["(no region listed)"] += max(short, 0) - len(ms)
                codes_here = centres + [m[:2] for m in loc_miss]
                single = sum(alone(c, regs, LOCATOR, codes_here) for c in codes_here)
                first = [r for r in regs if r[5] in FIRST]
                on_code = sum(any(inside(c, r) for c in centres) or any(inside(m, r) for m in loc_miss) for r in first)
                rows.append(dict(file=name, batch=bname, codes=visible, read=n_read,
                                 located_first=first_read + first_miss, located=loc_read + len(loc_miss), single=single,
                                 read_outside=n_read - loc_read, unread_located=len(loc_miss),
                                 not_located=max(short, 0) - len(loc_miss), regions=len(first), regions_on_code=on_code))

    def line(label, rs):
        s = {k: sum(r[k] for r in rs) for k in rs[0] if isinstance(rs[0][k], int)}
        c = max(s["codes"], 1)
        print(f"{label:<12} {len(rs):>3} {s['codes']:>6} {s['read']:>6}  {100 * s['located_first'] / c:6.1f} %"
              f"  {100 * s['located'] / c:6.1f} %  {100 * s['single'] / c:6.1f} %  {s['read_outside']:>6} {s['unread_located']:>8} {s['not_located']:>8}"
              f"  {100 * s['regions_on_code'] / max(s['regions'], 1):6.1f} %")

    print(f"locator {a.locator}, model {model or 'none'}, budget {a.budget}\n")
    print(f"{'batch':<12} {'img':>3} {'codes':>6} {'read':>6}  {'1st pass':>8}  {'+recall':>8}  {'single':>8}  {'rd-out':>6}"
          f" {'unread-L':>8} {'not-loc':>8}  {'precis.':>8}")
    for bname in sorted({r["batch"] for r in rows}):
        line(bname, [r for r in rows if r["batch"] == bname])
    line("all", rows)
    print("\n1st pass = codes inside a first-pass locator region (ai, classical); +recall = also the search-harder"
          "\nAI pass; single = located by a region holding at most 2 codes (not a merged row of labels);"
          "\nrd-out = read codes outside every locator region (read by a whole-image or slot step);"
          "\nunread-L = located but not decoded; not-loc = shortfall with no locator region;"
          "\nprecis. = first-pass regions holding a read or located code.")
    print("\nnot located, by diagnosed reason:  " + ", ".join(f"{k} {v}" for k, v in why_unlocated.most_common() if v))
    print("located, not decoded, by reason:   " + ", ".join(f"{k} {v}" for k, v in why_unread.most_common() if v))
    if a.boxes:
        # hand-placed boxes on codes (file,x,y,w,h): exact, where the miss list can be background
        by = defaultdict(lambda: Counter())
        with open(a.boxes, newline="") as f:
            for r in csv.DictReader(f):
                regs = [g[1:] for g in all_regions if g[0] == r["file"]]
                box = tuple(int(r[k]) for k in "xywh")
                c = (box[0] + box[2] / 2, box[1] + box[3] / 2)
                st = "first pass" if located(c, regs, FIRST) else "recall pass" if located(c, regs, LOCATOR) else \
                     "slot only" if located(c, regs, ("slot",)) else "no region"
                by[r["file"]][st] += 1
        print("\nhand-placed boxes, where the region came from:")
        for name, cnt in sorted(by.items()):
            print(f"  {name}: " + ", ".join(f"{k} {v}" for k, v in cnt.most_common()))
    return rows, all_regions


def score_via(a, bench, model):
    labels = {}  # image file -> [(type, cx, cy, (x, y, w, h))]
    for path in a.via:
        meta = json.load(open(path))
        meta = meta.get("_via_img_metadata", meta)
        for v in meta.values():
            codes = []
            for reg in v.get("regions", []):
                sa = reg["shape_attributes"]
                if sa["name"] == "polygon":
                    xs, ys = sa["all_points_x"], sa["all_points_y"]
                elif sa["name"] == "rect":
                    xs, ys = [sa["x"], sa["x"] + sa["width"]], [sa["y"], sa["y"] + sa["height"]]
                else:
                    continue
                typ = str(reg.get("region_attributes", {}).get("Type", "?")).upper()
                box = (min(xs), min(ys), max(xs) - min(xs), max(ys) - min(ys))
                codes.append((typ, sum(xs) / len(xs), sum(ys) / len(ys), box))
            if codes:
                labels[v["filename"]] = codes
    images = {}
    for root, _, files in os.walk(a.images):
        for f in files:
            if f in labels:
                images.setdefault(f, os.path.join(root, f))
    if not images:
        sys.exit("no annotated image found under --images")
    found, total, read_n = Counter(), Counter(), Counter()
    regions_all = regions_on = 0
    with tempfile.TemporaryDirectory(prefix="locator-recall-via-") as tmp:
        op = os.path.join(tmp, "operator.csv")
        with open(op, "w") as f:
            f.write("file,format,expected,note\n")
            for name, path in sorted(images.items()):
                fmts = {VIA_FORMATS.get(t, "any") for t, *_ in labels[name]}
                os.symlink(os.path.abspath(path), os.path.join(tmp, name))
                f.write(f"{name},{fmts.pop() if len(fmts) == 1 else 'any'},{len(labels[name])},\n")
        regions, read, _ = run_bench(bench, tmp, op, model, a.locator, a.budget, False, tmp)
    for name in images:
        regs = regions.get(name, [])
        for typ, cx, cy, box in labels[name]:
            total[typ] += 1
            found[typ] += located((cx, cy), regs, LOCATOR)
            read_n[typ] += any(inside(c, box, 0.25) for c in read.get(name, []))
        first = [r for r in regs if r[5] in FIRST]
        regions_all += len(first)
        regions_on += sum(any(inside((r[0] + r[2] / 2, r[1] + r[3] / 2), box, 0.25) for *_, box in labels[name]) for r in first)
    print(f"locator {a.locator}, model {model or 'none'}, budget {a.budget}, {len(images)} images\n")
    print(f"{'type':<14} {'codes':>6} {'located':>8} {'recall':>8} {'read':>6}")
    for group, types in (("1D", [t for t in total if t not in TWO_D]), ("2D", [t for t in total if t in TWO_D])):
        for t in sorted(types, key=lambda t: -total[t]):
            print(f"{t:<14} {total[t]:>6} {found[t]:>8} {100 * found[t] / total[t]:7.1f} % {read_n[t]:>6}")
        n = sum(total[t] for t in types)
        if n:
            print(f"{'all ' + group:<14} {n:>6} {sum(found[t] for t in types):>8} "
                  f"{100 * sum(found[t] for t in types) / n:7.1f} % {sum(read_n[t] for t in types):>6}")
    print(f"\nprecision: {regions_on} of {regions_all} first-pass regions on a labelled code "
          f"({100 * regions_on / max(regions_all, 1):.1f} %)")


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--samples", help="sample image folder (default: $VS_IMAGES, else samples/Barcode Images)")
    ap.add_argument("--via", nargs="+", help="VIA annotation files (BarBeR) instead of the sample images")
    ap.add_argument("--images", help="folder with the VIA set's images (searched recursively)")
    ap.add_argument("--bench", help="bench binary (default: engine/build/bench)")
    ap.add_argument("--model", help="CodeNet model (default: ml/models/codenet.onnx; 'none' for none)")
    ap.add_argument("--locator", default="auto", choices=("auto", "classical", "ai", "ai+classical"))
    ap.add_argument("--budget", help="time budget, ms or auto (default: auto for the samples, 2000 for --via)")
    ap.add_argument("--csv", help="write the per-image rows here (sample images)")
    ap.add_argument("--boxes", help="hand-placed code boxes, file,x,y,w,h (sample images): located or not, exactly")
    ap.add_argument("--regions", help="write every region tried here (sample images): file,x,y,w,h,read,src")
    ap.add_argument("--no-lock", action="store_true", help=f"do not take {LOCK}")
    a = ap.parse_args()
    if a.via and not a.images:
        ap.error("--via needs --images")
    repo = git("rev-parse", "--show-toplevel", cwd=os.getcwd())
    bench = a.bench or os.path.join(repo, "engine", "build", "bench")
    model = None if a.model == "none" else a.model or os.path.join(repo, "ml", "models", "codenet.onnx")
    a.budget = a.budget or ("2000" if a.via else "auto")
    if not a.no_lock:
        take_lock(f"locator_recall {os.getpid()}")
    try:
        print(f"load {os.getloadavg()[0]:.1f} on {os.cpu_count()} cores before")
        if a.via:
            score_via(a, bench, model)
        else:
            rows, regions = score_samples(a, bench, model)
            if a.regions:
                with open(a.regions, "w", newline="") as f:
                    w = csv.writer(f)
                    w.writerow(["file", "x", "y", "w", "h", "read", "src"])
                    w.writerows((n, x, y, wd, h, int(rd), src) for n, x, y, wd, h, rd, src in regions)
            if a.csv:
                with open(a.csv, "w", newline="") as f:
                    w = csv.DictWriter(f, fieldnames=list(rows[0]))
                    w.writeheader()
                    w.writerows(rows)
        print(f"\nload {os.getloadavg()[0]:.1f} after (timing-bound regions: valid on a quiet machine only)")
    finally:
        if not a.no_lock:
            subprocess.run(["rm", "-rf", LOCK])


if __name__ == "__main__":
    main()
