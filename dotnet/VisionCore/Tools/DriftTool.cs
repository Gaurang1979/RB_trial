using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionCore;

/// <summary>
/// Optical Drift &amp; Out-Of-Distribution (OOD) Detector (Tool 11 / AG-12 / AS-13).
/// Tracks illumination fading, lens contamination, focus degradation, and sudden scene changes
/// using 10D Mahalanobis distance over moving production windows to prevent false rejects.
/// </summary>
public sealed class DriftToolConfig : ToolConfig
{
    public int WindowSize { get; set; } = 50;
    public float WarnDistanceThreshold { get; set; } = 4.5f;
    public float DirtSharpnessSigma { get; set; } = 2.0f;
    public float LightDropSigma { get; set; } = 2.0f;
    public int GridStep { get; set; } = 4;
    public bool FailOnDrift { get; set; } = true;
    /// <summary>The optical baseline taught from good parts in Teach (at least <see cref="MinSamples"/>), saved with
    /// the job (AUD-01). Empty = not taught: every run is NG; a part on the line never becomes the baseline.</summary>
    public List<DriftSample> Baseline { get; set; } = new();

    public const int MinSamples = 5;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();

    /// <summary>Teaches the baseline from pictures of good parts, at the tool's area; returns the number used.</summary>
    public int Teach(IEnumerable<GrayImage> goodParts)
    {
        var samples = new List<DriftSample>();
        foreach (var img in goodParts)
        {
            if (img?.Pixels is null || ToolArea.Resolve(Roi, null, img.Width, img.Height, 4) is not { } a) continue;
            var px = ToolArea.Copy(img, a);
            samples.Add(DriftSample.Of(DriftTracker.ExtractFeatures(px, a.Width, a.Height, a.Width, Math.Max(1, GridStep))));
        }
        if (samples.Count < MinSamples)
            throw new InvalidOperationException($"The baseline needs at least {MinSamples} pictures of good parts with the area inside; {samples.Count} found");
        Baseline = samples;
        return samples.Count;
    }
}

/// <summary>The optical measures of one good part, taught into the drift baseline.</summary>
public sealed class DriftSample
{
    public float MeanBrightness { get; set; }
    public float ContrastStd { get; set; }
    public float Sharpness { get; set; }
    public float Uniformity { get; set; }
    public float Corner0 { get; set; }
    public float Corner1 { get; set; }
    public float Corner2 { get; set; }
    public float Corner3 { get; set; }
    public float SaturatedRatio { get; set; }
    public float DarkFloor { get; set; }

    public static unsafe DriftSample Of(DriftNative.Features f) => new()
    {
        MeanBrightness = f.MeanBrightness, ContrastStd = f.ContrastStd, Sharpness = f.Sharpness, Uniformity = f.Uniformity,
        Corner0 = f.CornerSharpness[0], Corner1 = f.CornerSharpness[1], Corner2 = f.CornerSharpness[2], Corner3 = f.CornerSharpness[3],
        SaturatedRatio = f.SaturatedRatio, DarkFloor = f.DarkFloor,
    };

    public unsafe DriftNative.Features ToFeatures()
    {
        var f = new DriftNative.Features
        {
            MeanBrightness = MeanBrightness, ContrastStd = ContrastStd, Sharpness = Sharpness, Uniformity = Uniformity,
            SaturatedRatio = SaturatedRatio, DarkFloor = DarkFloor,
        };
        f.CornerSharpness[0] = Corner0;
        f.CornerSharpness[1] = Corner1;
        f.CornerSharpness[2] = Corner2;
        f.CornerSharpness[3] = Corner3;
        return f;
    }
}

public sealed class DriftTool : ITool
{
    private readonly DriftToolConfig _cfg;
    private readonly DriftTracker _tracker;
    private readonly object _lock = new();
    private readonly string? _notTaught;

    public DriftTool(DriftToolConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        var p = new DriftNative.Params
        {
            StructSize = System.Runtime.InteropServices.Marshal.SizeOf<DriftNative.Params>(),
            WindowSize = Math.Max(5, cfg.WindowSize),
            WarnDistanceThreshold = cfg.WarnDistanceThreshold,
            DirtSharpnessSigma = cfg.DirtSharpnessSigma,
            LightDropSigma = cfg.LightDropSigma,
            GridStep = Math.Max(1, cfg.GridStep)
        };
        _tracker = new DriftTracker(p);
        // only the taught baseline, never a part on the line (AUD-01)
        if (cfg.Baseline.Count < DriftToolConfig.MinSamples)
            _notTaught = $"no optical baseline taught ({cfg.Baseline.Count} of {DriftToolConfig.MinSamples} good-part pictures). Teach the baseline";
        else
        {
            try
            {
                foreach (var b in cfg.Baseline) _tracker.TeachSample(b.ToFeatures());
                _tracker.FinalizeTeach();
            }
            catch (InvalidOperationException e) { _notTaught = "the taught baseline cannot be used: " + e.Message; }
        }
    }

