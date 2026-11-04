"""Dot-printed codes (inkjet on caps: each dark module a round dot with gaps between dots) and
print voids, for NI Vision's "cell filter" and dot-print modes (2026-10-29).

Draws QR and Data Matrix codes with every dark module as a round dot of `dot` x the module, the dots
jittered, plus voids (white specks inside dark modules) and spots, then blur and noise; `sweep`
measures the engine per level.

    python dots.py sweep [n]
"""
import sys, os, time
import numpy as np, cv2, zxingcpp
sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))


def modules(text, fmt):
    b = zxingcpp.create_barcode(text, fmt)
    img = np.array(zxingcpp.write_barcode_to_image(b, scale=1, add_hrt=False, add_quiet_zones=False))
    return img < 128


def scene(texts, fmt, rng, **kw):
    """several codes on a textured grey 1600 x 1200 field (a tray of caps), as the line sees them"""
    img = np.full((1200, 1600), 110, np.float32) + cv2.GaussianBlur(rng.normal(0, 25, (1200, 1600)).astype(np.float32), (0, 0), 4)
    for k, t in enumerate(texts):
        c = draw(modules(t, fmt), rng, **kw)[60:-60, 60:-60].astype(np.float32)
        y, x = 150 + (k // 3) * 500, 150 + (k % 3) * 480
        img[y:y + c.shape[0], x:x + c.shape[1]] = c
    return np.clip(img, 0, 255).astype(np.uint8)


def draw(mods, rng, px=6.0, dot=0.7, jitter=0.08, voids=0.0, spots=0.0, blur=0.6, noise=4.0, dark=40, light=210):
    n, m = mods.shape
    qz = 4
    S = 8                                                       # drawn 8x finer, averaged down
    H, W = int((n + 2 * qz) * px * S), int((m + 2 * qz) * px * S)
    hi = np.full((H, W), light, np.float32)
    r = dot * px * S / 2
    for y in range(n):
        for x in range(m):
            if not mods[y, x]: continue
            cx = (x + qz + 0.5 + rng.normal(0, jitter)) * px * S
            cy = (y + qz + 0.5 + rng.normal(0, jitter)) * px * S
            cv2.circle(hi, (int(cx), int(cy)), int(r), dark, -1, lineType=cv2.LINE_AA)
            if voids > 0 and rng.random() < voids:                  # a void: a light speck in the dot
                cv2.circle(hi, (int(cx + rng.normal(0, r / 3)), int(cy + rng.normal(0, r / 3))), int(r * 0.45), light, -1)
    if spots > 0:                                                # dark specks on the light
        for _ in range(int(spots * n * m)):
            cv2.circle(hi, (int(rng.uniform(0, W)), int(rng.uniform(0, H))), int(px * S * 0.25), dark, -1)
    img = cv2.resize(hi, (W // S, H // S), interpolation=cv2.INTER_AREA)
    if blur > 0: img = cv2.GaussianBlur(img, (0, 0), blur)
    img += rng.normal(0, noise, img.shape)
    # on a bigger grey background, as a cap on a case
    out = np.full((img.shape[0] + 120, img.shape[1] + 120), 120, np.float32)
    out[60:60 + img.shape[0], 60:60 + img.shape[1]] = img
    return np.clip(out, 0, 255).astype(np.uint8)




LEVELS = {
    'dot size': ('dot', [0.8, 0.6, 0.5, 0.4]),
    'voids':    ('voids', [0.3, 0.6, 0.9]),
    'spots':    ('spots', [0.1, 0.2, 0.4]),
    'module px': ('px', [3.0, 4.0]),
    'jitter':   ('jitter', [0.15, 0.25]),
}


def sweep(n=4, budget=500):
    """n scenes of 6 codes per level, through the engine at `budget` ms"""
    import engine
    M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')
    rng = np.random.default_rng(9)
    F = zxingcpp.BarcodeFormat
    for name, fmt, cr in (('Data Matrix', F.DataMatrix, 2), ('QR', F.QRCode, 1)):
        print(f'\n{name}: codes read per level ({6 * n} each; others: 6 px modules, dot 0.7, jitter 0.08; {budget} ms)')
        for label, (key, levels) in LEVELS.items():
            out = []
            for lv in levels:
                ok = wrong = 0; ms = []
                for _ in range(n):
                    texts = [f'CLUK A 0607{rng.integers(10000, 99999)}' for _ in range(6)]
                    kw = {'dot': 0.7, key: lv}
                    img = scene(texts, fmt, rng, **kw)
                    codes, st = engine.decode(img, fmt=cr, expected=6, budget_ms=budget, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
                    ms.append(st.total_ms)
                    got = [c[0] for c in codes]
                    ok += sum(t in got for t in texts); wrong += sum(g not in texts for g in got)
                out.append(f'{lv}: {ok}/{6 * n}' + (f' WRONG {wrong}' if wrong else '') + f' {np.median(ms):.0f}ms')
            print(f'  {label:10s} ' + ' | '.join(out), flush=True)


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == 'sweep':
        sweep(int(sys.argv[2]) if len(sys.argv) > 2 else 4)
