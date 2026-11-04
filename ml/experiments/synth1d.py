"""Synthetic side-camera frames of 1D case codes (no real 1D images exist yet, 2026-10-27).

GS1-128 SSCC (FNC1, AI 00, 18 digits, own Code 128 encoder so the FNC1 is right) and EAN-13 (zxing
writer), drawn on a white label on a carton at a module size of X px, then degraded one way at a
time: defocus, motion blur across the bars, low contrast, noise, tilt, print growth, scratches,
a glare band. `sweep` measures the engine (cr_decode, one expected code) per degradation level.

    python synth1d.py sweep [n_per_level]
"""
import sys, os, time
import numpy as np, cv2
sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))

# Code 128 bar/space widths for values 0..105 (derived from zxing-cpp renders, checked: 106
# distinct patterns of 11 modules with an even bar sum); stop is 13 modules
C128 = [(2, 1, 2, 2, 2, 2), (2, 2, 2, 1, 2, 2), (2, 2, 2, 2, 2, 1), (1, 2, 1, 2, 2, 3), (1, 2, 1, 3, 2, 2), (1, 3, 1, 2, 2, 2), (1, 2, 2, 2, 1, 3), (1, 2, 2, 3, 1, 2), (1, 3, 2, 2, 1, 2), (2, 2, 1, 2, 1, 3), (2, 2, 1, 3, 1, 2), (2, 3, 1, 2, 1, 2), (1, 1, 2, 2, 3, 2), (1, 2, 2, 1, 3, 2), (1, 2, 2, 2, 3, 1), (1, 1, 3, 2, 2, 2), (1, 2, 3, 1, 2, 2), (1, 2, 3, 2, 2, 1), (2, 2, 3, 2, 1, 1), (2, 2, 1, 1, 3, 2), (2, 2, 1, 2, 3, 1), (2, 1, 3, 2, 1, 2), (2, 2, 3, 1, 1, 2), (3, 1, 2, 1, 3, 1), (3, 1, 1, 2, 2, 2), (3, 2, 1, 1, 2, 2), (3, 2, 1, 2, 2, 1), (3, 1, 2, 2, 1, 2), (3, 2, 2, 1, 1, 2), (3, 2, 2, 2, 1, 1), (2, 1, 2, 1, 2, 3), (2, 1, 2, 3, 2, 1), (2, 3, 2, 1, 2, 1), (1, 1, 1, 3, 2, 3), (1, 3, 1, 1, 2, 3), (1, 3, 1, 3, 2, 1), (1, 1, 2, 3, 1, 3), (1, 3, 2, 1, 1, 3), (1, 3, 2, 3, 1, 1), (2, 1, 1, 3, 1, 3), (2, 3, 1, 1, 1, 3), (2, 3, 1, 3, 1, 1), (1, 1, 2, 1, 3, 3), (1, 1, 2, 3, 3, 1), (1, 3, 2, 1, 3, 1), (1, 1, 3, 1, 2, 3), (1, 1, 3, 3, 2, 1), (1, 3, 3, 1, 2, 1), (3, 1, 3, 1, 2, 1), (2, 1, 1, 3, 3, 1), (2, 3, 1, 1, 3, 1), (2, 1, 3, 1, 1, 3), (2, 1, 3, 3, 1, 1), (2, 1, 3, 1, 3, 1), (3, 1, 1, 1, 2, 3), (3, 1, 1, 3, 2, 1), (3, 3, 1, 1, 2, 1), (3, 1, 2, 1, 1, 3), (3, 1, 2, 3, 1, 1), (3, 3, 2, 1, 1, 1), (3, 1, 4, 1, 1, 1), (2, 2, 1, 4, 1, 1), (4, 3, 1, 1, 1, 1), (1, 1, 1, 2, 2, 4), (1, 1, 1, 4, 2, 2), (1, 2, 1, 1, 2, 4), (1, 2, 1, 4, 2, 1), (1, 4, 1, 1, 2, 2), (1, 4, 1, 2, 2, 1), (1, 1, 2, 2, 1, 4), (1, 1, 2, 4, 1, 2), (1, 2, 2, 1, 1, 4), (1, 2, 2, 4, 1, 1), (1, 4, 2, 1, 1, 2), (1, 4, 2, 2, 1, 1), (2, 4, 1, 2, 1, 1), (2, 2, 1, 1, 1, 4), (4, 1, 3, 1, 1, 1), (2, 4, 1, 1, 1, 2), (1, 3, 4, 1, 1, 1), (1, 1, 1, 2, 4, 2), (1, 2, 1, 1, 4, 2), (1, 2, 1, 2, 4, 1), (1, 1, 4, 2, 1, 2), (1, 2, 4, 1, 1, 2), (1, 2, 4, 2, 1, 1), (4, 1, 1, 2, 1, 2), (4, 2, 1, 1, 1, 2), (4, 2, 1, 2, 1, 1), (2, 1, 2, 1, 4, 1), (2, 1, 4, 1, 2, 1), (4, 1, 2, 1, 2, 1), (1, 1, 1, 1, 4, 3), (1, 1, 1, 3, 4, 1), (1, 3, 1, 1, 4, 1), (1, 1, 4, 1, 1, 3), (1, 1, 4, 3, 1, 1), (4, 1, 1, 1, 1, 3), (4, 1, 1, 3, 1, 1), (1, 1, 3, 1, 4, 1), (1, 1, 4, 1, 3, 1), (3, 1, 1, 1, 4, 1), (4, 1, 1, 1, 3, 1), (2, 1, 1, 4, 1, 2), (2, 1, 1, 2, 1, 4), (2, 1, 1, 2, 3, 2)]
STOP = (2, 3, 3, 1, 1, 1, 2)
START_C, FNC1 = 105, 102


