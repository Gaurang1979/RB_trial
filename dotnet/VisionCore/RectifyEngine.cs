using System;

namespace VisionCore;

public sealed class RectifyOptions
{
    public float LineSpeedMmS { get; set; } = 0.0f;
    public float MmPerPx { get; set; } = 0.08f;
    public float RowTimeUs { get; set; } = 22.5f;
    public float MotionAngleDeg { get; set; } = 0.0f;
    public float RefY { get; set; } = -1.0f;
    public byte FillValue { get; set; } = 0;
    public bool Bilinear { get; set; } = true;
}

public readonly record struct RectifyReport(float MaxShiftPx, float ShearAngleDeg, float TimeMs);

public static class RectifyEngine
{
    public static (float Kx, float Ky) CalculateShear(RectifyOptions options)
    {
        var p = ToNativeParams(options);
        int rc = RectifyNative.CalculateShear(ref p, out float kx, out float ky);
        if (rc != 0) throw new InvalidOperationException($"vs_rect_calculate_shear failed with code {rc}");
        return (kx, ky);
    }

    public static unsafe RectifyReport RectifyInPlace(byte[] pixels, int width, int height, int stride, RectifyOptions options)
    {
        if (pixels == null) throw new ArgumentNullException(nameof(pixels));
        if (width <= 0 || height <= 0 || stride < width) throw new ArgumentException("Invalid dimensions");

        var p = ToNativeParams(options);
        fixed (byte* ptr = pixels)
        {
            int rc = RectifyNative.ApplyMono8(ref p, (IntPtr)ptr, (IntPtr)ptr, width, height, stride, out var stats);
            if (rc != 0) throw new InvalidOperationException($"vs_rect_apply_mono8 failed with code {rc}");
            return new RectifyReport(stats.MaxShiftPx, stats.ShearAngleDeg, stats.TimeMs);
        }
    }

    public static unsafe (byte[] Rectified, RectifyReport Report) Rectify(byte[] src, int width, int height, int stride, RectifyOptions options)
    {
        if (src == null) throw new ArgumentNullException(nameof(src));
        if (width <= 0 || height <= 0 || stride < width) throw new ArgumentException("Invalid dimensions");

        byte[] dst = new byte[src.Length];
        var p = ToNativeParams(options);
        fixed (byte* srcPtr = src)
        fixed (byte* dstPtr = dst)
        {
            int rc = RectifyNative.ApplyMono8(ref p, (IntPtr)srcPtr, (IntPtr)dstPtr, width, height, stride, out var stats);
            if (rc != 0) throw new InvalidOperationException($"vs_rect_apply_mono8 failed with code {rc}");
            return (dst, new RectifyReport(stats.MaxShiftPx, stats.ShearAngleDeg, stats.TimeMs));
        }
    }

    public static (float X, float Y) DistortedToRectified(float distX, float distY, RectifyOptions options)
    {
        var p = ToNativeParams(options);
        int rc = RectifyNative.DistortedToRectified(ref p, distX, distY, out float rx, out float ry);
        if (rc != 0) throw new InvalidOperationException($"vs_rect_distorted_to_rectified failed with code {rc}");
        return (rx, ry);
    }

    public static (float X, float Y) RectifiedToDistorted(float rectX, float rectY, RectifyOptions options)
    {
        var p = ToNativeParams(options);
        int rc = RectifyNative.RectifiedToDistorted(ref p, rectX, rectY, out float dx, out float dy);
        if (rc != 0) throw new InvalidOperationException($"vs_rect_rectified_to_distorted failed with code {rc}");
        return (dx, dy);
    }

    private static RectifyNative.Params ToNativeParams(RectifyOptions opt)
    {
        var p = new RectifyNative.Params();
        RectifyNative.DefaultParams(ref p);
        if (opt != null)
        {
            p.LineSpeedMmS = opt.LineSpeedMmS;
            p.MmPerPx = opt.MmPerPx;
            p.RowTimeUs = opt.RowTimeUs;
            p.MotionAngleDeg = opt.MotionAngleDeg;
            p.RefY = opt.RefY;
            p.FillValue = opt.FillValue;
            p.Interpolation = opt.Bilinear ? (byte)1 : (byte)0;
        }
        return p;
    }
}
