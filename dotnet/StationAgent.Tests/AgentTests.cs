using PlantEvents;
using StationAgent;
using Xunit;

/// <summary>A wall clock and a monotonic clock that move together, unless the wall clock is set.</summary>
sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now = start;
    long _mono;
    public override DateTimeOffset GetUtcNow() => Now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _mono;
    public void Advance(TimeSpan t) { Now += t; _mono += t.Ticks; }
    public void SetWall(DateTimeOffset t) => Now = t;
}

/// <summary>A plant server that can be switched off, and that stores what it is sent.</summary>
sealed class FakeServer : IPlantLink
{
    public bool Up = true;
    public int TakeAtMost = int.MaxValue;
    public long AckOverride = -1;
    public TimeSpan Hang = TimeSpan.Zero;
    public readonly List<QueuedEvent> Stored = [];
    public readonly List<string> Heartbeats = [];

    public Task<long> SendAsync(IReadOnlyList<QueuedEvent> batch, CancellationToken ct)
    {
        if (!Up) throw new IOException("server unreachable");
        if (Hang > TimeSpan.Zero) return HangThen(ct);
        var take = batch.Take(TakeAtMost).ToList();
        lock (this) Stored.AddRange(take);
        return Task.FromResult(AckOverride >= 0 ? AckOverride : take[^1].Seq);
    }

    async Task<long> HangThen(CancellationToken ct) { await Task.Delay(Hang, ct); return 0; }

    public Task<HeartbeatAnswer> SendHeartbeatAsync(string json, CancellationToken ct)
    {
        if (!Up) throw new IOException("server unreachable");
        Heartbeats.Add(json);
        return Task.FromResult(new HeartbeatAnswer(ImageRequests.ToArray(), Changeover));
    }

    /// <summary>The changeover answer of each heartbeat.</summary>
    public ChangeoverAnswer? Changeover;

    /// <summary>What the server asks for in each heartbeat answer; it keeps asking until the test clears it.</summary>
    public readonly List<string> ImageRequests = [];
    /// <summary>Every image call in order: "up:id:camera:bytes", "done:id", "missing:id", and the same with "!" first when it failed.</summary>
    public readonly List<string> ImageCalls = [];
    /// <summary>Uploads fail without a status (a link that is down).</summary>
    public bool FailUploads;
    /// <summary>Uploads of these cameras are refused with this status.</summary>
    public readonly Dictionary<string, System.Net.HttpStatusCode> UploadStatus = [];
    /// <summary>Runs first in every image call (a slow or hanging link).</summary>
    public Func<CancellationToken, Task>? ImageDelay;

    public Task UploadImageAsync(string imageId, string camera, byte[] jpeg, CancellationToken ct)
        => Image($"up:{imageId}:{camera}:{jpeg.Length}", FailUploads, UploadStatus.TryGetValue(camera, out var s) ? s : null, ct);
    public Task ImageDoneAsync(string imageId, CancellationToken ct) => Image($"done:{imageId}", false, null, ct);
    public Task ImageMissingAsync(string imageId, CancellationToken ct) => Image($"missing:{imageId}", false, null, ct);

    async Task Image(string call, bool fail, System.Net.HttpStatusCode? status, CancellationToken ct)
    {
        if (ImageDelay is not null) await ImageDelay(ct);
        var bad = fail || status is not null;
        lock (this) ImageCalls.Add((bad ? "!" : "") + call);
        if (status is { } st) throw new HttpRequestException($"plant server answered {(int)st}", null, st);
        if (fail) throw new HttpRequestException("plant server unreachable");
    }
}

