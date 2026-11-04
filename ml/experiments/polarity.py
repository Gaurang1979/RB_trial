"""Light-on-dark codes (laser-marked caps, white ink on dark labels), 2026-10-29: the same degraded
codes printed dark-on-light and light-on-dark, in scenes of six; the gap between the two is what the
engine's own decoders (lattice, grid fit, known content, match, soft decoding) lose on inverted print.

    python polarity.py [n_scenes]
"""
import sys, os
import numpy as np, cv2, zxingcpp
sys.path.insert(0, os.path.dirname(__file__)); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
import engine


def modules(text, fmt):
    b = zxingcpp.create_barcode(text, fmt)
    img = np.array(zxingcpp.write_barcode_to_image(b, scale=1, add_hrt=False, add_quiet_zones=False))
    return img < 128

M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')


def scene(texts, fmt, rng, px=4.0, blur=0.6, ink=40, paper=210, noise=4.0, invert=False):
    img = np.full((1200, 1600), 110, np.float32) + cv2.GaussianBlur(rng.normal(0, 25, (1200, 1600)).astype(np.float32), (0, 0), 4)
    for k, t in enumerate(texts):
        m = modules(t, fmt)
        qz = 4
        hi = np.kron(np.pad(m, qz).astype(np.float32), np.ones((8, 8), np.float32))
        lab = cv2.resize(hi, (int(hi.shape[1] * px / 8), int(hi.shape[0] * px / 8)), interpolation=cv2.INTER_AREA)
        dark, light = (paper, ink) if invert else (ink, paper)   # inverted: light modules on dark ground
        lab = light + (dark - light) * lab
        y, x = 150 + (k // 3) * 500, 150 + (k % 3) * 480
        img[y:y + lab.shape[0], x:x + lab.shape[1]] = lab
    if blur > 0: img = cv2.GaussianBlur(img, (0, 0), blur)
    img += rng.normal(0, noise, img.shape)
    return np.clip(img, 0, 255).astype(np.uint8)


LEVELS = [('plain', {}), ('blur 1.6', {'blur': 1.6}), ('blur 2.2', {'blur': 2.2}), ('contrast 60', {'ink': 150}),
          ('modules 2.5 px', {'px': 2.5}), ('modules 2 px', {'px': 2.0, 'blur': 0.8})]


def main(n=3):
    F = zxingcpp.BarcodeFormat
    for name, fmt, cr in (('Data Matrix', F.DataMatrix, 2), ('QR', F.QRCode, 1)):
        print(f'\n{name}: codes read ({6 * n} per cell), dark-on-light vs light-on-dark')
        for label, kw in LEVELS:
            row = []
            for inv in (False, True):
                rng = np.random.default_rng(31)                    # the same codes and noise both ways
                ok = wrong = 0
                for _ in range(n):
                    texts = [f'CLUK A 0607{rng.integers(10000, 99999)}' for _ in range(6)]
                    img = scene(texts, fmt, rng, invert=inv, **kw)
                    codes, _ = engine.decode(img, fmt=cr, expected=6, budget_ms=500, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
                    got = [c[0] for c in codes]
                    ok += sum(t in got for t in texts); wrong += sum(g not in texts for g in got)
                row.append(f'{ok}/{6 * n}' + (f' WRONG {wrong}' if wrong else ''))
            print(f'  {label:15s} normal {row[0]:>8s}   inverted {row[1]:>8s}', flush=True)


if __name__ == '__main__':
    main(int(sys.argv[1]) if len(sys.argv) > 1 else 3)
