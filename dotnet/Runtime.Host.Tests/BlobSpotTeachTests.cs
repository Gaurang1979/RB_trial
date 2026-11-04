using System.Text.Json;
using Grpc.Core;
using VisionCore;
using VisionStudio.Remote.V1;
using VisionSystem.Runtime;
using Xunit;

/// <summary>The Blob tool in the job designer, follow-up of PR #307 (requirements 17.12 BLB-OUT-07, BLB-OUT-08): Teach
/// on the spot the engineer clicked (TryJob's teach_spot), never another one, and on a try a cross at each spot's deepest
/// dent and largest deviation, NG over its limit; every try graphic names its tool.</summary>
public sealed class BlobSpotTeachTests : IAsyncLifetime
{
    const int W = 400, H = 300;
    RuntimeFixture _f = null!;
    Metadata _eng = null!;
    string _pics = "";

    public async Task InitializeAsync()
    {
        _f = await RuntimeFixture.StartAsync(f =>
        {
            _pics = Path.Combine(f.Folder, "spots");
            Directory.CreateDirectory(_pics);
            // a large square left, a small one right, the small one with a bite out of its top edge in "bitten"
            Save("two.bmp", Picture(0));
            Save("bitten.bmp", Picture(6));
        });
        await _f.SignInOperator();
        var r = await _f.Sessions().SignInAsync(new SignInRequest { User = "eng", Password = "secret-pass", Client = ClientKind.Studio });
        Assert.True(r.Ok, r.ErrorText);
        _eng = RuntimeFixture.Bearer(r.Token);
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    void Save(string name, byte[] px) => BarcodeReading.BmpImage.SaveGray(Path.Combine(_pics, name), px, W, H);

    /// <summary>Dark squares on a light page, anti-aliased: 100 px side at (110, 150), 50 px side at (290, 150); a bite of
    /// radius <paramref name="biteR"/> centred on the small square's top edge.</summary>
    static byte[] Picture(float biteR)
    {
        var px = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        double u = x - 0.375 + sx * 0.25, v = y - 0.375 + sy * 0.25;
                        bool big = Math.Abs(u - 110) <= 50 && Math.Abs(v - 150) <= 50;
                        bool small = Math.Abs(u - 290) <= 25 && Math.Abs(v - 150) <= 25
                                     && !(biteR > 0 && (u - 290) * (u - 290) + (v - 125) * (v - 125) <= biteR * biteR);
                        if (big || small) inside++;
                    }
                px[y * W + x] = (byte)Math.Round(210 - 170 * inside / 16.0);
            }
        return px;
    }

    string Draft(BlobToolConfig blob) => JsonSerializer.Serialize(new Job
    {
        Name = "Lids",
        Cameras = [new CameraConfig { Name = "Top", TestFolder = _pics }],
        Tools = [blob],
    }, JobStore.Json);

    /// <summary>Largest first, so spot 1 is the large square and spot 2 the small one.</summary>
    static BlobToolConfig Lid() => new() { Name = "Lid", TimeBudgetMs = 2000, SortBy = BlobSort.Area, SortDescending = true };

    Task<TryJobResponse> Try(BlobToolConfig blob, string picture, string teach = "", int spot = 0) =>
        _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = Draft(blob), Camera = 0, Stored = picture, TeachTool = teach, TeachSpot = spot }, _eng).ResponseAsync;

    static BlobToolConfig TaughtTool(TryJobResponse t) => (BlobToolConfig)JsonSerializer.Deserialize<Job>(t.TaughtJson, JobStore.Json)!.Tools[0];

    /// <summary>The taught outline's width (x and y pairs round the centroid).</summary>
    static float Width(float[] xy) => Enumerable.Range(0, xy.Length / 2).Max(i => xy[2 * i]) - Enumerable.Range(0, xy.Length / 2).Min(i => xy[2 * i]);

    [Fact]
    public async Task The_clicked_spot_is_the_one_taught()
    {
        var first = await Try(Lid(), "two.bmp", "Lid");          // no spot: the first, as before
        Assert.True(first.Ok, first.ErrorText);
        Assert.StartsWith("Outline taught from spot 1", first.TaughtText);
        Assert.InRange(Width(TaughtTool(first).TaughtOutline!), 95, 105);

        var second = await Try(Lid(), "two.bmp", "Lid", 2);
        Assert.True(second.Ok, second.ErrorText);
        Assert.StartsWith("Outline taught from spot 2", second.TaughtText);
        var lid = TaughtTool(second);
        Assert.InRange(Width(lid.TaughtOutline!), 45, 55);       // the small square's outline
        Assert.Equal(2f, lid.MaxDeviation);                       // the same safe start as Teach on spot 1
        Assert.Equal(BlobPresence.Present, lid.Presence);
    }

    [Fact]
    public async Task A_spot_that_is_not_there_is_refused_never_another_taught()
    {
        var none = await Try(Lid(), "two.bmp", "Lid", 3);
        Assert.False(none.Ok);
        Assert.Equal("runtime.refused.teach", none.ErrorKey);
        Assert.Contains("no spot 3", none.ErrorText);
        Assert.Contains("2 found", none.ErrorText);
        Assert.Equal("", none.TaughtJson);
        Assert.Equal("runtime.refused.teach", (await Try(Lid(), "two.bmp", "Lid", -1)).ErrorKey);
    }

    [Fact]
    public async Task Try_graphics_name_their_tool_and_number_the_spots()
    {
        var t = await Try(Lid(), "two.bmp");
        Assert.True(t.Ok, t.ErrorText);
        Assert.All(t.Graphics, g => Assert.Equal("Lid", g.Tool));
        var spots = t.Graphics.Where(g => g.Label is "1" or "2").ToDictionary(g => g.Label);
        Assert.True(spots["1"].Points.Average(p => p.X) / t.Scale < 200);   // spot 1 is the large square, left
        Assert.True(spots["2"].Points.Average(p => p.X) / t.Scale > 200);
    }

    [Fact]
    public async Task A_dent_over_its_limit_gets_a_red_cross_where_it_is_and_no_rule_diamond()
    {
        var lid = TaughtTool(await Try(Lid(), "two.bmp", "Lid", 2));
        lid.MaxDentDepth = 2;
        lid.MaxDeviation = 50;   // the dent alone decides
        var t = await Try(lid, "bitten.bmp");
        Assert.True(t.Ok, t.ErrorText);
        Assert.False(t.Pass);
        var dents = t.Graphics.Where(g => g.Kind == VisionStudio.Remote.V1.Graphic.Types.Kind.Cross && g.Label.StartsWith("dent ")).ToList();
        var bitten = Assert.Single(dents, g => g.Colour == "status.ng");
        Assert.Equal("Lid", bitten.Tool);
        Assert.EndsWith(" px", bitten.Label);
        Assert.Contains("spot 2", bitten.Tooltip);
        Assert.Contains("at most 2.0 allowed", bitten.Tooltip);
        var at = Assert.Single(bitten.Points);
        Assert.InRange(at.X / t.Scale, 284, 296);                 // in the bite on the small square's top edge
        Assert.InRange(at.Y / t.Scale, 122, 134);
        Assert.DoesNotContain(t.Graphics, g => g.Kind == VisionStudio.Remote.V1.Graphic.Types.Kind.Polygon && g.Label is "dent" or "deviation");
        Assert.All(dents.Where(g => g != bitten), g => Assert.Equal("status.info", g.Colour));
        // the deviation from the taught outline is marked on every spot, within its limit
        Assert.Equal(2, t.Graphics.Count(g => g.Kind == VisionStudio.Remote.V1.Graphic.Types.Kind.Cross && g.Label.StartsWith("dev ") && g.Colour == "status.info"));
    }
}

