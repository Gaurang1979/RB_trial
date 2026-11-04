"""Measure an OCR line model per print type, scored the way the engine scores (engine/src/ocr/).

    python -m ocr.eval_ocr --make <dir> --n 400 --seed 900          # fixed synthetic test sets, one per print type
    python -m ocr.eval_ocr --model models/ocrnet.onnx --set laser=<dir>/laser=<dir>/laser/labels.tsv ...
    python -m ocr.eval_ocr --model m.onnx --sets <dir> --set real=<crops>=<test.tsv> --temp 1.5

The model runs in OpenCV dnn, as in the engine. Per set:

  free read   character accuracy (spaces ignored), lines read exactly, and the score of each wrong read:
              the line score is the lowest character confidence x 100 (vs_ocr_line.quality); a wrong read
              at or above --min-quality (default 60, the tool's default) would show every character OK
  check       the expected text force-aligned to the model output (CTC Viterbi, as verify.cpp): the
              true text must pass (every character >= min quality); the text with one character changed
              must fail. "random": any character from the bench's pool; "look-alike": a character that
              looks like it (0/O/D, 1/I/7, 5/S, 8/B ...), the change a misprint or a wrong job really makes

--every N   a much larger safety set: each line checked against every look-alike at every place, and N
            random characters at every place (one character changed each time); counted in two more columns.
            One change per line (the default columns) is too few to show a rate below about 1 in 400.

--temp T divides the logits by T before the softmax (confidence calibration, see train_ocr --calibrate).

Read text safety (vs_ocr_set_agreement, engine/src/ocr/recognize.cpp linePosterior), as the engine scores a free read:

  --line-conf  the line score is also capped by the calibrated line confidence: 100 x P(read text | line)^(1/gamma),
               P by the CTC forward algorithm with spaces counted as blanks (a dropped "." or an extra letter
               lowers it, which the lowest character confidence never shows). --gamma sets the calibration
               (default: the engine's kLineGamma)
  --agree X    a second reader must read the same text (spaces ignored) or the line scores 0: "shift" = the
               same model on the line moved by half an output step (2 px), or the path of a second .onnx
  --fit-gamma  the most lenient gamma at which no wrong read that the print decides scores >= min quality on
               the given sets (calibration sets, never the test sets)

A wrong read is "glyph-identical" when every character it got wrong is drawn the same as what it read in the
line's font (O and 0 in many fonts): no reader can decide it from the print, only a format can. The column
needs the fonts.tsv that --make writes beside labels.tsv.
"""

import argparse
import os
import random
from functools import lru_cache

import cv2
import numpy as np
from PIL import Image, ImageDraw

from . import synth_text as st

POOL = "0123456789ABCDEFGHJKLMNPRSTUVWXYZ"
LOOKALIKE = {
    "0": "ODQ8", "O": "0DQC", "D": "0OB", "Q": "O0", "C": "GO(", "G": "6C", "6": "Gb8", "b": "6h",
    "1": "I7l", "I": "1lT", "l": "1I", "7": "1T", "T": "7I", "2": "Z7", "Z": "2", "5": "S6", "S": "58",
    "8": "B3S0", "B": "8E3", "3": "8B", "E": "FB", "F": "EP", "P": "FR", "R": "PB", "4": "A9", "A": "4",
    "9": "g4", "g": "9q", "q": "g9", "U": "VO", "V": "UY", "Y": "V", "M": "NW", "N": "MH", "H": "N",
    "W": "M", "K": "X", "X": "K", "/": "1", ":": ".", ".": ",", ",": ".", "-": ".",
}


class _FontRng(random.Random):
    """random.Random that remembers the last font st.sample chose (same draws, so the same lines)."""
    font = ""

    def choice(self, seq):
        c = super().choice(seq)
        if seq is st.FONTS:
            self.font = c
        return c


