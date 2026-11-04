using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace VisionCore;

// P/Invoke declarations matching engine/include/vs_locate.h.
internal static class LocateNative
{
    private const string Lib = "vs_locate";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Params
    {
        public int StructSize;
        public int SearchX, SearchY, SearchW, SearchH;
        public float MaxAngleDeg;
        public float MinScore;
        public int TimeBudgetMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        public int Found;
        public float Score;
        public float Cx, Cy;
        public float AngleDeg;
        public float Dx, Dy;
        public float TimeMs;
    }

    [DllImport(Lib, EntryPoint = "vs_loc_default_params")] internal static extern void DefaultParams(out Params p);
    [DllImport(Lib, EntryPoint = "vs_loc_create")] internal static extern IntPtr Create(ref Params p);
    [DllImport(Lib, EntryPoint = "vs_loc_destroy")] internal static extern void Destroy(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_loc_teach")]
    internal static extern unsafe int Teach(IntPtr t, byte* px, int w, int h, int stride, int x, int y, int rw, int rh);
    [DllImport(Lib, EntryPoint = "vs_loc_set_pattern")]
    internal static extern unsafe int SetPattern(IntPtr t, byte* px, int w, int h, int stride, float ox, float oy);
    [DllImport(Lib, EntryPoint = "vs_loc_get_pattern")]
    internal static extern unsafe int GetPattern(IntPtr t, byte* buf, int cap, out int w, out int h, out float ox, out float oy);
    [DllImport(Lib, EntryPoint = "vs_loc_run")] internal static extern unsafe int Run(IntPtr t, byte* px, int w, int h, int stride);
    [DllImport(Lib, EntryPoint = "vs_loc_get_result")] internal static extern void GetResult(IntPtr t, out Result r);
    [DllImport(Lib, EntryPoint = "vs_loc_last_error")] private static extern IntPtr LastErrorPtr(IntPtr t);
    internal static string LastError(IntPtr t) => Marshal.PtrToStringUTF8(LastErrorPtr(t)) ?? "";
}

/// <summary>Edge gradient polarity matching mode for geometric shape models.</summary>
public enum LocateMetric
{
    /// <summary>Strict edge polarity: dark-to-light transitions must match.</summary>
    UsePolarity = 0,
    /// <summary>Inverted contrast tolerance: accepts inverted global print (white on black).</summary>
    IgnoreGlobalPolarity = 1,
    /// <summary>Local polarity invariant: accepts local specular or foil gradient reversals.</summary>
    IgnoreLocalPolarity = 2
}

/// <summary>Pattern search and matching algorithm family.</summary>
public enum LocateSearchMode
{
    /// <summary>Edge-based geometric shape matching (PatMax / shape-model standard).</summary>
    EdgeGeometric = 0,
    /// <summary>Normalized cross-correlation over image pyramid.</summary>
    Correlation = 1
}

/// <summary>Detailed diagnostic evaluation of a taught pattern's suitability as a geometric shape model.</summary>
public sealed record PatternQuality(
    double Score,
    int EdgeCount,
    double OrientationEntropy,
    double ContrastStdDev,
    string Rating,
    string[] Warnings)
{
    public bool IsSufficient => Score >= 50.0 && !Rating.Equals("Degenerate", StringComparison.OrdinalIgnoreCase);

    /// <summary>Evaluates sharpness, contrast, and edge directional entropy of an image region.</summary>
    public static PatternQuality Evaluate(GrayImage image, RoiBox roi)
    {
        var r = new RoiBox(
            Math.Max(0, roi.X),
            Math.Max(0, roi.Y),
            Math.Min(roi.Width, image.Width - Math.Max(0, roi.X)),
            Math.Min(roi.Height, image.Height - Math.Max(0, roi.Y)));

        if (r.Width < 16 || r.Height < 16)
        {
            return new PatternQuality(
                0.0, 0, 0.0, 0.0, "Degenerate",
                ["Pattern region is too small (minimum 16×16 pixels required)."]);
        }

        var px = image.Pixels;
        int stride = image.Stride;
        double sum = 0, sumSq = 0;
        int totalPx = r.Width * r.Height;

        for (int y = r.Y; y < r.Y + r.Height; y++)
        {
            int row = y * stride;
            for (int x = r.X; x < r.X + r.Width; x++)
            {
                byte val = px[row + x];
                sum += val;
                sumSq += (double)val * val;
            }
        }

        double mean = sum / totalPx;
        double variance = Math.Max(0, (sumSq / totalPx) - (mean * mean));
        double stdDev = Math.Sqrt(variance);

        // Compute 3x3 Sobel gradient directions and magnitude
        int edgeThreshold = (int)Math.Max(12.0, stdDev * 0.4);
        int[] angleBins = new int[8];
        int edgeCount = 0;

        for (int y = r.Y + 1; y < r.Y + r.Height - 1; y++)
        {
            int rPrev = (y - 1) * stride;
            int rCurr = y * stride;
            int rNext = (y + 1) * stride;

            for (int x = r.X + 1; x < r.X + r.Width - 1; x++)
            {
                int gx = (px[rPrev + x + 1] + 2 * px[rCurr + x + 1] + px[rNext + x + 1])
                       - (px[rPrev + x - 1] + 2 * px[rCurr + x - 1] + px[rNext + x - 1]);
                int gy = (px[rNext + x - 1] + 2 * px[rNext + x] + px[rNext + x + 1])
                       - (px[rPrev + x - 1] + 2 * px[rPrev + x] + px[rPrev + x + 1]);

                double mag = Math.Sqrt((double)gx * gx + (double)gy * gy);
                if (mag >= edgeThreshold)
                {
                    edgeCount++;
                    double angle = Math.Atan2(gy, gx); // -PI to +PI
                    if (angle < 0) angle += 2 * Math.PI;
                    int bin = (int)Math.Floor(angle / (Math.PI / 4.0)) % 8;
                    angleBins[bin]++;
                }
            }
        }

        // Calculate Shannon entropy over 8 orientation bins
        double entropy = 0.0;
        if (edgeCount > 0)
        {
            for (int i = 0; i < 8; i++)
            {
                if (angleBins[i] > 0)
                {
                    double p = (double)angleBins[i] / edgeCount;
                    entropy -= p * Math.Log2(p);
                }
            }
        }
        // Normalize entropy to 0..1 (log2(8) = 3.0)
        double normEntropy = Math.Clamp(entropy / 3.0, 0.0, 1.0);

        var warnings = new List<string>();
        if (stdDev < 8.0)
        {
            warnings.Add("Low contrast: pattern region has insufficient dynamic range (< 8.0 std dev).");
        }
        if (edgeCount < 16)
        {
            warnings.Add("Few distinct edges: pattern lacks prominent features for robust alignment.");
        }
        else if (normEntropy < 0.45)
        {
            warnings.Add("Low directional diversity: pattern edges are mostly parallel and may slip along their axis.");
        }

        double contrastScore = Math.Clamp((stdDev / 40.0) * 100.0, 0.0, 100.0);
        double entropyScore = normEntropy * 100.0;
        double densityScore = Math.Clamp(((double)edgeCount / (totalPx * 0.15)) * 100.0, 0.0, 100.0);

        double compositeScore = (contrastScore * 0.35) + (entropyScore * 0.40) + (densityScore * 0.25);
        compositeScore = Math.Clamp(compositeScore, 0.0, 100.0);

        string rating = compositeScore switch
        {
            >= 85.0 => "Optimal",
            >= 70.0 => "Good",
            >= 50.0 => "Fair",
            _ => "Degenerate"
        };

        return new PatternQuality(
            Math.Round(compositeScore, 1),
            edgeCount,
            Math.Round(normEntropy, 3),
            Math.Round(stdDev, 1),
            rating,
            warnings.ToArray());
    }
}

/// <summary>
/// Where the part is in this image against the teach image: a point (x, y) taught is at
/// <see cref="Map"/>(x, y) now. Tools of the same camera move and turn their areas with it.
/// </summary>
public sealed record Fixture(float OriginX, float OriginY, float Cx, float Cy, float AngleDeg)
{
    public static readonly Fixture None = new(0, 0, 0, 0, 0);

