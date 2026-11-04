using System.Net;
using System.Text;
using PlantEvents;
using StationAgent;
using Xunit;

/// <summary>The images the plant server asks for in its heartbeat answer: parsing, the HTTP calls and the agent's handling.</summary>
public sealed class ImageRequestTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("station-images-").FullName;
    readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    readonly FakeServer _server = new();
    readonly StationIdentity _id = new("P1", "L4", "S1");
    string Db => Path.Combine(_dir, "queue.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A source that answers per id and records what it was asked, in order.</summary>
    sealed class FakeSource : IImageSource
    {
        public readonly Dictionary<string, ImageAnswer> Answers = [];
        public readonly List<string> Asked = [];
        public Func<string, Task>? Before;
        public async Task<ImageAnswer> GetAsync(string imageId, CancellationToken ct)
        {
            Asked.Add(imageId);
            if (Before is not null) await Before(imageId);
            return Answers.TryGetValue(imageId, out var a) ? a : ImageAnswer.Missing;
        }
    }

    static ImageAnswer Found(params string[] cameras) => ImageAnswer.Found(cameras.Select(c => new CameraImage(c, [0xFF, 0xD8, 0xFF, 1, 2, 3])).ToList());

    static InspectionResult Ng(string part, string? imageId = null) =>
        new() { Ok = false, PartId = part, CycleMs = 40, Reasons = [new NgReason("Lot", "ocv.mismatch")], ImageId = imageId };

    // ---- parsing the heartbeat answer ----

    [Theory]
    [InlineData("""{"ok": true, "image_requests": ["a1", "b2"]}""", new[] { "a1", "b2" })]
    [InlineData("""{"ok": true}""", new string[0])]                                  // an older server
    [InlineData("""{"ok": true, "image_requests": null}""", new string[0])]
    [InlineData("""{"image_requests": "a1"}""", new string[0])]
    [InlineData("""{"image_requests": ["a1", 5, "", null, "a1", "b2"]}""", new[] { "a1", "b2" })]   // only non-empty strings, once each
    [InlineData("""["a1"]""", new string[0])]
    [InlineData("OK", new string[0])]                                                  // not JSON
    [InlineData("<html>502</html>", new string[0])]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void TheAnswerIsParsedAndAnythingOddIsAnEmptyList(string? body, string[] expected)
        => Assert.Equal(expected, HttpPlantLink.ParseImageRequests(body));

    [Fact]
    public void NoMoreThanTenIdsAreTaken()
    {
        var body = "{\"image_requests\":[" + string.Join(",", Enumerable.Range(1, 15).Select(i => $"\"i{i}\"")) + "]}";
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"i{i}"), HttpPlantLink.ParseImageRequests(body));
    }

    sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string? Type, int Bytes)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var n = r.Content is null ? 0 : (await r.Content.ReadAsByteArrayAsync(ct)).Length;
            Calls.Add((r.Method, r.RequestUri!.PathAndQuery, r.Content?.Headers.ContentType?.MediaType, n));
            return answer(r);
        }
    }

    static HttpPlantLink Link(Answering h) => new(new HttpClient(h) { BaseAddress = new Uri("https://plant.local/") }, "t");
    static HttpResponseMessage Text(HttpStatusCode c, string body) => new(c) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task TheHeartbeatReturnsTheRequestsAndToleratesAnOldOrBrokenAnswer()
    {
        foreach (var (body, want) in new[] { ("""{"ok":true,"image_requests":["x1","x2"]}""", 2), ("""{"ok":true}""", 0), ("not json at all", 0) })
        {
            var h = new Answering(_ => Text(HttpStatusCode.OK, body));
            Assert.Equal(want, (await Link(h).SendHeartbeatAsync("{}", default)).ImageRequests.Count);
            Assert.Equal("/vision/ingest/v1/heartbeat", h.Calls.Single().Url);
        }
        // a refusal is still a failure
        await Assert.ThrowsAsync<HttpRequestException>(() => Link(new Answering(_ => Text(HttpStatusCode.Unauthorized, "{}"))).SendHeartbeatAsync("{}", default));
    }

    [Fact]
    public async Task UploadsGoAsJpegWithTheCameraAndTheIdEscaped()
    {
        var h = new Answering(_ => Text(HttpStatusCode.OK, """{"ok":true}"""));
        var link = Link(h);
        await link.UploadImageAsync("20261030T101530123Z-000042", "cam 1/ä", [0xFF, 0xD8, 0xFF, 9], default);
        await link.ImageDoneAsync("a b/c", default);
        await link.ImageMissingAsync("20261030T101530123Z-000042", default);
        Assert.Equal((HttpMethod.Post, "/vision/ingest/v1/images/20261030T101530123Z-000042?camera=cam%201%2F%C3%A4", "image/jpeg", 4), h.Calls[0]);
        Assert.Equal((HttpMethod.Post, "/vision/ingest/v1/images/a%20b%2Fc/done", null, 0), h.Calls[1]);
        Assert.Equal("/vision/ingest/v1/images/20261030T101530123Z-000042/missing", h.Calls[2].Url);
        // 404 (no such result), 413 (too big) and the like throw
        await Assert.ThrowsAsync<HttpRequestException>(() => Link(new Answering(_ => Text(HttpStatusCode.NotFound, "{}"))).UploadImageAsync("x", "c", [1], default));
        await Assert.ThrowsAsync<HttpRequestException>(() => Link(new Answering(_ => Text(HttpStatusCode.RequestEntityTooLarge, "{}"))).ImageDoneAsync("x", default));
    }

    // ---- the agent ----

    [Fact]
    public async Task OnlyThreeAreHandledPerRoundAfterTheEventsAndTheRestFollow()
    {
        using var q = new EventQueue(Db);
        var src = new FakeSource();
        foreach (var i in Enumerable.Range(1, 5)) src.Answers[$"i{i}"] = Found("cam1");
        var a = new Agent(_id, q, _server, time: _clock) { Images = src };
        a.Publish(Ng("P1", "i1"));
        _server.ImageRequests.AddRange(["i1", "i2", "i3", "i4", "i5"]);
        // The events are stored before the first image call.
        int storedAtFirstAsk = -1;
        src.Before = _ => { if (storedAtFirstAsk < 0) storedAtFirstAsk = _server.Stored.Count + _server.ImageCalls.Count * 100; return Task.CompletedTask; };
        var wait = await a.PumpOnceAsync(default);
        Assert.Equal(1, storedAtFirstAsk);                   // the event was stored and no image call had been made yet
        Assert.Equal(["i1", "i2", "i3"], src.Asked);
        Assert.Equal(["up:i1:cam1:6", "done:i1", "up:i2:cam1:6", "done:i2", "up:i3:cam1:6", "done:i3"], _server.ImageCalls);
        Assert.Equal(TimeSpan.Zero, wait);                   // two more are waiting: the next round comes at once
        Assert.Equal(0, q.Depth());
        await a.PumpOnceAsync(default);
        Assert.Equal(["i1", "i2", "i3", "i4", "i5"], src.Asked);
        Assert.Equal(10, _server.ImageCalls.Count);   // an upload and a done for each of the five parts
    }

    [Fact]
    public async Task EveryCameraIsUploadedThenDoneAndAPartWithNoImageIsMissing()
    {
        using var q = new EventQueue(Db);
        var src = new FakeSource();
        src.Answers["two"] = Found("cam1", "cam2");
        src.Answers["gone"] = ImageAnswer.Missing;
        var a = new Agent(_id, q, _server, time: _clock) { Images = src };
        _server.ImageRequests.AddRange(["two", "gone"]);
        await a.PumpOnceAsync(default);
        Assert.Equal(["up:two:cam1:6", "up:two:cam2:6", "done:two", "missing:gone"], _server.ImageCalls);
    }

    [Fact]
    public async Task NotYetIsSkippedWithoutAnyCallAndAskedAgainLater()
    {
        using var q = new EventQueue(Db);
        var src = new FakeSource();
        src.Answers["w"] = ImageAnswer.NotYet;
        var a = new Agent(_id, q, _server, time: _clock) { Images = src };
        _server.ImageRequests.Add("w");
        await a.PumpOnceAsync(default);
        Assert.Empty(_server.ImageCalls);
        _clock.Advance(TimeSpan.FromSeconds(6));
        src.Answers["w"] = Found("cam1");
        await a.PumpOnceAsync(default);
        Assert.Equal(["up:w:cam1:6", "done:w"], _server.ImageCalls);
    }

    [Fact]
    public async Task AFailedUploadIsDroppedWithoutHurtingTheEventsAndTheServerIsNotHammered()
    {
        using var q = new EventQueue(Db);
        var src = new FakeSource();
        src.Answers["bad"] = Found("cam1");
        var a = new Agent(_id, q, _server, time: _clock) { Images = src };
        _server.FailUploads = true;
        _server.ImageRequests.Add("bad");
        a.Publish(Ng("P1", "bad"));
        var wait = await a.PumpOnceAsync(default);
        Assert.True(a.Connected);                                // events and heartbeat went through
        Assert.Equal(0, q.Depth());
        Assert.Equal(TimeSpan.FromSeconds(5), wait);             // no retry storm: the normal pause
        Assert.Equal(["!up:bad:cam1:6"], _server.ImageCalls);    // no "done" after a failed upload
        // asked again at once (next heartbeat): left alone for a short while, then tried again
        _clock.Advance(TimeSpan.FromSeconds(5));
        await a.PumpOnceAsync(default);
        Assert.Single(src.Asked);
        _clock.Advance(TimeSpan.FromSeconds(6));
        _server.FailUploads = false;
        await a.PumpOnceAsync(default);
        Assert.Equal(["!up:bad:cam1:6", "up:bad:cam1:6", "done:bad"], _server.ImageCalls);
    }

    [Fact]
    public async Task ASourceThatThrowsNeverFailsTheRound()
    {
        using var q = new EventQueue(Db);
        var src = new FakeSource { Before = _ => throw new IOException("disk") };
        var a = new Agent(_id, q, _server, time: _clock) { Images = src };
        _server.ImageRequests.Add("x");
        a.Publish(Ng("P1", "x"));
        Assert.Equal(TimeSpan.FromSeconds(5), await a.PumpOnceAsync(default));
        Assert.True(a.Connected);
        Assert.Equal(0, q.Depth());
    }

    [Fact]
    public async Task AnIdAnsweredIsNotAnsweredAgainWhileTheServerKeepsAsking()
    {
        using var q = new EventQueue(Db);
        var src = new FakeSource();
        src.Answers["d"] = Found("cam1");
        var a = new Agent(_id, q, _server, time: _clock) { Images = src };
        _server.ImageRequests.AddRange(["d", "m"]);                // "m" is not on this PC
        for (var i = 0; i < 4; i++) { await a.PumpOnceAsync(default); _clock.Advance(TimeSpan.FromSeconds(5)); }
        Assert.Equal(["up:d:cam1:6", "done:d", "missing:m"], _server.ImageCalls);   // 20 s of asking, answered once
        _clock.Advance(TimeSpan.FromSeconds(60));
        await a.PumpOnceAsync(default);
        Assert.Equal(6, _server.ImageCalls.Count);   // the memory has expired: answered again
    }

    [Fact]
    public async Task WithoutASourceRequestsAreIgnoredAndWhenTheServerIsAwayNothingIsSent()
    {
        using var q = new EventQueue(Db);
        var src = new FakeSource();
        var a = new Agent(_id, q, _server, time: _clock);
        _server.ImageRequests.Add("x");
        await a.PumpOnceAsync(default);
        Assert.Empty(_server.ImageCalls);
        a.Images = src;
        _server.Up = false;
        _clock.Advance(TimeSpan.FromSeconds(6));
        await a.PumpOnceAsync(default);
        Assert.Empty(src.Asked);
    }

    [Fact]
    public async Task AnOldServerAnswerWithoutRequestsLeavesEverythingAsBefore()
    {
        using var q = new EventQueue(Db);
        var src = new FakeSource();
        var a = new Agent(_id, q, _server, time: _clock) { Images = src };
        a.Publish(Ng("P1"));
        await a.PumpOnceAsync(default);
        Assert.Empty(src.Asked);
        Assert.Single(_server.Stored);
    }

    int Stored() { lock (_server) return _server.Stored.Count(e => e.Kind == "inspection.result"); }

    [Fact]
    public async Task ThePublisherPassesTheImageSourceAndTheImageIdToTheServer()
    {
        var o = new PlantLinkOptions
        {
            ServerUrl = "https://plant.local/", Plant = "P1", Line = "L4", Station = "S1",
            Sending = new AgentOptions { HeartbeatEvery = TimeSpan.FromMilliseconds(50), RetryMin = TimeSpan.FromMilliseconds(10), RetryMax = TimeSpan.FromMilliseconds(50) },
        };
        var src = new FakeSource();
        src.Answers["img-1"] = Found("cam1");
        _server.ImageRequests.Add("img-1");
        await using var p = StationPublisher.Start(o, _server, Db);
        p.Images = src;
        p.Result(false, [new NgReason("Lot", "ocv.mismatch")], 40, imageId: "img-1");
        for (var i = 0; i < 200 && !_server.ImageCalls.Contains("done:img-1"); i++) await Task.Delay(25);
        Assert.Contains("done:img-1", _server.ImageCalls);
        for (var i = 0; i < 200 && Stored() == 0; i++) await Task.Delay(25);
        string json;
        lock (_server) json = _server.Stored.Single(e => e.Kind == "inspection.result").Json;
        Assert.Equal("img-1", System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("image_id").GetString());
    }
}

