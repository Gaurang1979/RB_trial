#!/usr/bin/env python3
"""Batch check: the sample images, one batch per product, compared with the baseline.

The 'bench' heavy test (scripts/heavy-pick.sh tests), run by the session that changes the reading code. Runs once per
change to the reading code and never twice for the same code:

  python3 tools/bench/batch_check.py            run if needed, compare with the baseline
  python3 tools/bench/batch_check.py --force    run even if this code already passed
  python3 tools/bench/batch_check.py --update   run and save the result as the new baseline
  python3 tools/bench/batch_check.py --add-texts   run, and add the texts read that are not in
                                                   truth/codes.csv to it (only after checking them)

A batch is a product as the operator sets it: code type + case size (12/24/45/48/96).
Images that show part of a case (42-47 visible) run with the count set to 48; the
shrink-wrapped excise images (5/11/28/40) run as 'other' with the visible count.
Reads are scored against the visible count in truth/samples.csv. The images are customer
data: they come from --samples, else $VS_IMAGES, else samples/Barcode Images of the main
checkout. The engine's unit tests are not run here: run ctest yourself when engine/ changed
(scripts/heavy-pick.sh units lists them).

Skips (exit 0) when nothing under engine/, tools/bench/ or truth/ changed against
--base (default origin/main), or when the same code and sample set already passed
on this Mac (stamp in the git common dir, shared by all worktrees).

Wrong reads: every text read is looked up in truth/codes.csv (the true texts per image, by
SHA-256; format in truth/README.md). A text that is not there is a wrong read, and any wrong read
fails the check, whatever the read rate gained: a wrong read is worse than a miss. A run's wrong
reads are never forgiven by a better second run. Besides the sample batches, the check runs the
adversarial batch: synthetic cases from tools/bench/adversarial.cpp (engine/build/adversarial;
another product's codes, codes one module or codeword off, damage beyond error correction,
near-duplicates, serial-run traps, textures and print with no code), which must give no read or
only the codes printed on them. A code that a change reads for the first time is not in the
truth either: check it (the image, the serial run, an independent reader) and add it with
--add-texts; never add a text you have not checked.

Fails (exit 1) on any wrong read, when a batch reads fewer codes than the baseline, or an image goes over
its time budget and is more than 15 % slower than in the baseline. The baseline is the
worst of 3 runs (--update), and a batch that looks worse is run once more and judged on
its better run, so run-to-run noise does not fail it. Prints one line per batch, and
details only for what got worse.

Not judged (exit 2) when the machine is too busy: reads run against a time budget, so on
a loaded machine they measure the load, not the code (2026-10-30: at a load of 59-78 on
8 cores a known-good commit failed three times and the tip fell to 17.9 % read). The
check waits up to --wait minutes for the 1-minute load average to drop to --max-load
(default half the cores), and gives up if it doesn't. A run during which the load went
over --max-load plus the cores (the bench itself uses every core) is printed but not
judged. A run that is not judged saves no pass and no baseline.
"""
import argparse
import csv
import hashlib
import os
import subprocess
import sys
import tempfile
import time

CASE_SIZES = (12, 24, 45, 48, 96)
WATCHED = ("engine", "tools/bench", "truth", ":(exclude)truth/batch-baseline.csv")
SLOWER = 1.15
NOT_JUDGED = 2
ADVERSARIAL = "adversarial"


def git(*args, cwd):
    return subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True).stdout.strip()


def target_ms(count):  # bench.cpp targetMs
    return 100 if count <= 12 else 275 if count <= 24 else 500 if count <= 48 else 600


def operator_count(visible):
    if visible in CASE_SIZES:
        return visible
    if 42 <= visible < 48:
        return 48
    return visible  # not a product-master size: 'other'


def batch_name(fmt, visible):
    n = operator_count(visible)
    return f"{n} {fmt.upper()}" if n in CASE_SIZES else f"other {fmt.upper()}"


def load_truth(path):
    rows = []
    with open(path, newline="") as f:
        for r in csv.reader(line for line in f if not line.startswith("#")):
            if r and r[0] != "file":
                rows.append((r[0], r[1], int(r[2])))
    return rows


def load_texts(path):
    """truth/codes.csv: file -> set of SHA-256 (lower-case hex) of its true texts."""
    texts = {}
    if os.path.exists(path):
        with open(path, newline="") as f:
            for r in csv.reader(line for line in f if not line.startswith("#")):
                if len(r) >= 2 and r[0] != "file":
                    texts.setdefault(r[0], set()).add(r[1].strip().lower())
    return texts


