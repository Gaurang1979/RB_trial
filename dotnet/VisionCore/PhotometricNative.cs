using System;
using System.Runtime.InteropServices;

namespace VisionCore;

internal static class PhotometricNative
{
    private const string Lib = "vs_photometric";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Light
    {
        public float SlantDeg;
        public float TiltDeg;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Params
    {
        public int StructSize;
        public int NumLights;
        public Light Light0;
        public Light Light1;
        public Light Light2;
        public Light Light3;
        public Light Light4;
        public Light Light5;
        public Light Light6;
        public Light Light7;
        public float ShapeContrastGain;
        public float AlbedoGamma;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void vs_photo_default_params(ref Params p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr vs_photo_create(ref Params p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void vs_photo_destroy(IntPtr pipe);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vs_photo_compute(
        IntPtr pipe,
        IntPtr[] inputImages,
        int width,
        int height,
        int stride,
        IntPtr albedoOut,
        int albedoStride,
        IntPtr shapeOut,
        int shapeStride,
        IntPtr normalMapOut,
        int normalStride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vs_photo_synthesize_lighting(
        IntPtr albedo,
        int albedoStride,
        IntPtr normalMap,
        int normalStride,
        int width,
        int height,
        float virtualTiltDeg,
        float virtualSlantDeg,
        IntPtr shadedOut,
        int shadedStride);
}