/// <summary>What a refusal, a slow link and a spent time budget do to the image work: never to the events.</summary>
public sealed class ImageFailureTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("station-images2-").FullName;
    readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    readonly FakeServer _server = new();
    readonly StationIdentity _id = new("P1", "L4", "S1");
    string Db => Path.Combine(_dir, "queue.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    sealed class Source : IImageSource
    {
        public readonly List<string> Asked = [];
        public Action<string>? OnAsk;
        public Func<string, ImageAnswer> Answer = id => Many("cam1");
        public Task<ImageAnswer> GetAsync(string imageId, CancellationToken ct) { Asked.Add(imageId); OnAsk?.Invoke(imageId); return Task.FromResult(Answer(imageId)); }
    }

    static ImageAnswer Many(params string[] cameras) => ImageAnswer.Found(cameras.Select(c => new CameraImage(c, [0xFF, 0xD8, 0xFF, 1])).ToList());

    static InspectionResult Ok(string part) => new() { Ok = true, PartId = part, CycleMs = 40 };

    [Fact]
    public async Task A400Or413IsFinalForThatCameraAndAPartWithNoCameraTakenIsMissing()
    {
        using var q = new EventQueue(Db);
        var src = new Source { Answer = id => id == "all" ? Many("c1", "c2") : Many("c1", "c2") };
        var a = new Agent(_id, q, _server, time: _clock) { Images = src };
        _server.UploadStatus["c1"] = System.Net.HttpStatusCode.BadRequest;
        _server.UploadStatus["c2"] = System.Net.HttpStatusCode.RequestEntityTooLarge;
        _server.ImageRequests.Add("all");
        await a.PumpOnceAsync(default);
        Assert.Equal(["!up:all:c1:4", "!up:all:c2:4", "missing:all"], _server.ImageCalls);   // each tried once, then missing
        // one refused, the other taken: done
        _server.ImageCalls.Clear();
        _server.UploadStatus.Remove("c2");
        _server.ImageRequests.Clear();
        _server.ImageRequests.Add("some");
        _clock.Advance(TimeSpan.FromSeconds(6));
        await a.PumpOnceAsync(default);
        Assert.Equal(["!up:some:c1:4", "up:some:c2:4", "done:some"], _server.ImageCalls);
        // and neither is asked for again soon
        _server.ImageRequests.Add("all");
        _clock.Advance(TimeSpan.FromSeconds(6));
        await a.PumpOnceAsync(default);
        Assert.Equal(3, _server.ImageCalls.Count);
    }

    [Fact]
    public async Task AnotherRefusalDropsOnlyThatPartButAServerErrorEndsTheImageWorkOfTheRound()
    {
        using var q = new EventQueue(Db);
        var src = new Source();
        var a = new Agent(_id, q, _server, time: _clock) { Images = src };
        _server.UploadStatus["cam1"] = System.Net.HttpStatusCode.NotFound;
        _server.ImageRequests.AddRange(["a", "b"]);
        await a.PumpOnceAsync(default);
        Assert.Equal(["a", "b"], src.Asked);                       // 404: the next part is still tried
        Assert.Equal(["!up:a:cam1:4", "!up:b:cam1:4"], _server.ImageCalls);

        _server.UploadStatus["cam1"] = System.Net.HttpStatusCode.BadGateway;
        _server.ImageCalls.Clear();
        src.Asked.Clear();
        _server.ImageRequests.Clear();
        _server.ImageRequests.AddRange(["c", "d", "e"]);
        _clock.Advance(TimeSpan.FromSeconds(6));
        var wait = await a.PumpOnceAsync(default);
        Assert.Equal(["c"], src.Asked);                            // 502: stop at once
        Assert.Equal(TimeSpan.FromSeconds(5), wait);               // and no hurry to come back
        Assert.True(a.Connected);
    }

    [Fact]
    public async Task TheTimeBudgetEndsTheImageWorkAndTheNextRoundSendsEventsFirst()
    {
        using var q = new EventQueue(Db);
        var src = new Source();
        // every part takes 2 s of the budget (3 s): two parts fit, the third waits
        var a = new Agent(_id, q, _server, new AgentOptions { ImageBudget = TimeSpan.FromSeconds(3) }, _clock) { Images = src };
        src.OnAsk = _ => _clock.Advance(TimeSpan.FromSeconds(2));
        _server.ImageRequests.AddRange(["a", "b", "c"]);
        var wait = await a.PumpOnceAsync(default);
        Assert.Equal(["a", "b"], src.Asked);
        Assert.Equal(TimeSpan.Zero, wait);                         // leftovers continue at once
        a.Publish(Ok("late"));
        int storedWhenCWasAsked = -1;
        src.OnAsk = id => { if (id == "c") storedWhenCWasAsked = _server.Stored.Count; };
        await a.PumpOnceAsync(default);
        Assert.Equal(["a", "b", "c"], src.Asked);
        Assert.Equal(1, storedWhenCWasAsked);                      // the event went out before the image work
    }

    [Fact]
    public async Task ASlowImageLinkTimesOutShortAndTheEventsStillGoOutOnTheNextRound()
    {
        using var q = new EventQueue(Db);
        var src = new Source();
        var a = new Agent(_id, q, _server, new AgentOptions { ImageCallTimeout = TimeSpan.FromMilliseconds(150) }, _clock) { Images = src };
        _server.ImageDelay = ct => Task.Delay(Timeout.Infinite, ct);   // an upload that never answers
        _server.ImageRequests.AddRange(["a", "b"]);
        a.Publish(Ok("1"));
        var t = System.Diagnostics.Stopwatch.StartNew();
        var wait = await a.PumpOnceAsync(default);
        Assert.InRange(t.ElapsedMilliseconds, 100, 5000);
        Assert.Equal(["a"], src.Asked);                            // stopped at the first timeout
        Assert.Equal(TimeSpan.FromSeconds(5), wait);
        Assert.True(a.Connected);
        Assert.Single(_server.Stored);                             // the event was already out
        a.Publish(Ok("2"));
        _server.ImageDelay = null;
        _clock.Advance(TimeSpan.FromSeconds(5));
        await a.PumpOnceAsync(default);
        Assert.Equal(2, _server.Stored.Count);                     // and the next event is not held back
    }

    [Fact]
    public async Task WhenTheBudgetRunsOutInTheMiddleOfAPartNothingIsPostedAndTheNextAskStartsItAgain()
    {
        using var q = new EventQueue(Db);
        var src = new Source { Answer = _ => Many("c1", "c2", "c3", "c4", "c5", "c6", "c7", "c8") };
        var a = new Agent(_id, q, _server, new AgentOptions { ImageBudget = TimeSpan.FromSeconds(3) }, _clock) { Images = src };
        _server.ImageDelay = _ => { _clock.Advance(TimeSpan.FromSeconds(1)); return Task.CompletedTask; };   // every call takes 1 s
        _server.ImageRequests.AddRange(["p", "q"]);
        await a.PumpOnceAsync(default);
        Assert.Equal(["up:p:c1:4", "up:p:c2:4", "up:p:c3:4"], _server.ImageCalls);   // no done, no missing, and not the second part
        Assert.Equal(["p"], src.Asked);
        // the server asks again at the next heartbeat; with a fast link the rest of the part goes (c1 to c3 are not sent again)
        _server.ImageDelay = null;
        _server.ImageCalls.Clear();
        _clock.Advance(TimeSpan.FromSeconds(6));
        await a.PumpOnceAsync(default);
        Assert.Equal(["p", "p", "q"], src.Asked);
        Assert.Equal(Enumerable.Range(4, 5).Select(i => $"up:p:c{i}:4").Append("done:p").Concat(Enumerable.Range(1, 8).Select(i => $"up:q:c{i}:4")).Append("done:q"), _server.ImageCalls);
    }

    [Fact]
    public async Task APartThatNeedsSeveralAsksGoesOnWhereItStoppedAndEachCameraIsUploadedOnce()
    {
        using var q = new EventQueue(Db);
        var src = new Source { Answer = _ => Many("c1", "c2", "c3", "c4", "c5", "c6", "c7", "c8") };
        var a = new Agent(_id, q, _server, new AgentOptions { ImageBudget = TimeSpan.FromSeconds(3) }, _clock) { Images = src };
        _server.ImageDelay = _ => { _clock.Advance(TimeSpan.FromSeconds(1)); return Task.CompletedTask; };   // every call takes 1 s
        _server.ImageRequests.Add("p");
        await a.PumpOnceAsync(default);
        Assert.Equal(["up:p:c1:4", "up:p:c2:4", "up:p:c3:4"], _server.ImageCalls);
        _clock.Advance(TimeSpan.FromSeconds(6));
        await a.PumpOnceAsync(default);
        Assert.Equal(["up:p:c4:4", "up:p:c5:4", "up:p:c6:4"], _server.ImageCalls.Skip(3).ToArray());
        _clock.Advance(TimeSpan.FromSeconds(6));
        await a.PumpOnceAsync(default);
        Assert.Equal(["up:p:c7:4", "up:p:c8:4", "done:p"], _server.ImageCalls.Skip(6).ToArray());
        Assert.Equal(3, src.Asked.Count);                          // three asks, nothing uploaded twice
        Assert.Equal(9, _server.ImageCalls.Count);
        // once done, the memory of cameras is gone: a later request for the same id (after the answer memory) starts afresh
        _server.ImageDelay = null;
        _clock.Advance(TimeSpan.FromSeconds(70));
        await a.PumpOnceAsync(default);
        Assert.Equal(9 + 9, _server.ImageCalls.Count);
    }
}

public sealed class ChangeoverAnswerTests
{
    [Fact]
    public void TheAnswerIsReadOnlyInTheContractsShape()
    {
        var a = HttpPlantLink.ParseChangeover("""{"ok":true,"changeover":{"required":true,"code":"ab","ask":4,"state":"accepted","by":"Priya","at":12,"note":""}}""");
        Assert.Equal(new ChangeoverAnswer(true, "ab", 4, "accepted", "Priya", 12, ""), a);
        Assert.Null(HttpPlantLink.ParseChangeover("""{"ok":true}"""));
        Assert.Null(HttpPlantLink.ParseChangeover("""{"changeover":{"required":true,"state":"yes"}}"""));
        Assert.Null(HttpPlantLink.ParseChangeover("""{"changeover":{"required":"true","state":"accepted"}}"""));
        Assert.Null(HttpPlantLink.ParseChangeover("not json"));
    }
}
