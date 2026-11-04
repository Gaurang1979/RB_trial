"""Quality-violation generator: real QR / Data Matrix symbols degraded with the defects that
the quality parameters measure (ISO/IEC 15415, 29158) plus the line's own problems, paired
with the clean symbol as the training target.

Each sample carries the list of violations applied, so training can be checked per violation.
The clean target is ideal: black modules, white everywhere else (including a clean quiet zone).

    SC   low symbol contrast          MOD  uneven module ink        RM   modules near threshold
    FPD  finder / L pattern damaged    UEC  module errors, scratches  AN   axial stretch
    GN   wrinkled / curved grid        PG   print growth / loss       QZ   quiet zone intrusion
    LIGHT uneven light, half shadow    GLARE saturated glare          BLUR defocus
    MOTION motion blur                 DARK under-exposure + noise    SMALL small modules (1.6-3 px)
"""

import random

import cv2
import numpy as np

from synth import _code_matrix

ALL = ["SC", "MOD", "RM", "FPD", "UEC", "AN", "GN", "PG", "QZ", "LIGHT", "GLARE", "BLUR", "MOTION", "DARK", "SMALL"]
SS = 4  # supersampling for geometry


def _finder_cells(fmt, n_w, n_h):
    """Module rectangles (x0, y0, x1, y1) of the fixed patterns, in module units."""
    if fmt == "qr":
        return [(0, 0, 7, 7), (n_w - 7, 0, n_w, 7), (0, n_h - 7, 7, n_h)]
    return [(0, 0, 1, n_h), (0, n_h - 1, n_w, n_h)]  # Data Matrix L


