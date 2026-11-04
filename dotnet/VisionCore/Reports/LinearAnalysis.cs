using BarcodeReading;

namespace VisionCore.Reports;

/// <summary>One 1D scan line as the engine graded it (ISO/IEC 15416), as CodeReader's LinearScan hands it over:
/// reflectances in %, grades 4 = A .. 0 = F; Grade is the scan's worst parameter.</summary>
public sealed record EngineScan(float RMin, float RMax, float SymbolContrast, float EdgeContrastMin, float Modulation, float Defects,
    float Decodability, bool Decoded, bool QuietZoneOk, int GradeRmin, int GradeQuietZone, int Grade);

/// <summary>
/// The figures of a 1D verification report measured from the image, as the engine measures them
/// (grade1d.cpp): ten scan reflectance profiles across the bars from 10 % to 90 % of the bar height through
/// a 0.8 X aperture, each with Rmin, Rmax, symbol contrast, the minimum edge contrast, modulation, defects
/// and its quiet zones in X; the bar height, the symbol's width and the bar width growth. Decodability and
/// decode per scan are the engine's only (they need its reference decoders). Grey levels: the engine's
/// reflectance scale differs when it is calibrated, so these figures are the report's estimate.
/// </summary>
public static class LinearAnalysis
{
    /// <summary>Samples per pixel along a scan line, as the engine.</summary>
    public const int Sub = 4;

    /// <summary>One scan line: its profile (0..100, Sub samples per pixel), where the symbol's edges are, and its
    /// parameters. First and Last are the sample positions of the symbol's first and last edge.</summary>
    public sealed record Scan(float Offset, float[] Profile, float SamplesPerX, float First, float Last, float RMin, float RMax,
        float ECmin, float Modulation, float Defects, float QuietLeftX, float QuietRightX, bool QuietOk, int Edges)
    {
        public float SymbolContrast => RMax - RMin;
        public float Threshold => (RMax + RMin) / 2;
        public int GradeRmin => RMin <= 0.5f * RMax ? 4 : 0;
        public int GradeSc => GradeHigh(SymbolContrast, 70, 55, 40, 20);
        public int GradeEc => ECmin >= 15 ? 4 : 0;
        public int GradeMod => GradeHigh(Modulation, 0.70f, 0.60f, 0.50f, 0.40f);
        public int GradeDefects => GradeLow(Defects, 0.15f, 0.20f, 0.25f, 0.30f);
        public int GradeQuiet => QuietOk ? 4 : 0;
        /// <summary>The worst of the parameters measured here (decodability and decode not included: at most this).</summary>
        public int GradeMeasured => Edges < 4 ? 0 : new[] { GradeRmin, GradeSc, GradeEc, GradeMod, GradeDefects, GradeQuiet }.Min();
    }

    /// <summary>The ten scans and the symbol's size: X, bar height and width (first to last bar, middle scan) in
    /// pixels, and the bar width growth in X (bars wider than nominal +, narrower -; spaces the other way).</summary>
    public sealed record Symbol(IReadOnlyList<Scan> Scans, float XPx, float BarHeightPx, float WidthPx, float GrowthX);

    public static int GradeHigh(float v, float a, float b, float c, float d) => v >= a ? 4 : v >= b ? 3 : v >= c ? 2 : v >= d ? 1 : 0;
    public static int GradeLow(float v, float a, float b, float c, float d) => v <= a ? 4 : v <= b ? 3 : v <= c ? 2 : v <= d ? 1 : 0;

    /// <summary>The type's minimum quiet zones in X, left and right: EAN / UPC their own (ISO/IEC 15420), the others 10 X.</summary>
    public static (float Left, float Right) MinQuietZone(CodeFormat f) => f switch
    {
        CodeFormat.Ean13 => (11, 7), CodeFormat.Ean8 => (7, 7), CodeFormat.UpcA => (9, 9), CodeFormat.UpcE => (9, 7), _ => (10, 10),
    };

