namespace VisionSystem.Studio.Runtime;

public enum LinkState { Connecting, Connected, NotConnected }

/// <summary>What view-models see of the link (so tests can drive it by hand).</summary>
public interface IRuntimeLink
{
    LinkState State { get; }
    IRuntimeClient? Client { get; }
    event Action<LinkState>? StateChanged;
    event Action<RuntimeStatus>? StatusReceived;
    event Action<LiveFrame>? FrameReceived;
    /// <summary>Each inspected part's result (StreamResults), for the parts list.</summary>
    event Action<PartResult>? ResultReceived;
    void ReportBroken(IRuntimeClient client);
}

/// <summary>
/// Keeps one connection to the runtime alive: connects, reads status and live frames, and
/// when anything fails or the runtime ends a stream, drops the connection and tries again
/// with <see cref="Backoff"/>, forever (UI-UX-SPEC.md 2.1, "Not connected"). The app never
/// stops inspection by losing the link; the runtime keeps running on its own.
/// A stream may hang silently when the runtime goes away, so while connected the link also
/// asks for the status every <see cref="ProbeEvery"/>; no answer in time counts as a drop.
/// Events are raised on a background thread.
/// </summary>
public sealed class RuntimeLink : IRuntimeLink, IAsyncDisposable
{
    private readonly IRuntimeConnector _connector;
    private readonly Backoff _backoff;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _session;
    private Task? _loop;
    private volatile IRuntimeClient? _client;
    private volatile LinkState _state = LinkState.Connecting;

    public RuntimeLink(IRuntimeConnector connector, Backoff? backoff = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? probeEvery = null)
    {
        _connector = connector;
        _backoff = backoff ?? Backoff.Default();
        _delay = delay ?? Task.Delay;
        ProbeEvery = probeEvery ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>How often a live connection is checked; each check must answer within this time too.</summary>
    public TimeSpan ProbeEvery { get; }

    /// <summary>A connection that lasts shorter than this counts as a failed attempt.</summary>
    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(5);

    public LinkState State => _state;

    /// <summary>The live connection, or null while not connected.</summary>
    public IRuntimeClient? Client => _client;

    /// <summary>Why the last connection failed or ended, for the service log; null after a good start.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Failed connect attempts since the last good connection.</summary>
    public int FailedAttempts { get; private set; }

    public event Action<LinkState>? StateChanged;
    public event Action<RuntimeStatus>? StatusReceived;
    public event Action<LiveFrame>? FrameReceived;
    public event Action<PartResult>? ResultReceived;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>A call on <see cref="Client"/> failed: drop this connection and reconnect.</summary>
    public void ReportBroken(IRuntimeClient client)
    {
        if (ReferenceEquals(client, _client))
        {
            try { _session?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IRuntimeClient? client = null;
            var failedBefore = FailedAttempts;
            var connectedAt = (DateTime?)null;
            try
            {
                client = await _connector.ConnectAsync(ct);
                var status = await client.GetStatusAsync(ct);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _session = session;
                _client = client;
                connectedAt = DateTime.UtcNow;
                FailedAttempts = 0;
                SetState(LinkState.Connected);
                StatusReceived?.Invoke(status);

                var statusPump = PumpAsync(client.WatchStatusAsync(session.Token), s => StatusReceived?.Invoke(s));
                var livePump = PumpAsync(client.WatchLiveAsync(session.Token), f => FrameReceived?.Invoke(f));
                // The parts list is extra: a runtime without StreamResults, or a results stream that
                // ends, never drops the connection (the probe finds a runtime that is gone).
                var resultPump = ResultsAsync(client, session.Token);
                var probe = ProbeAsync(client, session.Token);
                var ended = await Task.WhenAny(statusPump, livePump, probe);
                if (ended.Exception?.InnerException is { } why) LastError = why;
                session.Cancel();
                await Task.WhenAll(statusPump.ContinueWith(_ => { }), livePump.ContinueWith(_ => { }),
                    resultPump.ContinueWith(_ => { }), probe.ContinueWith(_ => { }));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                LastError = e;
                FailedAttempts++;
            }
            finally
            {
                // A connection that ends within moments (a stream refused at once) counts as a failed
                // attempt, so the wait still grows and the screen does not flicker.
                if (connectedAt is { } at && !ct.IsCancellationRequested && DateTime.UtcNow - at < StableAfter)
                {
                    FailedAttempts = failedBefore + 1;
                }
                _client = null;
                _session = null;
                if (client is not null)
                {
                    try { await client.DisposeAsync(); } catch (Exception) { }
                }
            }

            if (ct.IsCancellationRequested) break;
            SetState(LinkState.NotConnected);
            try
            {
                await _delay(_backoff.Delay(Math.Max(0, FailedAttempts - 1)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProbeAsync(IRuntimeClient client, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(ProbeEvery, ct);
            using var answer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            answer.CancelAfter(ProbeEvery);
            RuntimeStatus status;
            try
            {
                status = await client.GetStatusAsync(answer.Token);
            }
            // Only a cancelled call is a timeout: an error that comes back late (a busy machine) is
            // reported as itself, not as "did not answer".
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested && answer.IsCancellationRequested)
            {
                throw new TimeoutException($"vs-runtime did not answer within {ProbeEvery.TotalSeconds:0.#} s", e);
            }
            // Also a fresh status in case the status stream hangs while the runtime still answers.
            StatusReceived?.Invoke(status);
        }
    }

    /// <summary>Reads the results stream, and opens it again when it ends while the link is up; parts
    /// made meanwhile show as a gap in the list (their ids jump).</summary>
    private async Task ResultsAsync(IRuntimeClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PumpAsync(client.WatchResultsAsync(ct), r => ResultReceived?.Invoke(r));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
            await Task.Delay(ProbeEvery, ct);
        }
    }

    private static async Task PumpAsync<T>(IAsyncEnumerable<T> source, Action<T> sink)
    {
        await foreach (var item in source)
        {
            sink(item);
        }
    }

    private void SetState(LinkState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (Exception) { }
        }
        _stop.Dispose();
    }
}
