"""Prototype: module values of an under-resolved code by least squares (each pixel is a blurred,
area-weighted mix of the modules it covers), instead of sampling module centres.

pixel(x) = L - (L - D) * sum_k w_k(x) d_k, with d_k = darkness of module k in [0, 1] and
w_k(x) = integral over module k (in module coordinates) of a Gaussian centred on the pixel's
position, sigma = blur (pixels) / module size (pixels), via the homography's local scale."""
import numpy as np, cv2
from scipy.sparse import coo_matrix
from scipy.sparse.linalg import lsqr
from scipy.special import erf


def lsq_modules(img, quad, n, blur_px=1.0, reg=0.05, margin=1):
    """img: grey (float), quad: TL, TR, BR, BL of the code (pixels), n modules.
    Returns n x n darkness estimates (1 = dark)."""
    src = np.float32([[0, 0], [n, 0], [n, n], [0, n]])
    H = cv2.getPerspectiveTransform(np.float32(quad), src)          # pixels -> module coords
    x0, y0 = np.floor(quad.min(0)).astype(int) - 2; x1, y1 = np.ceil(quad.max(0)).astype(int) + 3
    x0, y0 = max(x0, 0), max(y0, 0); x1, y1 = min(x1, img.shape[1]), min(y1, img.shape[0])
    ys, xs = np.mgrid[y0:y1, x0:x1]
    pts = np.stack([xs.ravel() + 0.0, ys.ravel() + 0.0], -1).astype(np.float32)[None]
    uv = cv2.perspectiveTransform(pts, H)[0]
    mod_px = np.linalg.norm(quad[1] - quad[0]) / n
    s = max(0.15, blur_px / mod_px)                                   # blur in module units
    pix = img[y0:y1, x0:x1].ravel().astype(np.float64)
    keep = (uv[:, 0] > -margin) & (uv[:, 0] < n + margin) & (uv[:, 1] > -margin) & (uv[:, 1] < n + margin)
    uv, pix = uv[keep], pix[keep]
    # light / dark levels from the area around and inside the code
    L = np.percentile(pix, 95); D = np.percentile(pix, 5)
    b = (L - pix) / max(1e-6, L - D)                                  # observed darkness
    rows, cols, vals = [], [], []
    for i, (u, v) in enumerate(uv):
        ku0, ku1 = int(np.floor(u - 3 * s)), int(np.floor(u + 3 * s))
        kv0, kv1 = int(np.floor(v - 3 * s)), int(np.floor(v + 3 * s))
        for kv in range(max(0, kv0), min(n - 1, kv1) + 1):
            wy = 0.5 * (erf((kv + 1 - v) / (s * np.sqrt(2))) - erf((kv - v) / (s * np.sqrt(2))))
            if wy < 1e-3: continue
            for ku in range(max(0, ku0), min(n - 1, ku1) + 1):
                wx = 0.5 * (erf((ku + 1 - u) / (s * np.sqrt(2))) - erf((ku - u) / (s * np.sqrt(2))))
                w = wx * wy
                if w > 1e-3: rows.append(i); cols.append(kv * n + ku); vals.append(w)
    A = coo_matrix((vals, (rows, cols)), shape=(len(b), n * n)).tocsr()
    d = lsqr(A, b, damp=reg, iter_lim=300)[0]
    return np.clip(d, -0.5, 1.5).reshape(n, n)
