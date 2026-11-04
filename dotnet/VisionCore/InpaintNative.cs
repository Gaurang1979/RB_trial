using System;
using System.Runtime.InteropServices;

namespace VisionCore;

// P/Invoke declarations matching engine/include/vs_inpaint.h.
public static class InpaintNative
{
    public const string Lib = "vs_inpaint";

    public enum InpaintMethod
    {
        Telea = 0,
        NavierStokes = 1,
        Harmonic = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public int StructSize;
        public byte GlareThreshold;
        public int HaloRadius;
        public int InpaintRadius;
        public int Method;
        public float MaxGlarePct;
        public int Iterations;
    }

    [DllImport(Lib, EntryPoint = "vs_inpaint_default_params")]
    internal static extern void DefaultParams(out Params p);

    [DllImport(Lib, EntryPoint = "vs_inpaint_detect_glare")]
    internal static extern unsafe int DetectGlare(
        byte* src, int width, int height, int srcStride,
        byte glareThreshold, int haloRadius,
        byte* maskOut, int maskStride, out int glarePixelCount);

    [DllImport(Lib, EntryPoint = "vs_inpaint_apply_mono8")]
    internal static extern unsafe int ApplyMono8(
        byte* src, int width, int height, int srcStride,
        byte* mask, int maskStride,
        byte* dst, int dstStride, ref Params p);

    [DllImport(Lib, EntryPoint = "vs_inpaint_auto_mono8")]
    internal static extern unsafe int AutoMono8(
        byte* src, int width, int height, int srcStride,
        byte* dst, int dstStride, ref Params p, out int outGlareCount);

    [DllImport(Lib, EntryPoint = "vs_inpaint_roi_mono8")]
    internal static extern unsafe int RoiMono8(
        byte* src, int width, int height, int srcStride,
        int roiX, int roiY, int roiW, int roiH,
        byte* dst, int dstStride, ref Params p, out int outGlareCount);
}