def gs1_check(digits):
    """GS1 mod-10 check digit of the digits before it"""
    s = sum(int(d) * (3 if i % 2 == 0 else 1) for i, d in enumerate(reversed(digits)))
    return str((10 - s % 10) % 10)


def sscc(rng):
    body = '3' + '76123450' + ''.join(str(d) for d in rng.integers(0, 10, 8))   # extension, prefix, serial
    return '00' + body + gs1_check(body)


def gs1_128_widths(ai_digits):
    """module widths (bar first) of FNC1 + an even-length digit string in code set C"""
    assert len(ai_digits) % 2 == 0 and ai_digits.isdigit()
    vals = [START_C, FNC1] + [int(ai_digits[i:i + 2]) for i in range(0, len(ai_digits), 2)]
    chk = (vals[0] + sum(i * v for i, v in enumerate(vals[1:], 1))) % 103
    w = []
    for v in vals + [chk]: w += C128[v]
    return w + list(STOP)


def ean13_widths(digits12):
    import zxingcpp
    text = digits12 + gs1_check(digits12)
    b = zxingcpp.create_barcode(text, zxingcpp.BarcodeFormat.EAN13)
    row = np.array(zxingcpp.write_barcode_to_image(b, scale=1, add_hrt=False, add_quiet_zones=False))[0] < 128
    w, c = [], 1
    for i in range(1, len(row)):
        if row[i] == row[i - 1]: c += 1
        else: w.append(c); c = 1
    w.append(c)
    return text, w


def render_row(widths, X, growth=0.0, sub=8):
    """one row of darkness (0..1) at sub-pixel resolution, X px per module, quiet zone 10 modules;
    dark bars widened by `growth` modules in total (ink spread)"""
    qz = 10
    total = sum(widths) + 2 * qz
    n = int(round(total * X * sub))
    row = np.zeros(n, np.float32)
    pos = qz
    for i, w in enumerate(widths):
        if i % 2 == 0:
            a = (pos - growth / 2) * X * sub; b = (pos + w + growth / 2) * X * sub
            row[max(0, int(round(a))):int(round(b))] = 1
        pos += w
    return cv2.resize(row[None], (int(round(total * X)), 1), interpolation=cv2.INTER_AREA)[0]


