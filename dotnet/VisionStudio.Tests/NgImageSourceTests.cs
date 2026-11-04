using StationAgent;
using StationGuard;
using VisionStudio.Services;
using Xunit;

public sealed class CameraNameTests
{
    static string[] N(params string?[] names) => CameraNames.ForJob(names);

    [Fact]
    public void NamesAreTrimmedControlCharactersReplacedAndCutTo64()
    {
        Assert.Equal(["Top"], N("  Top \t"));
        Assert.Equal(["A_B_C"], N("A\nB\u0000C"));
        var cut = N(new string('x', 100))[0];
        Assert.Equal(64, cut.Length);
        Assert.Equal(new string('x', 64), cut);
        Assert.Equal(63, N(new string('x', 63) + "\U0001F600")[0].Length);   // a pair is never split
    }

    [Fact]
    public void WhatTheServerCallsNotPrintableIsReplacedAndSpacesBecomeOrdinary()
    {
        Assert.Equal(["a b"], N("a\u00A0b"));            // no-break space -> space
        Assert.Equal(["a b"], N("a\u3000b"));            // ideographic space -> space
        Assert.Equal(["a_b"], N("a\u200Bb"));            // zero-width space (format)
        Assert.Equal(["a_b"], N("a\uFEFFb"));            // byte order mark (format)
        Assert.Equal(["a_b"], N("a\u2028b"));            // line separator
        Assert.Equal(["a_b"], N("a\u2029b"));            // paragraph separator
        Assert.Equal(["a_b"], N("a\uE000b"));            // private use
        Assert.Equal(["a_b"], N("a\uD800b"));            // an unpaired surrogate
        Assert.Equal(["a_b"], N("a\U000F0000b"));        // private use beyond the BMP: one "_", not two
        Assert.Equal(["a\U0001F600b"], N("a\U0001F600b"));   // a printable pair stays whole
        Assert.Equal(["Top"], N("\u00A0 Top\u3000"));     // outer spaces of any kind go
        Assert.Equal(["Kamera \u00E9"], N("Kamera \u00E9"));
    }

    [Fact]
    public void DotAndDotDotAreRefusedByTheServerSoTheyBecomeCameraNumbers()
        => Assert.Equal(["Camera 1", "Camera 2", "...", "Camera 4"], N(".", "..", "...", " .. "));

    [Fact]
    public void AnEmptyNameBecomesCameraNumberFromOne()
        => Assert.Equal(["Camera 1", "Side", "Camera 3", "Camera 4"], N("", "Side", "   ", null));

    [Fact]
    public void TwoCamerasNeverShareANameAndTheResultFitsTheLimit()
    {
        Assert.Equal(["Top", "Top (2)", "Top (3)", "top (4)"], N("Top", "Top", "Top ", "top"));
        Assert.Equal(["A", "A (2)", "A (2) (2)"], N("A", "A", "A (2)"));   // a suffix that was already a name
        var r = N(new string('y', 70), new string('y', 64));
        Assert.Equal(64, r[0].Length);
        Assert.Equal(64, r[1].Length);
        Assert.EndsWith(" (2)", r[1]);
        Assert.Equal(r.Length, r.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["Camera 1", "Camera 1 (2)"], N("", "Camera 1"));
    }
}

