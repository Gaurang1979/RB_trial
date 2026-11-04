using System;
using System.Runtime.InteropServices;

namespace VisionCore;

/// <summary>
/// P/Invoke declarations matching engine/include/vs_inspect.h.
/// Golden Template &amp; Variation Model Inspection Engine (Tool 8.2, AS-05).
/// </summary>
public static class InspectNative
{
    internal const string Lib = "vs_inspect";

    public enum InspectVerdict
    {
        Pass = 0,
        Defect = 1,
        Error = 2
    }

    public enum DefectType
    {
        Any = 0,
        Bright = 1,
        Dark = 2,
        Mixed = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public int StructSize;
        public float Sensitivity;
        public float BaseOffset;
        public float EdgeTolerancePx;
        public float EdgeMarginWeight;
        public int MinDefectAreaPx;
        public int NormalizeLighting;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Defect
    {
        public int BBoxX;
        public int BBoxY;
        public int BBoxW;
        public int BBoxH;
        public int AreaPx;
        public float PeakContrast;
        public int DefectType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Result
    {
        public int StructSize;
        public int Verdict;
        public int DefectCount;
        public int TotalDefectArea;
        public float MaxContrast;
        public float Confidence;
        public float TimeMs;
        public Defect WorstDefect;
    }

    [DllImport(Lib, EntryPoint = "vs_inspect_default_params")]
    internal static extern void DefaultParams(out Params p);

    [DllImport(Lib, EntryPoint = "vs_inspect_create")]
    internal static extern IntPtr Create(int width, int height, ref Params p);

    [DllImport(Lib, EntryPoint = "vs_inspect_destroy")]
    internal static extern void Destroy(IntPtr model);

    [DllImport(Lib, EntryPoint = "vs_inspect_reset")]
    internal static extern void Reset(IntPtr model);

    [DllImport(Lib, EntryPoint = "vs_inspect_set_params")]
    internal static extern int SetParams(IntPtr model, ref Params p);

    [DllImport(Lib, EntryPoint = "vs_inspect_get_params")]
    internal static extern int GetParams(IntPtr model, ref Params p);

    [DllImport(Lib, EntryPoint = "vs_inspect_add_sample")]
    internal static extern unsafe int AddSample(
        IntPtr model,
        byte* src,
        int width,
        int height,
        int stride);

    [DllImport(Lib, EntryPoint = "vs_inspect_finalize_teach")]
    internal static extern int FinalizeTeach(
        IntPtr model,
        out int outSamplesTrained);

    [DllImport(Lib, EntryPoint = "vs_inspect_run")]
    internal static extern unsafe int Run(
        IntPtr model,
        byte* src,
        int width,
        int height,
        int stride,
        ref Result outResult,
        byte* diffOut,
        int diffStride);

    [DllImport(Lib, EntryPoint = "vs_inspect_save", CharSet = CharSet.Ansi)]
    internal static extern int Save(IntPtr model, string filepath);

    [DllImport(Lib, EntryPoint = "vs_inspect_load", CharSet = CharSet.Ansi)]
    internal static extern int Load(string filepath, out IntPtr outModel);
}
