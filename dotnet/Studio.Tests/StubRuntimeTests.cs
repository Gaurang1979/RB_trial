using VisionStudio.Remote;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>
/// Vision Studio's runtime client against a running stub runtime (dotnet/RuntimeStub). Opt-in:
/// start the stub, then set VS_STUB=1 (and VS_RUNTIME_ENDPOINT if it is not on the default
/// endpoint). With the stub started with --drop-every-s, VS_STUB_DROPS=1 also checks reconnect.
/// Without VS_STUB the tests return at once.
/// </summary>
public class StubRuntimeTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("VS_STUB") == "1";

    private static async Task Until(Func<bool> condition, int seconds = 15, Func<string>? message = null)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(50);
        Assert.True(condition(), message?.Invoke());
    }

    [Fact]
    public async Task Connects_signs_in_and_runs_the_line_through_the_contract()
    {
        if (!Enabled) return;
        await using var link = new RuntimeLink(GrpcRuntimeConnector.ForDefaultEndpoint(() => "en"));
        RuntimeStatus? status = null;
        var frames = 0;
        link.StatusReceived += s => status = s;
        link.FrameReceived += _ => Interlocked.Increment(ref frames);
        link.Start();
        await Until(() => link.State == LinkState.Connected && status is not null, message: () => $"{link.State}: {link.LastError}");
        var calls = new RuntimeCalls(link);

        var (_, users) = await calls.TryAsync((c, ct) => c.ListUsersAsync(ct));
        Assert.Contains(users!, u => u.Id == "operator");
        var (_, wrong) = await calls.TryAsync((c, ct) => c.SignInWithPinAsync("operator", "0000", ct));
        Assert.Equal(SignInOutcome.Wrong, wrong!.Outcome);
        var (_, ok) = await calls.TryAsync((c, ct) => c.SignInWithPinAsync("operator", "1234", ct));
        Assert.Equal(SignInOutcome.Ok, ok!.Outcome);
        Assert.True(ok.User!.MayOperate);

        await calls.TryAsync((c, ct) => c.StartAsync(ct));
        await Until(() => status!.State == LineState.Running);
        await Until(() => frames > 0);

        var (_, products) = await calls.TryAsync((c, ct) => c.ListProductsAsync(ct));
        // Product pictures, full images and counters (PR #110).
        Assert.All(products!, p => Assert.NotEmpty(p.PictureId));
        var (_, picture) = await calls.TryAsync((c, ct) => c.GetImageAsync(products![0].PictureId, ct));
        Assert.True(picture!.Data.Length > 100);
        var (_, counters) = await calls.TryAsync((c, ct) => c.GetCountersAsync(ct));
        Assert.True(counters!.Total > 0 && counters.MedianCycleMs > 0);
        LiveFrame? last = null;
        link.FrameReceived += f => last = f;
        await Until(() => last?.ImageId is not null);
        var (_, full) = await calls.TryAsync((c, ct) => c.GetImageAsync(last!.ImageId!, ct));
        Assert.NotNull(full);
        var other = products!.First(p => p.Name != status!.Product!.Name);
        var (_, refused) = await calls.TryAsync((c, ct) => c.ChangeProductAsync(other, ct));
        Assert.False(refused!.Ok);
        Assert.Equal("runtime.refused.stopFirst", refused.ReasonKey);

        await calls.TryAsync((c, ct) => c.StopAsync(ct));
        await Until(() => status!.State == LineState.Stopped);
        var (_, changed) = await calls.TryAsync((c, ct) => c.ChangeProductAsync(other, ct));
        Assert.True(changed!.Ok);
        await Until(() => status!.Product == new ProductRef(other.Job, other.Variant), message: () => $"{status!.Product} vs {other}, link {link.State}");

        await calls.TryAsync(async (c, ct) => { await c.SignOutAsync(ct); return 0; });
        var (_, nobody) = await calls.TryAsync((c, ct) => c.WhoAmIAsync(ct));
        Assert.Null(nobody);
        var (_, unsigned) = await calls.TryAsync((c, ct) => c.StartAsync(ct));
        Assert.Equal("run.reason.sign_in", unsigned!.ReasonKey);
    }

    [Fact]
    public async Task Reconnects_by_itself_after_the_runtime_drops()
    {
        if (!Enabled || Environment.GetEnvironmentVariable("VS_STUB_DROPS") != "1") return;
        await using var link = new RuntimeLink(GrpcRuntimeConnector.ForDefaultEndpoint(() => "en"));
        var states = new List<LinkState>();
        link.StateChanged += s => { lock (states) states.Add(s); };
        link.Start();
        string Seen() { lock (states) return $"{string.Join(",", states)}; last error {link.LastError?.Message}"; }
        await Until(() => link.State == LinkState.Connected, message: Seen);
        await Until(() => link.State == LinkState.NotConnected, 30, Seen);
        await Until(() => link.State == LinkState.Connected, 30, Seen);
        lock (states)
        {
            // A first failed attempt while the socket comes up may come before; after the first good connection
            // the drop shows as NotConnected and the link comes back by itself.
            var afterFirst = states.SkipWhile(s => s != LinkState.Connected).ToList();
            Assert.Equal([LinkState.Connected, LinkState.NotConnected, LinkState.Connected], afterFirst.Take(3));
        }
    }
}
