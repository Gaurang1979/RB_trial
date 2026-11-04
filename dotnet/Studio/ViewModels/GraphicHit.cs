using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>Which result graphic a tap on the image falls on (points in image pixels).</summary>
public static class GraphicHit
{
    /// <summary>
    /// The graphic under the point: inside a polygon, or within <paramref name="slop"/> pixels of a
    /// line, cross or text anchor. Where shapes overlap the worst verdict wins, then the smallest
    /// shape, so a failed code inside a passed label region is the one explained.
    /// </summary>
    public static ResultGraphic? Find(IReadOnlyList<ResultGraphic> graphics, Point2 at, double slop)
    {
        ResultGraphic? best = null;
        var bestArea = double.MaxValue;
        foreach (var g in graphics)
        {
            if (g.Points.Count == 0 || !Hits(g, at, slop)) continue;
            var area = g.Kind == GraphicKind.Polygon && g.Points.Count >= 3 ? Math.Abs(SignedArea(g.Points)) : 0;
            if (best is null || Tones.Rank(g.Verdict) > Tones.Rank(best.Verdict)
                || (Tones.Rank(g.Verdict) == Tones.Rank(best.Verdict) && area < bestArea))
            {
                best = g;
                bestArea = area;
            }
        }
        return best;
    }

    /// <summary>What a tap on the graphic shows: the runtime's tooltip, else its label.</summary>
    public static string? Text(ResultGraphic g) => g.Tooltip ?? g.Label;

    /// <summary>
    /// The same, with the add-on of the code the graphic marks (its label is the code's text), since
    /// the add-on is read apart from the code: "4006381333931 · add-on 12".
    /// </summary>
    public static string? Text(ResultGraphic g, IReadOnlyList<CodeOutcome>? codes, Strings strings)
    {
        var text = Text(g);
        if (text is null || g.Label is not { Length: > 0 } label || codes is null) return text;
        var code = codes.FirstOrDefault(c => c.AddOn.Length > 0 && c.Text == label);
        return code is null ? text : strings.Format("result.addon", text, code.AddOn);
    }

    private static bool Hits(ResultGraphic g, Point2 at, double slop)
    {
        var p = g.Points;
        switch (g.Kind)
        {
            case GraphicKind.Polygon when p.Count >= 3:
                return Inside(p, at) || Near(p, at, slop, closed: true);
            case GraphicKind.Polygon:
            case GraphicKind.Polyline:
                return Near(p, at, slop, closed: false);
            default:   // cross and text: around the anchor
                return Distance(p[0], at) <= slop * 2;
        }
    }

    private static bool Inside(IReadOnlyList<Point2> p, Point2 at)
    {
        var inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
        {
            if ((p[i].Y > at.Y) != (p[j].Y > at.Y)
                && at.X < (p[j].X - p[i].X) * (at.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static bool Near(IReadOnlyList<Point2> p, Point2 at, double slop, bool closed)
    {
        if (p.Count == 1) return Distance(p[0], at) <= slop;
        var n = closed ? p.Count : p.Count - 1;
        for (var i = 0; i < n; i++)
        {
            if (SegmentDistance(p[i], p[(i + 1) % p.Count], at) <= slop) return true;
        }
        return false;
    }

    private static double SegmentDistance(Point2 a, Point2 b, Point2 at)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len = dx * dx + dy * dy;
        var t = len == 0 ? 0 : Math.Clamp(((at.X - a.X) * dx + (at.Y - a.Y) * dy) / len, 0, 1);
        return Distance(new Point2(a.X + t * dx, a.Y + t * dy), at);
    }

    private static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double SignedArea(IReadOnlyList<Point2> p)
    {
        double sum = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) sum += (p[j].X + p[i].X) * (p[j].Y - p[i].Y);
        return sum / 2;
    }
}