def sample(rng: random.Random, size=192, violations=None):
    """Returns (degraded uint8, clean float32 0/1, violations list, upscaled bool)."""
    if violations is None:
        k = rng.choice([1, 1, 2, 2, 3, 4])
        violations = rng.sample(ALL, k)
    v = set(violations)
    fmt = rng.choice(["qr", "dm"])
    mat = _code_matrix(rng, fmt).astype(np.float32) / 255.0  # 1 = light, includes quiet zone
    qz = 1 if fmt == "dm" else 4  # zxing's to_image quiet zone (modules)
    n_h, n_w = mat.shape
    code = 1.0 - mat  # 1 = dark module

    upscaled = "SMALL" in v
    module = rng.uniform(1.6, 3.0) if upscaled else rng.uniform(3.0, 7.0)
    module = min(module, size * 0.9 / max(n_w, n_h) / (2 if upscaled else 1))

    # print growth: grow or shrink dark modules on the supersampled bitmap
    big = cv2.resize(code, (n_w * 8, n_h * 8), interpolation=cv2.INTER_NEAREST)
    if "PG" in v:
        r = int(round(8 * rng.uniform(0.12, 0.35)))
        kern = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * r + 1, 2 * r + 1))
        big = cv2.dilate(big, kern) if rng.random() < 0.5 else cv2.erode(big, kern)
    clean_big = cv2.resize(code, (n_w * 8, n_h * 8), interpolation=cv2.INTER_NEAREST)

    # module-level defects on the printed symbol (not the target)
    printed = big.copy()
    cell = 8
    data_cells = [(x, y) for y in range(qz, n_h - qz) for x in range(qz, n_w - qz)]
    if "UEC" in v:
        for x, y in rng.sample(data_cells, max(1, int(len(data_cells) * rng.uniform(0.01, 0.05)))):
            printed[y * cell:(y + 1) * cell, x * cell:(x + 1) * cell] = 1 - code[y, x]
        for _ in range(rng.randint(0, 2)):  # scratches
            p1 = (rng.randint(0, n_w * cell), rng.randint(0, n_h * cell))
            p2 = (rng.randint(0, n_w * cell), rng.randint(0, n_h * cell))
            cv2.line(printed, p1, p2, float(rng.random() < 0.5), rng.randint(2, 6))
    if "FPD" in v:
        cells = _finder_cells(fmt, n_w - 2 * qz, n_h - 2 * qz)
        x0, y0, x1, y1 = rng.choice(cells)
        cx = (qz + rng.uniform(x0, x1)) * cell
        cy = (qz + rng.uniform(y0, y1)) * cell
        cv2.ellipse(printed, (int(cx), int(cy)), (int(rng.uniform(1, 4) * cell), int(rng.uniform(1, 3) * cell)),
                    rng.uniform(0, 180), 0, 360, float(rng.random() < 0.3), -1)
    ink = np.ones_like(printed)
    if "MOD" in v:  # uneven ink per module
        per = np.clip(np.random.default_rng(rng.randint(0, 1 << 30)).normal(1.0, 0.25, (n_h, n_w)), 0.4, 1.2)
        ink = cv2.resize(per.astype(np.float32), printed.shape[::-1], interpolation=cv2.INTER_NEAREST)
    if "RM" in v:  # some modules printed half-way
        per = np.ones((n_h, n_w), np.float32)
        for x, y in rng.sample(data_cells, max(1, len(data_cells) // 12)):
            per[y, x] = rng.uniform(0.35, 0.6)
        ink = ink * cv2.resize(per, printed.shape[::-1], interpolation=cv2.INTER_NEAREST)
    printed = np.clip(printed * ink, 0, 1)

    # paper and ink grey levels
    paper = rng.uniform(0.55, 1.0)
    ink_lvl = paper * (rng.uniform(0.45, 0.75) if "SC" in v else rng.uniform(0.0, 0.3))
    label = paper - (paper - ink_lvl) * printed  # grey label with the symbol

    # geometry: rotation, perspective, axial stretch, wrinkle
    out_scale = module / 8.0 * (2 if upscaled else 1)  # final pixels per supersampled pixel
    H, W = label.shape
    ang = np.deg2rad(rng.uniform(0, 360))
    sx = sy = out_scale * SS
    if "AN" in v:
        a = rng.uniform(0.82, 1.18)
        sx, sy = sx * a, sy / a
    c, s = np.cos(ang), np.sin(ang)
    A = np.float64([[c * sx, -s * sy, 0], [s * sx, c * sy, 0]])
    A[:, 2] = np.float64([size * SS / 2, size * SS / 2]) - A[:, :2] @ np.float64([W / 2, H / 2])
    bg_level = rng.uniform(0.05, 0.6) if "QZ" in v else paper

    def warp(img, border):
        out = cv2.warpAffine(img, A, (size * SS, size * SS), flags=cv2.INTER_LINEAR, borderValue=border)
        if "GN" in v:
            # smooth wrinkle field: a coarse random grid upscaled (fast; same smoothness)
            g = np.random.default_rng(seed)
            amp = rng_amp * module * (2 if upscaled else 1) * SS
            dx = cv2.resize(g.normal(0, 1, (5, 5)).astype(np.float32), (size * SS, size * SS), interpolation=cv2.INTER_CUBIC)
            dy = cv2.resize(g.normal(0, 1, (5, 5)).astype(np.float32), (size * SS, size * SS), interpolation=cv2.INTER_CUBIC)
            dx *= amp / (np.abs(dx).max() + 1e-6)
            dy *= amp / (np.abs(dy).max() + 1e-6)
            yy, xx = np.mgrid[0:size * SS, 0:size * SS].astype(np.float32)
            out = cv2.remap(out, xx + dx, yy + dy, cv2.INTER_LINEAR, borderValue=border)
        return out

    seed, rng_amp = rng.randint(0, 1 << 30), rng.uniform(0.3, 0.9)
    scene = warp(label.astype(np.float32), float(bg_level))
    target = warp((1.0 - clean_big).astype(np.float32), 1.0)  # 1 = light
    if "QZ" in v:
        # dark background / text right up to the symbol: shrink the label to the symbol edge
        m = warp(np.ones_like(label, np.float32), 0.0)
        inner = warp(np.pad(np.ones((H - 2 * qz * cell + 2 * cell, W - 2 * qz * cell + 2 * cell), np.float32),
                            qz * cell - cell), 0.0) if qz > 1 else m
        scene = np.where(inner > 0.5, scene, bg_level)
        txt = np.zeros(scene.shape, np.uint8)  # OpenCV draws text on 8-bit images only
        for _ in range(rng.randint(0, 3)):
            cv2.putText(txt, "".join(rng.choice("0123456789ABC") for _ in range(6)),
                        (rng.randint(0, size * SS // 2), rng.randint(size * SS // 4, size * SS)),
                        cv2.FONT_HERSHEY_SIMPLEX, rng.uniform(1.5, 3.0), 255, 3)
        scene = np.where(txt > 0, bg_level * 0.3, scene).astype(np.float32)

    scene = cv2.resize(cv2.GaussianBlur(scene, (0, 0), SS * 0.45), (size, size), interpolation=cv2.INTER_AREA)
    clean = (cv2.resize(target, (size, size), interpolation=cv2.INTER_AREA) > 0.5).astype(np.float32)

    # light, optics, sensor
    if "LIGHT" in v:
        gx, gy = np.meshgrid(np.linspace(-1, 1, size), np.linspace(-1, 1, size))
        if rng.random() < 0.5:
            scene = scene * (1 + rng.uniform(-0.6, 0.6) * gx + rng.uniform(-0.6, 0.6) * gy)
        else:  # half shadow with a soft edge (film or foil edge across the code)
            t = rng.uniform(0, np.pi)
            edge = np.cos(t) * gx + np.sin(t) * gy + rng.uniform(-0.4, 0.4)
            scene = scene * (rng.uniform(0.25, 0.6) + (1 - rng.uniform(0.25, 0.6)) / (1 + np.exp(-edge * 12)))
    if "GLARE" in v:
        gm = np.zeros((size, size), np.float32)
        cv2.ellipse(gm, (rng.randint(0, size), rng.randint(0, size)), (rng.randint(8, size // 2), rng.randint(4, size // 5)),
                    rng.uniform(0, 180), 0, 360, 1.0, -1)
        scene = scene + cv2.GaussianBlur(gm, (0, 0), rng.uniform(2, 8)) * rng.uniform(0.6, 2.5)
    unit = module * (2 if upscaled else 1)
    if "BLUR" in v:
        scene = cv2.GaussianBlur(scene.astype(np.float32), (0, 0), rng.uniform(0.35, 0.7) * unit)
    if "MOTION" in v:
        k = max(3, int(rng.uniform(0.5, 1.2) * unit))
        ker = np.zeros((k, k), np.float32)
        ker[k // 2, :] = 1.0 / k
        R = cv2.getRotationMatrix2D((k / 2 - 0.5, k / 2 - 0.5), rng.uniform(0, 180), 1)
        scene = cv2.filter2D(scene.astype(np.float32), -1, cv2.warpAffine(ker, R, (k, k)))
    if upscaled:
        # what the camera really saw: half the resolution; the network gets it upscaled 2x
        low = cv2.resize(scene, (size // 2, size // 2), interpolation=cv2.INTER_AREA)
        scene = cv2.resize(low, (size, size), interpolation=cv2.INTER_CUBIC)
    expo = float(np.exp(rng.uniform(np.log(0.02), np.log(0.12)))) if "DARK" in v else rng.uniform(0.5, 1.0)
    x = scene * expo * 255 + np.random.default_rng(rng.randint(0, 1 << 30)).normal(0, rng.uniform(0.3, 2.5 if "DARK" in v else 1.2), scene.shape)
    degraded = np.clip(np.round(x), 0, 255).astype(np.uint8)
    return degraded, clean, sorted(v), upscaled, fmt


if __name__ == "__main__":
    import sys
    rng = random.Random(3)
    tiles = []
    for name in ALL:
        d, c, vs, up, fmt = sample(rng, violations=[name])
        lo, hi = np.percentile(d, (1, 99.5))
        dv = np.clip((d.astype(np.float32) - lo) * 255 / max(4, hi - lo), 0, 255).astype(np.uint8)
        t = np.vstack([dv, (c * 255).astype(np.uint8)])
        cv2.putText(t, name, (3, 14), cv2.FONT_HERSHEY_SIMPLEX, 0.45, 128, 1)
        tiles.append(t)
    rows = [np.hstack(tiles[i:i + 8]) for i in range(0, len(tiles), 8)]
    rows[-1] = np.hstack([rows[-1], np.zeros((rows[0].shape[0], rows[0].shape[1] - rows[-1].shape[1]), np.uint8)])
    cv2.imwrite(sys.argv[1] if len(sys.argv) > 1 else "violations.png", np.vstack(rows))