def fingerprint(repo, samples, files):
    h = hashlib.sha256()
    h.update(git("ls-files", "-s", *WATCHED, cwd=repo).encode())
    h.update(git("diff", "HEAD", "--", *WATCHED, cwd=repo).encode())  # uncommitted edits count
    for name in files:
        st = os.stat(os.path.join(samples, name))
        h.update(f"{name}:{st.st_size}:{int(st.st_mtime)}".encode())
    return h.hexdigest()[:16]


def bench_rows(out, codes, bname, visible):
    """A bench run's csv and --codes csv: file -> (batch, visible, read, ms_max, budget, wrong).
    read counts only texts in the truth; wrong: (sha256, text, step, x, y) of each text not in it."""
    wrong = {}
    with open(codes, newline="") as f:
        for r in csv.DictReader(f):
            if r["in_truth"] != "1":
                wrong.setdefault(r["file"], []).append((r["sha256"], r["text"], r["step"], r["x"], r["y"]))
    rows = {}
    with open(out, newline="") as f:
        for r in csv.DictReader(f):
            v = visible[r["file"]]
            w = tuple(wrong.get(r["file"], ()))
            rows[r["file"]] = (bname, v, min(int(r["decoded"]) - len(w), v), float(r["total_ms_max"]),
                               target_ms(int(r["expected"])), w)
    return rows


def run_bench(bench, folder, op, texts, bname, visible):
    out, codes = folder + ".csv", folder + "-codes.csv"
    subprocess.run([bench, folder, op, "--budget", "auto", "--runs", "1", "--carry", "--csv", out,
                    "--texts", texts, "--codes", codes], check=True, stdout=subprocess.DEVNULL)
    return bench_rows(out, codes, bname, visible)


def run_batch(bench, tmp, samples, bname, items, texts):
    """One carried bench run of a batch: file -> (batch, visible, read, ms_max, budget, wrong)."""
    d = tempfile.mkdtemp(prefix=bname.replace(" ", "_") + "-", dir=tmp)
    op = os.path.join(d, "operator.csv")
    with open(op, "w") as f:
        f.write("file,format,expected,note\n")
        for name, fmt, visible in items:
            os.symlink(os.path.join(samples, name), os.path.join(d, name))
            f.write(f"{name},{fmt},{operator_count(visible)},\n")
    return run_bench(bench, d, op, texts, bname, {n: v for n, _, v in items})


def run_adversarial(generator, bench, tmp):
    """The adversarial batch, drawn afresh: file -> row as run_batch; visible = codes printed."""
    d = os.path.join(tmp, ADVERSARIAL)
    subprocess.run([generator, d], check=True, stdout=subprocess.DEVNULL)
    texts = os.path.join(d, "codes.csv")
    visible = {f: len(t) for f, t in load_texts(texts).items()}
    with open(os.path.join(d, "operator.csv"), newline="") as f:
        for r in csv.DictReader(f):
            visible.setdefault(r["file"], 0)
    return run_bench(bench, d, os.path.join(d, "operator.csv"), texts, ADVERSARIAL, visible)


def merge_runs(rows, more, worst):
    """Two runs of a batch. Reads and time: the worse run (worst) or the better one. Wrong reads:
    every one either run made, always: a wrong read is never noise."""
    pick_read, pick_ms = (min, max) if worst else (max, min)
    out = {}
    for f, r in rows.items():
        m = more[f]
        wrong = r[5] + tuple(w for w in m[5] if w[0] not in {x[0] for x in r[5]})
        out[f] = (*r[:2], pick_read(r[2], m[2]), pick_ms(r[3], m[3]), r[4], wrong)
    return out


def add_texts(path, result):
    """Append the wrong reads of the sample batches to truth/codes.csv (hash only): file -> count."""
    known = load_texts(path)
    added = {}
    new = not os.path.exists(path)
    with open(path, "a", newline="") as f:
        w = csv.writer(f, lineterminator="\n")
        if new:
            w.writerow(["file", "sha256", "text"])
        for name in sorted(result):
            r = result[name]
            if r[0] == ADVERSARIAL:
                continue
            for sha, *_ in r[5]:
                if sha not in known.get(name, set()):
                    w.writerow([name, sha, ""])
                    known.setdefault(name, set()).add(sha)
                    added[name] = added.get(name, 0) + 1
    return added


