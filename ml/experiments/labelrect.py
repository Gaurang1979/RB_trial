"""Prototype: local perspective correction of one failing region. The code is printed on a paper
label; when the label is tilted or folded over a cap, the label outline (a bright quadrilateral)
gives the local perspective. Warp the label flat and decode it."""
import cv2, numpy as np, zxingcpp


def label_quads(crop):
    """bright label outlines in the crop, as 4-point polygons (largest first)"""
    s = cv2.GaussianBlur(crop, (0, 0), 1.5)
    _, bw = cv2.threshold(s, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    bw = cv2.morphologyEx(bw, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_RECT, (9, 9)))   # fill the code
    cnts, _ = cv2.findContours(bw, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    out = []
    for c in sorted(cnts, key=cv2.contourArea, reverse=True)[:3]:
        if cv2.contourArea(c) < 0.05 * crop.size: continue
        hull = cv2.convexHull(c)
        peri = cv2.arcLength(hull, True)
        for eps in (0.02, 0.04, 0.06):
            ap = cv2.approxPolyDP(hull, eps * peri, True)
            if len(ap) == 4: out.append(ap.reshape(4, 2).astype(np.float32)); break
        else:
            out.append(cv2.boxPoints(cv2.minAreaRect(c)).astype(np.float32))
    return out


def order(q):
    s = q.sum(1); d = np.diff(q, axis=1).ravel()
    return np.float32([q[np.argmin(s)], q[np.argmin(d)], q[np.argmax(s)], q[np.argmax(d)]])


def read_rectified(crop, fmt=zxingcpp.BarcodeFormat.QRCode):
    for q in label_quads(crop):
        q = order(q)
        w = int(max(np.linalg.norm(q[1] - q[0]), np.linalg.norm(q[2] - q[3])))
        h = int(max(np.linalg.norm(q[3] - q[0]), np.linalg.norm(q[2] - q[1])))
        side = max(w, h)
        for (W, H) in ((w, h), (side, side)):                         # as measured, and squared
            for up in (2, 3):
                dst = np.float32([[0, 0], [W * up, 0], [W * up, H * up], [0, H * up]])
                M = cv2.getPerspectiveTransform(q, dst)
                flat = cv2.warpPerspective(crop, M, (W * up, H * up), flags=cv2.INTER_CUBIC)
                flat = cv2.copyMakeBorder(flat, 20, 20, 20, 20, cv2.BORDER_REPLICATE)
                for b in (zxingcpp.Binarizer.LocalAverage, zxingcpp.Binarizer.GlobalHistogram):
                    r = zxingcpp.read_barcodes(flat, formats=fmt, binarizer=b, try_rotate=True, try_downscale=True)
                    if r: return r[0].text, q
    return None, None
