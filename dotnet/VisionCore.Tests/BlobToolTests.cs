using System.Text.Json;
using VisionCore;
using Xunit;

// Count / spot (BlobTool over engine/vs_blob) on drawn images: pass rules, NG reasons, job file fields.
public class BlobToolTests
{
    private const int W = 400, H = 300;

    private static GrayImage Page(byte bg = 210, params (int X, int Y, int R)[] discs)
    {
        var px = new byte[W * H];
        Array.Fill(px, bg);
        foreach (var (cx, cy, r) in discs)
            for (int y = Math.Max(0, cy - r); y <= Math.Min(H - 1, cy + r); y++)
                for (int x = Math.Max(0, cx - r); x <= Math.Min(W - 1, cx + r); x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) px[y * W + x] = 40;
        return new GrayImage(px, W, H);
    }

    private static ToolResult Run(BlobToolConfig cfg, GrayImage img, Fixture? fx = null)
    {
        using var t = new BlobTool(cfg);
        return t.Run(img, new BatchFields(), fx);
    }

    private static readonly (int, int, int)[] Three = { (80, 80, 15), (200, 150, 15), (320, 220, 15) };

    [Fact]
    public void CountsAndMeasuresDiscs()
    {
        var r = Run(new BlobToolConfig { Name = "Caps" }, Page(210, Three));
        Assert.True(r.Pass);
        Assert.Equal("3 found", r.Summary);
        Assert.Equal(3, r.Blobs!.Count);
        Assert.Equal(3, r.Values["count"]);
        Assert.All(r.Blobs, b => Assert.InRange(b.Area, 680, 740));        // pi 15^2 = 707 px
        Assert.All(r.Blobs, b => Assert.InRange(b.Circularity, 0.9f, 1f));
        Assert.Equal(80, r.Blobs[0].Centroid.X, 0.3);
        Assert.Equal(3, r.Graphics.Count(g => g.Kind == GraphicKind.Ok));
    }

    [Fact]
    public void CountRangeFailsTooFewAndTooMany()
    {
        var few = Run(new BlobToolConfig { Name = "Caps", MinCount = 4 }, Page(210, Three));
        Assert.False(few.Pass);
        Assert.Equal("explain.blob.too_few", few.Explain);
        Assert.Equal("Found 3, at least 4 needed", few.Summary);

        var many = Run(new BlobToolConfig { Name = "Caps", MaxCount = 2 }, Page(210, Three));
        Assert.False(many.Pass);
        Assert.Equal("explain.blob.too_many", many.Explain);
        Assert.Equal(1, many.Graphics.Count(g => g.Kind == GraphicKind.Fail));   // the extra one is red

        Assert.True(Run(new BlobToolConfig { Name = "Caps", MinCount = 3, MaxCount = 3 }, Page(210, Three)).Pass);
    }

    [Fact]
    public void PresenceRules()
    {
        var clean = Page(210);
        var spot = Page(210, (200, 150, 6));
        Assert.True(Run(new BlobToolConfig { Name = "Dirt", Presence = BlobPresence.Absent }, clean).Pass);
        var dirty = Run(new BlobToolConfig { Name = "Dirt", Presence = BlobPresence.Absent }, spot);
        Assert.False(dirty.Pass);
        Assert.Equal("explain.blob.present", dirty.Explain);

        Assert.True(Run(new BlobToolConfig { Name = "Glue", Presence = BlobPresence.Present }, spot).Pass);
        var missing = Run(new BlobToolConfig { Name = "Glue", Presence = BlobPresence.Present }, clean);
        Assert.False(missing.Pass);
        Assert.Equal("explain.blob.none", missing.Explain);
    }

