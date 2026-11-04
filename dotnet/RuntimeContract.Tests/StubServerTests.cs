using Grpc.Core;
using Grpc.Net.Client;
using VisionStudio.Remote;
using VisionStudio.Remote.Stub;
using VisionStudio.Remote.V1;
using Xunit;

/// <summary>The whole contract over a real Unix socket, as Vision Studio will use it.</summary>
public sealed class StubServerTests : IAsyncLifetime
{
    readonly RuntimeEndpoint _endpoint = new(null, Path.Combine(Path.GetTempPath(), $"vs-test-{Guid.NewGuid():N}.sock"[..32]));
    readonly StubRuntime _rt = new(seed: 5) { Interval = TimeSpan.FromHours(1), NgRate = 0 };
    Microsoft.AspNetCore.Builder.WebApplication? _app;
    GrpcChannel? _channel;

    public async Task InitializeAsync()
    {
        _app = StubServer.Build(_rt, _endpoint);
        await _app.StartAsync();
        _channel = _endpoint.CreateChannel();
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _rt.Dispose();
        File.Delete(_endpoint.SocketPath!);
    }

    Session.SessionClient SessionClient => new(_channel);
    VisionRuntime.VisionRuntimeClient Runtime => new(_channel);
    static Metadata Bearer(string token) => new() { { "authorization", $"Bearer {token}" } };

    async Task<string> SignIn(string user, string? pin = null, string? password = null, ClientKind client = ClientKind.Studio)
    {
        var req = new SignInRequest { User = user, Client = client };
        if (pin is not null) req.Pin = pin; else req.Password = password;
        var r = await SessionClient.SignInAsync(req);
        Assert.True(r.Ok, r.ErrorText);
        return r.Token;
    }

    [Fact]
    public async Task ListUsersNeedsNoTokenAndHasNoSecrets()
    {
        var r = await SessionClient.ListUsersAsync(new ListUsersRequest());
        Assert.Equal(4, r.Users.Count);
        Assert.True(r.Users.Single(u => u.Name == "operator").HasPin);
        Assert.False(r.Users.Single(u => u.Name == "engineer").HasPin);
    }

    [Fact]
    public async Task WrongPinIsRefusedWithAKey()
    {
        var r = await SessionClient.SignInAsync(new SignInRequest { User = "operator", Pin = "0000" });
        Assert.False(r.Ok);
        Assert.Equal("runtime.signIn.wrong", r.ErrorKey);
        Assert.Empty(r.Token);
    }

    [Fact]
    public async Task OperatorStartsAndSeesStatusFramesAndCounters()
    {
        var token = await SignIn("operator", pin: "1234");
        var me = await SessionClient.WhoAmIAsync(new WhoAmIRequest(), Bearer(token));
        Assert.True(me.MayOperate);
        Assert.False(me.MayChangeSetup);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var status = Runtime.WatchStatus(new WatchStatusRequest(), Bearer(token), cancellationToken: cts.Token);
        Assert.True(await status.ResponseStream.MoveNext(cts.Token));
        Assert.Equal(VisionStudio.Remote.V1.Status.Types.State.Stopped, status.ResponseStream.Current.State);   // first message at once

        Assert.True((await Runtime.StartAsync(new StartRequest(), Bearer(token))).Ok);
        Assert.True(await status.ResponseStream.MoveNext(cts.Token));
        Assert.Equal(VisionStudio.Remote.V1.Status.Types.State.Busy, status.ResponseStream.Current.State);

        using var frames = Runtime.WatchFrames(new WatchFramesRequest { MaxFps = 100 }, Bearer(token), cancellationToken: cts.Token);
        var next = frames.ResponseStream.MoveNext(cts.Token);
        await Task.Delay(200, cts.Token);   // the stream is registered before the inspection
        _rt.Step();
        Assert.True(await next);
        var f = frames.ResponseStream.Current;
        Assert.Equal(48, f.Graphics.Count);
        Assert.All(f.Graphics, g => Assert.Equal("status.ok", g.Colour));

        var c = await Runtime.GetCountersAsync(new GetCountersRequest(), Bearer(token));
        Assert.Equal(1UL, c.Total);
        Assert.Equal(100f, c.YieldPercent);

        // the Run screen's chart lines and Batch and PLC card: the stub's own pace and its last part
        var s = await Runtime.GetStatusAsync(new GetStatusRequest(), Bearer(token));
        Assert.True(s.LineSpeedPpm > 0 && s.CycleBudgetMs > 0);
        Assert.True(s.Plc is { LinkUp: true, HeartbeatOk: true, RunEnable: true, LastResultPass: true });
        Assert.True(s.Plc.LastTriggerId > 0);
        Assert.NotNull(s.Plc.LastResultTime);
    }

