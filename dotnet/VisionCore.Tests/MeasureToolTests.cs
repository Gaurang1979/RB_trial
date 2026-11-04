using System.Text.Json;
using VisionCore;
using Xunit;

namespace VisionCore.Tests;

// The Measure tool on drawn parts whose sizes are known to a fraction of a pixel (each pixel is the share of
// its area inside the shape, as a camera sees an edge). Tool coordinates put a pixel's centre on whole numbers.
public class MeasureToolTests
{
    const byte Dark = 40, Bright = 200;

    static GrayImage Draw(int w, int h, Func<double, double, bool> inside, byte inVal = Bright, byte outVal = Dark)
    {
        const int S = 8;
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int n = 0;
                for (int j = 0; j < S; j++)
                    for (int i = 0; i < S; i++)
                        if (inside(x + (i + 0.5) / S - 0.5, y + (j + 0.5) / S - 0.5)) n++;
                px[y * w + x] = (byte)Math.Round(outVal + (inVal - outVal) * n / (double)(S * S));
            }
        return new GrayImage(px, w, h);
    }

    // a bright plate: a rectangle (half sizes hw, hh) round (cx, cy) turned deg counter-clockwise, with a dark hole of radius r at its centre
    static GrayImage Plate(double cx, double cy, double deg, double hw = 60.25, double hh = 40.4, double r = 15.3) =>
        Draw(320, 240, (x, y) =>
        {
            double a = deg * Math.PI / 180, dx = x - cx, dy = y - cy;
            double s = dx * Math.Cos(a) - dy * Math.Sin(a), t = dx * Math.Sin(a) + dy * Math.Cos(a); // part coordinates
            return Math.Abs(s) <= hw && Math.Abs(t) <= hh && s * s + t * t >= r * r;
        });

    static MeasureToolConfig PlateTool(params MeasureCheck[] checks) => new()
    {
        Name = "Size",
        Features =
        {
            new CaliperFeature { Name = "width", Box = new RotBox(160, 145, 160, 10, 0), Mode = CaliperMode.Pair, Polarity = EdgePolarity.DarkToLight },
            new LineFeature { Name = "top", X0 = 110, Y0 = 80, X1 = 210, Y1 = 80, Polarity = EdgePolarity.DarkToLight },
            new LineFeature { Name = "left", X0 = 100, Y0 = 150, X1 = 100, Y1 = 90, Polarity = EdgePolarity.DarkToLight, Calipers = 8 },
            new CircleFeature { Name = "hole", Cx = 160, Cy = 120, RInner = 8, ROuter = 24, Polarity = EdgePolarity.DarkToLight },
        },
        Checks = checks.ToList(),
    };

    static MeasureCheck Check(string name, MeasureValue v, string f, string? f2 = null, MeasureUnit u = MeasureUnit.Px, double? min = null, double? max = null) =>
        new() { Name = name, Value = v, Feature = f, Feature2 = f2, Unit = u, Min = min, Max = max };

    [Fact]
    public void MeasuresWidthLineHoleAndGeometryToAFractionOfAPixel()
    {
        var cfg = PlateTool(
            Check("w", MeasureValue.Width, "width"),
            Check("topAngle", MeasureValue.Angle, "top", u: MeasureUnit.Deg),
            Check("dia", MeasureValue.Diameter, "hole"),
            Check("round", MeasureValue.Roundness, "hole"),
            Check("holeToTop", MeasureValue.Distance, "hole", "top"),
            Check("corner", MeasureValue.AngleBetween, "top", "left", MeasureUnit.Deg),
            Check("straight", MeasureValue.Straightness, "top"));
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(Plate(160.2, 120.1, 0), new BatchFields());
        Assert.True(r.Pass, r.Summary + " | " + string.Join(" | ", r.Details));
        Assert.Equal(120.5, r.Values["w"], 0.05);
        Assert.Equal(0, r.Values["topAngle"], 0.02);
        Assert.Equal(30.6, r.Values["dia"], 0.1);
        Assert.True(r.Values["round"] < 0.2);
        // the top edge lies at y 79.7, but 8 x 8 drawing samples put it at 79.75 (6 of 8 rows): 40.35 is the drawn distance
        Assert.Equal(40.35, r.Values["holeToTop"], 0.02);
        Assert.Equal(90, r.Values["corner"], 0.05);
        Assert.True(r.Values["straight"] < 0.1);
        Assert.Null(r.Explain);
    }

    [Fact]
    public void OutOfToleranceFailsWithTheValueAndAReason()
    {
        using var tool = new MeasureTool(PlateTool(Check("w", MeasureValue.Width, "width", min: 118, max: 120)));
        var r = tool.Run(Plate(160, 120, 0), new BatchFields());
        Assert.False(r.Pass);
        Assert.Equal("explain.caliper.out_of_tolerance", r.Explain);
        Assert.Contains("allowed 118–120 px", r.Summary);
        Assert.Equal(120.5, r.Values["w"], 0.05); // still reported for the trend

        using var dia = new MeasureTool(PlateTool(Check("d", MeasureValue.Diameter, "hole", max: 30)));
        Assert.Equal("explain.circle.diameter", dia.Run(Plate(160, 120, 0), new BatchFields()).Explain);
    }

    [Fact]
    public void ADentLeftOutOfTheFitStillFailsStraightness()
    {
        // a 4 px deep notch, 18 px wide, in the plate's top edge: two calipers of ten land in it
        var img = Draw(320, 240, (x, y) => x >= 99.75 && x <= 220.25 && y >= 79.6 && y <= 160.6 && !(x > 150 && x < 168 && y < 83.6));
        var cfg = PlateTool(Check("straight", MeasureValue.Straightness, "top", max: 1), Check("a", MeasureValue.Angle, "top", u: MeasureUnit.Deg, min: -1, max: 1));
        cfg.Features.RemoveAll(f => f.Name is "hole" or "width");
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(img, new BatchFields());
        Assert.False(r.Pass, r.Summary);
        Assert.Equal("explain.line.straightness", r.Explain);
        Assert.True(r.Values["straight"] > 3.5);
        Assert.Equal(0, r.Values["a"], 0.02); // the fit itself ignores the notch
    }

    [Fact]
    public void AnAngleCheckRound180DegreesWorks()
    {
        // the top edge drawn right to left points at 180°
        var cfg = new MeasureToolConfig
        {
            Name = "Size",
            Features = { new LineFeature { Name = "top", X0 = 210, Y0 = 80, X1 = 110, Y1 = 80, Polarity = EdgePolarity.LightToDark } },
            Checks = { Check("a", MeasureValue.Angle, "top", u: MeasureUnit.Deg, min: 179, max: 181) },
        };
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(Plate(160, 120, 0.3), new BatchFields());
        Assert.True(r.Pass, r.Summary + " | " + string.Join(" | ", r.Details));
        Assert.Equal(180.3, r.Values["a"], 0.03);
        using var down = new MeasureTool(cfg);
        Assert.Equal(179.7, down.Run(Plate(160, 120, -0.3), new BatchFields()).Values["a"], 0.03);
    }

    [Fact]
    public void SizesInMmUseTheCameraCalibration()
    {
        var lens = new BarcodeReading.LensCalibration(0, 0, 0, 0, 0, 0, 0, 0, 0, 0.05f, -1);
        using var tool = new MeasureTool(PlateTool(Check("w", MeasureValue.Width, "width", u: MeasureUnit.Mm, min: 6, max: 6.1)), lens);
        var r = tool.Run(Plate(160, 120, 0), new BatchFields());
        Assert.True(r.Pass, r.Summary);
        Assert.Equal(120.5 * 0.05, r.Values["w"], 0.003);
    }

    [Fact]
    public void MmWithoutCalibrationIsRefusedAtSetup()
    {
        var cfg = PlateTool(Check("w", MeasureValue.Width, "width", u: MeasureUnit.Mm));
        Assert.Contains("calibrate", MeasureTool.SetupProblem(cfg, null));
        var job = new Job { Name = "j", Tools = { cfg } };
        Assert.Throws<ArgumentException>(() => job.Validate());
        job.Cameras[0].Calibration = new BarcodeReading.LensCalibration(0, 0, 0, 0, 0, 0, 0, 0, 0, 0.05f, -1);
        job.Validate();
    }

    [Theory]
    [InlineData(MeasureValue.Width, "top", MeasureUnit.Px)]          // a width is a caliper pair's
    [InlineData(MeasureValue.Angle, "top", MeasureUnit.Px)]          // an angle is in degrees
    [InlineData(MeasureValue.Diameter, "width", MeasureUnit.Px)]     // a diameter is a circle's
    [InlineData(MeasureValue.AngleBetween, "hole", MeasureUnit.Deg)] // between two lines
    public void ChecksThatDoNotFitTheirFeatureAreRefused(MeasureValue v, string feature, MeasureUnit u)
    {
        var cfg = PlateTool(Check("x", v, feature, v == MeasureValue.AngleBetween ? "top" : null, u));
        Assert.NotNull(MeasureTool.SetupProblem(cfg, null));
    }

    [Fact]
    public void AMissingEdgeOrHoleFails()
    {
        using var tool = new MeasureTool(PlateTool(Check("d", MeasureValue.Diameter, "hole")));
        var noHole = Plate(160, 120, 0, r: 0);
        var r = tool.Run(noHole, new BatchFields());
        Assert.False(r.Pass);
        Assert.Equal("explain.circle.not_found", r.Explain);
        Assert.False(r.Values.ContainsKey("d"));

        var flat = new GrayImage(Enumerable.Repeat((byte)120, 320 * 240).ToArray(), 320, 240);
        var f = tool.Run(flat, new BatchFields());
        Assert.False(f.Pass);
        Assert.Equal("explain.caliper.no_edge", f.Explain);
    }

    [Fact]
    public void AFaintEdgeIsReportedAsFaint()
    {
        var cfg = new MeasureToolConfig
        {
            Name = "Edge",
            Features = { new CaliperFeature { Name = "e", Box = new RotBox(50, 30, 40, 10, 0), Threshold = 30 } },
        };
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(Draw(100, 60, (x, _) => x >= 50.3, 135, 120), new BatchFields()); // a 15-grey step
        Assert.False(r.Pass);
        Assert.Equal("explain.caliper.weak_edge", r.Explain);
    }

    [Fact]
    public void ABoxOffTheImageFails()
    {
        var cfg = new MeasureToolConfig { Name = "Edge", Features = { new CaliperFeature { Name = "e", Box = new RotBox(5, 30, 40, 10, 0) } } };
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(Plate(160, 120, 0), new BatchFields());
        Assert.False(r.Pass);
        Assert.Equal("explain.measure.off_image", r.Explain);
    }

    [Fact]
    public void FeaturesFollowThePartWithTheFixture()
    {
        // taught on the plate at (160, 120) upright; now it is at (171.4, 113.2) turned 12 degrees
        var cfg = PlateTool(
            Check("w", MeasureValue.Width, "width"),
            Check("topAngle", MeasureValue.Angle, "top", u: MeasureUnit.Deg),
            Check("dia", MeasureValue.Diameter, "hole"),
            Check("holeToTop", MeasureValue.Distance, "hole", "top"));
        using var tool = new MeasureTool(cfg);
        var fx = new Fixture(160, 120, 171.4f, 113.2f, 12);
        var r = tool.Run(Plate(171.4, 113.2, 12), new BatchFields(), fx);
        Assert.True(r.Pass, r.Summary + " | " + string.Join(" | ", r.Details));
        Assert.Equal(120.5, r.Values["w"], 0.06);
        Assert.Equal(0, r.Values["topAngle"], 0.03); // against the part
        Assert.Equal(30.6, r.Values["dia"], 0.1);
        Assert.Equal(40.4, r.Values["holeToTop"], 0.06);

        // without the fixture the turned part is not where the features were drawn
        var lost = tool.Run(Plate(171.4, 113.2, 12), new BatchFields());
        Assert.True(!lost.Pass || Math.Abs(lost.Values["topAngle"]) > 5);
    }

    [Fact]
    public void JobFileKeepsTheMeasureTool()
    {
        var job = new Job
        {
            Name = "m",
            Tools = { PlateTool(Check("w", MeasureValue.Width, "width", min: 120, max: 121), Check("c", MeasureValue.AngleBetween, "top", "left", MeasureUnit.Deg)) },
        };
        job.Cameras[0].Calibration = new BarcodeReading.LensCalibration(2400, 2400, 960, 540, -0.1f, 0.02f, 0, 0, 0, 0.05f, 0.3f);
        var text = JsonSerializer.Serialize(job, JobStore.Json);
        Assert.Contains("\"kind\": \"measure\"", text);
        Assert.Contains("\"kind\": \"circle\"", text);
        Assert.Contains("\"Value\": \"AngleBetween\"", text);
        var back = JsonSerializer.Deserialize<Job>(text, JobStore.Json)!;
        var m = Assert.IsType<MeasureToolConfig>(back.Tools[0]);
        Assert.Equal(4, m.Features.Count);
        var w = Assert.IsType<CaliperFeature>(m.Features[0]);
        Assert.Equal(CaliperMode.Pair, w.Mode);
        Assert.Equal(new RotBox(160, 145, 160, 10, 0), w.Box);
        Assert.Equal(121, m.Checks[0].Max);
        Assert.Equal(0.05f, back.Cameras[0].Calibration!.MmPerPx);
        back.Validate();
    }

    [Fact]
    public void TheJobRunnerRunsIt()
    {
        var job = new Job { Name = "m", Tools = { PlateTool(Check("w", MeasureValue.Width, "width", min: 120, max: 121)) } };
        using var runner = new JobRunner(job, new ModelPaths("."));
        var r = runner.Run(0, Plate(160, 120, 0), new BatchFields());
        Assert.True(r.Pass, r.FirstProblem);
        Assert.Equal(120.5, r.Values["Size.w"], 0.05);
    }

    static MeasureToolConfig Tool(MeasureFeature[] features, params MeasureCheck[] checks) =>
        new() { Name = "Size", Features = features.ToList(), Checks = checks.ToList() };

    static void AssertPass(ToolResult r) => Assert.True(r.Pass, r.Summary + " | " + string.Join(" | ", r.Details));

    // an oval (semi-axes a along deg counter-clockwise on the screen, b across it)
    static bool InOval(double x, double y, double cx, double cy, double a, double b, double deg)
    {
        double t = deg * Math.PI / 180, dx = x - cx, dy = y - cy;
        double s = dx * Math.Cos(t) - dy * Math.Sin(t), q = dx * Math.Sin(t) + dy * Math.Cos(t);
        return (s / a) * (s / a) + (q / b) * (q / b) < 1;
    }

    [Fact]
    public void AnOvalHoleGivesItsAxesAndRatio()
    {
        var img = Draw(220, 200, (x, y) => InOval(x, y, 110.3, 95.6, 45.2, 28.7, 25), Dark, Bright);
        var cfg = Tool(new MeasureFeature[] { new EllipseFeature { Name = "oval", Cx = 108, Cy = 97, RInner = 18, ROuter = 56, Polarity = EdgePolarity.DarkToLight } },
            Check("major", MeasureValue.Major, "oval"), Check("minor", MeasureValue.Minor, "oval"),
            Check("ratio", MeasureValue.AxisRatio, "oval", u: MeasureUnit.None, min: 0.6, max: 0.7));
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(img, new BatchFields());
        AssertPass(r);
        Assert.Equal(90.4, r.Values["major"], 0.15);
        Assert.Equal(57.4, r.Values["minor"], 0.15);
        Assert.Equal(28.7 / 45.2, r.Values["ratio"], 0.005);

        // a dent left out of the fit still shows in the roundness
        var dented = Draw(220, 200, (x, y) => InOval(x, y, 110.3, 95.6, 45.2, 28.7, 25) || Math.Sqrt((x - 151) * (x - 151) + (y - 76.5) * (y - 76.5)) < 5, Dark, Bright);
        var rc = Tool(new MeasureFeature[] { new EllipseFeature { Name = "oval", Cx = 108, Cy = 97, RInner = 18, ROuter = 56, Polarity = EdgePolarity.DarkToLight } },
            Check("round", MeasureValue.Roundness, "oval", max: 0.5));
        using (var rt = new MeasureTool(rc))
        {
            AssertPass(rt.Run(img, new BatchFields()));
            var d = rt.Run(dented, new BatchFields());
            Assert.False(d.Pass, d.Summary);
            Assert.Equal("explain.circle.out_of_round", d.Explain);
        }

        cfg.Checks[2].Min = 0.9; // a round hole was asked for
        using var round = new MeasureTool(cfg);
        Assert.Equal("explain.circle.out_of_round", round.Run(img, new BatchFields()).Explain);
        using var none = new MeasureTool(cfg);
        Assert.Equal("explain.ellipse.not_found", none.Run(Draw(220, 200, (_, _) => false), new BatchFields()).Explain);
    }

    [Fact]
    public void ACornerIsWhereTheTwoEdgesMeet()
    {
        var cfg = PlateTool(Check("angle", MeasureValue.CornerAngle, "corner", u: MeasureUnit.Deg, min: 89.9, max: 90.1),
            Check("toLines", MeasureValue.Distance, "corner", "meet"), Check("toHole", MeasureValue.Distance, "corner", "hole"));
        cfg.Features.Add(new CornerFeature
        {
            Name = "corner", Polarity = EdgePolarity.DarkToLight,
            Edge1 = new Segment(110, 80, 200, 80), Edge2 = new Segment(100, 150, 100, 90),
        });
        cfg.Features.Add(new ConstructFeature { Name = "meet", Operation = ConstructOp.Intersection, Inputs = { "top", "left" } });
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(Plate(160.2, 120.1, 0), new BatchFields());
        AssertPass(r);
        Assert.Equal(90, r.Values["angle"], 0.05);
        Assert.Equal(0, r.Values["toLines"], 0.05); // the corner tool and the two lines met agree
        Assert.Equal(Math.Sqrt(60.25 * 60.25 + 40.4 * 40.4), r.Values["toHole"], 0.2);

        // two edges along the same side: parallel, no corner
        cfg.Features[^2] = new CornerFeature { Name = "corner", Edge1 = new Segment(110, 80, 150, 80), Edge2 = new Segment(160, 80, 200, 80) };
        cfg.Checks.RemoveAll(k => k.Name != "angle");
        using var flat = new MeasureTool(cfg);
        var p = flat.Run(Plate(160.2, 120.1, 0), new BatchFields());
        Assert.False(p.Pass);
        Assert.Equal("explain.corner.not_found", p.Explain);
        Assert.Contains("parallel", p.Summary);
    }

    static GrayImage Slot(bool bridge) =>
        Draw(100, 110, (x, y) => !(x >= 40.3 + 0.02 * (y - 30) && x < 52.67) || bridge && y >= 67.5 && y < 71.5);

    [Fact]
    public void AGapIsMeasuredAtEverySlice()
    {
        var gap = new GapFeature { Name = "slot", Box = new RotBox(46, 50, 40, 60, 0), Samples = 20, Polarity = EdgePolarity.LightToDark };
        var cfg = Tool(new MeasureFeature[] { gap },
            Check("min", MeasureValue.MinWidth, "slot", min: 11.2), Check("max", MeasureValue.MaxWidth, "slot", max: 12.6), Check("mean", MeasureValue.MeanWidth, "slot"));
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(Slot(false), new BatchFields());
        AssertPass(r);
        // slices 3 px high from y 20: the narrowest is the last (y 78.5), the widest the first (y 21.5)
        Assert.Equal(12.37 - 0.02 * 48.5, r.Values["min"], 0.05);
        Assert.Equal(12.37 + 0.02 * 8.5, r.Values["max"], 0.05);
        Assert.Equal(12.37 - 0.02 * 20, r.Values["mean"], 0.05);

        cfg.Checks[0].Min = 11.5;
        using var narrow = new MeasureTool(cfg);
        Assert.Equal("explain.gap.narrow", narrow.Run(Slot(false), new BatchFields()).Explain);
        cfg.Checks[0].Min = null;
        cfg.Checks[1].Max = 12.4;
        using var wide = new MeasureTool(cfg);
        Assert.Equal("explain.gap.wide", wide.Run(Slot(false), new BatchFields()).Explain);

        // closed at one slice: no width there, and that fails whatever the limits
        var closed = tool.Run(Slot(true), new BatchFields());
        Assert.False(closed.Pass);
        Assert.Equal("explain.gap.narrow", closed.Explain);
        Assert.Contains("slice 17 of 20", closed.Summary); // pixel rows 68-71

        // a burr in the slot at 60 % of the slot's contrast: the slices across it have no open width, and fail
        var px = (byte[])Slot(false).Pixels.Clone();
        for (int y = 40; y < 47; y++) for (int x = 44; x < 47; x++) px[y * 100 + x] = (byte)(Dark + 0.6 * (Bright - Dark));
        var b = tool.Run(new GrayImage(px, 100, 110), new BatchFields());
        Assert.False(b.Pass, b.Summary);
        Assert.Equal("explain.gap.narrow", b.Explain);
    }

    // 12 bright pins on dark, pitch 10.25, 4.6 wide, from x 20.4; one left out, one moved, or a thin 13th before them
    static GrayImage Pins(int missing = -1, int moved = -1, double by = 0, bool extra = false) => Draw(160, 60, (x, y) =>
    {
        if (y < 15 || y >= 45) return false;
        if (extra && x >= 15.5 && x < 18) return true;
        for (int i = 0; i < 12; i++)
        {
            double l = 20.4 + i * 10.25 + (i == moved ? by : 0);
            if (i != missing && x >= l && x < l + 4.6) return true;
        }
        return false;
    });

    static MeasureToolConfig PinTool() => Tool(
        new MeasureFeature[] { new PitchFeature { Name = "pins", Box = new RotBox(77, 30, 130, 20, 0), ExpectedCount = 12, Polarity = EdgePolarity.DarkToLight } },
        Check("n", MeasureValue.Count, "pins", u: MeasureUnit.None), Check("pitch", MeasureValue.Pitch, "pins", min: 10, max: 10.5),
        Check("mean", MeasureValue.MeanPitch, "pins"));

    [Fact]
    public void PinsAreCountedAndTheirPitchChecked()
    {
        using var tool = new MeasureTool(PinTool());
        var r = tool.Run(Pins(), new BatchFields());
        AssertPass(r);
        Assert.Equal(12, r.Values["n"]);
        Assert.Equal(10.25, r.Values["mean"], 0.02);
        Assert.Equal(10.25, r.Values["pitch"], 0.05);

        var missing = tool.Run(Pins(missing: 7), new BatchFields());
        Assert.False(missing.Pass);
        Assert.Equal("explain.pitch.missing", missing.Explain);
        Assert.Contains("pin 8 is missing", missing.Summary);

        var bent = tool.Run(Pins(moved: 3, by: 0.8), new BatchFields());
        Assert.False(bent.Pass);
        Assert.Equal("explain.pitch.bent", bent.Explain);
        Assert.Matches(@"pin 4 is out of place by 0\.[67]", bent.Summary); // 0.8 less the straight run's own shift towards it
        Assert.Equal(11.05, bent.Values["pitch"], 0.05);

        var extra = tool.Run(Pins(extra: true), new BatchFields());
        Assert.False(extra.Pass);
        Assert.Equal("explain.pitch.count", extra.Explain);
    }

    [Fact]
    public void ThreadEdgesAreCounted()
    {
        var img = Draw(140, 40, (x, _) => x >= 20.3 && x < 20.3 + 15 * 6.4 && (x - 20.3) % 6.4 < 3.2);
        var edges = new EdgeCountFeature { Name = "thread", Box = new RotBox(68, 20, 116, 10, 0), ExpectedCount = 15, Polarity = EdgePolarity.DarkToLight };
        var cfg = Tool(new MeasureFeature[] { edges }, Check("pitch", MeasureValue.Pitch, "thread", min: 6.3, max: 6.5), Check("mean", MeasureValue.MeanPitch, "thread"));
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(img, new BatchFields());
        AssertPass(r);
        Assert.Equal(6.4, r.Values["mean"], 0.02);
        Assert.Equal(6.4, r.Values["pitch"], 0.1); // the worst spacing: two edges, each within 0.05 px

        edges.ExpectedCount = 14;
        using var wrong = new MeasureTool(cfg);
        Assert.Equal("explain.edges.count", wrong.Run(img, new BatchFields()).Explain);
        edges.ExpectedCount = 0;
        cfg.Checks[0].Max = 6.35;
        using var spacing = new MeasureTool(cfg);
        Assert.Equal("explain.edges.pitch", spacing.Run(img, new BatchFields()).Explain);
    }

    [Fact]
    public void AFineThreadIsMeasuredWithTheDefaultSmoothing()
    {
        // both sides of every crest at 3.25 px, blurred a little by the drawing's area sampling only; every edge on the
        // drawing's 1/8 px grid, so the drawn spacings are exact, the end ones too
        var img = Draw(140, 40, (x, _) => x >= 20.25 && x < 20.25 + 15 * 6.5 && (x - 20.25) % 6.5 < 3.25);
        var cfg = Tool(new MeasureFeature[] { new EdgeCountFeature { Name = "thread", Box = new RotBox(68, 20, 116, 10, 0), ExpectedCount = 30 } },
            Check("pitch", MeasureValue.Pitch, "thread", min: 3.15, max: 3.35), Check("mean", MeasureValue.MeanPitch, "thread"));
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(img, new BatchFields());
        AssertPass(r);
        Assert.Equal(3.25, r.Values["mean"], 0.01);

        // the first crest 0.375 px wider outwards: its end spacing (3.625) is beyond the limits, as an inner one would
        // be (the end edges no longer lean, so there is no allowance for them: the 0.5 px one let this pass)
        var moved = Draw(140, 40, (x, _) => x >= 19.875 && x < 20.25 + 15 * 6.5 && (x < 20.25 || (x - 20.25) % 6.5 < 3.25));
        var m = tool.Run(moved, new BatchFields());
        Assert.False(m.Pass, m.Summary);
        Assert.Equal("explain.edges.pitch", m.Explain);
    }

    [Fact]
    public void AFaintStubIsNotCountedAsAPin()
    {
        // pin 8 cut down to a stub of a third of the contrast: the count matches, but the pin is in doubt
        var img = Pins();
        var px = (byte[])img.Pixels.Clone();
        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 160; x++)
                if (x >= 20.4 + 7 * 10.25 - 1 && x <= 20.4 + 7 * 10.25 + 5.6 && px[y * 160 + x] > Dark) px[y * 160 + x] = (byte)(Dark + (px[y * 160 + x] - Dark) / 3);
        using var tool = new MeasureTool(PinTool());
        var r = tool.Run(new GrayImage(px, 160, 60), new BatchFields());
        Assert.False(r.Pass, r.Summary);
        Assert.Equal("explain.pitch.missing", r.Explain);
        Assert.Contains("pin 8 is faint", r.Summary);
    }

    [Fact]
    public void ConstructionsGiveFootLineAndCircle()
    {
        var cfg = PlateTool(
            Check("holeToFoot", MeasureValue.Distance, "foot", "hole"),
            Check("footOnTop", MeasureValue.Distance, "foot", "top"),
            Check("square", MeasureValue.AngleBetween, "drop", "top", MeasureUnit.Deg),
            Check("thales", MeasureValue.Diameter, "circle"),
            Check("holeToMeet", MeasureValue.Distance, "hole", "meet"),
            Check("halfway", MeasureValue.Distance, "mid", "hole"));
        cfg.Features.Add(new ConstructFeature { Name = "meet", Operation = ConstructOp.Intersection, Inputs = { "top", "left" } });
        cfg.Features.Add(new ConstructFeature { Name = "foot", Operation = ConstructOp.Perpendicular, Inputs = { "hole", "top" } });
        cfg.Features.Add(new ConstructFeature { Name = "drop", Operation = ConstructOp.LineThrough, Inputs = { "hole", "foot" } });
        cfg.Features.Add(new ConstructFeature { Name = "circle", Operation = ConstructOp.CircleThrough, Inputs = { "hole", "foot", "meet" } });
        cfg.Features.Add(new ConstructFeature { Name = "mid", Operation = ConstructOp.Midpoint, Inputs = { "hole", "meet" } });
        Assert.Null(MeasureTool.SetupProblem(cfg, null));
        using var tool = new MeasureTool(cfg);
        var r = tool.Run(Plate(160.2, 120.1, 0), new BatchFields());
        AssertPass(r);
        Assert.Equal(40.35, r.Values["holeToFoot"], 0.02); // as the hole's distance from the top edge
        Assert.Equal(0, r.Values["footOnTop"], 1e-6);
        Assert.Equal(90, r.Values["square"], 0.02);
        // the corner of the triangle hole, foot, meet is square at the foot: the circle through them has the hole-to-meet diameter
        Assert.Equal(r.Values["holeToMeet"], r.Values["thales"], 1e-3);
        Assert.Equal(r.Values["holeToMeet"] / 2, r.Values["halfway"], 1e-3);

        // lines that do not meet
        cfg.Features.Add(new LineFeature { Name = "top2", X0 = 120, Y0 = 80, X1 = 200, Y1 = 80, Polarity = EdgePolarity.DarkToLight });
        cfg.Features.Add(new ConstructFeature { Name = "never", Operation = ConstructOp.Intersection, Inputs = { "top", "top2" } });
        using var par = new MeasureTool(cfg);
        var p = par.Run(Plate(160.2, 120.1, 0), new BatchFields());
        Assert.False(p.Pass);
        Assert.Equal("explain.geometry.parallel", p.Explain);
    }

    [Fact]
    public void NewFeaturesAndChecksAreCheckedAtSetup()
    {
        MeasureToolConfig With(MeasureFeature f, MeasureCheck? k = null) =>
            Tool(new MeasureFeature[] { new LineFeature { Name = "top", X0 = 110, Y0 = 80, X1 = 210, Y1 = 80 }, f }, k is null ? Array.Empty<MeasureCheck>() : new[] { k });
        // a construction may use only features listed before it, of the kind the operation needs
        var late = Tool(new MeasureFeature[]
        {
            new ConstructFeature { Name = "x", Operation = ConstructOp.Midpoint, Inputs = { "a", "b" } },
            new CaliperFeature { Name = "a" }, new CaliperFeature { Name = "b" },
        });
        Assert.Contains("listed before", MeasureTool.SetupProblem(late, null));
        Assert.Contains("gives no point", MeasureTool.SetupProblem(With(new ConstructFeature { Name = "m", Operation = ConstructOp.Midpoint, Inputs = { "top", "top" } }), null));
        Assert.Contains("pins expected", MeasureTool.SetupProblem(With(new PitchFeature { Name = "p" }), null));
        Assert.NotNull(MeasureTool.SetupProblem(With(new GapFeature { Name = "g", Box = new RotBox(50, 50, 40, 10, 0), Samples = 20 }), null)); // 0.5 px slices
        // a count has no unit; a pitch is a size
        var pins = new PitchFeature { Name = "p", ExpectedCount = 4 };
        Assert.Contains("no unit", MeasureTool.SetupProblem(With(pins, Check("n", MeasureValue.Count, "p")), null));
        Assert.Contains("unit", MeasureTool.SetupProblem(With(pins, Check("n", MeasureValue.Pitch, "p", u: MeasureUnit.None)), null));
        Assert.NotNull(MeasureTool.SetupProblem(With(pins, Check("d", MeasureValue.Distance, "p", "top")), null)); // a pin row is no point
        // gap, pins and edge counts need their tolerance
        Assert.Contains("Pitch check", MeasureTool.SetupProblem(With(pins, Check("n", MeasureValue.Count, "p", u: MeasureUnit.None, min: 4, max: 4)), null));
        Assert.Null(MeasureTool.SetupProblem(With(pins, Check("pitch", MeasureValue.Pitch, "p", min: 9, max: 11)), null));
        var slot = new GapFeature { Name = "g", Box = new RotBox(50, 50, 40, 40, 0) };
        Assert.Contains("width", MeasureTool.SetupProblem(With(slot, Check("m", MeasureValue.MeanWidth, "g")), null));
        Assert.Null(MeasureTool.SetupProblem(With(slot, Check("m", MeasureValue.MinWidth, "g", min: 3)), null));
        Assert.Contains("edges expected", MeasureTool.SetupProblem(With(new EdgeCountFeature { Name = "e" }), null));
        Assert.Null(MeasureTool.SetupProblem(With(new EdgeCountFeature { Name = "e", ExpectedCount = 9 }), null));
    }

    [Fact]
    public void JobFileKeepsTheNewFeatures()
    {
        var cfg = Tool(new MeasureFeature[]
        {
            new EllipseFeature { Name = "oval", Cx = 108, Cy = 97, RInner = 18, ROuter = 56 },
            new CornerFeature { Name = "corner", Edge1 = new Segment(1, 2, 3, 4), Edge2 = new Segment(5, 6, 7, 8) },
            new GapFeature { Name = "slot", Box = new RotBox(46, 50, 40, 60, 0) },
            new PitchFeature { Name = "pins", Box = new RotBox(77, 30, 130, 20, 0), ExpectedCount = 12 },
            new EdgeCountFeature { Name = "thread", Box = new RotBox(68, 20, 116, 10, 0), ExpectedCount = 15 },
            new ConstructFeature { Name = "mid", Operation = ConstructOp.Midpoint, Inputs = { "oval", "corner" } },
        }, Check("n", MeasureValue.Count, "pins", u: MeasureUnit.None, min: 12, max: 12), Check("p", MeasureValue.Pitch, "pins", min: 10, max: 10.5),
           Check("w", MeasureValue.MinWidth, "slot", min: 11));
        var job = new Job { Name = "m", Tools = { cfg } };
        var text = JsonSerializer.Serialize(job, JobStore.Json);
        foreach (var kind in new[] { "ellipse", "corner", "gap", "pitch", "edges", "construct" }) Assert.Contains($"\"kind\": \"{kind}\"", text);
        Assert.Contains("\"Operation\": \"Midpoint\"", text);
        Assert.Contains("\"Unit\": \"None\"", text);
        var back = Assert.IsType<MeasureToolConfig>(JsonSerializer.Deserialize<Job>(text, JobStore.Json)!.Tools[0]);
        Assert.Equal(new Segment(5, 6, 7, 8), Assert.IsType<CornerFeature>(back.Features[1]).Edge2);
        Assert.Equal(12, Assert.IsType<PitchFeature>(back.Features[3]).ExpectedCount);
        Assert.Equal(1, back.Features[3].FilterHalfWidth); // pins and edge counts smooth 1 px by default
        Assert.Equal(new[] { "oval", "corner" }, Assert.IsType<ConstructFeature>(back.Features[5]).Inputs);
        Assert.Null(MeasureTool.SetupProblem(back, null));
    }
}