    public ToolConfig Config => _cfg;

    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null)
    {
        var sw = Stopwatch.StartNew();
        var graphics = new List<Graphic>();
        var values = new Dictionary<string, double>();
        var details = new List<string>();

        if (image == null || image.Pixels == null || image.Width < 4 || image.Height < 4)
        {
            return new ToolResult(_cfg.Name, false, "Drift monitoring failed: Image buffer invalid or too small", details, sw.Elapsed.TotalMilliseconds, graphics)
            {
                Explain = "explain.drift.invalid_image"
            };
        }
        if (_notTaught is not null) return ToolArea.NotTaught(_cfg, _notTaught, sw.Elapsed.TotalMilliseconds);
        if (ToolArea.Resolve(_cfg.Roi, fixture, image.Width, image.Height, 4) is not { } area)
            return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds, 4);

        lock (_lock)
        {
            var roiPx = ToolArea.Copy(image, area);
            DriftNative.Features feat;
            try
            {
                feat = DriftTracker.ExtractFeatures(roiPx, area.Width, area.Height, area.Width, _cfg.GridStep);
            }
            catch (Exception ex)
            {
                return new ToolResult(_cfg.Name, false, $"Feature extraction failed: {ex.Message}", details, sw.Elapsed.TotalMilliseconds, graphics)
                {
                    Explain = "explain.drift.invalid_image"
                };
            }

            float dist;
            DriftNative.DriftStatus status;
            string diag;
            try
            {
                (dist, status, diag) = _tracker.Update(feat);
            }
            catch (Exception ex)
            {
                return new ToolResult(_cfg.Name, false, $"Tracker update failed: {ex.Message}", details, sw.Elapsed.TotalMilliseconds, graphics)
                {
                    Explain = "explain.drift.optical_degradation"
                };
            }

            values["drift_distance"] = dist;
            values["mean_brightness"] = feat.MeanBrightness;
            values["contrast_std"] = feat.ContrastStd;
            values["sharpness"] = feat.Sharpness;
            values["drift_status"] = (double)status;

            bool isOk = status == DriftNative.DriftStatus.Ok;
            bool pass = isOk || !_cfg.FailOnDrift;
            string summary;
            string? explainKey = null;

            if (isOk)
            {
                summary = $"Optical baseline OK (DM={dist:0.2}, Brightness={feat.MeanBrightness:0.1}, Sharpness={feat.Sharpness:0.1})";
                details.Add($"✓ Optical condition nominal: Mahalanobis distance {dist:0.2} < {_cfg.WarnDistanceThreshold:0.2}");
                details.Add($"✓ Image metrics: brightness {feat.MeanBrightness:0.1}, contrast {feat.ContrastStd:0.1}, sharpness {feat.Sharpness:0.1}");
            }
            else
            {
                explainKey = status switch
                {
                    DriftNative.DriftStatus.WarnLensDirt => "explain.drift.lens_dirt",
                    DriftNative.DriftStatus.WarnLightingDrop => "explain.drift.lighting_drop",
                    DriftNative.DriftStatus.WarnOverexposure => "explain.drift.overexposure",
                    _ => "explain.drift.optical_degradation"
                };

                summary = $"{diag} (DM={dist:0.2})";
                details.Add($"✗ {diag}");
                details.Add($"✗ Mahalanobis distance {dist:0.2} exceeds threshold {_cfg.WarnDistanceThreshold:0.2}");
            }

            graphics.Add(new Graphic(
                ToolArea.Corners(area),
                pass ? (isOk ? GraphicKind.Ok : GraphicKind.Info) : GraphicKind.Fail,
                isOk ? "Condition OK" : "Optical Drift"));

            double ms = sw.Elapsed.TotalMilliseconds;
            return new ToolResult(_cfg.Name, pass, summary, details, ms, graphics)
            {
                Explain = explainKey,
                Values = values
            };
        }
    }

    public void Dispose()
    {
        _tracker.Dispose();
    }
}
