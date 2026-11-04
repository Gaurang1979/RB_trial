using BarcodeReading;

namespace VisionCore.Reports;

/// <summary>
/// The figures of a grade report, measured from the image the way a verifier shows them (the grades
/// themselves are the engine's): each cell of a 2D symbol sampled at its centre through the engine's
/// aperture, its modulation 2|R - GT| / SC graded on ISO/IEC 15415's bands (the grade map and the
/// contrast uniformity), the histograms of the symbol's pixels and of its cell centres, and the scan
/// reflectance profile across a 1D symbol.
/// </summary>
public static class SymbolAnalysis
{
    /// <summary>A 2D symbol's cells: their reflectance (0..100), the global threshold and symbol contrast of the
    /// cell centres, and each cell's modulation and grade (4 = A .. 0 = F).</summary>
    public sealed record CellMap(int Rows, int Cols, float[] Reflectance, float Threshold, float Contrast, float[] Modulation, int[] Grade)
    {
        /// <summary>The lowest cell modulation (verifiers report the lowest codeword's: this is per cell, informative).</summary>
        public float MinModulation => Modulation.Length == 0 ? 0 : Modulation.Min();
    }

    private static readonly int[] DmSquare = { 10, 12, 14, 16, 18, 20, 22, 24, 26, 32, 36, 40, 44, 48, 52, 64, 72, 80, 88, 96, 104, 120, 132, 144 };
    private static readonly (int R, int C)[] DmRect = { (8, 18), (8, 32), (12, 26), (12, 36), (16, 36), (16, 48) };
    // rectangular extension (DMRE, ISO/IEC 21471), allowed by GS1
    private static readonly (int R, int C)[] DmRe =
    {
        (8, 48), (8, 64), (8, 80), (8, 96), (8, 120), (8, 144), (12, 64), (12, 88), (16, 64), (20, 36), (20, 44), (20, 64),
        (22, 48), (24, 48), (24, 64), (26, 40), (26, 48), (26, 64),
    };

    /// <summary>
    /// Rows and columns of a 2D symbol from its corners (symbol order: top-left, top-right, bottom-right,
    /// bottom-left) and module size: the nearest size the symbology has. (0, 0) for types without a grid here.
    /// The engine's own grid (CodeQuality.GridRows / GridCols) comes first when it gives one.
    /// </summary>
    public static (int Rows, int Cols) GridSize(CodeFormat format, PointF[] c, float modulePx)
    {
        if (c.Length != 4 || modulePx <= 0) return (0, 0);
        float w = 0.5f * (Dist(c[0], c[1]) + Dist(c[3], c[2])) / modulePx, h = 0.5f * (Dist(c[0], c[3]) + Dist(c[1], c[2])) / modulePx;
        if (format == CodeFormat.DataMatrix)
        {
            // a size the symbology has, within half a module on both axes; otherwise not estimated
            var all = DmSquare.Select(n => (R: n, C: n)).Concat(DmRect).Concat(DmRe).SelectMany(r => new[] { r, (R: r.C, C: r.R) });
            var best = all.MinBy(s => Math.Abs(s.R - h) + Math.Abs(s.C - w));
            return Math.Abs(best.R - h) <= 0.5f && Math.Abs(best.C - w) <= 0.5f ? best : (0, 0);
        }
        if (format == CodeFormat.QrCode)
        {
            int n = 21 + 4 * Math.Clamp((int)Math.Round((0.5 * (w + h) - 21) / 4), 0, 39);
            return Math.Abs(n - w) <= 0.5f && Math.Abs(n - h) <= 0.5f ? (n, n) : (0, 0);
        }
        if (format == CodeFormat.Aztec)
        {
            // ISO/IEC 24778: compact 15, 19, 23, 27; full-range 19 + 4 per layer and the reference grid lines
            // (one more line each side every 16 modules from the centre)
            var sizes = new[] { 15, 19, 23, 27 }.Concat(Enumerable.Range(1, 32).Select(l => 14 + 4 * l).Select(b => b + 1 + 2 * ((b / 2 - 1) / 15)));
            int n = sizes.MinBy(s => Math.Abs(s - 0.5f * (w + h)));
            return Math.Abs(n - w) <= 0.5f && Math.Abs(n - h) <= 0.5f ? (n, n) : (0, 0);
        }
        if (format == CodeFormat.MicroQr)
        {
            int n = 11 + 2 * Math.Clamp((int)Math.Round((0.5 * (w + h) - 11) / 2), 0, 3);
            return Math.Abs(n - w) <= 0.5f && Math.Abs(n - h) <= 0.5f ? (n, n) : (0, 0);
        }
        return (0, 0);
    }

