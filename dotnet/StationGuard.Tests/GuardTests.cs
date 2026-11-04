using StationGuard;
using Xunit;

sealed class Clock : TimeProvider
{
    public DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    long _mono;
    public override DateTimeOffset GetUtcNow() => Now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _mono;
    public void Advance(TimeSpan t) { Now += t; _mono += t.Ticks; }
}

/// <summary>Readings set by the test.</summary>
sealed class Probe(Clock clock) : IResourceProbe
{
    public long Private = 1L << 30, SystemLoad = 4L << 30, SystemTotal = 16L << 30, DiskFree = 500L << 30, DiskTotal = 1000L << 30;
    public ResourceSample Read() => new(clock.Now, Private, Private, Private / 2, SystemLoad, SystemTotal, 10, 500, 40, DiskFree, DiskTotal);
}

public class GuardTests
{
    readonly Clock _clock = new();
    const long GB = 1L << 30, MB = 1L << 20;

    (ResourceGuard G, Probe P) Make(GuardOptions? o = null)
    {
        var p = new Probe(_clock);
        return (new ResourceGuard(p, o ?? new GuardOptions { BudgetBytes = 4 * GB, StepCooldown = TimeSpan.Zero }, _clock), p);
    }

    [Fact]
    public void PartInProgressIsTrueBetweenPartStartedAndPartFinished()
    {
        var (g, _) = Make();
        Assert.False(g.PartInProgress);
        g.PartStarted();
        Assert.True(g.PartInProgress);
        g.PartFinished();
        Assert.False(g.PartInProgress);
        g.PartFinished();   // an extra finish never leaves it stuck
        g.PartStarted();
        Assert.True(g.PartInProgress);
    }

    GuardStatus Tick(ResourceGuard g) { _clock.Advance(TimeSpan.FromSeconds(5)); return g.Tick(); }

    [Fact]
    public void LevelsFollowMemoryAgainstTheBudget()
    {
        var (g, p) = Make();
        Assert.Equal(Pressure.Normal, Tick(g).Level);
        p.Private = (long)(4 * GB * .72);
        Assert.Equal(Pressure.Elevated, Tick(g).Level);
        p.Private = (long)(4 * GB * .9);
        Assert.Equal(Pressure.High, Tick(g).Level);
        p.Private = (long)(4 * GB * .97);
        var s = Tick(g);
        Assert.Equal(Pressure.Critical, s.Level);
        Assert.Contains("memory", s.Reason);
    }

    [Fact]
    public void ItStepsDownOnlyAfterStayingLowWithMargin()
    {
        var (g, p) = Make(new GuardOptions { BudgetBytes = 4 * GB, StepDownSamples = 3, StepCooldown = TimeSpan.Zero });
        p.Private = (long)(4 * GB * .9);
        Tick(g);
        p.Private = (long)(4 * GB * .83); // below High but inside the 5% margin: stays High
        for (var i = 0; i < 5; i++) Assert.Equal(Pressure.High, Tick(g).Level);
        p.Private = (long)(4 * GB * .5);
        Assert.Equal(Pressure.High, Tick(g).Level);
        Assert.Equal(Pressure.High, Tick(g).Level);
        Assert.Equal(Pressure.Elevated, Tick(g).Level); // one level at a time
    }

    [Fact]
    public void PcMemoryAndDiskCountToo()
    {
        var (g, p) = Make();
        p.SystemLoad = (long)(16 * GB * .92);
        Assert.Equal(Pressure.High, Tick(g).Level);
        var (g2, p2) = Make();
        p2.DiskFree = 20L << 30; // 2% free
        var s = Tick(g2);
        Assert.Equal(Pressure.Critical, s.Level);
        Assert.Contains("disk", s.Reason);
    }

    [Fact]
    public void StepsRunOneAtATimeCheapestFirstAndOnlyBetweenParts()
    {
        var (g, p) = Make();
        var ran = new List<string>();
        g.Add(new Reliever("compact", Pressure.High, TimeSpan.FromMilliseconds(300), () => { ran.Add("compact"); return 0; }));
        g.Add(new Reliever("trim-cache", Pressure.Elevated, TimeSpan.FromMilliseconds(5), () => { ran.Add("trim-cache"); return 0; }));
        g.Add(new Reliever("drop-previews", Pressure.Elevated, TimeSpan.FromMilliseconds(5), () => { ran.Add("drop-previews"); return 0; }));
        p.Private = (long)(4 * GB * .9);
        g.PartStarted();
        Tick(g);
        Assert.Empty(ran); // a part is being inspected
        g.PartFinished();
        Tick(g);
        Tick(g);
        Tick(g);
        Assert.Equal(["trim-cache", "drop-previews", "compact"], ran);
    }

