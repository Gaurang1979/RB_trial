"""ctypes binding to the C engine (libcodereader), for labelling and evaluation from Python."""

import ctypes
import os
import sys

import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

QR, DATAMATRIX, GS1_128, EAN13 = 1, 2, 4, 8
# cr_format flags (engine/include/codereader.h); the names match tools/bench and the hard-case sidecars
FORMATS = {"qr": QR, "dm": DATAMATRIX, "code128": GS1_128, "ean13": EAN13, "ean8": 0x10, "upca": 0x20, "upce": 0x40,
           "code39": 0x80, "code93": 0x100, "itf": 0x200, "codabar": 0x400, "databar": 0x800, "microqr": 0x1000,
           "pdf417": 0x2000, "aztec": 0x4000, "pharmacode": 0x8000, "pharmacode_rev": 0x10000, "dotcode": 0x20000,
           "rmqr": 0x40000}


class Params(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_int32),  # set by cr_default_params: keep it
        ("format", ctypes.c_int32), ("expected_count", ctypes.c_int32),
        ("time_budget_ms", ctypes.c_int32), ("threads", ctypes.c_int32),
        ("locator_scale", ctypes.c_int32), ("max_upscale", ctypes.c_int32),
        ("gamma", ctypes.c_float), ("locator_density", ctypes.c_float),
        ("full_pass", ctypes.c_int32),
        ("roi_x", ctypes.c_int32), ("roi_y", ctypes.c_int32), ("roi_w", ctypes.c_int32), ("roi_h", ctypes.c_int32),
        ("locator_mode", ctypes.c_int32), ("ai_threshold", ctypes.c_float), ("enhancer_mode", ctypes.c_int32),
        ("grade_quality", ctypes.c_int32), ("match_serials", ctypes.c_int32),
        ("hint_modules", ctypes.c_int32), ("hint_code_side_px", ctypes.c_float), ("hint_symbols_per_code", ctypes.c_float),
        ("min_length", ctypes.c_int32), ("max_length", ctypes.c_int32), ("check_digit", ctypes.c_int32),
        ("pharma_direction", ctypes.c_int32),
    ]


class Code(ctypes.Structure):
    _fields_ = [
        ("text", ctypes.c_char_p), ("format", ctypes.c_int32), ("step", ctypes.c_int32),
        ("x", ctypes.c_float * 4), ("y", ctypes.c_float * 4), ("copies", ctypes.c_int32),
    ]


class Quality(ctypes.Structure):
    _fields_ = [("measured", ctypes.c_int32)] + [(n, ctypes.c_float) for n in (
        "symbol_contrast", "modulation", "reflectance_margin", "fixed_pattern_damage", "axial_nonuniformity",
        "grid_nonuniformity", "unused_error_correction", "print_growth", "quiet_zone_dark", "module_size_px",
        "lighting_uniformity", "saturated")] + [(n, ctypes.c_int32) for n in (
        "grade_sc", "grade_mod", "grade_rm", "grade_fpd", "grade_an", "grade_gn", "grade_uec", "grade_qz", "overall",
        "calibrated")] + [("x_dim_mm", ctypes.c_float), ("linear", ctypes.c_int32)] + [(n, ctypes.c_float) for n in (
        "edge_contrast_min", "defects", "decodability", "r_min", "r_max")] + [(n, ctypes.c_int32) for n in (
        "scans", "decoded_scans", "grade_ec", "grade_defects", "grade_decodability")]

    def as_dict(self):
        return {k: getattr(self, k) for k, _ in self._fields_}


class Region(ctypes.Structure):
    _fields_ = [(n, ctypes.c_int32) for n in ("x", "y", "w", "h", "from_ai", "read")]


class Miss(ctypes.Structure):
    _fields_ = [("x", ctypes.c_int32), ("y", ctypes.c_int32), ("w", ctypes.c_int32), ("h", ctypes.c_int32)]


class MissInfo(ctypes.Structure):  # cr_miss_info: why a miss did not read
    REASONS = ("UNKNOWN", "CUT_OFF", "GLARE", "LOW_CONTRAST", "COVERED", "TOO_SMALL", "BLUR", "NO_FINDER")
    _fields_ = [(n, ctypes.c_int32) for n in ("x", "y", "w", "h", "reason", "measured")] + \
        [(n, ctypes.c_float) for n in ("sharpness", "contrast", "light", "dark", "saturated", "texture", "module_px",
                                       "learned_module_px")] + \
        [("finder", ctypes.c_int32), ("quiet_zone", ctypes.c_int32), ("ref_sharpness", ctypes.c_float),
         ("ref_contrast", ctypes.c_float)]

    def as_dict(self):
        d = {n: getattr(self, n) for n, _ in self._fields_}
        d["reason"] = self.REASONS[self.reason] if self.measured and 0 <= self.reason < len(self.REASONS) else "-"
        return d


class Stats(ctypes.Structure):
    _fields_ = [
        ("codes", ctypes.c_int32), ("expected", ctypes.c_int32), ("candidates", ctypes.c_int32),
        ("misses", ctypes.c_int32), ("locate_ms", ctypes.c_float), ("decode_ms", ctypes.c_float),
        ("total_ms", ctypes.c_float), ("timed_out", ctypes.c_int32), ("label_brightness", ctypes.c_float),
    ]


