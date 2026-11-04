using System;
using System.Runtime.InteropServices;

namespace VisionCore;

/// <summary>
/// Golden Template &amp; Variation Model Inspection Engine (Tool 8.2, AS-05).
/// "Compare to good": Learns natural appearance and process variation across
/// golden reference parts, then detects defects (scratches, stains, pinholes, voids)
/// on production parts with edge-aware tolerance and lighting drift compensation.
/// </summary>
public sealed class VariationModel : IDisposable
{
    private IntPtr _handle;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;

    public VariationModel(int width, int height, InspectNative.Params? parameters = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Width and height must be positive");

        _width = width;
        _height = height;

        var p = parameters ?? DefaultParams();
        _handle = InspectNative.Create(width, height, ref p);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create native vs_inspect variation model");
    }

    private VariationModel(IntPtr handle, int width, int height)
    {
        _handle = handle;
        _width = width;
        _height = height;
    }

    public static InspectNative.Params DefaultParams()
    {
        InspectNative.DefaultParams(out var p);
        return p;
    }

    public void Reset()
    {
        EnsureNotDisposed();
        InspectNative.Reset(_handle);
    }

    public void SetParams(InspectNative.Params p)
    {
        EnsureNotDisposed();
        int res = InspectNative.SetParams(_handle, ref p);
        if (res != 0) throw new InvalidOperationException("Failed to update parameters");
    }

    public InspectNative.Params GetParams()
    {
        EnsureNotDisposed();
        // the engine fills the struct only when its size is set (vs_inspect_get_params checks struct_size)
        var p = new InspectNative.Params { StructSize = Marshal.SizeOf<InspectNative.Params>() };
        int res = InspectNative.GetParams(_handle, ref p);
        if (res != 0) throw new InvalidOperationException($"Failed to retrieve parameters (error code: {res})");
        return p;
    }

    public void AddSample(ReadOnlySpan<byte> src, int width, int height, int stride)
    {
        EnsureNotDisposed();
        if (width != _width || height != _height || stride < width)
            throw new ArgumentException("Image dimensions must match model dimensions");

        unsafe
        {
            fixed (byte* pSrc = src)
            {
                int res = InspectNative.AddSample(_handle, pSrc, width, height, stride);
                if (res != 0)
                    throw new InvalidOperationException($"Failed to add golden sample (error code: {res})");
            }
        }
    }

    public int FinalizeTeach()
    {
        EnsureNotDisposed();
        int res = InspectNative.FinalizeTeach(_handle, out int trained);
        if (res != 0)
            throw new InvalidOperationException($"Finalize teach failed (error code: {res})");
        return trained;
    }

    public InspectNative.Result Run(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        int stride,
        Span<byte> diffOut = default,
        int diffStride = 0)
    {
        EnsureNotDisposed();
        if (width != _width || height != _height || stride < width)
            throw new ArgumentException("Image dimensions must match model dimensions");

        var result = new InspectNative.Result
        {
            StructSize = Marshal.SizeOf<InspectNative.Result>()
        };

        unsafe
        {
            fixed (byte* pSrc = src)
            fixed (byte* pDiff = diffOut)
            {
                int res = InspectNative.Run(
                    _handle,
                    pSrc,
                    width,
                    height,
                    stride,
                    ref result,
                    pDiff,
                    diffStride > 0 ? diffStride : width);

                if (res != 0)
                    throw new InvalidOperationException($"Variation inspection failed (error code: {res})");

                return result;
            }
        }
    }

    public void Save(string filepath)
    {
        EnsureNotDisposed();
        if (string.IsNullOrWhiteSpace(filepath))
            throw new ArgumentException("Filepath cannot be empty", nameof(filepath));

        int res = InspectNative.Save(_handle, filepath);
        if (res != 0)
            throw new InvalidOperationException($"Failed to save variation model to '{filepath}' (error: {res})");
    }

    public static VariationModel Load(string filepath)
    {
        if (string.IsNullOrWhiteSpace(filepath))
            throw new ArgumentException("Filepath cannot be empty", nameof(filepath));

        var (width, height) = ReadSize(filepath);
        int res = InspectNative.Load(filepath, out var handle);
        if (res != 0 || handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load variation model from '{filepath}' (error: {res})");
        var model = new VariationModel(handle, width, height);
        try
        {
            model.GetParams();   // the loaded model answers, or it is not used (requirements 17.10 AUD-08)
        }
        catch
        {
            model.Dispose();
            throw;
        }
        return model;
    }

    // the model file's header, as vs_inspect_save writes it: int32 magic 'VSIM', version 1, width, height
    private static (int Width, int Height) ReadSize(string filepath)
    {
        const uint Magic = 0x5653494D;
        try
        {
            using var r = new BinaryReader(File.OpenRead(filepath));
            uint magic = r.ReadUInt32(), version = r.ReadUInt32();
            int w = r.ReadInt32(), h = r.ReadInt32();
            if (magic != Magic || version != 1) throw new InvalidDataException("not a variation model file (version 1)");
            if (w <= 0 || h <= 0 || (long)w * h > 64L * 1024 * 1024) throw new InvalidDataException($"model size {w}×{h} is not valid");
            return (w, h);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Failed to load variation model from '{filepath}': {e.Message}", e);
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VariationModel));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_handle != IntPtr.Zero)
            {
                InspectNative.Destroy(_handle);
                _handle = IntPtr.Zero;
            }
            _disposed = true;
        }
    }
}