    /// <summary>
    /// Measures a 1D symbol from its corners (in reading order: the first pair of sides runs along the bars'
    /// reading direction when it is the longer) and the engine's X in pixels, the quiet zones against the
    /// type's minimum. Null when it cannot.
    /// </summary>
    public static Symbol? Analyse(GrayImage img, PointF[] c, float xPx, CodeFormat format)
    {
        if (c.Length != 4 || xPx <= 0) return null;
        var mid = new PointF(c.Average(p => p.X), c.Average(p => p.Y));
        bool along01 = Dist(c[0], c[1]) + Dist(c[3], c[2]) >= Dist(c[0], c[3]) + Dist(c[1], c[2]);
        var a = along01 ? Mid(c[0], c[3]) : Mid(c[0], c[1]);
        var b = along01 ? Mid(c[1], c[2]) : Mid(c[3], c[2]);
        float len = Dist(a, b);
        if (len < 4 * xPx) return null;
        float dx = (b.X - a.X) / len, dy = (b.Y - a.Y) / len, ex = -dy, ey = dx;

        // the bar height: from the middle, across the bars both ways, as far as the profile still looks like the middle's
        float[] Raw(float off) => Line(img, mid.X + off * ex, mid.Y + off * ey, dx, dy, len / 2, 1);
        var centre = Raw(0);
        bool Inside(float x, float y) => x >= 0 && y >= 0 && x <= img.Width && y <= img.Height;
        float Reach(int side)
        {
            // never past the image's edge: clamped samples would repeat its last row and look like more bars
            float off = 0, limit = Math.Min(2 * len, img.Width + img.Height);
            while (off < limit)
            {
                float o = side * (off + 0.5f), cx = mid.X + o * ex, cy = mid.Y + o * ey;
                if (!Inside(cx - len / 2 * dx, cy - len / 2 * dy) || !Inside(cx + len / 2 * dx, cy + len / 2 * dy)) break;
                if (Correlation(centre, Raw(o)) < 0.7f) break;
                off += 0.5f;
            }
            return off;
        }
        float lo = Reach(-1), hi = Reach(1);
        float height = lo + hi;
        if (height < 2) height = 2;
        float shift = (hi - lo) / 2;

        // ten scan lines from 10 % to 90 % of the bar height, through a 0.8 X aperture, with the quiet zones
        float extent = len / 2 + 0.1f * len + 12 * xPx;
        int box = Math.Max(1, (int)Math.Round(0.8f * xPx * Sub));
        var scans = new List<Scan>();
        for (int l = 0; l < 10; l++)
        {
            float off = shift + (-0.4f + 0.8f * l / 9f) * height;
            float sx = mid.X + off * ex, sy = mid.Y + off * ey;
            var raw = Line(img, sx, sy, dx, dy, extent, Sub);
            // the samples inside the image: a quiet zone is measured only there (beyond, the edge pixels repeat)
            int inLo = 0, inHi = raw.Length;
            while (inLo < raw.Length && !Inside(sx + (-extent + (float)inLo / Sub) * dx, sy + (-extent + (float)inLo / Sub) * dy)) inLo++;
            while (inHi > inLo && !Inside(sx + (-extent + (float)(inHi - 1) / Sub) * dx, sy + (-extent + (float)(inHi - 1) / Sub) * dy)) inHi--;
            scans.Add(Measure(Smooth(raw, box), xPx * Sub, MinQuietZone(format), off / height, inLo, inHi));
        }
        var middle = scans[4];
        float width = middle.Edges >= 4 ? (middle.Last - middle.First) / Sub : 0;
        bool twoWidth = format is CodeFormat.Code39 or CodeFormat.Itf or CodeFormat.Codabar;
        float growth = (float)scans.Where(s => s.Edges >= 4).Select(s => GrowthOf(s, xPx * Sub, twoWidth)).DefaultIfEmpty(0).Average();
        return new Symbol(scans, xPx, height, width, growth);
    }

