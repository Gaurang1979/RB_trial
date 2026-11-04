using System;
using System.Runtime.InteropServices;

namespace VisionCore;

/// <summary>
/// Result of wavelet homomorphic inspection.
/// </summary>
public sealed record WaveletResult(
    byte[]? Residual,
    byte[]? Mask,
    int DefectPixelCount,
    float MaxScratchContrast,
    float TextureEnergySuppressed,
    TextureOrientation DetectedOrientation,
    bool IsDefective);

/// <summary>
/// Wavelet Homomorphic Scratch &amp; Micro-Defect Filter Engine (Roadmap Priority 3).
/// Suppresses periodic background textures (brushed metal, knurled caps, foil seals)
/// using multi-scale stationary wavelet decomposition and directional shrinkage.
/// </summary>
public sealed class WaveletEngine : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public int Levels { get; set; } = 2;
    public TextureOrientation TextureMode { get; set; } = TextureOrientation.Auto;
    public float SuppressionStrength { get; set; } = 0.85f;
    public float ScratchGain { get; set; } = 3.0f;
    public bool HomomorphicEnable { get; set; } = true;
    public float DefectContrastThresh { get; set; } = 35.0f;
    public int MinDefectAreaPx { get; set; } = 5;

    public WaveletEngine(
        int levels = 2,
        TextureOrientation textureMode = TextureOrientation.Auto,
        float suppressionStrength = 0.85f,
        float scratchGain = 3.0f,
        bool homomorphicEnable = true,
        float defectContrastThresh = 35.0f,
        int minDefectAreaPx = 5)
    {
        Levels = levels;
        TextureMode = textureMode;
        SuppressionStrength = suppressionStrength;
        ScratchGain = scratchGain;
        HomomorphicEnable = homomorphicEnable;
        DefectContrastThresh = defectContrastThresh;
        MinDefectAreaPx = minDefectAreaPx;

        RecreateNativeHandle();
    }

    private void RecreateNativeHandle()
    {
        if (_handle != IntPtr.Zero)
        {
            WaveletNative.vs_wavelet_destroy(_handle);
            _handle = IntPtr.Zero;
        }

        var p = new WaveletNative.Params();
        WaveletNative.vs_wavelet_default_params(ref p);
        p.Levels = Levels;
        p.TextureMode = (int)TextureMode;
        p.SuppressionStrength = SuppressionStrength;
        p.ScratchGain = ScratchGain;
        p.HomomorphicEnable = HomomorphicEnable ? 1 : 0;
        p.DefectContrastThresh = DefectContrastThresh;
        p.MinDefectAreaPx = MinDefectAreaPx;

        _handle = WaveletNative.vs_wavelet_create(ref p);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate native vs_wavelet engine.");
        }
    }

    /// <summary>
    /// Process image through wavelet homomorphic filter, isolating scratches and pinholes.
    /// </summary>
    public WaveletResult Process(
        byte[] src,
        int width,
        int height,
        bool produceResidual = true,
        bool produceMask = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (src == null) throw new ArgumentNullException(nameof(src));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        int size = width * height;
        if (src.Length < size)
            throw new ArgumentException($"Source buffer ({src.Length}) smaller than required ({size}).");

        byte[]? residual = produceResidual ? new byte[size] : null;
        byte[]? mask = produceMask ? new byte[size] : null;

        GCHandle hSrc = GCHandle.Alloc(src, GCHandleType.Pinned);
        GCHandle hRes = produceResidual ? GCHandle.Alloc(residual, GCHandleType.Pinned) : default;
        GCHandle hMask = produceMask ? GCHandle.Alloc(mask, GCHandleType.Pinned) : default;

        try
        {
            var stats = new WaveletNative.Stats();
            int rc = WaveletNative.vs_wavelet_process(
                _handle,
                hSrc.AddrOfPinnedObject(),
                width,
                height,
                width,
                produceResidual ? hRes.AddrOfPinnedObject() : IntPtr.Zero,
                width,
                produceMask ? hMask.AddrOfPinnedObject() : IntPtr.Zero,
                width,
                ref stats);

            if (rc != 0)
            {
                throw new InvalidOperationException($"Wavelet processing failed with error code {rc}.");
            }

            return new WaveletResult(
                residual,
                mask,
                stats.DefectPixelCount,
                stats.MaxScratchContrast,
                stats.TextureEnergySuppressed,
                (TextureOrientation)stats.DetectedOrientation,
                stats.IsDefective != 0);
        }
        finally
        {
            if (hSrc.IsAllocated) hSrc.Free();
            if (hRes.IsAllocated) hRes.Free();
            if (hMask.IsAllocated) hMask.Free();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_handle != IntPtr.Zero)
            {
                WaveletNative.vs_wavelet_destroy(_handle);
                _handle = IntPtr.Zero;
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~WaveletEngine()
    {
        Dispose();
    }
}