    /// <summary>Samples every cell centre (bilinear over the corners) through a disc of the given radius.</summary>
    public static CellMap Cells(GrayImage img, PointF[] c, int rows, int cols, float radiusPx)
    {
        var refl = new float[rows * cols];
        for (int r = 0; r < rows; r++)
            for (int k = 0; k < cols; k++) refl[r * cols + k] = CellAt(img, c, rows, cols, r, k, radiusPx);
        // as the engine: dark and light levels at the 10th and 90th percentiles (one glare or ink spot does not set them)
        var sorted = refl.OrderBy(v => v).ToArray();
        float lo = sorted.Length > 0 ? sorted[sorted.Length / 10] : 0, hi = sorted.Length > 0 ? sorted[sorted.Length * 9 / 10] : 0;
        float gt = 0.5f * (lo + hi), sc = hi - lo;
        var mod = refl.Select(x => sc > 0 ? Math.Clamp(2 * Math.Abs(x - gt) / sc, 0f, 1f) : 0f).ToArray();
        return new CellMap(rows, cols, refl, gt, sc, mod, mod.Select(ModulationGrade).ToArray());
    }

    /// <summary>The reflectance (0..100) at cell (row, col); rows and columns outside the symbol (-1, rows, cols)
    /// fall in the quiet zone.</summary>
    public static float CellAt(GrayImage img, PointF[] c, int rows, int cols, int r, int k, float radiusPx)
    {
        var (x, y) = Map(c, (k + 0.5f) / cols, (r + 0.5f) / rows);
        return Disc(img, x, y, radiusPx) / 255f * 100f;
    }

