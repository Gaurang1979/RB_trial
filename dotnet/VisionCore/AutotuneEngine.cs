using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public sealed record MountReport(
    float[] ZoneSharpness,
    float MaxSharpness,
    float MinSharpness,
    float MeanSharpness,
    float UniformityRatio,
    bool IsMountTilted,
    string TiltDirection,
    string Advice);

public sealed record DustReport(
    int DustSpotsFound,
    float DustCoveragePct,
    bool IsLensContaminated,
    string Advice);

public sealed record LightingReport(
    float MeanLuminance,
    float MinLuminance,
    float MaxLuminance,
    float CornerToCenterRatio,
    bool IsDaytime,
    float SuggestedExposureTrim,
    string LightingAdvice);

public sealed record ApplicatorReport(
    float DeltaXMm,
    float DeltaYMm,
    float TotalEccentricityMm,
    float CpkEstimate,
    float XScrewTurns,
    float YScrewTurns,
    bool IsDriftCritical,
    string AdvisoryPill);

/// <summary>
/// Autonomous Environmental Adaptation &amp; Optical Health Engine (Roadmap Tool 12 / AI Auto-Tune).
/// Provides inline Mount Tilt (Scheimpflug) diagnostics, Lens Dust detection, Strobe-Minus-Ambient
/// sunlight decoupling, Lighting circadian analysis, and closed-loop Applicator Guidance.
/// </summary>
public static class AutotuneEngine
{
    public static MountReport CheckMountTilt(
        byte[] image,
        int width,
        int height,
        float minUniformity = 0.65f,
        float minSharpness = 5.0f)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (width < 32 || height < 32) throw new ArgumentOutOfRangeException(nameof(width));

        var cfg = new AutotuneNative.MountConfig();
        AutotuneNative.vs_autotune_default_mount_config(ref cfg);
        cfg.MinAcceptableUniformity = minUniformity;
        cfg.MinNominalSharpness = minSharpness;

        var diag = new AutotuneNative.MountDiagnostic { ZoneSharpness = new float[9] };
        GCHandle hImg = GCHandle.Alloc(image, GCHandleType.Pinned);

