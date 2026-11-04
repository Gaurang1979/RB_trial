using System.Numerics;

namespace VisionCore;

/// <summary>The five zones of the picture check: the corner and centre cells of a 3 x 3 grid.</summary>
public enum PictureZoneName { TopLeft, TopRight, BottomLeft, BottomRight, Centre }

/// <summary>A zone's mark: green (within what the job needs), yellow (close), red (not), or grey
/// (nothing in the zone to judge sharpness on: a plain surface).</summary>
public enum ZoneState { Good, Close, Bad, NoDetail }

/// <summary>The plain advice of requirements AS-13, one kind per bullet.</summary>
public enum PictureAdviceKind
{
    /// <summary>One or more corners blurred (a side, say), the centre sharp: the lens or camera is tilted, the
    /// part is not flat, or the lens is not sharp at its edge.</summary>
    Tilt,
    /// <summary>All zones blurred, or the centre: focus the lens, or the part moved.</summary>
    Focus,
    /// <summary>All zones soft and dull with low contrast: clean the lens or the window in front of it.</summary>
    CleanLens,
    /// <summary>All corners darker than the centre: open the aperture a little, move the light or use a wider one.</summary>
    Vignetting,
    /// <summary>A zone too dark: adjust the light.</summary>
    Dark,
    /// <summary>A zone too bright: adjust the light, or remove the glare there.</summary>
    Bright,
}

/// <summary>
/// One zone measured. Sharpness 0..100 is the steepest edge slope over the edge's own contrast: 100 is
/// a step from dark to light within one pixel, 40 an edge blurred over about 5 pixels (Gaussian sigma 2);
/// NaN when the zone has no edge to judge (<see cref="ZoneState.NoDetail"/>). Brightness is the mean grey.
/// Light is the bright level the zone gets (95th percentile; a corner's at its outer quarter, where
/// vignetting is strongest), Brightest the cell's 99th percentile (dark only when even that is low).
/// Contrast 0..1 is (p95 - p5) / (p95 + p5): a veil of light from a dirty lens lifts the dark end and
/// lowers it, a dark picture does not. Saturated is the share of pixels at 250 or more.
/// </summary>
public sealed record PictureZone(PictureZoneName Zone, RoiBox Box, double Sharpness, double Brightness, double Light,
                                 double Brightest, double Contrast, double Saturated, ZoneState SharpState, ZoneState LightState)
{
    /// <summary>The zone's mark: the worse of its sharpness and its light.</summary>
    public ZoneState State => SharpState == ZoneState.NoDetail ? (LightState == ZoneState.Good ? ZoneState.NoDetail : LightState)
        : (ZoneState)Math.Max((int)SharpState, (int)LightState);
}

/// <summary>One line of advice and the zones it is about.</summary>
public sealed record PictureAdvice(PictureAdviceKind Kind, IReadOnlyList<PictureZoneName> Zones);

public sealed record PictureCheckResult(IReadOnlyList<PictureZone> Zones, IReadOnlyList<PictureAdvice> Advice);

/// <summary>
/// What a picture needs. Starting values for code and text reading, from the owner's 58 sample pictures
/// (2026-11-01): zones with detail on pictures that read score 30 to 56, a picture of soft lines 21 to 23.
/// Sharpness: Good at 30 and more (an edge within about 6 pixels, Gaussian sigma 2.7), Bad under 22 (sigma
/// over 3.6). A job with small modules needs sharper. A zone is also blurred next to the sharpest zone: under
/// <see cref="RelativeClose"/> of it yellow, under <see cref="RelativeBad"/> red, so a tilt shows even when
/// every zone is still readable.
/// </summary>
public sealed record PictureCheckLimits
{
    public double SharpGood { get; init; } = 30;
    public double SharpBad { get; init; } = 22;
    public double RelativeClose { get; init; } = 0.7;
    public double RelativeBad { get; init; } = 0.55;
    /// <summary>Brightest level (99th percentile) under which a zone with detail is dark: red under DarkBad,
    /// yellow under DarkClose. A plain dark zone is the background, not a light to adjust.</summary>
    public double DarkBad { get; init; } = 50;
    public double DarkClose { get; init; } = 80;
    /// <summary>Share of saturated pixels (250 and more) over which a zone is too bright.</summary>
    public double BrightClose { get; init; } = 0.01;
    public double BrightBad { get; init; } = 0.05;
    /// <summary>All four corners' bright level under this share of the centre's: vignetting.</summary>
    public double Vignetting { get; init; } = 0.75;
    /// <summary>Contrast (0..1, see <see cref="PictureZone.Contrast"/>) under which a zone is dull.</summary>
    public double DullContrast { get; init; } = 0.3;
}

