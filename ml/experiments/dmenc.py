"""Minimal Data Matrix ECC200 encoder (square, single data region sizes 10..26): ASCII encodation
(digit pairs), Reed-Solomon over GF(256)/0x12D, standard module placement. For matching printed
codes; returns an n x n bool matrix (True = dark)."""
import numpy as np

SIZES = {10: (3, 5), 12: (5, 7), 14: (8, 10), 16: (12, 12), 18: (18, 14), 20: (22, 18), 22: (30, 20), 24: (36, 24), 26: (44, 28)}

EXP = [0] * 512; LOG = [0] * 256
x = 1
for i in range(255):
    EXP[i] = x; LOG[x] = i
    x <<= 1
    if x & 0x100: x ^= 0x12D
for i in range(255, 512): EXP[i] = EXP[i - 255]

def gmul(a, b):
    return 0 if a == 0 or b == 0 else EXP[LOG[a] + LOG[b]]

def rs(data, n_ecc):
    g = [1]
    for i in range(1, n_ecc + 1):
        ng = [0] * (len(g) + 1)
        for j, c in enumerate(g):
            ng[j] ^= gmul(c, EXP[i]); ng[j + 1] ^= c
        g = ng
    # g is low-to-high degree; long division
    msg = list(data) + [0] * n_ecc
    gen = g[::-1]  # high to low, leading 1
    for i in range(len(data)):
        coef = msg[i]
        if coef:
            for j in range(1, len(gen)):
                msg[i + j] ^= gmul(gen[j], coef)
    return msg[len(data):]

def ascii_cw(text):
    cw = []; i = 0
    while i < len(text):
        if i + 1 < len(text) and text[i].isdigit() and text[i + 1].isdigit():
            cw.append(130 + int(text[i:i + 2])); i += 2
        else:
            cw.append(ord(text[i]) + 1); i += 1
    return cw

def pad(cw, total):
    cw = list(cw)
    if len(cw) < total: cw.append(129)
    while len(cw) < total:
        r = ((149 * (len(cw) + 1)) % 253) + 1
        v = 129 + r
        cw.append(v - 254 if v > 254 else v)
    return cw

def place(cw, nrow, ncol):
    """ECC200 placement (ISO/IEC 16022 Annex F) into an nrow x ncol mapping matrix."""
    M = -np.ones((nrow, ncol), np.int32)
    bits = []
    def module(r, c, v, b):
        if r < 0: r += nrow; c += 4 - ((nrow + 4) % 8)
        if c < 0: c += ncol; r += 4 - ((ncol + 4) % 8)
        M[r, c] = (cw[v] >> (7 - b)) & 1 if v < len(cw) else 0
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
        while True:  # up-right
            if r < nrow and c >= 0 and M[r, c] < 0: utah(r, c, v); v += 1
            r -= 2; c += 2
            if not (r >= 0 and c < ncol): break
        r += 1; c += 3
        while True:  # down-left
            if r >= 0 and c < ncol and M[r, c] < 0: utah(r, c, v); v += 1
            r += 2; c -= 2
            if not (r < nrow and c >= 0): break
        r += 3; c += 1
        if not (r < nrow or c < ncol): break
    if M[nrow - 1, ncol - 1] < 0:
        M[nrow - 1, ncol - 1] = M[nrow - 2, ncol - 2] = 1
        M[nrow - 1, ncol - 2] = M[nrow - 2, ncol - 1] = 0
    return M

def encode(text, n, cw=None):
    nd, ne = SIZES[n]
    data = cw if cw is not None else ascii_cw(text)
    if len(data) > nd: return None
    data = pad(data, nd)
    allcw = data + rs(data, ne)
    m = place(allcw, n - 2, n - 2)
    out = np.zeros((n, n), bool)
    out[1:-1, 1:-1] = m == 1
    out[:, 0] = True; out[n - 1, :] = True                      # L finder: left, bottom
    out[0, :] = [x % 2 == 0 for x in range(n)]                  # top clock
    out[:, n - 1] = [(n - 1 - y) % 2 == 0 for y in range(n)]    # right clock
    return out
