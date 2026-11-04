using System;

namespace VisionCore;

/// <summary>
/// High-level .NET wrapper for the Specular Highlight Glare Inpainter Tool (AG-07).
/// Reconstructs Data Matrix clock tracks, QR finder patterns, 1D barcode bars, and characters
/// obliterated by plastic shrink-wrap reflection or saturated glare.
/// </summary>
public static class InpaintEngine
{
    public static InpaintNative.Params DefaultParams()
    {
        InpaintNative.DefaultParams(out var p);
        return p;
    }

    public static int DetectGlare(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int srcStride,
        byte glareThreshold,
        int haloRadius,
        Span<byte> maskOut,
        int maskStride,
        out int glarePixelCount)
    {
        if (width <= 0 || height <= 0 || srcStride < width || maskStride < width)
            throw new ArgumentException("Invalid image dimensions or stride");

        unsafe
        {
            fixed (byte* pSrc = src)
            fixed (byte* pMask = maskOut)
            {
                return InpaintNative.DetectGlare(
                    pSrc, width, height, srcStride,
                    glareThreshold, haloRadius,
                    pMask, maskStride, out glarePixelCount);
            }
        }
    }

    public static int Apply(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int srcStride,
        ReadOnlySpan<byte> mask,
        int maskStride,
        Span<byte> dst,
        int dstStride,
        InpaintNative.Params? parameters = null)
    {
        if (width <= 0 || height <= 0 || srcStride < width || maskStride < width || dstStride < width)
            throw new ArgumentException("Invalid image dimensions or stride");

        var p = parameters ?? DefaultParams();

        unsafe
        {
            fixed (byte* pSrc = src)
            fixed (byte* pMask = mask)
            fixed (byte* pDst = dst)
            {
                return InpaintNative.ApplyMono8(
                    pSrc, width, height, srcStride,
                    pMask, maskStride,
                    pDst, dstStride, ref p);
            }
        }
    }

    public static int InpaintAuto(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int srcStride,
        Span<byte> dst,
        int dstStride,
        out int glareCount,
        InpaintNative.Params? parameters = null)
    {
        if (width <= 0 || height <= 0 || srcStride < width || dstStride < width)
            throw new ArgumentException("Invalid image dimensions or stride");

        var p = parameters ?? DefaultParams();

        unsafe
        {
            fixed (byte* pSrc = src)
            fixed (byte* pDst = dst)
            {
                return InpaintNative.AutoMono8(
                    pSrc, width, height, srcStride,
                    pDst, dstStride, ref p, out glareCount);
            }
        }
    }

    public static int InpaintRoi(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int srcStride,
        int roiX,
        int roiY,
        int roiW,
        int roiH,
        Span<byte> dst,
        int dstStride,
        out int glareCount,
        InpaintNative.Params? parameters = null)
    {
        if (width <= 0 || height <= 0 || roiW <= 0 || roiH <= 0 ||
            roiX < 0 || roiY < 0 || roiX + roiW > width || roiY + roiH > height ||
            srcStride < width || dstStride < roiW)
            throw new ArgumentException("Invalid ROI crop boundaries");

        var p = parameters ?? DefaultParams();

        unsafe
        {
            fixed (byte* pSrc = src)
            fixed (byte* pDst = dst)
            {
                return InpaintNative.RoiMono8(
                    pSrc, width, height, srcStride,
                    roiX, roiY, roiW, roiH,
                    pDst, dstStride, ref p, out glareCount);
            }
        }
    }
}