/// <summary>
/// The picture check per corner and centre (requirements AS-13): sharpness and brightness of the four corners
/// and the centre, and plain advice for the lens and the light. Advice only: it never changes a tool or a
/// result; a part is still decided by the job's tools, and a picture in doubt is NG on the line (section 12).
/// </summary>
/// <remarks>
/// Sharpness is the edge slope over the local contrast: at each pixel the Sobel gradient (as grey levels per
/// pixel) divided by the grey range (max - min) in the 15 x 15 window around it, where that range is clearly
/// above the noise. The zone's value is the 90th percentile of that ratio (the edges' centres), times 200.
/// Dividing by the local range makes it the same for a faint edge and a strong one, so a dark or low-contrast
/// zone does not read as blurred; it depends only on how many pixels the edge is spread over. A large cell is
/// measured on its middle <see cref="MaxSide"/> pixels square. Pure C#: it only advises, takes tens of
/// milliseconds on a 20 MP picture and needs no change to the engine's C API.
/// </remarks>
public static class PictureCheck
{
    /// <summary>Half the window of the local grey range: holds an edge blurred up to sigma 3.5 whole.</summary>
    public const int Radius = 7;
    /// <summary>Sharpness is measured on the middle MaxSide x MaxSide pixels of a larger cell.</summary>
    public const int MaxSide = 768;
    const int Bins = 1024;
    const double MaxRatio = 0.75;

    public static PictureCheckResult Measure(GrayImage image, PictureCheckLimits? limits = null)
    {
        var l = limits ?? new PictureCheckLimits();
        var raw = new List<(PictureZoneName Zone, RoiBox Box, double Sharp, Levels Cell, double Light)>();
        foreach (var zone in Enum.GetValues<PictureZoneName>())
        {
            var box = Box(zone, image.Width, image.Height);
            raw.Add((zone, box, Sharpness(image, box), Measure(image, box), Measure(image, LightBox(zone, box)).P95));
        }

        var best = raw.Where(z => !double.IsNaN(z.Sharp)).Select(z => z.Sharp).DefaultIfEmpty(double.NaN).Max();
        var zones = raw.Select(z => new PictureZone(z.Zone, z.Box, z.Sharp, z.Cell.Mean, z.Light, z.Cell.P99,
            z.Cell.P95 + z.Cell.P5 > 0 ? (z.Cell.P95 - z.Cell.P5) / (z.Cell.P95 + z.Cell.P5) : 0, z.Cell.Saturated,
            SharpState(z.Sharp, best, l), LightState(z.Cell.P99, z.Cell.Saturated, !double.IsNaN(z.Sharp), l))).ToList();
        return new PictureCheckResult(zones, Advise(zones, l));
    }

