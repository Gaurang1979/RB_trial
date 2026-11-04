"""Prototype: generative decoding of a GS1-128 SSCC from a fused profile (band1d.profile), for
blur, motion and small modules where the bar edges are gone (2026-10-27).

The SSCC's layout is fixed: start C, FNC1, 10 digit pairs, check, stop = 156 modules. So:
1. geometry and blur from the parts every SSCC shares (start + FNC1, stop): offset, module size,
   PSF (Gaussian sigma and motion length) by least squares on those pixels;
2. every symbol position scored against all 100 digit-pair values (and the check against all
   103), each rendered through the PSF with its fixed context (a symbol always ends with a space
   and starts with a bar), levels fitted per window;
3. dynamic programming over the 10 positions for the best sequence whose mod-103 check and GS1
   mod-10 check digit both hold; accepted only with a clear margin over the best other valid one.
"""
import sys, os
import numpy as np, cv2
sys.path.insert(0, os.path.dirname(__file__))
from synth1d import C128, STOP, START_C, FNC1

NMOD = 156
SIGMAS = (0.5, 1.0, 1.6, 2.3, 3.0, 3.8, 4.8)   # px; the last of each is the grid's edge
MOTIONS = (0, 3, 6, 9, 12, 16)
MAX_CHANGED = 0                                  # the check digits confirm the per-position best, never change it (every wrong read at the blur limit was one change)


def bars_of(values, with_stop=True):
    """module darkness (1 = bar) of a symbol value sequence"""
    m = []
    for v in values:
        for i, w in enumerate(C128[v]): m += [1 - i % 2] * w
    if with_stop:
        for i, w in enumerate(STOP): m += [1 - i % 2] * w
    return np.array(m, np.float32)


def psf_kernel(sigma, motion, step):
    """PSF sampled every `step` px: Gaussian sigma (px) convolved with a box of `motion` px"""
    r = int(np.ceil((3 * sigma + motion / 2 + 1) / step))
    x = np.arange(-r, r + 1) * step
    g = np.exp(-0.5 * (x / max(sigma, 1e-3)) ** 2)
    if motion > step:
        box = (np.abs(x) <= motion / 2).astype(np.float32)
        g = np.convolve(g, box, 'same')
    return (g / g.sum()).astype(np.float32)


def render(mods, x0, m, pos, kern):
    """darkness of a module string starting at profile position x0 (samples), m samples per
    module, at sample positions pos, blurred by kern (sampled at 1 sample)"""
    # rendered over the window widened by the kernel radius, so the blur at the window's ends
    # sees the modules beyond it (pos is a run of consecutive samples)
    r = len(kern) // 2
    ext = np.arange(pos[0] - r, pos[-1] + r + 1, dtype=np.float32)
    idx = np.floor((ext - x0) / m).astype(int)
    inside = (idx >= 0) & (idx < len(mods))
    d = np.zeros(len(ext), np.float32)
    d[inside] = mods[idx[inside]]
    return np.convolve(d, kern, 'valid')


def fit_levels(model, obs):
    """least squares obs ~ a + b*model; residual sum of squares and b"""
    A = np.stack([np.ones_like(model), model], 1)
    coef, *_ = np.linalg.lstsq(A, obs, rcond=None)
    r = obs - A @ coef
    return float(r @ r), coef[1]


def code_extent(p):
    """first and last strong edge of the profile (samples)"""
    g = np.abs(np.gradient(cv2.GaussianBlur(p.reshape(1, -1).astype(np.float32), (0, 0), 2).ravel()))
    thr = 0.25 * np.percentile(g, 99.5)
    # edges: local maxima of the gradient above the threshold
    pk = np.nonzero((g[1:-1] > thr) & (g[1:-1] >= g[:-2]) & (g[1:-1] >= g[2:]))[0] + 1
    if len(pk) < 10: return (0, len(p) - 1)
    # the code is the longest run of edges without a quiet-zone-sized gap (a code's widest
    # element is 4 modules, its quiet zone 10): split at gaps over 5 typical edge spacings
    gaps = np.diff(pk)
    cut = 5 * np.median(gaps)
    runs, start = [], 0
    for i, gp in enumerate(gaps):
        if gp > cut: runs.append((start, i)); start = i + 1
    runs.append((start, len(pk) - 1))
    a, b = max(runs, key=lambda r: r[1] - r[0])
    return pk[a], pk[b]