public sealed class AgentTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("station-agent-").FullName;
    readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
    readonly FakeServer _server = new();
    readonly StationIdentity _id = new("P1", "L4", "S1");
    string Db => Path.Combine(_dir, "queue.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static InspectionResult Ok(string part) => new() { Ok = true, PartId = part, CycleMs = 40 };

    async Task Drain(Agent a)
    {
        for (var i = 0; i < 100; i++)
            if (await a.PumpOnceAsync(CancellationToken.None) > TimeSpan.Zero) return;
    }

    [Fact]
    public void SequenceIsGapFreeAndSurvivesARestart()
    {
        using (var q = new EventQueue(Db))
        {
            var a = new Agent(_id, q, _server, time: _clock);
            Assert.True(a.Publish(Ok("1")));
            Assert.True(a.Publish(Ok("2")));
        }
        using (var q = new EventQueue(Db))
        {
            var a = new Agent(_id, q, _server, time: _clock);
            Assert.True(a.Publish(Ok("3")));
            Assert.Equal([1L, 2L, 3L], q.Peek(10).Select(e => e.Seq));
            Assert.Equal(3, q.LastSeq());
        }
    }

    [Fact]
    public void StoredEventCarriesItsSequenceAndStation()
    {
        using var q = new EventQueue(Db);
        new Agent(_id, q, _server, time: _clock).Publish(Ok("A"));
        var e = (InspectionResult)PlantEventJson.Parse(q.Peek(1)[0].Json);
        Assert.Equal((1L, "P1", "L4", "S1", "A"), (e.Seq, e.Plant, e.Line, e.Station, e.PartId));
        Assert.Equal(_clock.Now.ToUnixTimeMilliseconds(), e.Ts);
    }

    [Fact]
    public async Task NothingIsLostWhileTheServerIsAway()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, time: _clock);
        _server.Up = false;
        for (var i = 1; i <= 450; i++) a.Publish(Ok(i.ToString()));
        Assert.True(await a.PumpOnceAsync(CancellationToken.None) > TimeSpan.Zero);
        Assert.False(a.Connected);
        Assert.Equal(450, q.Depth());

        _server.Up = true;
        await Drain(a);
        Assert.True(a.Connected);
        Assert.Equal(0, q.Depth());
        Assert.Equal(Enumerable.Range(1, 450).Select(i => (long)i), _server.Stored.Select(e => e.Seq));
    }

    [Fact]
    public async Task RetryWaitDoublesUpToTheLimitAndResetsOnSuccess()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { RetryMin = TimeSpan.FromSeconds(1), RetryMax = TimeSpan.FromSeconds(4) }, _clock);
        a.Publish(Ok("1"));
        _server.Up = false;
        var waits = new List<double>();
        for (var i = 0; i < 5; i++) { _clock.Advance(TimeSpan.FromSeconds(10)); waits.Add((await a.PumpOnceAsync(default)).TotalSeconds); }
        Assert.Equal([1, 2, 4, 4, 4], waits);
        _server.Up = true;
        await a.PumpOnceAsync(default);
        _server.Up = false;
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, (await a.PumpOnceAsync(default)).TotalSeconds);
    }

    [Fact]
    public async Task APartialAcceptanceSendsTheRestNextRound()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, time: _clock);
        for (var i = 1; i <= 5; i++) a.Publish(Ok(i.ToString()));
        _server.TakeAtMost = 2;
        Assert.Equal(TimeSpan.Zero, await a.PumpOnceAsync(default));
        Assert.Equal(3, q.Depth());
        await Drain(a);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], _server.Stored.Select(e => e.Seq));
    }

    [Fact]
    public async Task AnAcknowledgementPastTheBatchMarksNothing()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { BatchSize = 2 }, _clock);
        for (var i = 1; i <= 4; i++) a.Publish(Ok(i.ToString()));
        _server.AckOverride = 99; // a server talking about another queue's numbers
        Assert.True(await a.PumpOnceAsync(default) > TimeSpan.Zero);
        Assert.Equal(4, q.Depth());
        Assert.Equal(1, a.ProtocolErrors);
        Assert.False(a.Connected);
    }

    [Fact]
    public async Task HeartbeatTellsTheServerHowFarTheStationHasGot()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { AgentVersion = "9.9" }, _clock);
        a.Publish(Ok("1")); a.Publish(Ok("2"));
        _server.TakeAtMost = 0; // nothing accepted yet
        _server.AckOverride = 0;
        await a.PumpOnceAsync(default);
        var hb = (Heartbeat)PlantEventJson.Parse(_server.Heartbeats.Single());
        Assert.Equal((2L, 2L, "9.9"), (hb.Seq, hb.QueueDepth, hb.AgentVersion));
        Assert.Empty(hb.Validate());
    }

    [Fact]
    public async Task HeartbeatIsSentOnlyWhenDue()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { HeartbeatEvery = TimeSpan.FromSeconds(5) }, _clock);
        await a.PumpOnceAsync(default);
        _clock.Advance(TimeSpan.FromSeconds(2));
        await a.PumpOnceAsync(default);
        _clock.Advance(TimeSpan.FromSeconds(3));
        await a.PumpOnceAsync(default);
        Assert.Equal(2, _server.Heartbeats.Count);
    }

    [Fact]
    public async Task OldUnsentEventsAreDroppedAndCounted()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { Retention = TimeSpan.FromDays(7), PruneEvery = TimeSpan.FromHours(1) }, _clock);
        _server.Up = false;
        a.Publish(Ok("old"));
        _clock.Advance(TimeSpan.FromDays(8));
        a.Publish(Ok("new"));
        await a.PumpOnceAsync(default);
        Assert.Equal(1, q.Dropped());
        _server.Up = true;
        await Drain(a);
        Assert.Equal([2L], _server.Stored.Select(e => e.Seq)); // the server sees seq 1 missing
        var hb = (Heartbeat)PlantEventJson.Parse(_server.Heartbeats.Last());
        Assert.Equal(1, hb.Dropped);
    }

    [Fact]
    public async Task SentEventsAreForgottenAfterKeepSent()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { KeepSent = TimeSpan.FromDays(1), PruneEvery = TimeSpan.FromHours(1) }, _clock);
        a.Publish(Ok("1"));
        await a.PumpOnceAsync(default);
        _clock.Advance(TimeSpan.FromDays(2));
        await a.PumpOnceAsync(default);
        Assert.Equal(0, q.Dropped());
        a.Publish(Ok("2"));
        Assert.Equal([2L], q.Peek(10).Select(e => e.Seq)); // numbering goes on after a prune
    }

    [Fact]
    public void PublishNeverThrowsAndCountsFailures()
    {
        var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, time: _clock);
        Assert.False(a.Publish(new InspectionResult { Ok = false })); // an NG with no reason
        Assert.False(a.Publish(new Heartbeat()));                    // heartbeats are not stored
        q.Dispose();
        Assert.False(a.Publish(Ok("after the disk went away")));
        Assert.Equal(3, a.PublishFailures);
    }

    [Fact]
    public void QueueRefusesHeartbeats()
    {
        using var q = new EventQueue(Db);
        Assert.Throws<ArgumentException>(() => q.Enqueue(new Heartbeat(), 1));
    }

    [Fact]
    public void AnEventThatCannotBeStoredLeavesNoGap()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, time: _clock);
        var bad = new InspectionResult { Ok = true, CycleMs = 10, Values = new Dictionary<string, double> { ["offset"] = double.NaN } };
        Assert.False(a.Publish(bad));
        Assert.False(a.Publish(new InspectionResult { Ok = true, CycleMs = double.PositiveInfinity }));
        Assert.True(a.Publish(Ok("good")));
        Assert.Equal([1L], q.Peek(10).Select(e => e.Seq));
        Assert.Equal(2, a.PublishFailures);
    }

    [Fact]
    public async Task AWrongClockNeverDeletesUnsentEvents()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { PruneEvery = TimeSpan.Zero }, _clock);
        _server.Up = false;
        a.Publish(Ok("1"));
        a.Publish(Ok("2") with { Ts = 1 }); // a caller with a broken clock
        _clock.SetWall(_clock.Now.AddDays(30)); // the wall clock jumps; the server was away only seconds
        await a.PumpOnceAsync(default);
        Assert.Equal(0, q.Dropped());
        Assert.Equal(2, q.Depth());
    }

    [Fact]
    public async Task AClockSetBackPrunesNothing()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { PruneEvery = TimeSpan.Zero, KeepSent = TimeSpan.Zero }, _clock);
        a.Publish(Ok("1"));
        await a.PumpOnceAsync(default); // sent
        Assert.Equal(0, q.Prune(_clock.Now.AddDays(-1).ToUnixTimeMilliseconds(), TimeSpan.Zero, TimeSpan.Zero, dropUnsent: true));
    }

    [Fact]
    public async Task AHangingServerCallTimesOut()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { CallTimeout = TimeSpan.FromMilliseconds(100) }, _clock);
        a.Publish(Ok("1"));
        _server.Hang = TimeSpan.FromMinutes(5);
        await a.PumpOnceAsync(default); // the heartbeat goes through; the batch hangs
        Assert.False(a.Connected);
        Assert.Equal(1, q.Depth());
    }

    [Fact]
    public void ANewQueueFileHasANewIdAndEventsCarryIt()
    {
        string first;
        using (var q = new EventQueue(Db)) { first = q.QueueId; new Agent(_id, q, _server, time: _clock).Publish(Ok("1")); }
        using (var q = new EventQueue(Db)) Assert.Equal(first, q.QueueId);
        using var other = new EventQueue(Path.Combine(_dir, "other.db"));
        Assert.NotEqual(first, other.QueueId);
        using var again = new EventQueue(Db);
        Assert.Equal(first, PlantEventJson.Parse(again.Peek(1)[0].Json).QueueId);
    }

    [Fact]
    public void AQueueFileServesOneStationOnly()
    {
        using var q = new EventQueue(Db);
        _ = new Agent(_id, q, _server, time: _clock);
        Assert.Throws<InvalidOperationException>(() => new Agent(_id with { Station = "S2" }, q, _server, time: _clock));
    }

    [Fact]
    public async Task PruningALargeBacklogKeepsCountsRight()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { PruneEvery = TimeSpan.Zero, Retention = TimeSpan.FromDays(7) }, _clock);
        _server.Up = false;
        for (var i = 0; i < 12_000; i++) a.Publish(Ok(i.ToString()));
        _clock.Advance(TimeSpan.FromDays(8));
        a.Publish(Ok("fresh"));
        await a.PumpOnceAsync(default);
        Assert.Equal(12_000, q.Dropped());
        Assert.Equal(1, q.Depth());
        Assert.Equal(12_001, q.LastSeq());
    }

    [Fact]
    public void PublishFromManyThreadsKeepsTheSequenceGapFree()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, time: _clock);
        Parallel.For(0, 400, i => Assert.True(a.Publish(Ok(i.ToString()))));
        Assert.Equal(Enumerable.Range(1, 400).Select(i => (long)i), q.Peek(1000).Select(e => e.Seq));
    }

    [Fact]
    public async Task RunStopsWhenCancelled()
    {
        using var q = new EventQueue(Db);
        var a = new Agent(_id, q, _server, new AgentOptions { HeartbeatEvery = TimeSpan.FromMilliseconds(10) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.RunAsync(cts.Token));
        Assert.NotEmpty(_server.Heartbeats);
    }
}
