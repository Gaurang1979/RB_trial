using System.Net;
using PlantEvents;

namespace StationAgent;

/// <summary>Where the station is in the plant; stamped on every event.</summary>
public sealed record StationIdentity(string Plant, string Line, string Station);

public sealed record AgentOptions
{
    public int BatchSize { get; init; } = 200;
    public TimeSpan HeartbeatEvery { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PruneEvery { get; init; } = TimeSpan.FromHours(1);
    /// <summary>How long unsent events are kept while the server is away.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
    /// <summary>How long accepted events stay on the line PC.</summary>
    public TimeSpan KeepSent { get; init; } = TimeSpan.FromDays(1);
    public TimeSpan RetryMin { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>The longest one call to the server may take before it counts as failed.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Parts whose images are sent in one round of the loop, after the events.</summary>
    public int ImagesPerRound { get; init; } = 3;
    /// <summary>An id the server was answered for (done or missing) is not answered again for this long, if it keeps asking.</summary>
    public TimeSpan ImageAnswerMemory { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>After an upload failed, the same id is left alone for this long (the server asks again).</summary>
    public TimeSpan ImageRetryAfter { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>The longest the image work of one round runs; when it is spent the round ends (after the part in hand), so the next one sends events first.</summary>
    public TimeSpan ImageBudget { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>The longest one image call (an upload, done, missing) to the server may take; shorter than <see cref="CallTimeout"/>, so a slow link cannot hold the events back.</summary>
    public TimeSpan ImageCallTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public string AgentVersion { get; init; } = typeof(Agent).Assembly.GetName().Version?.ToString() ?? "0";
}

/// <summary>
/// The station agent: the vision application hands it events, and it sends
/// them to the plant server in order, with nothing lost while the server is
/// away. The line never waits for it and is never stopped by it:
/// <see cref="Publish"/> only writes to the local queue and never throws.
/// Whether a part is OK or NG is decided before an event is made, so
/// nothing here can change a result. When the server asks for a part's
/// images (in its heartbeat answer), the agent sends them after the events
/// of the round, a few parts at a time; a failed upload is dropped and the
/// server asks again, so images never hold back the events.
/// </summary>
public sealed class Agent
{
    readonly StationIdentity _id;
    readonly EventQueue _queue;
    readonly IPlantLink _link;
    readonly AgentOptions _opt;
    readonly TimeProvider _time;
    long? _lastHeartbeat, _lastPrune;
    // When the server was last reached, on the monotonic clock: unsent events
    // are dropped only after the agent itself has seen the server away for
    // the whole retention time, so a wrong or jumping wall clock never
    // deletes them. A restart starts the count again (keeping them longer).
    long _lastReached;
    long _publishFailures, _protocolErrors;
    TimeSpan _retry;
    // Image ids the server asked for and the agent has not handled yet (only the pump touches these).
    Queue<string> _wanted = new();
    // Ids answered (or failed) lately and until when, on the monotonic clock: bounded, so the same id is not redone every heartbeat.
    readonly Dictionary<string, (long At, TimeSpan For)> _handled = [];
    const int MaxHandled = 500;
    // Per image id, the cameras the server has taken (the server keeps them), so a part cut off by the budget goes on
    // where it stopped at the next ask. Dropped when done or missing is posted; bounded like _handled.
    readonly Dictionary<string, HashSet<string>> _uploaded = [];

    /// <summary>
    /// Throws when the queue file belongs to another station; the caller then
    /// runs without the plant link and raises a station alarm.
    /// </summary>
    public Agent(StationIdentity id, EventQueue queue, IPlantLink link, AgentOptions? options = null, TimeProvider? time = null)
    {
        _id = id; _queue = queue; _link = link;
        _opt = options ?? new AgentOptions();
        _time = time ?? TimeProvider.System;
        _retry = TimeSpan.Zero;
        _lastReached = _time.GetTimestamp();
        queue.BindStation($"{id.Plant}/{id.Line}/{id.Station}");
    }

    /// <summary>Answers from the server that cannot be true (an acknowledgement above the batch).</summary>
    public long ProtocolErrors => Interlocked.Read(ref _protocolErrors);

    /// <summary>Events the agent could not store (a disk or database fault); shown as a station alarm by the caller.</summary>
    public long PublishFailures => Interlocked.Read(ref _publishFailures);

    /// <summary>Where the images the server asks for come from; set once at start. Without it, requests are ignored.</summary>
    public IImageSource? Images { get; set; }

    /// <summary>The changeover each heartbeat carries for the supervisor's check (null: none, no job open); set once at start.</summary>
    public Func<ChangeoverAsk?>? Changeover { get; set; }

    /// <summary>The server's answer about the changeover check, after each heartbeat that carried one. Raised on the
    /// agent's thread; a listener that throws never stops the agent.</summary>
    public event Action<ChangeoverAnswer>? ChangeoverAnswered;

    /// <summary>Whether the last attempt to reach the server worked.</summary>
    public bool Connected { get; private set; }

    /// <summary>
    /// Stamps the event with this station and the time (when the caller left
    /// it unset), checks it and stores it. Returns false, without throwing,
    /// when the event is invalid or cannot be stored.
    /// </summary>
    public bool Publish(PlantEvent e)
    {
        try
        {
            var stamped = e with
            {
                Plant = _id.Plant, Line = _id.Line, Station = _id.Station,
                Ts = e.Ts > 0 ? e.Ts : Now(),
                Seq = 1, QueueId = _queue.QueueId, // placeholders so Validate passes; the queue sets both
            };
            if (stamped is Heartbeat || stamped.Validate().Count > 0) { Interlocked.Increment(ref _publishFailures); return false; }
            _queue.Enqueue(stamped, Now());
            return true;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _publishFailures);
            return false;
        }
    }

    /// <summary>
    /// <see cref="Publish"/> for several events, stored in one transaction.
    /// Returns how many were stored; the invalid ones, and all of them when
    /// the store fails, are counted in <see cref="PublishFailures"/>.
    /// </summary>
    public int PublishMany(IReadOnlyList<PlantEvent> events)
    {
        var good = new List<PlantEvent>(events.Count);
        var now = Now();
        foreach (var e in events)
        {
            var stamped = e with
            {
                Plant = _id.Plant, Line = _id.Line, Station = _id.Station,
                Ts = e.Ts > 0 ? e.Ts : now,
                Seq = 1, QueueId = _queue.QueueId, // placeholders so Validate passes; the queue sets both
            };
            if (stamped is Heartbeat || stamped.Validate().Count > 0) Interlocked.Increment(ref _publishFailures);
            else good.Add(stamped);
        }
        try
        {
            return _queue.EnqueueMany(good, now);
        }
        catch (Exception)
        {
            Interlocked.Add(ref _publishFailures, good.Count);
            return 0;
        }
    }

    // A record the server would refuse (a bad size) is left out, so the heartbeat still goes; with no record the check
    // stays undone and the line stays stopped.
    ChangeoverAsk? AskNow()
    {
        try
        {
            if (Changeover?.Invoke() is not { } a) return null;
            var p = new List<string>();
            a.CheckFields(p);
            return p.Count == 0 ? a : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// One round of the send loop: a heartbeat when one is due, the oldest
    /// unsent batch, and pruning when due. Returns how long to wait before
    /// the next round: at once while a backlog remains, a growing pause
    /// (doubling up to <see cref="AgentOptions.RetryMax"/>) while the server
    /// cannot be reached.
    /// </summary>
    public async Task<TimeSpan> PumpOnceAsync(CancellationToken ct)
    {
        var now = Now();
        try
        {
            if (_lastPrune is not { } p || now - p >= (long)_opt.PruneEvery.TotalMilliseconds)
            {
                var away = _time.GetElapsedTime(_lastReached);
                _queue.Prune(now, _opt.KeepSent, _opt.Retention, dropUnsent: away >= _opt.Retention);
                _lastPrune = now;
            }
            if (_lastHeartbeat is not { } h || now - h >= (long)_opt.HeartbeatEvery.TotalMilliseconds)
            {
                var hb = new Heartbeat
                {
                    Plant = _id.Plant, Line = _id.Line, Station = _id.Station, Ts = now,
                    Seq = _queue.LastSeq(), AgentVersion = _opt.AgentVersion,
                    QueueDepth = _queue.Depth(), Dropped = _queue.Dropped(),
                    Changeover = AskNow(),
                };
                HeartbeatAnswer answer;
                using (var call = Call(ct)) answer = await _link.SendHeartbeatAsync(PlantEventJson.Serialize(hb), call.Token);
                _wanted = new Queue<string>(answer.ImageRequests.Where(id => !Handled(id)));
                if (hb.Changeover is not null && answer.Changeover is { } co)
                    try { ChangeoverAnswered?.Invoke(co); } catch (Exception) { }
                // Only a heartbeat that arrived counts: after an outage the
                // first round tells the server at once how far the station is.
                _lastHeartbeat = now;
            }
            var batch = _queue.Peek(_opt.BatchSize);
            var progress = false;
            if (batch.Count > 0)
            {
                long acked;
                using (var call = Call(ct)) acked = await _link.SendAsync(batch, call.Token);
                if (acked > batch[^1].Seq)
                {
                    // Not possible for a server that stored this batch: it is
                    // talking about other numbers (another queue file?).
                    // Mark nothing; the events stay queued.
                    Interlocked.Increment(ref _protocolErrors);
                    return Failed();
                }
                if (acked >= batch[0].Seq)
                {
                    _queue.MarkSent(acked);
                    // More to send: a full batch, or the server took only part of it.
                    progress = batch.Count == _opt.BatchSize || acked < batch[^1].Seq;
                }
            }
            Connected = true;
            _lastReached = _time.GetTimestamp();
            _retry = TimeSpan.Zero;
            // After the events, and never part of their success: whatever happens here, the events are done.
            if (Images is { } source) progress |= await ServeImagesAsync(source, ct);
            return progress ? TimeSpan.Zero : _opt.HeartbeatEvery;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Includes a call that ran past CallTimeout.
            return Failed();
        }
    }

    /// <summary>
    /// Hands up to <see cref="AgentOptions.ImagesPerRound"/> of the requested ids to the image source and
    /// uploads what it has, for at most <see cref="AgentOptions.ImageBudget"/> (checked before each part and before each camera
    /// after the first; a part cut off that way is left for the server's next ask, which goes on with the cameras not yet taken, with no "done"). "Not yet" is
    /// skipped (the server asks again). A part is answered "done" when at least one camera image was taken and
    /// "missing" when none was (no image on this PC, or the server refused each with 400 or 413: final, never
    /// retried). A server error, a timeout or an unreachable server (<see cref="AgentOptions.ImageCallTimeout"/>)
    /// drops that id for <see cref="AgentOptions.ImageRetryAfter"/> and ends the image work of this round; any other
    /// refusal drops the id only. Nothing here fails the round. Returns whether more requested ids are waiting,
    /// so the next round follows at once (after the events).
    /// </summary>
    async Task<bool> ServeImagesAsync(IImageSource source, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        for (var served = 0; served < _opt.ImagesPerRound && _wanted.TryPeek(out var id);)
        {
            if (_time.GetElapsedTime(started) >= _opt.ImageBudget) break;
            _wanted.Dequeue();
            if (Handled(id)) continue;
            served++;
            ImageAnswer answer;
            try
            {
                using var call = Call(ct);
                answer = await source.GetAsync(id, call.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { Remember(id, _opt.ImageRetryAfter); continue; }
            if (answer.State == ImageState.NotYet) continue;
            try
            {
                int sent = 0, tried = 0;
                if (answer.State == ImageState.Found)
                    foreach (var img in answer.Images)
                    {
                        if (_uploaded.TryGetValue(id, out var have) && have.Contains(img.Camera)) { sent++; continue; }   // taken at an earlier ask
                        // Out of time in the middle of a part (the first camera to send always goes): nothing is posted, the id is not
                        // remembered, and the server's next ask goes on with the cameras not yet taken.
                        if (tried++ > 0 && _time.GetElapsedTime(started) >= _opt.ImageBudget) return _wanted.Count > 0;
                        try
                        {
                            using var up = ImageCall(ct);
                            await _link.UploadImageAsync(id, img.Camera, img.Jpeg, up.Token);
                            sent++;
                            if (!_uploaded.TryGetValue(id, out have))
                            {
                                if (_uploaded.Count >= MaxHandled) _uploaded.Clear();
                                _uploaded[id] = have = new HashSet<string>(StringComparer.Ordinal);
                            }
                            have.Add(img.Camera);
                        }
                        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge) { }
                    }
                using var call = ImageCall(ct);
                if (sent > 0) await _link.ImageDoneAsync(id, call.Token);
                else await _link.ImageMissingAsync(id, call.Token);
                _uploaded.Remove(id);
                Remember(id, _opt.ImageAnswerMemory);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                Remember(id, _opt.ImageRetryAfter);   // dropped: the server asks again
                if (!Unreachable(e)) continue;
                _wanted.Clear();                      // the link is bad: leave the rest to a later round
                return false;
            }
        }
        return _wanted.Count > 0;
    }

    // A call that timed out, could not connect, or met a server error: trying more now would only burn time.
    static bool Unreachable(Exception e) => e is OperationCanceledException or IOException or HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError };

    CancellationTokenSource ImageCall(CancellationToken ct)
    {
        var c = CancellationTokenSource.CreateLinkedTokenSource(ct);
        c.CancelAfter(_opt.ImageCallTimeout);
        return c;
    }

    bool Handled(string id) => _handled.TryGetValue(id, out var h) && _time.GetElapsedTime(h.At) < h.For;

    void Remember(string id, TimeSpan forHow)
    {
        if (_handled.Count >= MaxHandled)
        {
            foreach (var k in _handled.Where(kv => _time.GetElapsedTime(kv.Value.At) >= kv.Value.For).Select(kv => kv.Key).ToList()) _handled.Remove(k);
            if (_handled.Count >= MaxHandled) _handled.Clear();
        }
        _handled[id] = (_time.GetTimestamp(), forHow);
    }

    TimeSpan Failed()
    {
        Connected = false;
        _retry = _retry == TimeSpan.Zero ? _opt.RetryMin : TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, _opt.RetryMax.Ticks));
        return _retry;
    }

    CancellationTokenSource Call(CancellationToken ct)
    {
        var c = CancellationTokenSource.CreateLinkedTokenSource(ct);
        c.CancelAfter(_opt.CallTimeout);
        return c;
    }

    /// <summary>Runs the send loop until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = await PumpOnceAsync(ct);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct);
        }
    }

    long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();
}