    /// <summary>Transforms a point from teach-coordinates to current image coordinates.</summary>
    public PointF Map(float x, float y)
    {
        double a = AngleDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double px = x - OriginX, py = y - OriginY;
        return new PointF((float)(Cx + c * px + s * py), (float)(Cy - s * px + c * py));
    }

    /// <summary>Transforms a point from current image coordinates back to teach-coordinates.</summary>
    public PointF InverseMap(float imgX, float imgY)
    {
        double a = AngleDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double u = imgX - Cx, v = imgY - Cy;
        return new PointF((float)(OriginX + c * u - s * v), (float)(OriginY + s * u + c * v));
    }

    /// <summary>The taught area moved with the part: the upright box round its four turned corners.</summary>
    public RoiBox Map(RoiBox r, int imageWidth, int imageHeight)
    {
        if (this == None) return r;
        var pts = new[] { Map(r.X, r.Y), Map(r.X + r.Width, r.Y), Map(r.X + r.Width, r.Y + r.Height), Map(r.X, r.Y + r.Height) };
        int x0 = (int)Math.Floor(Math.Max(0, pts.Min(p => p.X))), y0 = (int)Math.Floor(Math.Max(0, pts.Min(p => p.Y)));
        int x1 = (int)Math.Ceiling(Math.Min(imageWidth, pts.Max(p => p.X))), y1 = (int)Math.Ceiling(Math.Min(imageHeight, pts.Max(p => p.Y)));
        return new RoiBox(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }
}

/// <summary>Find the part: a taught pattern (Roi in the teach image) found again in each image.</summary>
public sealed class LocateToolConfig : ToolConfig
{
    /// <summary>Where to look; null = the whole image.</summary>
    public RoiBox? SearchArea { get; set; }
    public float MaxAngleDeg { get; set; } = 15;
    /// <summary>0..1: lowest match accepted as found.</summary>
    public float MinScore { get; set; } = 0.6f;
    /// <summary>Edge gradient polarity metric.</summary>
    public LocateMetric Metric { get; set; } = LocateMetric.UsePolarity;
    /// <summary>Locate algorithm search mode.</summary>
    public LocateSearchMode SearchMode { get; set; } = LocateSearchMode.EdgeGeometric;
    /// <summary>Number of pyramid levels for coarse-to-fine hierarchical search.</summary>
    public int PyramidLevels { get; set; } = 3;
    /// <summary>Minimum isotropic scale factor searched (nominal 1.0).</summary>
    public float MinScale { get; set; } = 0.95f;
    /// <summary>Maximum isotropic scale factor searched (nominal 1.0).</summary>
    public float MaxScale { get; set; } = 1.05f;
    /// <summary>Diagnostic pattern quality evaluation calculated at teach time.</summary>
    public PatternQuality? Quality { get; set; }
    /// <summary>The pattern image file, next to the job (jobs/assets/&lt;job&gt;/...).</summary>
    public string? PatternFile { get; set; }
    public float OriginX { get; set; }
    public float OriginY { get; set; }

