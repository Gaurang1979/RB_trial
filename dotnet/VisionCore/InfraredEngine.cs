using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public sealed record InfraredFillResult(
    float MeniscusYPx,
    float FillHeightMm,
    float MeniscusContrast,
    float FoamThicknessMm,
    bool IsUnderfill,
    bool IsOverfill,
    bool IsOk);

public sealed record InfraredMetamericResult(
    int VerifiedInkPixels,
    int TransparentDyePixels,
    float MeanSecurityNdmi,
    bool IsAuthentic,
    byte[]? NdmiMap,
    byte[]? SecurityMask);

public sealed record InfraredMoistureResult(
    float MinRemainingBarrierPx,
    int MoisturePixelCount,
    bool HasCompleteBreach,
    bool IsSealOk,
    byte[]? MoistureMask);

public sealed record InfraredCrackResult(
    int CrackPixelCount,
    float MaxCrackContrast,
    bool IsCracked,
    byte[]? CrackMask);

/// <summary>
/// Infrared Vision Inspection Engine (Tool 9).
/// Near-Infrared (No-IR-filter CMOS 750-1050nm) and Short-Wave Infrared (SWIR 900-1700nm).
/// </summary>
public static class InfraredEngine
{
    /// <summary>
    /// Inspect liquid fill level and foam thickness through amber/dark glass/PET with NIR backlighting.
    /// </summary>
    public static InfraredFillResult MeasureFillLevel(
        byte[] nirImage,
        int width,
        int height,
        int roiX,
        int roiY,
        int roiW,
        int roiH,
        float datumY,
        float pxPerMm = 10.0f,
        float targetFillMm = 50.0f,
        float tolUnderfillMm = 2.0f,
        float tolOverfillMm = 2.0f,
        bool ignoreFoam = true)
    {
        if (nirImage == null) throw new ArgumentNullException(nameof(nirImage));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        var p = new InfraredNative.FillParams();
        InfraredNative.vs_ir_default_fill_params(ref p);
        p.RoiX = roiX;
        p.RoiY = roiY;
        p.RoiW = roiW;
        p.RoiH = roiH;
        p.DatumY = datumY;
        p.PxPerMm = pxPerMm;
        p.TargetFillMm = targetFillMm;
        p.TolUnderfillMm = tolUnderfillMm;
        p.TolOverfillMm = tolOverfillMm;
        p.IgnoreFoam = ignoreFoam ? 1 : 0;

        var r = new InfraredNative.FillResult();
        GCHandle hImg = GCHandle.Alloc(nirImage, GCHandleType.Pinned);
        try
        {
            int rc = InfraredNative.vs_ir_measure_fill_level(
                hImg.AddrOfPinnedObject(),
                width,
                height,
                width,
                ref p,
                ref r);

            if (rc != 0) throw new InvalidOperationException($"Fill measurement failed with error {rc}.");

            return new InfraredFillResult(
                r.MeniscusYPx,
                r.FillHeightMm,
                r.MeniscusContrast,
                r.FoamThicknessMm,
                r.IsUnderfill != 0,
                r.IsOverfill != 0,
                r.IsOk != 0);
        }
        finally
        {
            if (hImg.IsAllocated) hImg.Free();
        }
    }

    /// <summary>
    /// Compares Visible (VIS) and Near-Infrared (NIR) frames to verify authentic security ink vs counterfeit dye.
    /// </summary>
    public static InfraredMetamericResult VerifyMetameric(
        byte[] visImage,
        byte[] nirImage,
        int width,
        int height,
        float dyeNdmiThresh = 0.25f,
        float carbonNdmiMax = 0.15f,
        int minSecurityAreaPx = 50,
        bool generateOutputs = true)
    {
        if (visImage == null) throw new ArgumentNullException(nameof(visImage));
        if (nirImage == null) throw new ArgumentNullException(nameof(nirImage));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        int size = width * height;
        byte[]? ndmiMap = generateOutputs ? new byte[size] : null;
        byte[]? mask = generateOutputs ? new byte[size] : null;

        var p = new InfraredNative.MetamericParams();
        InfraredNative.vs_ir_default_metameric_params(ref p);
        p.DyeNdmiThresh = dyeNdmiThresh;
        p.CarbonNdmiMax = carbonNdmiMax;
        p.MinSecurityAreaPx = minSecurityAreaPx;

        var r = new InfraredNative.MetamericResult();
        GCHandle hVis = GCHandle.Alloc(visImage, GCHandleType.Pinned);
        GCHandle hNir = GCHandle.Alloc(nirImage, GCHandleType.Pinned);
        GCHandle hNdmi = generateOutputs ? GCHandle.Alloc(ndmiMap, GCHandleType.Pinned) : default;
        GCHandle hMask = generateOutputs ? GCHandle.Alloc(mask, GCHandleType.Pinned) : default;

        try
        {
            int rc = InfraredNative.vs_ir_verify_metameric(
                hVis.AddrOfPinnedObject(),
                width,
                hNir.AddrOfPinnedObject(),
                width,
                width,
                height,
                ref p,
                generateOutputs ? hNdmi.AddrOfPinnedObject() : IntPtr.Zero,
                width,
                generateOutputs ? hMask.AddrOfPinnedObject() : IntPtr.Zero,
                width,
                ref r);

            if (rc != 0) throw new InvalidOperationException($"Metameric verification failed with error {rc}.");

            return new InfraredMetamericResult(
                r.VerifiedInkPixels,
                r.TransparentDyePixels,
                r.MeanSecurityNdmi,
                r.IsAuthentic != 0,
                ndmiMap,
                mask);
        }
        finally
        {
            if (hVis.IsAllocated) hVis.Free();
            if (hNir.IsAllocated) hNir.Free();
            if (hNdmi.IsAllocated) hNdmi.Free();
            if (hMask.IsAllocated) hMask.Free();
        }
    }

