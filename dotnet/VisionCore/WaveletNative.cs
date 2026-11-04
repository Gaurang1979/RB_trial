using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public enum TextureOrientation
{
    Auto = 0,
    Horizontal = 1,
    Vertical = 2,
    Diagonal = 3,
    KnurledGrid = 4
}

internal static class WaveletNative
{
    private const string Lib = "vs_wavelet";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Params
    {
        public int StructSize;
        public int Levels;
        public int TextureMode;
        public float SuppressionStrength;
        public float ScratchGain;
        public int HomomorphicEnable;
        public float DefectContrastThresh;
        public int MinDefectAreaPx;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Stats
    {
        public int StructSize;
        public int DefectPixelCount;
        public float MaxScratchContrast;
        public float TextureEnergySuppressed;
        public int DetectedOrientation;
        public int IsDefective;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void vs_wavelet_default_params(ref Params p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr vs_wavelet_create(ref Params p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void vs_wavelet_destroy(IntPtr filter);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vs_wavelet_process(
        IntPtr filter,
        IntPtr src,
        int width,
        int height,
        int stride,
        IntPtr residualOut,
        int residualStride,
        IntPtr maskOut,
        int maskStride,
        ref Stats statsOut);
}
