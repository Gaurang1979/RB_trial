import sys, glob, os, time, cv2, numpy as np, zxingcpp
sys.path.insert(0, os.path.dirname(__file__))
F = zxingcpp.BarcodeFormat.QRCode
def stretch(c):
    lo, hi = np.percentile(c, (1, 99.5)); return np.clip((c.astype(np.float32) - lo) * 255 / max(4, hi - lo), 0, 255).astype(np.uint8)
def render(bits, cell=6, qz=4):
    n = bits.shape[0]; img = np.full(((n + 2 * qz) * cell,) * 2, 255, np.uint8)
    big = np.kron(bits.astype(np.uint8), np.ones((cell, cell), np.uint8)) * 255
    img[qz * cell:(qz + n) * cell, qz * cell:(qz + n) * cell] = 255 - big
    return img
def decode_bits(bits):
    r = zxingcpp.read_barcodes(render(bits), formats=F, is_pure=True, try_rotate=False, try_downscale=False, binarizer=zxingcpp.Binarizer.BoolCast)
    return r[0].text if r else None
def grid_values(img, quad, n, sub=3):
    src = np.float32([[0, 0], [n, 0], [n, n], [0, n]]); H = cv2.getPerspectiveTransform(src, np.float32(quad))
    o = (np.arange(sub) + 0.5) / sub * 0.6 + 0.2
    gx, gy = np.meshgrid(np.arange(n), np.arange(n))
    px = (gx[..., None, None] + o[None, None, :, None]).repeat(sub, -1); py = (gy[..., None, None] + o[None, None, None, :]).repeat(sub, -2)
    pts = np.stack([px.ravel(), py.ravel()], -1).astype(np.float32)[None]
    q = cv2.perspectiveTransform(pts, H)[0]
    vals = cv2.remap(img, q[:, 0].reshape(1, -1), q[:, 1].reshape(1, -1), cv2.INTER_LINEAR)
    return vals.reshape(n, n, sub * sub).mean(-1)
def thresholds(v):
    t = cv2.threshold(np.clip(v, 0, 255).astype(np.uint8), 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)[0]
    yield v < t
    for d in (-12, 12): yield v < t + d
    loc = cv2.blur(v, (7, 7)); yield v < loc          # local: each module vs its 7x7-module neighbourhood
    loc = cv2.blur(v, (5, 5)); yield v < loc - 2
def try_quad(img, quad, versions=(21,)):
    for n in versions:
        for dx in range(-12, 13, 4):
            for dy in range(-12, 13, 4):
                q = quad.copy(); q[2] += (dx, dy)      # BR corner: no finder there, least certain
                v = grid_values(img, q, n)
                for bits in thresholds(v):
                    t = decode_bits(bits)
                    if t: return t
    return None
if __name__ == "__main__":
    det = cv2.QRCodeDetector(); aru = cv2.QRCodeDetectorAruco()
    files = sorted(glob.glob(sys.argv[1] + "/*.png")); ok = 0; t0 = time.time()
    for p in files:
        c = cv2.imread(p, 0); s = stretch(c)
        u = cv2.resize(cv2.GaussianBlur(s, (0, 0), 0.6), None, fx=4, fy=4, interpolation=cv2.INTER_CUBIC).astype(np.float32)
        text = None
        for d in (det, aru):
            try: r, pts = d.detect(u.astype(np.uint8))
            except cv2.error: r = False
            if r:
                text = try_quad(u, pts.reshape(-1, 2).astype(np.float32))
                if text: break
        ok += bool(text); print(os.path.basename(p), text or "")
    print(f"decoded {ok}/{len(files)} in {time.time() - t0:.1f}s")
