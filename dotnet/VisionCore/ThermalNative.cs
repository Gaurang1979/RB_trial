using System;
using System.Runtime.InteropServices;

namespace VisionCore;

/// <summary>
/// P/Invoke declarations matching engine/include/vs_thermal.h.
/// Industrial Thermal Heat Seal &amp; Packaging Inspection Engine (Tool 8.4, Sun Teknovation eSaksham).
/// </summary>
public static class ThermalNative
{
    internal const string Lib = "vs_thermal";

    public enum ThermalVerdict
    {
        Pass = 0,
        NgMissingFoil = 1,
        NgIncompleteSeal = 2,
        NgOverheated = 3,
        NgUnderheated = 4,
        NgCrookedSeal = 5,
        NgSeamVoid = 6,
        NgSeamTooNarrow = 7,
        NgContamination = 8,
        NgMissingBead = 9,
        NgBrokenBead = 10,
        NgColdGlue = 11,
        NgMisplacedBead = 12,
        NgBlisterBarrierBreach = 13,
        NgJawTilt = 14,
        Error = 99
    }

    public enum ThermalPalette
    {
        Ironbow = 0,
        HotIron = 1,
        Rainbow = 2,
        Fire = 3
    }

    public enum ThermalFormat
    {
        Mono8 = 0,
        Radiometric16 = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BottleParams
    {
        public int StructSize;
        public float ExpectedCx;
        public float ExpectedCy;
        public float InnerRadiusPx;
        public float OuterRadiusPx;
        public float MinSealTempC;
        public float MaxSealTempC;
        public float MissingFoilTempC;
        public float MaxColdGapDeg;
        public float MaxTiltTempDelta;
        public float TMinCalib;
        public float TMaxCalib;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BottleResult
    {
        public int StructSize;
        public int Verdict;
        public float DetectedCx;
        public float DetectedCy;
        public float MeanSealTempC;
        public float PeakSealTempC;
        public float MinSealTempC;
        public float ColdGapDeg;
        public float ColdGapAngleDeg;
        public float TiltDeltaC;
        public float SealIntegrityPct;
        public float TimeMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PouchParams
    {
        public int StructSize;
        public int SeamRoiX;
        public int SeamRoiY;
        public int SeamRoiW;
        public int SeamRoiH;
        public int IsVertical;
        public float MinSealTempC;
        public float MinSealWidthPx;
        public float MaxVoidLengthPx;
        public float ContaminationDropC;
        public float TMinCalib;
        public float TMaxCalib;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PouchResult
    {
        public int StructSize;
        public int Verdict;
        public float MeanSeamTempC;
        public float MeanSeamWidthPx;
        public float MinSeamWidthPx;
        public int VoidCount;
        public float WorstVoidX;
        public float WorstVoidY;
        public float WorstVoidLengthPx;
        public int ContaminationFound;
        public float SealIntegrityPct;
        public float TimeMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GlueParams
    {
        public int StructSize;
        public int RoiX;
        public int RoiY;
        public int RoiW;
        public int RoiH;
        public int IsVertical;
        public int ExpectedBeadCount;
        public float MinGlueTempC;
        public float MinBeadLengthPx;
        public float MaxBeadGapPx;
        public float TMinCalib;
        public float TMaxCalib;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GlueResult
    {
        public int StructSize;
        public int Verdict;
        public int DetectedBeadCount;
        public float MeanGlueTempC;
        public float MinBeadLengthPx;
        public float WorstGapPx;
        public int TotalGlueAreaPx;
        public float BondingConfidence;
        public float TimeMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ToolingAdvice
    {
        public fixed byte FaultCode[32];
        public fixed byte RecommendedAction[256];
        public float AsymmetryMetric;
        public int Severity;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlisterParams
    {
        public int StructSize;
        public int CardRoiX;
        public int CardRoiY;
        public int CardRoiW;
        public int CardRoiH;
        public int Rows;
        public int Cols;
        public float PocketWPx;
        public float PocketHPx;
        public float PitchXPx;
        public float PitchYPx;
        public float MinBarrierWidthPx;
        public float MinSealTempC;
        public float TMinCalib;
        public float TMaxCalib;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlisterResult
    {
        public int StructSize;
        public int Verdict;
        public int CompromisedCavityIdx;
        public float MinBarrierWidthPx;
        public float WorstBarrierX;
        public float WorstBarrierY;
        public float MeanFlangeTempC;
        public ToolingAdvice Advice;
        public float TimeMs;
    }

    [DllImport(Lib, EntryPoint = "vs_thermal_default_bottle_params")]
    internal static extern void DefaultBottleParams(out BottleParams p);

    [DllImport(Lib, EntryPoint = "vs_thermal_default_pouch_params")]
    internal static extern void DefaultPouchParams(out PouchParams p);

    [DllImport(Lib, EntryPoint = "vs_thermal_default_glue_params")]
    internal static extern void DefaultGlueParams(out GlueParams p);

    [DllImport(Lib, EntryPoint = "vs_thermal_default_blister_params")]
    internal static extern void DefaultBlisterParams(out BlisterParams p);

    [DllImport(Lib, EntryPoint = "vs_thermal_inspect_bottle_seal")]
    internal static extern unsafe int InspectBottleSeal(
        void* src,
        int width,
        int height,
        int stride,
        int format,
        ref BottleParams p,
        out BottleResult outResult);

    [DllImport(Lib, EntryPoint = "vs_thermal_inspect_pouch_seam")]
    internal static extern unsafe int InspectPouchSeam(
        void* src,
        int width,
        int height,
        int stride,
        int format,
        ref PouchParams p,
        out PouchResult outResult);

    [DllImport(Lib, EntryPoint = "vs_thermal_inspect_hot_glue")]
    internal static extern unsafe int InspectHotGlue(
        void* src,
        int width,
        int height,
        int stride,
        int format,
        ref GlueParams p,
        out GlueResult outResult);

    [DllImport(Lib, EntryPoint = "vs_thermal_inspect_blister")]
    internal static extern unsafe int InspectBlister(
        void* src,
        int width,
        int height,
        int stride,
        int format,
        ref BlisterParams p,
        out BlisterResult outResult);

    [DllImport(Lib, EntryPoint = "vs_thermal_diagnose_tooling")]
    internal static extern void DiagnoseTooling(
        int packagingType,
        int verdict,
        float asymmetryMetric,
        out ToolingAdvice outAdvice);

    [DllImport(Lib, EntryPoint = "vs_thermal_apply_palette")]
    internal static extern unsafe int ApplyPalette(
        void* src,
        int width,
        int height,
        int stride,
        int format,
        float tMinC,
        float tMaxC,
        int palette,
        byte* dst,
        int dstStride);
}