    /// <summary>The zone's cell of the 3 x 3 grid, in image pixels.</summary>
    public static RoiBox Box(PictureZoneName zone, int width, int height)
    {
        var (col, row) = zone switch
        {
            PictureZoneName.TopLeft => (0, 0),
            PictureZoneName.TopRight => (2, 0),
            PictureZoneName.BottomLeft => (0, 2),
            PictureZoneName.BottomRight => (2, 2),
            _ => (1, 1),
        };
        int x0 = col * width / 3, x1 = (col + 1) * width / 3, y0 = row * height / 3, y1 = (row + 1) * height / 3;
        return new RoiBox(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Where a zone's light is read: a corner's quarter at the picture's corner (vignetting is
    /// strongest there, and the cell's side near the centre would hide it), the whole centre cell.</summary>
    static RoiBox LightBox(PictureZoneName zone, RoiBox b)
    {
        int w = b.Width / 2, h = b.Height / 2;
        return zone switch
        {
            PictureZoneName.TopLeft => new RoiBox(b.X, b.Y, w, h),
            PictureZoneName.TopRight => new RoiBox(b.X + b.Width - w, b.Y, w, h),
            PictureZoneName.BottomLeft => new RoiBox(b.X, b.Y + b.Height - h, w, h),
            PictureZoneName.BottomRight => new RoiBox(b.X + b.Width - w, b.Y + b.Height - h, w, h),
            _ => b,
        };
    }

    static ZoneState SharpState(double sharp, double best, PictureCheckLimits l)
    {
        if (double.IsNaN(sharp)) return ZoneState.NoDetail;
        if (sharp < l.SharpBad || sharp < l.RelativeBad * best) return ZoneState.Bad;
        if (sharp < l.SharpGood || sharp < l.RelativeClose * best) return ZoneState.Close;
        return ZoneState.Good;
    }

    /// <summary>Glare anywhere; dark only where there is detail (a plain dark zone is the background).</summary>
    static ZoneState LightState(double brightest, double saturated, bool detail, PictureCheckLimits l) =>
        saturated > l.BrightBad || detail && brightest < l.DarkBad ? ZoneState.Bad
        : saturated > l.BrightClose || detail && brightest < l.DarkClose ? ZoneState.Close
        : ZoneState.Good;

    /// <summary>The advice of AS-13 from the zones' marks: sharpness first (the lens), then the light.</summary>
    static List<PictureAdvice> Advise(List<PictureZone> zones, PictureCheckLimits l)
    {
        var advice = new List<PictureAdvice>();
        var judged = zones.Where(z => z.SharpState != ZoneState.NoDetail).ToList();
        var blurred = judged.Where(z => z.SharpState != ZoneState.Good).Select(z => z.Zone).ToList();
        // every zone that shows something is blurred (also the one zone of a part on a plain background): the
        // focus, or a dirty lens when dull too. Corners or a side blurred and the centre sharp: a tilt. The
        // centre blurred and corners sharp is the focus too: the lens is set on the centre.
        if (blurred.Count > 0 && (blurred.Count == judged.Count || blurred.Contains(PictureZoneName.Centre)))
        {
            var dull = Median(judged.Where(z => blurred.Contains(z.Zone)).Select(z => z.Contrast)) < l.DullContrast;
            advice.Add(new PictureAdvice(dull ? PictureAdviceKind.CleanLens : PictureAdviceKind.Focus, blurred));
        }
        else if (blurred.Count > 0) advice.Add(new PictureAdvice(PictureAdviceKind.Tilt, blurred));

        // corners darker than the centre: vignetting, and those corners are not "dark" on their own. Only when
        // every corner shows something: plain dark corners are the background around the part.
        var centre = zones.Single(z => z.Zone == PictureZoneName.Centre);
        var corners = zones.Where(z => z.Zone != PictureZoneName.Centre).ToList();
        var vignetting = centre.LightState == ZoneState.Good
                         && corners.All(c => c.SharpState != ZoneState.NoDetail && c.Light < l.Vignetting * centre.Light);
        if (vignetting) advice.Add(new PictureAdvice(PictureAdviceKind.Vignetting, corners.Select(c => c.Zone).ToList()));
        var dark = zones.Where(z => z.LightState != ZoneState.Good && z.Saturated <= l.BrightClose && !(vignetting && z.Zone != PictureZoneName.Centre))
            .Select(z => z.Zone).ToList();
        if (dark.Count > 0) advice.Add(new PictureAdvice(PictureAdviceKind.Dark, dark));
        var bright = zones.Where(z => z.Saturated > l.BrightClose).Select(z => z.Zone).ToList();
        if (bright.Count > 0) advice.Add(new PictureAdvice(PictureAdviceKind.Bright, bright));
        return advice;
    }

    static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToArray();
        return v.Length == 0 ? double.NaN : v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    readonly record struct Levels(double Mean, double P5, double P95, double P99, double Saturated);

    /// <summary>Mean grey, 5th, 95th and 99th percentiles, share at 250 and more.</summary>
    static Levels Measure(GrayImage image, RoiBox box)
    {
        var hist = new long[256];
        long sum = 0, n = 0;
        var step = (long)box.Width * box.Height > 1_000_000 ? 2 : 1;   // a large cell: every other row and column
        for (var y = box.Y; y < box.Y + box.Height; y += step)
        {
            var row = y * image.Stride;
            for (var x = box.X; x < box.X + box.Width; x += step)
            {
                var v = image.Pixels[row + x];
                hist[v]++;
                sum += v;
                n++;
            }
        }
        if (n == 0) return default;
        long saturated = 0;
        for (var v = 250; v < 256; v++) saturated += hist[v];
        return new Levels((double)sum / n, Percentile(hist, n, 0.05), Percentile(hist, n, 0.95), Percentile(hist, n, 0.99), (double)saturated / n);
    }

    static int Percentile(long[] hist, long n, double p)
    {
        long want = (long)Math.Ceiling(p * n), seen = 0;
        for (var v = 0; v < hist.Length; v++)
        {
            seen += hist[v];
            if (seen >= Math.Max(1, want)) return v;
        }
        return hist.Length - 1;
    }

    /// <summary>The zone's sharpness 0..100, or NaN when it has too few edges clearly above the noise.</summary>
    static double Sharpness(GrayImage image, RoiBox box)
    {
        // the middle of a large cell only: the same lens and light, a fraction of the time
        int w = Math.Min(box.Width, MaxSide), h = Math.Min(box.Height, MaxSide);
        int x0 = box.X + (box.Width - w) / 2, y0 = box.Y + (box.Height - h) / 2;
        if (w < 4 * Radius || h < 4 * Radius) return double.NaN;
        var px = new byte[w * h];
        for (var y = 0; y < h; y++) Array.Copy(image.Pixels, (y0 + y) * image.Stride + x0, px, y * w, w);

        // edges must stand clearly above the noise: sigma from the median step between neighbours
        var minRange = Math.Max(24, 8 * Noise(px, w, h));
        var max = Extreme(px, w, h, true);
        var min = Extreme(px, w, h, false);

        var hist = new long[Bins];
        long count = 0;
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                var range = max[i] - min[i];
                if (range < minRange) continue;
                int a = px[i - w - 1], b = px[i - w], c = px[i - w + 1], d = px[i - 1], f = px[i + 1], g = px[i + w - 1], k = px[i + w], m = px[i + w + 1];
                double gx = (c + 2 * f + m - a - 2 * d - g) / 8.0, gy = (g + 2 * k + m - a - 2 * b - c) / 8.0;
                var ratio = Math.Sqrt(gx * gx + gy * gy) / range;
                hist[Math.Min(Bins - 1, (int)(ratio / MaxRatio * Bins))]++;
                count++;
            }
        }
        // at least a few edges' worth of pixels: 0.5 % of the zone and 200 pixels
        if (count < Math.Max(200, w * h / 200)) return double.NaN;
        var p90 = (Percentile(hist, count, 0.9) + 0.5) / Bins * MaxRatio;
        return Math.Min(100, 200 * p90);
    }