def frame(widths, rng, X=3.0, defocus=0.6, motion=0.0, dark=30, light=225, noise=3.0, tilt=0.0,
          growth=0.0, scratches=0, glare=0.0, bar_h_mod=40, size=(1920, 1080)):
    """a side-camera frame (uint8) with one code; returns (image, code quad TL,TR,BR,BL)"""
    row = render_row(widths, X, growth)
    h = int(bar_h_mod * X)
    code = np.repeat(row[None], h, 0)                           # darkness, bars vertical
    pad = int(6 * X)
    label = np.zeros((h + 2 * pad, code.shape[1]), np.float32); label[pad:pad + h] = code
    lab = light - (light - dark) * label                        # grey label
    # carton: brown-grey with texture
    W, H = size
    img = np.full((H, W), 120, np.float32) + cv2.GaussianBlur(rng.normal(0, 18, (H, W)).astype(np.float32), (0, 0), 3)
    for _ in range(scratches):                                  # light scratches across the bars
        y = rng.integers(pad, pad + h); x0 = rng.integers(0, lab.shape[1] // 2)
        cv2.line(lab, (int(x0), int(y)), (int(x0 + lab.shape[1] * rng.uniform(0.3, 0.6)), int(y + rng.integers(-3, 4))), light, max(1, int(X)))
    # place the label, rotated by tilt
    cx, cy = W / 2 + rng.uniform(-200, 200), H / 2 + rng.uniform(-150, 150)
    M = cv2.getRotationMatrix2D((lab.shape[1] / 2, lab.shape[0] / 2), tilt, 1.0)
    M[0, 2] += cx - lab.shape[1] / 2; M[1, 2] += cy - lab.shape[0] / 2
    mask = cv2.warpAffine(np.ones_like(lab), M, (W, H), flags=cv2.INTER_LINEAR)
    warped = cv2.warpAffine(lab, M, (W, H), flags=cv2.INTER_LINEAR)
    img = img * (1 - mask) + warped * mask
    if glare > 0:                                               # a bright band along the motion
        yy, xx = np.mgrid[0:H, 0:W]
        img += glare * np.exp(-((xx - cx - rng.uniform(-100, 100)) / 60.0) ** 2)
    if motion > 0:                                              # case moving across the bars (x)
        k = np.zeros((1, int(np.ceil(motion)) | 1), np.float32); k[0, :] = 1; k /= k.sum()
        img = cv2.filter2D(img, -1, k)
    if defocus > 0: img = cv2.GaussianBlur(img, (0, 0), defocus)
    img += rng.normal(0, noise, img.shape)
    q = np.float32([[pad * 0 + 10 * X, pad], [lab.shape[1] - 10 * X, pad], [lab.shape[1] - 10 * X, pad + h], [10 * X, pad + h]])
    q = cv2.transform(q[None], M)[0]
    return np.clip(img, 0, 255).astype(np.uint8), q


LEVELS = {
    'module px':  ('X', [1.2, 1.5, 1.8, 2.2, 3.0]),
    'defocus':    ('defocus', [0.6, 1.2, 1.8, 2.4, 3.0]),
    'motion':     ('motion', [0, 3, 5, 7, 9]),
    'contrast':   ('dark', [30, 120, 160, 185, 200]),
    'noise':      ('noise', [3, 8, 14, 20, 28]),
    'tilt':       ('tilt', [0, 10, 25, 45, 80]),
    'growth':     ('growth', [0.0, 0.2, 0.35, -0.2, -0.35]),
    'scratches':  ('scratches', [0, 2, 4, 6, 9]),
    'glare':      ('glare', [0, 60, 100, 140, 180]),
}


def sweep(n=20, kinds=('sscc', 'ean13')):
    import engine
    rng = np.random.default_rng(7)
    fmt = {'sscc': 4, 'ean13': 8}
    for kind in kinds:
        print(f'\n{kind}: engine read rate per level ({n} frames each, others mild: X 3, defocus 0.6, noise 3)')
        for name, (key, levels) in LEVELS.items():
            out = []
            for lv in levels:
                ok = wrong = 0; ms = []
                for _ in range(n):
                    if kind == 'sscc':
                        text = sscc(rng); w = gs1_128_widths(text)
                    else:
                        text, w = ean13_widths(''.join(str(d) for d in rng.integers(0, 10, 12)))
                    img, _ = frame(w, rng, **{key: lv})
                    t0 = time.time()
                    codes, _ = engine.decode(img, fmt=fmt[kind], expected=1, budget_ms=300)
                    ms.append(1000 * (time.time() - t0))
                    got = [c[0] for c in codes]
                    if any(g.endswith(text) or g == text for g in got): ok += 1
                    elif got: wrong += 1
                out.append(f'{lv}: {ok}/{n}' + (f' WRONG {wrong}' if wrong else '') + f' {np.median(ms):.0f}ms')
            print(f'  {name:10s} ' + ' | '.join(out))


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == 'sweep':
        sweep(int(sys.argv[2]) if len(sys.argv) > 2 else 20)