/// <summary>The geometry of the marks (BLB-OUT-08) without a runtime: where they go, their value in the tool's unit, and
/// when there are none.</summary>
public sealed class BlobMarksTests
{
    static BlobInfo Spot(float dent, PointF dentAt, float? dev, PointF? devAt) => new(
        100, new PointF(10, 10), 40, 0.8f, 1, 12, 8, 1.5f, 0, new RoiBox(0, 0, 20, 20), [], 0, 60, false,
        new BlobContour([new(0, 0), new(20, 0), new(20, 20), new(0, 20)], 80, 400, 0.98f, [], dent, dentAt, 1, true, dev, dev / 2, devAt, new PointF(10, 10), 0));

    static VisionCore.ToolResult Result(params BlobInfo[] blobs) => new("Lid", true, "2 found", [], 1, []) { Blobs = blobs };

    static BlobToolConfig Cfg(float? dentLimit = null, float? devLimit = null, BlobOutlineUnit unit = BlobOutlineUnit.Px) =>
        new() { Name = "Lid", CheckOutline = true, MaxDentDepth = dentLimit, MaxDeviation = devLimit, OutlineUnit = unit };

    [Fact]
    public void Each_spot_gets_a_cross_at_its_dent_and_deviation_scaled_to_the_picture()
    {
        var marks = BlobMarks.For(Cfg(dentLimit: 1, devLimit: 2), Result(
            Spot(0.5f, new PointF(100, 40), 3f, new PointF(60, 80)),
            Spot(1.5f, new PointF(200, 50), 1f, new PointF(220, 90))), null, 0.5).ToList();
        Assert.Equal(4, marks.Count);
        Assert.All(marks, m => { Assert.Equal(VisionStudio.Remote.V1.Graphic.Types.Kind.Cross, m.Kind); Assert.Equal("Lid", m.Tool); Assert.Single(m.Points); });
        Assert.Equal(("dent 0.5 px", "status.info", 50f, 20f), (marks[0].Label, marks[0].Colour, marks[0].Points[0].X, marks[0].Points[0].Y));
        Assert.Equal(("dev 3.0 px", "status.ng", 30f, 40f), (marks[1].Label, marks[1].Colour, marks[1].Points[0].X, marks[1].Points[0].Y));
        Assert.Equal(("dent 1.5 px", "status.ng", 100f, 25f), (marks[2].Label, marks[2].Colour, marks[2].Points[0].X, marks[2].Points[0].Y));
        Assert.Equal(("dev 1.0 px", "status.info", 110f, 45f), (marks[3].Label, marks[3].Colour, marks[3].Points[0].X, marks[3].Points[0].Y));
        Assert.StartsWith("Lid, spot 2: deepest dent 1.5 px, at most 1.0 allowed", marks[2].Tooltip);
    }

