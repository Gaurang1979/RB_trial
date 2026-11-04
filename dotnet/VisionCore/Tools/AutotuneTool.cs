using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionCore;

/// <summary>
/// Autonomous Environmental Adaptation &amp; Optical Health Tool (Roadmap Tool 12 / AI Auto-Tune).
/// Monitors 9-zone MTF mount tilt (Scheimpflug error), ambient sunlight/lighting drift,
/// lens dust accumulation, and closed-loop upstream applicator eccentricity.
/// </summary>
public sealed class AutotuneToolConfig : ToolConfig
{
    public bool CheckMountTilt { get; set; } = true;
    public float MinAcceptableUniformity { get; set; } = 0.65f;
    public bool CheckLensDust { get; set; } = false;
    public int MaxAllowedDustSpots { get; set; } = 4;
    public bool AnalyzeLighting { get; set; } = true;
    public float TargetMeanDn { get; set; } = 128.0f;
    public bool TrackApplicator { get; set; } = false;
    public float PxPerMm { get; set; } = 10.0f;
    public float ScrewPitchMmPerTurn { get; set; } = 0.5f;
    public float MaxAllowedEccentricityMm { get; set; } = 1.20f;
    public bool FailOnCriticalTilt { get; set; } = false;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class AutotuneTool : ITool
{
    private readonly AutotuneToolConfig _cfg;

    public AutotuneTool(AutotuneToolConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    public ToolConfig Config => _cfg;

    public void Dispose()
    {
    }

    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null)
    {
        var sw = Stopwatch.StartNew();
        var graphics = new List<Graphic>();
        var values = new Dictionary<string, double>();
        var details = new List<string>();

        if (image == null || image.Pixels == null || image.Width < 32 || image.Height < 32)
        {
            sw.Stop();
            return new ToolResult(
                ToolName: _cfg.Name,
                Pass: false,
                Summary: "Fail: invalid or missing image for optical autotune check",
                Details: new[] { "Input image dimensions < 32x32 px" },
                TimeMs: sw.Elapsed.TotalMilliseconds,
                Graphics: graphics)
            {
                Explain = "invalid_input"
            };
        }

        // the area follows the part (AUD-04); the tool works on a copy, never on the camera image (AUD-02)
        if (ToolArea.Resolve(_cfg.Roi, fixture, image.Width, image.Height, 32) is not { } area)
            return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds, 32);
        var roiPx = ToolArea.Copy(image, area);
        int roiW = area.Width, roiH = area.Height, x0 = area.X, y0 = area.Y;
        bool overallPass = true;
        string? failureReason = null;

        // 1. Mount tilt / Scheimpflug diagnostic
        if (_cfg.CheckMountTilt)
        {
            try
            {
                var mountRep = AutotuneEngine.CheckMountTilt(
                    roiPx, roiW, roiH,
                    minUniformity: _cfg.MinAcceptableUniformity,
                    minSharpness: 4.0f);

                values["autotune.uniformity"] = mountRep.UniformityRatio;
                values["autotune.mean_sharpness"] = mountRep.MeanSharpness;
                values["autotune.is_mount_tilted"] = mountRep.IsMountTilted ? 1.0 : 0.0;

                details.Add($"Mount Tilt Check: {(mountRep.IsMountTilted ? "TILT DETECTED" : "UNIFORM")} (Uniformity={mountRep.UniformityRatio * 100:F1}%)");
                details.Add($"Advice: {mountRep.Advice}");

                if (mountRep.IsMountTilted && _cfg.FailOnCriticalTilt)
                {
                    overallPass = false;
                    failureReason = mountRep.Advice;
                }

                // Draw 9-zone overlay grid
                int zw = roiW / 3;
                int zh = roiH / 3;
                for (int zy = 0; zy < 3; zy++)
                {
                    for (int zx = 0; zx < 3; zx++)
                    {
                        int qIdx = zy * 3 + zx;
                        float qSharp = mountRep.ZoneSharpness[qIdx];
                        bool qOk = qSharp >= mountRep.MaxSharpness * 0.70f;
                        var kind = qOk ? GraphicKind.Ok : (mountRep.IsMountTilted ? GraphicKind.Fail : GraphicKind.Info);

                        int qx0 = x0 + zx * zw;
                        int qy0 = y0 + zy * zh;
                        graphics.Add(new Graphic(
                            new[]
                            {
                                new PointF(qx0, qy0),
                                new PointF(qx0 + zw, qy0),
                                new PointF(qx0 + zw, qy0 + zh),
                                new PointF(qx0, qy0 + zh)
                            },
                            kind,
                            $"Q{qIdx + 1}: {qSharp:F1}"));
                    }
                }
            }
            catch (Exception ex)
            {
                details.Add($"Mount diagnostic error: {ex.Message}");
                // an engine call that fails checked nothing: NG (AUD-03)
                overallPass = false;
                failureReason ??= $"Mount diagnostic error: {ex.Message}";
            }
        }

        // 2. Ambient lighting & Day/Night analysis
        if (_cfg.AnalyzeLighting)
        {
            try
            {
                var lightRep = AutotuneEngine.AnalyzeLighting(roiPx, roiW, roiH, _cfg.TargetMeanDn);
                values["autotune.mean_luminance"] = lightRep.MeanLuminance;
                values["autotune.corner_to_center"] = lightRep.CornerToCenterRatio;
                values["autotune.is_daytime"] = lightRep.IsDaytime ? 1.0 : 0.0;
                values["autotune.exposure_trim"] = lightRep.SuggestedExposureTrim;

                details.Add($"Lighting Analyzer: {(lightRep.IsDaytime ? "DAYTIME / SOLAR INGRESS" : "NIGHT / CONTROLLED")} (Mean={lightRep.MeanLuminance:F1} DN)");
                details.Add($"Suggested Exposure Trim: {lightRep.SuggestedExposureTrim:F2}x ({lightRep.LightingAdvice})");
            }
            catch (Exception ex)
            {
                details.Add($"Lighting diagnostic error: {ex.Message}");
                // an engine call that fails checked nothing: NG (AUD-03)
                overallPass = false;
                failureReason ??= $"Lighting diagnostic error: {ex.Message}";
            }
        }

        // 3. Lens dust check
        if (_cfg.CheckLensDust)
        {
            try
            {
                var dustRep = AutotuneEngine.CheckLensDust(roiPx, roiW, roiH, dustThresholdDn: 25, maxAllowedSpots: _cfg.MaxAllowedDustSpots);
                values["autotune.dust_spots"] = dustRep.DustSpotsFound;
                values["autotune.dust_coverage_pct"] = dustRep.DustCoveragePct;
                values["autotune.is_lens_contaminated"] = dustRep.IsLensContaminated ? 1.0 : 0.0;

                details.Add($"Lens Hygiene: {dustRep.Advice}");
                if (dustRep.IsLensContaminated && _cfg.FailOnCriticalTilt)
                {
                    overallPass = false;
                    failureReason ??= dustRep.Advice;
                }
            }
            catch (Exception ex)
            {
                details.Add($"Dust diagnostic error: {ex.Message}");
                // an engine call that fails checked nothing: NG (AUD-03)
                overallPass = false;
                failureReason ??= $"Dust diagnostic error: {ex.Message}";
            }
        }

        // 4. Closed-loop applicator telemetry (if cap & mark centers exist in batch fields or center calculation)
        if (_cfg.TrackApplicator)
        {
            try
            {
                float capCx = roiW * 0.5f;
                float capCy = roiH * 0.5f;
                float capR = Math.Min(roiW, roiH) * 0.40f;

                // Estimate inner feature center using weighted centroid
                double m00 = 0, m10 = 0, m01 = 0;
                for (int y = (int)(roiH * 0.25f); y < (int)(roiH * 0.75f); y++)
                {
                    for (int x = (int)(roiW * 0.25f); x < (int)(roiW * 0.75f); x++)
                    {
                        byte v = roiPx[y * roiW + x];
                        if (v < 100) // Dark hologram/mark on light cap
                        {
                            double wgt = (100.0 - v);
                            m00 += wgt;
                            m10 += wgt * x;
                            m01 += wgt * y;
                        }
                    }
                }

                float markCx = (m00 > 10.0) ? (float)(m10 / m00) : capCx;
                float markCy = (m00 > 10.0) ? (float)(m01 / m00) : capCy;

                var advRep = AutotuneEngine.CalculateApplicatorGuidance(
                    capCx, capCy, capR,
                    markCx, markCy, capR * 0.3f,
                    pxPerMm: _cfg.PxPerMm,
                    screwPitchMmPerTurn: _cfg.ScrewPitchMmPerTurn,
                    maxAllowedEccentricityMm: _cfg.MaxAllowedEccentricityMm);

                values["autotune.eccentricity_mm"] = advRep.TotalEccentricityMm;
                values["autotune.delta_x_mm"] = advRep.DeltaXMm;
                values["autotune.delta_y_mm"] = advRep.DeltaYMm;
                values["autotune.screw_x_turns"] = advRep.XScrewTurns;
                values["autotune.screw_y_turns"] = advRep.YScrewTurns;
                values["autotune.cpk"] = advRep.CpkEstimate;

                details.Add($"Applicator Telemetry: {advRep.AdvisoryPill}");

                var kind = advRep.IsDriftCritical ? GraphicKind.Info : GraphicKind.Ok;
                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0 + capCx - 6, y0 + capCy),
                        new PointF(x0 + capCx + 6, y0 + capCy),
                        new PointF(x0 + capCx, y0 + capCy - 6),
                        new PointF(x0 + capCx, y0 + capCy + 6)
                    },
                    kind,
                    $"Center Offset: {advRep.TotalEccentricityMm:F2}mm (Screw X: {advRep.XScrewTurns:F2} turns)"));
            }
            catch (Exception ex)
            {
                details.Add($"Applicator advisory error: {ex.Message}");
                // an engine call that fails checked nothing: NG (AUD-03)
                overallPass = false;
                failureReason ??= $"Applicator advisory error: {ex.Message}";
            }
        }

        sw.Stop();
        string summary = overallPass
            ? $"Pass: Optical & Environmental Health OK ({details.Count} checks evaluated)"
            : $"Fail: Optical health violation ({failureReason})";

        return new ToolResult(
            ToolName: _cfg.Name,
            Pass: overallPass,
            Summary: summary,
            Details: details,
            TimeMs: sw.Elapsed.TotalMilliseconds,
            Graphics: graphics)
        {
            Values = values,
            Explain = overallPass ? null : (failureReason ?? "optical_health_violation")
        };
    }
}
