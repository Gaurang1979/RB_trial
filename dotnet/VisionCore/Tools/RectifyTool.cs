using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionCore;

/// <summary>
/// Conveyor rolling-shutter rectification tool (Tool 0 / Line Acquisition / AG-06).
/// Compensates line-by-line exposure shear distortion for moving items under rolling-shutter CMOS sensors.
/// </summary>
public sealed class RectifyToolConfig : ToolConfig
{
    public float LineSpeedMmS { get; set; } = 300.0f;
    public float MmPerPx { get; set; } = 0.08f;
    public float RowTimeUs { get; set; } = 22.5f;
    public float MotionAngleDeg { get; set; } = 0.0f;
    public bool Bilinear { get; set; } = true;
    public float MaxAllowedShearDeg { get; set; } = 15.0f;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class RectifyTool : ITool
{
    private readonly RectifyToolConfig _cfg;

    public RectifyTool(RectifyToolConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
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
            return new ToolResult(_cfg.Name, false, "Rectification failed: Image buffer invalid or too small", details, sw.Elapsed.TotalMilliseconds, graphics)
            {
                Explain = "explain.rectify.excessive_shear"
            };
        }

        // the area follows the part (AUD-04); the tool works on a copy, never on the camera image (AUD-02)
        if (ToolArea.Resolve(_cfg.Roi, fixture, image.Width, image.Height, 4) is not { } area)
            return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds, 4);
        int x0 = area.X, y0 = area.Y, w = area.Width, h = area.Height;

        var opt = new RectifyOptions
        {
            LineSpeedMmS = _cfg.LineSpeedMmS,
            MmPerPx = _cfg.MmPerPx,
            RowTimeUs = _cfg.RowTimeUs,
            MotionAngleDeg = _cfg.MotionAngleDeg,
            Bilinear = _cfg.Bilinear
        };

        RectifyReport report;
        try
        {
            // on a copy of the area: the camera image is never changed (AUD-02)
            report = RectifyEngine.RectifyInPlace(ToolArea.Copy(image, area), w, h, w, opt);
        }
        catch (Exception ex)
        {
            details.Add($"✗ Rectification native error: {ex.Message}");
            return new ToolResult(_cfg.Name, false, $"Rectification failed: {ex.Message}", details, sw.Elapsed.TotalMilliseconds, graphics)
            {
                Explain = "explain.rectify.excessive_shear"
            };
        }

        values["max_shift_px"] = report.MaxShiftPx;
        values["shear_angle_deg"] = report.ShearAngleDeg;
        values["rectify_ms"] = report.TimeMs;

        bool pass = Math.Abs(report.ShearAngleDeg) <= _cfg.MaxAllowedShearDeg;
        string summary = "";
        string? explainKey = null;

        if (pass)
        {
            summary = $"Conveyor rolling-shutter rectified ({report.ShearAngleDeg:0.2}° shear, {report.MaxShiftPx:0.1} px shift in {report.TimeMs:0.1} ms)";
            details.Add($"✓ Line velocity compensated: {_cfg.LineSpeedMmS:0.0} mm/s at {report.ShearAngleDeg:0.2}° shear angle");
            details.Add($"✓ Maximum geometric correction: {report.MaxShiftPx:0.1} pixels across vertical scan");
        }
        else
        {
            summary = $"Excessive motion shear distortion ({report.ShearAngleDeg:0.2}° > {_cfg.MaxAllowedShearDeg:0.2}° limit)";
            explainKey = "explain.rectify.excessive_shear";
            details.Add($"✗ Conveyor speed or readout timing induces excessive shear ({report.ShearAngleDeg:0.2}°)");
            details.Add($"✗ Exceeds maximum allowable tolerance ({_cfg.MaxAllowedShearDeg:0.2}°)");
        }

        graphics.Add(new Graphic(
            new[]
            {
                new PointF(x0, y0),
                new PointF(x0 + w, y0),
                new PointF(x0 + w, y0 + h),
                new PointF(x0, y0 + h)
            },
            pass ? GraphicKind.Ok : GraphicKind.Fail,
            pass ? "Motion Rectified" : "Excessive Shear"));

        double ms = sw.Elapsed.TotalMilliseconds;
        return new ToolResult(_cfg.Name, pass, summary, details, ms, graphics)
        {
            Explain = explainKey,
            Values = values
        };
    }

    public void Dispose()
    {
    }
}