def _load():
    names = {"darwin": "libcodereader.dylib", "win32": "codereader.dll"}
    name = names.get(sys.platform, "libcodereader.so")
    for d in ("engine/build", "engine/build/Release"):
        p = os.path.join(ROOT, d, name)
        if os.path.exists(p):
            lib = ctypes.CDLL(p)
            break
    else:
        raise FileNotFoundError(f"{name} not found; build the engine first (see README)")
    lib.cr_default_params.argtypes = [ctypes.POINTER(Params)]
    lib.cr_create.argtypes = [ctypes.POINTER(Params)]
    lib.cr_create.restype = ctypes.c_void_p
    lib.cr_destroy.argtypes = [ctypes.c_void_p]
    lib.cr_decode.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int32, ctypes.c_int32, ctypes.c_int32]
    lib.cr_code_count.argtypes = [ctypes.c_void_p]
    lib.cr_get_code.argtypes = [ctypes.c_void_p, ctypes.c_int32, ctypes.POINTER(Code)]
    lib.cr_get_stats.argtypes = [ctypes.c_void_p, ctypes.POINTER(Stats)]
    lib.cr_load_model.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    lib.cr_load_enhancer.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    lib.cr_miss_count.argtypes = [ctypes.c_void_p]
    lib.cr_region_count.argtypes = [ctypes.c_void_p]
    lib.cr_get_region.argtypes = [ctypes.c_void_p, ctypes.c_int32, ctypes.POINTER(Region)]
    lib.cr_get_quality.argtypes = [ctypes.c_void_p, ctypes.c_int32, ctypes.POINTER(Quality)]
    lib.cr_get_miss.argtypes = [ctypes.c_void_p, ctypes.c_int32, ctypes.POINTER(Miss)]
    lib.cr_get_miss_info.argtypes = [ctypes.c_void_p, ctypes.c_int32, ctypes.POINTER(MissInfo)]
    lib.cr_last_error.argtypes = [ctypes.c_void_p]
    lib.cr_get_learned.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32]
    lib.cr_set_learned.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    lib.cr_last_error.restype = ctypes.c_char_p
    return lib


_lib = None


def lib():
    global _lib
    if _lib is None:
        _lib = _load()
    return _lib


def decode(gray: np.ndarray, fmt: int = QR, expected: int = 0, budget_ms: int = 2000, model: str = None,
           enhancer: str = None, **overrides):
    """Decode an 8-bit grey image. Returns (codes, stats); codes = [(text, quad 4x2 float array, step, copies)]."""
    gray = np.ascontiguousarray(gray, dtype=np.uint8)
    L = lib()
    p = Params()
    L.cr_default_params(ctypes.byref(p))
    p.format, p.expected_count, p.time_budget_ms = fmt, expected, budget_ms
    for k, v in overrides.items():
        setattr(p, k, v)
    r = L.cr_create(ctypes.byref(p))
    try:
        if model and L.cr_load_model(r, model.encode("utf-8")) != 0:
            raise RuntimeError(L.cr_last_error(r).decode())
        if enhancer and L.cr_load_enhancer(r, enhancer.encode("utf-8")) != 0:
            raise RuntimeError(L.cr_last_error(r).decode())
        L.cr_decode(r, gray.ctypes.data, gray.shape[1], gray.shape[0], gray.strides[0])
        out = []
        for i in range(L.cr_code_count(r)):
            c = Code()
            L.cr_get_code(r, i, ctypes.byref(c))
            quad = np.array([[c.x[k], c.y[k]] for k in range(4)], dtype=np.float32)
            out.append((c.text.decode("utf-8", "replace"), quad, c.step, c.copies))
        s = Stats()
        L.cr_get_stats(r, ctypes.byref(s))
        quality = []
        if getattr(p, "grade_quality", 0):
            for i in range(L.cr_code_count(r)):
                qq = Quality()
                L.cr_get_quality(r, i, ctypes.byref(qq))
                quality.append(qq.as_dict())
        s.quality = quality
        misses, why = [], []
        for i in range(L.cr_miss_count(r)):
            m = Miss()
            L.cr_get_miss(r, i, ctypes.byref(m))
            misses.append((m.x, m.y, m.w, m.h))
            mi = MissInfo()
            L.cr_get_miss_info(r, i, ctypes.byref(mi))
            why.append(mi.as_dict())
        s.miss_rects = misses
        s.miss_info = why
        regions = []
        for i in range(L.cr_region_count(r)):
            g = Region()
            L.cr_get_region(r, i, ctypes.byref(g))
            regions.append((g.x, g.y, g.w, g.h, bool(g.from_ai), bool(g.read)))
        s.regions = regions
        return out, s
    finally:
        L.cr_destroy(r)


def load_truth(path=os.path.join(ROOT, "truth", "samples.csv")):
    """file -> (format, expected)"""
    truth = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or line.startswith("file,"):
                continue
            parts = line.rstrip("\n").split(",")
            if len(parts) >= 3:
                truth[parts[0]] = (FORMATS.get(parts[1], QR), int(parts[2]))
    return truth