def geometry(p, sub, m_prior=None):
    """offset, samples per module, sigma and motion (px), by matched filters: the start + FNC1
    template and the stop template slide along the profile; the module size is the one at which
    both match 143 modules apart. Returns (score, x0, m, sigma, motion) or None."""
    a, b = code_extent(p)
    m_guess = m_prior / 1.17 if m_prior else (b - a) / NMOD
    prof = p.astype(np.float32)[None, :]
    head = np.concatenate([[0, 0], bars_of([START_C, FNC1], with_stop=False)])      # 2 quiet modules first
    tail = np.concatenate([bars_of([], with_stop=True), [0, 0]])
    best = None
    for m in m_guess * np.linspace(0.8, 1.2, 41):
        for sig in SIGMAS:
            for mot in MOTIONS:
                k = psf_kernel(sig * sub, mot * sub, 1.0)
                th = (1 - render(head, 0.0, m, np.arange(0, int(len(head) * m), dtype=np.float32), k)).astype(np.float32)[None, :]
                tt = (1 - render(tail, 0.0, m, np.arange(0, int(len(tail) * m), dtype=np.float32), k)).astype(np.float32)[None, :]
                if th.shape[1] >= prof.shape[1] or tt.shape[1] >= prof.shape[1]: continue
                ch = cv2.matchTemplate(prof, th, cv2.TM_CCOEFF_NORMED)[0]                # at x: head starts at x
                ct = cv2.matchTemplate(prof, tt, cv2.TM_CCOEFF_NORMED)[0]
                # the code starts 2 modules after the head template; the stop starts 143 modules later
                shift = int(round(143 * m + 2 * m))
                if shift >= len(ct): continue
                n = min(len(ch), len(ct) - shift)
                if n <= 0: continue
                tot = ch[:n] + ct[shift:shift + n]
                xh = int(np.argmax(tot))
                sc = float(tot[xh])
                if best is None or sc > best[0]: best = (sc, xh + 2 * m, m, sig, mot)
    return best


