using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace VisionCore;

// P/Invoke declarations matching engine/include/vs_measure.h. Results carry StructSize: set before the call
// (Result<T>), and the library writes back the size it filled, which must be this mirror's (Matches).
internal static class MeasureNative
{
    private const string Lib = "vs_measure";

    internal interface ISized { int Size { get; } }

    internal static T Result<T>() where T : struct, ISized
    {
        var r = new T();
        Unsafe.As<T, int>(ref r) = Marshal.SizeOf<T>(); // StructSize is every result's first field
        return r;
    }

    /// <summary>The library filled exactly this mirror's layout (a library of another version would not).</summary>
    internal static bool Matches<T>(in T r) where T : struct, ISized => r.Size == Marshal.SizeOf<T>();

    [StructLayout(LayoutKind.Sequential)]
    internal struct Calib
    {
        public float Fx, Fy, Cx, Cy, K1, K2, P1, P2, K3;
        public float MmPerPx;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Box
    {
        public float Cx, Cy, Length, Height, AngleDeg;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CaliperParams
    {
        public int StructSize;
        public int Mode;
        public int Polarity;
        public float Threshold;
        public float FilterHalfWidth;
        public float ExpectedWidth;
        public int ScoreBy;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Edge
    {
        public float X, Y, Pos, Contrast;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CaliperResult : ISized
    {
        public int StructSize;
        public int Found, Ambiguous, Candidates;
        public float Threshold;
        public Edge Edge0, Edge1;
        public float Position, WidthPx, WidthMm;
        public float Mx0, My0, Mx1, My1;
        public readonly int Size => StructSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LineParams
    {
        public int StructSize;
        public float X0, Y0, X1, Y1;
        public int Calipers;
        public float SearchWidth, CaliperHeight, OutlierPx;
        public int Fit;
        public CaliperParams Caliper;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LineResult : ISized
    {
        public int StructSize;
        public int Found, Calipers, PointsFound, PointsUsed;
        public float X0, Y0, X1, Y1;
        public float Mx0, My0, Mx1, My1;
        public float AngleDeg, StraightnessPx, RmsPx, MeanContrast;
        public readonly int Size => StructSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CircleParams
    {
        public int StructSize;
        public float Cx, Cy, RInner, ROuter, StartDeg, SpanDeg;
        public int Calipers, Inward;
        public float CaliperHeight, OutlierPx;
        public int Fit;
        public CaliperParams Caliper;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CircleResult : ISized
    {
        public int StructSize;
        public int Found, Calipers, PointsFound, PointsUsed;
        public float Cx, Cy, Mcx, Mcy, RadiusPx, RoundnessPx, RmsPx, MeanContrast;
        public readonly int Size => StructSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EllipseResult : ISized
    {
        public int StructSize;
        public int Found, Calipers, PointsFound, PointsUsed;
        public float Cx, Cy, Mcx, Mcy, MajorPx, MinorPx, AngleDeg, FitErrorPx, RoundnessPx, RmsPx, MeanContrast;
        public readonly int Size => StructSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CornerParams
    {
        public int StructSize;
        public LineParams Line1, Line2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CornerResult : ISized
    {
        public int StructSize;
        public int Found, Parallel;
        public float X, Y, Mx, My, AngleDeg;
        public LineResult Line1, Line2;
        public readonly int Size => StructSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GapParams
    {
        public int StructSize;
        public Box Box;
        public int Samples;
        public CaliperParams Caliper;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GapResult : ISized
    {
        public int StructSize;
        public int Samples, Found;
        public float MinPx, MaxPx, MeanPx;
        public int MinIndex, MaxIndex;
        public float MinX, MinY, MaxX, MaxY;
        public readonly int Size => StructSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PitchParams
    {
        public int StructSize;
        public Box Box;
        public int Polarity;
        public float Threshold, FilterHalfWidth, MinWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Pin
    {
        public float X, Y, Pos, WidthPx, Contrast;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PitchResult : ISized
    {
        public int StructSize;
        public int Count, Unpaired;
        public float Threshold, MeanPitchPx, MinPitchPx, MaxPitchPx;
        public readonly int Size => StructSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EdgesParams
    {
        public int StructSize;
        public Box Box;
        public int Polarity;
        public float Threshold, FilterHalfWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EdgesResult : ISized
    {
        public int StructSize;
        public int Count;
        public float Threshold, MeanSpacingPx, MinSpacingPx, MaxSpacingPx, PeriodPx;
        public readonly int Size => StructSize;
    }

    [DllImport(Lib, EntryPoint = "vs_meas_default_caliper")] internal static extern void DefaultCaliper(out CaliperParams p);
    [DllImport(Lib, EntryPoint = "vs_meas_default_line")] internal static extern void DefaultLine(out LineParams p);
    [DllImport(Lib, EntryPoint = "vs_meas_default_circle")] internal static extern void DefaultCircle(out CircleParams p);
    [DllImport(Lib, EntryPoint = "vs_meas_default_corner")] internal static extern void DefaultCorner(out CornerParams p);
    [DllImport(Lib, EntryPoint = "vs_meas_default_gap")] internal static extern void DefaultGap(out GapParams p);
    [DllImport(Lib, EntryPoint = "vs_meas_default_pitch")] internal static extern void DefaultPitch(out PitchParams p);
    [DllImport(Lib, EntryPoint = "vs_meas_default_edges")] internal static extern void DefaultEdges(out EdgesParams p);
    [DllImport(Lib, EntryPoint = "vs_meas_caliper")]
    internal static extern unsafe int Caliper(byte* px, int w, int h, int stride, ref Box box, ref CaliperParams p, Calib* calib,
        Edge* edges, int cap, ref CaliperResult r);
    [DllImport(Lib, EntryPoint = "vs_meas_line")]
    internal static extern unsafe int Line(byte* px, int w, int h, int stride, ref LineParams p, Calib* calib, float* pts, int cap, ref LineResult r);
    [DllImport(Lib, EntryPoint = "vs_meas_circle")]
    internal static extern unsafe int Circle(byte* px, int w, int h, int stride, ref CircleParams p, Calib* calib, float* pts, int cap, ref CircleResult r);
    [DllImport(Lib, EntryPoint = "vs_meas_ellipse")]
    internal static extern unsafe int Ellipse(byte* px, int w, int h, int stride, ref CircleParams p, Calib* calib, float* pts, int cap, ref EllipseResult r);
    [DllImport(Lib, EntryPoint = "vs_meas_corner")]
    internal static extern unsafe int Corner(byte* px, int w, int h, int stride, ref CornerParams p, Calib* calib, ref CornerResult r);
    [DllImport(Lib, EntryPoint = "vs_meas_gap")]
    internal static extern unsafe int Gap(byte* px, int w, int h, int stride, ref GapParams p, Calib* calib, float* widths, int cap, ref GapResult r);
    [DllImport(Lib, EntryPoint = "vs_meas_pitch")]
    internal static extern unsafe int Pitch(byte* px, int w, int h, int stride, ref PitchParams p, Calib* calib, Pin* pins, int cap, ref PitchResult r);
    [DllImport(Lib, EntryPoint = "vs_meas_edges")]
    internal static extern unsafe int Edges(byte* px, int w, int h, int stride, ref EdgesParams p, Calib* calib, Edge* edges, int cap, ref EdgesResult r);
    [DllImport(Lib, EntryPoint = "vs_meas_to_measure")]
    internal static extern unsafe void ToMeasure(Calib* calib, float x, float y, out float mx, out float my);
    [DllImport(Lib, EntryPoint = "vs_meas_to_image")]
    internal static extern unsafe void ToImage(Calib* calib, float mx, float my, out float x, out float y);
}

/// <summary>A turned box in image pixels: centre, length along its direction, height across it, and its angle
/// in degrees counter-clockwise on the screen (0 = left to right). Calipers search along the length.</summary>
public sealed record RotBox(float Cx, float Cy, float Length, float Height, float AngleDeg);

public enum EdgePolarity { Any, DarkToLight, LightToDark }
public enum CaliperMode { Single, Pair }
public enum CaliperScore { Contrast, Position, Width }
public enum FitMethod { Ransac, LeastSquares }

/// <summary>One feature the Measure tool finds: an edge or edge pair, a straight edge, a hole or boss (round or oval),
/// a corner, widths along a gap, a row of pins, the edges along a line, or a point, line or circle constructed from
/// features before it. Drawn on the teach image; with a Find the part tool on the camera it moves and turns with
/// the part.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CaliperFeature), "caliper")]
[JsonDerivedType(typeof(LineFeature), "line")]
[JsonDerivedType(typeof(CircleFeature), "circle")]
[JsonDerivedType(typeof(EllipseFeature), "ellipse")]
[JsonDerivedType(typeof(CornerFeature), "corner")]
[JsonDerivedType(typeof(GapFeature), "gap")]
[JsonDerivedType(typeof(PitchFeature), "pitch")]
[JsonDerivedType(typeof(EdgeCountFeature), "edges")]
[JsonDerivedType(typeof(ConstructFeature), "construct")]
public abstract class MeasureFeature
{
    public string Name { get; set; } = "";
    /// <summary>Direction of the (first) edge along the search: dark to light, light to dark or either.</summary>
    public EdgePolarity Polarity { get; set; }
    /// <summary>Lowest edge contrast in grey levels; 0 = auto (from the image's noise, at least 5).</summary>
    public float Threshold { get; set; }
    /// <summary>Smoothing in px (1-20): larger for noisy or soft edges.</summary>
    public float FilterHalfWidth { get; set; } = 2;
}

/// <summary>Caliper (spec 2.1): an edge, or a pair of edges, along a turned box.</summary>
public sealed class CaliperFeature : MeasureFeature
{
    public RotBox Box { get; set; } = new(0, 0, 40, 20, 0);
    /// <summary>Pair: the first edge has <see cref="MeasureFeature.Polarity"/>, the second the opposite (a bright bar for dark to light).</summary>
    public CaliperMode Mode { get; set; }
    /// <summary>Pair: the width expected, in px, for <see cref="CaliperScore.Width"/>; 0 = none.</summary>
    public float ExpectedWidth { get; set; }
    public CaliperScore ScoreBy { get; set; }
}

/// <summary>Find line (spec 2.2): calipers across the drawn line, fitted. They search across it from its left
/// to its right side seen from (X0, Y0) towards (X1, Y1): top to bottom for a line drawn left to right.</summary>
public sealed class LineFeature : MeasureFeature
{
    public float X0 { get; set; }
    public float Y0 { get; set; }
    public float X1 { get; set; }
    public float Y1 { get; set; }
    public int Calipers { get; set; } = 10;
    /// <summary>How far across the line each caliper looks, px.</summary>
    public float SearchWidth { get; set; } = 20;
    /// <summary>Points farther than this from the fitted line are left out, px.</summary>
    public float OutlierPx { get; set; } = 1.5f;
    public FitMethod Fit { get; set; }
    /// <summary>0..1: the share of calipers whose point must be used, or the edge counts as broken.</summary>
    public float MinShare { get; set; } = 0.6f;
}

/// <summary>Calipers across the ring between the two radii (a circle or an ellipse is fitted to their points).</summary>
public abstract class RingFeature : MeasureFeature
{
    public float Cx { get; set; }
    public float Cy { get; set; }
    public float RInner { get; set; }
    public float ROuter { get; set; }
    /// <summary>The arc searched: degrees counter-clockwise from the right (span 360 = the whole ring).</summary>
    public float StartDeg { get; set; }
    public float SpanDeg { get; set; } = 360;
    public int Calipers { get; set; } = 36;
    /// <summary>True: calipers search from the outer radius inwards (polarity seen that way).</summary>
    public bool Inward { get; set; }
    public float OutlierPx { get; set; } = 1.5f;
    public FitMethod Fit { get; set; }
    public float MinShare { get; set; } = 0.6f;
}

/// <summary>Find circle (spec 2.3): a hole or boss.</summary>
public sealed class CircleFeature : RingFeature { }

/// <summary>Find ellipse (spec 2.4): an oval hole, or a round one seen at a slant. The ring must hold the whole rim:
/// the inner radius below half the smallest diameter, the outer above half the largest.</summary>
public sealed class EllipseFeature : RingFeature { }

/// <summary>A drawn line, image pixels.</summary>
public sealed record Segment(float X0, float Y0, float X1, float Y1);

/// <summary>Find corner (spec 2.5): two straight edges, each found as a <see cref="LineFeature"/> along its drawn
/// segment, and the point where they meet.</summary>
public sealed class CornerFeature : MeasureFeature
{
    public Segment Edge1 { get; set; } = new(0, 0, 0, 0);
    public Segment Edge2 { get; set; } = new(0, 0, 0, 0);
    public int Calipers { get; set; } = 10;
    public float SearchWidth { get; set; } = 20;
    public float OutlierPx { get; set; } = 1.5f;
    public FitMethod Fit { get; set; }
    /// <summary>0..1: the share of each edge's calipers whose point must be used.</summary>
    public float MinShare { get; set; } = 0.6f;
}

/// <summary>Edge width / gap (spec 2.7): the box is cut across its height into <see cref="Samples"/> slices, and each
/// slice measures the width between two edges along the box: a strip, slot or seam running across the box. The
/// first edge has <see cref="MeasureFeature.Polarity"/> (a dark gap on bright: light to dark). Every slice must
/// give a width: a closed gap, a slice whose edges are in doubt, or one with an edge inside the gap (a burr)
/// fails. A gap needs a check on its widths with a limit.</summary>
public sealed class GapFeature : MeasureFeature
{
    public RotBox Box { get; set; } = new(0, 0, 40, 40, 0);
    public int Samples { get; set; } = 20;
}

/// <summary>Pin pitch (spec 2.8): a row of pins along the box (starting and ending on the background). Each pin is
/// an edge of <see cref="MeasureFeature.Polarity"/> (dark to light: bright pins on dark; any: that of the first
/// edge) and the next edge. A count other than <see cref="ExpectedCount"/>, an edge that belongs to no pin, or a pin
/// fainter than half the row's usual contrast (a stub, a missing pin's trace) fails. It needs a Pitch check with a
/// limit.</summary>
public sealed class PitchFeature : MeasureFeature
{
    /// <summary>Smoothing 1 px (not 2): a neighbour's response stays out of each edge's window.</summary>
    public PitchFeature() => FilterHalfWidth = 1;

    public RotBox Box { get; set; } = new(0, 0, 100, 20, 0);
    public int ExpectedCount { get; set; }
    /// <summary>Narrower pairs are not pins, px.</summary>
    public float MinWidth { get; set; } = 1;
}

/// <summary>Count edges / thread (spec 2.9): every edge of <see cref="MeasureFeature.Polarity"/> along the box; the
/// first and last edge are placed as exactly as the inner ones (the engine takes off their lean on a soft image), so
/// the Pitch check holds every spacing to its limits. It needs <see cref="ExpectedCount"/> or a check with a limit.</summary>
public sealed class EdgeCountFeature : MeasureFeature
{
    public EdgeCountFeature() => FilterHalfWidth = 1;

    public RotBox Box { get; set; } = new(0, 0, 100, 10, 0);
    /// <summary>The edges expected; 0 = not checked (then check Count or Pitch).</summary>
    public int ExpectedCount { get; set; }
}

/// <summary>Constructions of spec 2.6. Intersection: of two lines (a point). Perpendicular: the foot of the
/// perpendicular from a point onto a line (a point). Midpoint: of two points. LineThrough: two points. CircleThrough:
/// three points.</summary>
public enum ConstructOp { Intersection, Perpendicular, Midpoint, LineThrough, CircleThrough }

/// <summary>A point, line or circle made from features listed before it (spec 2.6), for distances and angles.
/// Points come from calipers, circles, ellipses (centres), corners and point constructions; lines from lines and
/// LineThrough.</summary>
public sealed class ConstructFeature : MeasureFeature
{
    public ConstructOp Operation { get; set; }
    /// <summary>The features used, by name, in the order the operation lists them (Perpendicular: the point, then the line).</summary>
    public List<string> Inputs { get; set; } = new();
}

/// <summary>What a check measures. Position and Width are a caliper's; Angle and Straightness a line's (Straightness and
/// Roundness count every point found, also those left out of the fit);
/// Diameter, Radius and Roundness a circle's; Distance and AngleBetween are between two features (spec 2.6).
/// Major, Minor and AxisRatio (minor / major) an ellipse's; CornerAngle a corner's; MinWidth, MaxWidth and MeanWidth
/// a gap's; Count, Pitch (every pitch or spacing in the limits: the worst is reported) and MeanPitch a row of pins'
/// or an edge count's.</summary>
public enum MeasureValue
{
    Position, Width, Angle, Straightness, Diameter, Radius, Roundness, Distance, AngleBetween,
    Major, Minor, AxisRatio, CornerAngle, MinWidth, MaxWidth, MeanWidth, Count, Pitch, MeanPitch,
}
/// <summary>None: a count or a ratio.</summary>
public enum MeasureUnit { Px, Mm, Deg, None }

/// <summary>A measured value with its tolerance: the part fails when it is below Min or above Max (either may be
/// left out; with neither the value is only reported).</summary>
public sealed class MeasureCheck
{
    public string Name { get; set; } = "";
    public MeasureValue Value { get; set; }
    public string Feature { get; set; } = "";
    /// <summary>The second feature of Distance and AngleBetween.</summary>
    public string? Feature2 { get; set; }
    /// <summary>Px or Mm for sizes (Mm needs the camera's calibration), Deg for angles, None for Count and AxisRatio.</summary>
    public MeasureUnit Unit { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
}

/// <summary>Check size (tool spec 2): features found by calipers, and checks of their sizes, distances and angles.</summary>
public sealed class MeasureToolConfig : ToolConfig
{
    public List<MeasureFeature> Features { get; set; } = new();
    public List<MeasureCheck> Checks { get; set; } = new();

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class MeasureTool : ITool
{
    private readonly MeasureToolConfig _cfg;
    private readonly MeasureNative.Calib _calib;
    private readonly bool _hasCalib;

    /// <param name="calibration">The camera's lens and scale at the part (Set real size); null = sizes in px only.</param>
    public MeasureTool(MeasureToolConfig cfg, BarcodeReading.LensCalibration? calibration = null)
    {
        _cfg = cfg;
        if (calibration is { } l)
        {
            _calib = new MeasureNative.Calib
            {
                Fx = l.Fx, Fy = l.Fy, Cx = l.Cx, Cy = l.Cy, K1 = l.K1, K2 = l.K2, P1 = l.P1, P2 = l.P2, K3 = l.K3, MmPerPx = l.MmPerPx,
            };
            _hasCalib = true;
        }
    }

    public ToolConfig Config => _cfg;
    private double MmPerPx => _hasCalib ? _calib.MmPerPx : 0;

    /// <summary>Why the tool cannot run as set up (a sentence for the engineer), or null.</summary>
    public static string? SetupProblem(MeasureToolConfig c, BarcodeReading.LensCalibration? calibration)
    {
        if (c.Features.Count == 0) return "add a feature to measure (an edge, a line or a circle)";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var earlier = new Dictionary<string, MeasureFeature>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in c.Features)
        {
            if (string.IsNullOrWhiteSpace(f.Name)) return "every feature needs a name";
            if (!names.Add(f.Name)) return $"two features are called {f.Name}";
            if (f.FilterHalfWidth is < 1 or > 20) return $"{f.Name}: the smoothing is 1 to 20 px";
            if (f.Threshold is < 0 or > 255) return $"{f.Name}: the edge threshold is 0 (auto) to 255 grey levels";
            switch (f)
            {
                case CaliperFeature k when k.Box.Length < 3 || k.Box.Height < 1:
                    return $"{f.Name}: the box must be at least 3 px long and 1 px high";
                case LineFeature l when Math.Sqrt((l.X1 - l.X0) * (l.X1 - l.X0) + (l.Y1 - l.Y0) * (l.Y1 - l.Y0)) < 2:
                    return $"{f.Name}: draw the line along the edge";
                case LineFeature l when l.Calipers is < 3 or > 200:
                    return $"{f.Name}: a line has 3 to 200 calipers";
                case LineFeature l when l.SearchWidth < 3 || l.OutlierPx <= 0 || l.MinShare is <= 0 or > 1:
                    return $"{f.Name}: the search width is at least 3 px, the outlier distance above 0 and the share used above 0 and at most 1";
                case RingFeature r when r.RInner < 0 || r.ROuter - r.RInner < 3:
                    return $"{f.Name}: the outer radius must be at least 3 px beyond the inner";
                case RingFeature r when r.Calipers is < 8 or > 360 || r.SpanDeg is <= 0 or > 360:
                    return $"{f.Name}: a circle or ellipse has 8 to 360 calipers over an arc of up to 360°";
                case RingFeature r when r.OutlierPx <= 0 || r.MinShare is <= 0 or > 1:
                    return $"{f.Name}: the outlier distance must be above 0 and the share used above 0 and at most 1";
                case CornerFeature k when Length(k.Edge1) < 2 || Length(k.Edge2) < 2:
                    return $"{f.Name}: draw both edges of the corner";
                case CornerFeature k when k.Calipers is < 3 or > 200 || k.SearchWidth < 3 || k.OutlierPx <= 0 || k.MinShare is <= 0 or > 1:
                    return $"{f.Name}: each edge has 3 to 200 calipers, a search width of at least 3 px, an outlier distance above 0 and a share used above 0 and at most 1";
                case GapFeature g when g.Box.Length < 3 || g.Samples is < 1 or > 200 || g.Box.Height / g.Samples < 1:
                    return $"{f.Name}: the box must be at least 3 px long, with 1 to 200 slices of at least 1 px each";
                case PitchFeature k when k.Box.Length < 3 || k.Box.Height < 1:
                    return $"{f.Name}: the box must be at least 3 px long and 1 px high";
                case PitchFeature k when k.ExpectedCount < 1 || k.MinWidth < 0:
                    return $"{f.Name}: set the number of pins expected";
                case EdgeCountFeature k when k.Box.Length < 3 || k.Box.Height < 1 || k.ExpectedCount < 0:
                    return $"{f.Name}: the box must be at least 3 px long and 1 px high, and the edges expected 0 (not checked) or more";
                case ConstructFeature k when ConstructProblem(k, earlier) is { } why:
                    return $"{f.Name}: {why}";
            }
            earlier[f.Name] = f;
        }
        var byName = c.Features.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var checks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in c.Checks)
        {
            if (string.IsNullOrWhiteSpace(k.Name)) return "every check needs a name";
            if (!checks.Add(k.Name)) return $"two checks are called {k.Name}";
            if (!byName.TryGetValue(k.Feature, out var f1)) return $"{k.Name}: pick a feature";
            MeasureFeature? f2 = null;
            if (k.Value is MeasureValue.Distance or MeasureValue.AngleBetween && (k.Feature2 is null || !byName.TryGetValue(k.Feature2, out f2)))
                return $"{k.Name}: pick the second feature";
            bool fits = k.Value switch
            {
                MeasureValue.Position => f1 is CaliperFeature,
                MeasureValue.Width => f1 is CaliperFeature { Mode: CaliperMode.Pair },
                MeasureValue.Angle => GivesLine(f1),
                MeasureValue.Straightness => f1 is LineFeature,
                MeasureValue.Diameter or MeasureValue.Radius => f1 is CircleFeature or ConstructFeature { Operation: ConstructOp.CircleThrough },
                MeasureValue.Roundness => f1 is RingFeature,
                MeasureValue.Major or MeasureValue.Minor or MeasureValue.AxisRatio => f1 is EllipseFeature,
                MeasureValue.CornerAngle => f1 is CornerFeature,
                MeasureValue.MinWidth or MeasureValue.MaxWidth or MeasureValue.MeanWidth => f1 is GapFeature,
                MeasureValue.Count or MeasureValue.Pitch or MeasureValue.MeanPitch => f1 is PitchFeature or EdgeCountFeature,
                MeasureValue.AngleBetween => GivesLine(f1) && f2 is not null && GivesLine(f2),
                MeasureValue.Distance => (GivesPoint(f1) || GivesLine(f1)) && f2 is not null && (GivesPoint(f2) || GivesLine(f2)),
                _ => false,
            };
            if (!fits) return $"{k.Name}: {Describe(k.Value)} cannot be measured on {k.Feature}" +
                              (k.Value == MeasureValue.Width ? " (a width needs a caliper in pair mode)" : "");
            bool angle = k.Value is MeasureValue.Angle or MeasureValue.AngleBetween or MeasureValue.CornerAngle;
            bool plain = k.Value is MeasureValue.Count or MeasureValue.AxisRatio;
            if (plain != (k.Unit == MeasureUnit.None)) return plain ? $"{k.Name}: a count or ratio has no unit" : $"{k.Name}: pick a unit";
            if (!plain && angle != (k.Unit == MeasureUnit.Deg)) return angle ? $"{k.Name}: an angle is in degrees" : $"{k.Name}: a size is in px or mm";
            if (k.Unit == MeasureUnit.Mm && !(calibration is { MmPerPx: > 0 }))
                return $"{k.Name}: sizes in mm need the camera's real size (calibrate the camera first) or use px";
            if (k.Min is { } lo && k.Max is { } hi && lo > hi) return $"{k.Name}: the lowest allowed value is above the highest";
            if (k.Min is { } a && !double.IsFinite(a) || k.Max is { } b && !double.IsFinite(b)) return $"{k.Name}: the limits must be numbers";
        }
        // gap, pins and edge counts decide nothing alone: each needs its tolerance (spec 2.7-2.9, Essential)
        static bool Limited(MeasureCheck k) => k.Min is not null || k.Max is not null;
        foreach (var f in c.Features)
        {
            var mine = c.Checks.Where(k => k.Feature.Equals(f.Name, StringComparison.OrdinalIgnoreCase) && Limited(k)).ToList();
            switch (f)
            {
                case GapFeature when mine.Count == 0:
                    return $"{f.Name}: add a check on the gap's width with a limit";
                case PitchFeature when !mine.Any(k => k.Value == MeasureValue.Pitch):
                    return $"{f.Name}: add a Pitch check with the allowed pitch";
                case EdgeCountFeature e when e.ExpectedCount == 0 && mine.Count == 0:
                    return $"{f.Name}: set the edges expected or add a check with a limit";
            }
        }
        return null;
    }

    static double Length(Segment s) => Math.Sqrt((s.X1 - s.X0) * (s.X1 - s.X0) + (s.Y1 - s.Y0) * (s.Y1 - s.Y0));

    // features that give a point (calipers: the edge or the pair's middle; circles and ellipses: the centre)
    static bool GivesPoint(MeasureFeature f) =>
        f is CaliperFeature or RingFeature or CornerFeature || f is ConstructFeature { Operation: not ConstructOp.LineThrough };

    static bool GivesLine(MeasureFeature f) => f is LineFeature or ConstructFeature { Operation: ConstructOp.LineThrough };

    static string? ConstructProblem(ConstructFeature k, IReadOnlyDictionary<string, MeasureFeature> earlier)
    {
        var need = k.Operation switch
        {
            ConstructOp.Intersection => new[] { "line", "line" },
            ConstructOp.Perpendicular => new[] { "point", "line" },
            ConstructOp.CircleThrough => new[] { "point", "point", "point" },
            _ => new[] { "point", "point" },
        };
        if (k.Inputs.Count != need.Length) return $"pick {need.Length} features ({string.Join(", ", need)})";
        for (int i = 0; i < need.Length; i++)
        {
            if (!earlier.TryGetValue(k.Inputs[i], out var f)) return $"{k.Inputs[i]} must be a feature listed before this one";
            if (need[i] == "line" ? !GivesLine(f) : !GivesPoint(f)) return $"{k.Inputs[i]} gives no {need[i]}";
        }
        return null;
    }

    static string Describe(MeasureValue v) => v switch
    {
        MeasureValue.AngleBetween => "an angle between",
        MeasureValue.Distance => "a distance",
        MeasureValue.AxisRatio => "the axis ratio",
        MeasureValue.CornerAngle => "a corner angle",
        MeasureValue.MinWidth or MeasureValue.MaxWidth or MeasureValue.MeanWidth => "a gap width",
        MeasureValue.MeanPitch => "the mean pitch",
        _ => "the " + v.ToString().ToLowerInvariant(),
    };

    // one feature as found: measure-space (undistorted px) geometry for the checks, image points for drawing
    sealed class Found
    {
        public bool Ok;
        public string? Problem;
        public string? Explain;
        public string Note = "";
        public double Mx, My;            // caliper: edge or pair middle; circle, ellipse: centre; corner; point constructions
        public double Mx0, My0, Mx1, My1; // line ends
        public double Position, WidthPx, AngleDeg, StraightnessPx, RadiusPx, RoundnessPx;
        public double MajorPx, MinorPx, CornerAngle, MinW, MaxW, MeanW, MeanPitch;
        public int Count;
        public List<double> Pitches = new();  // pins: centre to centre; edges: edge to edge (measure px)
        public List<double> PinPos = new();   // pins: centres along the box (measure px)
        public PointF[]? MinMark, MaxMark;      // gap: the narrowest and widest slice
        public bool IsLine, IsPoint;
        public readonly List<Graphic> Graphics = new();
    }

    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null)
    {
        var sw = Stopwatch.StartNew();
        var found = new Dictionary<string, Found>(StringComparer.OrdinalIgnoreCase);
        var details = new List<string>();
        var graphics = new List<Graphic>();
        string? problem = null, explain = null;
        foreach (var f in _cfg.Features)
        {
            var r = f is ConstructFeature c ? Construct(c, found) : Find(image, f, fixture);
            found[f.Name] = r;
            graphics.AddRange(r.Graphics);
            details.Add($"{f.Name}: {(r.Ok ? r.Note : r.Problem)}");
            if (!r.Ok && problem is null) { problem = $"{f.Name}: {r.Problem}"; explain = r.Explain; }
        }
        var values = new Dictionary<string, double>();
        double mm = MmPerPx;
        foreach (var k in _cfg.Checks)
        {
            var f1 = found[k.Feature];
            var f2 = k.Feature2 is null ? null : found.GetValueOrDefault(k.Feature2);
            if (!f1.Ok || f2 is { Ok: false })
            {
                details.Add($"{k.Name}: not measured, {(f1.Ok ? k.Feature2 : k.Feature)} not found");
                continue;
            }
            double scale = k.Unit == MeasureUnit.Mm ? mm : 1;
            int worstAt = -1;
            double? v = Value(k, f1, f2, fixture, scale, ref worstAt);
            // an angle is compared on the turn nearest the middle of its limits, so limits round 180° work
            if (v is { } deg && k.Unit == MeasureUnit.Deg && (k.Min ?? k.Max) is { } lo0)
                v = ((k.Min ?? lo0) + (k.Max ?? lo0)) / 2 + Wrap180(deg - ((k.Min ?? lo0) + (k.Max ?? lo0)) / 2);
            string unit = k.Unit switch { MeasureUnit.Mm => " mm", MeasureUnit.Deg => " °", MeasureUnit.None => "", _ => " px" };
            if (v is { } px && k.Unit == MeasureUnit.Mm) v = mm > 0 ? px * mm : null;
            if (v is not { } val || !double.IsFinite(val))
            {
                string why = k.Unit == MeasureUnit.Mm && mm <= 0 ? "the camera has no real size (calibrate it)" : "could not be measured";
                details.Add($"{k.Name}: {why}");
                if (problem is null)
                {
                    problem = $"{k.Name} {why}";
                    explain = k.Unit == MeasureUnit.Mm && mm <= 0 ? "explain.measure.no_calibration" : "explain.measure.not_measured";
                }
                continue;
            }
            values[k.Name] = val;
            bool low = k.Min is { } lo && val < lo, high = k.Max is { } hi && val > hi;
            string limits = (k.Min, k.Max) switch
            {
                ({ } a, { } b) => $", allowed {N(a)}–{N(b)}{unit}",
                ({ } a, null) => $", at least {N(a)}{unit}",
                (null, { } b) => $", at most {N(b)}{unit}",
                _ => "",
            };
            details.Add($"{k.Name}: {N(val)}{unit}{limits}{(low || high ? " — out of tolerance" : "")}");
            if ((low || high) && problem is null)
            {
                problem = $"{k.Name} is {N(val)}{unit}{limits}";
                bool pins = _cfg.Features.Find(x => x.Name.Equals(k.Feature, StringComparison.OrdinalIgnoreCase)) is PitchFeature;
                // the gap's red mark: the narrowest slice when too narrow, the widest when too wide
                if (k.Value is MeasureValue.MinWidth or MeasureValue.MaxWidth or MeasureValue.MeanWidth && (low ? f1.MinMark : f1.MaxMark) is { } mark)
                    graphics.Add(new Graphic(mark, GraphicKind.Fail));
                // a pitch out of tolerance: the pin that is out of place, against the row's own straight run
                if (pins && k.Value == MeasureValue.Pitch && OutOfPlace(f1.PinPos, worstAt) is var (pin, off))
                    problem = $"{k.Name}: pin {pin} is out of place by {N(off * scale)}{unit} (pitch {N(val)}{unit}{limits})";
                explain = k.Value switch
                {
                    MeasureValue.Position or MeasureValue.Width => "explain.caliper.out_of_tolerance",
                    MeasureValue.Angle => "explain.line.angle",
                    MeasureValue.Straightness => "explain.line.straightness",
                    MeasureValue.Diameter or MeasureValue.Radius or MeasureValue.Major or MeasureValue.Minor => "explain.circle.diameter",
                    MeasureValue.Roundness or MeasureValue.AxisRatio => "explain.circle.out_of_round",
                    MeasureValue.CornerAngle => "explain.corner.angle",
                    MeasureValue.MinWidth or MeasureValue.MaxWidth or MeasureValue.MeanWidth => low ? "explain.gap.narrow" : "explain.gap.wide",
                    MeasureValue.Count => pins ? "explain.pitch.count" : "explain.edges.count",
                    MeasureValue.Pitch or MeasureValue.MeanPitch => pins ? "explain.pitch.bent" : "explain.edges.pitch",
                    _ => "explain.geometry.out_of_tolerance",
                };
            }
        }
        bool pass = problem is null;
        string summary = pass ? (values.Count > 0 ? "Sizes OK: " + string.Join(", ", values.Select(kv => $"{kv.Key} {N(kv.Value)}")) : "Features found")
                              : problem!;
        return new ToolResult(_cfg.Name, pass, summary, details, sw.Elapsed.TotalMilliseconds, graphics) { Explain = explain, Values = values };
    }

    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // the check's value in measure-space px (Mm: times the scale, done by the caller), degrees or plain; scale (the
    // check's unit per px) picks the worst pitch against the limits, and worstAt says which
    static double? Value(MeasureCheck k, Found a, Found? b, Fixture? fixture, double scale, ref int worstAt)
    {
        switch (k.Value)
        {
            case MeasureValue.Major: return a.MajorPx;
            case MeasureValue.Minor: return a.MinorPx;
            case MeasureValue.AxisRatio: return a.MajorPx > 0 ? a.MinorPx / a.MajorPx : null;
            case MeasureValue.CornerAngle: return a.CornerAngle;
            case MeasureValue.MinWidth: return a.MinW;
            case MeasureValue.MaxWidth: return a.MaxW;
            case MeasureValue.MeanWidth: return a.MeanW;
            case MeasureValue.Count: return a.Count;
            case MeasureValue.MeanPitch: return a.Count >= 2 ? a.MeanPitch : null;
            case MeasureValue.Pitch:
                worstAt = Worst(a.Pitches, k.Min, k.Max, scale);
                return worstAt < 0 ? null : a.Pitches[worstAt];
            case MeasureValue.Position: return a.Position;
            case MeasureValue.Width: return a.WidthPx;
            case MeasureValue.Angle: return Wrap180(a.AngleDeg - (fixture?.AngleDeg ?? 0)); // against the part
            case MeasureValue.Straightness: return a.StraightnessPx;
            case MeasureValue.Diameter: return 2 * a.RadiusPx;
            case MeasureValue.Radius: return a.RadiusPx;
            case MeasureValue.Roundness: return a.RoundnessPx;
            case MeasureValue.AngleBetween:
                return Math.Abs(Wrap180(b!.AngleDeg - a.AngleDeg)); // between the lines' directions, 0..180
            case MeasureValue.Distance:
                if (a.IsPoint && b!.IsPoint) return Math.Sqrt((a.Mx - b.Mx) * (a.Mx - b.Mx) + (a.My - b.My) * (a.My - b.My));
                if (a.IsPoint && b!.IsLine) return LineDistance(b, a.Mx, a.My);
                if (a.IsLine && b!.IsPoint) return LineDistance(a, b.Mx, b.My);
                // two lines: from the middle of the first to the second
                return LineDistance(b!, (a.Mx0 + a.Mx1) / 2, (a.My0 + a.My1) / 2);
            default: return null;
        }
    }


    // the pitch farthest out: from the middle of the limits, or below the lowest or above the highest alone
    static int Worst(List<double> xs, double? lo, double? hi, double scale)
    {
        if (xs.Count == 0) return -1;
        double mid = (lo, hi) switch
        {
            ({ } a, { } b) => (a + b) / 2,
            ({ } a, null) => a + 1e12,
            (null, { } b) => b - 1e12,
            _ => xs.Average() * scale,
        };
        int w = 0;
        for (int i = 1; i < xs.Count; i++)
            if (Math.Abs(xs[i] * scale - mid) > Math.Abs(xs[w] * scale - mid)) w = i;
        return w;
    }

    // of the two pins either side of pitch j, the one farther from the straight run fitted to every pin
    // (centre = p0 + P x index, least squares); 1-based, and how far, px
    static (int Pin, double Off)? OutOfPlace(List<double> pos, int j)
    {
        int n = pos.Count;
        if (j < 0 || j + 1 >= n) return null;
        double mi = (n - 1) / 2.0, mp = pos.Average(), sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++) { sxy += (i - mi) * (pos[i] - mp); sxx += (i - mi) * (i - mi); }
        double slope = sxx > 0 ? sxy / sxx : 0;
        double Res(int i) => pos[i] - (mp + slope * (i - mi));
        int pin = Math.Abs(Res(j)) >= Math.Abs(Res(j + 1)) ? j : j + 1;
        return (pin + 1, Math.Abs(Res(pin)));
    }

    static double LineDistance(Found l, double x, double y)
    {
        double dx = l.Mx1 - l.Mx0, dy = l.My1 - l.My0, len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-9 ? double.NaN : Math.Abs((x - l.Mx0) * dy - (y - l.My0) * dx) / len;
    }

    static double Wrap180(double d)
    {
        d %= 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return d;
    }

    static int Pol(EdgePolarity p) => (int)p;

    MeasureNative.CaliperParams CaliperParams(MeasureFeature f)
    {
        MeasureNative.DefaultCaliper(out var p);
        p.Polarity = Pol(f.Polarity);
        p.Threshold = f.Threshold;
        p.FilterHalfWidth = f.FilterHalfWidth;
        return p;
    }

    unsafe Found Find(GrayImage image, MeasureFeature f, Fixture? fx)
    {
        var o = new Found();
        float turn = fx?.AngleDeg ?? 0;
        PointF Map(float x, float y) => fx is null ? new PointF(x, y) : fx.Map(x, y);
        var cal = _calib;
        MeasureNative.Calib* pc = _hasCalib ? &cal : null;
        fixed (byte* px = image.Pixels)
        {
            switch (f)
            {
                case CaliperFeature k:
                {
                    var c = Map(k.Box.Cx, k.Box.Cy);
                    var box = new MeasureNative.Box { Cx = c.X, Cy = c.Y, Length = k.Box.Length, Height = k.Box.Height, AngleDeg = k.Box.AngleDeg + turn };
                    var p = CaliperParams(f);
                    p.Mode = (int)k.Mode;
                    p.ExpectedWidth = k.ExpectedWidth;
                    p.ScoreBy = (int)k.ScoreBy;
                    var r = MeasureNative.Result<MeasureNative.CaliperResult>();
                    int n = MeasureNative.Caliper(px, image.Width, image.Height, image.Stride, ref box, ref p, pc, null, 0, ref r);
                    var outline = BoxCorners(box);
                    if (n == -2) return Fail(o, "the box is off the image", "explain.measure.off_image", outline);
                    if (n < 0 || !MeasureNative.Matches(r)) return Fail(o, n < 0 ? "the caliper could not run" : Mismatch, "explain.measure.not_measured", outline);
                    if (r.Found == 0)
                    {
                        // a third of the threshold finds it: the edge is there but faint
                        var weak = p;
                        weak.Threshold = r.Threshold / 3;
                        var w = MeasureNative.Result<MeasureNative.CaliperResult>();
                        MeasureNative.Caliper(px, image.Width, image.Height, image.Stride, ref box, ref weak, pc, null, 0, ref w);
                        return w.Found != 0
                            ? Fail(o, $"the edge is faint (contrast {Math.Abs(w.Edge0.Contrast):0}, needs {r.Threshold:0})", "explain.caliper.weak_edge", outline)
                            : Fail(o, k.Mode == CaliperMode.Pair ? "no edge pair found" : "no edge found", "explain.caliper.no_edge", outline);
                    }
                    if (r.Ambiguous != 0) return Fail(o, $"several edges found ({r.Candidates}), the choice is in doubt", "explain.caliper.many_edges", outline);
                    o.Ok = true;
                    o.IsPoint = true;
                    o.Position = MeasurePosition(box, r, pc);
                    if (k.Mode == CaliperMode.Pair)
                    {
                        o.Mx = (r.Mx0 + r.Mx1) / 2;
                        o.My = (r.My0 + r.My1) / 2;
                        o.WidthPx = r.WidthPx;
                        o.Note = $"width {r.WidthPx:0.###} px, contrast {Math.Abs(r.Edge0.Contrast):0} / {Math.Abs(r.Edge1.Contrast):0}";
                    }
                    else
                    {
                        o.Mx = r.Mx0;
                        o.My = r.My0;
                        o.Note = $"edge at {r.Position:0.###} px, contrast {Math.Abs(r.Edge0.Contrast):0}";
                    }
                    o.Graphics.Add(new Graphic(outline, GraphicKind.Info));
                    o.Graphics.Add(new Graphic(EdgeMark(box, r.Edge0), GraphicKind.Ok));
                    if (k.Mode == CaliperMode.Pair) o.Graphics.Add(new Graphic(EdgeMark(box, r.Edge1), GraphicKind.Ok));
                    return o;
                }
                case LineFeature l:
                {
                    MeasureNative.DefaultLine(out var p);
                    var a = Map(l.X0, l.Y0);
                    var b = Map(l.X1, l.Y1);
                    p.X0 = a.X; p.Y0 = a.Y; p.X1 = b.X; p.Y1 = b.Y;
                    p.Calipers = l.Calipers;
                    p.SearchWidth = l.SearchWidth;
                    p.OutlierPx = l.OutlierPx;
                    p.Fit = l.Fit == FitMethod.Ransac ? 1 : 0;
                    p.Caliper = CaliperParams(f);
                    var pts = new float[2 * l.Calipers];
                    int rc;
                    var r = MeasureNative.Result<MeasureNative.LineResult>();
                    fixed (float* pp = pts) rc = MeasureNative.Line(px, image.Width, image.Height, image.Stride, ref p, pc, pp, l.Calipers, ref r);
                    var drawn = new[] { a, b };
                    if (rc < 0 || !MeasureNative.Matches(r)) return Fail(o, rc < 0 ? "the line could not run" : Mismatch, "explain.measure.not_measured", drawn);
                    if (r.Found == 0) return Fail(o, $"edge not found along the line ({r.PointsFound} of {r.Calipers} points)", "explain.line.not_found", drawn);
                    double share = (double)r.PointsUsed / r.Calipers;
                    var fitted = new[] { new PointF(r.X0, r.Y0), new PointF(r.X1, r.Y1) };
                    if (share < l.MinShare)
                        return Fail(o, $"only {share * 100:0}% of the edge found (needs {l.MinShare * 100:0}%)", "explain.line.broken", fitted);
                    o.Ok = true;
                    o.IsLine = true;
                    (o.Mx0, o.My0, o.Mx1, o.My1) = (r.Mx0, r.My0, r.Mx1, r.My1);
                    o.AngleDeg = r.AngleDeg;
                    o.StraightnessPx = r.StraightnessPx;
                    o.Note = $"angle {r.AngleDeg:0.###}°, {r.PointsUsed} of {r.Calipers} points, straightness {r.StraightnessPx:0.###} px";
                    o.Graphics.Add(new Graphic(fitted, GraphicKind.Ok));
                    AddPoints(o, pts);
                    return o;
                }
                case CircleFeature c:
                {
                    var p = RingParams(c, Map(c.Cx, c.Cy), turn);
                    var pts = new float[2 * c.Calipers];
                    int rc;
                    var r = MeasureNative.Result<MeasureNative.CircleResult>();
                    fixed (float* pp = pts) rc = MeasureNative.Circle(px, image.Width, image.Height, image.Stride, ref p, pc, pp, c.Calipers, ref r);
                    var ring = Ring(p.Cx, p.Cy, (c.RInner + c.ROuter) / 2);
                    if (rc < 0 || !MeasureNative.Matches(r)) return Fail(o, rc < 0 ? "the circle could not run" : Mismatch, "explain.measure.not_measured", ring);
                    double share = (double)r.PointsUsed / r.Calipers;
                    if (r.Found == 0 || share < c.MinShare)
                        return Fail(o, $"hole not found ({r.PointsUsed} of {r.Calipers} points agree, needs {c.MinShare * 100:0}%)", "explain.circle.not_found", ring);
                    o.Ok = true;
                    o.IsPoint = true;
                    (o.Mx, o.My) = (r.Mcx, r.Mcy);
                    o.RadiusPx = r.RadiusPx;
                    o.RoundnessPx = r.RoundnessPx;
                    o.Note = $"diameter {2 * r.RadiusPx:0.###} px, roundness {r.RoundnessPx:0.###} px, {r.PointsUsed} of {r.Calipers} points";
                    o.Graphics.Add(new Graphic(Ring(r.Cx, r.Cy, r.RadiusPx), GraphicKind.Ok));
                    AddPoints(o, pts);
                    return o;
                }
                case EllipseFeature c:
                {
                    var p = RingParams(c, Map(c.Cx, c.Cy), turn);
                    var pts = new float[2 * c.Calipers];
                    int rc;
                    var r = MeasureNative.Result<MeasureNative.EllipseResult>();
                    fixed (float* pp = pts) rc = MeasureNative.Ellipse(px, image.Width, image.Height, image.Stride, ref p, pc, pp, c.Calipers, ref r);
                    var ring = Ring(p.Cx, p.Cy, (c.RInner + c.ROuter) / 2);
                    if (rc < 0 || !MeasureNative.Matches(r)) return Fail(o, rc < 0 ? "the ellipse could not run" : Mismatch, "explain.measure.not_measured", ring);
                    double share = (double)r.PointsUsed / r.Calipers;
                    if (r.Found == 0 || share < c.MinShare)
                        return Fail(o, $"oval not found ({r.PointsUsed} of {r.Calipers} points agree, needs {c.MinShare * 100:0}%)", "explain.ellipse.not_found", ring);
                    o.Ok = true;
                    o.IsPoint = true;
                    (o.Mx, o.My) = (r.Mcx, r.Mcy);
                    (o.MajorPx, o.MinorPx) = (r.MajorPx, r.MinorPx);
                    o.RoundnessPx = r.RoundnessPx;
                    o.Note = $"{r.MajorPx:0.###} x {r.MinorPx:0.###} px at {r.AngleDeg:0.##}°, roundness {r.RoundnessPx:0.###} px, {r.PointsUsed} of {r.Calipers} points";
                    o.Graphics.Add(new Graphic(Oval(r.Cx, r.Cy, r.MajorPx / 2, r.MinorPx / 2, r.AngleDeg), GraphicKind.Ok));
                    AddPoints(o, pts);
                    return o;
                }
                case CornerFeature k:
                {
                    MeasureNative.DefaultCorner(out var p);
                    p.Line1 = CornerLine(p.Line1, k, k.Edge1, Map);
                    p.Line2 = CornerLine(p.Line2, k, k.Edge2, Map);
                    var r = MeasureNative.Result<MeasureNative.CornerResult>();
                    int rc = MeasureNative.Corner(px, image.Width, image.Height, image.Stride, ref p, pc, ref r);
                    var drawn = new[] { new PointF(p.Line1.X0, p.Line1.Y0), new PointF(p.Line1.X1, p.Line1.Y1), new PointF(p.Line2.X1, p.Line2.Y1), new PointF(p.Line2.X0, p.Line2.Y0) };
                    if (rc < 0 || !MeasureNative.Matches(r)) return Fail(o, rc < 0 ? "the corner could not run" : Mismatch, "explain.measure.not_measured", drawn);
                    double s1 = (double)r.Line1.PointsUsed / r.Line1.Calipers, s2 = (double)r.Line2.PointsUsed / r.Line2.Calipers;
                    if (r.Line1.Found == 0 || r.Line2.Found == 0 || Math.Min(s1, s2) < k.MinShare)
                        return Fail(o, $"corner not found (edges {s1 * 100:0}% and {s2 * 100:0}% found, needs {k.MinShare * 100:0}%)", "explain.corner.not_found", drawn);
                    if (r.Found == 0)
                        return Fail(o, r.Parallel != 0 ? "the two edges are parallel, so they do not meet" : "corner not found", "explain.corner.not_found", drawn);
                    o.Ok = true;
                    o.IsPoint = true;
                    (o.Mx, o.My) = (r.Mx, r.My);
                    o.CornerAngle = r.AngleDeg;
                    o.Note = $"corner at ({r.X:0.##}, {r.Y:0.##}), angle {r.AngleDeg:0.###}°";
                    o.Graphics.Add(new Graphic(new[] { new PointF(r.Line1.X0, r.Line1.Y0), new PointF(r.X, r.Y), new PointF(r.Line2.X0, r.Line2.Y0) }, GraphicKind.Ok));
                    o.Graphics.Add(new Graphic(Cross(r.X, r.Y), GraphicKind.Ok));
                    return o;
                }
                case GapFeature g:
                {
                    MeasureNative.DefaultGap(out var p);
                    p.Box = MapBox(g.Box, Map, turn);
                    p.Samples = g.Samples;
                    p.Caliper = CaliperParams(f);
                    p.Caliper.Mode = (int)CaliperMode.Pair;
                    var widths = new float[g.Samples];
                    int rc;
                    var r = MeasureNative.Result<MeasureNative.GapResult>();
                    fixed (float* pw = widths) rc = MeasureNative.Gap(px, image.Width, image.Height, image.Stride, ref p, pc, pw, g.Samples, ref r);
                    var outline = BoxCorners(p.Box);
                    if (rc == -2) return Fail(o, "the box is off the image", "explain.measure.off_image", outline);
                    if (rc < 0 || !MeasureNative.Matches(r)) return Fail(o, rc < 0 ? "the gap could not run" : Mismatch, "explain.measure.not_measured", outline);
                    // every slice must give a width: one without is a closed gap or edges in doubt (line safety)
                    int miss = Array.FindIndex(widths, w => !float.IsFinite(w));
                    if (miss >= 0)
                    {
                        Fail(o, $"no gap at slice {miss + 1} of {g.Samples} (closed, an edge inside it, or its edges in doubt)", "explain.gap.narrow", outline);
                        o.Graphics.Add(new Graphic(SliceMark(p.Box, g.Samples, miss), GraphicKind.Fail));
                        return o;
                    }
                    o.Ok = true;
                    (o.MinW, o.MaxW, o.MeanW) = (r.MinPx, r.MaxPx, r.MeanPx);
                    o.Note = $"width {r.MinPx:0.###} to {r.MaxPx:0.###} px, mean {r.MeanPx:0.###} px over {r.Found} slices";
                    o.Graphics.Add(new Graphic(outline, GraphicKind.Info));
                    o.MinMark = SliceMark(p.Box, g.Samples, r.MinIndex);
                    o.MaxMark = SliceMark(p.Box, g.Samples, r.MaxIndex);
                    o.Graphics.Add(new Graphic(o.MinMark, GraphicKind.Info, "min"));
                    o.Graphics.Add(new Graphic(o.MaxMark, GraphicKind.Info, "max"));
                    return o;
                }
                case PitchFeature k:
                {
                    MeasureNative.DefaultPitch(out var p);
                    p.Box = MapBox(k.Box, Map, turn);
                    p.Polarity = Pol(f.Polarity);
                    p.Threshold = f.Threshold;
                    p.FilterHalfWidth = f.FilterHalfWidth;
                    p.MinWidth = k.MinWidth;
                    int cap = Math.Max(4 * k.ExpectedCount, 16);
                    var pins = new MeasureNative.Pin[cap];
                    int rc;
                    var r = MeasureNative.Result<MeasureNative.PitchResult>();
                    fixed (MeasureNative.Pin* pp = pins) rc = MeasureNative.Pitch(px, image.Width, image.Height, image.Stride, ref p, pc, pp, cap, ref r);
                    var outline = BoxCorners(p.Box);
                    if (rc == -2) return Fail(o, "the box is off the image", "explain.measure.off_image", outline);
                    if (rc < 0 || !MeasureNative.Matches(r)) return Fail(o, rc < 0 ? "the pin row could not run" : Mismatch, "explain.measure.not_measured", outline);
                    int n = Math.Min(r.Count, cap);
                    for (int i = 0; i < n; i++)
                    {
                        o.PinPos.Add(pins[i].Pos);
                        if (i > 0) o.Pitches.Add(pins[i].Pos - pins[i - 1].Pos);
                        o.Graphics.Add(new Graphic(Cross(pins[i].X, pins[i].Y), GraphicKind.Info, (i + 1).ToString(CultureInfo.InvariantCulture)));
                    }
                    o.Count = r.Count;
                    o.MeanPitch = r.MeanPitchPx;
                    if (r.Count != k.ExpectedCount)
                    {
                        // a pitch half as long again as the row's usual one: the pin after it is missing
                        double typical = o.Pitches.Count > 0 ? o.Pitches.OrderBy(x => x).ElementAt(o.Pitches.Count / 2) : 0;
                        int gap = r.Count < k.ExpectedCount && typical > 0 ? o.Pitches.FindIndex(x => x > 1.5 * typical) : -1;
                        return gap >= 0
                            ? Fail(o, $"pin {gap + 2} is missing (found {r.Count} pins, expected {k.ExpectedCount})", "explain.pitch.missing", outline)
                            : Fail(o, $"found {r.Count} pins, expected {k.ExpectedCount}", "explain.pitch.count", outline);
                    }
                    if (r.Unpaired > 0)
                        return Fail(o, $"{r.Unpaired} edges belong to no pin (a double edge, a pin cut by the box, or dirt)", "explain.pitch.count", outline);
                    // a pin far fainter than the others is a stub or a missing pin's trace, not a pin (doubt is NG)
                    double usual = pins.Take(n).Select(x => (double)x.Contrast).OrderBy(x => x).ElementAt(n / 2);
                    int faint = Array.FindIndex(pins, 0, n, x => x.Contrast < 0.5 * usual);
                    if (faint >= 0)
                    {
                        Fail(o, $"pin {faint + 1} is faint (contrast {pins[faint].Contrast:0}, the others {usual:0}): a stub or a missing pin", "explain.pitch.missing", outline);
                        o.Graphics.Add(new Graphic(Cross(pins[faint].X, pins[faint].Y), GraphicKind.Fail));
                        return o;
                    }
                    o.Ok = true;
                    o.Note = $"{r.Count} pins, pitch {r.MinPitchPx:0.###} to {r.MaxPitchPx:0.###} px, mean {r.MeanPitchPx:0.###} px";
                    o.Graphics.Add(new Graphic(outline, GraphicKind.Info));
                    return o;
                }
                case EdgeCountFeature k:
                {
                    MeasureNative.DefaultEdges(out var p);
                    p.Box = MapBox(k.Box, Map, turn);
                    p.Polarity = Pol(f.Polarity);
                    p.Threshold = f.Threshold;
                    p.FilterHalfWidth = f.FilterHalfWidth;
                    int cap = Math.Max(4 * k.ExpectedCount, (int)k.Box.Length + 2);
                    var edges = new MeasureNative.Edge[cap];
                    int rc;
                    var r = MeasureNative.Result<MeasureNative.EdgesResult>();
                    fixed (MeasureNative.Edge* pe = edges) rc = MeasureNative.Edges(px, image.Width, image.Height, image.Stride, ref p, pc, pe, cap, ref r);
                    var outline = BoxCorners(p.Box);
                    if (rc == -2) return Fail(o, "the box is off the image", "explain.measure.off_image", outline);
                    if (rc < 0 || !MeasureNative.Matches(r)) return Fail(o, rc < 0 ? "the edge count could not run" : Mismatch, "explain.measure.not_measured", outline);
                    int n = Math.Min(r.Count, cap);
                    double lx = 0, ly = 0;
                    for (int i = 0; i < n; i++)
                    {
                        MeasureNative.ToMeasure(pc, edges[i].X, edges[i].Y, out float mx, out float my);
                        if (i > 0) o.Pitches.Add(Math.Sqrt((mx - lx) * (mx - lx) + (my - ly) * (my - ly)));
                        (lx, ly) = (mx, my);
                        o.Graphics.Add(new Graphic(EdgeMark(p.Box, edges[i]), GraphicKind.Info));
                    }
                    o.Count = r.Count;
                    o.MeanPitch = r.MeanSpacingPx;
                    if (k.ExpectedCount > 0 && r.Count != k.ExpectedCount)
                        return Fail(o, $"counted {r.Count} edges, expected {k.ExpectedCount}", "explain.edges.count", outline);
                    o.Ok = true;
                    o.Note = $"{r.Count} edges, spacing {r.MinSpacingPx:0.###} to {r.MaxSpacingPx:0.###} px, mean {r.MeanSpacingPx:0.###} px" +
                             (r.PeriodPx > 0 ? $", repeat {r.PeriodPx:0.###} px" : "");
                    o.Graphics.Add(new Graphic(outline, GraphicKind.Info));
                    return o;
                }
                default:
                    return Fail(o, $"unknown feature {f.GetType().Name}", "explain.measure.not_measured", Array.Empty<PointF>());
            }
        }
    }

    const string Mismatch = "the measure library does not match this program (reinstall)";

    MeasureNative.CircleParams RingParams(RingFeature c, PointF centre, float turn)
    {
        MeasureNative.DefaultCircle(out var p);
        p.Cx = centre.X; p.Cy = centre.Y;
        p.RInner = c.RInner; p.ROuter = c.ROuter;
        p.StartDeg = c.StartDeg + turn; p.SpanDeg = c.SpanDeg;
        p.Calipers = c.Calipers;
        p.Inward = c.Inward ? 1 : 0;
        p.OutlierPx = c.OutlierPx;
        p.Fit = c.Fit == FitMethod.Ransac ? 1 : 0;
        p.Caliper = CaliperParams(c);
        return p;
    }

    MeasureNative.LineParams CornerLine(MeasureNative.LineParams p, CornerFeature k, Segment s, Func<float, float, PointF> map)
    {
        var a = map(s.X0, s.Y0);
        var b = map(s.X1, s.Y1);
        p.X0 = a.X; p.Y0 = a.Y; p.X1 = b.X; p.Y1 = b.Y;
        p.Calipers = k.Calipers;
        p.SearchWidth = k.SearchWidth;
        p.OutlierPx = k.OutlierPx;
        p.Fit = k.Fit == FitMethod.Ransac ? 1 : 0;
        p.Caliper = CaliperParams(k);
        return p;
    }

    static MeasureNative.Box MapBox(RotBox b, Func<float, float, PointF> map, float turn)
    {
        var c = map(b.Cx, b.Cy);
        return new MeasureNative.Box { Cx = c.X, Cy = c.Y, Length = b.Length, Height = b.Height, AngleDeg = b.AngleDeg + turn };
    }

    // a construction (spec 2.6) from the features found before it, in measure space
    Found Construct(ConstructFeature k, Dictionary<string, Found> found)
    {
        var o = new Found();
        var ins = k.Inputs.Select(n => found.GetValueOrDefault(n)).ToList();
        if (ins.Any(x => x is not { Ok: true }))
            return Fail(o, $"not made, {k.Inputs[ins.FindIndex(x => x is not { Ok: true })]} not found", "explain.measure.not_measured", Array.Empty<PointF>());
        switch (k.Operation)
        {
            case ConstructOp.Intersection:
            {
                Found a = ins[0]!, b = ins[1]!;
                double dax = a.Mx1 - a.Mx0, day = a.My1 - a.My0, dbx = b.Mx1 - b.Mx0, dby = b.My1 - b.My0;
                double cr = dax * dby - day * dbx, la = Math.Sqrt(dax * dax + day * day), lb = Math.Sqrt(dbx * dbx + dby * dby);
                if (la < 1e-9 || lb < 1e-9 || Math.Abs(cr) / (la * lb) < Math.Sin(0.5 * Math.PI / 180))
                    return Fail(o, "the two lines are parallel, so they do not meet", "explain.geometry.parallel", Array.Empty<PointF>());
                double s = ((b.Mx0 - a.Mx0) * dby - (b.My0 - a.My0) * dbx) / cr;
                (o.Mx, o.My) = (a.Mx0 + s * dax, a.My0 + s * day);
                break;
            }
            case ConstructOp.Perpendicular:
            {
                Found q = ins[0]!, l = ins[1]!;
                double dx = l.Mx1 - l.Mx0, dy = l.My1 - l.My0, len2 = dx * dx + dy * dy;
                if (len2 < 1e-18) return Fail(o, $"{k.Inputs[1]} has no length", "explain.measure.not_measured", Array.Empty<PointF>());
                double t = ((q.Mx - l.Mx0) * dx + (q.My - l.My0) * dy) / len2;
                (o.Mx, o.My) = (l.Mx0 + t * dx, l.My0 + t * dy);
                o.Graphics.Add(new Graphic(new[] { ToImage(q.Mx, q.My), ToImage(o.Mx, o.My) }, GraphicKind.Info));
                break;
            }
            case ConstructOp.Midpoint:
                (o.Mx, o.My) = ((ins[0]!.Mx + ins[1]!.Mx) / 2, (ins[0]!.My + ins[1]!.My) / 2);
                break;
            case ConstructOp.LineThrough:
            {
                Found a = ins[0]!, b = ins[1]!;
                if (Math.Abs(a.Mx - b.Mx) + Math.Abs(a.My - b.My) < 1e-6)
                    return Fail(o, "the two points are the same, so no line passes through them alone", "explain.measure.not_measured", Array.Empty<PointF>());
                o.Ok = true;
                o.IsLine = true;
                (o.Mx0, o.My0, o.Mx1, o.My1) = (a.Mx, a.My, b.Mx, b.My);
                o.AngleDeg = Math.Atan2(-(b.My - a.My), b.Mx - a.Mx) * 180 / Math.PI;
                o.Note = $"line at {o.AngleDeg:0.###}°";
                o.Graphics.Add(new Graphic(new[] { ToImage(a.Mx, a.My), ToImage(b.Mx, b.My) }, GraphicKind.Ok));
                return o;
            }
            case ConstructOp.CircleThrough:
            {
                Found p = ins[0]!, q = ins[1]!, r = ins[2]!;
                double d = 2 * (p.Mx * (q.My - r.My) + q.Mx * (r.My - p.My) + r.Mx * (p.My - q.My));
                double scaleSq = Math.Max(1e-9, Math.Pow(Math.Max(Math.Abs(q.Mx - p.Mx) + Math.Abs(q.My - p.My), Math.Abs(r.Mx - p.Mx) + Math.Abs(r.My - p.My)), 2));
                if (Math.Abs(d) / scaleSq < 1e-6)
                    return Fail(o, "the three points are in a line, so no circle passes through them", "explain.measure.not_measured", Array.Empty<PointF>());
                double p2 = p.Mx * p.Mx + p.My * p.My, q2 = q.Mx * q.Mx + q.My * q.My, r2 = r.Mx * r.Mx + r.My * r.My;
                o.Mx = (p2 * (q.My - r.My) + q2 * (r.My - p.My) + r2 * (p.My - q.My)) / d;
                o.My = (p2 * (r.Mx - q.Mx) + q2 * (p.Mx - r.Mx) + r2 * (q.Mx - p.Mx)) / d;
                o.RadiusPx = Math.Sqrt((p.Mx - o.Mx) * (p.Mx - o.Mx) + (p.My - o.My) * (p.My - o.My));
                var c = ToImage(o.Mx, o.My);
                o.Graphics.Add(new Graphic(Ring(c.X, c.Y, (float)o.RadiusPx), GraphicKind.Ok));
                break;
            }
        }
        o.Ok = true;
        o.IsPoint = true;
        var at = ToImage(o.Mx, o.My);
        o.Note = $"point at ({at.X:0.##}, {at.Y:0.##})" + (k.Operation == ConstructOp.CircleThrough ? $", diameter {2 * o.RadiusPx:0.###} px" : "");
        o.Graphics.Add(new Graphic(Cross(at.X, at.Y), GraphicKind.Ok));
        return o;
    }

    // a measure-space point in image pixels, for drawing
    unsafe PointF ToImage(double mx, double my)
    {
        var cal = _calib;
        MeasureNative.ToImage(_hasCalib ? &cal : null, (float)mx, (float)my, out float x, out float y);
        return new PointF(x, y);
    }

    // position along the box from its start, in measure space (the same as image px without a lens model)
    static unsafe double MeasurePosition(MeasureNative.Box box, MeasureNative.CaliperResult r, MeasureNative.Calib* pc)
    {
        double a = box.AngleDeg * Math.PI / 180, ux = Math.Cos(a), uy = -Math.Sin(a);
        float sx = (float)(box.Cx - ux * box.Length / 2), sy = (float)(box.Cy - uy * box.Length / 2);
        MeasureNative.ToMeasure(pc, sx, sy, out float mx, out float my);
        double px = (r.Mx0 + (r.WidthPx > 0 ? r.Mx1 : r.Mx0)) / 2, py = (r.My0 + (r.WidthPx > 0 ? r.My1 : r.My0)) / 2;
        return Math.Sqrt((px - mx) * (px - mx) + (py - my) * (py - my));
    }

    static Found Fail(Found o, string problem, string explain, PointF[] outline)
    {
        o.Ok = false;
        o.Problem = problem;
        o.Explain = explain;
        if (outline.Length > 0) o.Graphics.Add(new Graphic(outline, GraphicKind.Fail));
        return o;
    }

    static void AddPoints(Found o, float[] pts)
    {
        for (int i = 0; i + 1 < pts.Length; i += 2)
            if (float.IsFinite(pts[i]))
                o.Graphics.Add(new Graphic(new[] { new PointF(pts[i] - 1.5f, pts[i + 1]), new PointF(pts[i] + 1.5f, pts[i + 1]) }, GraphicKind.Info));
    }

    static PointF[] BoxCorners(MeasureNative.Box b)
    {
        double a = b.AngleDeg * Math.PI / 180, ux = Math.Cos(a), uy = -Math.Sin(a), vx = Math.Sin(a), vy = Math.Cos(a);
        double hl = b.Length / 2, hh = b.Height / 2;
        PointF P(double s, double t) => new((float)(b.Cx + ux * s + vx * t), (float)(b.Cy + uy * s + vy * t));
        return new[] { P(-hl, -hh), P(hl, -hh), P(hl, hh), P(-hl, hh) };
    }

    // a mark across the box at the edge
    static PointF[] EdgeMark(MeasureNative.Box b, MeasureNative.Edge e)
    {
        double a = b.AngleDeg * Math.PI / 180, vx = Math.Sin(a), vy = Math.Cos(a), hh = b.Height / 2;
        return new[] { new PointF((float)(e.X - vx * hh), (float)(e.Y - vy * hh)), new PointF((float)(e.X + vx * hh), (float)(e.Y + vy * hh)) };
    }

    static PointF[] Cross(float x, float y) =>
        new[] { new PointF(x - 3, y), new PointF(x + 3, y), new PointF(x, y), new PointF(x, y - 3), new PointF(x, y + 3) };

    // the middle line of slice i of n across the box (along its length)
    static PointF[] SliceMark(MeasureNative.Box b, int n, int i)
    {
        double a = b.AngleDeg * Math.PI / 180, ux = Math.Cos(a), uy = -Math.Sin(a), vx = Math.Sin(a), vy = Math.Cos(a);
        double off = -b.Height / 2 + b.Height / n * (i + 0.5), hl = b.Length / 2;
        return new[] { new PointF((float)(b.Cx - ux * hl + vx * off), (float)(b.Cy - uy * hl + vy * off)),
                       new PointF((float)(b.Cx + ux * hl + vx * off), (float)(b.Cy + uy * hl + vy * off)) };
    }

    // an ellipse: semi-axes along deg (counter-clockwise on the screen) and across it
    static PointF[] Oval(float cx, float cy, float a, float b, float deg)
    {
        var p = new PointF[36];
        double t0 = deg * Math.PI / 180, ux = Math.Cos(t0), uy = -Math.Sin(t0), vx = Math.Sin(t0), vy = Math.Cos(t0);
        for (int i = 0; i < p.Length; i++)
        {
            double t = 2 * Math.PI * i / p.Length, s = a * Math.Cos(t), q = b * Math.Sin(t);
            p[i] = new PointF((float)(cx + ux * s + vx * q), (float)(cy + uy * s + vy * q));
        }
        return p;
    }

    static PointF[] Ring(float cx, float cy, float r)
    {
        var p = new PointF[36];
        for (int i = 0; i < p.Length; i++)
        {
            double a = 2 * Math.PI * i / p.Length;
            p[i] = new PointF((float)(cx + r * Math.Cos(a)), (float)(cy - r * Math.Sin(a)));
        }
        return p;
    }

    public void Dispose() { }
}
