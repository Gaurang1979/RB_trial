using System;
using System.Runtime.InteropServices;

namespace VisionCore;

// P/Invoke declarations matching engine/include/vs_drift.h.
public static class DriftNative
{
    internal const string Lib = "vs_drift";

    public enum DriftStatus
    {
        Ok = 0,
        WarnLensDirt = 1,
        WarnLightingDrop = 2,
        WarnOverexposure = 3,
        WarnOod = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Features
    {
        public float MeanBrightness;
        public float ContrastStd;
        public float Sharpness;
        public float Uniformity;
        public fixed float CornerSharpness[4];
        public float SaturatedRatio;
        public float DarkFloor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public int StructSize;
        public int WindowSize;
        public float WarnDistanceThreshold;
        public float DirtSharpnessSigma;
        public float LightDropSigma;
        public int GridStep;
    }

    [DllImport(Lib, EntryPoint = "vs_drift_default_params")]
    internal static extern void DefaultParams(out Params p);

    [DllImport(Lib, EntryPoint = "vs_drift_extract_features")]
    internal static extern unsafe int ExtractFeatures(
        byte* src, int width, int height, int stride, int gridStep,
        out Features outFeat);

    [DllImport(Lib, EntryPoint = "vs_drift_create")]
    internal static extern IntPtr Create(ref Params p);

    [DllImport(Lib, EntryPoint = "vs_drift_destroy")]
    internal static extern void Destroy(IntPtr tracker);

    [DllImport(Lib, EntryPoint = "vs_drift_reset")]
    internal static extern void Reset(IntPtr tracker);

    [DllImport(Lib, EntryPoint = "vs_drift_teach_sample")]
    internal static extern int TeachSample(IntPtr tracker, ref Features feat);

    [DllImport(Lib, EntryPoint = "vs_drift_finalize_teach")]
    internal static extern int FinalizeTeach(IntPtr tracker);

    [DllImport(Lib, EntryPoint = "vs_drift_update")]
    internal static extern unsafe int Update(
        IntPtr tracker, ref Features feat,
        out float outDist, out int outStatus,
        byte* outDiagnostic, int diagCap);

    [DllImport(Lib, EntryPoint = "vs_drift_inspect_mono8")]
    internal static extern unsafe int InspectMono8(
        IntPtr tracker, byte* src, int width, int height, int stride,
        out float outDist, out int outStatus,
        byte* outDiagnostic, int diagCap);
}
