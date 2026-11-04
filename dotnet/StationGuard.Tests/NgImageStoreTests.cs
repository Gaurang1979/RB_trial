using System.Text.Json;
using StationGuard;
using Xunit;

public sealed class NgImageStoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ng-store-").FullName;
    Pressure _pressure = Pressure.Normal;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    NgImageStore Make(NgImageOptions? o = null) => new(o ?? new NgImageOptions { Folder = _dir }, () => _pressure);

    static byte[] Img(int w = 64, int h = 48) { var b = new byte[w * h]; for (var i = 0; i < b.Length; i++) b[i] = (byte)i; return b; }

    static void Drain(NgImageStore s)
    {
        for (var i = 0; i < 200 && s.Counts.QueuedBytes > 0; i++) Thread.Sleep(10);
    }

    [Fact]
    public void NgImagesAreSavedWithReasonsAndCounted()
    {
        using var s = Make();
        s.StartBatch("B1");
        Assert.True(s.Record("Carton-30", false, ["too_large"], Img(), 64, 48));
        Assert.False(s.Record("Carton-30", true, [], Img(), 64, 48)); // OK parts: counted, not saved
        Assert.True(s.Record("Carton-30", false, ["date_unreadable", "too_large"], Img(), 64, 48));
        Drain(s);
        var c = s.Counts;
        Assert.Equal((3L, 1L, 2L, 2L, 2), (c.Parts, c.Ok, c.Ng, c.Saved, c.FilesKept));
        Assert.Equal(new ReasonCount(2, 2), c.PerReason["too_large"]);
        Assert.Equal(1, c.Skipped[SkipReason.Policy]);
        var bmp = Directory.GetFiles(_dir, "*.bmp", SearchOption.AllDirectories);
        Assert.Equal(2, bmp.Length);
        var meta = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(bmp.Order().First(), ".json"))).RootElement;
        Assert.Equal("B1", meta.GetProperty("batch").GetString());
    }

    [Fact]
    public void WithoutDateFoldersImagesGoInOneFolderPerJobWithTheCallersFields()
    {
        using var s = Make(new NgImageOptions { Folder = _dir, DateFolders = false });
        var extra = new Dictionary<string, object?> { ["format"] = "dm", ["expected"] = 24, ["job"] = "not this one" };
        Assert.True(s.Record("P-24", false, ["codes_missing"], Img(), 64, 48, imageId: "20261030_101500_123", camera: "top", meta: extra));
        Drain(s);
        var bmp = Assert.Single(Directory.GetFiles(_dir, "*.bmp", SearchOption.AllDirectories));
        Assert.Equal(Path.Combine(_dir, "P-24", "20261030_101500_123_top.bmp"), bmp);
        var meta = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(bmp, ".json"))).RootElement;
        Assert.Equal("dm", meta.GetProperty("format").GetString());
        Assert.Equal(24, meta.GetProperty("expected").GetInt32());
        Assert.Equal("P-24", meta.GetProperty("job").GetString()); // the store's own fields win
        Assert.Equal("codes_missing", meta.GetProperty("reasons")[0].GetString());
    }

    [Fact]
    public void TheBmpIsAReadableGreyImage()
    {
        var path = Path.Combine(_dir, "t.bmp");
        BmpGray.Write(path, Img(5, 3), 5, 3);
        var b = File.ReadAllBytes(path);
        Assert.Equal("BM", System.Text.Encoding.ASCII.GetString(b, 0, 2));
        Assert.Equal(b.Length, BitConverter.ToInt32(b, 2));
        Assert.Equal((5, 3, (short)8), (BitConverter.ToInt32(b, 18), BitConverter.ToInt32(b, 22), BitConverter.ToInt16(b, 28)));
        // bottom-up rows, padded to 8 bytes: the first stored row is the last image row
        Assert.Equal(10, b[14 + 40 + 1024]);
    }

    [Fact]
    public void UnderHighPressureOnlyAFewPerReasonPerHourAreKept()
    {
        using var s = Make(new NgImageOptions { Folder = _dir, PerReasonPerHourUnderPressure = 2 });
        _pressure = Pressure.High;
        var kept = Enumerable.Range(0, 5).Count(_ => s.Record("J", false, ["too_large"], Img(), 64, 48));
        kept += Enumerable.Range(0, 5).Count(_ => s.Record("J", false, ["no_code"], Img(), 64, 48));
        Assert.Equal(4, kept);
        Assert.Equal(6, s.Counts.Skipped[SkipReason.Pressure]);
        Assert.Equal(10, s.Counts.Ng); // every part still counted
    }

    [Fact]
    public void UnderCriticalPressureNothingIsCopiedButEverythingIsCounted()
    {
        using var s = Make();
        _pressure = Pressure.Critical;
        Assert.False(s.Record("J", false, ["x"], Img(), 64, 48));
        Assert.Equal((1L, 0L), (s.Counts.Ng, s.Counts.QueuedBytes));
    }

    [Fact]
    public void AFullQueueIsCountedNotWaitedFor()
    {
        using var s = Make(new NgImageOptions { Folder = _dir, MaxQueueBytes = 64 * 48 * 2 });
        var big = Img();
        var kept = Enumerable.Range(0, 50).Count(_ => s.Record("J", false, ["x"], big, 64, 48));
        Assert.InRange(kept, 2, 50);
        Assert.Equal(50, s.Counts.Ng);
        Assert.Equal(50 - kept, s.Counts.Skipped.GetValueOrDefault(SkipReason.QueueFull));
    }

    [Fact]
    public void TheFolderStaysUnderItsLimitOldestFirst()
    {
        using var s = Make(new NgImageOptions { Folder = _dir, MaxFiles = 3 });
        for (var i = 0; i < 6; i++) { s.Record("J", false, [$"r{i}"], Img(), 64, 48); Drain(s); Thread.Sleep(5); }
        var left = Directory.GetFiles(_dir, "*.bmp", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).Order().ToList();
        Assert.Equal(3, left.Count);
        Assert.All(left, n => Assert.Matches("_r[345]$", n!));
        Assert.DoesNotContain(Directory.GetFiles(_dir, "*.json", SearchOption.AllDirectories), f => !f.EndsWith("counts.json") && !File.Exists(Path.ChangeExtension(f, ".bmp")));
    }

    [Fact]
    public void EveryNthKeepsOneInN()
    {
        using var s = Make(new NgImageOptions { Folder = _dir, Mode = SaveMode.EveryNthNg, EveryNth = 4 });
        var kept = Enumerable.Range(0, 12).Count(_ => s.Record("J", false, ["x"], Img(), 64, 48));
        Assert.Equal(3, kept);
    }

    [Fact]
    public void CountsSurviveARestartAndANewBatchStartsAtZero()
    {
        using (var s = Make())
        {
            s.StartBatch("B1");
            s.Record("J", false, ["x"], Img(), 64, 48);
            s.Record("J", true, [], Img(), 64, 48);
            Drain(s);
        }
        using var again = Make();
        Assert.Equal(("B1", 2L, 1L, 1), (again.Counts.Batch, again.Counts.Parts, again.Counts.Ng, again.Counts.FilesKept));
        again.StartBatch("B2");
        Assert.Equal(("B2", 0L), (again.Counts.Batch, again.Counts.Parts));
        Assert.Equal(1, again.Counts.FilesKept); // images stay; only the counts start again
    }

    [Fact]
    public void TheGuardCanDropWaitingImagesToFreeMemory()
    {
        using var s = Make(new NgImageOptions { Folder = _dir, MaxQueueBytes = 1L << 30 });
        for (var i = 0; i < 200; i++) s.Record("J", false, ["x"], Img(512, 512), 512, 512);
        var freed = s.DropQueuedReliever().Relieve();
        Drain(s);
        var c = s.Counts;
        Assert.Equal(200, c.Saved + c.Skipped.GetValueOrDefault(SkipReason.Pressure));
        Assert.Equal(freed, c.Skipped.GetValueOrDefault(SkipReason.Pressure) * 512 * 512);
    }

    [Fact]
    public void ABadImageNeverThrowsIntoTheInspection()
    {
        using var s = Make();
        Assert.False(s.Record("J", false, ["x"], new byte[10], 64, 48)); // smaller than width × height
        Assert.False(s.Record("J", false, ["x"], new byte[10], 64, 48));
        Assert.Equal(2, s.Counts.Skipped[SkipReason.WriteError]);
        Assert.Equal(2, s.Counts.Ng);
        Assert.Equal(0, s.Counts.QueuedBytes); // nothing reserved for a bad frame
        Assert.True(s.Record("J", false, ["x"], Img(), 64, 48)); // and saving goes on
    }

    [Fact]
    public void PartsAreCountedOncePerPartNotPerCamera()
    {
        using var s = Make();
        s.CountPart(false, ["too_large", "no_code"]); // NG on two cameras: one NG part
        s.KeepImage("J-cam1", false, ["too_large"], Img(), 64, 48);
        s.KeepImage("J-cam2", false, ["no_code"], Img(), 64, 48);
        s.CountPart(true, []);
        Drain(s);
        var c = s.Counts;
        Assert.Equal((2L, 1L, 1L, 2L), (c.Parts, c.Ok, c.Ng, c.Saved));
        Assert.Equal(new ReasonCount(1, 1), c.PerReason["no_code"]);
    }

    [Fact]
    public void UnderCriticalPressureAPartTheModeSkipsIsCountedAsPolicy()
    {
        using var s = Make(new NgImageOptions { Folder = _dir, Mode = SaveMode.None });
        _pressure = Pressure.Critical;
        s.Record("J", false, ["x"], Img(), 64, 48);
        Assert.Equal(1, s.Counts.Skipped[SkipReason.Policy]);
        Assert.False(s.Counts.Skipped.ContainsKey(SkipReason.Pressure));
    }

    [Fact]
    public void AnImageQueuedBeforeANewBatchCountsForTheOldOne()
    {
        using var s = Make(new NgImageOptions { Folder = _dir, MaxQueueBytes = 1L << 30 });
        s.StartBatch("B1");
        for (var i = 0; i < 20; i++) s.Record("J", false, ["x"], Img(1024, 1024), 1024, 1024);
        s.StartBatch("B2");
        Drain(s);
        Assert.Equal(0, s.Counts.Saved);
        Assert.Equal(20, s.Counts.FilesKept);
    }

    [Fact]
    public void AnUnusableFolderStillCountsAndNeverThrows()
    {
        var file = Path.Combine(_dir, "a-file");
        File.WriteAllText(file, "x");
        using var s = new NgImageStore(new NgImageOptions { Folder = Path.Combine(file, "images") });
        Assert.False(s.ImagesEnabled);
        Assert.False(s.Record("J", false, ["x"], Img(), 64, 48));
        Assert.Equal((1L, 1L), (s.Counts.Ng, s.Counts.Skipped[SkipReason.Disk]));
    }

    [Fact]
    public void DisposingWhileTheWriterIsBusyDoesNotCrash()
    {
        var s = Make(new NgImageOptions { Folder = _dir, MaxQueueBytes = 1L << 30 });
        for (var i = 0; i < 50; i++) s.Record("J", false, ["x"], Img(1024, 1024), 1024, 1024);
        s.Dispose();
        s.Dispose();
    }
}

