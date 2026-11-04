using System.Globalization;
using VisionCore;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Runtime;

/// <summary>
/// The dent and deviation marks of a Blob tool's outline check on a try in the job designer (requirements 17.12
/// BLB-OUT-08): for every spot, a cross at its deepest dent and, with a taught outline, at its largest deviation from
/// it, labelled with the value in the tool's unit. Over its limit a mark is NG, within it information. Advice for
/// setting the limits only: no verdict changes, and the line's own graphics are not touched.
/// </summary>
public static class BlobMarks
{
    /// <summary>The label VisionCore gives the diamond it draws on a spot that breaks the dent or deviation rule; a try
    /// shows the cross of <see cref="For"/> there instead.</summary>
    public static bool IsRuleMark(Graphic g) => g.Label is "dent" or "deviation";

    /// <summary>The marks of one Blob tool's result, in JPEG pixels (<paramref name="scale"/> JPEG pixels per full-image
    /// pixel). None when the outline check is off, or limits in mm have no calibration to show them in.</summary>
    public static IEnumerable<V1.Graphic> For(BlobToolConfig cfg, ToolResult result, BarcodeReading.LensCalibration? calibration, double scale)
    {
        if (!cfg.CheckOutline || result.Blobs is not { Count: > 0 } blobs) yield break;
        var mm = cfg.OutlineUnit == BlobOutlineUnit.Mm;
        double unit = 1;
        if (mm)
        {
            if (calibration is not { MmPerPx: > 0 } c || !float.IsFinite(c.MmPerPx)) yield break;
            unit = c.MmPerPx;
        }
        var name = mm ? "mm" : "px";
        for (var i = 0; i < blobs.Count; i++)
        {
            if (blobs[i].Contour is not { } o) continue;
            var spot = i + 1;
            if (o.MaxDentDepth > 0)
                yield return Mark(o.DentAt, "dent", o.MaxDentDepth * unit, cfg.MaxDentDepth, name, spot, cfg.Name, scale);
            if (o.MaxDeviation is { } dev && o.DeviationAt is { } at)
                yield return Mark(at, "dev", dev * unit, cfg.MaxDeviation, name, spot, cfg.Name, scale);
        }
    }

    static V1.Graphic Mark(PointF at, string what, double value, float? limit, string unit, int spot, string tool, double scale)
    {
        var over = limit is { } l && value > l;
        var text = string.Create(CultureInfo.InvariantCulture, $"{what} {value:0.0##} {unit}");
        var tip = string.Create(CultureInfo.InvariantCulture,
            $"{tool}, spot {spot}: {(what == "dent" ? "deepest dent" : "largest deviation from the taught outline")} {value:0.0##} {unit}")
            + (limit is { } m ? string.Create(CultureInfo.InvariantCulture, $", at most {m:0.0##} allowed") : ", no limit set");
        var g = new V1.Graphic
        {
            Kind = V1.Graphic.Types.Kind.Cross,
            Colour = over ? "status.ng" : "status.info",
            Label = text,
            Tooltip = tip,
            Tool = tool,
        };
        g.Points.Add(new V1.Point { X = (float)(at.X * scale), Y = (float)(at.Y * scale) });
        return g;
    }
}