    [Fact]
    public void AreaRulesFailThePart()
    {
        var img = Page(210, (80, 80, 15), (200, 150, 25), (320, 220, 15));
        var one = Run(new BlobToolConfig { Name = "Dots", BlobAreaMin = 600, BlobAreaMax = 800 }, img);
        Assert.False(one.Pass);
        Assert.Equal("explain.blob.area", one.Explain);
        Assert.StartsWith("Spot 2 has area", one.Summary);
        Assert.Single(one.Graphics, g => g.Kind == GraphicKind.Fail);

        var total = Run(new BlobToolConfig { Name = "Dots", TotalAreaMax = 2000 }, img);
        Assert.False(total.Pass);
        Assert.Equal("explain.blob.total_area", total.Explain);
        Assert.True(Run(new BlobToolConfig { Name = "Dots", TotalAreaMin = 3000, TotalAreaMax = 4000 }, img).Pass);
    }

    [Fact]
    public void FiltersLeaveBlobsOutWithoutFailing()
    {
        var img = Page(210, (80, 80, 15), (200, 150, 3), (320, 220, 15));
        var r = Run(new BlobToolConfig { Name = "Caps", MinArea = 100, MinCount = 2, MaxCount = 2 }, img);
        Assert.True(r.Pass);
        Assert.Contains(r.Graphics, g => g.Kind == GraphicKind.Info && g.Label == "small");
    }

    [Fact]
    public void DarkFrameIsNeverNoSpots()
    {
        var black = new GrayImage(new byte[W * H], W, H);
        var r = Run(new BlobToolConfig { Name = "Dirt", Presence = BlobPresence.Absent }, black);
        Assert.False(r.Pass);
        Assert.Equal("explain.blob.image_dark", r.Explain);
    }

    [Fact]
    public void WhiteFrameIsNeverNoSpots()
    {
        var white = Page(255);
        var r = Run(new BlobToolConfig { Name = "Dirt", Presence = BlobPresence.Absent }, white);
        Assert.False(r.Pass);
        Assert.Equal("explain.blob.image_bright", r.Explain);
    }

    [Fact]
    public void SmearAcrossTheEdgeFailsNoneAllowed()
    {
        // a spot cut by the area's edge is left out of the count, but not out of "none allowed" or the highest count
        var img = Page(210, (100, 150, 20));
        var cfg = new BlobToolConfig { Name = "Dirt", Roi = new RoiBox(100, 50, 200, 200), Presence = BlobPresence.Absent };
        var r = Run(cfg, img);
        Assert.False(r.Pass);
        Assert.Equal("explain.blob.present", r.Explain);
        Assert.Contains(r.Graphics, g => g.Kind == GraphicKind.Fail && g.Label == "edge");
        Assert.False(Run(new BlobToolConfig { Name = "Caps", Roi = new RoiBox(100, 50, 200, 200), MaxCount = 0 }, img).Pass);
        // without an upper limit the edge spot is only left out
        Assert.True(Run(new BlobToolConfig { Name = "Caps", Roi = new RoiBox(100, 50, 200, 200) }, img).Pass);
    }

    [Fact]
    public void AreaCutByThePictureIsNotChecked()
    {
        var cfg = new BlobToolConfig { Name = "Dirt", Roi = new RoiBox(300, 200, 200, 200), Presence = BlobPresence.Absent };
        var r = Run(cfg, Page(210));
        Assert.False(r.Pass);
        Assert.Equal("explain.blob.region", r.Explain);
        // moved by the part off the picture
        var moved = Run(new BlobToolConfig { Name = "Dirt", Roi = new RoiBox(100, 100, 100, 100) }, Page(210), new Fixture(150, 150, 380, 150, 0));
        Assert.Equal("explain.blob.region", moved.Explain);
    }

    [Fact]
    public void TimeBudgetOverrunIsNotChecked()
    {
        // 1 875 spots and a 1 ms budget: the run stops, and the part is NG, not "fewer found"
        var px = new byte[W * H];
        Array.Fill(px, (byte)210);
        for (int y = 4; y < H - 4; y += 8)
            for (int x = 4; x < W - 4; x += 8)
                for (int dy = 0; dy < 4; dy++)
                    for (int dx = 0; dx < 4; dx++) px[(y + dy) * W + x + dx] = 40;
        var r = Run(new BlobToolConfig { Name = "Dust", MinArea = 1, Presence = BlobPresence.Absent, TimeBudgetMs = 1 }, new GrayImage(px, W, H));
        Assert.False(r.Pass);
        Assert.Contains(r.Explain, new[] { "explain.blob.incomplete", "explain.blob.present" });
    }