def wrong_notes(rows):
    """Every wrong read, one note per image."""
    return [f"WRONG READ {f}: " + "; ".join(f"{text} ({step} at {x},{y}; {sha[:12]})" for sha, text, step, x, y in r[5])
            for f, r in sorted(rows.items()) if r[5]]


def problems(rows, base):
    """What got worse than the baseline: fewer codes read, or newly over budget and slower."""
    notes = []
    had = [f for f in rows if f in base]
    base_read = sum(base[f][0] for f in had)
    now_read = sum(rows[f][2] for f in had)
    if now_read < base_read:
        notes.append(f"reads {now_read} < baseline {base_read}: " +
                     ", ".join(f"{f} {rows[f][2]}/{base[f][0]}" for f in had if rows[f][2] < base[f][0]))
    slow = [f for f, r in rows.items() if r[3] > r[4] and (f not in base or r[3] > base[f][1] * SLOWER)]
    if slow:
        notes.append("over budget: " + ", ".join(f"{f} {rows[f][3]:.0f}/{rows[f][4]} ms" for f in slow))
    return notes


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--samples", help="sample image folder (default: $VS_IMAGES, else samples/Barcode Images)")
    ap.add_argument("--bench", help="bench binary (default: engine/build/bench, built if needed)")
    ap.add_argument("--base", default="origin/main", help="skip when nothing watched changed against this ref")
    ap.add_argument("--force", action="store_true", help="run even if this code already passed")
    ap.add_argument("--update", action="store_true", help="save this run as the new baseline")
    ap.add_argument("--add-texts", action="store_true",
                    help="add the texts read that are not in truth/codes.csv to it (check them first)")
    ap.add_argument("--max-load", type=float, default=(os.cpu_count() or 8) / 2,
                    help="1-minute load average to wait for before running (default: half the cores)")
    ap.add_argument("--wait", type=float, default=10, help="minutes to wait for the load to drop (default 10)")
    a = ap.parse_args()

    repo = git("rev-parse", "--show-toplevel", cwd=os.getcwd())
    common = os.path.abspath(os.path.join(repo, git("rev-parse", "--git-common-dir", cwd=repo)))
    main_checkout = os.path.dirname(common)
    samples = a.samples or next((p for p in (os.environ.get("VS_IMAGES", ""),
                                             os.path.join(repo, "samples", "Barcode Images"),
                                             os.path.join(main_checkout, "samples", "Barcode Images"))
                                 if os.path.isdir(p)), None)
    if not samples:
        sys.exit("batch-check: no sample folder; pass --samples")
    baseline_path = os.path.join(repo, "truth", "batch-baseline.csv")

    texts_path = os.path.join(repo, "truth", "codes.csv")

    if not (a.force or a.update or a.add_texts):
        if git("rev-parse", "--verify", "-q", a.base, cwd=repo):
            changed = git("diff", "--name-only", f"{a.base}...HEAD", "--", *WATCHED, cwd=repo)
            changed += git("diff", "--name-only", "HEAD", "--", *WATCHED, cwd=repo)
            if not changed.strip():
                print(f"batch-check: skip, nothing under engine/, tools/bench/ or truth/ changed against {a.base}")
                return 0

    truth = [t for t in load_truth(os.path.join(repo, "truth", "samples.csv"))
             if os.path.exists(os.path.join(samples, t[0]))]
    stamp_dir = os.path.join(common, "batch-check")
    fp = fingerprint(repo, samples, sorted(t[0] for t in truth))
    stamp = os.path.join(stamp_dir, fp)
    if os.path.exists(stamp) and not (a.force or a.update or a.add_texts):
        print(f"batch-check: skip, this code and sample set already passed ({open(stamp).read().strip()})")
        return 0

    bench = a.bench or os.path.join(repo, "engine", "build", "bench")
    generator = os.path.join(os.path.dirname(bench), "adversarial")
    if not a.bench:
        build = os.path.join(repo, "engine", "build")
        if not os.path.exists(os.path.join(build, "build.ninja")):
            subprocess.run(["cmake", "-S", os.path.join(repo, "engine"), "-B", build, "-G", "Ninja",
                            "-DCMAKE_BUILD_TYPE=Release"], check=True, stdout=subprocess.DEVNULL)
        subprocess.run(["cmake", "--build", build], check=True, stdout=subprocess.DEVNULL)
    if not os.access(generator, os.X_OK):
        sys.exit(f"batch-check: no adversarial generator at {generator}; build the engine's 'adversarial' target")

    cores = os.cpu_count() or 8
    deadline = time.time() + a.wait * 60
    while os.getloadavg()[0] > a.max_load:
        if time.time() >= deadline:
            print(f"batch-check: not judged, machine busy (load {os.getloadavg()[0]:.0f} on {cores} cores, "
                  f"waited {a.wait:g} min for {a.max_load:g}); run it again when the machine is quiet")
            return NOT_JUDGED
        time.sleep(30)
    peak = os.getloadavg()[0]

    batches = {}
    for name, fmt, visible in truth:
        batches.setdefault(batch_name(fmt, visible), []).append((name, fmt, visible))

    base = {}
    if os.path.exists(baseline_path):
        with open(baseline_path, newline="") as f:
            for r in csv.DictReader(f):
                base[r["file"]] = (int(r["read"]), float(r["ms_max"]))

    result = {}  # file -> (batch, visible, read, ms_max, budget, wrong reads)
    with tempfile.TemporaryDirectory(prefix="batch-check-") as tmp:
        for bname, items in sorted(batches.items()):
            rows = run_batch(bench, tmp, samples, bname, items, texts_path)
            if a.update or a.add_texts:  # the baseline is the worst of 3 runs, so noise alone never fails a check
                for _ in range(2):
                    rows = merge_runs(rows, run_batch(bench, tmp, samples, bname, items, texts_path), worst=True)
            elif base and problems(rows, base):  # confirm: worse in the best of two runs
                rows = merge_runs(rows, run_batch(bench, tmp, samples, bname, items, texts_path), worst=False)
            result.update(rows)
            peak = max(peak, os.getloadavg()[0])
        result.update(run_adversarial(generator, bench, tmp))
        peak = max(peak, os.getloadavg()[0])

    added = {}
    if a.add_texts:
        for line in wrong_notes({f: r for f, r in result.items() if r[0] != ADVERSARIAL}):
            print(f"added to truth: {line[len('WRONG READ '):]}")
        added = add_texts(texts_path, result)
        result = {f: (*r[:2], min(r[2] + len(r[5]), r[1]), *r[3:5], ()) if f in added else r
                  for f, r in result.items()}

    judged = peak <= a.max_load + cores
    failed = False
    total_read = total_visible = 0
    total_wrong = 0
    for bname in sorted(batches) + [ADVERSARIAL]:
        rows = {f: r for f, r in result.items() if r[0] == bname}
        read = sum(r[2] for r in rows.values())
        vis = sum(r[1] for r in rows.values())
        wrong = sum(len(r[5]) for r in rows.values())
        total_wrong += wrong
        if bname != ADVERSARIAL:
            total_read += read
            total_visible += vis
        notes = wrong_notes(rows)  # the adversarial batch is judged on wrong reads only
        if base and not (a.update or a.add_texts) and bname != ADVERSARIAL:
            notes += problems(rows, base)
        failed |= bool(notes)
        slowest = max(r[3] for r in rows.values())
        label = "n/j " if not judged else "FAIL" if notes else "ok  "
        print(f"{label} {bname:10} {read}/{vis} read, {wrong} wrong, slowest {slowest:.0f} ms"
              + "".join(f"\n     {n}" for n in notes))
    print(f"all: {total_read}/{total_visible} read ({100 * total_read / max(total_visible, 1):.1f} %), "
          f"{total_wrong} wrong, {len(result)} images")
    if added:
        print(f"texts added to {os.path.relpath(texts_path, repo)}: {sum(added.values())} on {len(added)} images "
              "(commit them with the change, and say in the pull request how they were checked)")

    if not judged:
        print(f"batch-check: not judged, the load reached {peak:.0f} on {cores} cores during the run "
              f"(limit {a.max_load + cores:g}); no pass or baseline saved, run it again when the machine is quiet")
        return NOT_JUDGED
    if (a.update or not base) and total_wrong:
        print("baseline not saved: a run with wrong reads is no baseline")
    elif a.update or not base:
        with open(baseline_path, "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["file", "batch", "visible", "read", "ms_max"])
            for name in sorted(result):
                b, v, r, ms, _, _ = result[name]
                if b != ADVERSARIAL:
                    w.writerow([name, b, v, r, f"{ms:.0f}"])
        print(f"baseline saved: {os.path.relpath(baseline_path, repo)} (commit it with the change)")
    if not failed and not a.add_texts:  # the truth changed: the next run is the check
        os.makedirs(stamp_dir, exist_ok=True)
        with open(stamp, "w") as f:
            f.write(f"{git('rev-parse', '--short', 'HEAD', cwd=repo)}, {total_read}/{total_visible}\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
