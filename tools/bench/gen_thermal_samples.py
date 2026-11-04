#!/usr/bin/env python3
"""
gen_thermal_samples.py - Generates realistic synthetic thermal packaging test images in standard 8-bit BMP format.
Zero external dependencies (pure standard Python library).

Generated sets:
1. Bottle Induction Cap Sealing:
   - bottle_seal_good.bmp
   - bottle_seal_missing_foil.bmp
   - bottle_seal_incomplete_gap.bmp
   - bottle_seal_overheated.bmp
2. Flexible Pouch & Tray Heat Seal:
   - pouch_seam_good.bmp
   - pouch_seam_void_leak.bmp
   - pouch_seam_contamination.bmp
3. Hot Melt Adhesive (Glue) Carton Flaps:
   - hot_glue_good_2beads.bmp
   - hot_glue_missing_bead.bmp
   - hot_glue_broken_gap.bmp
"""

import os
import math
import struct

def write_gray_bmp(filepath, width, height, pixels):
    """
    Writes an 8-bit grayscale BMP file.
    pixels: list of ints (0..255) of length width * height, row-major (top to bottom).
    """
    row_stride = (width + 3) & ~3
    image_size = row_stride * height
    file_size = 14 + 40 + 1024 + image_size

    # 1. BMP File Header (14 bytes)
    file_header = struct.pack(
        "<2sIHHI",
        b"BM",
        file_size,
        0, 0,
        14 + 40 + 1024 # Offset to pixel data
    )

    # 2. DIB Header (BITMAPINFOHEADER, 40 bytes)
    dib_header = struct.pack(
        "<IIIHHIIIIII",
        40,              # Header size
        width,
        height,          # Positive = bottom-to-top
        1,               # Color planes
        8,               # Bits per pixel
        0,               # Compression (BI_RGB)
        image_size,
        2835, 2835,      # Pixels per meter (~72 DPI)
        256, 256         # Color table entries
    )

    # 3. Grayscale Color Table (1024 bytes: B, G, R, 0)
    color_table = bytearray()
    for i in range(256):
        color_table.extend((i, i, i, 0))

    # 4. Pixel data (BMP is stored bottom-to-top)
    pad = b"\x00" * (row_stride - width)
    pixel_data = bytearray()
    for y in reversed(range(height)):
        row = bytes(pixels[y * width : (y + 1) * width])
        pixel_data.extend(row)
        pixel_data.extend(pad)

    with open(filepath, "wb") as f:
        f.write(file_header)
        f.write(dib_header)
        f.write(color_table)
        f.write(pixel_data)

def ensure_dir(path):
    os.makedirs(path, exist_ok=True)

def gen_bottle_samples(out_dir):
    w, h = 320, 320
    cx, cy = 160.0, 160.0
    r_in, r_out = 70.0, 110.0

    # 1. Good seal (78 C uniform ring, mapped on 20..120 C scale: p = 148)
    good = [10] * (w * h)
    for y in range(h):
        for x in range(w):
            d = math.hypot(x - cx, y - cy)
            if r_in <= d <= r_out:
                good[y * w + x] = 148
    write_gray_bmp(os.path.join(out_dir, "bottle_seal_good.bmp"), w, h, good)

    # 2. Missing foil (all cold ambient 24 C: p = 10)
    missing = [10] * (w * h)
    write_gray_bmp(os.path.join(out_dir, "bottle_seal_missing_foil.bmp"), w, h, missing)

    # 3. Incomplete seal (45 deg cold gap)
    gap = list(good)
    for y in range(h):
        for x in range(w):
            d = math.hypot(x - cx, y - cy)
            if r_in <= d <= r_out:
                ang = math.degrees(math.atan2(y - cy, x - cx))
                if ang < 0: ang += 360
                if 45 <= ang <= 90:
                    gap[y * w + x] = 10
    write_gray_bmp(os.path.join(out_dir, "bottle_seal_incomplete_gap.bmp"), w, h, gap)

    # 4. Overheated (112 C scorch: p = 234)
    overheated = [10] * (w * h)
    for y in range(h):
        for x in range(w):
            d = math.hypot(x - cx, y - cy)
            if r_in <= d <= r_out:
                overheated[y * w + x] = 234
    write_gray_bmp(os.path.join(out_dir, "bottle_seal_overheated.bmp"), w, h, overheated)

