using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public static class AutotuneNative
{
    private const string Lib = "vs_autotune";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct MountDiagnostic
    {
        public int StructSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public float[] ZoneSharpness;
        public float MaxSharpness;
        public float MinSharpness;
        public float MeanSharpness;
        public float UniformityRatio;
        public int IsMountTilted;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string TiltDirection;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Advice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DustDiagnostic
    {
        public int StructSize;
        public int DustSpotsFound;
        public float DustCoveragePct;
        public int IsLensContaminated;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Advice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct LightingDiagnostic
    {
        public int StructSize;
        public float MeanLuminance;
        public float MinLuminance;
        public float MaxLuminance;
        public float CornerToCenterRatio;
        public int IsDaytime;
        public float SuggestedExposureTrim;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string LightingAdvice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct ApplicatorAdvisory
    {
        public int StructSize;
        public float DeltaXMm;
        public float DeltaYMm;
        public float TotalEccentricityMm;
        public float CpkEstimate;
        public float XScrewTurns;
        public float YScrewTurns;
        public int IsDriftCritical;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string AdvisoryPill;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MountConfig
    {
        public int StructSize;
        public float MinAcceptableUniformity;
        public float MinNominalSharpness;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DustConfig
    {
        public int StructSize;
        public byte DustThresholdDn;
        public int MinSpotPx;
        public int MaxSpotPx;
        public int MaxAllowedSpots;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ApplicatorConfig
    {
        public int StructSize;
        public float PxPerMm;
        public float ScrewPitchMmPerTurn;
        public float MaxAllowedEccentricityMm;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_autotune_default_mount_config(ref MountConfig cfg);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_autotune_default_dust_config(ref DustConfig cfg);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_autotune_default_applicator_config(ref ApplicatorConfig cfg);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_autotune_check_mount_tilt(
        IntPtr img,
        int w,
        int h,
        int stride,
        ref MountConfig cfg,
        ref MountDiagnostic diag);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_autotune_check_lens_dust(
        IntPtr backgroundOrMinImg,
        int w,
        int h,
        int stride,
        ref DustConfig cfg,
        ref DustDiagnostic diag);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_autotune_strobe_minus_ambient(
        IntPtr strobeImg,
        IntPtr ambientImg,
        IntPtr cleanDst,
        int w,
        int h,
        int stride,
        out float ambientLeakagePct);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_autotune_analyze_lighting(
        IntPtr img,
        int w,
        int h,
        int stride,
        float targetMeanDn,
        ref LightingDiagnostic diag);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_autotune_applicator_guidance(
        float capCx,
        float capCy,
        float capRadius,
        float markCx,
        float markCy,
        float markRadius,
        ref ApplicatorConfig cfg,
        ref ApplicatorAdvisory advisory);
}
