"""Synthetic training scenes for the code locator, rendered at locator scale (1/8 of full res).

Each scene is a grey canvas with labels carrying real QR / Data Matrix codes (made by
zxing-cpp, so the module patterns are genuine), plus things that are NOT codes and must be
ignored: printed text, 1D barcodes, box edges, dividers, cardboard texture and tape glare.
The camera is then simulated: very low exposure with low-bit quantisation, blur and sensor
noise, as on the darkest sample images.

Returns (image uint8 HxW, mask uint8 HxW) where mask = 1 inside code quads (not the label).
"""

import random
import string

import cv2
import numpy as np
import zxingcpp

SS = 4  # supersampling when drawing codes, then area-downscaled into the scene

_FMT = {"qr": zxingcpp.BarcodeFormat.QRCode, "dm": zxingcpp.BarcodeFormat.DataMatrix}


def _random_payload(rng):
    # GS1-like payloads of realistic lengths: GTIN, dates, batch, serial
    gtin = "".join(rng.choice(string.digits) for _ in range(14))
    serial = "".join(rng.choice(string.digits + string.ascii_uppercase) for _ in range(rng.randint(6, 20)))
    batch = "".join(rng.choice(string.digits + string.ascii_uppercase) for _ in range(rng.randint(3, 10)))
    s = f"01{gtin}17{rng.randint(240101, 301231)}10{batch}\x1d21{serial}"
    if rng.random() < 0.2:
        s = f"https://id.gs1.org/01/{gtin}/21/{serial}"
    return s


def _code_matrix(rng, fmt):
    # QR error correction as on the sample lines: M (envelopes), Q (cartons), sometimes H
    kw = {"ec_level": rng.choice(["M", "M", "Q", "Q", "H"])} if fmt == "qr" else {}
    img = np.array(zxingcpp.create_barcode(_random_payload(rng), _FMT[fmt], **kw).to_image(scale=1))
    # to_image adds a quiet zone; keep it (it is part of a real label too)
    return img


def _text_lines(canvas, rng, x, y, w, h, ink, k=1):
    # OpenCV draws text only on 8-bit images: draw a coverage mask, then blend the ink in
    font = rng.choice([cv2.FONT_HERSHEY_SIMPLEX, cv2.FONT_HERSHEY_PLAIN, cv2.FONT_HERSHEY_DUPLEX])
    scale = rng.uniform(0.15, 0.45) * k
    cover = np.zeros(canvas.shape, np.uint8)
    yy = y + 6 * k
    while yy < y + h:
        txt = "".join(rng.choice(string.ascii_uppercase + string.digits + " :.-/") for _ in range(rng.randint(4, 18)))
        cv2.putText(cover, txt, (int(x), int(yy)), font, scale, 255, 1, cv2.LINE_AA)
        yy += rng.uniform(4, 9) * k
    a = cover.astype(np.float32) / 255.0
    canvas[:] = canvas * (1 - a) + float(ink) * a


def _barcode_1d(canvas, rng, x, y, w, h, ink):
    xx = x
    while xx < x + w:
        bw = rng.choice([0.5, 1.0, 1.5, 2.0])
        if rng.random() < 0.5:
            cv2.rectangle(canvas, (int(xx), int(y)), (int(xx + bw), int(y + h)), float(ink), -1)
        xx += bw + rng.choice([0.5, 1.0, 1.5])


def _shrink_wrap(canvas, rng):
    """Plastic film over cardboard: dark blotches, bright wrinkles, sheen bands."""
    H, W = canvas.shape
    g = np.random.default_rng(rng.randint(0, 1 << 30))
    blot = cv2.GaussianBlur(g.normal(0, 1, (H, W)).astype(np.float32), (0, 0), rng.uniform(2, 8))
    canvas -= (blot > rng.uniform(0.3, 1.0)).astype(np.float32) * rng.uniform(0.05, 0.3)
    wr = np.zeros((H, W), np.uint8)
    for _ in range(rng.randint(3, 25)):
        pts = [(rng.uniform(0, W), rng.uniform(0, H))]
        for _ in range(rng.randint(2, 6)):
            pts.append((pts[-1][0] + rng.uniform(-40, 40), pts[-1][1] + rng.uniform(-40, 40)))
        cv2.polylines(wr, [np.int32(pts)], False, 255, 1, cv2.LINE_AA)
    canvas += cv2.GaussianBlur(wr.astype(np.float32) / 255, (0, 0), rng.uniform(0.4, 1.2)) * rng.uniform(0.2, 1.2)
    for _ in range(rng.randint(0, 3)):
        band = np.zeros((H, W), np.float32)
        y0 = rng.uniform(0, H)
        cv2.rectangle(band, (0, int(y0)), (W, int(y0 + rng.uniform(5, 40))), 1.0, -1)
        canvas += cv2.GaussianBlur(band, (0, 0), rng.uniform(2, 6)) * rng.uniform(0.05, 0.3)