def make_sets(out, n, seed, kinds):
    for k in kinds:
        d = os.path.join(out, k)
        os.makedirs(d, exist_ok=True)
        rng = _FontRng(seed + 101 * st.KINDS.index(k))
        with open(os.path.join(d, "labels.tsv"), "w", encoding="utf-8") as f, \
                open(os.path.join(d, "fonts.tsv"), "w", encoding="utf-8") as ff:
            for i in range(n):
                img, text = st.sample(rng, kind=k)
                name = f"{k}_{i:04d}.png"
                cv2.imwrite(os.path.join(d, name), img)
                f.write(f"{name}\t{text}\n")
                ff.write(f"{name}\t{rng.font}\n")
        print("made", d, n)


def load_fonts(folder):
    p = os.path.join(folder, "fonts.tsv")
    if not os.path.exists(p):
        return {}
    with open(p, encoding="utf-8") as f:
        return dict(line.rstrip("\n").split("\t", 1) for line in f if "\t" in line)


@lru_cache(maxsize=None)
def _glyph(font, c, cap=20):
    """c drawn in the font at a capital height of `cap` px (the lines' size), blurred by the print's spread."""
    fp = st._font_for(c, font)
    if fp is None:
        return None
    size = int(round(100 * cap / st._cap_height(st.FC.get(fp, 100))))
    im = Image.new("L", (4 * cap, 3 * cap), 0)
    ImageDraw.Draw(im).text((cap, int(2.2 * cap)), c, font=st.FC.get(fp, size), fill=255, anchor="ls")
    a = cv2.GaussianBlur(np.asarray(im, np.float32), (0, 0), 1.0)
    a -= a.mean()
    n = np.sqrt((a * a).sum())
    return a / n if n > 0 else None


def same_glyph(font, a, b):
    """True when the font draws a and b alike at the lines' size (correlation >= 0.9: O and 0 in Menlo,
    PT Mono and Andale Mono, I and l in Futura; not O and 0 in Arial or Helvetica)."""
    ga, gb = _glyph(font, a), _glyph(font, b)
    return ga is not None and gb is not None and float((ga * gb).sum()) >= 0.9


def glyph_identical(font, truth, pred):
    """The read differs from the truth only by characters the font draws alike."""
    if not font or len(truth) != len(pred):
        return False
    return all(t == p or same_glyph(font, t, p) for t, p in zip(truth, pred))


