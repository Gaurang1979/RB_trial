using System;
using System.Text;

namespace VisionCore;

/// <summary>
/// Optical Drift &amp; Out-Of-Distribution (OOD) Detector (AG-12, AS-13).
/// Tracks optical condition (LED fading, lens contamination, focus degradation)
/// using Mahalanobis distance over moving production windows to warn before false rejects occur.
/// </summary>
public sealed class DriftTracker : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public DriftTracker(DriftNative.Params? parameters = null)
    {
        var p = parameters ?? DefaultParams();
        _handle = DriftNative.Create(ref p);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create native vs_drift tracker");
    }

    public static DriftNative.Params DefaultParams()
    {
        DriftNative.DefaultParams(out var p);
        return p;
    }

    public static DriftNative.Features ExtractFeatures(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int stride,
        int gridStep = 4)
    {
        if (width <= 0 || height <= 0 || stride < width)
            throw new ArgumentException("Invalid image dimensions or stride");

        unsafe
        {
            fixed (byte* pSrc = src)
            {
                int res = DriftNative.ExtractFeatures(pSrc, width, height, stride, gridStep, out var feat);
                if (res != 0) throw new InvalidOperationException("Feature extraction failed");
                return feat;
            }
        }
    }

    public void Reset()
    {
        EnsureNotDisposed();
        DriftNative.Reset(_handle);
    }

    public void TeachSample(DriftNative.Features feat)
    {
        EnsureNotDisposed();
        int res = DriftNative.TeachSample(_handle, ref feat);
        if (res != 0) throw new InvalidOperationException("Failed to add teach sample");
    }

    public void FinalizeTeach()
    {
        EnsureNotDisposed();
        int res = DriftNative.FinalizeTeach(_handle);
        if (res != 0) throw new InvalidOperationException("Finalize teach failed (minimum 5 samples required)");
    }

    public (float Distance, DriftNative.DriftStatus Status, string Diagnostic) Update(DriftNative.Features feat)
    {
        EnsureNotDisposed();
        var buf = new byte[256];
        unsafe
        {
            fixed (byte* pDiag = buf)
            {
                int res = DriftNative.Update(_handle, ref feat, out float dist, out int status, pDiag, buf.Length);
                if (res != 0) throw new InvalidOperationException("Drift tracker update failed");
                string diagStr = MarshalUtf8(buf);
                return (dist, (DriftNative.DriftStatus)status, diagStr);
            }
        }
    }

    public (float Distance, DriftNative.DriftStatus Status, string Diagnostic) Inspect(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int stride)
    {
        EnsureNotDisposed();
        var buf = new byte[256];
        unsafe
        {
            fixed (byte* pSrc = src)
            fixed (byte* pDiag = buf)
            {
                int res = DriftNative.InspectMono8(_handle, pSrc, width, height, stride, out float dist, out int status, pDiag, buf.Length);
                if (res != 0) throw new InvalidOperationException("Drift inspection failed");
                string diagStr = MarshalUtf8(buf);
                return (dist, (DriftNative.DriftStatus)status, diagStr);
            }
        }
    }

    private static string MarshalUtf8(byte[] buf)
    {
        int len = 0;
        while (len < buf.Length && buf[len] != 0) len++;
        return Encoding.UTF8.GetString(buf, 0, len);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DriftTracker));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_handle != IntPtr.Zero)
            {
                DriftNative.Destroy(_handle);
                _handle = IntPtr.Zero;
            }
            _disposed = true;
        }
    }
}