def _background(rng, H, W):
    base = rng.uniform(0.04, 0.6)
    canvas = np.full((H, W), base, np.float32)
    # gradient (uneven lighting)
    gx, gy = np.meshgrid(np.linspace(-1, 1, W), np.linspace(-1, 1, H))
    canvas += rng.uniform(-0.15, 0.15) * gx + rng.uniform(-0.15, 0.15) * gy
    # cardboard texture
    tex = cv2.GaussianBlur(np.random.default_rng(rng.randint(0, 1 << 30)).normal(0, 1, (H, W)).astype(np.float32), (0, 0), rng.uniform(0.6, 3))
    canvas += tex * rng.uniform(0.0, 0.05)
    # boxes / bottles / dividers: rectangles and lines in other greys
    for _ in range(rng.randint(2, 12)):
        x0, y0 = rng.uniform(-20, W), rng.uniform(-20, H)
        w, h = rng.uniform(20, 160), rng.uniform(20, 160)
        cv2.rectangle(canvas, (int(x0), int(y0)), (int(x0 + w), int(y0 + h)), float(rng.uniform(0.02, 0.8)), -1 if rng.random() < 0.6 else 1)
    for _ in range(rng.randint(0, 10)):
        p1 = (int(rng.uniform(0, W)), int(rng.uniform(0, H)))
        p2 = (int(rng.uniform(0, W)), int(rng.uniform(0, H)))
        cv2.line(canvas, p1, p2, float(rng.uniform(0, 0.9)), rng.randint(1, 3), cv2.LINE_AA)
    # distractors: printed text and 1D barcodes on plain labels
    if rng.random() < 0.35:
        _shrink_wrap(canvas, rng)
    for _ in range(rng.randint(0, 6)):
        x0, y0, w, h = rng.uniform(0, W - 20), rng.uniform(0, H - 10), rng.uniform(15, 70), rng.uniform(8, 40)
        paper = rng.uniform(0.5, 1.0)
        cv2.rectangle(canvas, (int(x0), int(y0)), (int(x0 + w), int(y0 + h)), paper, -1)
        if rng.random() < 0.6:
            _text_lines(canvas, rng, x0 + 1, y0, w, h, paper * rng.uniform(0, 0.3))
        else:
            _barcode_1d(canvas, rng, x0 + 2, y0 + 2, w - 4, h - 4, paper * rng.uniform(0, 0.3))
    return canvas


