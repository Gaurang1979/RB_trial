using System;
using System.Runtime.InteropServices;

namespace VisionCore;

/// <summary>
/// Industrial Thermal Heat Seal &amp; Packaging Inspection Engine (Tool 8.4, Sun Teknovation eSaksham).
/// Provides non-destructive thermal validation of:
///   1. Bottle induction cap sealing (foil presence, continuous heat ring, overheat check, cap tilt).
///   2. Flexible pouch and tray heat seal seams (cold channel voids, seam thinning, trapped product).
///   3. Hot melt adhesive carton flaps (bead count, broken beads, glue temperature, cold joints).
/// </summary>
public static class ThermalEngine
{
    public static ThermalNative.BottleParams DefaultBottleParams()
    {
        ThermalNative.DefaultBottleParams(out var p);
        return p;
    }

    public static ThermalNative.PouchParams DefaultPouchParams()
    {
        ThermalNative.DefaultPouchParams(out var p);
        return p;
    }

    public static ThermalNative.GlueParams DefaultGlueParams()
    {
        ThermalNative.DefaultGlueParams(out var p);
        return p;
    }

    public static ThermalNative.BlisterParams DefaultBlisterParams()
    {
        ThermalNative.DefaultBlisterParams(out var p);
        return p;
    }

    public static ThermalNative.BottleResult InspectBottleSeal(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int stride,
        ThermalNative.BottleParams? parameters = null,
        ThermalNative.ThermalFormat format = ThermalNative.ThermalFormat.Mono8)
    {
        if (width <= 0 || height <= 0 || stride <= 0)
            throw new ArgumentException("Invalid image dimensions or stride");

        var p = parameters ?? DefaultBottleParams();

        unsafe
        {
            fixed (byte* pSrc = src)
            {
                int res = ThermalNative.InspectBottleSeal(
                    pSrc,
                    width,
                    height,
                    stride,
                    (int)format,
                    ref p,
                    out var outResult);

                if (res != 0)
                    throw new InvalidOperationException($"Thermal bottle inspection failed (error: {res})");

                return outResult;
            }
        }
    }

    public static ThermalNative.BottleResult InspectBottleSealRadiometric(
        ReadOnlySpan<ushort> rad16,
        int width,
        int height,
        int strideBytes,
        ThermalNative.BottleParams? parameters = null)
    {
        if (width <= 0 || height <= 0 || strideBytes <= 0)
            throw new ArgumentException("Invalid radiometric dimensions or stride");

        var p = parameters ?? DefaultBottleParams();

        unsafe
        {
            fixed (ushort* pSrc = rad16)
            {
                int res = ThermalNative.InspectBottleSeal(
                    pSrc,
                    width,
                    height,
                    strideBytes,
                    (int)ThermalNative.ThermalFormat.Radiometric16,
                    ref p,
                    out var outResult);

                if (res != 0)
                    throw new InvalidOperationException($"Radiometric bottle inspection failed (error: {res})");

                return outResult;
            }
        }
    }

    public static ThermalNative.PouchResult InspectPouchSeam(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int stride,
        ThermalNative.PouchParams? parameters = null,
        ThermalNative.ThermalFormat format = ThermalNative.ThermalFormat.Mono8)
    {
        if (width <= 0 || height <= 0 || stride <= 0)
            throw new ArgumentException("Invalid image dimensions or stride");

        var p = parameters ?? DefaultPouchParams();

        unsafe
        {
            fixed (byte* pSrc = src)
            {
                int res = ThermalNative.InspectPouchSeam(
                    pSrc,
                    width,
                    height,
                    stride,
                    (int)format,
                    ref p,
                    out var outResult);

                if (res != 0)
                    throw new InvalidOperationException($"Thermal pouch inspection failed (error: {res})");

                return outResult;
            }
        }
    }

    public static ThermalNative.GlueResult InspectHotGlue(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int stride,
        ThermalNative.GlueParams? parameters = null,
        ThermalNative.ThermalFormat format = ThermalNative.ThermalFormat.Mono8)
    {
        if (width <= 0 || height <= 0 || stride <= 0)
            throw new ArgumentException("Invalid image dimensions or stride");

        var p = parameters ?? DefaultGlueParams();

        unsafe
        {
            fixed (byte* pSrc = src)
            {
                int res = ThermalNative.InspectHotGlue(
                    pSrc,
                    width,
                    height,
                    stride,
                    (int)format,
                    ref p,
                    out var outResult);

                if (res != 0)
                    throw new InvalidOperationException($"Hot glue inspection failed (error: {res})");

                return outResult;
            }
        }
    }

    public static ThermalNative.BlisterResult InspectBlister(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int stride,
        ThermalNative.BlisterParams? parameters = null,
        ThermalNative.ThermalFormat format = ThermalNative.ThermalFormat.Mono8)
    {
        if (width <= 0 || height <= 0 || stride <= 0)
            throw new ArgumentException("Invalid image dimensions or stride");

        var p = parameters ?? DefaultBlisterParams();

        unsafe
        {
            fixed (byte* pSrc = src)
            {
                int res = ThermalNative.InspectBlister(
                    pSrc,
                    width,
                    height,
                    stride,
                    (int)format,
                    ref p,
                    out var outResult);

                if (res != 0)
                    throw new InvalidOperationException($"Blister inspection failed (error: {res})");

                return outResult;
            }
        }
    }

    public static (string FaultCode, string RecommendedAction, int Severity) DiagnoseTooling(
        int packagingType,
        ThermalNative.ThermalVerdict verdict,
        float asymmetryMetric = 0.0f)
    {
        ThermalNative.DiagnoseTooling(packagingType, (int)verdict, asymmetryMetric, out var advice);

        unsafe
        {
            string code = MarshalUtf8(advice.FaultCode, 32);
            string action = MarshalUtf8(advice.RecommendedAction, 256);
            return (code, action, advice.Severity);
        }
    }

    private static unsafe string MarshalUtf8(byte* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != 0) len++;
        return System.Text.Encoding.UTF8.GetString(ptr, len);
    }

    public static void ApplyPalette(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int stride,
        Span<byte> dstBgr,
        int dstStride,
        float tMinC,
        float tMaxC,
        ThermalNative.ThermalPalette palette = ThermalNative.ThermalPalette.Ironbow)
    {
        if (width <= 0 || height <= 0 || stride <= 0 || dstStride <= 0)
            throw new ArgumentException("Invalid dimensions or stride");

        unsafe
        {
            fixed (byte* pSrc = src)
            fixed (byte* pDst = dstBgr)
            {
                int res = ThermalNative.ApplyPalette(
                    pSrc,
                    width,
                    height,
                    stride,
                    (int)ThermalNative.ThermalFormat.Mono8,
                    tMinC,
                    tMaxC,
                    (int)palette,
                    pDst,
                    dstStride);

                if (res != 0)
                    throw new InvalidOperationException($"Palette synthesis failed (error: {res})");
            }
        }
    }
}
