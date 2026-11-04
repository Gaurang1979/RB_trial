using System.Text.Json;
using PlantEvents;
using StationAgent;
using Xunit;

public sealed class PublisherTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("station-publisher-").FullName;
    readonly FakeServer _server = new();
    string Db => Path.Combine(_dir, "queue.db");

    static PlantLinkOptions Opt() => new()
    {
        ServerUrl = "https://plant.local/", Plant = "P1", Line = "L4", Station = "S1",
        CountersEvery = TimeSpan.FromMilliseconds(100),
        Sending = new AgentOptions { HeartbeatEvery = TimeSpan.FromMilliseconds(50), RetryMin = TimeSpan.FromMilliseconds(10), RetryMax = TimeSpan.FromMilliseconds(50) },
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    List<JsonElement> Stored()
    {
        lock (_server) return _server.Stored.Select(q => JsonDocument.Parse(q.Json).RootElement).ToList();
    }

    async Task<List<JsonElement>> Until(Func<List<JsonElement>, bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            var s = Stored();
            if (done(s)) return s;
            await Task.Delay(25);
        }
        return Stored();
    }

    [Fact]
    public async Task EventsReachTheServerInOrderAndRepeatsAreSentOnce()
    {
        await using var p = StationPublisher.Start(Opt(), _server, Db);
        Assert.True(p.Enabled);
        p.Job("Carton-24");
        p.Job("Carton-24");
        p.State(LineStateValue.Running);
        p.State(LineStateValue.Running);
        p.Result(true, [], 40, partId: "A1");
        p.Result(false, [new NgReason("Lot", "ocv.mismatch")], double.NaN);
        p.Alarm("camera_error", true, AlarmPriority.High);
        p.Alarm("camera_error", true, AlarmPriority.High);
        p.Alarm("camera_error", false);
        var s = await Until(x => x.Count >= 6);
        Assert.Equal(new[] { "job.changed", "line.state", "inspection.result", "inspection.result", "alarm.raised", "alarm.cleared" },
            s.Take(6).Select(e => e.GetProperty("kind").GetString()).ToArray());
        Assert.Equal("Carton-24", s[2].GetProperty("job").GetString());          // results carry the job
        Assert.Equal(0, s[3].GetProperty("cycle_ms").GetDouble());              // a NaN time is never sent
        Assert.All(s, e => Assert.Equal("S1", e.GetProperty("station").GetString()));
        Assert.Equal(Enumerable.Range(1, s.Count).Select(i => (long)i), s.Select(e => e.GetProperty("seq").GetInt64()));
    }

    [Fact]
    public async Task WhileTheServerIsAwayEventsWaitOnDiskAndGoWhenItIsBack()
    {
        _server.Up = false;
        await using var p = StationPublisher.Start(Opt(), _server, Db);
        for (var i = 0; i < 50; i++) p.Result(true, [], 30);
        var st = p.Status;
        for (var i = 0; i < 100 && st.Waiting < 50; i++) { await Task.Delay(20); st = p.Status; }
        Assert.Equal(50, st.Waiting);
        Assert.False(st.Connected);
        _server.Up = true;
        Assert.Equal(50, (await Until(x => x.Count >= 50)).Count);
    }

    [Fact]
    public async Task CountersAreSnapshotted()
    {
        await using var p = StationPublisher.Start(Opt(), _server, Db);
        p.Counters = () => (7, 2, 1_761_700_000_000);
        var s = await Until(x => x.Any(e => e.GetProperty("kind").GetString() == "counter.snapshot"));
        var c = s.First(e => e.GetProperty("kind").GetString() == "counter.snapshot");
        Assert.Equal((7, 2, 9), (c.GetProperty("good").GetInt64(), c.GetProperty("bad").GetInt64(), c.GetProperty("total").GetInt64()));
    }

    [Fact]
    public void NotConfiguredIsOffAndDoesNothing()
    {
        using var p = StationPublisher.Start(new PlantLinkOptions());
        Assert.False(p.Enabled);
        Assert.Null(p.Error);
        p.Result(true, [], 1);
        Assert.Equal(new PlantLinkStatus(false, false, 0, 0, null), p.Status);
    }

    [Fact]
    public void AMissingTokenOrAPlainHttpAddressLeavesTheLinkOffWithAReason()
    {
        var o = Opt();
        o.QueueFile = Db;
        o.TokenFile = Path.Combine(_dir, "no-such-token.txt");
        using (var p = StationPublisher.Start(o)) { Assert.False(p.Enabled); Assert.Contains("plant link off", p.Error); }
        File.WriteAllText(o.TokenFile, "secret\n");
        o.ServerUrl = "http://plant.local/";
        using (var p = StationPublisher.Start(o)) { Assert.False(p.Enabled); Assert.Contains("https", p.Error); }
    }

    [Fact]
    public async Task AQueueFileOfAnotherStationLeavesTheLinkOff()
    {
        await using (var p = StationPublisher.Start(Opt(), _server, Db)) p.Result(true, [], 1);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var other = Opt();
        other.Station = "S2";
        await using var q = StationPublisher.Start(other, _server, Db);
        Assert.False(q.Enabled);
        Assert.NotNull(q.Error);
    }

    [Fact]
    public async Task AlarmsAreSentAgainOnlyWhenTheirPriorityChangesAndJobsWhenTheirVersionDoes()
    {
        await using var p = StationPublisher.Start(Opt(), _server, Db);
        p.Alarm("pc_pressure", true, AlarmPriority.Medium);
        p.Alarm("pc_pressure", true, AlarmPriority.Medium);
        p.Alarm("pc_pressure", true, AlarmPriority.High);
        p.Alarm("pc_pressure", false);
        p.Alarm("pc_pressure", false);
        p.Job("J", 1);
        p.Job("J", 1);
        p.Job("J", 2);
        p.Result(true, [], 5);
        var s = await Until(x => x.Count >= 6);
        Assert.Equal(new[] { "alarm.raised:medium", "alarm.raised:high", "alarm.cleared:high", "job.changed:", "job.changed:", "inspection.result:" },
            s.Select(e => e.GetProperty("kind").GetString() + ":" + (e.TryGetProperty("priority", out var pr) ? pr.GetString() : "")).ToArray());
        Assert.Equal(2, s[5].GetProperty("job_version").GetInt32());
    }

    [Fact]
    public async Task AnNgAlwaysArrivesWithAReasonAndWithoutBadNumbers()
    {
        await using var p = StationPublisher.Start(Opt(), _server, Db);
        p.Result(false, [], 5);
        p.Result(false, [new NgReason(" ", "x"), new NgReason("Lot", "")], 5, values: new Dictionary<string, double> { ["a"] = 1, ["b"] = double.NaN });
        var s = await Until(x => x.Count >= 2);
        Assert.Equal(2, s.Count);
        Assert.All(s, e => Assert.Equal("station", e.GetProperty("reasons")[0].GetProperty("tool").GetString()));
        Assert.Single(s[1].GetProperty("values").EnumerateObject());
        Assert.Equal(0, p.Status.Lost);
    }

    [Fact]
    public async Task AFullMemoryQueueLosesAndCountsInsteadOfWaiting()
    {
        var o = Opt();
        o.MemoryQueue = 10;
        _server.Up = false;
        await using var p = StationPublisher.Start(o, _server, Db);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5000; i++) p.Result(true, [], 1);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"publishing took {sw.ElapsedMilliseconds} ms");
        var st = p.Status;
        for (var i = 0; i < 100 && st.Waiting + st.Lost < 5000; i++) { await Task.Delay(20); st = p.Status; }
        Assert.Equal(5000, st.Waiting + st.Lost);   // every event is either kept or counted, none vanishes
    }

    [Fact]
    public async Task ClosingStoresWhatIsStillInMemory()
    {
        _server.Up = false;
        await using (var p = StationPublisher.Start(Opt(), _server, Db))
            for (var i = 0; i < 300; i++) p.Result(true, [], 1);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var q = new EventQueue(Db);
        Assert.Equal(300, q.Depth());
        Assert.Equal(300, q.LastSeq());
    }
}