    [Fact]
    public async Task BrowserSessionMayLookButNotStart()
    {
        var token = await SignIn("admin", password: "admin", client: ClientKind.Browser);
        Assert.NotNull(await Runtime.GetStatusAsync(new GetStatusRequest(), Bearer(token)));
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Runtime.StartAsync(new StartRequest(), Bearer(token)));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains("Vision Studio", ex.Status.Detail);
    }

    [Fact]
    public async Task PlantTokensReadOrOperate()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await Runtime.StopAsync(new StopRequest(), Bearer(StubRuntime.PlantReadToken)));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.True((await Runtime.StopAsync(new StopRequest(), Bearer(StubRuntime.PlantOperateToken))).Ok);
    }

    [Fact]
    public async Task NoTokenReadsButMayNotChange()
    {
        Assert.Equal("Whisky 750 ml", (await Runtime.GetStatusAsync(new GetStatusRequest())).ActiveJob);
        Assert.Equal(3, (await Runtime.ListJobsAsync(new ListJobsRequest())).Jobs.Count);
        Assert.Equal(0UL, (await Runtime.GetCountersAsync(new GetCountersRequest())).Total);
        var me = await SessionClient.WhoAmIAsync(new WhoAmIRequest());
        Assert.False(me.MayOperate);
        Assert.Null(me.User);
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Runtime.StartAsync(new StartRequest()));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task UnknownTokenIsUnauthenticatedEvenForReads()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await Runtime.GetStatusAsync(new GetStatusRequest(), Bearer("expired-token")));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task ChannelWorksAgainAfterTheRuntimeRestarts()
    {
        var token = await SignIn("operator", pin: "1234");
        await _app!.StopAsync();
        await _app.DisposeAsync();
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Runtime.GetStatusAsync(new GetStatusRequest(), Bearer(token)));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);

        _app = StubServer.Build(_rt, _endpoint);
        await _app.StartAsync();
        Assert.Equal("Whisky 750 ml", (await Runtime.GetStatusAsync(new GetStatusRequest(), Bearer(token))).ActiveJob);
    }

    static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';

    static async Task<(byte[] Data, ImageChunk First)> GetImage(VisionRuntime.VisionRuntimeClient rt, string id)
    {
        using var call = rt.GetImage(new GetImageRequest { ImageId = id });
        using var data = new MemoryStream();
        ImageChunk? first = null;
        await foreach (var c in call.ResponseStream.ReadAllAsync())
        {
            first ??= c;
            c.Data.WriteTo(data);
        }
        return (data.ToArray(), first!);
    }

    [Fact]
    public async Task GetImageSendsKeptImagesAndProductPictures()
    {
        var jobs = await Runtime.ListJobsAsync(new ListJobsRequest());
        Assert.All(jobs.Jobs, j => Assert.Equal($"picture-{j.Number}", j.PictureId));
        var (picture, pc) = await GetImage(Runtime, jobs.Jobs[0].PictureId);
        Assert.True(IsPng(picture));
        Assert.Equal((320, 240, "png"), (pc.Width, pc.Height, pc.Format));

        var r = _rt.Step();
        Assert.Equal("img-1", r.ImageIds.Single());
        var (full, fc) = await GetImage(Runtime, "img-1");
        Assert.True(IsPng(full));   // no frames folder: a drawn picture of the case
        Assert.Equal((1280, 960), (fc.Width, fc.Height));

        for (var i = 0; i < StubRuntime.KeptImages; i++) _rt.Step();
        var gone = await Assert.ThrowsAsync<RpcException>(() => GetImage(Runtime, "img-1"));
        Assert.Equal(StatusCode.NotFound, gone.StatusCode);
        Assert.True(IsPng((await GetImage(Runtime, $"img-{StubRuntime.KeptImages + 1}")).Data));
    }

    [Fact]
    public async Task GraphicsCarryTooltipsAndAlarmsTheirSteps()
    {
        using var bad = new StubRuntime(seed: 2) { Interval = TimeSpan.FromHours(1), NgRate = 1 };
        using var cts = new CancellationTokenSource();
        var frames = bad.WatchFrames(cts.Token);
        bad.Step();
        Assert.True(frames.TryRead(out var f));
        Assert.All(f!.Graphics, g => Assert.StartsWith("Code ", g.Tooltip));
        Assert.Contains(f.Graphics, g => g.Colour == "status.ng" && g.Tooltip.Contains("not read"));

        _rt.RaiseAlarm(StubRuntime.SampleAlarm());
        _rt.RaiseAlarm(StubRuntime.SampleWarning());
        var s = await Runtime.GetStatusAsync(new GetStatusRequest());
        Assert.Equal(Alarm.Types.Level.Alarm, s.Alarms[0].Level);
        Assert.True(s.Alarms.All(a => a.Help.Split('\n').Length >= 3));
    }
}
