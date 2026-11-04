using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public static class PolarNative
{
    public const string Lib = "vs_polar";

    public enum MosaicLayout
    {
        SonyPolarsens = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public int StructSize;
        public int MosaicLayout;
        public float GlareSuppressAlpha;
        public float SpecularGain;
        public float DolpDefectThresh;
        public float StressGradientThresh;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Stats
    {
        public int StructSize;
        public float MeanDolp;
        public float MaxDolp;
        public int GlarePixelCount;
        public float MaxStressGradient;
        public int IsStressDefective;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_polar_default_params(ref Params p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_polar_process_channels(
        IntPtr i0,
        IntPtr i45,
        IntPtr i90,
        IntPtr i135,
        int width,
        int height,
        int stride,
        ref Params p,
        IntPtr diffuseOut,
        int diffuseStride,
        IntPtr specularOut,
        int specularStride,
        IntPtr dolpOut,
        int dolpStride,
        IntPtr aolpOut,
        int aolpStride,
        ref Stats statsOut);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_polar_process_mosaic(
        IntPtr rawMosaic,
        int width,
        int height,
        int stride,
        ref Params p,
        IntPtr diffuseOut,
        int diffuseStride,
        IntPtr specularOut,
        int specularStride,
        IntPtr dolpOut,
        int dolpStride,
        IntPtr aolpOut,
        int aolpStride,
        ref Stats statsOut);
}
