using System.Collections.Concurrent;
using PlantEvents;

namespace StationAgent;

/// <summary>The "Plant" settings of a line application. No server address: the plant link is off.</summary>
public sealed class PlantLinkOptions
{
    /// <summary>The plant server, https://host:port/ (http only with AllowHttp, for a server on the same PC).</summary>
    public string? ServerUrl { get; set; }
    /// <summary>A file holding the station's token (from /admin/devices); never the token itself in settings.
    /// The environment variable VS_PLANT_TOKEN is used when no file is given.</summary>
    public string? TokenFile { get; set; }
    public string Plant { get; set; } = "";
    public string Line { get; set; } = "";
    public string Station { get; set; } = "";
    /// <summary>The store-and-forward queue on this PC.</summary>
    public string QueueFile { get; set; } = "plant-queue.db";
    public bool AllowHttp { get; set; }
    public TimeSpan CountersEvery { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan HealthEvery { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Events held in memory while the writer is behind (it stores up to 500 per disk flush).</summary>
    public int MemoryQueue { get; set; } = 20_000;
    /// <summary>How long closing waits for the writer to store what is still in memory.</summary>
    public TimeSpan CloseWait { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Batch size, heartbeat, retries and retention of the agent.</summary>
    public AgentOptions Sending { get; set; } = new();

    public bool Enabled => !string.IsNullOrWhiteSpace(ServerUrl);
}

/// <summary>What the Run screen shows about the plant link.</summary>
public sealed record PlantLinkStatus(bool Enabled, bool Connected, long Waiting, long Lost, string? Error);

/// <summary>
/// The line application's side of the station agent. Inspection code calls
/// <see cref="Result"/>, <see cref="State"/> and the others from any thread;
/// each only puts the event in a bounded memory queue and returns, so the
/// inspection never waits for the disk or the network. A writer thread stores
/// the events in the agent's queue file, many per disk flush, and the agent
/// sends them to the plant server. When the memory queue is full (the disk is
/// stalled) an event is lost and counted, never waited for. Nothing here can
/// change OK or NG: the result is decided before it is published.
/// </summary>
public sealed class StationPublisher : IAsyncDisposable, IDisposable
{
    const int WriteBatch = 500;

    readonly Agent? _agent;
    readonly EventQueue? _queue;
    readonly HttpClient? _http;
    readonly BlockingCollection<PlantEvent> _pending;
    readonly CancellationTokenSource _stop = new();
    readonly Thread? _writer;
    readonly Task _sender = Task.CompletedTask, _ticker = Task.CompletedTask;
    readonly TimeProvider _time;
    readonly PlantLinkOptions _opt;
    readonly object _lock = new();
    readonly Dictionary<string, AlarmPriority> _alarms = [];
    LineStateValue? _state;
    string? _job;
    int? _jobVersion;
    long _lost;
    int _disposed;

    /// <summary>Counters for the snapshot every <see cref="PlantLinkOptions.CountersEvery"/>: good, bad, and when they started (ms).</summary>
    public Func<(long Good, long Bad, long Since)>? Counters { get; set; }
    /// <summary>The health sample every <see cref="PlantLinkOptions.HealthEvery"/>; null skips one.</summary>
    public Func<HealthSample?>? Health { get; set; }
    /// <summary>Where the images come from when the plant server asks for a part's images; ignored while the link is off.</summary>
    public IImageSource? Images { get => _agent?.Images; set { if (_agent is not null) _agent.Images = value; } }

    /// <summary>The changeover each heartbeat carries (CHANGEOVER-VERIFY.md); ignored while the link is off.</summary>
    public Func<ChangeoverAsk?>? Changeover { get => _agent?.Changeover; set { if (_agent is not null) _agent.Changeover = value; } }
    /// <summary>The plant server's answer about the changeover check; never raised while the link is off.</summary>
    public event Action<ChangeoverAnswer>? ChangeoverAnswered
    {
        add { if (_agent is not null) _agent.ChangeoverAnswered += value; }
        remove { if (_agent is not null) _agent.ChangeoverAnswered -= value; }
    }
    /// <summary>A plant server is set up in the settings, even when the link could not start (then nothing reaches it).</summary>
    public bool Configured => _opt.Enabled;

    /// <summary>Why the plant link is off, when it was asked for and could not start.</summary>
    public string? Error { get; }

    /// <summary>A publisher that sends nothing: the plant link is not configured.</summary>
    public static StationPublisher Off() => new(new PlantLinkOptions(), null, null, null, null, TimeProvider.System);

    /// <summary>
    /// Starts the plant link from settings. Never throws: a missing token, a bad
    /// address or a queue file of another station leaves the link off with
    /// <see cref="Error"/> set, and the line runs as before.
    /// </summary>
    public static StationPublisher Start(PlantLinkOptions o, string? baseFolder = null, TimeProvider? time = null)
    {
        if (!o.Enabled) return Off();
        HttpClient? http = null;
        try
        {
            var token = ReadToken(o, baseFolder);
            if (string.IsNullOrWhiteSpace(o.Plant) || string.IsNullOrWhiteSpace(o.Line) || string.IsNullOrWhiteSpace(o.Station))
                throw new InvalidOperationException("Plant, Line and Station must be set");
            var url = o.ServerUrl!.EndsWith('/') ? o.ServerUrl : o.ServerUrl + "/";
            http = new HttpClient { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
            var link = new HttpPlantLink(http, token, o.AllowHttp);
            return Start(o, link, Path.GetFullPath(o.QueueFile, baseFolder ?? AppContext.BaseDirectory), time, http);
        }
        catch (Exception e)
        {
            http?.Dispose();
            return new StationPublisher(o, null, null, null, "plant link off: " + e.Message, time ?? TimeProvider.System);
        }
    }

    /// <summary>Starts with a given link (tests, or another transport).</summary>
    public static StationPublisher Start(PlantLinkOptions o, IPlantLink link, string queueFile, TimeProvider? time = null, HttpClient? http = null)
    {
        var t = time ?? TimeProvider.System;
        EventQueue? queue = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(queueFile))!);
            queue = new EventQueue(queueFile);
            var agent = new Agent(new StationIdentity(o.Plant, o.Line, o.Station), queue, link, o.Sending, t);
            return new StationPublisher(o, agent, queue, http, null, t);
        }
        catch (Exception e)
        {
            queue?.Dispose();
            http?.Dispose();
            return new StationPublisher(o, null, null, null, "plant link off: " + e.Message, t);
        }
    }

    StationPublisher(PlantLinkOptions o, Agent? agent, EventQueue? queue, HttpClient? http, string? error, TimeProvider time)
    {
        _opt = o; _agent = agent; _queue = queue; _http = http; Error = error; _time = time;
        _pending = new BlockingCollection<PlantEvent>(Math.Max(1, o.MemoryQueue));
        if (agent is null) return;
        var ct = _stop.Token;
        // A thread of its own: the disk work is blocking and must never take the thread pool's threads.
        _writer = new Thread(() => WriteLoop(agent)) { IsBackground = true, Name = "plant-writer", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
        _sender = Task.Run(() => SendLoop(agent, ct), ct);
        _ticker = Task.Run(() => TickLoop(ct), ct);
    }

    static string ReadToken(PlantLinkOptions o, string? baseFolder)
    {
        var token = !string.IsNullOrWhiteSpace(o.TokenFile)
            ? File.ReadAllText(Path.GetFullPath(o.TokenFile, baseFolder ?? AppContext.BaseDirectory)).Trim()
            : Environment.GetEnvironmentVariable("VS_PLANT_TOKEN")?.Trim();
        return string.IsNullOrEmpty(token) ? throw new InvalidOperationException("no station token (TokenFile or VS_PLANT_TOKEN)") : token;
    }

    public bool Enabled => _agent is not null;

    public PlantLinkStatus Status => new(Enabled, _agent?.Connected ?? false, (_queue?.Depth() ?? 0) + _pending.Count,
        Interlocked.Read(ref _lost) + (_agent?.PublishFailures ?? 0), Error);

    long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Hands an event over; stamped now unless it has a time. Never blocks, never throws.</summary>
    public void Publish(PlantEvent e)
    {
        if (_agent is null || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            if (!_pending.TryAdd(e.Ts > 0 ? e : e with { Ts = Now() })) Interlocked.Increment(ref _lost);
        }
        catch (InvalidOperationException) { Interlocked.Increment(ref _lost); }   // closing
    }

    /// <summary>
    /// One inspected part. An NG always reaches the server with a reason: an
    /// empty or blank reason becomes ("station", "failed"), and a measurement
    /// that is not a finite number is left out rather than losing the part.
    /// </summary>
    public void Result(bool ok, IReadOnlyList<NgReason> reasons, double cycleMs, string? partId = null, string? camera = null,
        IReadOnlyDictionary<string, double>? values = null, string? imageId = null)
    {
        if (_agent is null) return;
        string? job; int? version;
        lock (_lock) { job = _job; version = _jobVersion; }
        IReadOnlyList<NgReason> why = [];
        if (!ok)
        {
            var clean = reasons.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Tool) && !string.IsNullOrWhiteSpace(r.Reason)).ToList();
            why = clean.Count > 0 ? clean : [new NgReason("station", "failed")];
        }
        Publish(new InspectionResult
        {
            Ok = ok, Reasons = why, CycleMs = double.IsFinite(cycleMs) && cycleMs >= 0 ? cycleMs : 0, PartId = partId, Camera = camera,
            Values = values?.Where(kv => double.IsFinite(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, double>(),
            ImageId = imageId, Job = job, JobVersion = version,
        });
    }

    /// <summary>The line's state; sent only when it changes, so calling it every cycle is free.</summary>
    public void State(LineStateValue state, string? reason = null)
    {
        lock (_lock)
        {
            if (_state == state) return;
            _state = state;
        }
        Publish(new LineState { State = state, Reason = reason });
    }

    /// <summary>The station now runs another job, or a new version of it; sent only when either changes.</summary>
    public void Job(string job, int? version = null, string? by = null)
    {
        string? from;
        lock (_lock)
        {
            if (_job == job && _jobVersion == version) return;
            from = _job;
            _job = job;
            _jobVersion = version;
        }
        Publish(new JobChanged { From = from, To = job, By = by, Job = job, JobVersion = version });
    }

    /// <summary>Raises or clears a station alarm. A raise is sent again when its priority changes; each other repeat is not sent.</summary>
    public void Alarm(string code, bool active, AlarmPriority priority = AlarmPriority.Medium)
    {
        lock (_lock)
        {
            if (active)
            {
                if (_alarms.TryGetValue(code, out var p) && p == priority) return;
                _alarms[code] = priority;
            }
            else if (!_alarms.Remove(code, out priority)) return;
        }
        Publish(active ? new AlarmRaised { Code = code, Priority = priority } : new AlarmCleared { Code = code, Priority = priority });
    }

    void WriteLoop(Agent agent)
    {
        var batch = new List<PlantEvent>(WriteBatch);
        try
        {
            foreach (var first in _pending.GetConsumingEnumerable())
            {
                batch.Clear();
                batch.Add(first);
                while (batch.Count < WriteBatch && _pending.TryTake(out var more)) batch.Add(more);
                agent.PublishMany(batch);   // counts its own failures; never throws
            }
        }
        catch (Exception) { }
    }

    async Task SendLoop(Agent agent, CancellationToken ct)
    {
        try { await agent.RunAsync(ct); }
        catch (OperationCanceledException) { }
    }

    async Task TickLoop(CancellationToken ct)
    {
        long lastCounters = 0, lastHealth = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = Now();
                if (Counters is { } c && now - lastCounters >= (long)_opt.CountersEvery.TotalMilliseconds)
                {
                    lastCounters = now;
                    try
                    {
                        var (good, bad, since) = c();
                        Publish(new CounterSnapshot { Good = good, Bad = bad, Total = good + bad, ShiftStart = since });
                    }
                    catch (Exception) { }
                }
                if (Health is { } h && now - lastHealth >= (long)_opt.HealthEvery.TotalMilliseconds)
                {
                    lastHealth = now;
                    try { if (h() is { } s) Publish(s); } catch (Exception) { }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, _opt.CountersEvery.TotalMilliseconds)), _time, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Stores what is still in memory (waiting up to <see cref="PlantLinkOptions.CloseWait"/>),
    /// then stops sending. What the writer could not store in that time is counted as lost.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pending.CompleteAdding();
        var stored = _writer is null || await Task.Run(() => _writer.Join(_opt.CloseWait));
        _stop.Cancel();
        try { await Task.WhenAll(_sender, _ticker).WaitAsync(_opt.CloseWait); } catch (Exception) { }
        if (stored) _queue?.Dispose();
        else Interlocked.Add(ref _lost, _pending.Count);   // the writer is stuck on the disk; it may still finish
        _http?.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
