using System;

namespace VisionCore;

/// <summary>
/// Result of an anomaly inspection.
/// </summary>
public readonly record struct AnomalyResult(
    float Score,
    AnomalyNative.AnomalyVerdict Verdict,
    AnomalyNative.Defect Defect);

/// <summary>
/// In-Memory PatchCore Anomaly Detection Engine (AG-02, Tool 10).
/// Unsupervised visual defect inspection (scratches, dents, contamination, voids)
/// using localized patch descriptors indexed in an in-memory HNSW graph.
/// Zero defect samples required for training: learns strictly from golden reference parts.
/// </summary>
public sealed class AnomalyEngine : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public AnomalyEngine(AnomalyNative.Params? parameters = null)
    {
        var p = parameters ?? DefaultParams();
        _handle = AnomalyNative.Create(ref p);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create native vs_anomaly engine");
    }

    private AnomalyEngine(IntPtr handle)
    {
        _handle = handle;
    }

    public static AnomalyNative.Params DefaultParams()
    {
        AnomalyNative.DefaultParams(out var p);
        return p;
    }

    public void Reset()
    {
        EnsureNotDisposed();
        AnomalyNative.Reset(_handle);
    }

    public void TeachImage(ReadOnlySpan<byte> src, int width, int height, int stride)
    {
        EnsureNotDisposed();
        if (width <= 0 || height <= 0 || stride < width)
            throw new ArgumentException("Invalid image dimensions or stride");

        unsafe
        {
            fixed (byte* pSrc = src)
            {
                int res = AnomalyNative.TeachImage(_handle, pSrc, width, height, stride);
                if (res != 0)
                    throw new InvalidOperationException($"Failed to teach image (error code: {res})");
            }
        }
    }

    public (float CalibratedThreshold, int CoresetVectors) FinalizeTeach()
    {
        EnsureNotDisposed();
        int res = AnomalyNative.FinalizeTeach(_handle, out float threshold, out int vectors);
        if (res != 0)
            throw new InvalidOperationException($"Finalize teach failed (error code: {res})");

        return (threshold, vectors);
    }

    public AnomalyResult Inspect(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int stride,
        Span<byte> heatmapOut = default,
        int heatmapStride = 0)
    {
        EnsureNotDisposed();
        if (width <= 0 || height <= 0 || stride < width)
            throw new ArgumentException("Invalid image dimensions or stride");

        unsafe
        {
            fixed (byte* pSrc = src)
            fixed (byte* pHeatmap = heatmapOut)
            {
                int res = AnomalyNative.InspectMono8(
                    _handle,
                    pSrc,
                    width,
                    height,
                    stride,
                    out float score,
                    out int verdict,
                    out var defect,
                    pHeatmap,
                    heatmapStride > 0 ? heatmapStride : width);

                if (res != 0)
                    throw new InvalidOperationException($"Anomaly inspection failed (error code: {res})");

                return new AnomalyResult(score, (AnomalyNative.AnomalyVerdict)verdict, defect);
            }
        }
    }

    public void SaveModel(string filepath)
    {
        EnsureNotDisposed();
        if (string.IsNullOrWhiteSpace(filepath))
            throw new ArgumentException("Filepath cannot be empty", nameof(filepath));

        int res = AnomalyNative.SaveModel(_handle, filepath);
        if (res != 0)
            throw new InvalidOperationException($"Failed to save anomaly model to '{filepath}' (error: {res})");
    }

    public static AnomalyEngine LoadModel(string filepath)
    {
        if (string.IsNullOrWhiteSpace(filepath))
            throw new ArgumentException("Filepath cannot be empty", nameof(filepath));

        int res = AnomalyNative.LoadModel(filepath, out var handle);
        if (res != 0 || handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load anomaly model from '{filepath}' (error: {res})");

        return new AnomalyEngine(handle);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AnomalyEngine));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_handle != IntPtr.Zero)
            {
                AnomalyNative.Destroy(_handle);
                _handle = IntPtr.Zero;
            }
            _disposed = true;
        }
    }
}