public sealed class NgImageSourceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ng-source-").FullName;
    readonly NgImageStore _store;
    public NgImageSourceTests() => _store = new NgImageStore(new NgImageOptions { Folder = _dir });
    public void Dispose() { _store.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    sealed class Clock : TimeProvider
    {
        long _t;
        public override long GetTimestamp() => _t;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan d) => _t += d.Ticks;
    }

    // generous: the tests run next to a heavy encoder test on a busy machine
    static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    void Keep(string id, params string[] cameras)
    {
        var px = new byte[200 * 100];
        new Random(3).NextBytes(px);
        foreach (var c in cameras) Assert.True(_store.KeepImage("J-" + c, false, ["x"], px, 200, 100, imageId: id, camera: c));
        for (var i = 0; i < 300 && _store.Find(id).State == NgImageState.Queued; i++) Thread.Sleep(10);
    }

    [Fact]
    public async Task AtMostThePartsPerMinuteAreMadeThenTheAnswerIsNotYetUntilTheMinuteIsUp()
    {
        var clock = new Clock();
        foreach (var id in new[] { "p1", "p2", "p3" }) Keep(id, "c1");
        using var source = new NgImageSource(_store, null, maxPartsPerMinute: 2, wait: Long, time: clock);
        Assert.Equal(ImageState.Found, (await source.GetAsync("p1", default)).State);
        Assert.Equal(ImageState.Found, (await source.GetAsync("p2", default)).State);
        Assert.Equal(ImageState.NotYet, (await source.GetAsync("p3", default)).State);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(ImageState.NotYet, (await source.GetAsync("p3", default)).State);
        clock.Advance(TimeSpan.FromSeconds(2));
        var a = await source.GetAsync("p3", default);
        Assert.Equal(ImageState.Found, a.State);
        Assert.Equal(["c1"], a.Images.Select(x => x.Camera).ToArray());
        // an id the store does not have is answered without using up the rate
        Assert.Equal(ImageState.Missing, (await source.GetAsync("none", default)).State);
    }

    [Fact]
    public async Task NothingIsMadeWhileBusyAndItWorksAgainWhenFree()
    {
        Keep("b1", "c1", "c2");
        var busy = true;
        using var source = new NgImageSource(_store, () => busy, wait: Long);
        Assert.Equal(ImageState.NotYet, (await source.GetAsync("b1", default)).State);
        busy = false;
        var a = await source.GetAsync("b1", default);
        Assert.Equal(ImageState.Found, a.State);
        Assert.Equal(["c1", "c2"], a.Images.Select(x => x.Camera).Order().ToArray());
        Assert.All(a.Images, x => Assert.True(x.Jpeg.Length <= JpegReducer.MaxBytes));
    }

    [Fact]
    public async Task ABusyRefusalDoesNotCountAgainstTheRate()
    {
        Keep("r1", "c1");
        var busy = true;
        using var source = new NgImageSource(_store, () => busy, maxPartsPerMinute: 1, wait: Long, time: new Clock());
        for (var i = 0; i < 5; i++) Assert.Equal(ImageState.NotYet, (await source.GetAsync("r1", default)).State);
        busy = false;
        Assert.Equal(ImageState.Found, (await source.GetAsync("r1", default)).State);
    }

    [Fact]
    public async Task AReductionTheAskStoppedWaitingForIsKeptForTheNextAskAndNotMadeTwice()
    {
        Keep("k1", "c1");
        using var source = new NgImageSource(_store, null, maxPartsPerMinute: 1, wait: TimeSpan.Zero, time: new Clock());
        ImageAnswer a = ImageAnswer.NotYet;
        for (var i = 0; i < 500 && a.State == ImageState.NotYet; i++)
        {
            a = await source.GetAsync("k1", default);   // the rate allows one part: later asks must reuse the running or finished job
            if (a.State == ImageState.NotYet) await Task.Delay(10);
        }
        Assert.Equal(ImageState.Found, a.State);
    }

    [Fact]
    public async Task OnlyADamagedOrMissingFileMeansMissingAnythingElseMeansTryAgain()
    {
        Keep("m1", "c1", "c2");
        Keep("m2", "c1");
        var files = (string id) => _store.Find(id).Files.ToDictionary(f => f.Camera, f => f.Path);
        using var source = new NgImageSource(_store, null, maxPartsPerMinute: 100, wait: Long);
        // one camera's file gone, one damaged: the part is missing (nothing left to send)
        File.Delete(files("m1")["c1"]);
        File.WriteAllBytes(files("m1")["c2"], new byte[50]);
        Assert.Equal(ImageState.Missing, (await source.GetAsync("m1", default)).State);
        // one gone, one fine: what is left is sent
        Keep("m4", "c1", "c2");
        File.Delete(files("m4")["c1"]);
        var four = await source.GetAsync("m4", default);
        Assert.Equal(ImageState.Found, four.State);
        Assert.Equal(["c2"], four.Images.Select(x => x.Camera).ToArray());
        // a file that cannot be opened (here a folder in its place) is not "missing": the server asks again
        var p = files("m2")["c1"];
        File.Delete(p);
        Directory.CreateDirectory(p);
        Assert.Equal(ImageState.NotYet, (await source.GetAsync("m2", default)).State);
        // and once it can be read, the same id is answered
        Directory.Delete(p);
        BmpGray.Write(p, new byte[200 * 100], 200, 100);
        Assert.Equal(ImageState.Found, (await source.GetAsync("m2", default)).State);
    }

    [Fact]
    public void ReduceBmpGivesTheSameJpegAsReducingTheDecodedPixels()
    {
        var px = new byte[301 * 177];   // an odd width: rows are padded in the file
        new Random(5).NextBytes(px);
        var path = Path.Combine(_dir, "x.bmp");
        BmpGray.Write(path, px, 301, 177);
        Assert.Equal(JpegReducer.Reduce(px, 301, 177), JpegReducer.ReduceBmp(path));
    }
}