public sealed class StrideTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ng-stride-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void PaddedRowsAreSavedWithoutThePadding()
    {
        using (var s = new NgImageStore(new NgImageOptions { Folder = _dir }))
        {
            var padded = new byte[8 * 2];
            for (var x = 0; x < 5; x++) { padded[x] = (byte)(x + 1); padded[8 + x] = (byte)(x + 11); }
            Assert.True(s.Record("J", false, ["x"], padded, 5, 2, stride: 8));
            for (var i = 0; i < 200 && s.Counts.QueuedBytes > 0; i++) Thread.Sleep(10);
        }
        var b = File.ReadAllBytes(Directory.GetFiles(_dir, "*.bmp", SearchOption.AllDirectories).Single());
        const int px = 14 + 40 + 1024;
        Assert.Equal(new byte[] { 11, 12, 13, 14, 15 }, b[px..(px + 5)]); // bottom row first
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, b[(px + 8)..(px + 13)]);
    }
}

public sealed class FolderQuotaTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("quota-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void KeepsTheNewestWithTheirSideFilesAndLeavesOtherFolders()
    {
        var other = Directory.CreateDirectory(Path.Combine(_dir, "labelled-set")).FullName;
        File.WriteAllText(Path.Combine(other, "20250101_000000_000_1.bmp"), "x");
        File.WriteAllText(Path.Combine(_dir, "notes.bmp"), "x"); // not the saver's name pattern
        for (var i = 0; i < 5; i++)
        {
            var bmp = Path.Combine(_dir, $"20260101_00000{i}_000_{i}.bmp");
            File.WriteAllText(bmp, "x");
            File.WriteAllText(Path.ChangeExtension(bmp, ".json"), "{}");
            File.SetLastWriteTimeUtc(bmp, new DateTime(2026, 1, 1).AddMinutes(i));
        }
        Assert.Equal(3, FolderQuota.Trim(_dir, "????????_??????_???_*.bmp", 2, ".json"));
        Assert.Equal(["20260101_000003_000_3.bmp", "20260101_000003_000_3.json", "20260101_000004_000_4.bmp", "20260101_000004_000_4.json", "notes.bmp"],
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
        Assert.Single(Directory.GetFiles(other));
    }

    [Fact]
    public void AMissingFolderIsNothingToDo() => Assert.Equal(0, FolderQuota.Trim(Path.Combine(_dir, "none"), "*.bmp", 1));
}
