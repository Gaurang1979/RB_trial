using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public static class MetamorphicNative
{
    private const string Lib = "vs_metamorphic";

    [StructLayout(LayoutKind.Sequential)]
    public struct Transform
    {
        public float RotAngleDeg;
        public float Scale;
        public float ShiftX;
        public float ShiftY;
        public float GainGamma;
        public float BiasBeta;
        public float NoiseSigma;
        public int QuietZonePadPx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public int StructSize;
        public float MaxRotationDeg;
        public int RotationSteps;
        public float MaxIllumGainDelta;
        public int IllumSteps;
        public float MaxScaleDelta;
        public int ScaleSteps;
        public float MaxNoiseSigma;
        public int NoiseSteps;
        public int MaxQuietZonePx;
        public float MaxAllowedJitterPx;
        public float MinAcceptableConfidence;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Report
    {
        public int StructSize;
        public int TotalEvaluations;
        public int VerdictFlips;
        public float MaxPositionalJitterPx;
        public float MeanPositionalJitterPx;
        public float MinObservedConfidence;
        public float MaxObservedConfidence;
        public int IsRobust;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string FailureReason;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int VisionEvalCallback(
        IntPtr img,
        int w,
        int h,
        int stride,
        IntPtr userData,
        ref float confidence,
        ref float outX,
        ref float outY,
        ref int isOk);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_metamorphic_default_config(ref Config config);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_metamorphic_apply_transform(
        IntPtr src,
        int srcW,
        int srcH,
        int srcStride,
        ref Transform t,
        IntPtr dst,
        int dstW,
        int dstH,
        int dstStride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_metamorphic_run_fuzzing_suite(
        IntPtr baselineImg,
        int w,
        int h,
        int stride,
        ref Config config,
        VisionEvalCallback evalFn,
        IntPtr userData,
        ref Report report);
}
