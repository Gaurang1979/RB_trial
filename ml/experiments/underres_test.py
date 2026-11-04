"""Synthetic test of underres.py: version 1 codes at 1.0 - 2.0 px per module, rotated, blurred,
noisy, the starting homography off by up to `jitter` px per corner. Also random images (must give
nothing). Usage: python underres_test.py [count] [noise sd] [jitter]"""
import sys, os, time; sys.path.insert(0, os.path.dirname(__file__))
import numpy as np, cv2
from qrcodegen import QrCode, QrSegment
import underres


def synth(text, mod, ang, rng, noise, blur=0.5, light=24.0, dark=4.0, ecl=QrCode.Ecc.MEDIUM, up=16):
    q = QrCode.encode_segments(QrSegment.make_segments(text), ecl, 1, 1, -1, False)
    n = q._size
    bits = np.array([[q._modules[y][x] for x in range(n)] for y in range(n)], np.float32)
    W = 64; c = W / 2 + rng.uniform(-0.5, 0.5, 2)
    a = np.deg2rad(ang); R = np.array([[np.cos(a), -np.sin(a)], [np.sin(a), np.cos(a)]]) * mod
    H = np.eye(3); H[:2, :2] = R; H[:2, 2] = c - R @ [n / 2, n / 2]     # module -> pixel (pixel centres at integers)
    # render at up x: pixel centre i covers [i - 0.5, i + 0.5]
    S = np.diag([up, up, 1.0]); S[:2, 2] = up * 0.5 - 0.5
    Hu = S @ H @ np.array([[1, 0, 0.5], [0, 1, 0.5], [0, 0, 1.0]])   # bits pixel j covers modules [j, j + 1]
    big = cv2.warpPerspective(bits, Hu, (W * up, W * up), flags=cv2.INTER_NEAREST, borderValue=0)
    img = cv2.resize(big, (W, W), interpolation=cv2.INTER_AREA)
    if blur > 0: img = cv2.GaussianBlur(img, (0, 0), blur)
    img = light - (light - dark) * img + rng.normal(0, noise, img.shape)
    return np.clip(np.round(img), 0, 255), H


def jittered(H, n, j, rng):
    src = np.float32([[0, 0], [n, 0], [n, n], [0, n]])
    q = cv2.perspectiveTransform(src[None].astype(np.float64), H)[0] + rng.uniform(-j, j, (4, 2))
    return cv2.getPerspectiveTransform(src, np.float32(q)).astype(np.float64)


if __name__ == "__main__":
    N = int(sys.argv[1]) if len(sys.argv) > 1 else 20
    noise = float(sys.argv[2]) if len(sys.argv) > 2 else 1.5
    jit = float(sys.argv[3]) if len(sys.argv) > 3 else 0.5
    rng = np.random.default_rng(1)
    for mod in (2.0, 1.75, 1.5, 1.25, 1.0):
        ok = wrong = 0; t0 = time.time()
        for i in range(N):
            text = f"1BRGW3800788{rng.integers(0, 10**5):05d}"
            img, H = synth(text, mod, rng.uniform(0, 90), rng, noise)
            t = underres.decode(img, jittered(H, 21, jit, rng))
            ok += t == text; wrong += bool(t) and t != text
        print(f"{mod:.2f} px/module noise {noise}: {ok}/{N} read, {wrong} wrong ({(time.time() - t0) / N:.1f} s each)", flush=True)
    # random images with the same statistics: nothing may come out
    bad = 0
    for i in range(N):
        img = np.clip(np.round(14 + rng.normal(0, 6, (64, 64))), 0, 255)
        img = cv2.GaussianBlur(img, (0, 0), 0.8)
        H = np.eye(3); H[:2, :2] *= 1.6; H[:2, 2] = 15
        bad += underres.decode(img, H) is not None
    print(f"random images: {bad}/{N} gave a text")
