# SDK and integration API

This document specifies how other programs use VisionStudio tools without the vision
application: first the Vision Package (section 0), the one full package that other applications
such as the aggregation software load; then the parts it is built from: the native C API (one DLL per parent tool on a shared `vs_core.dll`), the .NET and
Python wrappers, `.vstool` files exported from the GUI, the job runner and the Remote API for
talking to a running vision application. It implements
[section 10 of the requirements](../REQUIREMENTS-VISION-SYSTEM.md#10-vision-package-for-other-applications-sdk)
and generalises the conventions of today's `engine/include/codereader.h`. The process layout is
in [ARCHITECTURE.md](ARCHITECTURE.md), the tools' parameters and results in
[TOOL-SPECIFICATIONS.md](TOOL-SPECIFICATIONS.md), licence checks in [LICENSING.md](LICENSING.md),
the job container in [JOB-FILE-FORMAT.md](JOB-FILE-FORMAT.md).

## 0. The Vision Package (owner, 2026-10-30)

Other applications, first the owner's aggregation software, use the reader as one full package,
not as separate tool DLLs (requirements section 10). The tool libraries, wrappers and `.vstool`
files of sections 1 to 10 are what the package is built from; a host application references only
the package.

### 0.1 Contents

```
VisionStudio.Package.<version>.nupkg
  lib/net8.0/VisionStudio.Package.dll       managed API the host calls (section 0.3), for .NET 8 hosts
  lib/net10.0/VisionStudio.Package.dll      the same API for .NET 10 hosts
  worker/vs-runtime.exe                     the runtime program, started by the package in private mode
  worker/*.dll                              vs_core, vs_code, vs_ocr, ... and the package's own copies
                                            of OpenCV, ONNX Runtime, zxing-cpp, libdmtx, camera SDKs
  models/*.vsmodel                          AI models, encrypted (section 0.5)
  selftest/                                 synthetic images and their expected answers
  manifest.json                             version, API level, every file with its SHA-256
  manifest.sig                              Ed25519 signature over manifest.json (company key)
  RELEASE-NOTES.md                          new features, changed defaults, licences they need
```

The first host, the owner's aggregation software, is written in .NET (C#) (owner, 2026-10-30), so
the package ships for .NET only. A C API for hosts in other languages (`vs_package.dll`, `vp_*`
functions over the same worker) is built later, on request. The aggregation software runs on .NET 8
or newer (owner, 2026-10-30), so the client library is built for `net8.0` and `net10.0`; NuGet picks
the right one. The `net8.0` build stays only until the aggregation software moves to .NET 10 (.NET 8
support ends 2026-11-10). The worker is published self-contained on .NET 10, so the host's .NET
version never reaches the reading.

### 0.2 Process model

| Part | Runs in | Does |
| --- | --- | --- |
| `VisionStudio.Package.dll` | The host's process | Checks the package signature, starts and watches the worker, turns calls into requests on the worker's pipe, raises result and frame events |
| `vs-runtime.exe` in private mode | Its own process, started by the package | Cameras, products, reading, AI, licence client, learned state, duplicates. The same program as the runtime service ([ARCHITECTURE.md](ARCHITECTURE.md) section 2) with no Windows service, no web pages and no Remote API port: one named pipe whose ACL admits only the host's user, speaking `visionstudio.remote.v1` plus the package's setup calls |
| Live frames | Shared memory, as for Vision Studio | Downscaled frames with graphics for the host's screen; full images on request |

If the worker stops or stops answering (2 s, as the Vision Studio link), the case in flight is
NG with reason "reader stopped", the package raises an event, restarts the worker, reloads the
product and reports ready again. The host process is never taken down by a fault in native code.
Because the worker loads only its own libraries from its own folder, the host's OpenCV, ONNX
Runtime or camera SDK versions never meet the package's.

### 0.3 .NET API sketch

```csharp
using VisionStudio.Package;

await using var vp = await VisionPackage.OpenAsync(new PackageOptions {
    DataFolder    = @"C:\ProgramData\MyAggregation\vision",  // products, learned state, logs
    LicenceServer = "https://licence.example.com",
    LicenceKey    = key });

LicenceStatus lic = vp.Licence.Status;            // Active, Grace(days), NotLicensed(reason)

IReadOnlyList<CameraInfo> cams = vp.Cameras.List();          // model, serial, name, interface
await vp.Cameras.AssignAsync(CameraRole.Top,  "DA1234567");  // child codes
await vp.Cameras.AssignAsync(CameraRole.Side, "DA7654321");  // parent code

await vp.Products.SaveAsync(new Product {
    Name = "Case24", CodeType = CodeType.DataMatrix, CodesPerCase = 24,
    Gtin = "08901234567893", ParentCodeType = CodeType.Code128,
    Camera = new CameraSettings { ExposureUs = 900, Gain = 4 } }, user: "supervisor1");
await vp.Products.ImportAsync(@"D:\from-vision-studio\Case24.vsprod");   // teach and tuning

vp.Line.CaseRead += (_, r) => Save(r.Pass, r.Codes, r.Parent, r.NgReason, r.ProductVersion);
await vp.Line.StartAsync("Case24", new() { ["Batch"] = "B123" });
CaseResult one = await vp.Line.TriggerAsync(partId: "P-0001");          // or the host's PLC signal

byte[] png = await vp.Images.GetAsync(one.ImageId, withGraphics: true);
CaseResult offline = await vp.ReadAsync(myImage, "Case24");              // no camera
```

Every call that changes cameras or products is refused while the line runs, checked (known code
type, count in range, GTIN check digit), versioned and logged with the user name the host
passes. Teach, tuning, AI models, calibration and users are not in this API: they come from
Vision Studio as an encrypted product file (owner, 2026-10-30).

### 0.4 Updates

| Step | Rule |
| --- | --- |
| Build | Each Vision_System release builds the package; every new feature is in it. The owner exports it and adds it to the host application |
| Delivery | The host application delivers the package to its locations and decides when each one updates. The package never downloads or installs anything by itself |
| Install | `VisionPackage.InstallAsync(path)` checks `manifest.sig` against the company public key compiled into the loader and every file's SHA-256, then unpacks to `versions/<version>/` beside the current one. A changed or unsigned package is refused |
| Switch | `OpenAsync` uses the version the host names, or the newest installed; a switch happens only at open, so never during a batch |
| Data | On first open a newer version copies the data folder to its own version and migrates it there (product schema, learned state); the old data stays for rollback |
| Self-test | On first open the worker reads `selftest/` and loads every licensed AI model against its reference output. A failure refuses Start with the reason, and the host rolls back |
| Rollback | `OpenAsync(version: previous)` |
| Compatibility | Semantic versioning; within a major version the API only grows (section 12) |

### 0.5 Licence and encryption

The worker uses the same licence client as Vision Studio (`dotnet/VisionCore/Licensing`) against
the ERPNext licence service ([LICENCE-SERVER-ERPNEXT.md](LICENCE-SERVER-ERPNEXT.md)), sending the
licence key, the installation id, the package version and the camera serials. Until a good answer
arrives, no tool or camera opens and nothing is decrypted. AI models, teach and tuning data,
learned state and product files from Vision Studio are encrypted (AES-256-GCM); their key comes
only inside the signed ERPNext answer, encrypted for this installation's own key pair, which is
made at first start and kept with Windows DPAPI. Details in [LICENSING.md](LICENSING.md) section 7.4.

## 1. Principles

| Principle | Rule | Carried over from `codereader.h` |
| --- | --- | --- |
| Stable C ABI | Every DLL exports plain C functions with `extern "C"` and the platform's default calling convention (`__cdecl` on x64 Windows). No C++ types, STL or exceptions cross the boundary | Yes (`CR_API`, `extern "C"`) |
| Opaque handles | Each tool instance, image buffer, calibration and job is an opaque pointer created and destroyed by the library | Yes (`cr_reader*`) |
| Status codes plus message | Every function that can fail returns `vs_status` (0 = OK, negative = error). `vs_last_error(handle)` gives an English message; the stable code is what callers test | Partly: `cr_decode` returned -1/-2 and `cr_last_error`. Now one shared enum |
| Never throw across the DLL | Every exported function catches all C++ exceptions and turns them into `VS_E_INTERNAL` with a message. Out-of-memory becomes `VS_E_NO_MEMORY` | Yes |
| Thread safety per instance | Different handles may be used from different threads at once. One handle must not be used from two threads at once. Tools may use threads internally (the `threads` parameter) | Yes, same rule |
| Zero-copy images | Images are passed as a view: pointer, width, height, stride, pixel format. The library never keeps the pointer after the call returns | Yes (`pixels`, `stride`) |
| Defaults first | Every parameter struct has a `vs_<tool>_default_params` function; callers change only what they need | Yes (`cr_default_params`) |
| Extensible structs | Every parameter and result struct starts with `uint32_t struct_size`. The library reads only the fields the caller's size covers, so new fields can be added at the end within a major version | New. `cr_params` had no size field, so every new field broke old P/Invoke layouts |
| Result lifetime | Strings and arrays returned by getters stay valid until the next run or destroy on the same handle | Yes |
| Semantic versioning | `MAJOR.MINOR.PATCH`. No breaking ABI change within a major version (section 9) | New |
| Same results as the app | The vision application uses exactly these DLLs. CPU results are bit-identical; GPU results match within the tolerance stated per tool | New, required by section 10 of the requirements |
| UTF-8 everywhere | Paths and strings are UTF-8 `const char*`. On Windows the library converts paths to UTF-16 internally | Yes (`cr_load_model`) |
| Coordinates | Pixels, origin at the top-left pixel's centre, x right, y down, angles in degrees counter-clockwise as seen on screen. World units (mm) only when a calibration is attached | New, made explicit |

## 2. Libraries

| DLL | Header | Content | Licence |
| --- | --- | --- | --- |
| `vs_core.dll` | `vs_core.h` | Types, status codes, image buffers, graphics, licence checks (through `vs_lic.dll`), logging, `.vstool` load/save, generic tool functions | Always (Base) |
| `vs_base.dll` | `vs_base.h` | Free base tools: filters, threshold, morphology, histogram, polar unwrap, flat-field | Base |
| `vs_locate.dll` | `vs_locate.h` | Correlation and geometric match, fixture | `VS-LOC` |
| `vs_measure.dll` | `vs_measure.h` | Caliper, find line/circle/ellipse/corner, geometry | `VS-MEA` |
| `vs_calib.dll` | `vs_calib.h` | Checkerboard, dot grid, N-point, hand-eye 2D | `VS-CAL` |
| `vs_blob.dll` | `vs_blob.h` | Blob, contour, count | `VS-BLB` |
| `vs_code.dll` | `vs_code.h` | 1D/2D code reading (today's engine) | `VS-COD` |
| `vs_verify.dll` | `vs_verify.h` | ISO 15415/15416/29158 grading | `VS-VER` |
| `vs_ocr.dll` | `vs_ocr.h` | OCR, OCV, format validation | `VS-OCR` |
| `vs_inspect.dll` | `vs_inspect.h` | Presence, golden template, flaws, label | `VS-INS` |
| `vs_colour.dll` | `vs_colour.h` | Colour match, segment, ΔE | `VS-COL` |
| `vs_dl.dll` | `vs_dl.h` | Anomaly, classification, detection, segmentation, AI OCR | `VS-DL` |
| `vs_multicam.dll` | (used by `vs_job.dll`) | Multi-camera jobs, stitching | `VS-MCE` |
| `vs_job.dll` | `vs_job.h` | Job runner (load a `.vsjob` and run it in-process) | Licences of the tools in the job; multi-camera jobs need `VS-MCE` |

All DLLs are installed always; a tool DLL that is present but not licensed loads and then
refuses to create tools with `VS_E_NOT_LICENSED`. "Loaded only if licensed" in the requirements
means the vision application does not load unlicensed DLLs into its runtime; SDK callers may
link to any of them and get a clear status.

Spelling note: identifiers use British spelling where the requirements do (`vs_colour.h`,
`VS_E_LICENCE_*`), so the API reads the same as the documentation.

## 3. `vs_core.h`

A sketch, complete enough to review the ABI. Final names may change before 1.0; after 1.0 they
are fixed for the major version.

```c
/*
 * vs_core.h - common C API of the VisionStudio tool libraries.
 *
 * Every tool DLL (vs_ocr.dll, vs_measure.dll, ...) uses the types here: an image view, a region
 * of interest, a pose, a status code with a message, and graphics to draw on the image. The
 * vision application uses the same DLLs, so a tool gives the same result in both.
 *
 * Threading: one handle must not be used from two threads at once. Different handles may be
 * used from different threads. Tools use several threads internally (vs_tool_set_threads).
 *
 * Structs: every struct the caller fills starts with struct_size. Set it to sizeof(the struct)
 * (the *_default_* functions do this), so newer DLLs can add fields at the end.
 */
#ifndef VS_CORE_H
#define VS_CORE_H

#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_CORE_BUILD
#    define VS_API __declspec(dllexport)
#  else
#    define VS_API __declspec(dllimport)
#  endif
#else
#  define VS_API __attribute__((visibility("default")))
#endif

#if defined(__GNUC__) || defined(__clang__)
#  define VS_DEPRECATED(msg) __attribute__((deprecated(msg)))
#elif defined(_MSC_VER)
#  define VS_DEPRECATED(msg) __declspec(deprecated(msg))
#else
#  define VS_DEPRECATED(msg)
#endif

#define VS_ABI_MAJOR 1   /* changes only with a breaking change */

#ifdef __cplusplus
extern "C" {
#endif

/* Status of a call. 0 = OK; negative = failed (see vs_last_error); positive = OK with a note. */
typedef enum vs_status {
    VS_OK = 0,
    VS_WARN_TIMEOUT = 1,          /* stopped at the time budget; results so far are valid */
    VS_WARN_PARTIAL = 2,          /* some results are missing (e.g. fewer edges than asked) */
    VS_E_ARG = -1,                /* bad argument: null pointer, size out of range */
    VS_E_INTERNAL = -2,           /* bug caught inside the library; message says where */
    VS_E_NO_MEMORY = -3,
    VS_E_IMAGE_FORMAT = -4,       /* this tool does not accept this pixel format */
    VS_E_NOT_TRAINED = -5,        /* run before train/teach (e.g. pattern not taught) */
    VS_E_WRONG_TOOL = -6,         /* tool-specific call on a handle of another tool kind */
    VS_E_FILE = -7,               /* file missing, unreadable or not writable */
    VS_E_FORMAT = -8,             /* file is not a valid .vstool / .vsjob / model */
    VS_E_VERSION = -9,            /* file needs a newer library */
    VS_E_NOT_LICENSED = -10,      /* tool's parent licence not active; message names the serial prefix */
    VS_E_LICENCE_EXPIRED = -11,
    VS_E_LICENCE_BINDING = -12,   /* licence belongs to another PC */
    VS_E_DONGLE_MISSING = -13,
    VS_E_CANCELLED = -14,         /* vs_tool_cancel was called */
    VS_E_DEVICE = -15,            /* GPU or inference device not available */
    VS_E_NOT_INITIALISED = -16    /* vs_init not called */
} vs_status;

/* Pixel formats. Packed formats are interleaved; stride is in bytes. */
typedef enum vs_pixel_format {
    VS_PIX_MONO8 = 1,
    VS_PIX_MONO16 = 2,            /* 10/12/16-bit cameras, LSB aligned; bits in vs_image.bits */
    VS_PIX_RGB8 = 3,
    VS_PIX_BGR8 = 4,              /* OpenCV and Windows bitmaps */
    VS_PIX_BGRA8 = 5,
    VS_PIX_BAYER_RG8 = 10,        /* demosaiced by tools that need colour */
    VS_PIX_BAYER_GR8 = 11,
    VS_PIX_BAYER_GB8 = 12,
    VS_PIX_BAYER_BG8 = 13,
    VS_PIX_FLOAT32 = 20           /* one channel, for results of filters */
} vs_pixel_format;

/*
 * A view on pixels owned by the caller. Not copied: the pointer must stay valid until the call
 * that receives it returns. stride = bytes from one row to the next (>= width * bytes per pixel;
 * may be negative for bottom-up bitmaps).
 */
typedef struct vs_image {
    const void* data;
    int32_t     width;
    int32_t     height;
    int64_t     stride;
    int32_t     format;           /* vs_pixel_format */
    int32_t     bits;             /* significant bits for MONO16; 0 = 16 */
    int64_t     timestamp_ns;     /* camera timestamp (PTP when available); 0 = unknown */
    int32_t     camera;           /* camera index in a job; 0 for SDK callers */
    int32_t     reserved;
} vs_image;

/* A point and a 2D pose (rigid transform plus scale) of a found part or fixture. */
typedef struct vs_point { double x, y; } vs_point;

typedef struct vs_pose {
    double x, y;                  /* translation, pixels (or mm when calibrated) */
    double angle_deg;             /* counter-clockwise on screen */
    double scale;                 /* 1 = taught size */
    double skew_deg;              /* 0 unless the tool fits an affine pose */
} vs_pose;

/* Region of interest. Defined in the image or relative to a fixture pose (vs_tool_run's pose). */
typedef enum vs_roi_kind {
    VS_ROI_FULL = 0,              /* whole image */
    VS_ROI_RECT = 1,              /* axis-aligned: x, y, w, h */
    VS_ROI_ROTRECT = 2,           /* centre cx, cy, size w, h, angle_deg */
    VS_ROI_CIRCLE = 3,            /* cx, cy, r1 */
    VS_ROI_ANNULUS = 4,           /* cx, cy, r1 (inner), r2 (outer), angle_deg start, sweep_deg */
    VS_ROI_POLYGON = 5            /* points / point_count */
} vs_roi_kind;

typedef struct vs_roi {
    uint32_t  struct_size;
    int32_t   kind;               /* vs_roi_kind */
    double    x, y, w, h;         /* RECT; for ROTRECT x, y are the centre */
    double    angle_deg, sweep_deg;
    double    r1, r2;
    const vs_point* points;       /* POLYGON; copied by the tool when set */
    int32_t   point_count;
    int32_t   fixtured;           /* 1 = coordinates are relative to the pose given at run time */
} vs_roi;

/* The part every tool's result has: pass/fail first, then confidence and time. */
typedef struct vs_result {
    uint32_t struct_size;
    int32_t  status;              /* vs_status of the run */
    int32_t  pass;                /* 1 = pass, 0 = fail, -1 = not judged (no limits set) */
    float    confidence;          /* 0..100, same meaning for every tool (see TOOL-SPECIFICATIONS.md) */
    float    time_ms;             /* wall time of the run */
    int32_t  reason;              /* tool-specific fail reason code; 0 = none */
    const char* reason_text;      /* plain-English reason for the explain view; UI translates by reason code */
} vs_result;

/* Graphics the tool draws on the image (the app and SDK samples draw the same thing). */
typedef enum vs_graphic_kind {
    VS_GFX_LINE = 1, VS_GFX_RECT = 2, VS_GFX_ROTRECT = 3, VS_GFX_CIRCLE = 4,
    VS_GFX_POLYLINE = 5, VS_GFX_CROSS = 6, VS_GFX_TEXT = 7, VS_GFX_ARROW = 8
} vs_graphic_kind;

typedef enum vs_graphic_role {
    VS_ROLE_INFO = 0,             /* drawn neutral (search region, construction lines) */
    VS_ROLE_PASS = 1,             /* green plus tick */
    VS_ROLE_FAIL = 2,             /* red plus cross */
    VS_ROLE_WARN = 3              /* amber plus "!" */
} vs_graphic_role;

typedef struct vs_graphic {
    int32_t kind;                 /* vs_graphic_kind */
    int32_t role;                 /* vs_graphic_role: the colour comes from the theme, not the tool */
    double  x1, y1, x2, y2;       /* LINE, ARROW, RECT; centre/size for ROTRECT; CIRCLE uses x1, y1, x2 = r */
    double  angle_deg;
    const vs_point* points;       /* POLYLINE */
    int32_t point_count;
    const char* text;             /* TEXT: UTF-8, already in the caller's language when a key was given */
} vs_graphic;

/* Process set-up. Call once before anything else; safe to call again (counted). */
typedef struct vs_init_params {
    uint32_t    struct_size;
    const char* licence_path;     /* NULL = the PC's licence file (LICENSING.md section 4.1) */
    const char* log_path;         /* NULL = no file log */
    int32_t     log_level;        /* 0 off, 1 error, 2 warn, 3 info, 4 debug */
    int32_t     threads;          /* default threads per tool; 0 = all cores */
    int32_t     device;           /* 0 = CPU, 1 = GPU if available (AI tools); results on CPU are the reference */
} vs_init_params;

VS_API void        vs_default_init_params(vs_init_params* p);
VS_API vs_status   vs_init(const vs_init_params* p);
VS_API void        vs_shutdown(void);

/* Versions: library "1.4.2" and the ABI major it implements. */
VS_API const char* vs_version(void);
VS_API int32_t     vs_abi_major(void);
/* sizeof a public struct as this DLL was built ("vs_ocr_params"); 0 = unknown name. Wrappers
   compare it with their own layout at start-up. */
VS_API int32_t     vs_struct_size(const char* struct_name);

/*
 * Licence of one parent tool, by tool code ("OCR", "MEA", ...). Does not activate anything.
 * state: 0 active, 1 trial, 2 expiring (<= 14 days), 3 expired, 4 locked, 5 binding mismatch,
 * 6 clock problem, 7 dongle missing.
 */
typedef struct vs_licence_info {
    uint32_t struct_size;
    int32_t  state;
    int32_t  days_left;           /* -1 = perpetual */
    int32_t  watermark;           /* 1 = trial watermark on saved images */
    char     serial_prefix[16];   /* "VS-OCR-" for messages */
} vs_licence_info;

VS_API vs_status   vs_licence_query(const char* tool_code, vs_licence_info* out);

/* Owned image buffer, for callers that have no pixels of their own (file load, tool output). */
typedef struct vs_buffer vs_buffer;
VS_API vs_buffer*  vs_buffer_create(int32_t width, int32_t height, int32_t format);
VS_API vs_status   vs_buffer_load(const char* path, vs_buffer** out);   /* BMP, PNG, TIFF */
VS_API void        vs_buffer_view(const vs_buffer* b, vs_image* out);   /* view stays valid until destroy */
VS_API void        vs_buffer_destroy(vs_buffer* b);

/*
 * Generic tool handle. Every tool DLL returns a vs_tool*; tool-specific calls check the kind and
 * return VS_E_WRONG_TOOL for another kind. Create functions live in the tool's header.
 */
typedef struct vs_tool vs_tool;

VS_API void        vs_tool_destroy(vs_tool* t);
VS_API const char* vs_tool_kind(const vs_tool* t);           /* "ocr", "caliper", "code", ... */
VS_API const char* vs_last_error(const vs_tool* t);          /* message of the last failed call on t */
VS_API const char* vs_thread_last_error(void);               /* message when there is no handle yet (create/load) */

/* Load a tool saved from the vision application (.vstool). Picks the right DLL by kind. */
VS_API vs_tool*    vs_tool_load(const char* path, vs_status* status);
VS_API vs_status   vs_tool_save(const vs_tool* t, const char* path);

/* Parameters by name as JSON, for generic UIs and scripts (typed structs are in each header). */
VS_API int32_t     vs_tool_get_params_json(const vs_tool* t, char* buf, int32_t cap); /* size incl. NUL */
VS_API vs_status   vs_tool_set_params_json(vs_tool* t, const char* json);

/* Run on one image. pose = fixture from a locator (NULL = image frame). */
VS_API vs_status   vs_tool_run(vs_tool* t, const vs_image* img, const vs_pose* pose);
VS_API vs_status   vs_tool_get_result(const vs_tool* t, vs_result* out);

/* Graphics of the last run. Valid until the next run or destroy. */
VS_API int32_t     vs_graphics_count(const vs_tool* t);
VS_API vs_status   vs_get_graphic(const vs_tool* t, int32_t index, vs_graphic* out);

/* Calibration from vs_calib.dll: results in mm from then on. NULL detaches. */
typedef struct vs_calibration vs_calibration;
VS_API vs_status   vs_tool_set_calibration(vs_tool* t, const vs_calibration* c);

/* Ask a running vs_tool_run on another thread to stop soon (returns VS_E_CANCELLED there). */
VS_API void        vs_tool_cancel(vs_tool* t);
VS_API vs_status   vs_tool_set_threads(vs_tool* t, int32_t threads);

/* Results as JSON (the same record the vision application stores), for logging and parity tests. */
VS_API int32_t     vs_tool_get_result_json(const vs_tool* t, char* buf, int32_t cap);

#ifdef __cplusplus
}
#endif

#endif /* VS_CORE_H */
```

Notes on the choices:

- `vs_tool_run` is generic so a caller can run any loaded `.vstool` without knowing its kind.
  Typed getters in each tool header read the details.
- `vs_tool_get_result_json` writes the exact record the vision application writes to its results
  database, with floats in shortest round-trip form. Parity tests compare these strings (see
  [TEST-ACCEPTANCE-PLAN.md](TEST-ACCEPTANCE-PLAN.md), `SDK-PAR-*`).
- The size-then-fill pattern (`buf = NULL` returns the size) is the one `cr_get_learned` uses.
- `vs_thread_last_error` exists because a failed create has no handle to ask (`cr_create` today
  returns `NULL` with no message).

## 4. Tool headers

### 4.1 `vs_ocr.h`

```c
/*
 * vs_ocr.h - read text (OCR) and check expected text (OCV). Licence: VS-OCR.
 *
 * One OCR tool = one text field on the part (for example "the date line under the logo").
 * Create it with the field's settings, then call vs_tool_run (vs_core.h) for every image and
 * read the lines and characters below. The pretrained AI reader works with no training; the
 * classical reader needs a font (vs_ocr_train_font).
 */
#ifndef VS_OCR_H
#define VS_OCR_H

#include "vs_core.h"

#ifdef __cplusplus
extern "C" {
#endif

enum vs_ocr_engine {
    VS_OCR_AI = 0,            /* pretrained deep-learning reader (default; no training) */
    VS_OCR_CLASSICAL = 1,     /* segmentation plus trained font; fastest on clean print */
    VS_OCR_AUTO = 2           /* classical first, AI when confidence is low */
};

enum vs_ocr_print {
    VS_PRINT_ANY = 0, VS_PRINT_SOLID = 1, VS_PRINT_DOT_MATRIX = 2, VS_PRINT_INKJET = 3,
    VS_PRINT_EMBOSSED = 4     /* embossed or etched; AI engine recommended */
};

/* Field settings. Call vs_ocr_default_params first, then change what you need. */
typedef struct vs_ocr_params {
    uint32_t    struct_size;
    vs_roi      roi;              /* text area; fixtured to the locator pose when roi.fixtured = 1 */
    int32_t     engine;           /* vs_ocr_engine */
    int32_t     print;            /* vs_ocr_print */
    int32_t     polarity;         /* 0 auto, 1 dark on light, 2 light on dark */
    int32_t     max_lines;        /* 1..8 (default 2) */
    float       char_height_px;   /* expected character height; 0 = auto */
    float       min_confidence;   /* 0..100: a character below this is "unsure" (default 70) */
    const char* charset;          /* allowed characters, e.g. "0123456789/:-ABCDEFGHJKLMNPRSTUVWXYZ"; NULL = all */
    const char* script;           /* "latin" (default), "devanagari", ... (option, see TOOL-SPECIFICATIONS.md) */
    /* OCV: expected text. NULL = OCR only. Supports fields: {DATE:DD/MM/YY}, {LOT}, {SHIFT}. */
    const char* expected;
    float       min_print_quality;/* OCV: per-character quality 0..100 below which a char fails (default 50) */
    int32_t     time_budget_ms;   /* stop and report after this long (default 50) */
} vs_ocr_params;

/* One read character. */
typedef struct vs_ocr_char {
    uint32_t struct_size;
    uint32_t codepoint;           /* Unicode */
    float    confidence;          /* 0..100 */
    float    print_quality;       /* OCV: 0..100; -1 when no expected text */
    int32_t  status;              /* 0 good, 1 unsure, 2 wrong (OCV), 3 missing (OCV), 4 broken (OCV) */
    vs_point corners[4];          /* box in image pixels, clockwise from top-left */
} vs_ocr_char;

/* One read line. Text stays valid until the next run on the tool. */
typedef struct vs_ocr_line {
    uint32_t    struct_size;
    const char* text;             /* UTF-8 */
    float       confidence;       /* lowest character confidence on the line */
    int32_t     first_char, char_count; /* range in the tool's character list */
    int32_t     format_ok;        /* 1 = matches the expected format (date valid, lot pattern); -1 not checked */
    vs_point    corners[4];
} vs_ocr_line;

VS_API void      vs_ocr_default_params(vs_ocr_params* p);
VS_API vs_tool*  vs_ocr_create(const vs_ocr_params* p, vs_status* status);
VS_API vs_status vs_ocr_set_params(vs_tool* t, const vs_ocr_params* p);

/* Classical engine: learn a font from images whose text is known (one string per image). */
VS_API vs_status vs_ocr_train_font(vs_tool* t, const vs_image* images, const char* const* texts, int32_t count);

/*
 * Change the expected text between runs (OCV on a new lot) without rebuilding the tool.
 * Cheap: call it per part if needed.
 */
VS_API vs_status vs_ocr_set_expected(vs_tool* t, const char* expected);

VS_API int32_t   vs_ocr_line_count(const vs_tool* t);
VS_API vs_status vs_ocr_get_line(const vs_tool* t, int32_t index, vs_ocr_line* out);
VS_API int32_t   vs_ocr_char_count(const vs_tool* t);
VS_API vs_status vs_ocr_get_char(const vs_tool* t, int32_t index, vs_ocr_char* out);

#ifdef __cplusplus
}
#endif

#endif /* VS_OCR_H */
```

### 4.2 `vs_measure.h` (caliper part)

```c
/*
 * vs_measure.h - measure sizes on the image. Licence: VS-MEA. This part: the caliper.
 *
 * A caliper looks for edges across a narrow box (the projection region): it averages the grey
 * levels along the box, differentiates across it, and reports edges or edge pairs with sub-pixel
 * positions. Results are in pixels, or mm when a calibration is attached (vs_tool_set_calibration).
 */
#ifndef VS_MEASURE_H
#define VS_MEASURE_H

#include "vs_core.h"

#ifdef __cplusplus
extern "C" {
#endif

enum vs_edge_polarity {
    VS_EDGE_ANY = 0,
    VS_EDGE_DARK_TO_LIGHT = 1,   /* along the search direction */
    VS_EDGE_LIGHT_TO_DARK = 2
};

enum vs_caliper_mode {
    VS_CALIPER_SINGLE = 0,       /* individual edges */
    VS_CALIPER_PAIR = 1          /* edge pairs: width of a bar, gap, pin */
};

typedef struct vs_caliper_params {
    uint32_t struct_size;
    vs_roi   roi;                 /* must be VS_ROI_ROTRECT: search runs along the box's width axis */
    int32_t  mode;                /* vs_caliper_mode */
    int32_t  polarity;            /* first edge (and second for pairs) */
    int32_t  polarity2;
    float    filter_half_px;      /* smoothing across the profile, 0.5..20 (default 2) */
    float    min_contrast;        /* grey levels; edges weaker than this are ignored (default 10) */
    float    expected_width;      /* PAIR: the width to look for (pixels or mm); 0 = any */
    float    width_tolerance;     /* PAIR: +/- around expected_width */
    int32_t  max_results;         /* edges or pairs returned, best first (default 1) */
    /* Pass/fail limits on the first result's width (PAIR) or position (SINGLE); NaN = no limit. */
    float    limit_low, limit_high;
} vs_caliper_params;

typedef struct vs_caliper_edge {
    uint32_t struct_size;
    vs_point position;            /* sub-pixel, image pixels (mm when calibrated) */
    float    offset;              /* along the search axis from the box centre */
    float    contrast;            /* grey levels */
    float    score;               /* 0..100 */
    int32_t  polarity;
} vs_caliper_edge;

typedef struct vs_caliper_pair {
    uint32_t struct_size;
    int32_t  edge0, edge1;        /* indices into the edge list */
    float    width;               /* distance between the edges along the search axis */
    vs_point centre;
    float    score;               /* 0..100, contrast and closeness to expected_width */
} vs_caliper_pair;

VS_API void      vs_caliper_default_params(vs_caliper_params* p);
VS_API vs_tool*  vs_caliper_create(const vs_caliper_params* p, vs_status* status);
VS_API vs_status vs_caliper_set_params(vs_tool* t, const vs_caliper_params* p);

/* Auto-learn: place a caliper across the edge nearest to a clicked point. */
VS_API vs_status vs_caliper_learn_at(vs_tool* t, const vs_image* img, vs_point click);

VS_API int32_t   vs_caliper_edge_count(const vs_tool* t);
VS_API vs_status vs_caliper_get_edge(const vs_tool* t, int32_t index, vs_caliper_edge* out);
VS_API int32_t   vs_caliper_pair_count(const vs_tool* t);
VS_API vs_status vs_caliper_get_pair(const vs_tool* t, int32_t index, vs_caliper_pair* out);

/* The averaged profile and its derivative, for the properties panel graph. */
VS_API int32_t   vs_caliper_get_profile(const vs_tool* t, float* profile, float* derivative, int32_t cap);

#ifdef __cplusplus
}
#endif

#endif /* VS_MEASURE_H */
```

## 5. Code Reader: from `codereader.h` to `vs_code.h`

The Code Reader parent tool is today's engine. `vs_code.dll` contains the same code (the
`engine/src` sources) behind the new conventions. Nothing about decoding changes.

| Today (`codereader.h`) | New (`vs_code.h`) | Change |
| --- | --- | --- |
| `cr_reader*` | `vs_tool*` of kind `"code"` | Generic handle; works with `vs_tool_load`, graphics, JSON results |
| `cr_params` (no size field) | `vs_code_params` with `struct_size` first, then the same fields | Fields keep their names without the prefix; `roi_x..roi_h` become a `vs_roi` |
| `cr_default_params`, `cr_create`, `cr_set_params`, `cr_destroy` | `vs_code_default_params`, `vs_code_create`, `vs_code_set_params`, `vs_tool_destroy` | `create` reports a status |
| `enum cr_format` (bit values, one per job) | `enum vs_code_format`, same values plus the new symbologies (Code 39, 93, ITF, Codabar, DataBar, Pharmacode, PDF417, Aztec, Micro QR, DotCode) | Values 1, 2, 4, 8, 0xFFFF kept so stored settings still mean the same |
| `cr_decode(r, pixels, w, h, stride)` | `vs_tool_run(t, &img, pose)` with `vs_image` | Any `vs_pixel_format`; colour converted to grey inside |
| `cr_decode_frames` | `vs_code_run_frames(t, const vs_image* frames, int32_t count)` | Same behaviour |
| Return value = code count, -1 / -2 errors | `vs_status`; `vs_code_count(t)` | Errors become `VS_E_ARG`, `VS_E_INTERNAL` |
| `cr_code_count`, `cr_get_code`, `cr_code` | `vs_code_count`, `vs_code_get`, `vs_code_result` | Adds `struct_size`, `confidence`, corners as `vs_point[4]` |
| `cr_get_quality`, `cr_quality` | `vs_code_get_quality` | Unchanged content; the ISO verifier is a separate tool in `vs_verify.dll` |
| `cr_region_count`, `cr_get_region` | `vs_code_region_count`, `vs_code_get_region` | Same |
| `cr_miss_count`, `cr_get_miss`, `cr_get_miss_info` | `vs_code_miss_count`, `vs_code_get_miss` (box and diagnosis in one struct) | Merged |
| `cr_get_stats`, `cr_stats` | `vs_tool_get_result` (pass, time) plus `vs_code_get_stats` | `pass` = expected count reached |
| `cr_get_zone_stats` | `vs_code_get_zone_stats` | Same |
| `cr_set_found_callback` | `vs_code_set_found_callback` | Same rules (runs on the calling thread) |
| `cr_load_model`, `cr_load_enhancer` | `vs_code_load_model`, `vs_code_load_enhancer` | Models may also come inside a `.vstool` |
| `cr_get_learned`, `cr_set_learned` | `vs_code_get_learned`, `vs_code_set_learned` | Same blob format, versioned by the engine |
| `cr_last_error`, `cr_version` | `vs_last_error`, `vs_version` | Shared |
| No licence check | `VS_E_NOT_LICENSED` without `VS-COD` | See compatibility plan |

### 5.1 Compatibility plan for today's callers

Today's callers are the .NET `BarcodeReading.CodeReader` class (`dotnet/CodeReader/`), the
LineRunner and the Dashboard, plus `tools/bench`.

1. **`codereader.dll` shim.** For the whole of SDK major version 1, a `codereader.dll` with the
   exact `codereader.h` exports keeps shipping. It is a thin layer over `vs_code.dll`: it maps
   `cr_params` to `vs_code_params` and back. Struct layouts are frozen at the current header
   (the `Native.cs` layouts stay valid). It is removed in major version 2, announced at least 12
   months before.
2. **Same results.** The shim calls the same decoder, so `bench` results through `codereader.dll`
   and through `vs_code.dll` must be identical on `truth/samples.csv`. This is a CI test
   (`SDK-PAR-CR` in [TEST-ACCEPTANCE-PLAN.md](TEST-ACCEPTANCE-PLAN.md)).
3. **.NET.** `BarcodeReading.CodeReader` keeps its public API in the `BarcodeReading` package for
   version 1 and is marked `[Obsolete]` with a pointer to `VisionStudio.Sdk.Code.CodeReaderTool`.
   Its higher-level helpers (`Calibration`, `TuningAgent`, `ProductMaster`, `Gs1Data`,
   `MissDiagnosis`, `LineAdvisor`, `DuplicateMemory`) move to `VisionStudio.Sdk.Code` with the
   same behaviour.
4. **Licence.** The shim checks `VS-COD` like any tool. Existing aggregation installations get a
   `VS-COD` perpetual serial at upgrade (open question in [LICENSING.md](LICENSING.md)).
5. **Learned state.** Blobs saved by `cr_get_learned` load in `vs_code_set_learned` unchanged; the
   engine already drops blobs that are not its own, so no migration code is needed.
6. **Bench.** `tools/bench` moves to `vs_code.h` once `vs_code.dll` exists, and keeps a
   `--shim` switch to run the same set through `codereader.dll`.

## 6. .NET wrapper (`VisionStudio.Sdk`)

| Item | Design |
| --- | --- |
| Packages | `VisionStudio.Sdk` (core, image, licence, `.vstool`), one package per parent tool (`VisionStudio.Sdk.Ocr`, `.Measure`, `.Code`, ...) so an application ships only the DLLs it uses. Each package carries its native DLL under `runtimes/win-x64/native/` |
| Target | .NET 10 (LTS; .NET 8 until its end of support in November 2026). `net10.0` only; no .NET Framework |
| Interop | `LibraryImport` (source-generated P/Invoke, no runtime marshalling) with blittable structs. Struct sizes checked at start against `vs_struct_size("vs_ocr_params")` exported by each DLL |
| Handles | `VsToolHandle : SafeHandle` calls `vs_tool_destroy`. Tool classes are `IDisposable`; a finaliser is a safety net only. Today's `CodeReader` uses a raw `IntPtr`; the new wrapper fixes that |
| Images | `ImageView` is a `readonly ref struct` over `ReadOnlySpan<byte>` plus width, height, stride, format. Overloads take `IntPtr` (camera buffers, as `dotnet/HikCamera` does today), `Memory<byte>`, `System.Drawing.Bitmap` (via `LockBits`, no copy) and WPF `BitmapSource` (one copy) |
| Errors | Negative `vs_status` becomes `VisionStudioException` (with `Status` and the message) on the managed side only. `NotLicensedException` derives from it and carries the serial prefix |
| Async | `RunAsync(ImageView ..., CancellationToken)` cannot take a ref struct, so it takes `ReadOnlyMemory<byte>`; it runs on a dedicated thread per tool and maps cancellation to `vs_tool_cancel` |
| Results | Records (`OcrResult`, `OcrLine`, `CaliperResult`), copied out of native memory so they outlive the next run |
| Threading | Same as native: one tool instance per thread. A `ToolPool<T>` helper creates instances per thread for parallel lines |
| Graphics | `result.Graphics` as a list of records; `VisionStudio.Sdk.Wpf` (optional) draws them on an image control with the theme colours |

```csharp
using VisionStudio.Sdk;
using VisionStudio.Sdk.Ocr;

VisionStudio.Init();                                   // reads the PC's licence file

using var ocr = Tool.Load<OcrTool>(@"C:\Jobs\date-line.vstool");   // tuned in the GUI
using var image = ImageBuffer.Load(@"C:\Images\part-001.png");

OcrResult r = ocr.Run(image.View);
Console.WriteLine($"{(r.Pass ? "OK" : "NG")} {r.Lines[0].Text} ({r.Confidence:0}) {r.TimeMs:0.0} ms");

ocr.SetExpected("EXP 11/27 LOT A1234");                 // OCV for the next lot
var r2 = await ocr.RunAsync(frameMemory, width, height, stride, PixelFormat.Mono8, ct);
```

## 7. Python package (`visionstudio`)

| Item | Design |
| --- | --- |
| Package | `pip install visionstudio`. One wheel per platform (`win_amd64` first; `manylinux_2_28_x86_64` and `aarch64` for Jetson in P5) containing all tool DLLs. Python 3.10 to 3.13 |
| Binding | `cffi` in ABI mode over the same C headers, so there is no compiled Python extension per Python version and the C ABI is the only contract |
| Images | Any NumPy array with `dtype=uint8` or `uint16`, shape `(h, w)` or `(h, w, 3)`, and unit stride along the row (`arr.strides[1] == arr.itemsize * channels`). Row stride may be anything, so slices and OpenCV images work without a copy. Other layouts raise `ValueError` with a hint to call `np.ascontiguousarray` |
| Colour order | `format=` argument; default `"bgr"` for 3-channel arrays because OpenCV is the common source |
| Results | `@dataclass(frozen=True)` per result type; `to_dict()` for pandas; `graphics` as a list of dataclasses with a `draw(arr)` helper |
| Errors | `VisionStudioError(status, message)`, `NotLicensedError` |
| GIL | Released during `run`, so threads with separate tools run in parallel |

```python
import numpy as np, cv2
import visionstudio as vs

vs.init()
cal = vs.measure.Caliper.load("C:/Jobs/gap.vstool")
img = cv2.imread("part-001.png", cv2.IMREAD_GRAYSCALE)      # no copy into the SDK
res = cal.run(img)
print(res.passed, res.pairs[0].width, res.time_ms)
```

## 8. C usage

```c
#include "vs_core.h"
#include "vs_ocr.h"
#include <stdio.h>

int main(void)
{
    vs_init_params ip;
    vs_default_init_params(&ip);
    if (vs_init(&ip) != VS_OK) { fprintf(stderr, "%s\n", vs_thread_last_error()); return 1; }

    vs_status st;
    vs_tool* ocr = vs_tool_load("date-line.vstool", &st);       /* one line: tuned in the GUI */
    if (!ocr) { fprintf(stderr, "%d %s\n", st, vs_thread_last_error()); return 1; }

    vs_image img = { 0 };
    img.data = camera_pixels;   /* frame buffer from the camera SDK, not copied */
    img.width = 2448; img.height = 2048;
    img.stride = 2448; img.format = VS_PIX_MONO8;

    if (vs_tool_run(ocr, &img, NULL) >= 0) {
        vs_result r = { sizeof r };
        vs_tool_get_result(ocr, &r);
        vs_ocr_line line = { sizeof line };
        if (vs_ocr_line_count(ocr) > 0 && vs_ocr_get_line(ocr, 0, &line) == VS_OK)
            printf("%s %s %.0f\n", r.pass ? "OK" : "NG", line.text, r.confidence);
    } else {
        fprintf(stderr, "%s\n", vs_last_error(ocr));
    }
    vs_tool_destroy(ocr);
    vs_shutdown();
    return 0;
}
```

## 9. `.vstool` files

| Item | Design |
| --- | --- |
| What | One tool exactly as tuned in the job designer: kind, parameters, ROI, taught patterns or fonts, AI models, pass/fail limits, optional calibration reference |
| Container | ZIP (same container rules as `.vsjob`, see [JOB-FILE-FORMAT.md](JOB-FILE-FORMAT.md)): `tool.json` (kind, schema version, engine version, parameters), `blobs/` (patterns, fonts, models), `signature` |
| Export | In the designer: right-click a tool, "Export for SDK". Fixtured ROIs are saved relative to the fixture, so the SDK caller passes the pose from its own locator, or exports the locator too |
| Load | `vs_tool_load(path, &status)`, `Tool.Load<T>(path)`, `vs.load(path)`: one line in each language |
| Protection | Optional password (AES-256-GCM, key from PBKDF2). AI models inside stay encrypted and need the DL licence (see [LICENSING.md](LICENSING.md) 7.3) |
| Versions | A newer library opens older `.vstool` files. An older library refuses a newer file with `VS_E_VERSION` and names the needed version |

## 10. Job runner API (`vs_job.h`)

For applications that run a whole job built in the vision application, in their own process,
on images they supply. No camera, no PLC: the caller owns acquisition and I/O.

```c
typedef struct vs_job vs_job;

VS_API vs_job*   vs_job_load(const char* path, const char* password, vs_status* status);
VS_API void      vs_job_destroy(vs_job* j);
VS_API int32_t   vs_job_camera_count(const vs_job* j);
VS_API vs_status vs_job_set_variant(vs_job* j, const char* variant);      /* product variant / recipe */
VS_API vs_status vs_job_set_input(vs_job* j, const char* name, const char* value); /* e.g. "PartID" */
/* images[i] is camera i of the job (synchronised and independent job types). */
VS_API vs_status vs_job_run(vs_job* j, const vs_image* images, int32_t count);
VS_API vs_status vs_job_get_result(const vs_job* j, vs_result* out);     /* overall pass/fail */
VS_API int32_t   vs_job_output_count(const vs_job* j);
VS_API vs_status vs_job_get_output(const vs_job* j, int32_t index, const char** name, const char** value);
VS_API int32_t   vs_job_get_result_json(const vs_job* j, char* buf, int32_t cap);
VS_API vs_tool*  vs_job_tool(vs_job* j, const char* tool_name);           /* borrow; owned by the job */
```

Outputs are the same named values the PLC mapping uses (see [PLC-INTERFACE.md](PLC-INTERFACE.md)).
Sequential and stitched multi-camera job types need the runtime's part tracking and are not
supported by the in-process runner in version 1.

## 11. Remote API

For applications on the same or another PC that talk to a running vision application. The
runtime service hosts it. Off by default; the admin turns it on and creates tokens.

### 11.1 gRPC service

```proto
syntax = "proto3";
package visionstudio.remote.v1;

import "google/protobuf/timestamp.proto";

service VisionRuntime {
  rpc GetStatus(GetStatusRequest) returns (Status);
  rpc ListJobs(ListJobsRequest) returns (ListJobsResponse);
  rpc ChangeJob(ChangeJobRequest) returns (ChangeJobResponse);
  rpc Trigger(TriggerRequest) returns (TriggerResponse);
  rpc StreamResults(StreamResultsRequest) returns (stream InspectionResult);
  rpc GetImage(GetImageRequest) returns (stream ImageChunk);
  rpc SetInput(SetInputRequest) returns (SetInputResponse);    // PartID, variant, expected text
}

message GetStatusRequest {}
message Status {
  enum State { STATE_UNSPECIFIED = 0; READY = 1; BUSY = 2; STOPPED = 3; ERROR = 4; TEACH = 5; }
  State state = 1;
  string active_job = 2;
  int32 active_job_number = 3;
  bool plc_connected = 4;
  uint32 error_code = 5;            // same codes as PLC-INTERFACE.md
  string error_text = 6;            // in the requested language
  uint64 inspections = 7;
  uint64 ng = 8;
  string version = 9;
}

message ListJobsRequest {}
message ListJobsResponse { repeated JobInfo jobs = 1; }
message JobInfo { string name = 1; int32 number = 2; string version = 3; repeated string variants = 4; }

message ChangeJobRequest { oneof job { string name = 1; int32 number = 2; } string variant = 3; }
message ChangeJobResponse { bool ok = 1; uint32 error_code = 2; string error_text = 3; }

message TriggerRequest {
  string part_id = 1;
  uint32 camera_mask = 2;           // 0 = all cameras of the job
  bool wait_for_result = 3;         // true: response carries the result
}
message TriggerResponse { uint32 trigger_id = 1; InspectionResult result = 2; uint32 error_code = 3; }

message StreamResultsRequest { bool include_values = 1; bool ng_only = 2; }

message InspectionResult {
  uint64 inspection_id = 1;
  uint32 trigger_id = 2;
  google.protobuf.Timestamp time = 3;
  string job = 4;
  string part_id = 5;
  bool pass = 6;
  float cycle_ms = 7;
  repeated ToolResult tools = 8;
  repeated NamedValue outputs = 9;  // the mapped outputs, as on the PLC
  repeated string image_ids = 10;   // one per camera; for GetImage
}
message ToolResult { string name = 1; string kind = 2; bool pass = 3; float confidence = 4;
                     uint32 reason = 5; string reason_text = 6; float time_ms = 7;
                     optional float print_quality = 8;    // Check text: PrintQuality 0..100 (tool spec 7.3)
                     bool report_only = 9;                // Read text set to report only: information, never OK
                     optional float code_grade = 10; }    // graded Read code: lowest grade 4 = A .. 0 = F; absent
                                                          // when not graded or any code could not be graded
message NamedValue { string name = 1; oneof v { double number = 2; string text = 3; bool flag = 4; } }

message GetImageRequest {
  string image_id = 1;
  enum Encoding { ENCODING_UNSPECIFIED = 0; PNG = 1; JPEG = 2; RAW = 3; }
  Encoding encoding = 2;
  bool with_graphics = 3;           // burn the result graphics in
}
message ImageChunk { bytes data = 1; int32 width = 2; int32 height = 3; string format = 4; }

message SetInputRequest { string name = 1; string value = 2; }
message SetInputResponse { bool ok = 1; uint32 error_code = 2; }
```

### 11.2 REST mapping

The same service over HTTP/JSON (gRPC JSON transcoding in ASP.NET Core), for callers without
gRPC. Field names follow the proto JSON mapping (lowerCamelCase).

| Method | Path | RPC | Notes |
| --- | --- | --- | --- |
| GET | `/v1/status` | `GetStatus` | |
| GET | `/v1/jobs` | `ListJobs` | |
| PUT | `/v1/job` | `ChangeJob` | Body `{ "name": "Label-A", "variant": "500ml" }` |
| POST | `/v1/trigger` | `Trigger` | |
| GET | `/v1/results/stream` | `StreamResults` | Server-sent events, one JSON result per event |
| GET | `/v1/images/{imageId}?encoding=png&withGraphics=true` | `GetImage` | Returns the image bytes with the right content type |
| POST | `/v1/inputs/{name}` | `SetInput` | |

### 11.3 Rules

- **Security.** TLS 1.2 or later always; no plain-text port. A self-signed certificate is
  created at install; the admin can replace it. Bearer tokens are created in the admin console
  with a role: `read` (status, results, images), `operate` (trigger, change job, inputs),
  `engineer` (reserved). Tokens are stored hashed and can be revoked. Every call is written to
  the audit trail (requirements section 12).
- **Control only from Vision Studio or a token** (owner, 2026-10-30). A browser signed in to the
  dashboard gets `read` whatever the user's role. Plant software (MES, ERP) uses an `operate`
  token to start and stop, change job or product, trigger and set inputs. No token edits jobs,
  products, tool settings, users or settings: those change only in Vision Studio, where they
  are versioned and signed ([ARCHITECTURE.md](ARCHITECTURE.md) section 2.2).
- **The PLC stays the machine interface.** Each job has a trigger source: PLC (default), Remote
  API, or both. While the PLC owns the job, `Trigger` and `ChangeJob` return error 120 ("PLC
  has control"). Remote triggering is for test benches, integration and offline stations.
- **Ports.** gRPC and REST share one HTTPS port (default 50443), configurable.
- **Versioning.** The package name carries the major version (`v1`); fields are only added.
- **Languages.** `error_text` and `reason_text` come back in the language of the
  `Accept-Language` header, using the UI string tables (see [LOCALISATION.md](LOCALISATION.md)).

## 12. Versioning and deprecation

| Rule | Detail |
| --- | --- |
| Version | One SDK version for all DLLs and packages in a release (`1.4.2`). `vs_version()` returns it |
| PATCH | Bug fixes only. No API change. Results may change only to fix a bug, and the release notes list it |
| MINOR | New functions, new tools, new fields at the end of structs, new enum values. Old binaries keep working with the new DLLs |
| MAJOR | Removes deprecated functions or changes layouts. At most once every two years |
| Deprecation | A function is marked `VS_DEPRECATED("use ...")` and `[Obsolete]` in .NET for at least one full minor-release cycle and at least 12 months before removal in the next major |
| ABI check in CI | The exported symbol list and every public struct's size and field offsets are compared with the last release; a difference fails the build unless the version is a new major (see [TEST-ACCEPTANCE-PLAN.md](TEST-ACCEPTANCE-PLAN.md)) |
| Results stability | A minor release must not change any golden-image result beyond the tool's stated tolerance unless the release notes say why |
| Enums | Callers must handle unknown enum values (new reasons, new formats) without failing |
| File formats | `.vstool` and `.vsjob` from any older version open in newer ones (requirements section 13, "Updates") |

## 13. Samples

| Sample | Language | Shows |
| --- | --- | --- |
| `hello-ocr` | C | Init, load a `.vstool`, run on a file, print result |
| `caliper-from-scratch` | C | Create a caliper with typed params, auto-learn at a point, draw graphics |
| `code-reader-migration` | C | The same image through `codereader.dll` and `vs_code.dll`, results compared |
| `WinFormsInspector` | C# | Bitmap from a camera SDK, run three tools, draw graphics, async |
| `WpfOperatorPanel` | C# | `VisionStudio.Sdk.Wpf` overlay, licence status display |
| `JobRunnerConsole` | C# | Load a `.vsjob`, run on a folder, write CSV |
| `RemoteClient` | C# | gRPC: stream results, fetch NG images |
| `numpy_pipeline` | Python | OpenCV capture, OCR and caliper, results into pandas |
| `remote_rest.py` | Python | REST trigger and SSE results with `requests` |
| `labview-ocr` | LabVIEW | Call Library Function nodes on `vs_ocr.dll` (P4) |

Every sample builds in CI and runs against a small image set that ships with the samples
(synthetic or owned images only, never customer images).

## 14. Documentation deliverables

- API reference generated from the headers (Doxygen) and the .NET XML comments (DocFX), and a
  Python reference (Sphinx autodoc), published per version.
- Getting-started guide per language (one page each, first result in under 15 minutes).
- Tool guides: what each parameter does, with images, shared with
  [TOOL-SPECIFICATIONS.md](TOOL-SPECIFICATIONS.md).
- Migration guide from `codereader.h` (section 5).
- Remote API guide with the `.proto` file, the OpenAPI document generated from it, and token set-up.
- Licensing guide for SDK developers (developer licence, deployment, error handling).
- Release notes with ABI and result changes called out.

## 15. Open questions

- [ ] Price of a Vision Package location (it uses the same ERPNext licence record as Vision Studio; type `R` stays reserved), see requirements section 15 and [LICENSING.md](LICENSING.md).
- [ ] Linux and Jetson SDK timing (requirements section 15). The C ABI and `cffi` binding are
      chosen so a Linux build adds no API change.
- [ ] LabVIEW and Delphi samples: needed at P4 or on request?
- [ ] Should the in-process job runner support sequential and stitched multi-camera jobs in
      version 1?
- [ ] GPU result tolerance per AI tool, to be set with [AI-DEEP-LEARNING.md](AI-DEEP-LEARNING.md).