        try
        {
            int rc = AutotuneNative.vs_autotune_check_mount_tilt(
                hImg.AddrOfPinnedObject(),
                width,
                height,
                width,
                ref cfg,
                ref diag);

            if (rc != 0) throw new InvalidOperationException($"Mount tilt check failed with code {rc}.");

            return new MountReport(
                diag.ZoneSharpness ?? new float[9],
                diag.MaxSharpness,
                diag.MinSharpness,
                diag.MeanSharpness,
                diag.UniformityRatio,
                diag.IsMountTilted != 0,
                diag.TiltDirection ?? "",
                diag.Advice ?? "");
        }
        finally
        {
            if (hImg.IsAllocated) hImg.Free();
        }
    }

    public static DustReport CheckLensDust(
        byte[] backgroundOrMinImg,
        int width,
        int height,
        byte dustThresholdDn = 25,
        int maxAllowedSpots = 4)
    {
        if (backgroundOrMinImg == null) throw new ArgumentNullException(nameof(backgroundOrMinImg));
        if (width < 16 || height < 16) throw new ArgumentOutOfRangeException(nameof(width));

        var cfg = new AutotuneNative.DustConfig();
        AutotuneNative.vs_autotune_default_dust_config(ref cfg);
        cfg.DustThresholdDn = dustThresholdDn;
        cfg.MaxAllowedSpots = maxAllowedSpots;

        var diag = new AutotuneNative.DustDiagnostic();
        GCHandle hImg = GCHandle.Alloc(backgroundOrMinImg, GCHandleType.Pinned);

        try
        {
            int rc = AutotuneNative.vs_autotune_check_lens_dust(
                hImg.AddrOfPinnedObject(),
                width,
                height,
                width,
                ref cfg,
                ref diag);

            if (rc != 0) throw new InvalidOperationException($"Lens dust check failed with code {rc}.");

            return new DustReport(
                diag.DustSpotsFound,
                diag.DustCoveragePct,
                diag.IsLensContaminated != 0,
                diag.Advice ?? "");
        }
        finally
        {
            if (hImg.IsAllocated) hImg.Free();
        }
    }

    public static (byte[] CleanImage, float AmbientLeakagePct) StrobeMinusAmbient(
        byte[] strobeImg,
        byte[] ambientImg,
        int width,
        int height)
    {
        if (strobeImg == null) throw new ArgumentNullException(nameof(strobeImg));
        if (ambientImg == null) throw new ArgumentNullException(nameof(ambientImg));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        byte[] cleanDst = new byte[width * height];
        GCHandle hStrobe = GCHandle.Alloc(strobeImg, GCHandleType.Pinned);
        GCHandle hAmbient = GCHandle.Alloc(ambientImg, GCHandleType.Pinned);
        GCHandle hClean = GCHandle.Alloc(cleanDst, GCHandleType.Pinned);

        try
        {
            int rc = AutotuneNative.vs_autotune_strobe_minus_ambient(
                hStrobe.AddrOfPinnedObject(),
                hAmbient.AddrOfPinnedObject(),
                hClean.AddrOfPinnedObject(),
                width,
                height,
                width,
                out float leakage);

            if (rc != 0) throw new InvalidOperationException($"Strobe-minus-ambient failed with code {rc}.");

            return (cleanDst, leakage);
        }
        finally
        {
            if (hStrobe.IsAllocated) hStrobe.Free();
            if (hAmbient.IsAllocated) hAmbient.Free();
            if (hClean.IsAllocated) hClean.Free();
        }
    }

    public static LightingReport AnalyzeLighting(
        byte[] image,
        int width,
        int height,
        float targetMeanDn = 128.0f)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (width < 16 || height < 16) throw new ArgumentOutOfRangeException(nameof(width));

        var diag = new AutotuneNative.LightingDiagnostic();
        GCHandle hImg = GCHandle.Alloc(image, GCHandleType.Pinned);

        try
        {
            int rc = AutotuneNative.vs_autotune_analyze_lighting(
                hImg.AddrOfPinnedObject(),
                width,
                height,
                width,
                targetMeanDn,
                ref diag);

            if (rc != 0) throw new InvalidOperationException($"Lighting analysis failed with code {rc}.");

            return new LightingReport(
                diag.MeanLuminance,
                diag.MinLuminance,
                diag.MaxLuminance,
                diag.CornerToCenterRatio,
                diag.IsDaytime != 0,
                diag.SuggestedExposureTrim,
                diag.LightingAdvice ?? "");
        }
        finally
        {
            if (hImg.IsAllocated) hImg.Free();
        }
    }

    public static ApplicatorReport CalculateApplicatorGuidance(
        float capCx,
        float capCy,
        float capRadius,
        float markCx,
        float markCy,
        float markRadius,
        float pxPerMm = 10.0f,
        float screwPitchMmPerTurn = 0.5f,
        float maxAllowedEccentricityMm = 1.20f)
    {
        var cfg = new AutotuneNative.ApplicatorConfig();
        AutotuneNative.vs_autotune_default_applicator_config(ref cfg);
        cfg.PxPerMm = pxPerMm;
        cfg.ScrewPitchMmPerTurn = screwPitchMmPerTurn;
        cfg.MaxAllowedEccentricityMm = maxAllowedEccentricityMm;

        var adv = new AutotuneNative.ApplicatorAdvisory();
        int rc = AutotuneNative.vs_autotune_applicator_guidance(
            capCx, capCy, capRadius,
            markCx, markCy, markRadius,
            ref cfg,
            ref adv);

        if (rc != 0) throw new InvalidOperationException($"Applicator guidance failed with code {rc}.");

        return new ApplicatorReport(
            adv.DeltaXMm,
            adv.DeltaYMm,
            adv.TotalEccentricityMm,
            adv.CpkEstimate,
            adv.XScrewTurns,
            adv.YScrewTurns,
            adv.IsDriftCritical != 0,
            adv.AdvisoryPill ?? "");
    }
}