    [Fact]
    public void AreaFollowsTheFixture()
    {
        // taught: the spot at (100, 100) inside the area; the part moved 150 px right
        var cfg = new BlobToolConfig { Name = "Hole", Roi = new RoiBox(70, 70, 60, 60), MinCount = 1, MaxCount = 1 };
        var moved = Page(210, (250, 100, 10));
        Assert.False(Run(cfg, moved).Pass);
        var fx = new Fixture(100, 100, 250, 100, 0);
        var r = Run(cfg, moved, fx);
        Assert.True(r.Pass);
        Assert.Equal(250, r.Blobs![0].Centroid.X, 0.3);
    }

    [Fact]
    public void JobFileKeepsBlobFields()
    {
        var job = new Job
        {
            Name = "Caps",
            Tools =
            {
                new BlobToolConfig
                {
                    Name = "Caps", Roi = new RoiBox(10, 20, 300, 200), Threshold = BlobThreshold.Local, Polarity = BlobPolarity.LightOnDark,
                    LocalWindow = 51, MinArea = 50, MaxCircularity = 0.9f, SortBy = BlobSort.Area, SortDescending = true,
                    Presence = BlobPresence.Present, MinCount = 6, MaxCount = 6, BlobAreaMin = 400,
                },
            },
        };
        job.Validate();
        var text = JsonSerializer.Serialize(job, JobStore.Json);
        Assert.Contains("\"kind\": \"blob\"", text);
        Assert.Contains("\"Threshold\": \"Local\"", text);
        var b = Assert.IsType<BlobToolConfig>(JsonSerializer.Deserialize<Job>(text, JobStore.Json)!.Tools[0]);
        Assert.Equal((BlobThreshold.Local, BlobPolarity.LightOnDark, 51, 50f), (b.Threshold, b.Polarity, b.LocalWindow, b.MinArea));
        Assert.Equal((BlobSort.Area, true, BlobPresence.Present, 6, 6, 400f), (b.SortBy, b.SortDescending, b.Presence, b.MinCount, b.MaxCount, b.BlobAreaMin));
    }

    [Theory]
    [InlineData("band")]
    [InlineData("absent-min")]
    [InlineData("counts")]
    [InlineData("keep")]
    [InlineData("keep-max")]
    public void ValidateRefusesToolsThatCannotPass(string what)
    {
        var t = new BlobToolConfig { Name = "Spots" };
        switch (what)
        {
            case "band": t.Threshold = BlobThreshold.Band; t.Level = 150; t.LevelHigh = 100; break;
            case "absent-min": t.Presence = BlobPresence.Absent; t.MinCount = 2; break;
            case "counts": t.MinCount = 5; t.MaxCount = 3; break;
            case "keep": t.KeepFirst = 2; t.MinCount = 3; break;
            case "keep-max": t.KeepFirst = 6; t.MaxCount = 6; break;
        }
        var job = new Job { Name = "J", Tools = { t } };
        Assert.Throws<ArgumentException>(() => job.Validate());
        var r = Run(t, Page(210, Three));
        Assert.False(r.Pass);
        Assert.Equal("explain.blob.setup", r.Explain);
    }