def load_set(spec):
    name, folder, tsv = spec.split("=", 2)
    items = []
    with open(tsv, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if line and not line.startswith("#"):
                fn, text = line.split("\t", 1)
                p = os.path.join(folder, fn)
                if os.path.exists(p):
                    items.append((p, text))
    return name, items


class Model:
    def __init__(self, path, temp=1.0):
        self.net = cv2.dnn.readNetFromONNX(path)
        self.temp = temp

    def logp(self, img):
        x = img.astype(np.float32)[None, None] / 255.0
        self.net.setInput(x)
        z = self.net.forward()[0, :, 0, :].T / self.temp    # T x C
        z = z - z.max(1, keepdims=True)
        return z - np.log(np.exp(z).sum(1, keepdims=True))


GAMMA = 0.9       # kLineGamma in engine/src/ocr/ocr.hpp: python -m ocr.eval_ocr --fit-gamma
SPACE = st.CHARSET.index(" ") + 1


def line_posterior(lp, text):
    """P(text | line) summed over every CTC alignment, spaces counted as blanks (they are never checked);
    as linePosterior in engine/src/ocr/recognize.cpp."""
    idx = {c: i + 1 for i, c in enumerate(st.CHARSET)}
    lab = [idx.get(c, -1) for c in text if c != " "]
    if not lab or min(lab) < 0:
        return 0.0
    blank = np.logaddexp(lp[:, 0], lp[:, SPACE])
    ext = [0] * (2 * len(lab) + 1)
    ext[1::2] = lab
    em = lp[:, ext].copy()
    em[:, 0::2] = blank[:, None]
    skip = np.array([s >= 3 and s % 2 == 1 and ext[s] != ext[s - 2] for s in range(len(ext))])
    a = np.full(len(ext), -np.inf)
    a[0], a[1] = em[0, 0], em[0, 1]
    for t in range(1, lp.shape[0]):
        n = a.copy()
        n[1:] = np.logaddexp(n[1:], a[:-1])
        n[skip] = np.logaddexp(n[skip], a[np.nonzero(skip)[0] - 2])
        a = n + em[t]
    return float(np.exp(np.logaddexp(a[-1], a[-2])))


def line_conf(p, gamma):
    return 100.0 * p ** (1.0 / gamma)


def shifted(img, k=2):
    """The line moved right by k px (half an output step for k = 2), padded with its own background,
    width kept a multiple of 4: as the engine's shifted re-read."""
    bg = int(np.median(img[:, :2]))
    out = cv2.copyMakeBorder(img, 0, 0, k, (-(img.shape[1] + k)) % 4, cv2.BORDER_CONSTANT, value=bg)
    return out


def greedy(lp):
    """-> [(char, conf)] as greedyRead in recognize.cpp."""
    out, prev = [], 0
    best = lp.argmax(1)
    for t, c in enumerate(best):
        p = float(np.exp(lp[t, c]))
        if c != 0 and c == prev and out:
            out[-1][1] = max(out[-1][1], p)
        elif c != 0:
            out.append([st.CHARSET[c - 1], p])
        prev = c
    return out


def force_scores(lp, labels):
    """CTC Viterbi as forceAlign in verify.cpp; -> per-label score 0..100 (best probability in its
    columns), or None when no alignment exists."""
    T, L = lp.shape[0], len(labels)
    if L == 0:
        return []
    S = 2 * L + 1
    emit = np.empty((T, S), np.float32)
    emit[:, 0::2] = lp[:, [0]]
    for k, c in enumerate(labels):
        emit[:, 2 * k + 1] = lp[:, c] if c > 0 else -13.8
    skip = np.zeros(S, bool)
    for s in range(3, S, 2):
        c, p = labels[s // 2], labels[s // 2 - 1]
        skip[s] = not (c >= 1 and c == p)
    prev = np.full(S, -np.inf, np.float32)
    prev[0] = emit[0, 0]
    if S > 1:
        prev[1] = emit[0, 1]
    back = np.zeros((T, S), np.int8)
    for t in range(1, T):
        c0 = prev
        c1 = np.concatenate([[-np.inf], prev[:-1]])
        c2 = np.where(skip, np.concatenate([[-np.inf, -np.inf], prev[:-2]]), -np.inf)
        stack = np.stack([c0, c1, c2])
        frm = stack.argmax(0)              # ties keep the earlier option, as the C++ strict ">"
        best = stack[frm, np.arange(S)]
        back[t] = frm
        prev = best + emit[t]
    s = S - 1
    if S > 1 and prev[S - 2] > prev[S - 1]:
        s = S - 2
    if not np.isfinite(prev[s]):
        return None
    score = np.zeros(L)
    for t in range(T - 1, -1, -1):
        if s % 2 == 1:
            k = s // 2
            c = labels[k]
            score[k] = max(score[k], float(np.exp(lp[t, c])) if c > 0 else 0.0)
        if t > 0:
            s -= back[t, s]
    return list(100 * score)


def force_scores_batch(lp, labs):
    """force_scores for N label lists of one length at once (labs: N x L ints) -> N x L scores 0..100,
    0 for every label of a list with no alignment. The same Viterbi, ties and back-tracking as forceAlign."""
    T = lp.shape[0]
    N, L = labs.shape
    S = 2 * L + 1
    emit = np.empty((N, T, S), np.float32)
    emit[:, :, 0::2] = lp[None, :, [0]]
    e = np.transpose(lp[:, np.where(labs > 0, labs, 0)], (1, 0, 2))     # N x T x L
    emit[:, :, 1::2] = np.where((labs > 0)[:, None, :], e, -13.8)
    skip = np.zeros((N, S), bool)
    if L > 1:
        skip[:, 3::2] = ~((labs[:, 1:] >= 1) & (labs[:, 1:] == labs[:, :-1]))
    prev = np.full((N, S), -np.inf, np.float32)
    prev[:, 0] = emit[:, 0, 0]
    prev[:, 1] = emit[:, 0, 1]
    back = np.zeros((T, N, S), np.int8)
    pad1, pad2 = np.full((N, 1), -np.inf, np.float32), np.full((N, 2), -np.inf, np.float32)
    for t in range(1, T):
        c1 = np.concatenate([pad1, prev[:, :-1]], 1)
        c2 = np.where(skip, np.concatenate([pad2, prev[:, :-2]], 1), -np.inf)
        stack = np.stack([prev, c1, c2])
        frm = stack.argmax(0)
        back[t] = frm
        prev = np.take_along_axis(stack, frm[None], 0)[0] + emit[:, t]
    ar = np.arange(N)
    s = np.where(prev[:, S - 2] > prev[:, S - 1], S - 2, S - 1)
    ok = np.isfinite(prev[ar, s])
    score = np.zeros((N, L))
    for t in range(T - 1, -1, -1):
        odd = s % 2 == 1
        n, k = ar[odd], s[odd] // 2
        c = labs[n, k]
        p = np.where(c > 0, np.exp(lp[t, np.maximum(c, 0)]), 0.0)
        score[n, k] = np.maximum(score[n, k], p)
        if t > 0:
            s = s - back[t, ar, s]
    score[~ok] = 0
    return 100 * score


def every_change(text, rng, n_random):
    """(kind, changed text) for every look-alike at every place and n_random random characters per place"""
    out = []
    for i, ch in enumerate(text):
        if ch == " ":
            continue
        for c in LOOKALIKE.get(ch, ""):
            if c != ch:
                out.append(("every_look", text[:i] + c + text[i + 1:]))
        pool = [x for x in POOL if x != ch]
        for c in rng.sample(pool, min(n_random, len(pool))):
            out.append(("every_rand", text[:i] + c + text[i + 1:]))
    return out


def check_pass(lp, text, min_q):
    idx = {c: i + 1 for i, c in enumerate(st.CHARSET)}
    lab = [idx.get(c, -1) for c in text if c != " "]
    sc = force_scores(lp, lab)
    return sc is not None and len(sc) > 0 and min(sc) >= min_q


def mutate(text, rng, lookalike):
    pos = [i for i, c in enumerate(text) if c != " " and (not lookalike or c in LOOKALIKE)]
    if not pos:
        return None
    i = rng.choice(pos)
    pool = LOOKALIKE[text[i]] if lookalike else POOL
    c = rng.choice([x for x in pool if x != text[i]] or ["#"])
    return text[:i] + c + text[i + 1:]


def edit(a, b):
    d = list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        prev, d[0] = d[0], i
        for j in range(1, len(b) + 1):
            cur = d[j]
            d[j] = min(d[j] + 1, d[j - 1] + 1, prev + (a[i - 1] != b[j - 1]))
            prev = cur
    return d[len(b)]


def read_line(model, img, lp=None):
    """-> (text without spaces, lowest character confidence x 100, P(text | line))"""
    lp = model.logp(img) if lp is None else lp
    rd = greedy(lp)
    text = "".join(c for c, _ in rd)
    q = min([100 * cf for c, cf in rd if c != " "], default=0.0)
    return text.replace(" ", ""), q, line_posterior(lp, text)


def safe_score(model, img, lp, line_conf_on, gamma, agree):
    """The line score the engine gives a free read under these settings."""
    pred, q, post = read_line(model, img, lp)
    score = min(q, line_conf(post, gamma)) if line_conf_on else q
    if agree is not None and score > 0:
        pred2, q2, post2 = read_line(model, shifted(img)) if agree == "shift" else read_line(agree, img)
        if pred2 != pred:
            return pred, 0.0
        score = min(score, min(q2, line_conf(post2, gamma)) if line_conf_on else q2)
    return pred, score


def evaluate(model, items, min_q=60.0, seed=5, line_conf_on=False, gamma=GAMMA, agree=None, fonts=None, every=0):
    rng = random.Random(seed)
    erng = random.Random(seed + 1)     # its own draws: the default columns stay as they were
    r = dict(lines=0, chars=0, errs=0, exact=0, wrong=0, wrong_conf=0, wrong_same=0, right_unsure=0, wrong_q=[],
             chk=0, chk_pass=0, mut=0, mut_acc=0, look=0, look_acc=0,
             every_look=0, every_look_acc=0, every_rand=0, every_rand_acc=0)
    idx = {c: i + 1 for i, c in enumerate(st.CHARSET)}
    for p, text in items:
        img = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
        lp = model.logp(img)
        pred, q = safe_score(model, img, lp, line_conf_on, gamma, agree)
        truth = text.replace(" ", "")
        e = edit(pred, truth)
        r["lines"] += 1
        r["chars"] += len(truth)
        r["errs"] += min(e, len(truth))
        if e == 0:
            r["exact"] += 1
            r["right_unsure"] += q < min_q
        else:
            r["wrong"] += 1
            r["wrong_q"].append(q)
            r["wrong_conf"] += q >= min_q
            r["wrong_same"] += q >= min_q and glyph_identical((fonts or {}).get(os.path.basename(p), ""), truth, pred)
        r["chk"] += 1
        r["chk_pass"] += check_pass(lp, text, min_q)
        for key, look in (("mut", False), ("look", True)):
            m = mutate(text, rng, look)
            if m is not None:
                r[key] += 1
                r[key + "_acc"] += check_pass(lp, m, min_q)
        if every:
            changes = every_change(text, erng, every)
            if changes:
                labs = np.array([[idx.get(c, -1) for c in m if c != " "] for _, m in changes], np.int64)
                acc = force_scores_batch(lp, labs).min(1) >= min_q
                for (key, _), a in zip(changes, acc):
                    r[key] += 1
                    r[key + "_acc"] += int(a)
    return r


def row(name, r, every=0):
    wq = r["wrong_q"]
    return (f"| {name} | {r['lines']} | {100 * (1 - r['errs'] / max(1, r['chars'])):.2f}% | "
            f"{100 * r['exact'] / max(1, r['lines']):.1f}% | {r['wrong_conf']} / {r['wrong']} | {r['wrong_same']} | "
            f"{r['exact'] - r['right_unsure']} | "
            f"{(np.median(wq) if wq else 0):.0f} / {(max(wq) if wq else 0):.0f} | {r['right_unsure']} | "
            f"{100 * r['chk_pass'] / max(1, r['chk']):.1f}% | {r['mut_acc']} / {r['mut']} | {r['look_acc']} / {r['look']} |"
            + (f" {r['every_look_acc']} / {r['every_look']} | {r['every_rand_acc']} / {r['every_rand']} |" if every else ""))


HEADER = ("| Set | Lines | Characters | Lines exact | Wrong reads scoring >= min | of them glyph-identical "
          "| Right reads >= min | Wrong-read score median / max "
          "| Right reads below min | Check: true text passes | Check: random change accepted | Check: look-alike accepted |\n"
          "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")


def fit_gamma(model, sets, min_q, agree):
    """The largest gamma (most right reads kept) at which no decidable wrong read scores >= min_q."""
    rows = []
    for name, items, fonts in sets:
        for p, text in items:
            img = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
            lp = model.logp(img)
            pred, q, post = read_line(model, img, lp)
            ok2, q2, post2 = True, 100.0, 1.0
            if agree is not None:
                pred2, q2, post2 = read_line(model, shifted(img)) if agree == "shift" else read_line(agree, img)
                ok2 = pred2 == pred
            truth = text.replace(" ", "")
            same = pred != truth and glyph_identical(fonts.get(os.path.basename(p), ""), truth, pred)
            rows.append((name, pred == truth, same, ok2, min(q, q2), min(post, post2)))
    print("| gamma | " + " | ".join(f"{n}: decidable wrong / identical / right >= min" for n, _, _ in sets) + " |")
    best = None
    for g in [0.5, 0.6, 0.7, 0.75, 0.8, 0.85, 0.9, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 6.0]:
        cells, bad = [], 0
        for n, _, _ in sets:
            sel = [x for x in rows if x[0] == n]
            sc = lambda x: min(x[4], line_conf(x[5], g)) if x[3] else 0.0
            w = sum(1 for x in sel if not x[1] and not x[2] and sc(x) >= min_q)
            i = sum(1 for x in sel if x[2] and sc(x) >= min_q)
            k = sum(1 for x in sel if x[1] and sc(x) >= min_q)
            bad += w
            cells.append(f"{w} / {i} / {k}")
        print(f"| {g} | " + " | ".join(cells) + " |")
        if bad == 0:
            best = g
    print("\nfitted gamma:", best)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--make", default="", help="write synthetic test sets into this folder and stop")
    ap.add_argument("--kinds", default=",".join(st.KINDS))
    ap.add_argument("--n", type=int, default=400)
    ap.add_argument("--seed", type=int, default=900)
    ap.add_argument("--model", default="models/ocrnet.onnx")
    ap.add_argument("--sets", default="", help="folder made by --make: every print type in it")
    ap.add_argument("--set", action="append", default=[], help="name=<folder>=<labels.tsv>")
    ap.add_argument("--temp", type=float, default=1.0)
    ap.add_argument("--min-quality", type=float, default=60.0)
    ap.add_argument("--line-conf", action="store_true", help="cap the line score by the calibrated line confidence")
    ap.add_argument("--gamma", type=float, default=GAMMA)
    ap.add_argument("--agree", default="", help="second reader: shift, or a second .onnx")
    ap.add_argument("--fit-gamma", action="store_true")
    ap.add_argument("--every", type=int, default=0, help="also every look-alike and N random characters at every place")
    a = ap.parse_args()
    if a.make:
        make_sets(a.make, a.n, a.seed, a.kinds.split(","))
        return
    specs = []
    if a.sets:
        for k in a.kinds.split(","):
            d = os.path.join(a.sets, k)
            if os.path.isdir(d):
                specs.append(f"{k}={d}={d}/labels.tsv")
    specs += a.set
    model = Model(a.model, a.temp)
    agree = None if not a.agree else "shift" if a.agree == "shift" else Model(a.agree, a.temp)
    sets = [(*load_set(s), load_fonts(s.split("=")[1])) for s in specs]
    if a.fit_gamma:
        fit_gamma(model, sets, a.min_quality, agree)
        return
    print(f"model {a.model}, temperature {a.temp}, min quality {a.min_quality}, "
          f"line confidence {'gamma ' + str(a.gamma) if a.line_conf else 'off'}, second reader {a.agree or 'none'}\n")
    head = HEADER
    if a.every:
        h1, h2 = HEADER.split("\n")
        head = (h1 + f" Every look-alike at every place accepted | {a.every} random at every place accepted |\n"
                + h2 + " ---: | ---: |")
    print(head)
    for name, items, fonts in sets:
        print(row(name, evaluate(model, items, a.min_quality, line_conf_on=a.line_conf, gamma=a.gamma,
                                 agree=agree, fonts=fonts, every=a.every), a.every), flush=True)


if __name__ == "__main__":
    main()
