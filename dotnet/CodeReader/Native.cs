using System;
using System.Runtime.InteropServices;

namespace BarcodeReading;

// P/Invoke declarations matching engine/include/codereader.h. Keep the struct
// layouts in step with the header: field order and sizes must match exactly.
internal static partial class Native
{
    private const string Lib = "codereader";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Params
    {
        public int StructSize; // ParamsSize: the engine refuses a struct without it
        public int Format;
        public int ExpectedCount;
        public int TimeBudgetMs;
        public int Threads;
        public int LocatorScale;
        public int MaxUpscale;
        public float Gamma;
        public float LocatorDensity;
        public int FullPass;
        public int RoiX, RoiY, RoiW, RoiH;
        public int LocatorMode;
        public float AiThreshold;
        public int EnhancerMode;
        public int GradeQuality;
        public int MatchSerials;
        public int HintModules;
        public float HintCodeSidePx;
        public float HintSymbolsPerCode;
        public int MinLength;
        public int MaxLength;
        public int CheckDigit;
        public int PharmaDirection;
    }

    // codereader.h CR_PARAMS_TAG | sizeof(cr_params)
    internal const int ParamsTag = 0x43520000;
    internal static readonly int ParamsSize = ParamsTag | Marshal.SizeOf<Params>();

    [StructLayout(LayoutKind.Sequential)]
    internal struct Code
    {
        public IntPtr Text;
        public int Format;
        public int Step;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] X;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] Y;
        public int Copies;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Miss
    {
        public int X, Y, W, H;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MissInfo
    {
        public int X, Y, W, H;
        public int Reason;
        public int Measured;
        public float Sharpness, Contrast, Light, Dark, Saturated, Texture, ModulePx, LearnedModulePx;
        public int Finder, QuietZone;
        public float RefSharpness, RefContrast;
        public int SlotRow, SlotCol;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FoundRegion
    {
        public int X, Y, W, H, FromAi, Read;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FoundFn(IntPtr user, IntPtr regions, int count);

    [DllImport(Lib, EntryPoint = "cr_set_found_callback")]
    internal static extern void SetFoundCallback(IntPtr r, FoundFn? fn, IntPtr user);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Quality
    {
        public int Measured;
        public float SymbolContrast, Modulation, ReflectanceMargin, FixedPatternDamage, AxialNonuniformity,
            GridNonuniformity, UnusedErrorCorrection, PrintGrowth, QuietZoneDark, ModuleSizePx, LightingUniformity, Saturated;
        public int GradeSc, GradeMod, GradeRm, GradeFpd, GradeAn, GradeGn, GradeUec, GradeQz;
        public int Overall;
        public int Calibrated;
        public float XDimMm;
        public int Linear;
        public float EdgeContrastMin, Defects, Decodability, RMin, RMax;
        public int Scans, DecodedScans;
        public int GradeEc, GradeDefects, GradeDecodability;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Lens
    {
        public float Fx, Fy, Cx, Cy, K1, K2, P1, P2, K3, MmPerPx, RmsPx;
    }

    [DllImport(Lib, EntryPoint = "cr_set_flat_field")]
    internal static extern int SetFlatField(IntPtr r, IntPtr pixels, int width, int height, int stride);

    [DllImport(Lib, EntryPoint = "cr_set_reflectance")]
    internal static extern int SetReflectance(IntPtr r, float greyDark, float greyLight, float reflDark, float reflLight);

    [DllImport(Lib, EntryPoint = "cr_measure_card")]
    internal static extern int MeasureCard(IntPtr r, IntPtr pixels, int width, int height, int stride, out float greyDark, out float greyLight);

    [DllImport(Lib, EntryPoint = "cr_set_lens")]
    internal static extern int SetLens(IntPtr r, ref Lens lens);

    [DllImport(Lib, EntryPoint = "cr_calibrate_chessboard")]
    internal static extern int CalibrateChessboard(IntPtr[] pixels, int count, int width, int height, int stride, int cols, int rows, float squareMm, out Lens lens);

    [DllImport(Lib, EntryPoint = "cr_get_quality")]
    internal static extern int GetQuality(IntPtr r, int index, out Quality q);

    [DllImport(Lib, EntryPoint = "cr_region_count")]
    internal static extern int RegionCount(IntPtr r);

    [DllImport(Lib, EntryPoint = "cr_get_region")]
    internal static extern int GetRegion(IntPtr r, int index, out FoundRegion g);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Stats
    {
        public int Codes;
        public int Expected;
        public int Candidates;
        public int Misses;
        public float LocateMs;
        public float DecodeMs;
        public float TotalMs;
        public int TimedOut;
        public float LabelBrightness;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StatsExt
    {
        public int StructSize;
        public int RejectedLength;
        public int RejectedCheckDigit;
        public int CopiesOver;
        public int ExtraUnchecked;
        public int RefusedEan;
        public int RefusedAddon;
    }

    [DllImport(Lib, EntryPoint = "cr_get_stats_ext")]
    internal static extern int GetStatsExt(IntPtr r, ref StatsExt s);

    [DllImport(Lib, EntryPoint = "cr_default_params")]
    internal static extern void DefaultParams(out Params p);

    [DllImport(Lib, EntryPoint = "cr_create")]
    internal static extern IntPtr Create(ref Params p);

    [DllImport(Lib, EntryPoint = "cr_destroy")]
    internal static extern void Destroy(IntPtr r);

    [DllImport(Lib, EntryPoint = "cr_set_params")]
    internal static extern int SetParams(IntPtr r, ref Params p);

    [DllImport(Lib, EntryPoint = "cr_load_model")]
    internal static extern int LoadModel(IntPtr r, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, EntryPoint = "cr_load_enhancer")]
    internal static extern int LoadEnhancer(IntPtr r, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    // Load from decrypted ONNX bytes already in memory (the model never exists as a plaintext file on
    // the shipped disk). `size` is size_t -> nuint.
    [DllImport(Lib, EntryPoint = "cr_load_model_buffer")]
    internal static extern int LoadModelBuffer(IntPtr r, byte[] data, nuint size);

    [DllImport(Lib, EntryPoint = "cr_load_enhancer_buffer")]
    internal static extern int LoadEnhancerBuffer(IntPtr r, byte[] data, nuint size);

    [DllImport(Lib, EntryPoint = "cr_decode")]
    internal static extern unsafe int Decode(IntPtr r, byte* pixels, int width, int height, int stride);

    [DllImport(Lib, EntryPoint = "cr_decode_frames")]
    internal static extern unsafe int DecodeFrames(IntPtr r, byte** pixels, int frames, int width, int height, int stride);

    [DllImport(Lib, EntryPoint = "cr_code_count")]
    internal static extern int CodeCount(IntPtr r);

    [DllImport(Lib, EntryPoint = "cr_get_code")]
    internal static extern int GetCode(IntPtr r, int index, out Code c);

    // cr_code_ext and cr_scan grow at their end: StructSize tells the engine how much to write
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct CodeExt
    {
        public int StructSize;
        public fixed byte SymbologyId[4];
        public int Gs1;
        public int GradeRmin;
        public int ScanCount;
        public float Angle;
        public int CheckDigitOk;
        public int QuietZoneOk;
        public IntPtr AddOn;
        public float CodewordYield;
        public int GradeCodewordYield;
        public int GridRows, GridCols;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Scan
    {
        public int StructSize;
        public float RMin, RMax, SymbolContrast, EdgeContrastMin, Modulation, Defects, Decodability;
        public int Decoded, QuietZoneOk, GradeRmin, GradeQuietZone, Grade;
    }

    [DllImport(Lib, EntryPoint = "cr_get_code_ext")]
    internal static extern int GetCodeExt(IntPtr r, int index, ref CodeExt e);

    [DllImport(Lib, EntryPoint = "cr_get_scan")]
    internal static extern int GetScan(IntPtr r, int index, int scan, ref Scan s);

    [DllImport(Lib, EntryPoint = "cr_miss_count")]
    internal static extern int MissCount(IntPtr r);

    [DllImport(Lib, EntryPoint = "cr_get_miss")]
    internal static extern int GetMiss(IntPtr r, int index, out Miss m);

    [DllImport(Lib, EntryPoint = "cr_get_miss_info")]
    internal static extern int GetMissInfo(IntPtr r, int index, out MissInfo m);

    [DllImport(Lib, EntryPoint = "cr_get_zone_stats")]
    internal static extern void GetZoneStats(IntPtr r, [Out] int[] read, [Out] int[] unread);

    [DllImport(Lib, EntryPoint = "cr_get_stats")]
    internal static extern void GetStats(IntPtr r, out Stats s);

    [DllImport(Lib, EntryPoint = "cr_last_error")]
    internal static extern IntPtr LastError(IntPtr r);

    [DllImport(Lib, EntryPoint = "cr_version")]
    internal static extern IntPtr Version();

    // Runtime anti-tamper (codereader.h): the latched cr_tamper state. IntegrityCheck runs the checks
    // and may latch; IntegrityState only reads the latch. Process-wide, not per-reader.
    [DllImport(Lib, EntryPoint = "cr_integrity_check")]
    internal static extern int IntegrityCheck();

    [DllImport(Lib, EntryPoint = "cr_integrity_state")]
    internal static extern int IntegrityState();

    [DllImport(Lib, EntryPoint = "cr_get_learned")]
    internal static extern int GetLearned(IntPtr r, byte[]? buf, int cap);

    [DllImport(Lib, EntryPoint = "cr_set_learned")]
    internal static extern int SetLearned(IntPtr r, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
}