    [Fact]
    public void In_mm_the_values_are_converted_and_a_limit_compares_in_mm()
    {
        var lens = new BarcodeReading.LensCalibration(0, 0, 0, 0, 0, 0, 0, 0, 0, 0.05f, 0);
        var marks = BlobMarks.For(Cfg(dentLimit: 0.1f, unit: BlobOutlineUnit.Mm), Result(Spot(4, new PointF(10, 10), null, null)), lens, 1).ToList();
        var dent = Assert.Single(marks);                          // no taught outline: no deviation mark
        Assert.Equal(("dent 0.2 mm", "status.ng"), (dent.Label, dent.Colour));
        Assert.EndsWith("no limit set", BlobMarks.For(Cfg(unit: BlobOutlineUnit.Mm), Result(Spot(4, new PointF(10, 10), null, null)), lens, 1).Single().Tooltip);
    }

    [Fact]
    public void No_marks_without_the_outline_check_a_dent_or_a_calibration_for_mm()
    {
        var spot = Spot(2, new PointF(10, 10), 1, new PointF(5, 5));
        Assert.Empty(BlobMarks.For(new BlobToolConfig { Name = "Lid" }, Result(spot), null, 1));
        Assert.Empty(BlobMarks.For(Cfg(unit: BlobOutlineUnit.Mm), Result(spot), null, 1));
        Assert.Empty(BlobMarks.For(Cfg(), new VisionCore.ToolResult("Lid", false, "Nothing found", [], 1, []), null, 1));
        var convex = BlobMarks.For(Cfg(), Result(Spot(0, default, 1, new PointF(5, 5))), null, 1).ToList();
        Assert.StartsWith("dev ", Assert.Single(convex).Label);   // a convex spot has no dent to mark
        Assert.True(BlobMarks.IsRuleMark(new VisionCore.Graphic([], VisionCore.GraphicKind.Fail, "dent")));
        Assert.False(BlobMarks.IsRuleMark(new VisionCore.Graphic([], VisionCore.GraphicKind.Ok, "1")));
    }
}