def decode_sscc(p, sub=4, known_prefix=None, m_prior=None):
    """returns (text '00' + 18 digits, margin) or (None, why)"""
    g = geometry(p, sub, m_prior)
    if g is None: return None, 'no geometry'
    gscore, x0, m, sig, mot = g
    # a blur at the edge of the grid is a blur the model cannot explain: no read
    if sig >= SIGMAS[-1] or mot >= MOTIONS[-1]: return None, f'blur beyond the model (sigma {sig}, motion {mot})'
    if gscore < 1.6: return None, f'fixed parts do not match ({gscore:.2f} of 2)'
    k = psf_kernel(sig * sub, mot * sub, 1.0)
    pos = np.arange(len(p), dtype=np.float32)
    noise = np.var(np.diff(p)) / 2 + 1e-3
    # per symbol position (0..10: 10 data, 1 check), cost of every value. Context: the module
    # before a symbol is light, the one after dark (or the stop's first bar)
    cost = np.full((11, 103), np.inf)
    for j in range(11):
        sx = x0 + (22 + 11 * j) * m
        w = (pos >= sx - 1.0 * m) & (pos < sx + 12 * m)
        if w.sum() < 8: return None, 'code outside the profile'
        for v in range(103 if j == 10 else 100):
            mods = np.concatenate([[0], bars_of([v], with_stop=False), [1]])
            r, b = fit_levels(render(mods, sx - m, m, pos[w], k), p[w])
            cost[j, v] = r / (2 * noise) if b < 0 else np.inf
    # known content (the product's extension + company prefix): fixed positions
    fixed = {0: 0}                                          # the AI: always 00 for an SSCC
    if known_prefix:
        for j in range(len(known_prefix) // 2):           # known_prefix starts with '00'
            fixed[j] = int(known_prefix[2 * j:2 * j + 2])
    # DP over data positions: state (sum mod 103 of weighted values, GS1 weighted digit sum mod 10)
    # GS1 check digit: the 18 digits after AI 00: d1..d17 weights 3,1,3,... from the left, d18 check.
    # Pair j (0..9) holds characters 2j, 2j+1 of '00' + 18 digits, i.e. digits index 2j-2, 2j-1.
    INF = np.inf
    dp = np.full((103, 10), INF); dp[(START_C + FNC1) % 103, 0] = 0.0
    back = []
    for j in range(10):
        nd = np.full((103, 10), INF); bk = np.full((103, 10, 2), -1, int)
        vals = [fixed[j]] if j in fixed else range(100)
        for v in vals:
            c = cost[j, v]
            if not np.isfinite(c): continue
            hi, lo = divmod(v, 10)
            # digit weights (positions after '00'): digit i (0-based) weight 3 if i even else 1, i < 17
            add10 = 0
            for i, dg in ((2 * j - 2, hi), (2 * j - 1, lo)):
                if 0 <= i < 17: add10 += (3 if i % 2 == 0 else 1) * dg
            for s103 in range(103):
                row = dp[s103]
                if not np.isfinite(row).any(): continue
                n103 = (s103 + (j + 2) * v) % 103
                for s10 in np.nonzero(np.isfinite(row))[0]:
                    tot = row[s10] + c
                    n10 = (s10 + add10) % 10
                    if tot < nd[n103, n10]:
                        nd[n103, n10] = tot; bk[n103, n10] = (s103, s10 * 1000 + v)
        dp = nd; back.append(bk)
    # end: check symbol = sum mod 103; GS1 check digit = last digit (lo of pair 9) must close mod 10
    finals = []
    for s103 in range(103):
        for s10 in range(10):
            if not np.isfinite(dp[s103, s10]): continue
            finals.append((dp[s103, s10] + cost[10, s103], s103, s10))
    if not finals: return None, 'no path'
    finals.sort()

    def trace(s103, s10):
        vals = []
        for j in range(9, -1, -1):
            ps103, code = back[j][s103, s10]
            v = code % 1000; ps10 = code // 1000
            vals.append(v); s103, s10 = ps103, ps10
        return vals[::-1]

    valid = []
    for tot, s103, s10 in finals:
        vals = trace(s103, s10)
        digits = ''.join(f'{v:02d}' for v in vals)          # '00' + 18 digits
        if digits[:2] != '00': continue
        body = digits[2:]
        chk = sum(int(d) * (3 if i % 2 == 0 else 1) for i, d in enumerate(body[:17]))
        if (chk + int(body[17])) % 10 != 0: continue
        valid.append((tot, digits))
        if len(valid) == 2: break
    if len(valid) < 2: return None, 'no competitor to measure the margin against'
    margin = valid[1][0] - valid[0][0]
    # the check digits confirm, they do not choose: the decoded symbols may differ from each
    # position's own best fit in at most MAX_CHANGED places (two checks over 18 digits leave many
    # valid sequences; under a wrong model the best valid one is a wrong code)
    best_vals = [int(valid[0][1][2 * j:2 * j + 2]) for j in range(10)]
    data_best = [fixed[j] if j in fixed else int(np.argmin(cost[j, :100])) for j in range(10)]
    s103 = (START_C + FNC1 + sum((j + 2) * v for j, v in enumerate(best_vals))) % 103
    changed = sum(a != b for a, b in zip(best_vals, data_best)) + int(np.argmin(cost[10]) != s103)
    if changed > MAX_CHANGED: return None, f'{changed} symbols changed by the check digits'
    # how sure each decoded data symbol is: its cost gap to the position's next best value
    gaps = []
    for j in range(10):
        if j in fixed: continue
        c = cost[j, :100]; v = best_vals[j]
        others = np.delete(c, v)
        gaps.append(float(others.min() - c[v]))
    decode_sscc.last = dict(sigma=sig, motion=mot, changed=changed, gap_min=min(gaps) if gaps else np.inf,
                            gap_sorted=sorted(gaps)[:3], margin=margin, gscore=gscore)
    return valid[0][1], margin


if __name__ == '__main__':
    sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
    import synth1d as s
    from band1d import bands, profile, read_band_zxing
    rng = np.random.default_rng(5)
    for key, levels in (('defocus', [1.8, 2.4, 3.0, 4.0, 5.0]), ('motion', [5, 7, 9, 12, 15]), ('X', [1.0, 1.2, 1.5, 1.8]), ('dark', [185, 200, 210]), ('noise', [20, 28, 40]), ('scratches', [6, 9]), ('glare', [140, 180]), ('tilt', [30, 60])):
        res = []
        for lv in levels:
            ok = wrong = 0; margins = []
            for _ in range(12):
                text = s.sscc(rng); img, _ = s.frame(s.gs1_128_widths(text), rng, **{key: lv})
                X = lv if key == 'X' else 3.0
                bs = bands(img, X)
                if not bs: continue
                p = profile(img, bs[0], X)
                mp = bs[0][2] * 4 / NMOD
                got, mg = decode_sscc(p, m_prior=mp)
                if got is None:
                    got, mg = decode_sscc(p[::-1], m_prior=mp)
                if got == text: ok += 1; margins.append(mg)
                elif got: wrong += 1; margins.append(-mg)
            res.append(f'{lv}: {ok}/12' + (f' WRONG {wrong}' if wrong else '') + (f' margin min {min(margins):.0f}' if margins else ''))
        print(f'{key:8s} ' + ' | '.join(res), flush=True)
