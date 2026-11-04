using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.Tests;

public class BackoffTests
{
    [Fact]
    public void Doubles_from_the_first_wait_up_to_the_cap()
    {
        var b = new Backoff(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5), jitter: 0);
        Assert.Equal([250, 500, 1000, 2000, 4000, 5000, 5000],
            Enumerable.Range(0, 7).Select(i => b.Delay(i).TotalMilliseconds));
        Assert.Equal(5000, b.Delay(1000).TotalMilliseconds);   // never overflows, never gives up
    }

    [Fact]
    public void Jitter_only_adds_up_to_its_share()
    {
        var b = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), jitter: 0.2, new Random(1));
        for (var i = 0; i < 100; i++)
        {
            var ms = b.Delay(3).TotalMilliseconds;
            Assert.InRange(ms, 1000, 1200);
        }
    }
}

public class RuntimeLinkTests
{
    private sealed class ScriptedConnector : IRuntimeConnector
    {
        public Queue<Func<IRuntimeClient>> Next { get; } = new();
        public int Attempts { get; private set; }

        public Task<IRuntimeClient> ConnectAsync(CancellationToken ct)
        {
            Attempts++;
            if (Next.Count == 0) throw new IOException("unreachable");
            return Task.FromResult(Next.Dequeue()());
        }
    }

