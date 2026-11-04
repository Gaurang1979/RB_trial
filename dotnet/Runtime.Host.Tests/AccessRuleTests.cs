using Grpc.Core;
using VisionStudio.Remote.V1;
using Xunit;

/// <summary>The access rules of PR #88 in the real runtime: reads without a token only on the
/// local endpoint, changes only signed in, remote control only with an operate token.</summary>
public sealed class AccessRuleTests : IAsyncLifetime
{
    RuntimeFixture _f = null!;
    public async Task InitializeAsync() => _f = await RuntimeFixture.StartAsync();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    static async Task<StatusCode> Code(Func<Task> call)
    {
        try { await call(); return StatusCode.OK; }
        catch (RpcException e) { return e.StatusCode; }
    }

    [Fact]
    public async Task Local_without_a_token_reads_but_never_changes()
    {
        var s = await _f.Runtime().GetStatusAsync(new GetStatusRequest());
        Assert.Equal("1.0.0", s.Version);
        Assert.Equal((0f, 0f), (s.LineSpeedPpm, s.CycleBudgetMs));   // no line speed set: no chart lines
        var users = await _f.Sessions().ListUsersAsync(new ListUsersRequest());
        Assert.Contains(users.Users, u => u.Name == "op" && u.HasPin && !u.HasPassword);
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime().StartAsync(new StartRequest()).ResponseAsync));
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }).ResponseAsync));
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime().SetInputAsync(new SetInputRequest { Name = "batch.LOT", Value = "1" }).ResponseAsync));
    }

    [Fact]
    public async Task Network_without_a_token_reads_nothing()
    {
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime(network: true).GetStatusAsync(new GetStatusRequest()).ResponseAsync));
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Sessions(network: true).ListUsersAsync(new ListUsersRequest()).ResponseAsync));
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime(network: true).GetCountersAsync(new GetCountersRequest()).ResponseAsync));
    }

    [Fact]
    public async Task Operator_signed_in_locally_runs_the_line()
    {
        var h = await _f.SignInOperator();
        var who = await _f.Sessions().WhoAmIAsync(new WhoAmIRequest(), h);
        Assert.Equal(ClientKind.Studio, who.Client);
        Assert.True(who.MayOperate);
        Assert.False(who.MayChangeSetup);
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, h)).Ok);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest(), h)).Ok);
        Assert.True((await _f.Runtime().StopAsync(new StopRequest(), h)).Ok);
    }

    [Fact]
    public async Task Studio_sign_in_over_the_network_is_a_browser_session_and_only_reads()
    {
        var r = await _f.Sessions(network: true).SignInAsync(new SignInRequest { User = "eng", Password = "secret-pass", Client = ClientKind.Studio });
        Assert.True(r.Ok);
        var h = RuntimeFixture.Bearer(r.Token);
        var who = await _f.Sessions(network: true).WhoAmIAsync(new WhoAmIRequest(), h);
        Assert.Equal(ClientKind.Browser, who.Client);
        Assert.False(who.MayOperate);
        Assert.NotNull(await _f.Runtime(network: true).GetStatusAsync(new GetStatusRequest(), h));
        Assert.Equal(StatusCode.PermissionDenied, await Code(() => _f.Runtime(network: true).StartAsync(new StartRequest(), h).ResponseAsync));
        // the session belongs to the network: it is not taken to the local endpoint
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime().GetStatusAsync(new GetStatusRequest(), h).ResponseAsync));
    }

    [Fact]
    public async Task Plant_tokens_read_and_only_the_operate_token_controls()
    {
        var read = RuntimeFixture.Bearer(_f.AddToken("mes-read", operate: false));
        var operate = RuntimeFixture.Bearer(_f.AddToken("mes", operate: true));
        Assert.NotNull(await _f.Runtime(network: true).GetCountersAsync(new GetCountersRequest(), read));
        Assert.Equal(StatusCode.PermissionDenied, await Code(() => _f.Runtime(network: true).StopAsync(new StopRequest(), read).ResponseAsync));
        var who = await _f.Sessions(network: true).WhoAmIAsync(new WhoAmIRequest(), operate);
        Assert.Equal(ClientKind.PlantSoftware, who.Client);
        Assert.True(who.MayOperate);
        Assert.False(who.MayChangeSetup);
        Assert.True((await _f.Runtime(network: true).ChangeJobAsync(new ChangeJobRequest { Number = 7 }, operate)).Ok);
        Assert.Equal("Labels", (await _f.Runtime(network: true).GetStatusAsync(new GetStatusRequest(), operate)).ActiveJob);
        // a token that is not on file
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime(network: true).GetStatusAsync(new GetStatusRequest(), RuntimeFixture.Bearer("vst_nope")).ResponseAsync));
    }

    [Fact]
    public async Task Wrong_pins_lock_the_user_for_a_while()
    {
        for (var i = 0; i < 5; i++)
        {
            var r = await _f.Sessions().SignInAsync(new SignInRequest { User = "op", Pin = "0000" });
            Assert.Equal("runtime.signIn.wrong", r.ErrorKey);
        }
        var locked = await _f.Sessions().SignInAsync(new SignInRequest { User = "op", Pin = RuntimeFixture.OperatorPin });
        Assert.False(locked.Ok);
        Assert.Equal("runtime.signIn.locked", locked.ErrorKey);
    }

    [Fact]
    public async Task Signed_out_token_is_unknown()
    {
        var h = await _f.SignInOperator();
        await _f.Sessions().SignOutAsync(new SignOutRequest(), h);
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime().GetStatusAsync(new GetStatusRequest(), h).ResponseAsync));
    }
}
