using Grpc.Core;
using VisionStudio.Remote.V1;
using Xunit;

/// <summary>CHANGEOVER-VERIFY.md on a real runtime whose plant server is set up but cannot be reached: Start on the
/// cameras waits for the supervisor's check, and a second person may accept at the line PC (owner, 2026-11-01).</summary>
public sealed class ChangeoverTests : IAsyncLifetime
{
    RuntimeFixture _f = null!;
    Metadata _op = null!;

    public async Task InitializeAsync()
    {
        _f = await RuntimeFixture.StartAsync(f =>
        {
            var store = new VisionSystem.Runtime.AccessStore(Path.Combine(f.Folder, "data"));
            store.SaveUser("sup", "Shift Supervisor", [Role.Supervisor], null, "2580");
            store.SaveUser("eng2", "Second Engineer", [Role.Engineer], null, "1357");
            File.WriteAllText(Path.Combine(f.Folder, "plant-token.txt"), "t0ken");
        }, "--Plant:ServerUrl=http://127.0.0.1:9/", "--Plant:AllowHttp=true", "--Plant:Plant=P", "--Plant:Line=L", "--Plant:Station=S1",
           "--Plant:TokenFile=plant-token.txt");
        _op = await _f.SignInOperator();
    }

    public Task DisposeAsync() => _f.DisposeAsync().AsTask();

    async Task<Changeover> Check() => (await _f.Runtime().GetStatusAsync(new GetStatusRequest(), _op)).Changeover;

    AcceptChangeoverRequest Sup(string code, string pin = "2580", string user = "sup") => new() { User = user, Pin = pin, Code = code };

    [Fact]
    public async Task Start_waits_until_a_second_person_accepts_at_the_line_PC()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        var co = await Check();
        Assert.True(co.Required);
        Assert.Equal(Changeover.Types.State.NoServer, co.State);
        var start = await _f.Runtime().StartAsync(new StartRequest { Source = Source.Camera }, _op);
        Assert.False(start.Ok);
        Assert.Equal("runtime.refused.changeover", start.ErrorKey);

        Assert.Equal("runtime.signIn.wrong", (await _f.Runtime().AcceptChangeoverAsync(Sup(co.Code, "0000"), _op)).ErrorKey);
        Assert.Equal("runtime.changeover.notApprover", (await _f.Runtime().AcceptChangeoverAsync(Sup(co.Code, "1357", "eng2"), _op)).ErrorKey);
        var supSignedIn = await _f.Sessions().SignInAsync(new SignInRequest { User = "sup", Pin = "2580", Client = ClientKind.Studio });
        Assert.Equal("runtime.changeover.samePerson",
            (await _f.Runtime().AcceptChangeoverAsync(Sup(co.Code), RuntimeFixture.Bearer(supSignedIn.Token))).ErrorKey);
        Assert.Equal("runtime.changeover.notNow", (await _f.Runtime().AcceptChangeoverAsync(Sup("00000000"), _op)).ErrorKey);
        // Over the network a session only watches the line: refused before anyone's PIN is even checked.
        var net = await _f.SignInOperator(network: true);
        var e = await Assert.ThrowsAsync<RpcException>(async () => await _f.Runtime(network: true).AcceptChangeoverAsync(Sup(co.Code), net));
        Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);

        Assert.True((await _f.Runtime().AcceptChangeoverAsync(Sup(co.Code), _op)).Ok);
        co = await Check();
        Assert.Equal((Changeover.Types.State.Accepted, true, "Shift Supervisor"), (co.State, co.AtLine, co.By));
    }

    [Fact]
    public async Task Without_a_sign_in_nobody_accepts()
    {
        await Assert.ThrowsAsync<RpcException>(async () => await _f.Runtime().AcceptChangeoverAsync(Sup("00000000")));
    }
}