    private static readonly Backoff NoJitter = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5), jitter: 0);

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Retries_with_backoff_until_the_runtime_answers()
    {
        var connector = new ScriptedConnector();
        var waits = new List<TimeSpan>();
        var gate = new SemaphoreSlim(0);
        await using var link = new RuntimeLink(connector, NoJitter, async (d, ct) =>
        {
            waits.Add(d);
            await gate.WaitAsync(ct);
        });
        var states = new List<LinkState>();
        link.StateChanged += s => { lock (states) states.Add(s); };
        link.Start();

        await Until(() => waits.Count == 1);
        Assert.Equal(LinkState.NotConnected, link.State);
        gate.Release();
        await Until(() => waits.Count == 2);
        gate.Release();
        await Until(() => waits.Count == 3);
        Assert.Equal([250, 500, 1000], waits.Select(w => w.TotalMilliseconds));

        var client = new ScriptedClient();
        connector.Next.Enqueue(() => client);
        gate.Release();
        await Until(() => link.State == LinkState.Connected);
        Assert.Same(client, link.Client);
        Assert.Equal(0, link.FailedAttempts);
        lock (states) Assert.Equal([LinkState.NotConnected, LinkState.Connected], states);
    }

    [Fact]
    public async Task Hands_on_status_and_frames_then_reconnects_when_a_stream_breaks()
    {
        var connector = new ScriptedConnector();
        var first = new ScriptedClient();
        var second = new ScriptedClient();
        connector.Next.Enqueue(() => first);
        connector.Next.Enqueue(() => second);
        var waits = new List<TimeSpan>();
        await using var link = new RuntimeLink(connector, NoJitter, (d, _) => { waits.Add(d); return Task.CompletedTask; });
        var statuses = 0;
        var frames = 0;
        link.StatusReceived += _ => Interlocked.Increment(ref statuses);
        link.FrameReceived += _ => Interlocked.Increment(ref frames);
        link.Start();

        await Until(() => link.Client == first);
        first.StatusStream.Writer.TryWrite(Make.Running());
        first.LiveStream.Writer.TryWrite(Make.Frame(Verdict.Ok));
        await Until(() => statuses == 2 && frames == 1);

        first.LiveStream.Writer.TryComplete(new IOException("pipe broken"));
        await Until(() => link.Client == second);
        Assert.True(first.Disposed);
        Assert.Equal([250], waits.Select(w => w.TotalMilliseconds));   // first retry is quick after a good link
    }

    [Fact]
    public async Task A_runtime_that_stops_answering_while_streams_hang_is_dropped_by_the_probe()
    {
        var connector = new ScriptedConnector();
        var first = new ScriptedClient();
        var second = new ScriptedClient();
        connector.Next.Enqueue(() => first);
        connector.Next.Enqueue(() => second);
        await using var link = new RuntimeLink(connector, NoJitter, (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(100));
        link.Start();
        await Until(() => link.Client == first);
        first.FailWith = new IOException("socket gone");   // streams stay open but silent, as after a crash
        await Until(() => link.Client == second);
        Assert.IsType<IOException>(link.LastError);
    }

    [Fact]
    public async Task An_error_that_comes_back_after_the_probe_time_is_reported_as_itself()
    {
        // What a busy machine does to the test above: the failure only arrives after the probe's
        // deadline. It is still a drop, and the reason shown is the error, not "did not answer".
        var connector = new ScriptedConnector();
        var first = new ScriptedClient();
        var second = new ScriptedClient();
        connector.Next.Enqueue(() => first);
        connector.Next.Enqueue(() => second);
        await using var link = new RuntimeLink(connector, NoJitter, (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(100));
        link.Start();
        await Until(() => link.Client == first);
        first.StatusLatency = TimeSpan.FromMilliseconds(300);
        first.FailWith = new IOException("socket gone");
        await Until(() => link.Client == second);
        Assert.IsType<IOException>(link.LastError);
    }

    [Fact]
    public async Task A_status_answer_that_never_comes_is_a_drop_after_the_probe_time()
    {
        var connector = new ScriptedConnector();
        var first = new ScriptedClient();
        var second = new ScriptedClient();
        connector.Next.Enqueue(() => first);
        connector.Next.Enqueue(() => second);
        await using var link = new RuntimeLink(connector, NoJitter, (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(100));
        link.Start();
        await Until(() => link.Client == first);
        first.HangStatus = true;
        await Until(() => link.Client == second);
        Assert.IsType<TimeoutException>(link.LastError);
    }

    [Fact]
    public async Task Probe_answers_refresh_the_status()
    {
        var connector = new ScriptedConnector();
        var client = new ScriptedClient();
        connector.Next.Enqueue(() => client);
        await using var link = new RuntimeLink(connector, NoJitter, (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(50));
        RuntimeStatus? seen = null;
        link.StatusReceived += s => seen = s;
        link.Start();
        await Until(() => link.Client == client);
        client.Status = Make.Running(good: 5);
        await Until(() => seen?.Counters.Good == 5);
    }

    [Fact]
    public async Task A_connection_that_drops_at_once_still_grows_the_wait()
    {
        var connector = new ScriptedConnector();
        for (var i = 0; i < 4; i++)
        {
            connector.Next.Enqueue(() =>
            {
                var c = new ScriptedClient();
                c.LiveStream.Writer.TryComplete(new IOException("stream refused"));
                return c;
            });
        }
        var waits = new List<TimeSpan>();
        await using var link = new RuntimeLink(connector, NoJitter, (d, _) => { lock (waits) waits.Add(d); return Task.CompletedTask; });
        link.Start();
        await Until(() => { lock (waits) return waits.Count >= 4; });
        lock (waits) Assert.Equal([250, 500, 1000, 2000], waits.Take(4).Select(w => w.TotalMilliseconds));
    }

    [Fact]
    public async Task A_failed_call_drops_the_connection_and_reconnects()
    {
        var connector = new ScriptedConnector();
        var first = new ScriptedClient();
        var second = new ScriptedClient();
        connector.Next.Enqueue(() => first);
        connector.Next.Enqueue(() => second);
        await using var link = new RuntimeLink(connector, NoJitter, (_, _) => Task.CompletedTask);
        link.Start();
        await Until(() => link.Client == first);

        link.ReportBroken(second);              // not the live client: ignored
        await Task.Delay(50);
        Assert.Same(first, link.Client);

        link.ReportBroken(first);
        await Until(() => link.Client == second);
    }

    [Fact]
    public async Task A_status_call_that_fails_counts_as_not_reached()
    {
        var connector = new ScriptedConnector();
        connector.Next.Enqueue(() => new ScriptedClient { FailWith = new IOException("no answer") });
        var waits = 0;
        await using var link = new RuntimeLink(connector, NoJitter, (_, _) => { Interlocked.Increment(ref waits); return Task.Delay(5); });
        link.Start();
        await Until(() => waits >= 2);
        Assert.NotEqual(LinkState.Connected, link.State);
        Assert.Null(link.Client);
    }

    [Fact]
    public async Task Dispose_stops_retrying()
    {
        var connector = new ScriptedConnector();
        var link = new RuntimeLink(connector, NoJitter, Task.Delay);
        link.Start();
        await Until(() => connector.Attempts >= 1);
        await link.DisposeAsync();
        var attempts = connector.Attempts;
        await Task.Delay(600);
        Assert.Equal(attempts, connector.Attempts);
    }
}
