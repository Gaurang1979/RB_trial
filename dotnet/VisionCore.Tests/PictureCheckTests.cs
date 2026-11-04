using VisionCore;
using Xunit;
using Xunit.Abstractions;

// Picture check per corner and centre (requirements AS-13) on drawn pictures: a printed-looking pattern of
// dark and light blocks, then blurred, darkened or dulled where each test needs it.
public class PictureCheckTests(ITestOutputHelper output)
{
    private const int W = 600, H = 450;

    /// <summary>Blocks of 6 pixels, dark (40) or light (200) at random, with a little noise: sharp and even.</summary>
    private static double[] Pattern(int seed = 1)
    {
        var rnd = new Random(seed);
        var cols = (W + 5) / 6;
        var blocks = new double[cols * ((H + 5) / 6)];
        for (var i = 0; i < blocks.Length; i++) blocks[i] = rnd.Next(2) == 0 ? 40 : 200;
        var px = new double[W * H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                px[y * W + x] = blocks[(y / 6) * cols + x / 6] + rnd.NextDouble() * 4 - 2;
        return px;
    }

    private static GrayImage Image(double[] px) =>
        new(px.Select(v => (byte)Math.Clamp(Math.Round(v), 0, 255)).ToArray(), W, H);

    /// <summary>A Gaussian blur of sigma over the rectangle (the rest is left sharp).</summary>
    private static void Blur(double[] px, double sigma, int x0 = 0, int y0 = 0, int x1 = W, int y1 = H)
    {
        var r = (int)Math.Ceiling(3 * sigma);
        var k = Enumerable.Range(-r, 2 * r + 1).Select(i => Math.Exp(-i * i / (2 * sigma * sigma))).ToArray();
        var sum = k.Sum();
        for (var i = 0; i < k.Length; i++) k[i] /= sum;
        var tmp = (double[])px.Clone();
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                double v = 0;
                for (var i = -r; i <= r; i++) v += k[i + r] * px[y * W + Math.Clamp(x + i, 0, W - 1)];
                tmp[y * W + x] = v;
            }
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                double v = 0;
                for (var i = -r; i <= r; i++) v += k[i + r] * tmp[Math.Clamp(y + i, 0, H - 1) * W + x];
                px[y * W + x] = v;
            }
    }

    private PictureCheckResult Check(double[] px)
    {
        var r = PictureCheck.Measure(Image(px));
        foreach (var z in r.Zones)
            output.WriteLine($"{z.Zone,-12} sharp {z.Sharpness,5:0.0} {z.SharpState,-8} mean {z.Brightness,5:0} light {z.Light,4:0} brightest {z.Brightest,4:0} contrast {z.Contrast:0.00} sat {z.Saturated:0.000} {z.LightState}");
        foreach (var a in r.Advice) output.WriteLine($"advice {a.Kind}: {string.Join(", ", a.Zones)}");
        return r;
    }

    private static PictureZone Zone(PictureCheckResult r, PictureZoneName name) => r.Zones.Single(z => z.Zone == name);

    [Fact]
    public void SharpEvenPictureHasNoAdvice()
    {
        var r = Check(Pattern());
        Assert.Empty(r.Advice);
        Assert.All(r.Zones, z => Assert.Equal(ZoneState.Good, z.State));
        Assert.All(r.Zones, z => Assert.InRange(z.Sharpness, 60, 100));
        Assert.Equal(5, r.Zones.Count);
    }

    [Fact]
    public void ZonesAreTheCornersAndCentreOfAThreeByThreeGrid()
    {
        Assert.Equal(new RoiBox(0, 0, 200, 150), PictureCheck.Box(PictureZoneName.TopLeft, W, H));
        Assert.Equal(new RoiBox(400, 300, 200, 150), PictureCheck.Box(PictureZoneName.BottomRight, W, H));
        Assert.Equal(new RoiBox(200, 150, 200, 150), PictureCheck.Box(PictureZoneName.Centre, W, H));
    }

    [Fact]
    public void OneBlurredCornerIsATilt()
    {
        var px = Pattern();
        Blur(px, 2.5, 0, 0, W / 3, H / 3);
        var r = Check(px);
        Assert.Equal(ZoneState.Bad, Zone(r, PictureZoneName.TopLeft).SharpState);
        Assert.All(r.Zones.Where(z => z.Zone != PictureZoneName.TopLeft), z => Assert.Equal(ZoneState.Good, z.SharpState));
        var a = Assert.Single(r.Advice);
        Assert.Equal(PictureAdviceKind.Tilt, a.Kind);
        Assert.Equal([PictureZoneName.TopLeft], a.Zones);
    }

    [Fact]
    public void OneBlurredSideIsATilt()
    {
        var px = Pattern();
        Blur(px, 2.5, 0, 0, W / 3, H);
        var r = Check(px);
        var a = Assert.Single(r.Advice);
        Assert.Equal(PictureAdviceKind.Tilt, a.Kind);
        Assert.Equal([PictureZoneName.TopLeft, PictureZoneName.BottomLeft], a.Zones);
    }

    [Fact]
    public void EverythingBlurredIsFocus()
    {
        var px = Pattern();
        Blur(px, 5);
        var r = Check(px);
        Assert.All(r.Zones, z => Assert.Equal(ZoneState.Bad, z.SharpState));
        var a = Assert.Single(r.Advice);
        Assert.Equal(PictureAdviceKind.Focus, a.Kind);
        Assert.Equal(5, a.Zones.Count);
    }

    [Fact]
    public void ABlurredCentreWithSharpCornersIsFocusNotTilt()
    {
        var px = Pattern();
        Blur(px, 3, W / 3, H / 3, 2 * W / 3, 2 * H / 3);
        var r = Check(px);
        var a = Assert.Single(r.Advice);
        Assert.Equal(PictureAdviceKind.Focus, a.Kind);
        Assert.Equal([PictureZoneName.Centre], a.Zones);
    }

    [Fact]
    public void APartOnAPlainBackgroundStillGetsFocusAdvice()
    {
        // only the centre shows something: when it is blurred there is nothing to compare, but it still needs focus
        var px = Pattern();
        Blur(px, 5);
        var rnd = new Random(4);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (x < W / 3 || x >= 2 * W / 3) px[y * W + x] = 150 + rnd.NextDouble() * 4;
        var r = Check(px);
        Assert.Equal(ZoneState.Bad, Zone(r, PictureZoneName.Centre).SharpState);
        var a = Assert.Single(r.Advice);
        Assert.Equal(PictureAdviceKind.Focus, a.Kind);
        Assert.Equal([PictureZoneName.Centre], a.Zones);
    }

    [Fact]
    public void SoftAndDullEverywhereIsCleanTheLens()
    {
        // a film on the lens: a veil of light over everything (low contrast) and softer edges
        var px = Pattern();
        Blur(px, 3);
        for (var i = 0; i < px.Length; i++) px[i] = 110 + (px[i] - 120) * 0.3;
        var r = Check(px);
        Assert.All(r.Zones, z => Assert.True(z.Contrast < 0.3, $"{z.Zone} contrast {z.Contrast}"));
        Assert.All(r.Zones, z => Assert.NotEqual(ZoneState.Good, z.SharpState));
        var a = Assert.Single(r.Advice);
        Assert.Equal(PictureAdviceKind.CleanLens, a.Kind);
    }

    [Fact]
    public void LowContrastAloneDoesNotReadAsBlurred()
    {
        // a faint but sharp print: the sharpness follows the edge's spread, not its strength
        var px = Pattern();
        for (var i = 0; i < px.Length; i++) px[i] = 110 + (px[i] - 120) * 0.3;
        var r = Check(px);
        Assert.All(r.Zones, z => Assert.Equal(ZoneState.Good, z.SharpState));
        Assert.Empty(r.Advice);
    }

    [Fact]
    public void CornersDarkerThanTheCentreIsVignetting()
    {
        var px = Pattern();
        double cx = W / 2.0, cy = H / 2.0, far = Math.Sqrt(cx * cx + cy * cy);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / far;
                px[y * W + x] *= 1 - 0.6 * d * d;   // cos^4-like fall-off: 40 % light left in the far corner
            }
        var r = Check(px);
        var a = Assert.Single(r.Advice);
        Assert.Equal(PictureAdviceKind.Vignetting, a.Kind);
        Assert.Equal(4, a.Zones.Count);
        Assert.DoesNotContain(PictureZoneName.Centre, a.Zones);
        // darker, still sharp: the measure is divided by the local contrast
        Assert.All(r.Zones, z => Assert.Equal(ZoneState.Good, z.SharpState));
    }

    [Fact]
    public void OneDarkZoneIsTheLight()
    {
        var px = Pattern();
        for (var y = 2 * H / 3; y < H; y++)
            for (var x = 2 * W / 3; x < W; x++) px[y * W + x] *= 0.2;
        var r = Check(px);
        Assert.Equal(ZoneState.Bad, Zone(r, PictureZoneName.BottomRight).LightState);
        Assert.Equal(ZoneState.Good, Zone(r, PictureZoneName.BottomRight).SharpState);   // dark, not blurred
        var a = Assert.Single(r.Advice);
        Assert.Equal(PictureAdviceKind.Dark, a.Kind);
        Assert.Equal([PictureZoneName.BottomRight], a.Zones);
    }

    [Fact]
    public void GlareInOneZoneIsTooBright()
    {
        var px = Pattern();
        for (var y = 0; y < H / 3; y++)
            for (var x = 2 * W / 3; x < W; x++)
                if (px[y * W + x] > 120) px[y * W + x] = 255;
        var r = Check(px);
        var a = Assert.Single(r.Advice);
        Assert.Equal(PictureAdviceKind.Bright, a.Kind);
        Assert.Equal([PictureZoneName.TopRight], a.Zones);
    }

    [Fact]
    public void PlainZonesAreNotJudged()
    {
        // a plain surface (only noise) has nothing to be sharp on: grey mark, no advice
        var px = Pattern();
        var rnd = new Random(2);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (x < W / 3 || x >= 2 * W / 3) px[y * W + x] = 150 + rnd.NextDouble() * 8 - 4;
        var r = Check(px);
        foreach (var name in new[] { PictureZoneName.TopLeft, PictureZoneName.TopRight, PictureZoneName.BottomLeft, PictureZoneName.BottomRight })
        {
            Assert.Equal(ZoneState.NoDetail, Zone(r, name).SharpState);
            Assert.True(double.IsNaN(Zone(r, name).Sharpness));
            Assert.Equal(ZoneState.NoDetail, Zone(r, name).State);
        }
        Assert.Empty(r.Advice);
    }

    [Fact]
    public void ADarkBackgroundAroundThePartIsNotAdvice()
    {
        // the part in the middle, dark plain background in the corners: nothing to light there
        var px = Pattern();
        var rnd = new Random(3);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (x < W / 3 || x >= 2 * W / 3) px[y * W + x] = 8 + rnd.NextDouble() * 4;
        var r = Check(px);
        Assert.All(r.Zones.Where(z => z.Zone != PictureZoneName.Centre), z => Assert.Equal(ZoneState.NoDetail, z.State));
        Assert.Empty(r.Advice);
    }

    [Fact]
    public void ATinyPictureIsMeasuredWithoutJudgingSharpness()
    {
        var r = PictureCheck.Measure(new GrayImage(new byte[20 * 10], 20, 10));
        Assert.Equal(5, r.Zones.Count);
        Assert.All(r.Zones, z => Assert.Equal(ZoneState.NoDetail, z.SharpState));
    }

    /// <summary>The samples in VS_IMAGES (customer pictures, never in the repository), when that folder is set:
    /// each is measured without error and the values are printed for a look.</summary>
    [Fact]
    public void SamplePicturesWhenThere()
    {
        var folder = Environment.GetEnvironmentVariable("VS_IMAGES");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).Order().Take(80))
        {
            var image = GrayImage.Load(file);
            // processor time: about the same on a busy Mac as on a quiet one (the check is single-threaded)
            var before = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var r = PictureCheck.Measure(image);
            var after = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            output.WriteLine($"{Path.GetFileName(file)} ({image.Width} x {image.Height}, {watch.ElapsedMilliseconds} ms wall, {after - before:0} ms cpu): " + string.Join("  ", r.Zones.Select(z => $"{z.Zone}:{z.Sharpness:0}/{z.Brightness:0}/{z.State}"))
                             + "  -> " + string.Join("; ", r.Advice.Select(a => $"{a.Kind}({string.Join(",", a.Zones)})")));
            Assert.Equal(5, r.Zones.Count);
        }
    }
}
