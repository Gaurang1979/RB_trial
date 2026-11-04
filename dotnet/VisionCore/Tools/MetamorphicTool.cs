using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionCore;

/// <summary>
/// Metamorphic Robustness &amp; Fuzzing Certification Tool (Tool 10 / Tool H / AG-10).
/// Verifies mathematical metamorphic invariants under rotation, illumination gain/bias,
/// affine scaling, sensor Gaussian noise, and quiet-zone padding to guarantee line stability.
/// </summary>
public sealed class MetamorphicToolConfig : ToolConfig
{
    public float MaxRotationDeg { get; set; } = 3.0f;
    public int RotationSteps { get; set; } = 7;
    public float MaxIllumGainDelta { get; set; } = 0.10f;
    public int IllumSteps { get; set; } = 5;
    public float MaxScaleDelta { get; set; } = 0.05f;
    public int ScaleSteps { get; set; } = 5;
    public float MaxNoiseSigma { get; set; } = 5.0f;
    public int NoiseSteps { get; set; } = 4;
    public int MaxQuietZonePx { get; set; } = 8;
    public float MaxAllowedJitterPx { get; set; } = 0.75f;
    public float MinAcceptableConfidence { get; set; } = 0.60f;
    public bool FailOnNonRobust { get; set; } = true;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class MetamorphicTool : ITool
{
    private readonly MetamorphicToolConfig _cfg;

    public MetamorphicTool(MetamorphicToolConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    public ToolConfig Config => _cfg;

    public void Dispose()
    {
        // No persistent unmanaged resources; native calls are per-run or static
    }

    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null)
    {
        var sw = Stopwatch.StartNew();
        var graphics = new List<Graphic>();
        var values = new Dictionary<string, double>();
        var details = new List<string>();

        if (image == null || image.Pixels == null || image.Width < 8 || image.Height < 8)
        {
            sw.Stop();
            return new ToolResult(
                ToolName: _cfg.Name,
                Pass: false,
                Summary: "Fail: invalid or missing image input",
                Details: new[] { "Input image is null or dimensions < 8x8 px" },
                TimeMs: sw.Elapsed.TotalMilliseconds,
                Graphics: graphics)
            {
                Explain = "invalid_input"
            };
        }

        // the area follows the part (AUD-04); the tool works on a copy, never on the camera image (AUD-02)
        if (ToolArea.Resolve(_cfg.Roi, fixture, image.Width, image.Height, 8) is not { } area)
            return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds, 8);
        var roiPx = ToolArea.Copy(image, area);
        int roiW = area.Width, roiH = area.Height, x0 = area.X, y0 = area.Y;

        // Standard vision evaluation delegate tracking centroid & moment stability
        VisionEvalDelegate evalDelegate = (testImg, imgW, imgH) =>
        {
            double m00 = 0, m10 = 0, m01 = 0;
            int foregroundCount = 0;

            for (int y = 0; y < imgH; y++)
            {
                int rowOff = y * imgW;
                for (int x = 0; x < imgW; x++)
                {
                    byte v = testImg[rowOff + x];
                    // Foreground features: contrast difference from nominal border
                    if (v < 128)
                    {
                        double weight = (128.0 - v) / 128.0;
                        m00 += weight;
                        m10 += weight * x;
                        m01 += weight * y;
                        foregroundCount++;
                    }
                }
            }

            if (m00 > 1.0 && foregroundCount > 5)
            {
                float px = (float)(m10 / m00);
                float py = (float)(m01 / m00);
                double avgContrast = m00 / foregroundCount;
                float conf = (float)Math.Clamp(avgContrast, 0.0, 1.0);
                return (conf, px, py, true);
            }

            return (0.1f, imgW * 0.5f, imgH * 0.5f, false);
        };

        try
        {
            var report = MetamorphicEngine.RunFuzzingSuite(
                roiPx,
                roiW,
                roiH,
                evalDelegate,
                maxRotationDeg: _cfg.MaxRotationDeg,
                rotationSteps: Math.Max(3, _cfg.RotationSteps),
                maxIllumGainDelta: _cfg.MaxIllumGainDelta,
                illumSteps: Math.Max(3, _cfg.IllumSteps),
                maxScaleDelta: _cfg.MaxScaleDelta,
                scaleSteps: Math.Max(3, _cfg.ScaleSteps),
                maxNoiseSigma: _cfg.MaxNoiseSigma,
                noiseSteps: Math.Max(2, _cfg.NoiseSteps),
                maxQuietZonePx: _cfg.MaxQuietZonePx,
                maxAllowedJitterPx: _cfg.MaxAllowedJitterPx,
                minAcceptableConfidence: _cfg.MinAcceptableConfidence);

            values["metamorphic.evaluations"] = report.TotalEvaluations;
            values["metamorphic.verdict_flips"] = report.VerdictFlips;
            values["metamorphic.max_jitter_px"] = report.MaxPositionalJitterPx;
            values["metamorphic.mean_jitter_px"] = report.MeanPositionalJitterPx;
            values["metamorphic.min_confidence"] = report.MinObservedConfidence;
            values["metamorphic.max_confidence"] = report.MaxObservedConfidence;
            values["metamorphic.is_robust"] = report.IsRobust ? 1.0 : 0.0;

            bool passed = report.IsRobust && report.VerdictFlips == 0 && report.MaxPositionalJitterPx <= _cfg.MaxAllowedJitterPx;
            if (!_cfg.FailOnNonRobust)
            {
                passed = true;
            }

            var kind = passed ? GraphicKind.Ok : GraphicKind.Fail;
            graphics.Add(new Graphic(
                new[]
                {
                    new PointF(x0, y0),
                    new PointF(x0 + roiW, y0),
                    new PointF(x0 + roiW, y0 + roiH),
                    new PointF(x0, y0 + roiH)
                },
                kind,
                $"{_cfg.Name}: {(passed ? "ROBUST" : "UNSTABLE")} (Jitter={report.MaxPositionalJitterPx:F2}px, Flips={report.VerdictFlips})"));

            details.Add($"Evaluations: {report.TotalEvaluations}, Verdict Flips: {report.VerdictFlips}");
            details.Add($"Positional Jitter: Max={report.MaxPositionalJitterPx:F2}px (limit {_cfg.MaxAllowedJitterPx:F2}px), Mean={report.MeanPositionalJitterPx:F2}px");
            details.Add($"Observed Confidence: [{report.MinObservedConfidence:F2} .. {report.MaxObservedConfidence:F2}]");
            if (!string.IsNullOrEmpty(report.FailureReason))
            {
                details.Add($"Failure Reason: {report.FailureReason}");
            }

            sw.Stop();
            string summary = passed
                ? $"Pass: Robust ({report.TotalEvaluations} metamorphic checks passed, max jitter {report.MaxPositionalJitterPx:F2}px)"
                : $"Fail: Metamorphic violation ({(string.IsNullOrEmpty(report.FailureReason) ? $"{report.VerdictFlips} flips, jitter {report.MaxPositionalJitterPx:F2}px" : report.FailureReason)})";

            return new ToolResult(
                ToolName: _cfg.Name,
                Pass: passed,
                Summary: summary,
                Details: details,
                TimeMs: sw.Elapsed.TotalMilliseconds,
                Graphics: graphics)
            {
                Values = values,
                Explain = passed ? null : (report.FailureReason ?? "metamorphic_instability")
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ToolResult(
                ToolName: _cfg.Name,
                Pass: false,
                Summary: $"Fail: Metamorphic harness error - {ex.Message}",
                Details: new[] { ex.ToString() },
                TimeMs: sw.Elapsed.TotalMilliseconds,
                Graphics: graphics)
            {
                Explain = "metamorphic_execution_error"
            };
        }
    }
}
