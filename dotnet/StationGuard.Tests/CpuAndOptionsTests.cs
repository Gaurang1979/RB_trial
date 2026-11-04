using StationGuard;
using Xunit;

public class CpuAndOptionsTests
{
    [Fact]
    public void WholePcCpuIsTheBusyShareBetweenTwoReads()
    {
        var times = new Queue<(ulong, ulong)?>([(100, 1000), (400, 2000), (400, 2000), null, (900, 3000)]);
        var cpu = new SystemCpu(() => times.Dequeue());
        Assert.Null(cpu.Read());                       // the first read has nothing to compare with
        Assert.Equal(30, cpu.Read()!.Value, 6);       // 300 busy of 1000
        Assert.Null(cpu.Read());                       // no time passed
        Assert.Null(cpu.Read());                       // the system could not tell
        Assert.Null(cpu.Read());                       // nothing to compare with again
    }

    [Fact]
    public void CountersThatGoBackwardsGiveNoReading()
    {
        var times = new Queue<(ulong, ulong)?>([(500, 1000), (100, 2000)]);
        var cpu = new SystemCpu(() => times.Dequeue());
        cpu.Read();
        Assert.Null(cpu.Read());
    }

    [Fact]
    public void TheRealProbeReadsTheWholePcCpu()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        Assert.NotNull(SystemCpu.Times());
        var probe = new ProcessProbe(Path.GetTempPath());
        Assert.Null(probe.Read().SystemCpuPct);
        // keep a core busy until the system's counters move (ticks of 10 ms; slower on a loaded PC)
        var start = SystemCpu.Times()!.Value.Total;
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until && SystemCpu.Times()!.Value.Total == start) { }
        var pct = probe.Read().SystemCpuPct;
        Assert.NotNull(pct);
        Assert.InRange(pct!.Value, 0, 100);
    }

    [Fact]
    public void TheGuardShowsTheLimitsItRunsWith()
    {
        var o = new GuardOptions { BudgetBytes = 2L << 30, HighPct = 80, SystemCriticalPct = 96, DiskHighFreePct = 12 };
        using var g = new ResourceGuard(new Probe(new Clock()), o);
        Assert.Same(o, g.Options);
        Assert.Equal(80, g.Options.HighPct);
        Assert.Equal(96, g.Options.SystemCriticalPct);
        Assert.Equal(12, g.Options.DiskHighFreePct);
        Assert.Equal(2L << 30, g.Tick().BudgetBytes);
    }

    [Fact]
    public void WithNoBudgetSetTheStatusCarriesTheBudgetInForce()
    {
        using var g = new ResourceGuard(new Probe(new Clock()));
        Assert.Equal(0, g.Options.BudgetBytes);
        Assert.Equal(8L << 30, g.Tick().BudgetBytes); // half of the probe's 16 GB
    }
}
