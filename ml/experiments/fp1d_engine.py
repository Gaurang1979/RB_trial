"""False-read checks through the engine (cr_decode, GS1-128 + EAN-13): no code, a non-SSCC Code 128,
an SSCC cut by the frame edge, a real SSCC. Every frame must give nothing or the right text."""
import sys, os, numpy as np, cv2
sys.path.insert(0, os.path.dirname(__file__)); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
import synth1d as s, engine
from fp1d import code128_b
rng = np.random.default_rng(17)
bad = 0; right = 0
for trial in range(80):
    kind = trial % 4
    blur = rng.uniform(0.6, 3.2)
    if kind == 0:
        img, _ = s.frame([1], rng); img[:] = cv2.GaussianBlur(rng.normal(120, 18, img.shape).astype(np.float32), (0, 0), 3).clip(0, 255).astype(np.uint8); truth = set()
    elif kind == 1:
        txt = ''.join(chr(rng.integers(65, 91)) for _ in range(11))
        img, _ = s.frame(code128_b(txt), rng, defocus=blur, tilt=rng.uniform(0, 60)); truth = {txt}
    elif kind == 2:
        t = s.sscc(rng); img, q = s.frame(s.gs1_128_widths(t), rng, defocus=blur, tilt=rng.uniform(0, 60))
        x = int(q[:, 0].min() + 0.4 * (q[:, 0].max() - q[:, 0].min())); img[:, :x] = 120; truth = set()
    else:
        t = s.sscc(rng); img, _ = s.frame(s.gs1_128_widths(t), rng, defocus=blur, motion=rng.uniform(0, 9), tilt=rng.uniform(0, 60)); truth = {t}
    codes, _ = engine.decode(img, fmt=4 | 8, expected=1, budget_ms=300)
    got = [c[0] for c in codes]
    wrong = [g for g in got if g not in truth]
    right += sum(g in truth for g in got)
    if wrong: bad += 1; print('kind', kind, 'blur %.1f' % blur, 'WRONG', wrong, 'truth', truth)
print('frames with a false read:', bad, 'of 80; right reads', right)
