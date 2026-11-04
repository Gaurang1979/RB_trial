using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace VisionCore;

/// <summary>Find defects AI (tool spec 10.1, Red tool): in-memory PatchCore surface anomaly detection.</summary>
public sealed class AnomalyToolConfig : ToolConfig
{
    public int PatchSize { get; set; } = 16;
    public int PatchStride { get; set; } = 8;
    public float Sensitivity { get; set; } = 1.0f;
    public int MinDefectAreaPx { get; set; } = 20;
    public string? ModelPath { get; set; }

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class AnomalyTool : ITool
{
    private readonly AnomalyToolConfig _cfg;
    private AnomalyEngine? _engine;
    private readonly object _lock = new();

    public AnomalyTool(AnomalyToolConfig cfg)
    {
        _cfg = cfg;
        if (!string.IsNullOrWhiteSpace(cfg.ModelPath) && File.Exists(cfg.ModelPath))
        {
            try { _engine = AnomalyEngine.LoadModel(cfg.ModelPath); }
            catch { /* model loaded on teach or runtime setup */ }
        }
    }

    public ToolConfig Config => _cfg;

    public void SetEngine(AnomalyEngine engine)
    {
        lock (_lock)
        {
            _engine?.Dispose();
            _engine = engine;
        }
    }

    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null)
    {
        var sw = Stopwatch.StartNew();
        var graphics = new List<Graphic>();
        var values = new Dictionary<string, double>();
        var details = new List<string>();

        lock (_lock)
        {
            if (_engine is null)
            {
                return new ToolResult(
                    _cfg.Name,
                    false,
                    "Anomaly model not trained",
                    new[] { "No golden baseline anomaly model has been trained for this tool." },
                    sw.Elapsed.TotalMilliseconds,
                    graphics)
                {
                    Explain = "explain.anomaly.no_model"
                };
            }

            // the area follows the part (AUD-04); the tool works on a copy, never on the camera image (AUD-02)
            if (ToolArea.Resolve(_cfg.Roi, fixture, image.Width, image.Height, 1) is not { } area)
                return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds);
            int x0 = area.X, y0 = area.Y, w = area.Width, h = area.Height;

            byte[] roiPixels = ToolArea.Copy(image, area);

            var runResult = _engine.Inspect(roiPixels, w, h, w);
            double ms = sw.Elapsed.TotalMilliseconds;

            values["anomaly_score"] = runResult.Score;
            values["defect_area"] = runResult.Defect.AreaPx;
            values["peak_score"] = runResult.Defect.PeakAnomalyScore;

            bool pass = runResult.Verdict == AnomalyNative.AnomalyVerdict.Pass;

            if (pass)
            {
                details.Add($"✓ Surface normal (anomaly score {runResult.Score:0.00}, threshold 1.00)");
                if (_cfg.Roi is not null)
                    graphics.Add(new Graphic(ToolArea.Corners(area), GraphicKind.Ok));

                return new ToolResult(_cfg.Name, true, "OK", details, ms, graphics)
                {
                    Values = values
                };
            }
            else
            {
                var defect = runResult.Defect;
                int absX = x0 + defect.BBoxX;
                int absY = y0 + defect.BBoxY;

                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(absX, absY),
                        new PointF(absX + defect.BBoxW, absY),
                        new PointF(absX + defect.BBoxW, absY + defect.BBoxH),
                        new PointF(absX, absY + defect.BBoxH)
                    },
                    GraphicKind.Fail,
                    $"Defect {defect.AreaPx}px"));

                string summary = $"Anomaly score {runResult.Score:0.00} exceeds threshold 1.00 ({defect.AreaPx} px defect)";
                details.Add($"✗ Defect of {defect.AreaPx} px detected at ({absX},{absY}) [Peak {defect.PeakAnomalyScore:0.00}]");

                return new ToolResult(_cfg.Name, false, summary, details, ms, graphics)
                {
                    Explain = "explain.anomaly.defect",
                    Values = values
                };
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
