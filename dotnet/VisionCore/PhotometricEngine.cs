using System;
using System.Runtime.InteropServices;

namespace VisionCore;

/// <summary>
/// Computational Photometric Stereo Engine (Keyence LumiTrax / Cognex SurfaceFX alternative).
/// Decouples 2D printed ink (albedo with glare removed) from 3D surface relief (shape with print removed)
/// using 4-directional strobe illumination.
/// </summary>
public sealed class PhotometricEngine : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public int NumLights { get; }
    public float ShapeContrastGain { get; set; } = 140.0f;
    public float AlbedoGamma { get; set; } = 1.0f;

    public PhotometricEngine(float shapeContrastGain = 140.0f, float albedoGamma = 1.0f)
    {
        var p = new PhotometricNative.Params();
        PhotometricNative.vs_photo_default_params(ref p);
        p.ShapeContrastGain = shapeContrastGain;
        p.AlbedoGamma = albedoGamma;

        _handle = PhotometricNative.vs_photo_create(ref p);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to initialize native vs_photometric pipeline.");
        }

        NumLights = p.NumLights;
        ShapeContrastGain = shapeContrastGain;
        AlbedoGamma = albedoGamma;
    }

    /// <summary>
    /// Decompose 4 directional strobe images into pure 2D Albedo and pure 3D Curvature Shape images.
    /// </summary>
    public (byte[] Albedo, byte[] Shape) Compute(byte[][] inputImages, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (inputImages == null || inputImages.Length < 4)
            throw new ArgumentException("Requires at least 4 directional images.", nameof(inputImages));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");

        int stride = width;
        int size = stride * height;

        for (int i = 0; i < 4; i++)
        {
            if (inputImages[i] == null || inputImages[i].Length < size)
                throw new ArgumentException($"Image {i} buffer too small for {width}x{height}.", nameof(inputImages));
        }

        byte[] albedo = new byte[size];
        byte[] shape = new byte[size];

        GCHandle[] handles = new GCHandle[4];
        IntPtr[] ptrs = new IntPtr[4];
        GCHandle hAlbedo = default;
        GCHandle hShape = default;

        try
        {
            for (int i = 0; i < 4; i++)
            {
                handles[i] = GCHandle.Alloc(inputImages[i], GCHandleType.Pinned);
                ptrs[i] = handles[i].AddrOfPinnedObject();
            }

            hAlbedo = GCHandle.Alloc(albedo, GCHandleType.Pinned);
            hShape = GCHandle.Alloc(shape, GCHandleType.Pinned);

            int rc = PhotometricNative.vs_photo_compute(
                _handle,
                ptrs,
                width,
                height,
                stride,
                hAlbedo.AddrOfPinnedObject(),
                stride,
                hShape.AddrOfPinnedObject(),
                stride,
                IntPtr.Zero,
                0);

            if (rc != 0)
            {
                throw new InvalidOperationException($"Photometric computation failed with code {rc}.");
            }
        }
        finally
        {
            for (int i = 0; i < 4; i++)
            {
                if (handles[i].IsAllocated) handles[i].Free();
            }
            if (hAlbedo.IsAllocated) hAlbedo.Free();
            if (hShape.IsAllocated) hShape.Free();
        }

        return (albedo, shape);
    }

    /// <summary>
    /// Compute decomposition and also retrieve normal map (width * height * 3 floats).
    /// </summary>
    public (byte[] Albedo, byte[] Shape, float[] NormalMap) ComputeWithNormals(byte[][] inputImages, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (inputImages == null || inputImages.Length < 4)
            throw new ArgumentException("Requires at least 4 directional images.", nameof(inputImages));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");

        int stride = width;
        int size = stride * height;

        byte[] albedo = new byte[size];
        byte[] shape = new byte[size];
        float[] normalMap = new float[size * 3];

        GCHandle[] handles = new GCHandle[4];
        IntPtr[] ptrs = new IntPtr[4];
        GCHandle hAlbedo = default;
        GCHandle hShape = default;
        GCHandle hNormals = default;

        try
        {
            for (int i = 0; i < 4; i++)
            {
                handles[i] = GCHandle.Alloc(inputImages[i], GCHandleType.Pinned);
                ptrs[i] = handles[i].AddrOfPinnedObject();
            }

            hAlbedo = GCHandle.Alloc(albedo, GCHandleType.Pinned);
            hShape = GCHandle.Alloc(shape, GCHandleType.Pinned);
            hNormals = GCHandle.Alloc(normalMap, GCHandleType.Pinned);

            int normalStride = width * 3 * sizeof(float);
            int rc = PhotometricNative.vs_photo_compute(
                _handle,
                ptrs,
                width,
                height,
                stride,
                hAlbedo.AddrOfPinnedObject(),
                stride,
                hShape.AddrOfPinnedObject(),
                stride,
                hNormals.AddrOfPinnedObject(),
                normalStride);

            if (rc != 0)
            {
                throw new InvalidOperationException($"Photometric computation failed with code {rc}.");
            }
        }
        finally
        {
            for (int i = 0; i < 4; i++)
            {
                if (handles[i].IsAllocated) handles[i].Free();
            }
            if (hAlbedo.IsAllocated) hAlbedo.Free();
            if (hShape.IsAllocated) hShape.Free();
            if (hNormals.IsAllocated) hNormals.Free();
        }

        return (albedo, shape, normalMap);
    }

    /// <summary>
    /// Synthesizes virtual directional lighting from arbitrary tilt and slant angles.
    /// </summary>
    public static byte[] SynthesizeLighting(
        byte[] albedo,
        float[] normalMap,
        int width,
        int height,
        float virtualTiltDeg,
        float virtualSlantDeg = 45.0f)
    {
        if (albedo == null) throw new ArgumentNullException(nameof(albedo));
        if (normalMap == null) throw new ArgumentNullException(nameof(normalMap));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        int size = width * height;
        byte[] shaded = new byte[size];

        GCHandle hAlbedo = GCHandle.Alloc(albedo, GCHandleType.Pinned);
        GCHandle hNormals = GCHandle.Alloc(normalMap, GCHandleType.Pinned);
        GCHandle hShaded = GCHandle.Alloc(shaded, GCHandleType.Pinned);

        try
        {
            int normalStride = width * 3 * sizeof(float);
            int rc = PhotometricNative.vs_photo_synthesize_lighting(
                hAlbedo.AddrOfPinnedObject(),
                width,
                hNormals.AddrOfPinnedObject(),
                normalStride,
                width,
                height,
                virtualTiltDeg,
                virtualSlantDeg,
                hShaded.AddrOfPinnedObject(),
                width);

            if (rc != 0)
            {
                throw new InvalidOperationException($"Lighting synthesis failed with code {rc}.");
            }
        }
        finally
        {
            if (hAlbedo.IsAllocated) hAlbedo.Free();
            if (hNormals.IsAllocated) hNormals.Free();
            if (hShaded.IsAllocated) hShaded.Free();
        }

        return shaded;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_handle != IntPtr.Zero)
            {
                PhotometricNative.vs_photo_destroy(_handle);
                _handle = IntPtr.Zero;
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~PhotometricEngine()
    {
        Dispose();
    }
}
