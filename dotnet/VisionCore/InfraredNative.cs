using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public static class InfraredNative
{
    public const string Lib = "vs_infrared";

    [StructLayout(LayoutKind.Sequential)]
    public struct FillParams
    {
        public int StructSize;
        public int RoiX, RoiY, RoiW, RoiH;
        public float DatumY;
        public float PxPerMm;
        public float TargetFillMm;
        public float TolUnderfillMm;
        public float TolOverfillMm;
        public float MinMeniscusContrast;
        public int IgnoreFoam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FillResult
    {
        public int StructSize;
        public float MeniscusYPx;
        public float FillHeightMm;
        public float MeniscusContrast;
        public float FoamThicknessMm;
        public int IsUnderfill;
        public int IsOverfill;
        public int IsOk;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MetamericParams
    {
        public int StructSize;
        public float DyeNdmiThresh;
        public float CarbonNdmiMax;
        public float MinSecurityAreaPx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MetamericResult
    {
        public int StructSize;
        public int VerifiedInkPixels;
        public int TransparentDyePixels;
        public float MeanSecurityNdmi;
        public int IsAuthentic;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MoistureParams
    {
        public int StructSize;
        public float AbsorptionThresh;
        public float MinNominalBarrierPx;
        public float MinSafeBarrierPx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MoistureResult
    {
        public int StructSize;
        public float MinRemainingBarrierPx;
        public int MoisturePixelCount;
        public int HasCompleteBreach;
        public int IsSealOk;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CrackParams
    {
        public int StructSize;
        public float HessianSigma;
        public float CrackResponseThresh;
        public int MinCrackLengthPx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CrackResult
    {
        public int StructSize;
        public int CrackPixelCount;
        public float MaxCrackContrast;
        public int IsCracked;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_ir_default_fill_params(ref FillParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_ir_measure_fill_level(
        IntPtr nirImage,
        int width,
        int height,
        int stride,
        ref FillParams p,
        ref FillResult r);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_ir_default_metameric_params(ref MetamericParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_ir_verify_metameric(
        IntPtr visImage,
        int visStride,
        IntPtr nirImage,
        int nirStride,
        int width,
        int height,
        ref MetamericParams p,
        IntPtr ndmiOut,
        int ndmiStride,
        IntPtr maskOut,
        int maskStride,
        ref MetamericResult r);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_ir_default_moisture_params(ref MoistureParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_ir_detect_moisture_seam(
        IntPtr swirImage,
        int width,
        int height,
        int stride,
        int seamRoiX,
        int seamRoiY,
        int seamRoiW,
        int seamRoiH,
        ref MoistureParams p,
        IntPtr maskOut,
        int maskStride,
        ref MoistureResult r);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_ir_default_crack_params(ref CrackParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_ir_extract_cracks(
        IntPtr elImage,
        int width,
        int height,
        int stride,
        ref CrackParams p,
        IntPtr crackMaskOut,
        int maskStride,
        ref CrackResult r);
}