    /// <summary>The pattern pixels (8-bit grey, row by row) while teaching and after loading; the file holds them on disk.</summary>
    [JsonIgnore] public byte[]? Pattern { get; set; }
    [JsonIgnore] public int PatternWidth { get; set; }
    [JsonIgnore] public int PatternHeight { get; set; }

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();

    /// <summary>Cuts the pattern from the teach image at <see cref="ToolConfig.Roi"/> and evaluates quality.</summary>
    public unsafe void Teach(GrayImage image)
    {
        if (Roi is not { } r) throw new InvalidOperationException("Mark the area with the pattern first");
        Quality = PatternQuality.Evaluate(image, r);
        using var t = new LocateTool(this, require: false);
        t.TeachFrom(image, r);
    }
}

public sealed class LocateTool : ITool
{
    private readonly LocateToolConfig _cfg;
    private IntPtr _h;

    public LocateTool(LocateToolConfig cfg, bool require = true)
    {
        _cfg = cfg;
        LocateNative.DefaultParams(out var p);
        if (cfg.SearchArea is { } s) { p.SearchX = s.X; p.SearchY = s.Y; p.SearchW = s.Width; p.SearchH = s.Height; }
        p.MaxAngleDeg = cfg.MaxAngleDeg;
        p.MinScore = cfg.MinScore;
        _h = LocateNative.Create(ref p);
        if (_h == IntPtr.Zero) throw new InvalidOperationException("vs_loc_create failed");
        if (cfg.Pattern is { } px && cfg.PatternWidth > 0)
        {
            unsafe
            {
                fixed (byte* b = px)
                    if (LocateNative.SetPattern(_h, b, cfg.PatternWidth, cfg.PatternHeight, cfg.PatternWidth, cfg.OriginX, cfg.OriginY) != 0)
                        throw new InvalidOperationException(LocateNative.LastError(_h));
            }
        }
        else if (require)
        {
            Dispose();
            throw new InvalidOperationException($"{cfg.Name}: teach the pattern first (Auto-find)");
        }
    }

