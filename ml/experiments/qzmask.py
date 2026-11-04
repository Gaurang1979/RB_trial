"""Artificial quiet zone: whiten everything outside a candidate code square (grown a little),
then let the normal decoder (zxing) find and read the code."""
import sys, glob, os, time, cv2, numpy as np, zxingcpp
sys.path.insert(0, os.path.dirname(__file__))
from dmfit import code_squares, refine

F = zxingcpp.BarcodeFormat.DataMatrix
B = zxingcpp.Binarizer

def masked(c, box, grow):
    ctr = box.mean(0); b = ctr + (box - ctr) * grow
    m = np.zeros_like(c); cv2.fillConvexPoly(m, b.astype(np.int32), 255)
    paper = np.percentile(c[m > 0], 95)
    out = c.copy(); out[m == 0] = paper
    return out

def read(img):
    for b in (B.LocalAverage, B.GlobalHistogram):
        r = zxingcpp.read_barcodes(img, formats=F, binarizer=b, try_rotate=True)
        if r: return r[0].text
    return None

def attempt(c, side, sizes=(16, 18)):
    cf = c.astype(np.float32)
    for box in code_squares(c, side):
        cands = [box] + [refine(cf, box, n)[0] for n in sizes]
        for b in cands:
            for grow in (1.04, 1.10, 1.0, 1.16):
                t = read(masked(c, np.float32(b), grow))
                if t: return t
    return None

if __name__ == '__main__':
    files = sorted(glob.glob(sys.argv[1])); side = float(sys.argv[2]); ok = 0; t0 = time.time()
    for p in files:
        c = cv2.imread(p, 0); lo, hi = np.percentile(c, (1, 99.5))
        c = np.clip((c.astype(np.float32) - lo) * 255 / max(4, hi - lo), 0, 255).astype(np.uint8)
        t = attempt(c, side); ok += bool(t); print(os.path.basename(p), t or '')
    print(f'decoded {ok}/{len(files)} in {time.time() - t0:.1f}s')
