"""Prototype: QR soft decoding (versions 1..6) from n x n module means.
- function patterns are known (taken from a reference symbol of the same version / EC level / mask)
- mask (and EC level) from a soft match of the format modules
- data modules in placement order, unmasked, with a confidence each
- codewords de-interleaved into RS blocks; least certain codewords as erasures; RS over
  GF(256)/0x11D, roots alpha^0.. (fcr 0); 2e + f <= ecc - 2 per block
- the corrected codewords are redrawn into a clean symbol and must decode (zxing)"""
import numpy as np, cv2, reedsolo
from qrcodegen import QrCode, QrSegment
from gridfit2 import decode_bits

ECLS = [QrCode.Ecc.LOW, QrCode.Ecc.MEDIUM, QrCode.Ecc.QUARTILE, QrCode.Ecc.HIGH]
_REF = {}


def reference(version, ecl, mask):
    k = (version, ecl.ordinal, mask)
    if k not in _REF:
        q = QrCode.encode_segments([QrSegment.make_bytes(b'0')], ecl, version, version, mask, False)
        _REF[k] = q
    return _REF[k]


ALIGN = {1: [], 2: [6, 18], 3: [6, 22], 4: [6, 26], 5: [6, 30], 6: [6, 34]}

def function_mask(version):
    """modules that are not data: finders + separators + format areas, timing, alignment, dark module"""
    n = 17 + 4 * version
    f = np.zeros((n, n), bool)
    f[:9, :9] = True; f[:9, n - 8:] = True; f[n - 8:, :9] = True       # finders, separators, format
    f[6, :] = True; f[:, 6] = True                                      # timing
    pos = ALIGN[version]
    for cy in pos:
        for cx in pos:
            if (cx == 6 and cy == 6) or (cx == 6 and cy == pos[-1]) or (cx == pos[-1] and cy == 6): continue
            f[cy - 2:cy + 3, cx - 2:cx + 3] = True
    return f

_FM = {}

def placement(q):
    """data module positions in codeword-bit order"""
    n = q._size; out = []
    if n not in _FM: _FM[n] = function_mask((n - 17) // 4)
    isf = _FM[n]
    right = n - 1
    while right >= 1:
        if right == 6: right = 5
        for vert in range(n):
            for j in range(2):
                x = right - j
                upward = ((right + 1) & 2) == 0
                y = (n - 1 - vert) if upward else vert
                if not isf[y, x]: out.append((y, x))
        right -= 2
    return out


def block_layout(version, ecl):
    """interleaved codeword index -> (block, index in block); and per-block (data len, ecc len)"""
    nb = QrCode._NUM_ERROR_CORRECTION_BLOCKS[ecl.ordinal][version]
    ecclen = QrCode._ECC_CODEWORDS_PER_BLOCK[ecl.ordinal][version]
    raw = QrCode._get_num_raw_data_modules(version) // 8
    nshort = nb - raw % nb; shortlen = raw // nb
    blocks = []
    for i in range(nb):
        dlen = shortlen - ecclen + (0 if i < nshort else 1)
        blocks.append([(i, k) for k in range(dlen)] + [None] * (0 if i >= nshort else 1) + [(i, dlen + k) for k in range(ecclen)])
    # in short blocks the dummy sits at index dlen (before the ecc), as qrcodegen interleaves
    order = []
    for k in range(len(blocks[0]) if nb else 0):
        for i, blk in enumerate(blocks):
            longlen = max(len(b) for b in blocks)
            if k < len(blk) and blk[k] is not None: order.append(blk[k])
    sizes = [(shortlen - ecclen + (0 if i < nshort else 1), ecclen) for i in range(nb)]
    return order, sizes


def soft_decode(v, version, ecl_hint=None):
    """v: n x n module means (dark = low), canonical orientation. Returns text or None."""
    n = 17 + 4 * version
    if v.shape != (n, n): return None
    i8 = cv2.normalize(v, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
    cut = v.min() + cv2.threshold(i8, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)[0] / 255 * (v.max() - v.min())
    d, l = v[v <= cut], v[v > cut]
    if len(d) == 0 or len(l) == 0: return None
    t = (d.mean() + l.mean()) / 2
    dark = v < t; conf = np.abs(v - t) / (v.std() + 1e-6)
    z = (t - v) / (v.std() + 1e-6)                          # dark positive
    # EC level and mask: soft match of the format modules
    cands = []
    for ecl in ([ecl_hint] if ecl_hint is not None else ECLS):
        for m in range(8):
            q = reference(version, ecl, m)
            fm = np.array([[1.0 if q._modules[y][x] else -1.0 for x in range(n)] for y in range(n)])
            fmt = np.zeros((n, n), bool)
            fmt[8, :9] = fmt[:9, 8] = True; fmt[8, n - 8:] = fmt[n - 7:, 8] = True
            fmt[6, :] = fmt[:, 6] = False
            cands.append((float((z * fm)[fmt].sum()), ecl, m))
    cands.sort(key=lambda c: -c[0])
    for _, ecl, m in cands[:3]:
        q = reference(version, ecl, m)
        pos = placement(q)
        order, sizes = block_layout(version, ecl)
        total = len(order)
        bits = []; bconf = []
        for (y, x) in pos[:total * 8]:
            b = bool(dark[y, x]) ^ (QrCode._MASK_PATTERNS[m](x, y) == 0)
            bits.append(int(b)); bconf.append(conf[y, x])
        cw = [int(''.join(map(str, bits[k * 8:k * 8 + 8])), 2) for k in range(total)]
        cc = [min(bconf[k * 8:k * 8 + 8]) for k in range(total)]
        blocks = [[0] * (a + b) for a, b in sizes]; bcf = [[0.0] * (a + b) for a, b in sizes]
        for idx, (bi, k) in enumerate(order):
            blocks[bi][k] = cw[idx]; bcf[bi][k] = cc[idx]
        fixed = []
        ok = True
        for bi, (dl, el) in enumerate(sizes):
            rs = reedsolo.RSCodec(el, fcr=0, prim=0x11D, generator=2, c_exp=8)
            order_c = list(np.argsort(bcf[bi]))
            done = None
            for f in range(0, el - 1):
                er = sorted(int(i) for i in order_c[:f])
                try:
                    dec, full, errata = rs.decode(bytearray(blocks[bi]), erase_pos=er if er else None)
                except reedsolo.ReedSolomonError:
                    continue
                e = len([p for p in errata if p not in er])
                if 2 * e + f > el - 2: continue
                done = list(full); break
            if done is None: ok = False; break
            fixed.append(done)
        if not ok: continue
        # redraw: interleave the corrected blocks, place with the mask into the reference symbol
        cwf = [0] * total
        for idx, (bi, k) in enumerate(order): cwf[idx] = fixed[bi][k]
        isf = _FM[n]
        sym = np.array([[bool(q._modules[y][x]) if isf[y, x] else False for x in range(n)] for y in range(n)])
        bitsf = [(cwf[k >> 3] >> (7 - (k & 7))) & 1 for k in range(total * 8)]
        for k, (y, x) in enumerate(pos):
            b = bitsf[k] if k < len(bitsf) else 0
            sym[y, x] = bool(b) ^ (QrCode._MASK_PATTERNS[m](x, y) == 0)
        text = decode_bits(sym)
        if text: return text
    return None