    public ToolConfig Config => _cfg;

    internal unsafe void TeachFrom(GrayImage image, RoiBox r)
    {
        fixed (byte* p = image.Pixels)
            if (LocateNative.Teach(_h, p, image.Width, image.Height, image.Stride, r.X, r.Y, r.Width, r.Height) != 0)
                throw new InvalidOperationException(LocateNative.LastError(_h));
        int need = LocateNative.GetPattern(_h, null, 0, out int w, out int h, out float ox, out float oy);
        var buf = new byte[need];
        fixed (byte* b = buf) LocateNative.GetPattern(_h, b, need, out w, out h, out ox, out oy);
        _cfg.Pattern = buf;
        _cfg.PatternWidth = w;
        _cfg.PatternHeight = h;
        _cfg.OriginX = ox;
        _cfg.OriginY = oy;
    }

    /// <summary>Finds the part; the fixture is null when it was not found.</summary>
    public unsafe (ToolResult Result, Fixture? Fixture) Find(GrayImage image)
    {
        var sw = Stopwatch.StartNew();
        fixed (byte* p = image.Pixels)
            if (LocateNative.Run(_h, p, image.Width, image.Height, image.Stride) != 0)
                throw new InvalidOperationException(LocateNative.LastError(_h));
        LocateNative.GetResult(_h, out var r);
        var fx = new Fixture(_cfg.OriginX, _cfg.OriginY, r.Cx, r.Cy, r.AngleDeg);
        var graphics = new List<Graphic>();
        if (_cfg.PatternWidth > 0)
        {
            float hw = _cfg.PatternWidth / 2f, hh = _cfg.PatternHeight / 2f, ox = _cfg.OriginX, oy = _cfg.OriginY;
            graphics.Add(new Graphic(new[] { fx.Map(ox - hw, oy - hh), fx.Map(ox + hw, oy - hh), fx.Map(ox + hw, oy + hh), fx.Map(ox - hw, oy + hh) },
                r.Found != 0 ? GraphicKind.Info : GraphicKind.Fail, $"{r.Score:0.00}"));
        }
        bool found = r.Found != 0;
        var details = new[] { $"match {r.Score:0.00}, moved {r.Dx:0} / {r.Dy:0} px, turned {r.AngleDeg:0.0}°" };
        var res = new ToolResult(_cfg.Name, found, found ? $"Part found ({r.Score:0.00})" : $"Part not found (match {r.Score:0.00})",
            details, sw.Elapsed.TotalMilliseconds, graphics) { Explain = found ? null : "explain.locate.not_found", MatchScore = r.Score };
        return (res, found ? fx : null);
    }

    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null) => Find(image).Result;

    public void Dispose()
    {
        if (_h != IntPtr.Zero) LocateNative.Destroy(_h);
        _h = IntPtr.Zero;
    }
}
