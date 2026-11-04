"""Prototype: 1D codes at any angle (2026-10-27). zxing scans rows (and a 90 degree turn), so a
case label tilted 25-45 degrees is not read at all (synth1d.py sweep: 0/20).

1. band: the gradient structure tensor at about two modules; bars give strong, coherent gradients
   all in one direction. The largest coherent blob is the code; its gradient direction is the
   normal to the bars (the scan direction).
2. profile: K scan lines along that direction across the middle of the bars, sampled at
   `sub` points per pixel and fused by the median (scratches, spots and noise drop out).
3. the profile drawn as a clean upright image and read by zxing.
"""
import sys, os
import numpy as np, cv2


def bands(img, X=3.0, max_bands=3):
    """candidate code bands: (centre, scan direction unit vector, length along scan, height across)"""
    g = img.astype(np.float32)
    # half resolution for the locator: cheap, and bars of 2 px or more survive
    f = 0.5 if X >= 2.5 else 1.0
    s = cv2.resize(g, None, fx=f, fy=f, interpolation=cv2.INTER_AREA) if f != 1.0 else g
    gx = cv2.Sobel(s, cv2.CV_32F, 1, 0, ksize=3); gy = cv2.Sobel(s, cv2.CV_32F, 0, 1, ksize=3)
    sig = max(2.0, 2.5 * X * f)
    jxx = cv2.GaussianBlur(gx * gx, (0, 0), sig); jyy = cv2.GaussianBlur(gy * gy, (0, 0), sig); jxy = cv2.GaussianBlur(gx * gy, (0, 0), sig)
    tr = jxx + jyy; det = np.sqrt((jxx - jyy) ** 2 + 4 * jxy ** 2)
    coh = det / (tr + 1e-6)                                   # 1 = all gradients in one direction
    energy = np.sqrt(tr)
    thr = max(np.percentile(energy, 99) * 0.25, 1e-3)
    mask = ((coh > 0.6) & (energy > thr)).astype(np.uint8)
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (int(3 * X * f) | 1,) * 2))
    n, lab, stats, _ = cv2.connectedComponentsWithStats(mask)
    order = np.argsort(-stats[1:, cv2.CC_STAT_AREA])[:max_bands] + 1
    out = []
    for k in order:
        if stats[k, cv2.CC_STAT_AREA] < 200 * f * f: continue
        ys, xs = np.nonzero(lab == k)
        # the scan direction: mean doubled angle of the gradient over the blob
        theta = 0.5 * np.arctan2(2 * jxy[ys, xs].sum(), (jxx[ys, xs] - jyy[ys, xs]).sum())
        d = np.array([np.cos(theta), np.sin(theta)])          # across the bars
        e = np.array([-d[1], d[0]])                            # along the bars
        pts = np.stack([xs, ys], 1) / f
        c = pts.mean(0)
        u = (pts - c) @ d; v = (pts - c) @ e
        L = (np.percentile(u, 99.5) - np.percentile(u, 0.5)); Hh = (np.percentile(v, 97) - np.percentile(v, 3))
        cu = (np.percentile(u, 99.5) + np.percentile(u, 0.5)) / 2; cv_ = (np.percentile(v, 97) + np.percentile(v, 3)) / 2
        out.append((c + cu * d + cv_ * e, d, L, Hh))
    return out


def profile(img, band, X=3.0, lines=24, sub=4, margin_mod=14):
    """median profile across the bars, `sub` samples per pixel, with a quiet-zone margin"""
    c, d, L, Hh = band
    e = np.array([-d[1], d[0]])
    half = L / 2 + margin_mod * X
    t = np.arange(-half, half, 1.0 / sub, dtype=np.float32)
    offs = np.linspace(-0.3 * Hh, 0.3 * Hh, lines, dtype=np.float32)
    mx = (c[0] + t[None, :] * d[0] + offs[:, None] * e[0]).astype(np.float32)
    my = (c[1] + t[None, :] * d[1] + offs[:, None] * e[1]).astype(np.float32)
    rows = cv2.remap(img.astype(np.float32), mx, my, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
    return np.median(rows, 0)


def as_image(p, height=24):
    lo, hi = np.percentile(p, 1), np.percentile(p, 99)
    q = np.clip((p - lo) * 255.0 / max(4.0, hi - lo), 0, 255).astype(np.uint8)
    return np.repeat(q[None], height, 0)


def read_band_zxing(img, X=3.0, formats=None):
    import zxingcpp
    out = []
    for b in bands(img, X):
        p = profile(img, b, X)
        for flip in (False, True):
            im = as_image(p[::-1] if flip else p)
            r = zxingcpp.read_barcodes(im, formats=formats) if formats is not None else zxingcpp.read_barcodes(im)
            if r:
                out.append(r[0].text); break
        if out: break
    return out


if __name__ == '__main__':
    sys.path.insert(0, os.path.dirname(__file__)); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
    import synth1d as s, zxingcpp
    rng = np.random.default_rng(3)
    for key, levels in (('tilt', [0, 10, 25, 45, 60, 80]), ('scratches', [0, 6, 9]), ('noise', [14, 20, 28]), ('defocus', [1.2, 1.8, 2.4]), ('motion', [3, 5, 7]), ('dark', [160, 185, 200]), ('X', [1.5, 1.8])):
        res = []
        for lv in levels:
            ok = wrong = 0
            for _ in range(20):
                text = s.sscc(rng); img, _ = s.frame(s.gs1_128_widths(text), rng, **{key: lv})
                X = lv if key == 'X' else 3.0
                got = read_band_zxing(img, X)
                if any(g.replace('(', '').replace(')', '').endswith(text) for g in got): ok += 1
                elif got: wrong += 1
            res.append(f'{lv}: {ok}/20' + (f' WRONG {wrong}' if wrong else ''))
        print(f'{key:9s} ' + ' | '.join(res))
