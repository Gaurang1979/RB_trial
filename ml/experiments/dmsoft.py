"""Prototype: Data Matrix soft decoding with a virtual perimeter and Reed-Solomon erasures.
- the L finder and clock tracks are not read at all (they are known: 'virtual perimeter')
- each data module gets a confidence (distance of its mean from the threshold)
- codewords are read through the placement map; the least confident ones become erasures
- RS corrects e errors + f erasures when 2e + f <= ecc; we keep a margin of 2 check symbols so a
  miscorrection cannot pass, and the corrected symbol must still decode (zxing) as a final check."""
import numpy as np, reedsolo, cv2
from dmenc import SIZES, place, encode
from dmfit import decode_bits

_MAPS = {}

def placement_map(n):
    """(codeword, bit) -> (row, col) in the n x n symbol: ECC200 placement, recording positions"""
    if n in _MAPS: return _MAPS[n]
    nrow = ncol = n - 2
    used = np.zeros((nrow, ncol), bool); pos = {}
    def module(r, c, v, b):
        if r < 0: r += nrow; c += 4 - ((nrow + 4) % 8)
        if c < 0: c += ncol; r += 4 - ((ncol + 4) % 8)
        used[r, c] = True; pos[(v, b)] = (r + 1, c + 1)
    def utah(r, c, v):
        for b, (dr, dc) in enumerate([(-2, -2), (-2, -1), (-1, -2), (-1, -1), (-1, 0), (0, -2), (0, -1), (0, 0)]):
            module(r + dr, c + dc, v, b)
    def corner(v, pts):
        for b, (r, c) in enumerate(pts): module(r, c, v, b)
    r, c, v = 4, 0, 0
    while True:
        if r == nrow and c == 0:
            corner(v, [(nrow - 1, 0), (nrow - 1, 1), (nrow - 1, 2), (0, ncol - 2), (0, ncol - 1), (1, ncol - 1), (2, ncol - 1), (3, ncol - 1)]); v += 1
        if r == nrow - 2 and c == 0 and ncol % 4:
            corner(v, [(nrow - 3, 0), (nrow - 2, 0), (nrow - 1, 0), (0, ncol - 4), (0, ncol - 3), (0, ncol - 2), (0, ncol - 1), (1, ncol - 1)]); v += 1
        if r == nrow - 2 and c == 0 and ncol % 8 == 4:
            corner(v, [(nrow - 3, 0), (nrow - 2, 0), (nrow - 1, 0), (0, ncol - 2), (0, ncol - 1), (1, ncol - 1), (2, ncol - 1), (3, ncol - 1)]); v += 1
        if r == nrow + 4 and c == 2 and not ncol % 8:
            corner(v, [(nrow - 1, 0), (nrow - 1, ncol - 1), (0, ncol - 3), (0, ncol - 2), (0, ncol - 1), (1, ncol - 3), (1, ncol - 2), (1, ncol - 1)]); v += 1
        while True:
            if r < nrow and c >= 0 and not used[r, c]: utah(r, c, v); v += 1
            r -= 2; c += 2
            if not (r >= 0 and c < ncol): break
        r += 1; c += 3
        while True:
            if r >= 0 and c < ncol and not used[r, c]: utah(r, c, v); v += 1
            r += 2; c -= 2
            if not (r < nrow and c >= 0): break
        r += 3; c += 1
        if not (r < nrow or c < ncol): break
    _MAPS[n] = pos
    return pos


def soft_decode(v, n, max_erasures=None):
    """v: n x n module means (dark = low), canonical orientation (L finder left and bottom)."""
    nd, ne = SIZES[n]; total = nd + ne
    inner = v[1:-1, 1:-1]
    i8 = cv2.normalize(inner, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
    t8 = cv2.threshold(i8, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)[0]
    t = inner.min() + t8 / 255 * (inner.max() - inner.min())   # Otsu: the dark / light split
    spread = np.std(inner) + 1e-6
    dark = v < t
    conf = np.abs(v - t) / spread
    pos = placement_map(n)
    cws, cconf = [], []
    for k in range(total):
        val, cmin = 0, 9.0
        for b in range(8):
            r, c = pos[(k, b)]
            val |= int(dark[r, c]) << (7 - b)
            cmin = min(cmin, conf[r, c])
        cws.append(val); cconf.append(cmin)
    rs = reedsolo.RSCodec(ne, fcr=1, prim=0x12D, generator=2, c_exp=8)
    order = list(np.argsort(cconf))
    limit = ne - 2 if max_erasures is None else max_erasures
    for f in range(0, limit + 1):
        er = sorted(int(i) for i in order[:f])
        try:
            dec, full, errata = rs.decode(bytearray(cws), erase_pos=er if er else None)
        except reedsolo.ReedSolomonError:
            continue
        e = len([p for p in errata if p not in er])
        if 2 * e + f > ne - 2: continue      # too close to the limit: could be a miscorrection
        sym = encode(None, n, cw=list(dec))  # redraw the clean symbol from the corrected data
        # encode() pads; dec already has the full data length, so pad is a no-op
        text = decode_bits(sym)
        if text: return text, f, e
    return None