    /// <summary>
    /// Detects trapped liquid or moisture breach in heat-sealed pouch or blister seams via SWIR 1450nm absorption.
    /// </summary>
    public static InfraredMoistureResult DetectMoistureSeam(
        byte[] swirImage,
        int width,
        int height,
        int seamRoiX,
        int seamRoiY,
        int seamRoiW,
        int seamRoiH,
        float absorptionThresh = 40.0f,
        float minNominalBarrierPx = 30.0f,
        float minSafeBarrierPx = 15.0f,
        bool generateMask = true)
    {
        if (swirImage == null) throw new ArgumentNullException(nameof(swirImage));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        int size = width * height;
        byte[]? mask = generateMask ? new byte[size] : null;

        var p = new InfraredNative.MoistureParams();
        InfraredNative.vs_ir_default_moisture_params(ref p);
        p.AbsorptionThresh = absorptionThresh;
        p.MinNominalBarrierPx = minNominalBarrierPx;
        p.MinSafeBarrierPx = minSafeBarrierPx;

        var r = new InfraredNative.MoistureResult();
        GCHandle hImg = GCHandle.Alloc(swirImage, GCHandleType.Pinned);
        GCHandle hMask = generateMask ? GCHandle.Alloc(mask, GCHandleType.Pinned) : default;

        try
        {
            int rc = InfraredNative.vs_ir_detect_moisture_seam(
                hImg.AddrOfPinnedObject(),
                width,
                height,
                width,
                seamRoiX,
                seamRoiY,
                seamRoiW,
                seamRoiH,
                ref p,
                generateMask ? hMask.AddrOfPinnedObject() : IntPtr.Zero,
                width,
                ref r);

            if (rc != 0) throw new InvalidOperationException($"Moisture inspection failed with error {rc}.");

            return new InfraredMoistureResult(
                r.MinRemainingBarrierPx,
                r.MoisturePixelCount,
                r.HasCompleteBreach != 0,
                r.IsSealOk != 0,
                mask);
        }
        finally
        {
            if (hImg.IsAllocated) hImg.Free();
            if (hMask.IsAllocated) hMask.Free();
        }
    }

    /// <summary>
    /// Extracts micro-cracks and fracture lines from solar cell electroluminescence (EL) or silicon transmission.
    /// </summary>
    public static InfraredCrackResult ExtractCracks(
        byte[] elImage,
        int width,
        int height,
        float crackResponseThresh = 12.0f,
        int minCrackLengthPx = 8,
        bool generateMask = true)
    {
        if (elImage == null) throw new ArgumentNullException(nameof(elImage));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        int size = width * height;
        byte[]? mask = generateMask ? new byte[size] : null;

        var p = new InfraredNative.CrackParams();
        InfraredNative.vs_ir_default_crack_params(ref p);
        p.CrackResponseThresh = crackResponseThresh;
        p.MinCrackLengthPx = minCrackLengthPx;

        var r = new InfraredNative.CrackResult();
        GCHandle hImg = GCHandle.Alloc(elImage, GCHandleType.Pinned);
        GCHandle hMask = generateMask ? GCHandle.Alloc(mask, GCHandleType.Pinned) : default;

        try
        {
            int rc = InfraredNative.vs_ir_extract_cracks(
                hImg.AddrOfPinnedObject(),
                width,
                height,
                width,
                ref p,
                generateMask ? hMask.AddrOfPinnedObject() : IntPtr.Zero,
                width,
                ref r);

            if (rc != 0) throw new InvalidOperationException($"Crack extraction failed with error {rc}.");

            return new InfraredCrackResult(
                r.CrackPixelCount,
                r.MaxCrackContrast,
                r.IsCracked != 0,
                mask);
        }
        finally
        {
            if (hImg.IsAllocated) hImg.Free();
            if (hMask.IsAllocated) hMask.Free();
        }
    }
}
