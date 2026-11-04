using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public static class RectifyNative
{
    public const string Lib = "vs_rectify";

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public int StructSize;
        public float LineSpeedMmS;
        public float MmPerPx;
        public float RowTimeUs;
        public float MotionAngleDeg;
        public float RefY;
        public byte FillValue;
        public byte Interpolation;
        public byte Pad1;
        public byte Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Stats
    {
        public float MaxShiftPx;
        public float ShearAngleDeg;
        public float TimeMs;
    }

    [DllImport(Lib, EntryPoint = "vs_rect_default_params")]
    internal static extern void DefaultParams(ref Params p);

    [DllImport(Lib, EntryPoint = "vs_rect_calculate_shear")]
    internal static extern int CalculateShear(ref Params p, out float kx, out float ky);

    [DllImport(Lib, EntryPoint = "vs_rect_apply_mono8")]
    internal static extern int ApplyMono8(ref Params p,
                                          IntPtr src,
                                          IntPtr dst,
                                          int width,
                                          int height,
                                          int stride,
                                          out Stats stats);

    [DllImport(Lib, EntryPoint = "vs_rect_apply_roi_mono8")]
    internal static extern int ApplyRoiMono8(ref Params p,
                                             IntPtr src,
                                             int srcWidth,
                                             int srcHeight,
                                             int srcStride,
                                             int roiX,
                                             int roiY,
                                             int roiW,
                                             int roiH,
                                             IntPtr dstRoi,
                                             int dstStride,
                                             out Stats stats);

    [DllImport(Lib, EntryPoint = "vs_rect_distorted_to_rectified")]
    internal static extern int DistortedToRectified(ref Params p,
                                                    float distX,
                                                    float distY,
                                                    out float rectX,
                                                    out float rectY);

    [DllImport(Lib, EntryPoint = "vs_rect_rectified_to_distorted")]
    internal static extern int RectifiedToDistorted(ref Params p,
                                                    float rectX,
                                                    float rectY,
                                                    out float distX,
                                                    out float distY);
}
