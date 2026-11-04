using System;
using System.Runtime.InteropServices;

namespace VisionCore;

/// <summary>
/// P/Invoke declarations matching engine/include/vs_anomaly.h.
/// In-Memory PatchCore Anomaly Detection Engine (AG-02, Tool 10).
/// </summary>
public static class AnomalyNative
{
    internal const string Lib = "vs_anomaly";

    public enum AnomalyVerdict
    {
        Pass = 0,
        Defect = 1,
        Borderline = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public int StructSize;
        public int PatchSize;
        public int PatchStride;
        public int FeatureDim;
        public float CoresetRatio;
        public int MaxCoresetSize;
        public int HnswM;
        public int HnswEfConstruction;
        public int HnswEfSearch;
        public float Sensitivity;
        public int MinDefectAreaPx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Defect
    {
        public int BBoxX;
        public int BBoxY;
        public int BBoxW;
        public int BBoxH;
        public float PeakAnomalyScore;
        public int AreaPx;
    }

    [DllImport(Lib, EntryPoint = "vs_anom_default_params")]
    internal static extern void DefaultParams(out Params p);

    [DllImport(Lib, EntryPoint = "vs_anom_create")]
    internal static extern IntPtr Create(ref Params p);

    [DllImport(Lib, EntryPoint = "vs_anom_destroy")]
    internal static extern void Destroy(IntPtr model);

    [DllImport(Lib, EntryPoint = "vs_anom_reset")]
    internal static extern void Reset(IntPtr model);

    [DllImport(Lib, EntryPoint = "vs_anom_teach_image")]
    internal static extern unsafe int TeachImage(
        IntPtr model,
        byte* src,
        int width,
        int height,
        int stride);

    [DllImport(Lib, EntryPoint = "vs_anom_finalize_teach")]
    internal static extern int FinalizeTeach(
        IntPtr model,
        out float outCalibratedThreshold,
        out int outCoresetVectors);

    [DllImport(Lib, EntryPoint = "vs_anom_inspect_mono8")]
    internal static extern unsafe int InspectMono8(
        IntPtr model,
        byte* src,
        int width,
        int height,
        int stride,
        out float outScore,
        out int outVerdict,
        out Defect outDefect,
        byte* heatmapOut,
        int heatmapStride);

    [DllImport(Lib, EntryPoint = "vs_anom_save_model", CharSet = CharSet.Ansi)]
    internal static extern int SaveModel(IntPtr model, string filepath);

    [DllImport(Lib, EntryPoint = "vs_anom_load_model", CharSet = CharSet.Ansi)]
    internal static extern int LoadModel(string filepath, out IntPtr outModel);
}
