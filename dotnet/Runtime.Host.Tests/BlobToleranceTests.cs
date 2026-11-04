using System.Text.Json;
using Grpc.Core;
using VisionCore;
using VisionStudio.Remote.V1;
using Xunit;

/// <summary>The Blob tool in the job designer (requirements 17.12 BLB-OUT-04, BLB-OUT-06): Teach on a Blob tool teaches
/// the outline of the first spot and leaves a tool that never passes any shape, and SuggestBlobTolerance measures stored
/// pictures of good parts with BlobTool.AutoTolerance. Only an engineer in Vision Studio calls it, it never changes the
/// job, and a picture without one whole outline gives no suggestion.</summary>
public sealed class BlobToleranceTests : IAsyncLifetime
{
    const int W = 400, H = 300;
    RuntimeFixture _f = null!;
    Metadata _op = null!, _eng = null!;
    string _bars = "";

    public async Task InitializeAsync()
    {
        _f = await RuntimeFixture.StartAsync(f =>
        {
            _bars = Path.Combine(f.Folder, "bars");
            Directory.CreateDirectory(_bars);
            Save("bar0.bmp", Bar(200, 150, 0));
            Save("bar1.bmp", Bar(190.4f, 140.7f, 35));
            Save("bar2.bmp", Bar(205.3f, 155.2f, -20));
            Save("dent.bmp", Bar(190.4f, 140.7f, 35, 64, 0, 9));
            Save("empty.bmp", Bar(-500, -500, 0));
        });
        _op = await _f.SignInOperator();
        var r = await _f.Sessions().SignInAsync(new SignInRequest { User = "eng", Password = "secret-pass", Client = ClientKind.Studio });
        Assert.True(r.Ok, r.ErrorText);
        _eng = RuntimeFixture.Bearer(r.Token);
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    void Save(string name, byte[] px) => BarcodeReading.BmpImage.SaveGray(Path.Combine(_bars, name), px, W, H);

    /// <summary>A dark bar with a notch on a light page, anti-aliased (as BlobToolTests draws it); a bite makes a dent.</summary>
    static byte[] Bar(float cx, float cy, float deg, float bx = 0, float by = 0, float biteR = 0)
    {
        var px = new byte[W * H];
        double a = -deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
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
        return px;
    }

    string Draft(BlobToolConfig blob, BarcodeReading.LensCalibration? calibration = null) => JsonSerializer.Serialize(new Job
    {
        Name = "Lids",
        Cameras = [new CameraConfig { Name = "Top", TestFolder = _bars, Calibration = calibration }],
        Tools = [blob],
    }, JobStore.Json);

    static BlobToolConfig Lid() => new() { Name = "Lid", TimeBudgetMs = 2000 };

    async Task<BlobToolConfig> Taught(BarcodeReading.LensCalibration? calibration = null, BlobOutlineUnit unit = BlobOutlineUnit.Px)
    {
        var lid = Lid();
        lid.OutlineUnit = unit;
        var t = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = Draft(lid, calibration), Camera = 0, Stored = "bar0.bmp", TeachTool = "Lid" }, _eng);
        Assert.True(t.Ok, t.ErrorText);
        return (BlobToolConfig)JsonSerializer.Deserialize<Job>(t.TaughtJson, JobStore.Json)!.Tools[0];
    }

    static async Task<StatusCode> Code(Func<Task> call)
    {
        try { await call(); return StatusCode.OK; }
        catch (RpcException e) { return e.StatusCode; }
    }

    [Fact]
    public async Task Only_an_engineer_asks_for_a_suggestion()
    {
        Assert.Equal(StatusCode.PermissionDenied, await Code(() => _f.Runtime().SuggestBlobToleranceAsync(new SuggestBlobToleranceRequest(), _op).ResponseAsync));
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime().SuggestBlobToleranceAsync(new SuggestBlobToleranceRequest()).ResponseAsync));
    }

    [Fact]
    public async Task Teach_on_a_blob_tool_teaches_the_outline_and_leaves_no_tool_that_passes_any_shape()
    {
        var t = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = Draft(Lid()), Camera = 0, Stored = "bar0.bmp", TeachTool = "Lid" }, _eng);
        Assert.True(t.Ok, t.ErrorText);
        Assert.StartsWith("Outline taught from spot 1", t.TaughtText);
        var lid = (BlobToolConfig)JsonSerializer.Deserialize<Job>(t.TaughtJson, JobStore.Json)!.Tools[0];
        Assert.True(lid.CheckOutline);
        Assert.True(lid.TaughtOutline is { Length: >= 6 });
        Assert.Equal(2f, lid.MaxDeviation);                     // a start limit, never none
        Assert.Equal(BlobPresence.Present, lid.Presence);       // a missing part is never passed
        Assert.True(t.Pass, string.Join("; ", t.Tools.Select(x => x.Reason)));

        // nothing to teach on a page with no part: refused with the reason
        var none = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = Draft(Lid()), Camera = 0, Stored = "empty.bmp", TeachTool = "Lid" }, _eng);
        Assert.Equal("runtime.refused.teach", none.ErrorKey);
    }

    [Fact]
    public async Task Good_pictures_give_a_suggestion_that_fails_a_dented_part_and_the_job_is_not_changed()
    {
        var lid = await Taught();
        var json = Draft(lid);
        var s = await _f.Runtime().SuggestBlobToleranceAsync(new SuggestBlobToleranceRequest { JobJson = json, Camera = 0, Tool = "Lid", Pictures = { "bar0.bmp", "bar1.bmp", "bar2.bmp" } }, _eng);
        Assert.True(s.Ok, s.ErrorText);
        Assert.True(s.Found, s.ReasonText);
        Assert.Equal("px", s.Unit);
        Assert.Equal(3, s.Pictures);
        Assert.InRange(s.MaxDeviation, 0.3f, 2f);
        Assert.True(s.MaxDeviation >= s.WorstDeviation && s.MaxDentDepth >= s.WorstDent);
        Assert.Null(s.Calibration);                             // no calibration: px only

        // the suggestion accepted: the good parts pass, the dented one fails
        lid.MaxDeviation = s.MaxDeviation;
        foreach (var (name, pass) in new[] { ("bar1.bmp", true), ("dent.bmp", false) })
        {
            var t = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = Draft(lid), Camera = 0, Stored = name }, _eng);
            Assert.True(t.Ok, t.ErrorText);
            Assert.Equal(pass, t.Pass);
        }
    }

    [Fact]
    public async Task In_mm_the_suggestion_is_scaled_and_the_calibration_comes_back()
    {
        var lens = new BarcodeReading.LensCalibration(1200, 1201, 200, 150, 0.01f, 0, 0, 0, 0, 0.05f, 0.2f);
        var px = await _f.Runtime().SuggestBlobToleranceAsync(new SuggestBlobToleranceRequest { JobJson = Draft(await Taught()), Camera = 0, Tool = "Lid",
            Pictures = { "bar0.bmp", "bar1.bmp", "bar2.bmp" } }, _eng);
        var lid = await Taught(lens, BlobOutlineUnit.Mm);
        Assert.Equal(0.1f, lid.MaxDeviation!.Value, 4);          // the start limit, 2 px, in mm
        var mm = await _f.Runtime().SuggestBlobToleranceAsync(new SuggestBlobToleranceRequest { JobJson = Draft(lid, lens), Camera = 0, Tool = "Lid",
            Pictures = { "bar0.bmp", "bar1.bmp", "bar2.bmp" } }, _eng);
        Assert.True(mm.Found, mm.ReasonText);
        Assert.Equal("mm", mm.Unit);
        Assert.Equal(px.MaxDeviation * 0.05f, mm.MaxDeviation, 4);
        Assert.Equal(0.05f, mm.Calibration.MmPerPx);
        Assert.Equal((1200f, 1201f, 0.01f, 0.2f), (mm.Calibration.Fx, mm.Calibration.Fy, mm.Calibration.K1, mm.Calibration.RmsPx));
    }

    [Fact]
    public async Task Refusals_and_no_suggestion_say_why()
    {
        var taught = Draft(await Taught());
        async Task<SuggestBlobToleranceResponse> Ask(string json, string tool, params string[] pictures) =>
            await _f.Runtime().SuggestBlobToleranceAsync(new SuggestBlobToleranceRequest { JobJson = json, Camera = 0, Tool = tool, Pictures = { pictures } }, _eng);

        Assert.Equal("runtime.refused.notTaught", (await Ask(Draft(Lid()), "Lid", "bar0.bmp", "bar1.bmp", "bar2.bmp")).ErrorKey);
        Assert.Equal("runtime.refused.noTool", (await Ask(taught, "Cap", "bar0.bmp", "bar1.bmp", "bar2.bmp")).ErrorKey);
        Assert.Equal("runtime.refused.pictures", (await Ask(taught, "Lid", "bar0.bmp", "bar1.bmp")).ErrorKey);
        Assert.Equal("runtime.refused.picture", (await Ask(taught, "Lid", "bar0.bmp", "bar1.bmp", "../jobs/Labels.json")).ErrorKey);
        var mm = await Taught(new BarcodeReading.LensCalibration(0, 0, 0, 0, 0, 0, 0, 0, 0, 0.05f, 0));
        mm.OutlineUnit = BlobOutlineUnit.Mm;
        Assert.Equal("runtime.refused.needsCalibration", (await Ask(Draft(mm), "Lid", "bar0.bmp", "bar1.bmp", "bar2.bmp")).ErrorKey);

        // a picture with no part: no made-up limit
        var none = await Ask(taught, "Lid", "bar0.bmp", "bar1.bmp", "empty.bmp");
        Assert.True(none.Ok, none.ErrorText);
        Assert.False(none.Found);
        Assert.StartsWith("No suggestion", none.ReasonText);
    }
}
