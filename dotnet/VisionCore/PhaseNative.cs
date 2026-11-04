using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public static class PhaseNative
{
    private const string Lib = "vs_phase";

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public int StructSize;
        public int EstimateRotation;
        public int EstimateScale;
        public float MinCorrelationScore;
        public float MinPsr;
        public float MinScale;
        public float MaxScale;
        public int ApplyHannWindow;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Result
    {
        public int StructSize;
        public float ShiftX;
        public float ShiftY;
        public float RotationDeg;
        public float Scale;
        public float CorrelationScore;
        public float Psr;
        public int IsFound;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_phase_default_params(ref Params p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vs_phase_create(ref Params p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_phase_destroy(IntPtr c);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_phase_teach(
        IntPtr c,
        IntPtr goldenTemplate,
        int width,
        int height,
        int stride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_phase_match(
        IntPtr c,
        IntPtr testImage,
        int width,
        int height,
        int stride,
        ref Result result);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_phase_correlate_translation(
        IntPtr img1,
        int stride1,
        IntPtr img2,
        int stride2,
        int width,
        int height,
        ref float shiftX,
        ref float shiftY,
        ref float score,
        ref float psr);
}