    /// <summary>The unit square mapped onto the corners by a homography (as the engine places cells under perspective).</summary>
    public static (float X, float Y) Map(PointF[] q, float u, float v)
    {
        double x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
        double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3, dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
        double a, b, d, e, g = 0, h = 0;
        double det = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(dx3) < 1e-9 && Math.Abs(dy3) < 1e-9 || Math.Abs(det) < 1e-12)
        {
            a = x1 - x0; b = x3 - x0; d = y1 - y0; e = y3 - y0; // parallelogram: affine
        }
        else
        {
            g = (dx3 * dy2 - dx2 * dy3) / det;
            h = (dx1 * dy3 - dx3 * dy1) / det;
            a = x1 - x0 + g * x1; b = x3 - x0 + h * x3; d = y1 - y0 + g * y1; e = y3 - y0 + h * y3;
        }
        double w = g * u + h * v + 1;
        return ((float)((a * u + b * v + x0) / w), (float)((d * u + e * v + y0) / w));
    }

    /// <summary>Data Matrix ECC 200 by size (rows x cols): its data region, data and error-correction codewords.</summary>
    public static (int DataRows, int DataCols, int DataCw, int EcCw)? Ecc200(int rows, int cols)
    {
        var square = new Dictionary<int, (int D, int E)>
        {
            [10] = (3, 5), [12] = (5, 7), [14] = (8, 10), [16] = (12, 12), [18] = (18, 14), [20] = (22, 18), [22] = (30, 20),
            [24] = (36, 24), [26] = (44, 28), [32] = (62, 36), [36] = (86, 42), [40] = (114, 48), [44] = (144, 56), [48] = (174, 68),
            [52] = (204, 84), [64] = (280, 112), [72] = (368, 144), [80] = (456, 192), [88] = (576, 224), [96] = (696, 272),
            [104] = (816, 336), [120] = (1050, 408), [132] = (1304, 496), [144] = (1558, 620),
        };
        if (rows == cols && square.TryGetValue(rows, out var sq))
        {
            int regions = rows <= 26 ? 1 : rows <= 52 ? 2 : rows <= 104 ? 4 : 6;
            return (rows - 2 * regions, cols - 2 * regions, sq.D, sq.E);
        }
        return (rows, cols) switch
        {
            (8, 18) => (6, 16, 5, 7), (8, 32) => (6, 28, 10, 11), (12, 26) => (10, 24, 16, 14),
            (12, 36) => (10, 32, 22, 18), (16, 36) => (14, 32, 32, 24), (16, 48) => (14, 44, 49, 28),
            _ => null,
        };
    }

    /// <summary>One fixed-pattern segment of a Data Matrix, checked cell by cell against its known pattern.</summary>
    public sealed record Segment(string Name, string Code, int Cells, int Wrong, float LowestModulation, List<(int R, int C)> WrongCells);

    /// <summary>
    /// The Data Matrix fixed patterns as a verifier lists them: the L finder's left and bottom sides (dark), the
    /// clock tracks along the top and right (alternating), and the one-cell quiet zone ring outside each side
    /// (light). Each cell is on the right side of the global threshold or not, with its modulation
    /// 2|R - GT| / SC (0 when on the wrong side). Informative: the engine grades fixed pattern damage.
    /// </summary>
    public static List<Segment> DataMatrixSegments(GrayImage img, PointF[] c, CellMap m, float radiusPx)
    {
        int h = m.Rows, w = m.Cols;
        var segs = new List<Segment>();
        void Add(string name, string code, IEnumerable<(int R, int C, bool Dark)> cells)
        {
            int n = 0, wrong = 0;
            float low = 1f;
            var bad = new List<(int, int)>();
            foreach (var (r, k, dark) in cells)
            {
                float v = r >= 0 && r < h && k >= 0 && k < w ? m.Reflectance[r * w + k] : CellAt(img, c, h, w, r, k, radiusPx);
                bool isDark = v < m.Threshold;
                float mod = m.Contrast > 0 ? Math.Clamp(2 * Math.Abs(v - m.Threshold) / m.Contrast, 0f, 1f) : 0f;
                n++;
                if (isDark != dark) { wrong++; bad.Add((r, k)); mod = 0; }
                low = Math.Min(low, mod);
            }
            segs.Add(new Segment(name, code, n, wrong, n > 0 ? low : 0, bad));
        }
        Add("Left 'L' side", "LLS", Enumerable.Range(0, h).Select(r => (r, 0, true)));
        Add("Bottom 'L' side", "BLS", Enumerable.Range(0, w).Select(k => (h - 1, k, true)));
        Add("Left quiet zone", "LQZ", Enumerable.Range(-1, h + 2).Select(r => (r, -1, false)));
        Add("Bottom quiet zone", "BQZ", Enumerable.Range(-1, w + 2).Select(k => (h, k, false)));
        Add("Top quiet zone", "TQZ", Enumerable.Range(-1, w + 2).Select(k => (-1, k, false)));
        Add("Right quiet zone", "RQZ", Enumerable.Range(-1, h + 2).Select(r => (r, w, false)));
        // clock tracks: the top row dark on even columns; the right column dark on odd rows from the top
        Add("Top clock track", "TCT", Enumerable.Range(0, w).Select(k => (0, k, k % 2 == 0)));
        Add("Right clock track", "RCT", Enumerable.Range(0, h).Select(r => (r, w - 1, (h - 1 - r) % 2 == 0)));
        return segs;
    }

    /// <summary>ISO/IEC 15415 modulation bands: A ≥ 0.50, B ≥ 0.40, C ≥ 0.30, D ≥ 0.20, else F.</summary>
    public static int ModulationGrade(float m) => m >= 0.5f ? 4 : m >= 0.4f ? 3 : m >= 0.3f ? 2 : m >= 0.2f ? 1 : 0;

    /// <summary>The grade map: the symbol's region, each cell tinted by its grade (A green ... F red) over the image.</summary>
    public static (byte[] Rgb, int Width, int Height) GradeMap(CellMap m, int cellPx = 12)
    {
        int w = m.Cols * cellPx, h = m.Rows * cellPx;
        var rgb = new byte[w * h * 3];
        for (int r = 0; r < m.Rows; r++)
            for (int k = 0; k < m.Cols; k++)
            {
                var (cr, cg, cb) = GradeColour(m.Grade[r * m.Cols + k]);
                bool dark = m.Reflectance[r * m.Cols + k] < m.Threshold;
                for (int y = 0; y < cellPx; y++)
                    for (int x = 0; x < cellPx; x++)
                    {
                        // a dark cell is drawn dark in its grade's colour, a light one light; a 1 px grid between cells
                        bool edge = x == 0 || y == 0;
                        float f = edge ? 0.55f : dark ? 0.45f : 1f;
                        int i = ((r * cellPx + y) * w + k * cellPx + x) * 3;
                        rgb[i] = (byte)(255 * (dark ? cr * f : 1 - (1 - cr) * 0.55f * (edge ? 1.4f : 1f)));
                        rgb[i + 1] = (byte)(255 * (dark ? cg * f : 1 - (1 - cg) * 0.55f * (edge ? 1.4f : 1f)));
                        rgb[i + 2] = (byte)(255 * (dark ? cb * f : 1 - (1 - cb) * 0.55f * (edge ? 1.4f : 1f)));
                    }
            }
        return (rgb, w, h);
    }

    /// <summary>A, B, C, D, F as green, light green, yellow, orange, red.</summary>
    public static (float R, float G, float B) GradeColour(int grade) => grade switch
    {
        4 => (0.15f, 0.62f, 0.25f), 3 => (0.55f, 0.78f, 0.25f), 2 => (0.95f, 0.80f, 0.15f), 1 => (0.95f, 0.50f, 0.10f), _ => (0.85f, 0.15f, 0.15f),
    };

    /// <summary>Counts of values 0..100 in `bins` bins.</summary>
    public static int[] Histogram(IEnumerable<float> values, int bins = 50)
    {
        var h = new int[bins];
        foreach (var v in values) h[Math.Clamp((int)(v / 100f * bins), 0, bins - 1)]++;
        return h;
    }

    /// <summary>The reflectance (0..100) of every pixel inside the symbol's quadrilateral.</summary>
    public static IEnumerable<float> PixelsInside(GrayImage img, PointF[] c)
    {
        if (c.Length != 4) yield break;
        int x0 = Math.Max(0, (int)c.Min(p => p.X)), x1 = Math.Min(img.Width - 1, (int)c.Max(p => p.X));
        int y0 = Math.Max(0, (int)c.Min(p => p.Y)), y1 = Math.Min(img.Height - 1, (int)c.Max(p => p.Y));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (Inside(c, x + 0.5f, y + 0.5f)) yield return img.Pixels[y * img.Stride + x] / 255f * 100f;
    }

    /// <summary>
    /// The scan reflectance profile across a 1D symbol: along its longer side through its middle, from a
    /// quiet zone's width before it to one after, sampled every half pixel through a disc of the aperture.
    /// </summary>
    public static float[] Profile(GrayImage img, PointF[] c, float xPx, float apertureDiameterPx)
    {
        if (c.Length != 4 || xPx <= 0) return Array.Empty<float>();
        var mid = new PointF(c.Average(p => p.X), c.Average(p => p.Y));
        // the longer pair of sides runs along the bars' direction of reading
        bool along01 = Dist(c[0], c[1]) + Dist(c[3], c[2]) >= Dist(c[0], c[3]) + Dist(c[1], c[2]);
        var a = along01 ? Mid(c[0], c[3]) : Mid(c[0], c[1]);
        var b = along01 ? Mid(c[1], c[2]) : Mid(c[3], c[2]);
        float len = Dist(a, b);
        if (len <= 0) return Array.Empty<float>();
        float dx = (b.X - a.X) / len, dy = (b.Y - a.Y) / len, half = len / 2 + 12 * xPx;
        int n = (int)(2 * half * 2);
        var p = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = -half + i / 2f;
            p[i] = Disc(img, mid.X + t * dx, mid.Y + t * dy, apertureDiameterPx / 2) / 255f * 100f;
        }
        return p;
    }

    private static float Disc(GrayImage img, float cx, float cy, float r)
    {
        r = Math.Max(0.5f, r);
        int x0 = (int)Math.Floor(cx - r), x1 = (int)Math.Ceiling(cx + r), y0 = (int)Math.Floor(cy - r), y1 = (int)Math.Ceiling(cy + r);
        double sum = 0;
        int n = 0;
        for (int y = Math.Max(0, y0); y <= Math.Min(img.Height - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(img.Width - 1, x1); x++)
            {
                float ddx = x + 0.5f - cx, ddy = y + 0.5f - cy;
                if (ddx * ddx + ddy * ddy > r * r) continue;
                sum += img.Pixels[y * img.Stride + x];
                n++;
            }
        if (n == 0)
        {
            int x = Math.Clamp((int)cx, 0, img.Width - 1), y = Math.Clamp((int)cy, 0, img.Height - 1);
            return img.Pixels[y * img.Stride + x];
        }
        return (float)(sum / n);
    }

    private static bool Inside(PointF[] q, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
            if ((q[i].Y > y) != (q[j].Y > y) && x < (q[j].X - q[i].X) * (y - q[i].Y) / (q[j].Y - q[i].Y) + q[i].X) inside = !inside;
        return inside;
    }

    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static PointF Mid(PointF a, PointF b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
}