def _paste_code(canvas, mask, rng, size_px, fmt):
    """Draw one label with a code at a random pose; size_px = code side at scene scale."""
    H, W = canvas.shape
    mat = _code_matrix(rng, fmt).astype(np.float32) / 255.0
    n = mat.shape[0]
    side = max(4, int(round(size_px * SS)))
    code = cv2.resize(mat, (side, side), interpolation=cv2.INTER_NEAREST)

    # label around the code, sometimes with text beside it; some labels are much brighter than
    # the scene (white labels on a dark case: they saturate while the case stays dark)
    paper = rng.uniform(0.45, 1.0) if rng.random() < 0.8 else rng.uniform(1.6, 4.0)
    ink = paper * rng.uniform(0.0, 0.35)
    polarity_inverted = rng.random() < 0.08
    lw = int(side * rng.uniform(1.1, 2.6))
    lh = int(side * rng.uniform(1.1, 1.8))
    label = np.full((lh, lw), paper, np.float32)
    ox = rng.randint(0, max(0, lw - side))
    oy = rng.randint(0, max(0, lh - side))
    patch = ink + (paper - ink) * code
    if polarity_inverted:
        patch = paper + ink - patch
    label[oy:oy + side, ox:ox + side] = patch
    if lw - side > side * 0.4 and rng.random() < 0.7:
        tx = ox + side + 2 if ox < lw / 2 else 2
        if rng.random() < 0.3:
            # decorative printed frame (as on the caps' labels): a decoy, not a code
            fw = lw - side - 6
            cv2.rectangle(label, (int(tx + 2), int(lh * 0.15)), (int(tx + fw), int(lh * 0.85)), float(ink), max(1, SS // 2))
            cv2.rectangle(label, (int(tx + 2 + SS * 2), int(lh * 0.15 + SS * 2)), (int(tx + fw - SS * 2), int(lh * 0.85 - SS * 2)), float(ink), 1)
        _text_lines(label, rng, tx, 2, lw - side - 4, lh - 4, ink, k=SS)
    code_quad = np.float32([[ox, oy], [ox + side, oy], [ox + side, oy + side], [ox, oy + side]])
    label_quad = np.float32([[0, 0], [lw, 0], [lw, lh], [0, lh]])

    # pose: rotation, scale to scene, mild perspective, position
    ang = rng.uniform(0, 360)
    persp = rng.uniform(0, 0.08)
    cx, cy = rng.uniform(0, W), rng.uniform(0, H)
    M_rot = cv2.getRotationMatrix2D((lw / 2, lh / 2), ang, 1.0 / SS)
    pts = cv2.transform(label_quad[None], M_rot)[0]
    pts += np.float32([cx, cy]) - pts.mean(0)
    pts += np.float32([rng.normalvariate(0, 1) for _ in range(8)]).reshape(4, 2) * persp * size_px
    P = cv2.getPerspectiveTransform(label_quad, pts.astype(np.float32))

    # draw at scene scale: area-average by warping the supersampled label with INTER_AREA-like blur
    # area-average for the downscale, plus optional defocus (labels bent over caps)
    defocus = rng.uniform(0, 0.25 * size_px) if rng.random() < 0.3 else 0
    blur = cv2.GaussianBlur(label, (0, 0), SS * 0.45 + SS * defocus * 0.15)
    warped = cv2.warpPerspective(blur, P, (W, H), flags=cv2.INTER_LINEAR, borderValue=-1)
    alpha = cv2.warpPerspective(np.ones_like(label), P, (W, H), flags=cv2.INTER_LINEAR, borderValue=0)
    m = alpha > 0.5
    canvas[m] = warped[m] * alpha[m] + canvas[m] * (1 - alpha[m])

    cq = cv2.perspectiveTransform(code_quad[None], P)[0]
    cv2.fillConvexPoly(mask, cq.round().astype(np.int32), 1)
    return cq


def _glare(canvas, rng, n):
    H, W = canvas.shape
    for _ in range(n):
        g = np.zeros_like(canvas)
        cv2.ellipse(g, (int(rng.uniform(0, W)), int(rng.uniform(0, H))),
                    (int(rng.uniform(3, 25)), int(rng.uniform(2, 10))), rng.uniform(0, 180), 0, 360, 1.0, -1)
        g = cv2.GaussianBlur(g, (0, 0), rng.uniform(0.5, 2.5))
        canvas += g * rng.uniform(0.5, 3.0)


def camera(img01, rng):
    """Simulate exposure, quantisation and noise on a 0..1 float scene -> uint8."""
    k = float(np.exp(rng.uniform(np.log(0.015), np.log(1.0))))   # exposure: 1.5% .. 100%
    x = img01 * k * 255.0
    noise = rng.uniform(0.3, 2.5)
    x = x + np.random.default_rng(rng.randint(0, 1 << 30)).normal(0, noise, x.shape)
    if rng.random() < 0.5:
        x = cv2.GaussianBlur(x, (0, 0), rng.uniform(0.3, 0.9))
    return np.clip(np.round(x), 0, 255).astype(np.uint8)


def scene(rng: random.Random, H=256, W=256):
    canvas = _background(rng, H, W)
    mask = np.zeros((H, W), np.uint8)
    fmt = rng.choice(["qr", "dm"])
    n_codes = rng.choice([0, 1, 2, 4, 6, 8, 12, 16])
    # code side at 1/8 scale: modules (~20-50) x module px at full res (2.5-9) / 8
    for _ in range(n_codes):
        # 40% tiny codes (shrink-wrapped case from the 4096 x 2160 camera: 5-10 px here)
        r = rng.random()
        size = rng.uniform(3.0, 6.0) if r < 0.2 else rng.uniform(6.0, 10) if r < 0.45 else rng.uniform(10, 45)
        _paste_code(canvas, mask, rng, size, fmt)
    if rng.random() < 0.3:
        # plastic film haze over everything: lowers contrast
        h = rng.uniform(0.05, 0.35)
        canvas[:] = canvas * (1 - h) + h * rng.uniform(0.2, 0.8)
    if rng.random() < 0.35:
        _glare(canvas, rng, rng.randint(1, 5))
    img = camera(np.clip(canvas, 0, 3), rng)
    return img, mask


def darken_real(img_u8, rng):
    """Apply a random lower exposure to a real (already captured) image."""
    if rng.random() < 0.3:
        return img_u8
    return camera(img_u8.astype(np.float32) / 255.0, rng)


if __name__ == "__main__":
    import sys
    out = sys.argv[1] if len(sys.argv) > 1 else "synth_preview.png"
    rng = random.Random(1)
    tiles = []
    for i in range(8):
        im, m = scene(rng)
        vis = cv2.normalize(im, None, 0, 255, cv2.NORM_MINMAX)
        vis = cv2.cvtColor(vis, cv2.COLOR_GRAY2BGR)
        cnts, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        cv2.drawContours(vis, cnts, -1, (0, 255, 0), 1)
        tiles.append(vis)
    cv2.imwrite(out, np.vstack([np.hstack(tiles[:4]), np.hstack(tiles[4:])]))
    print("wrote", out)