    // one scan as grade1d.cpp's gradeScan, without decodability and decode
    // (inLo, inHi: the samples inside the image; a quiet zone reaching past them is measured only to the image's edge)
    internal static Scan Measure(float[] r, float xs, (float L, float R) minQz, float offset, int inLo = 0, int inHi = int.MaxValue)
    {
        var empty = new Scan(offset, r, xs, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0);
        if (r.Length < 32) return empty;
        // the symbol: the run of edges around the scan's middle with no gap wider than 6 X (a quiet zone is 10 X)
        List<float> SymbolEdges(List<float> all, out int before, out int after)
        {
            before = 0; after = r.Length;
            if (all.Count == 0) return all;
            float m = r.Length / 2f;
            int i0 = 0;
            for (int k = 0; k < all.Count; k++) if (Math.Abs(all[k] - m) < Math.Abs(all[i0] - m)) i0 = k;
            int lo = i0, hi = i0;
            while (lo > 0 && all[lo] - all[lo - 1] <= 6 * xs) lo--;
            while (hi + 1 < all.Count && all[hi + 1] - all[hi] <= 6 * xs) hi++;
            before = lo > 0 ? (int)Math.Ceiling(all[lo - 1]) : 0;
            after = hi + 1 < all.Count ? (int)Math.Floor(all[hi + 1]) + 1 : r.Length;
            return all.GetRange(lo, hi - lo + 1);
        }
        SymbolEdges(EdgesOf(r, (r.Min() + r.Max()) / 2), out int q0, out int q1);
        float rmin = float.MaxValue, rmax = float.MinValue;
        for (int i = q0; i < q1; i++) { rmin = Math.Min(rmin, r[i]); rmax = Math.Max(rmax, r[i]); }
        float sc = rmax - rmin, gt = (rmax + rmin) / 2;
        var e = SymbolEdges(EdgesOf(r, gt), out int before, out int after);
        if (e.Count < 4 || sc <= 0) return empty with { RMin = rmin, RMax = rmax, Edges = e.Count };
        int a = (int)Math.Floor(e[0]), b = (int)Math.Ceiling(e[^1]);
        float qzL = (e[0] - Math.Max(before, inLo)) / xs, qzR = (Math.Min(after, inHi) - e[^1]) / xs;
        bool quiet = qzL >= minQz.L && qzR >= minQz.R && r[a > 0 ? a - 1 : 0] > gt;
        // elements between consecutive edges, plus the quiet zones as the outer spaces
        var el = new List<(int, int)> { (before, a) };
        for (int k = 0; k + 1 < e.Count; k++) el.Add(((int)Math.Ceiling(e[k]), (int)Math.Floor(e[k + 1]) + 1));
        el.Add((b, after));
        var ext = new List<float>();
        float ern = 0;
        for (int k = 0; k < el.Count; k++)
        {
            var (x0, x1) = el[k];
            x1 = Math.Min(x1, r.Length);
            if (x1 <= x0) { ext.Add(gt); continue; }
            bool bar = k % 2 == 1;
            float elo = float.MaxValue, ehi = float.MinValue;
            for (int i = x0; i < x1; i++) { elo = Math.Min(elo, r[i]); ehi = Math.Max(ehi, r[i]); }
            ext.Add(bar ? elo : ehi);
            // element reflectance non-uniformity: a valley in a space, or a peak in a bar, with higher (lower) profile on both sides
            int n = x1 - x0;
            if (n > 2)
            {
                var left = new float[n];
                var right = new float[n];
                for (int i = 0; i < n; i++) left[i] = i > 0 ? (bar ? Math.Min(left[i - 1], r[x0 + i]) : Math.Max(left[i - 1], r[x0 + i])) : r[x0];
                for (int i = n - 1; i >= 0; i--) right[i] = i + 1 < n ? (bar ? Math.Min(right[i + 1], r[x0 + i]) : Math.Max(right[i + 1], r[x0 + i])) : r[x0 + i];
                for (int i = 1; i + 1 < n; i++)
                    ern = Math.Max(ern, bar ? r[x0 + i] - Math.Max(left[i - 1], right[i + 1]) : Math.Min(left[i - 1], right[i + 1]) - r[x0 + i]);
            }
        }
        float ec = float.MaxValue;
        for (int k = 0; k + 1 < ext.Count; k++) ec = Math.Min(ec, Math.Abs(ext[k] - ext[k + 1]));
        return new Scan(offset, r, xs, e[0], e[^1], rmin, rmax, ec, ec / sc, ern / sc, qzL, qzR, quiet, e.Count);
    }

    /// <summary>Element edges at the threshold, sub-sample (a sample on the threshold counts as light, as the engine).</summary>
    public static List<float> EdgesOf(float[] r, float gt)
    {
        var e = new List<float>();
        for (int i = 1; i < r.Length; i++)
            if (r[i - 1] < gt != r[i] < gt) e.Add(i - 1 + (gt - r[i - 1]) / (r[i] - r[i - 1]));
        return e;
    }

    /// <summary>
    /// Bar width growth in X: each element's width against the nearest whole number of modules, bars gaining
    /// what spaces lose (g: bar = n X + g, space = n X - g), found by a few rounds from g = 0. Two-width types
    /// (Code 39, ITF, Codabar): half the narrow bars' excess over the narrow spaces.
    /// </summary>
    public static float GrowthOf(Scan s, float xs, bool twoWidth = false)
    {
        var e = EdgesOf(s.Profile, s.Threshold).Where(v => v >= s.First - 0.01f && v <= s.Last + 0.01f).ToList();
        if (e.Count < 4) return 0;
        if (twoWidth)
        {
            // two widths (wide 2 to 3 X, not whole modules): the narrow bars against the narrow spaces, (bar - space) / 2
            var nb = new List<float>();
            var ns = new List<float>();
            for (int k = 0; k + 1 < e.Count; k++)
            {
                float w = e[k + 1] - e[k];
                if (w < 1.6f * xs) (k % 2 == 0 ? nb : ns).Add(w);
            }
            return nb.Count == 0 || ns.Count == 0 ? 0 : (nb.Average() - ns.Average()) / 2 / xs;
        }
        float g = 0;
        for (int round = 0; round < 4; round++)
        {
            double sum = 0;
            int n = 0;
            for (int k = 0; k + 1 < e.Count; k++)
            {
                bool bar = k % 2 == 0; // the first edge is light to dark
                float w = e[k + 1] - e[k];
                float nominal = Math.Max(1, (float)Math.Round((w - (bar ? g : -g)) / xs)) * xs;
                sum += bar ? w - nominal : nominal - w;
                n++;
            }
            g = (float)(sum / n);
        }
        return g / xs;
    }

