"""False-read checks for gen1d.decode_sscc: no code, a non-SSCC Code 128, a code cut by the frame
edge, and two codes side by side. Every one must give nothing (or the right text)."""
import sys, os, numpy as np, cv2
sys.path.insert(0, os.path.dirname(__file__))
import synth1d as s
from band1d import bands, profile
from gen1d import decode_sscc, NMOD, bars_of, C128, STOP

def code128_b(text):
    vals = [104] + [ord(c) - 32 for c in text]
    chk = (vals[0] + sum(i * v for i, v in enumerate(vals[1:], 1))) % 103
    w = []
    for v in vals + [chk]: w += C128[v]
    return w + list(STOP)

def run(img, X=3.0):
    out = []
    for b in bands(img, X):
        p = profile(img, b, X); mp = b[2] * 4 / NMOD
        for q in (p, p[::-1]):
            t, mg = decode_sscc(q, m_prior=mp)
            if t: out.append(t)
    return out

rng = np.random.default_rng(11)
bad = 0
for trial in range(40):
    kind = trial % 4
    if kind == 0:   # carton only
        img, _ = s.frame([1], rng, defocus=rng.uniform(0.6, 3)); img[:] = cv2.GaussianBlur(rng.normal(120, 18, img.shape).astype(np.float32), (0, 0), 3).clip(0, 255).astype(np.uint8)
        truth = None
    elif kind == 1:  # a Code 128 that is not an SSCC, about the same length
        txt = ''.join(chr(rng.integers(65, 91)) for _ in range(11))
        img, _ = s.frame(code128_b(txt), rng, defocus=rng.uniform(0.6, 3)); truth = None
    elif kind == 2:  # an SSCC cut by the frame edge (only its right 60% visible)
        t = s.sscc(rng); img, q = s.frame(s.gs1_128_widths(t), rng, defocus=rng.uniform(0.6, 2.5))
        x = int(q[:, 0].min() + 0.4 * (q[:, 0].max() - q[:, 0].min())); img[:, :x] = 120; truth = None
    else:            # two SSCCs: only the true texts may come back
        t1 = s.sscc(rng); img, q = s.frame(s.gs1_128_widths(t1), rng, defocus=rng.uniform(0.6, 2.5)); truth = {t1}
    got = run(img)
    wrong = [g for g in got if truth is None or g not in truth]
    if wrong: bad += 1; print('kind', kind, 'WRONG', wrong)
print('false reads:', bad, 'of 40 frames')