    /// <summary>The noise's sigma: the median absolute step between horizontal neighbours (a flat surface's
    /// steps are all noise; edges are few), as sigma of one pixel.</summary>
    static double Noise(byte[] px, int w, int h)
    {
        var hist = new long[256];
        long n = 0;
        for (var y = 0; y < h; y += 2)
            for (var x = 0; x < w - 1; x++)
            {
                hist[Math.Abs(px[y * w + x + 1] - px[y * w + x])]++;
                n++;
            }
        // |a - b| of two pixels with noise sigma s has median 0.954 s (half-normal of sigma s * sqrt 2)
        return Percentile(hist, n, 0.5) / 0.954;
    }

    /// <summary>The largest (or smallest) grey in the (2 Radius + 1) square around each pixel, clipped at the
    /// zone's border: rows then columns, a vector of pixels at a time.</summary>
    static byte[] Extreme(byte[] px, int w, int h, bool largest)
    {
        var rows = new byte[px.Length];
        var padded = new byte[w + 2 * Radius];
        for (var y = 0; y < h; y++)
        {
            // edge pixels repeated: the same as a window clipped at the border
            Array.Copy(px, y * w, padded, Radius, w);
            Array.Fill(padded, px[y * w], 0, Radius);
            Array.Fill(padded, px[y * w + w - 1], Radius + w, Radius);
            var row = rows.AsSpan(y * w, w);
            padded.AsSpan(0, w).CopyTo(row);
            for (var k = 1; k <= 2 * Radius; k++) Combine(row, padded.AsSpan(k, w), largest);
        }
        var result = new byte[px.Length];
        for (var y = 0; y < h; y++)
        {
            var row = result.AsSpan(y * w, w);
            rows.AsSpan(Math.Max(0, y - Radius) * w, w).CopyTo(row);
            for (var k = Math.Max(0, y - Radius) + 1; k <= Math.Min(h - 1, y + Radius); k++) Combine(row, rows.AsSpan(k * w, w), largest);
        }
        return result;
    }

    /// <summary>into = max (or min) of into and other, element by element.</summary>
    static void Combine(Span<byte> into, ReadOnlySpan<byte> other, bool largest)
    {
        var n = Vector<byte>.Count;
        var i = 0;
        for (; i <= into.Length - n; i += n)
        {
            var a = new Vector<byte>(into[i..]);
            var b = new Vector<byte>(other[i..]);
            (largest ? Vector.Max(a, b) : Vector.Min(a, b)).CopyTo(into[i..]);
        }
        for (; i < into.Length; i++) into[i] = largest ? Math.Max(into[i], other[i]) : Math.Min(into[i], other[i]);
    }
}