def gen_pouch_samples(out_dir):
    w, h = 400, 120
    # Good seam (y=45..75, width 30 at 85 C: p = 118 on 20..160 C)
    good = [10] * (w * h)
    for y in range(45, 75):
        for x in range(w):
            good[y * w + x] = 118
    write_gray_bmp(os.path.join(out_dir, "pouch_seam_good.bmp"), w, h, good)

    # Void leak (cold channel at x=200..216)
    void_leak = list(good)
    for y in range(45, 75):
        for x in range(200, 216):
            void_leak[y * w + x] = 10
    write_gray_bmp(os.path.join(out_dir, "pouch_seam_void_leak.bmp"), w, h, void_leak)

    # Contamination (cold product trapped at x=280..305, y=55..75)
    contamination = list(good)
    for y in range(55, 75):
        for x in range(280, 305):
            contamination[y * w + x] = 40
    write_gray_bmp(os.path.join(out_dir, "pouch_seam_contamination.bmp"), w, h, contamination)

def gen_glue_samples(out_dir):
    w, h = 300, 160
    # Two beads at y=50 and y=110, length x=30..270 at 130 C: p = 175
    good = [10] * (w * h)
    for dy in range(-3, 4):
        for x in range(30, 270):
            good[(50 + dy) * w + x] = 175
            good[(110 + dy) * w + x] = 175
    write_gray_bmp(os.path.join(out_dir, "hot_glue_good_2beads.bmp"), w, h, good)

    # Missing bead (only y=50 exists)
    missing = [10] * (w * h)
    for dy in range(-3, 4):
        for x in range(30, 270):
            missing[(50 + dy) * w + x] = 175
    write_gray_bmp(os.path.join(out_dir, "hot_glue_missing_bead.bmp"), w, h, missing)

    # Broken bead (gap at x=130..160 on top bead)
    broken = list(good)
    for dy in range(-3, 4):
        for x in range(130, 160):
            broken[(50 + dy) * w + x] = 10
    write_gray_bmp(os.path.join(out_dir, "hot_glue_broken_gap.bmp"), w, h, broken)

def gen_blister_samples(out_dir):
    w, h = 240, 140
    # Good blister card (flange at 80 C: p = 153 on 20..120 C)
    card = [153] * (w * h)
    margin_x = (w - (3 * 70.0)) * 0.5
    margin_y = (h - (2 * 50.0)) * 0.5

    for r in range(2):
        for c in range(3):
            pc_x = int(margin_x + c * 70.0 + 35.0)
            pc_y = int(margin_y + r * 50.0 + 25.0)
            for dy in range(-10, 11):
                for dx in range(-15, 16):
                    card[(pc_y + dy) * w + (pc_x + dx)] = 12 # Cold tablet pocket (25 C)

    write_gray_bmp(os.path.join(out_dir, "blister_seal_good.bmp"), w, h, card)

    # Leaking cavity (pocket 1 has leak channel to edge)
    breach = list(card)
    leak_cx = int(margin_x + 1 * 70.0 + 35.0)
    for y in range(45):
        for dx in range(-4, 5):
            breach[y * w + (leak_cx + dx)] = 12
    write_gray_bmp(os.path.join(out_dir, "blister_seal_leak.bmp"), w, h, breach)

def main():
    target_dir = os.path.join(os.path.dirname(__file__), "../../truth/thermal")
    ensure_dir(target_dir)
    print(f"Generating synthetic thermal test samples into: {target_dir}")
    gen_bottle_samples(target_dir)
    gen_pouch_samples(target_dir)
    gen_glue_samples(target_dir)
    gen_blister_samples(target_dir)
    print("Thermal BMP samples generated successfully with zero dependencies.")

if __name__ == "__main__":
    main()