    [Fact]
    public void AStepWaitsForAGapLongEnough()
    {
        var (g, p) = Make();
        var ran = 0;
        g.Add(new Reliever("slow", Pressure.Elevated, TimeSpan.FromMilliseconds(300), () => { ran++; return 0; }));
        p.Private = (long)(4 * GB * .75);
        // Parts every 100 ms with 50 ms gaps: a 300 ms step never fits.
        for (var i = 0; i < 20; i++)
        {
            g.PartStarted(); _clock.Advance(TimeSpan.FromMilliseconds(50)); g.PartFinished(); _clock.Advance(TimeSpan.FromMilliseconds(50));
        }
        g.Tick();
        Assert.Equal(0, ran);
        _clock.Advance(TimeSpan.FromSeconds(2)); // the line paused
        g.Tick();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void AStepThatThrowsIsRecordedAndTheNextOneRuns()
    {
        var (g, p) = Make();
        g.Add(new Reliever("bad", Pressure.Elevated, TimeSpan.Zero, () => throw new InvalidOperationException("boom")));
        g.Add(new Reliever("good", Pressure.Elevated, TimeSpan.Zero, () => 10 * MB));
        p.Private = (long)(4 * GB * .75);
        Tick(g);
        var s = Tick(g);
        Assert.Equal(["good", "bad"], s.Recent.Select(x => x.Name));
        Assert.Equal("boom", s.Recent[1].Error);
    }

    [Fact]
    public void ARestartIsRecommendedOnlyAfterEveryStepAndNeverDone()
    {
        var (g, p) = Make();
        g.Add(new Reliever("one", Pressure.Elevated, TimeSpan.Zero, () => 0));
        p.Private = (long)(4 * GB * .97);
        Assert.False(Tick(g).RestartRecommended); // the step runs first
        Assert.True(Tick(g).RestartRecommended);
        p.Private = GB;
        GuardStatus s = Tick(g);
        for (var i = 0; i < 30 && s.Level != Pressure.Normal; i++) s = Tick(g);
        Assert.Equal(Pressure.Normal, s.Level);
        Assert.False(s.RestartRecommended);
    }

    [Fact]
    public void AfterCalmingDownTheStepsMayRunAgain()
    {
        var (g, p) = Make(new GuardOptions { BudgetBytes = 4 * GB, StepDownSamples = 1, StepCooldown = TimeSpan.Zero });
        var ran = 0;
        g.Add(new Reliever("one", Pressure.Elevated, TimeSpan.Zero, () => { ran++; return 0; }));
        p.Private = (long)(4 * GB * .75); Tick(g);
        p.Private = GB; Tick(g);
        p.Private = (long)(4 * GB * .75); Tick(g);
        Assert.Equal(2, ran);
    }

    [Fact]
    public void ASteadyLeakRaisesTheLevelEvenBelowTheBudget()
    {
        var (g, p) = Make(new GuardOptions { BudgetBytes = 16 * GB, LeakBuckets = 10, LeakMbPerHour = 100 });
        var ran = 0;
        g.Add(new Reliever("one", Pressure.Elevated, TimeSpan.Zero, () => { ran++; return 0; }));
        // 300 MB an hour, with the usual up-and-down of each minute.
        GuardStatus s = g.Tick();
        for (var minute = 0; minute < 12; minute++)
            for (var k = 0; k < 12; k++)
            {
                p.Private = GB + minute * 5 * MB + (k % 3) * 40 * MB;
                s = Tick(g);
            }
        Assert.True(s.Leak.Suspected, $"{s.Leak.MegabytesPerHour} MB/h");
        Assert.InRange(s.Leak.MegabytesPerHour, 250, 350);
        Assert.Equal(Pressure.Elevated, s.Level);
        Assert.Equal(1, ran);
    }

    [Fact]
    public void NoLeakWhenMemoryOnlyGoesUpAndDown()
    {
        var d = new LeakDetector(TimeSpan.FromMinutes(1), 10, 100);
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rnd = new Random(1);
        for (var i = 0; i < 12 * 12; i++) d.Add(t.AddSeconds(5 * i), GB + rnd.Next(0, 200) * MB);
        Assert.False(d.Check().Suspected);
    }

    [Fact]
    public void OneStartupJumpIsNotALeak()
    {
        var d = new LeakDetector(TimeSpan.FromMinutes(1), 10, 100);
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 12; i++) d.Add(t.AddMinutes(i), i < 2 ? GB : 2 * GB); // models loaded, then flat
        Assert.False(d.Check().Suspected);
    }

