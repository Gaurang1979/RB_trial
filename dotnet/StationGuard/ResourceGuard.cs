using System.Diagnostics;
using System.Runtime;

namespace StationGuard;

/// <summary>How hard the PC is pressed. Each level allows more clean-up steps.</summary>
public enum Pressure { Normal, Elevated, High, Critical }

/// <summary>What a clean-up step frees: a step runs only when that is what is short.</summary>
public enum Relieves { Memory, Disk }

public sealed record GuardOptions
{
    /// <summary>Memory the station process may use; 0 = half the PC's memory.</summary>
    public long BudgetBytes { get; init; }
    public double ElevatedPct { get; init; } = 70;
    public double HighPct { get; init; } = 85;
    public double CriticalPct { get; init; } = 95;
    /// <summary>The whole PC's memory in use (other programs too).</summary>
    public double SystemHighPct { get; init; } = 90;
    public double SystemCriticalPct { get; init; } = 97;
    /// <summary>Free space on the data drive.</summary>
    public double DiskHighFreePct { get; init; } = 10;
    public double DiskCriticalFreePct { get; init; } = 3;
    public TimeSpan SampleEvery { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>The leak check: floors per minute over half an hour, more than 100 MB an hour.</summary>
    public TimeSpan LeakBucket { get; init; } = TimeSpan.FromMinutes(1);
    public int LeakBuckets { get; init; } = 30;
    public double LeakMbPerHour { get; init; } = 100;
    /// <summary>Samples in a row below a level (by <see cref="HysteresisPct"/>) before stepping down.</summary>
    public int StepDownSamples { get; init; } = 6;
    public double HysteresisPct { get; init; } = 5;
    /// <summary>Pause between two clean-up steps, so each one's effect is seen before the next.</summary>
    public TimeSpan StepCooldown { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>
    /// A step runs only when no part is being inspected and either the line has been still for
    /// this long since the last part ended, or the usual gap between parts leaves at least twice the
    /// step's cost before the next part is due. A step that costs <see cref="PauseCost"/> or more
    /// (it may pause every thread) runs only when the line has been still for this long.
    /// </summary>
    public TimeSpan QuietFor { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PauseCost { get; init; } = TimeSpan.FromMilliseconds(100);
    public int KeepSteps { get; init; } = 50;
}

/// <summary>One clean-up step an application offers (empty a cache, drop old previews …).</summary>
public interface IReliever
{
    string Name { get; }
    /// <summary>The lowest pressure at which this step may run.</summary>
    Pressure From { get; }
    /// <summary>How long the step usually takes; it runs only in a gap at least this long.</summary>
    TimeSpan Cost { get; }
    /// <summary>Does the work; returns the bytes it expects to have freed (0 if unknown).</summary>
    long Relieve();
    /// <summary>What the step frees; it runs only when that resource is the one pressed.</summary>
    Relieves Relieves => Relieves.Memory;
}

public sealed class Reliever(string name, Pressure from, TimeSpan cost, Func<long> relieve, Relieves relieves = Relieves.Memory) : IReliever
{
    public string Name => name;
    public Pressure From => from;
    public TimeSpan Cost => cost;
    public Relieves Relieves => relieves;
    public long Relieve() => relieve();
}

/// <summary>
/// The last resort before a restart: a full, compacting collection of the
/// .NET heap (the large-object heap included). It pauses every thread, so it
/// runs only at High pressure, only in a quiet gap, and at most every 5 minutes.
/// </summary>
public sealed class CompactHeapReliever(TimeProvider? time = null) : IReliever
{
    readonly TimeProvider _time = time ?? TimeProvider.System;
    long? _last;
    public string Name => "compact-heap";
    public Pressure From => Pressure.High;
    public TimeSpan Cost => TimeSpan.FromMilliseconds(300);

    public long Relieve()
    {
        if (_last is { } l && _time.GetElapsedTime(l) < TimeSpan.FromMinutes(5)) return 0;
        _last = _time.GetTimestamp();
        var before = GC.GetTotalMemory(false);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return Math.Max(0, before - GC.GetTotalMemory(false));
    }
}

public sealed record ReliefStep(DateTimeOffset At, string Name, Pressure Level, long FreedBytes, TimeSpan Took, string? Error);

public sealed record GuardStatus(
    Pressure Level, string Reason, ResourceSample? Last, double MemoryUsedPct, long BudgetBytes,
    LeakVerdict Leak, bool RestartRecommended, IReadOnlyList<ReliefStep> Recent);

/// <summary>
/// Keeps the line PC fast and stable while the station runs. Every few
/// seconds it reads memory, CPU and disk, decides the pressure level and,
/// when pressed, takes one clean-up step at a time — the cheapest first —
/// and only between parts, on a low-priority thread, so inspection always
/// comes first. It never stops the station: when every step has been tried
/// and the pressure stays critical (or a leak keeps growing), it recommends
/// a restart, which the application does between batches.
/// </summary>
public sealed class ResourceGuard : IDisposable
{
    readonly IResourceProbe _probe;
    readonly GuardOptions _opt;
    readonly TimeProvider _time;
    readonly LeakDetector _leak;
    readonly List<IReliever> _relievers = [];
    readonly HashSet<IReliever> _triedThisEpisode = [];
    readonly LinkedList<ReliefStep> _steps = new();
    readonly object _lock = new();
    Pressure _memoryLevel, _diskLevel;
    int _busy;
    long _lastPartStart, _lastPartEnd, _lastStep;
    double _gapEwmaMs = -1;
    int _below;
    Thread? _thread;
    volatile bool _stop;
    GuardStatus _status;

    public ResourceGuard(IResourceProbe probe, GuardOptions? options = null, TimeProvider? time = null)
    {
        _probe = probe;
        _opt = options ?? new GuardOptions();
        _time = time ?? TimeProvider.System;
        _leak = new LeakDetector(_opt.LeakBucket, _opt.LeakBuckets, _opt.LeakMbPerHour);
        _status = new GuardStatus(Pressure.Normal, "starting", null, 0, _opt.BudgetBytes, new LeakVerdict(false, 0, 0), false, []);
    }

    /// <summary>
    /// The limits this guard runs with, for health gauges and reports to show the running
    /// station's thresholds instead of the defaults. <see cref="GuardStatus.BudgetBytes"/> is the
    /// memory budget in force (<see cref="GuardOptions.BudgetBytes"/> 0 = half the PC's memory).
    /// </summary>
    public GuardOptions Options => _opt;

    /// <summary>The level now; read on every part, so it is a plain field read.</summary>
    public Pressure Level => _status.Level;
    public GuardStatus Status { get { lock (_lock) return _status; } }

    /// <summary>Raised on the guard's thread after every sample.</summary>
    public event Action<GuardStatus>? Sampled;
    /// <summary>Raised when the level changes.</summary>
    public event Action<Pressure, Pressure, string>? LevelChanged;
    /// <summary>Raised after each clean-up step.</summary>
    public event Action<ReliefStep>? Stepped;

    public ResourceGuard Add(IReliever r)
    {
        lock (_lock)
        {
            _relievers.Add(r);
            // Cheapest pressure first, then the order they were added.
            var ordered = _relievers.Select((x, i) => (x, i)).OrderBy(t => t.x.From).ThenBy(t => t.i).Select(t => t.x).ToList();
            _relievers.Clear();
            _relievers.AddRange(ordered);
        }
        return this;
    }

    /// <summary>Whether a part is being inspected right now (between <see cref="PartStarted"/> and <see cref="PartFinished"/>); a plain read, for background work that must not compete with inspection.</summary>
    public bool PartInProgress => Volatile.Read(ref _busy) > 0;

    /// <summary>The station starts inspecting a part (call on every part; cheap).</summary>
    public void PartStarted()
    {
        var now = _time.GetTimestamp();
        var end = Interlocked.Read(ref _lastPartEnd);
        if (end != 0)
        {
            var gap = _time.GetElapsedTime(end, now).TotalMilliseconds;
            // Smoothed gap between the end of one part and the start of the next.
            var old = Volatile.Read(ref _gapEwmaMs);
            Volatile.Write(ref _gapEwmaMs, old < 0 ? gap : old * .9 + gap * .1);
        }
        Interlocked.Exchange(ref _lastPartStart, now);
        Interlocked.Increment(ref _busy);
    }

    /// <summary>The part is done (call in a finally, so a failed part still counts as done).</summary>
    public void PartFinished()
    {
        if (Interlocked.Decrement(ref _busy) < 0) Interlocked.Exchange(ref _busy, 0);
        Interlocked.Exchange(ref _lastPartEnd, _time.GetTimestamp());
    }

    /// <summary>Starts the guard's own thread (below normal priority).</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "station-guard" };
        _thread.Start();
    }

    void Loop()
    {
        while (!_stop)
        {
            try { Tick(); }
            catch (Exception) { /* the guard must never take the station down */ }
            var until = _time.GetTimestamp();
            while (!_stop && _time.GetElapsedTime(until) < _opt.SampleEvery) Thread.Sleep(100);
        }
    }

    /// <summary>One round: sample, decide the level, maybe take one clean-up step.</summary>
    public GuardStatus Tick()
    {
        var s = _probe.Read();
        var budget = _opt.BudgetBytes > 0 ? _opt.BudgetBytes : Math.Max(512L << 20, s.SystemTotalBytes / 2);
        _leak.Add(s.At, s.PrivateBytes);
        var leak = _leak.Check();
        var memPct = 100.0 * s.PrivateBytes / budget;
        var sysPct = s.SystemTotalBytes > 0 ? 100.0 * s.SystemLoadBytes / s.SystemTotalBytes : 0;
        var diskFreePct = s.DiskTotalBytes > 0 ? 100.0 * s.DiskFreeBytes / s.DiskTotalBytes : 100;
        var (raw, why, memLevel, diskLevel) = Judge(memPct, sysPct, diskFreePct, leak, hysteresis: 0);
        var (rawLow, _, _, _) = Judge(memPct, sysPct, diskFreePct, leak, hysteresis: _opt.HysteresisPct);

        Pressure old, level;
        lock (_lock)
        {
            old = _status.Level;
            level = old;
            if (raw > old) { level = raw; _below = 0; }
            else if (rawLow < old) { if (++_below >= _opt.StepDownSamples) { level = (Pressure)((int)old - 1); _below = 0; } }
            else _below = 0;
            if (level == Pressure.Normal) _triedThisEpisode.Clear();
            _memoryLevel = memLevel;
            _diskLevel = diskLevel;
            var restart = _status.RestartRecommended && level != Pressure.Normal;
            _status = new GuardStatus(level, why, s, Math.Round(memPct, 1), budget, leak, restart, _steps.ToList());
        }
        if (level != old) LevelChanged?.Invoke(old, level, why);
        if (level > Pressure.Normal) TryStep(level);
        GuardStatus st;
        lock (_lock) st = _status = _status with { Recent = _steps.ToList() };
        Sampled?.Invoke(st);
        return st;
    }

    (Pressure Level, string Why, Pressure Memory, Pressure Disk) Judge(double memPct, double sysPct, double diskFreePct, LeakVerdict leak, double hysteresis)
    {
        var h = hysteresis;
        var level = Pressure.Normal;
        Pressure mem = Pressure.Normal, disk = Pressure.Normal;
        var why = "normal";
        void Up(Pressure p, string w)
        {
            if (w.StartsWith("disk")) { if (p > disk) disk = p; }
            else if (p > mem) mem = p;
            if (p > level) { level = p; why = w; }
        }
        if (memPct >= _opt.CriticalPct - h) Up(Pressure.Critical, $"memory {memPct:0}% of budget");
        else if (memPct >= _opt.HighPct - h) Up(Pressure.High, $"memory {memPct:0}% of budget");
        else if (memPct >= _opt.ElevatedPct - h) Up(Pressure.Elevated, $"memory {memPct:0}% of budget");
        if (sysPct >= _opt.SystemCriticalPct - h) Up(Pressure.Critical, $"PC memory {sysPct:0}% in use");
        else if (sysPct >= _opt.SystemHighPct - h) Up(Pressure.High, $"PC memory {sysPct:0}% in use");
        if (diskFreePct <= _opt.DiskCriticalFreePct + h) Up(Pressure.Critical, $"disk {diskFreePct:0.#}% free");
        else if (diskFreePct <= _opt.DiskHighFreePct + h) Up(Pressure.High, $"disk {diskFreePct:0.#}% free");
        if (leak.Suspected) Up(Pressure.Elevated, $"memory growing {leak.MegabytesPerHour:0} MB/h");
        return (level, why, mem, disk);
    }

    void TryStep(Pressure level)
    {
        IReliever? next;
        lock (_lock)
        {
            if (_lastStep != 0 && _time.GetElapsedTime(_lastStep) < _opt.StepCooldown) return;
            // Only steps for what is actually short: a full disk never costs saved images
            // memory relief, and memory pressure never deletes saved images.
            next = _relievers.FirstOrDefault(r => !_triedThisEpisode.Contains(r) &&
                r.From <= (r.Relieves == Relieves.Disk ? _diskLevel : _memoryLevel));
            if (next is null)
            {
                // Every step tried and still pressed hard: say so, never act alone.
                if (level == Pressure.Critical || (level >= Pressure.High && _status.Leak.Suspected))
                    _status = _status with { RestartRecommended = true };
                return;
            }
            if (!Quiet(next.Cost)) return;
            _triedThisEpisode.Add(next);
            _lastStep = _time.GetTimestamp();
        }
        var sw = Stopwatch.StartNew();
        long freed = 0;
        string? error = null;
        try { freed = next.Relieve(); }
        catch (Exception e) { error = e.Message; }
        var step = new ReliefStep(_time.GetUtcNow(), next.Name, level, freed, sw.Elapsed, error);
        lock (_lock)
        {
            _steps.AddFirst(step);
            while (_steps.Count > _opt.KeepSteps) _steps.RemoveLast();
            // A step that freed a lot moves the floor; judge the leak afresh.
            if (freed > 64L << 20) _leak.Reset();
        }
        Stepped?.Invoke(step);
    }

    bool Quiet(TimeSpan cost)
    {
        if (Volatile.Read(ref _busy) > 0) return false;
        var end = Interlocked.Read(ref _lastPartEnd);
        // Before the first part, or with the line still for QuietFor: nothing to disturb.
        if (end == 0 || _time.GetElapsedTime(end) >= _opt.QuietFor) return true;
        // While running, only cheap steps, and only when the usual gap leaves room for twice the
        // step before the next part is due. A step that may pause threads waits for a real pause.
        if (cost >= _opt.PauseCost) return false;
        var gap = Volatile.Read(ref _gapEwmaMs);
        var left = gap - _time.GetElapsedTime(end).TotalMilliseconds;
        return gap > 0 && left >= 2 * cost.TotalMilliseconds;
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