    // a 120 x 60 bar with a notch, centre (cx, cy), turned deg counter-clockwise on screen; a bite of radius biteR
    // at (bx, by) in the bar's own frame
    private static GrayImage Bar(float cx, float cy, float deg, float bx = 0, float by = 0, float biteR = 0)
    {
        var px = new byte[W * H];
        double a = -deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        // 4 x 4 samples per pixel: grey from the share inside, so the edge is anti-aliased
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        double dx = x - 0.375 + sx * 0.25 - cx, dy = y - 0.375 + sy * 0.25 - cy;
                        double u = c * dx + s * dy, v = -s * dx + c * dy;
                        bool bar = Math.Abs(u) <= 60 && Math.Abs(v) <= 30 && !(u >= -20 && u <= 10 && v >= 10);
                        if (bar && !(biteR > 0 && (u - bx) * (u - bx) + (v - by) * (v - by) <= biteR * biteR)) inside++;
                    }
                px[y * W + x] = (byte)Math.Round(210 - 170 * inside / 16.0);
            }
        return new GrayImage(px, W, H);
    }

    [Fact]
    public void OutlineIsMeasuredWhenChecked()
    {
        var off = Run(new BlobToolConfig { Name = "Caps" }, Page(210, Three));
        Assert.All(off.Blobs!, b => Assert.Null(b.Contour));
        var r = Run(new BlobToolConfig { TimeBudgetMs = 2000, Name = "Caps", CheckOutline = true }, Page(210, Three));
        Assert.True(r.Pass);
        Assert.All(r.Blobs!, b =>
        {
            var c = b.Contour!;
            Assert.InRange(c.Length, 2 * Math.PI * 15 - 3, 2 * Math.PI * 15 + 3);
            Assert.InRange(c.Convexity, 0.97f, 1f);
            Assert.True(c.Closed);
            Assert.InRange(c.EdgeShare, 0.95f, 1f);
            Assert.Empty(c.Defects);
            Assert.Null(c.MaxDeviation);
            Assert.InRange(c.Outline.Length, 8, 200);      // simplified for display
        });
        Assert.Equal(1, r.Values["min_convexity"], 0.03);
    }

    [Fact]
    public void DentFailsThePartAndIsMarked()
    {
        var cfg = new BlobToolConfig { TimeBudgetMs = 2000, Name = "Cap", CheckOutline = true, MaxDentDepth = 3, Presence = BlobPresence.Present };
        Assert.True(Run(cfg, Page(210, (200, 150, 40))).Pass);
        // a bite of radius 10 centred 4 px inside the rim: ~14 px deep
        var img = Page(210, (200, 150, 40));
        for (int y = 140; y <= 160; y++)
            for (int x = 226; x <= 246; x++)
                if ((x - 236) * (x - 236) + (y - 150) * (y - 150) <= 100) img.Pixels[y * W + x] = 210;
        var dented = Run(cfg, img);
        Assert.False(dented.Pass);
        Assert.InRange(dented.Values["max_dent"], 12, 16);
        Assert.Equal("explain.contour.dent", dented.Explain);
        Assert.StartsWith("Spot 1: dent of", dented.Summary);
        Assert.Contains(dented.Graphics, g => g.Kind == GraphicKind.Fail && g.Label == "dent");
    }

    [Fact]
    public void TaughtOutlineCatchesADentAnywhere()
    {
        // the bar's own notch is 20 px deep, so a dent rule cannot be used; the taught outline can
        using var teacher = new BlobTool(new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true });
        var taught = teacher.TeachOutline(Bar(200, 150, 0));
        Assert.NotNull(taught);
        Assert.True(taught!.Length > 400);
        var cfg = new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true, TaughtOutline = taught, MaxDeviation = 1.5f, MinCount = 1, MaxCount = 1 };
        var turned = Run(cfg, Bar(190.4f, 140.7f, 35));
        Assert.True(turned.Pass, turned.Summary);
        Assert.InRange(turned.Blobs![0].Contour!.PoseAngleDeg!.Value, 34.5f, 35.5f);
        Assert.InRange(turned.Values["max_deviation"], 0, 1);
        var dented = Run(cfg, Bar(190.4f, 140.7f, 35, 64, 0, 9));
        Assert.False(dented.Pass);
        Assert.Equal("explain.contour.deviation", dented.Explain);
        Assert.InRange(dented.Values["max_deviation"], 4, 6);
        Assert.Contains(dented.Graphics, g => g.Kind == GraphicKind.Fail && g.Label == "deviation");
    }

    [Fact]
    public void ADentBelowTheListingDepthStillMeetsTheDentRule()
    {
        // indents under MinDefectDepth are not listed, but the dent rule sees the deepest one whatever its depth
        var img = Page(210, (200, 150, 40));
        for (int y = 145; y <= 155; y++)
            for (int x = 236; x <= 246; x++)
                if ((x - 241) * (x - 241) + (y - 150) * (y - 150) <= 9) img.Pixels[y * W + x] = 210;   // ~2 px bite
        var cfg = new BlobToolConfig { TimeBudgetMs = 2000, Name = "Cap", CheckOutline = true, MinDefectDepth = 5, MaxDentDepth = 1, Presence = BlobPresence.Present };
        var r = Run(cfg, img);
        Assert.False(r.Pass, $"max dent {r.Values.GetValueOrDefault("max_dent")}, {r.Summary}");
        Assert.Equal("explain.contour.dent", r.Explain);
        Assert.Empty(r.Blobs![0].Contour!.Defects);
    }

    [Fact]
    public void TeachRefusesABrokenOutline()
    {
        using var t = new BlobTool(new BlobToolConfig { Name = "Lid", Roi = new RoiBox(100, 50, 200, 200), ExcludeEdge = false });
        Assert.Null(t.TeachOutline(Page(210, (100, 150, 20))));                 // cut by the area's edge
        Assert.Null(t.TeachOutline(new GrayImage(new byte[W * H], W, H)));     // dark frame
        Assert.NotNull(t.TeachOutline(Page(210, (200, 150, 20))));
    }

    [Fact]
    public void ConvexityRule()
    {
        var cfg = new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true, MinConvexity = 0.95f, Presence = BlobPresence.Present };
        var r = Run(cfg, Bar(200, 150, 0));     // the notch takes 600 of 7 800 px of the hull
        Assert.False(r.Pass);
        Assert.Equal("explain.contour.convexity", r.Explain);
        Assert.True(Run(cfg, Page(210, Three)).Pass);
    }

    [Fact]
    public void OutlineCutByTheAreaIsNotChecked()
    {
        var cfg = new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", Roi = new RoiBox(100, 50, 200, 200), ExcludeEdge = false, CheckOutline = true, MaxDentDepth = 3, Presence = BlobPresence.Present };
        var r = Run(cfg, Page(210, (100, 150, 20)));
        Assert.False(r.Pass);
        Assert.Equal("explain.contour.not_closed", r.Explain);
        // left out for the edge (the default), next to a good part: still not checked, never passed
        cfg.ExcludeEdge = true;
        var left = Run(cfg, Page(210, (100, 150, 20), (200, 150, 20)));
        Assert.False(left.Pass);
        Assert.Equal("explain.contour.not_closed", left.Explain);
        Assert.Contains(left.Graphics, g => g.Kind == GraphicKind.Fail && g.Label == "edge");
        // a level no grey crosses: no edge found, so not checked either
        var weak = Run(new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true, EdgeLevel = 250, MaxDentDepth = 3, Presence = BlobPresence.Present }, Page(210, Three));
        Assert.Equal("explain.contour.not_closed", weak.Explain);
        Assert.False(weak.Values.ContainsKey("max_deviation"));
    }

    [Theory]
    [InlineData("rules-off")]
    [InlineData("deviation-untaught")]
    [InlineData("taught-no-limit")]
    [InlineData("bad-outline")]
    [InlineData("keep-outline")]
    [InlineData("outline-no-part")]
    public void ValidateRefusesOutlineSetups(string what)
    {
        var t = new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true, Presence = BlobPresence.Present };
        switch (what)
        {
            case "rules-off": t.CheckOutline = false; t.MaxDentDepth = 2; break;
            case "deviation-untaught": t.MaxDeviation = 1; break;
            case "taught-no-limit": t.TaughtOutline = new float[] { 0, 0, 10, 0, 10, 10 }; break;
            case "bad-outline": t.TaughtOutline = new float[] { 0, 0, 10, 0, 10 }; t.MaxDeviation = 1; break;
            case "keep-outline": t.KeepFirst = 2; t.MinConvexity = 0.9f; break;
            case "outline-no-part": t.Presence = BlobPresence.Any; t.MaxDentDepth = 2; break;
        }
        Assert.Throws<ArgumentException>(() => new Job { Name = "J", Tools = { t } }.Validate());
        Assert.Equal("explain.blob.setup", Run(t, Page(210, Three)).Explain);
    }

    [Fact]
    public void JobFileKeepsOutlineFields()
    {
        var job = new Job
        {
            Name = "Lid",
            Tools =
            {
                new BlobToolConfig
                {
                    Name = "Lid", CheckOutline = true, EdgeLevel = 120, OutlineSmoothing = 1.5f, MinDefectDepth = 0.5f, MaxDentDepth = 2,
                    MinConvexity = 0.9f, TaughtOutline = new float[] { -5, -5, 5, -5, 5, 5, -5, 5 }, MaxDeviation = 1.2f, MinEdgeShare = 0.8f,
                    Presence = BlobPresence.Present,
                },
            },
        };
        job.Validate();
        var b = Assert.IsType<BlobToolConfig>(JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, JobStore.Json), JobStore.Json)!.Tools[0]);
        Assert.Equal((true, 120, 1.5f, 0.5f, 2f, 0.9f), (b.CheckOutline, b.EdgeLevel, b.OutlineSmoothing, b.MinDefectDepth, b.MaxDentDepth, b.MinConvexity));
        Assert.Equal((1.2f, 0.8f), (b.MaxDeviation, b.MinEdgeShare));
        Assert.Equal(new float[] { -5, -5, 5, -5, 5, 5, -5, 5 }, b.TaughtOutline);
    }

    private static readonly BarcodeReading.LensCalibration Lens = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0.05f, -1);

    private static GrayImage Dented()
    {
        var img = Page(210, (200, 150, 40));
        for (int y = 140; y <= 160; y++)
            for (int x = 226; x <= 246; x++)
                if ((x - 236) * (x - 236) + (y - 150) * (y - 150) <= 100) img.Pixels[y * W + x] = 210;   // ~14 px bite
        return img;
    }

    [Fact]
    public void OutlineValuesAndLimitsAreInMmWithACalibration()
    {
        // 0.05 mm/px: the ~14 px dent is ~0.7 mm; a 0.5 mm limit fails it, a 1 mm limit passes it
        var cfg = new BlobToolConfig { TimeBudgetMs = 2000, Name = "Cap", CheckOutline = true, OutlineUnit = BlobOutlineUnit.Mm, MaxDentDepth = 0.5f, Presence = BlobPresence.Present };
        using var t = new BlobTool(cfg, Lens);
        var r = t.Run(Dented(), new BatchFields());
        Assert.False(r.Pass);
        Assert.Equal("explain.contour.dent", r.Explain);
        Assert.InRange(r.Values["max_dent"], 0.6, 0.8);
        Assert.Contains("mm", r.Summary);
        Assert.Contains("outline values in mm", r.Details);

        cfg.MaxDentDepth = 1;
        using var ok = new BlobTool(cfg, Lens);
        Assert.True(ok.Run(Dented(), new BatchFields()).Pass);
    }

    [Fact]
    public void OutlineInPxSaysSoWithoutACalibration()
    {
        var r = Run(new BlobToolConfig { TimeBudgetMs = 2000, Name = "Cap", CheckOutline = true }, Page(210, (200, 150, 40)));
        Assert.Contains(r.Details, d => d.Contains("outline values in px") && d.Contains("no real size"));
        Assert.InRange(r.Values["max_dent"], 0, 3);
    }

    [Fact]
    public void MmWithoutACalibrationIsRefused()
    {
        var cfg = new BlobToolConfig { TimeBudgetMs = 2000, Name = "Cap", CheckOutline = true, OutlineUnit = BlobOutlineUnit.Mm, MaxDentDepth = 1, Presence = BlobPresence.Present };
        Assert.NotNull(BlobTool.SetupProblem(cfg));
        Assert.Null(BlobTool.SetupProblem(cfg, Lens));
        Assert.Equal("explain.blob.setup", Run(cfg, Page(210, (200, 150, 40))).Explain);
        Assert.Throws<ArgumentException>(() => new Job { Name = "J", Tools = { cfg } }.Validate());
    }

    [Fact]
    public void AutoToleranceFromGoodImagesCatchesADent()
    {
        using var teacher = new BlobTool(new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true });
        var taught = teacher.TeachOutline(Bar(200, 150, 0));
        var cfg = new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true, TaughtOutline = taught, MinCount = 1, MaxCount = 1 };
        var good = new[] { Bar(200, 150, 0), Bar(190.4f, 140.7f, 35), Bar(205.3f, 155.2f, -20) };
        var tol = BlobTool.AutoTolerance(cfg, good);
        Assert.NotNull(tol);
        Assert.InRange(tol!.MaxDeviation, 0.3, 2);
        Assert.True(tol.MaxDeviation >= tol.WorstDeviation);
        // in mm the same suggestion is the px one times the scale
        var mm = BlobTool.AutoTolerance(new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true, TaughtOutline = taught, MinCount = 1, MaxCount = 1, OutlineUnit = BlobOutlineUnit.Mm }, good, Lens);
        Assert.Equal(tol.MaxDeviation * 0.05f, mm!.MaxDeviation, 4);
        // the suggestion passes the good parts and fails a dented one
        cfg.MaxDeviation = tol.MaxDeviation;
        Assert.All(good, g => Assert.True(Run(cfg, g).Pass));
        Assert.False(Run(cfg, Bar(190.4f, 140.7f, 35, 64, 0, 9)).Pass);
    }

    [Fact]
    public void AutoToleranceSuggestsNothingFromBadInput()
    {
        using var teacher = new BlobTool(new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true });
        var cfg = new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true, TaughtOutline = teacher.TeachOutline(Bar(200, 150, 0)), MinCount = 1, MaxCount = 1 };
        var good = new[] { Bar(200, 150, 0), Bar(190.4f, 140.7f, 35), Bar(205.3f, 155.2f, -20) };
        Assert.Null(BlobTool.AutoTolerance(cfg, good.Take(2).ToArray()));                                  // too few
        Assert.Null(BlobTool.AutoTolerance(cfg, good.Append(new GrayImage(new byte[W * H], W, H)).ToArray())); // a dark frame
        Assert.Null(BlobTool.AutoTolerance(cfg, good.Append(Page(210)).ToArray()));                         // no part
        Assert.Null(BlobTool.AutoTolerance(new BlobToolConfig { TimeBudgetMs = 2000, Name = "Lid", CheckOutline = true }, good)); // nothing taught
    }

    [Fact]
    public void SimplifyKeepsCornersOnly()
    {
        var square = new List<PointF>();
        for (int k = 0; k < 40; k++) square.Add(new PointF(k, 0));
        for (int k = 0; k < 40; k++) square.Add(new PointF(40, k));
        for (int k = 0; k < 40; k++) square.Add(new PointF(40 - k, 40));
        for (int k = 0; k < 40; k++) square.Add(new PointF(0, 40 - k));
        Assert.Equal(4, BlobTool.Simplify(square.ToArray(), 0.25f).Length);
    }

    [Fact]
    public void JobRunnerRunsTheBlobTool()
    {
        var job = new Job { Name = "J", Tools = { new BlobToolConfig { Name = "Caps", MinCount = 3, MaxCount = 3 } } };
        using var runner = new JobRunner(job, new ModelPaths(""));
        var r = runner.Run(0, Page(210, Three), new BatchFields());
        Assert.True(r.Pass);
        Assert.Equal(3, r.Values["Caps.count"]);
    }
}
