using System.Text.Json;
using BarcodeReading;
using BarcodeReading.Dashboard.Services;
using StationGuard;
using Xunit;

public sealed class HardCasesTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("dash-hard-").FullName;
    Pressure _pressure = Pressure.Normal;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    NgImageStore Make(SaveMode mode = SaveMode.NgOnly, string? folder = null) =>
        new(new NgImageOptions { Folder = folder ?? _dir, DateFolders = false, Mode = mode }, () => _pressure);

    static readonly ProductMaster Product = new() { ProductCode = "P-24", ChildCamera = new CameraSettings { ExposureUs = 1500 } };
    static readonly ReaderSettings Dm24 = new() { Format = CodeFormat.DataMatrix, ExpectedCount = 24 };

    static byte[] Img(int w = 64, int h = 48) { var b = new byte[w * h]; for (var i = 0; i < b.Length; i++) b[i] = (byte)i; return b; }

    static void Drain(NgImageStore s)
    {
        for (var i = 0; i < 300 && s.Counts.QueuedBytes > 0; i++) Thread.Sleep(10);
    }

    static GuardStatus Guard(Pressure level, string reason = "memory 90%", bool restart = false) =>
        new(level, reason, null, 0, 0, new LeakVerdict(false, 0, 0), restart, []);

    [Fact]
    public void AShortCaseIsSavedForTheRetrainingAgentInItsProductFolder()
    {
        using var s = Make();
        var reasons = HardCases.Reasons(complete: false, parentMissing: false, parentInvalid: false, duplicate: false);
        Assert.Equal(1, HardCases.Keep(s, Product, Dm24, false, false, reasons, Img(), 64, 48, 64, null, "20261030_101500_123"));
        Drain(s);
        var bmp = Assert.Single(Directory.GetFiles(_dir, "*.bmp", SearchOption.AllDirectories));
        Assert.Equal(Path.Combine(_dir, "P-24", "20261030_101500_123_top.bmp"), bmp);
        // the sidecar autolabel.py reads: format and expected count
        var meta = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(bmp, ".json"))).RootElement;
        Assert.Equal("dm", meta.GetProperty("format").GetString());
        Assert.Equal(24, meta.GetProperty("expected").GetInt32());
        Assert.Equal("P-24", meta.GetProperty("product").GetString());
        Assert.Equal(1, s.Counts.PerReason["codes_missing"].Ng);
    }

    [Fact]
    public void ACaseOfSeveralFramesKeepsEveryFrameUnderTheBenchNaming()
    {
        using var s = Make();
        byte[][] frames = [Img(), Img(), Img()];
        Assert.Equal(3, HardCases.Keep(s, Product, Dm24, false, false, ["codes_missing"], frames[^1], 64, 48, 64, frames, "case1"));
        Drain(s);
        var names = Directory.GetFiles(Path.Combine(_dir, "P-24"), "*.bmp").Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(["case1_f1.bmp", "case1_f2.bmp", "case1_f3.bmp"], names);
        Assert.Equal(1, s.Counts.Parts); // one case, however many frames
    }

    [Fact]
    public void OnlyShortCasesAreSavedButEveryCaseIsCountedByReason()
    {
        using var s = Make();
        Assert.Equal(0, HardCases.Keep(s, Product, Dm24, true, true, [], Img(), 64, 48, 64, null, "a"));
        var dup = HardCases.Reasons(complete: true, parentMissing: false, parentInvalid: false, duplicate: true);
        Assert.Equal(0, HardCases.Keep(s, Product, Dm24, false, true, dup, Img(), 64, 48, 64, null, "b"));
        Drain(s);
        Assert.Empty(Directory.GetFiles(_dir, "*.bmp", SearchOption.AllDirectories));
        var c = s.Counts;
        Assert.Equal((2L, 1L, 1L), (c.Parts, c.Ok, c.Ng));
        Assert.Equal(1, c.PerReason["duplicate"].Ng);
    }

    [Fact]
    public void UnderCriticalPressureNothingIsSavedAndTheStationIsTold()
    {
        using var s = Make();
        _pressure = Pressure.Critical;
        Assert.Equal(0, HardCases.Keep(s, Product, Dm24, false, false, ["codes_missing"], Img(), 64, 48, 64, null, "c"));
        var c = s.Counts;
        Assert.Equal(1, c.Ng); // the case is still counted as NG
        Assert.Equal(1, c.Skipped[SkipReason.Pressure]);
        var h = HardCases.Health(Guard(Pressure.Critical), c, s.ImagesEnabled);
        Assert.Equal(Pressure.Critical, h.Level);
        Assert.Contains("no hard-case images are saved", h.Warning);
        Assert.Contains("memory 90%", h.Warning);
        Assert.Equal(1, h.HardCasesSkipped);
    }

    [Fact]
    public void AFolderThatCannotBeUsedStillCountsAndWarns()
    {
        var file = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(file, "x");
        using var s = Make(folder: Path.Combine(file, "hard-cases"));
        Assert.False(s.ImagesEnabled);
        Assert.Equal(0, HardCases.Keep(s, Product, Dm24, false, false, ["codes_missing"], Img(), 64, 48, 64, null, "d"));
        Assert.Equal(1, s.Counts.Ng);
        var h = HardCases.Health(Guard(Pressure.Normal), s.Counts, s.ImagesEnabled);
        Assert.Contains("cannot be used", h.Warning);
    }

    [Fact]
    public void AStoreThatFailsNeverThrowsIntoTheInspection()
    {
        var s = Make();
        s.Dispose(); // adding to a completed queue throws inside the store; Keep must still return
        Assert.Equal(0, HardCases.Keep(s, Product, Dm24, false, false, ["codes_missing"], Img(), 64, 48, 64, null, "e"));
    }

    [Fact]
    public void NoWarningWhenAllIsWellAndSavingOffStillCounts()
    {
        using var s = Make(SaveMode.None);
        Assert.Equal(0, HardCases.Keep(s, Product, Dm24, false, false, ["codes_missing"], Img(), 64, 48, 64, null, "f"));
        var h = HardCases.Health(Guard(Pressure.Elevated), s.Counts, s.ImagesEnabled);
        Assert.Null(h.Warning);
        Assert.Equal(0, h.HardCasesSkipped); // switched off by the settings is not a skip the station must hear about
        Assert.Equal(1, s.Counts.Ng);
    }

    [Fact]
    public void ARestartRecommendationIsPassedOn()
    {
        using var s = Make();
        var h = HardCases.Health(Guard(Pressure.High, "disk 8% free", restart: true), s.Counts, true);
        Assert.True(h.RestartRecommended);
        Assert.Contains("only a few hard-case images", h.Warning);
        Assert.Contains("restart the station between batches", h.Warning);
    }

    [Fact]
    public void ReasonsNameEveryCauseOfAReject()
    {
        Assert.Empty(HardCases.Reasons(true, false, false, false));
        Assert.Equal(["codes_missing", "no_parent", "parent_invalid", "duplicate"], HardCases.Reasons(false, true, true, true));
        Assert.Equal(["codes_extra"], HardCases.Reasons(true, false, false, false, extra: true));
    }
}