    [Fact]
    public void TheThreadRunsAndStops()
    {
        var p = new Probe(_clock);
        using var g = new ResourceGuard(p, new GuardOptions { SampleEvery = TimeSpan.FromMilliseconds(50) });
        var seen = 0;
        g.Sampled += _ => Interlocked.Increment(ref seen);
        g.Start();
        Thread.Sleep(400);
        Assert.True(seen >= 1);
    }

    [Fact]
    public void TheRealProbeReadsThisProcess()
    {
        var s = new ProcessProbe(Path.GetTempPath()).Read();
        Assert.True(s.PrivateBytes > 0 && s.WorkingSetBytes > 0 && s.Threads > 0 && s.SystemTotalBytes > 0);
    }

    [Fact]
    public void LongPartsWithShortGapsNeverRunAPausingStep()
    {
        var (g, p) = Make();
        var ran = 0;
        g.Add(new Reliever("compact", Pressure.Elevated, TimeSpan.FromMilliseconds(300), () => { ran++; return 0; }));
        p.Private = (long)(4 * GB * .75);
        // 1.1 s parts, 10 ms gaps: the step must wait for a real pause.
        for (var i = 0; i < 10; i++)
        {
            g.PartStarted(); _clock.Advance(TimeSpan.FromMilliseconds(1100)); g.PartFinished();
            g.Tick();
            _clock.Advance(TimeSpan.FromMilliseconds(10));
        }
        Assert.Equal(0, ran);
        _clock.Advance(TimeSpan.FromSeconds(2));
        g.Tick();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void ACheapStepFitsAWideGapWhileRunning()
    {
        var (g, p) = Make();
        var ran = 0;
        g.Add(new Reliever("trim", Pressure.Elevated, TimeSpan.FromMilliseconds(5), () => { ran++; return 0; }));
        p.Private = (long)(4 * GB * .75);
        for (var i = 0; i < 10; i++) { g.PartStarted(); _clock.Advance(TimeSpan.FromMilliseconds(50)); g.PartFinished(); _clock.Advance(TimeSpan.FromMilliseconds(400)); }
        g.PartStarted(); _clock.Advance(TimeSpan.FromMilliseconds(50)); g.PartFinished();
        g.Tick(); // just after a part, 400 ms gap expected: room for a 5 ms step
        Assert.Equal(1, ran);
    }

    [Fact]
    public void MemoryPressureNeverRunsADiskStepAndTheOtherWayRound()
    {
        var (g, p) = Make();
        var ran = new List<string>();
        g.Add(new Reliever("trim-disk", Pressure.High, TimeSpan.Zero, () => { ran.Add("trim-disk"); return 0; }, Relieves.Disk));
        g.Add(new Reliever("free-memory", Pressure.High, TimeSpan.Zero, () => { ran.Add("free-memory"); return 0; }));
        p.Private = (long)(4 * GB * .9);
        Tick(g); Tick(g); Tick(g);
        Assert.Equal(["free-memory"], ran);
        var (g2, p2) = Make();
        var ran2 = new List<string>();
        g2.Add(new Reliever("trim-disk", Pressure.High, TimeSpan.Zero, () => { ran2.Add("trim-disk"); return 0; }, Relieves.Disk));
        g2.Add(new Reliever("free-memory", Pressure.High, TimeSpan.Zero, () => { ran2.Add("free-memory"); return 0; }));
        p2.DiskFree = 50L << 30; // 5% free
        Tick(g2); Tick(g2); Tick(g2);
        Assert.Equal(["trim-disk"], ran2);
    }

    [Fact]
    public void ThePcMemoryIsReadLive()
    {
        var m = SystemMemory.Read();
        Assert.NotNull(m);
        Assert.True(m!.Value.Load > 0 && m.Value.Total >= m.Value.Load);
    }
}