    /// <summary>
    /// The decoded data's check digit, as a verifier lists it: the GS1 mod 10 digit of EAN / UPC (and ITF-14),
    /// Code 39's optional mod 43 character; for Code 128 and Code 93 the decoder checked the symbol's own check
    /// characters, which are not in the data. Null for types without one.
    /// </summary>
    public static (string Text, bool? Ok)? CheckDigit(CodeFormat f, string text)
    {
        bool digits = text.Length > 1 && text.All(char.IsAsciiDigit);
        switch (f)
        {
            case CodeFormat.Ean13 or CodeFormat.Ean8 or CodeFormat.UpcA when digits:
            case CodeFormat.Itf when digits && text.Length == 14:
                {
                    int want = Mod10(text[..^1]);
                    bool ok = text[^1] - '0' == want;
                    return ($"{text[^1]} (GS1 mod 10: {want}) {(ok ? "correct" : "WRONG")}", ok);
                }
            case CodeFormat.UpcE when digits && text.Length == 8 && UpcEToA(text) is { } upcA:
                {
                    int want = Mod10(upcA[..^1]);
                    bool ok = text[^1] - '0' == want;
                    return ($"{text[^1]} (GS1 mod 10 of UPC-A {upcA[..^1]}: {want}) {(ok ? "correct" : "WRONG")}", ok);
                }
            case CodeFormat.Itf:
                return ("optional in ITF; not a 14-digit ITF-14", null);
            case CodeFormat.Code39 when text.Length > 1:
                {
                    const string set = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
                    int sum = 0;
                    foreach (char ch in text[..^1]) { int v = set.IndexOf(ch); if (v < 0) return ("optional mod 43: data outside the Code 39 set", null); sum += v; }
                    bool match = set[sum % 43] == text[^1];
                    return (match ? $"{text[^1]} matches mod 43 (optional in Code 39)" : "no mod 43 check character (optional in Code 39)", null);
                }
            case CodeFormat.Gs1_128 or CodeFormat.Code93:
                return ("symbol check character(s) checked by the decoder (not part of the data)", true);
            default:
                return null;
        }
    }

    /// <summary>The GS1 mod 10 check digit of the digits before it.</summary>
    public static int Mod10(string body)
    {
        int sum = 0;
        for (int i = 0; i < body.Length; i++) sum += (body[body.Length - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        return (10 - sum % 10) % 10;
    }

    /// <summary>UPC-E (number system, six digits, check) expanded to its UPC-A; null when not a UPC-E.</summary>
    public static string? UpcEToA(string e)
    {
        if (e.Length != 8 || e[0] is not ('0' or '1')) return null;
        string d = e[1..7];
        string body = d[5] switch
        {
            '0' or '1' or '2' => d[..2] + d[5] + "0000" + d[2..5],
            '3' => d[..3] + "00000" + d[3..5],
            '4' => d[..4] + "00000" + d[4],
            _ => d[..5] + "0000" + d[5],
        };
        return e[0] + body + e[7];
    }

    // a line of samples (0..100) every 1/sub pixel from -half to +half about (x, y) along (dx, dy), bilinear
    private static float[] Line(GrayImage img, float x, float y, float dx, float dy, float half, int sub)
    {
        int n = Math.Max(2, (int)(2 * half * sub));
        var p = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = -half + (float)i / sub;
            p[i] = Bilinear(img, x + t * dx, y + t * dy) / 255f * 100f;
        }
        return p;
    }

    // the aperture along the scan line: a running mean of `box` samples, as the engine
    private static float[] Smooth(float[] r, int box)
    {
        var o = new float[r.Length];
        for (int i = 0; i < r.Length; i++)
        {
            double s = 0;
            int n = 0;
            for (int d = -box / 2; d <= box / 2; d++)
                if (i + d >= 0 && i + d < r.Length) { s += r[i + d]; n++; }
            o[i] = (float)(s / n);
        }
        return o;
    }

    private static float Bilinear(GrayImage img, float x, float y)
    {
        x = Math.Clamp(x - 0.5f, 0, img.Width - 1);
        y = Math.Clamp(y - 0.5f, 0, img.Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, img.Width - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
        float fx = x - x0, fy = y - y0;
        float P(int px, int py) => img.Pixels[py * img.Stride + px];
        return (P(x0, y0) * (1 - fx) + P(x1, y0) * fx) * (1 - fy) + (P(x0, y1) * (1 - fx) + P(x1, y1) * fx) * fy;
    }

    private static float Correlation(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < n; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
        return saa <= 1e-6 || sbb <= 1e-6 ? 0 : (float)(sab / Math.Sqrt(saa * sbb));
    }

    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static PointF Mid(PointF a, PointF b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
}
