using System.Text.Json;
using StationGuard;
using Xunit;

/// <summary>Image ids: file names, the .json, lookup by id (saved, queued, missing) and the index after a restart.</summary>
public sealed class NgImageIdTests : IDisposable
{
    const string Id = "20261030T101530123Z-000042";
    readonly string _dir = Directory.CreateTempSubdirectory("ng-ids-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    NgImageStore Make(int maxFiles = 5000, long queueBytes = 64L << 20)
        => new(new NgImageOptions { Folder = _dir, MaxFiles = maxFiles, MaxQueueBytes = queueBytes });

    static byte[] Img(int w = 64, int h = 48) { var b = new byte[w * h]; for (var i = 0; i < b.Length; i++) b[i] = (byte)i; return b; }

    static void Drain(NgImageStore s)
    {
        for (var i = 0; i < 500 && s.Counts.QueuedBytes > 0; i++) Thread.Sleep(10);
        Thread.Sleep(20);   // the writer lowers the count just after the file is listed
    }

    static NgImageLookup Wait(NgImageStore s, string id)
    {
        NgImageLookup f = s.Find(id);
        for (var i = 0; i < 500 && f.State == NgImageState.Queued; i++) { Thread.Sleep(10); f = s.Find(id); }
        return f;
    }

    [Fact]
    public void FilesAreNamedByIdAndCameraAndTheJsonSaysSo()
    {
        using var s = Make();
        Assert.True(s.KeepImage("J-cam1", false, ["too_large"], Img(), 64, 48, imageId: Id, camera: "cam1"));
        Assert.True(s.KeepImage("J-cam2", false, ["no_code"], Img(), 64, 48, imageId: Id, camera: "cam2"));
        Drain(s);
        var bmp = Directory.GetFiles(_dir, "*.bmp", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)!).Order().ToArray();
        Assert.Equal([$"{Id}_cam1.bmp", $"{Id}_cam2.bmp"], bmp);
        var meta = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(_dir, $"{Id}_cam2.json", SearchOption.AllDirectories).Single())).RootElement;
        Assert.Equal((Id, "cam2", "J-cam2"), (meta.GetProperty("image_id").GetString(), meta.GetProperty("camera").GetString(), meta.GetProperty("job").GetString()));
        var f = s.Find(Id);
        Assert.Equal(NgImageState.Found, f.State);
        Assert.Equal(["cam1", "cam2"], f.Files.Select(x => x.Camera).Order().ToArray());
        Assert.All(f.Files, x => Assert.True(File.Exists(x.Path)));
        var (px, w, h) = BmpGray.Read(f.Files.First().Path);
        Assert.Equal((64, 48), (w, h));
        Assert.Equal(Img(), px);
    }

    [Fact]
    public void WithoutAnIdTheOldNamingStaysAndNothingCanBeFound()
    {
        using var s = Make();
        Assert.True(s.Record("J", false, ["x"], Img(), 64, 48));
        Drain(s);
        Assert.Matches(@"^\d{6}_\d{3}_x\.bmp$", Path.GetFileName(Directory.GetFiles(_dir, "*.bmp", SearchOption.AllDirectories).Single()));
        Assert.Equal(NgImageState.Missing, s.Find(Id).State);
    }

    [Fact]
    public void UnsafeCharactersInTheIdAndCameraNeverReachTheFileName()
    {
        using var s = Make();
        Assert.True(s.KeepImage("J", false, ["x"], Img(), 64, 48, imageId: "a/b:c", camera: "cam 1\\ä"));
        Drain(s);
        Assert.Equal("a_b_c_cam_1__.bmp", Path.GetFileName(Directory.GetFiles(_dir, "*.bmp", SearchOption.AllDirectories).Single()));
        Assert.Equal("cam 1\\ä", s.Find("a/b:c").Files.Single().Camera);   // the real name goes to the server
    }

    [Fact]
    public void AnImageStillWaitingForTheWriterIsQueuedNotMissing()
    {
        // Many large images in a row: the one made last is still waiting when the loop ends.
        using var s = Make(queueBytes: 1L << 30);
        var px = Img(512, 512);
        string last = "";
        for (var i = 0; i < 300; i++) { last = $"{Id[..^6]}{i:D6}"; Assert.True(s.KeepImage("J", false, ["x"], px, 512, 512, imageId: last, camera: "cam1")); }
        var now = s.Find(last);
        Assert.Equal(NgImageState.Queued, now.State);
        Assert.Equal(NgImageState.Missing, s.Find("never-seen").State);     // an id nobody made is not "queued"
        var done = Wait(s, last);
        Assert.Equal(NgImageState.Found, done.State);
        Assert.Single(done.Files);
    }

    [Fact]
    public void ImagesTrimmedByTheFolderLimitAreMissingAndTheNewestAreFound()
    {
        using var s = Make(maxFiles: 2);
        for (var i = 1; i <= 4; i++) { s.KeepImage("J", false, ["x"], Img(), 64, 48, imageId: $"id{i}", camera: "cam1"); Drain(s); }
        Assert.Equal(NgImageState.Missing, s.Find("id1").State);
        Assert.Equal(NgImageState.Missing, s.Find("id2").State);
        Assert.Equal(NgImageState.Found, s.Find("id3").State);
        Assert.Equal(NgImageState.Found, s.Find("id4").State);
    }

    [Fact]
    public void ThePressureReliefDropsQueuedImagesAndTheirIdsAreMissing()
    {
        using var s = Make(queueBytes: 1L << 30);
        var px = Img(512, 512);
        for (var i = 0; i < 200; i++) s.KeepImage("J", false, ["x"], px, 512, 512, imageId: $"q{i}", camera: "cam1");
        s.DropQueuedReliever().Relieve();
        Drain(s);
        // whatever was dropped is no longer "queued": nothing is left waiting
        Assert.DoesNotContain(Enumerable.Range(0, 200), i => s.Find($"q{i}").State == NgImageState.Queued);
    }

    [Fact]
    public void IdsSurviveARestartAndOlderFilesWithoutOneAreNotIndexed()
    {
        using (var s = Make())
        {
            s.KeepImage("J-cam1", false, ["x"], Img(), 64, 48, imageId: Id, camera: "cam1");
            s.KeepImage("J-cam2", false, ["x"], Img(), 64, 48, imageId: Id, camera: "cam2");
            s.Record("J", false, ["y"], Img(), 64, 48);   // an image of the old kind
            Drain(s);
        }
        using var again = Make();
        var f = again.Find(Id);
        Assert.Equal(NgImageState.Found, f.State);
        Assert.Equal(["cam1", "cam2"], f.Files.Select(x => x.Camera).Order().ToArray());
        Assert.Equal(3, again.Counts.FilesKept);
        // a damaged .json costs only that file its id
        File.WriteAllText(Directory.GetFiles(_dir, $"{Id}_cam1.json", SearchOption.AllDirectories).Single(), "{ not json");
        using var third = Make();
        Assert.Equal(["cam2"], third.Find(Id).Files.Select(x => x.Camera).ToArray());
    }

    [Fact]
    public void KeepImageNeverThrowsForABadFrameWithAnIdAndLeavesNothingQueued()
    {
        using var s = Make();
        Assert.False(s.KeepImage("J", false, ["x"], new byte[10], 64, 48, imageId: Id, camera: "c"));
        Assert.Equal(NgImageState.Missing, s.Find(Id).State);
    }

    [Fact]
    public void TheRowReaderGivesRowsInFileOrderWithTheirPictureY()
    {
        var path = Path.Combine(_dir, "r.bmp");
        var px = Img(7, 5);
        BmpGray.Write(path, px, 7, 5);
        using var r = BmpGray.Open(path);
        var row = new byte[r.Width];
        var seen = new List<int>();
        int y;
        while ((y = r.ReadRow(row)) >= 0) { seen.Add(y); Assert.Equal(px[(y * 7)..(y * 7 + 7)], row); }
        Assert.Equal([4, 3, 2, 1, 0], seen);
        Assert.Equal(-1, r.ReadRow(row));
    }

    [Fact]
    public void AGreyBmpOfAnyWidthRoundTripsAndOtherFilesAreRefused()
    {
        var path = Path.Combine(_dir, "t.bmp");
        var px = Img(7, 5);   // a width that needs row padding
        BmpGray.Write(path, px, 7, 5);
        var good = File.ReadAllBytes(path);
        var (back, w, h) = BmpGray.Read(path);
        Assert.Equal((7, 5), (w, h));
        Assert.Equal(px, back);
        File.WriteAllBytes(path, new byte[2000]);
        Assert.Throws<InvalidDataException>(() => BmpGray.Read(path));
        File.WriteAllBytes(path, good[..1100]);   // cut short
        Assert.Throws<InvalidDataException>(() => BmpGray.Read(path));
    }
}
