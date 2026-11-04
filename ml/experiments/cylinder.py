"""Codes wrapped around a cylinder (bottle shoulder, cap edge), as NI Vision's unwrap treats them
(2026-10-29): a flat code laid on a cylinder of radius R with a vertical axis is seen squeezed
toward its sides, x = cx + R sin(s / R) for arc length s, and darker there (light falls off with
the angle). `wrap` = the code's width over R (radians around the cylinder); 0 = flat.

    python cylinder.py [n_scenes]
"""
import sys, os
import numpy as np, cv2, zxingcpp
sys.path.insert(0, os.path.dirname(__file__)); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
import engine

M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')


def modules(text, fmt):
    b = zxingcpp.create_barcode(text, fmt)
    img = np.array(zxingcpp.write_barcode_to_image(b, scale=1, add_hrt=False, add_quiet_zones=False))
    return img < 128


def wrapped(mods, px, wrap, shade=0.5):
    """the code (with a 4-module quiet zone) wrapped on a cylinder: grey image, light 210 dark 40"""
    qz = 4
    flat = np.kron(np.pad(mods, qz).astype(np.float32), np.ones((8, 8), np.float32))
    flat = cv2.resize(flat, (int(flat.shape[1] * px / 8), int(flat.shape[0] * px / 8)), interpolation=cv2.INTER_AREA)
    H, W = flat.shape
    if wrap <= 1e-3:
        out, ang = flat, np.zeros(W)
    else:
        R = W / wrap
        half = R * np.sin(wrap / 2)                                  # the seen half-width
        xs = np.arange(-int(half), int(half) + 1, dtype=np.float32)
        s = R * np.arcsin(np.clip(xs / R, -1, 1)) + W / 2            # arc position of each seen column
        mapx = np.tile(s, (H, 1)).astype(np.float32)
        mapy = np.tile(np.arange(H, dtype=np.float32)[:, None], (1, len(xs)))
        out = cv2.remap(flat, mapx, mapy, cv2.INTER_AREA if False else cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=0)
        ang = np.arcsin(np.clip(xs / R, -1, 1))
    img = 210 - 170 * out
    light = 1 - shade * (1 - np.cos(ang))                            # light falls off around the curve
    return img * light[None, :]


def scene(texts, fmt, rng, px=4.0, wrap=0.0, blur=0.7, noise=4.0):
    img = np.full((1200, 1600), 90, np.float32) + cv2.GaussianBlur(rng.normal(0, 20, (1200, 1600)).astype(np.float32), (0, 0), 4)
    for k, t in enumerate(texts):
        c = wrapped(modules(t, fmt), px, wrap)
        y, x = 150 + (k // 3) * 500, 150 + (k % 3) * 480
        img[y:y + c.shape[0], x:x + c.shape[1]] = c
    img = cv2.GaussianBlur(img, (0, 0), blur) + rng.normal(0, noise, img.shape)
    return np.clip(img, 0, 255).astype(np.uint8)


def main(n=3):
    F = zxingcpp.BarcodeFormat
    for name, fmt, cr in (('Data Matrix', F.DataMatrix, 2), ('QR', F.QRCode, 1)):
        print(f'\n{name}: codes read ({6 * n} per level), 4 px modules, by wrap angle (code width / radius)')
        row = []
        for wrap in (0.0, 0.8, 1.2, 1.6, 2.0, 2.4):
            rng = np.random.default_rng(5)
            ok = wrong = 0
            for _ in range(n):
                texts = [f'CLUK A 0607{rng.integers(10000, 99999)}' for _ in range(6)]
                img = scene(texts, fmt, rng, wrap=wrap)
                codes, _ = engine.decode(img, fmt=cr, expected=6, budget_ms=500, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
                got = [c[0] for c in codes]
                ok += sum(t in got for t in texts); wrong += sum(g not in texts for g in got)
            row.append(f'{wrap:.1f} rad ({np.degrees(wrap):.0f} deg): {ok}/{6 * n}' + (f' WRONG {wrong}' if wrong else ''))
        print('  ' + ' | '.join(row), flush=True)


if __name__ == '__main__':
    main(int(sys.argv[1]) if len(sys.argv) > 1 else 3)
