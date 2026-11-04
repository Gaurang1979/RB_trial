using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using VisionCore.Licensing;
using VisionStudio.Remote;
using VisionStudio.Remote.V1;
using VisionSystem.Runtime;
using Xunit;

/// <summary>
/// Support and OEM in Vision Studio on the line PC (AS-04h): the password and today's code open the Service role
/// (service OTP) or the OEM role (factory OTP); opened, they sort pictures and start or stop learning, nothing more, and
/// every sign-in and action is in the audit trail. The code is taken only at the line PC with a password, and five wrong
/// codes hold that user's codes.
/// </summary>
[Collection("support-link")]   // the secrets come from the environment: one test class at a time sets them
public sealed class SupportSignInTests : IAsyncLifetime
{
    const string Licence = "LIC-TEST-0077";
    const string ServiceSecret = "service-secret-for-tests-0077";
    const string FactorySecret = "factory-secret-for-tests-0077";
    const string Password = "oem-pass-1";
    RuntimeFixture _f = null!;

    static string ServiceCode(string? day = null) => OemOtpVerifier.CalculateDailyOtp(Licence, day, ServiceSecret)!;
    static string FactoryCode(string? day = null) => OemOtpVerifier.CalculateFactoryOtp(Licence, day, FactorySecret)!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(CommissioningSecrets.ServiceOtpSecret, ServiceSecret);
        Environment.SetEnvironmentVariable(CommissioningSecrets.FactoryOtpSecret, FactorySecret);
        _f = await RuntimeFixture.StartAsync(f =>
        {
            var data = Path.Combine(f.Folder, "data");
            File.WriteAllText(Path.Combine(data, "licence.json"), JsonSerializer.Serialize(new { LicenceKey = Licence }));
            var store = new AccessStore(data);
            store.SaveUser("oem", "Factory Engineer", [Role.Oem], Password, "1357");
            store.SaveUser("svc", "Field Service", [Role.Service], Password, null);
            store.SaveUser("both", "Engineer and OEM", [Role.Engineer, Role.Oem], Password, null);
        });
    }

    public async Task DisposeAsync()
    {
        await _f.DisposeAsync();
        Environment.SetEnvironmentVariable(CommissioningSecrets.ServiceOtpSecret, null);
        Environment.SetEnvironmentVariable(CommissioningSecrets.FactoryOtpSecret, null);
    }

    Task<SignInResponse> SignIn(string user, string? code, bool network = false) =>
        _f.Sessions(network).SignInAsync(new SignInRequest { User = user, Password = Password, Code = code ?? "", Client = ClientKind.Studio }).ResponseAsync;

    List<string> Audit() => _f.App.Services.GetRequiredService<VisionReports.ResultLog>()
        .Events(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Where(e => e.Kind == "support_access").Select(e => e.Detail).ToList();

    [Fact]
    public async Task OEM_with_todays_factory_code_sorts_and_learns_only_and_every_action_is_audited()
    {
        var r = await SignIn("oem", FactoryCode());
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal([Role.Oem], r.User.Roles);
        var h = RuntimeFixture.Bearer(r.Token);
        var who = await _f.Sessions().WhoAmIAsync(new WhoAmIRequest(), h);
        Assert.True(who.MayLearn);
        Assert.False(who.MayOperate);
        Assert.False(who.MayChangeSetup);
        Assert.False(who.MayDesignJobs);
        Assert.False(who.MayTestPlc);

        var rt = _f.Runtime();
        await rt.ListToSortAsync(new ListToSortRequest { Camera = -1 }, h);
        await rt.GetReviewsAsync(new GetReviewsRequest(), h);
        var learn = await rt.LearnFromReviewsAsync(new LearnRequest(), h);   // answered (no job open), not PERMISSION_DENIED
        Assert.False(learn.Ok);
        var set = await rt.SetLearningSettingsAsync(new LearningSettings { Enabled = false, IdleMinutes = 5 }, h);
        Assert.True(set.Ok, set.ErrorText);
        var review = await rt.ReviewPartAsync(new ReviewPartRequest { ImageId = "nope-0", Decision = ReviewDecision.ConfirmedNg }, h);
        Assert.False(review.Ok);   // no such picture: answered, the right is there

        var apply = await rt.ApplyLearningAsync(new ApplyLearningRequest { ProposalId = "x" }, h);
        Assert.Equal(("runtime.refused.supervisorApplies", 403u), (apply.ErrorKey, apply.ErrorCode));
        foreach (var call in new Func<Task>[]
                 {
                     () => rt.StartAsync(new StartRequest(), h).ResponseAsync,
                     () => rt.StopAsync(new StopRequest(), h).ResponseAsync,
                     () => rt.ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, h).ResponseAsync,
                     () => rt.GetJobAsync(new GetJobRequest { Name = "Labels" }, h).ResponseAsync,
                     () => rt.PlcTestSignalAsync(new PlcTestSignalRequest { Ok = true }, h).ResponseAsync,
                 })
            Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(call)).StatusCode);

        await _f.Sessions().SignOutAsync(new SignOutRequest(), h);
        var audit = Audit();
        string[] expected =
        [
            "Vision Studio sign-in as OEM with today's code: oem", "Sorting opened, by oem (OEM)",
            "Learning started on camera 0, by oem (OEM)", "Learning refused: ",
            "Picture nope-0 sorted: rejected: refused", "Apply learning refused", "Signed out of Vision Studio: oem (OEM)",
        ];
        foreach (var e in expected) Assert.True(audit.Any(a => a.Contains(e)), $"\"{e}\" not in the audit trail: {string.Join(" | ", audit)}");
        Assert.DoesNotContain(audit, a => a.Contains(FactoryCode()));   // the code is never written
        // the setting's own audit event names the role too
        Assert.Contains(_f.App.Services.GetRequiredService<VisionReports.ResultLog>()
            .Events(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)), e => e.Kind == "learning_settings" && e.Detail.EndsWith("by oem (OEM)"));
    }

    [Fact]
    public async Task Each_role_opens_only_with_its_own_code_of_today()
    {
        Assert.True((await SignIn("svc", ServiceCode())).Ok);
        Assert.Equal("runtime.signIn.codeWrong", (await SignIn("svc", FactoryCode())).ErrorKey);
        Assert.Equal("runtime.signIn.codeWrong", (await SignIn("oem", ServiceCode())).ErrorKey);
        Assert.Equal("runtime.signIn.codeWrong", (await SignIn("oem", FactoryCode("2020-01-01"))).ErrorKey);   // another day's code
        Assert.Equal(("runtime.signIn.codeNeeded", 401u), ((await SignIn("oem", null)).ErrorKey, (await SignIn("oem", "")).ErrorCode));
        Assert.Contains(Audit(), a => a.Contains("Vision Studio sign-in as OEM refused (wrong or expired code): oem"));
    }

    [Fact]
    public async Task The_code_is_taken_only_at_the_line_PC_with_a_password()
    {
        var net = await SignIn("oem", FactoryCode(), network: true);
        Assert.Equal(("runtime.signIn.codeLocalOnly", ""), (net.ErrorKey, net.Token));
        var pin = await _f.Sessions().SignInAsync(new SignInRequest { User = "oem", Pin = "1357", Code = FactoryCode(), Client = ClientKind.Studio });
        Assert.Equal("runtime.signIn.codePassword", pin.ErrorKey);
        // the browser dashboard's own sign-in is no way in either
        Assert.Equal("runtime.signIn.codeLocalOnly", _f.App.Services.GetRequiredService<Sessions>().SignInBrowser("oem", Password).ErrorKey);
        // none of these counted as a wrong code
        Assert.True((await SignIn("oem", FactoryCode())).Ok);
    }

    [Fact]
    public async Task Five_wrong_codes_hold_that_users_codes_even_the_right_one()
    {
        for (var i = 0; i < VisionStudio.Services.OtpAttempts.MaxFailures; i++)
            Assert.Equal("runtime.signIn.codeWrong", (await SignIn("oem", "000000")).ErrorKey);
        var held = await SignIn("oem", FactoryCode());
        Assert.Equal(("runtime.signIn.codeLocked", 429u), (held.ErrorKey, held.ErrorCode));
        Assert.True((await SignIn("svc", ServiceCode())).Ok);   // another user is not held
        Assert.Contains(Audit(), a => a.Contains("refused (held after too many wrong codes): oem"));
    }

    [Fact]
    public async Task A_user_with_a_customer_role_signs_in_with_it_alone_without_the_code()
    {
        var r = await SignIn("both", null);
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal([Role.Engineer], r.User.Roles);
        var opened = await SignIn("both", FactoryCode());
        Assert.Equal([Role.Engineer, Role.Oem], opened.User.Roles);
        var who = await _f.Sessions().WhoAmIAsync(new WhoAmIRequest(), RuntimeFixture.Bearer(opened.Token));
        Assert.True(who.MayOperate && who.MayDesignJobs && who.MayLearn);
    }

    [Fact]
    public void A_remote_service_session_stays_view_only()
    {
        // a remote engineer reaches the runtime only as the dashboard's caller built from the agent's session (REMOTE-SUPPORT.md)
        foreach (var role in new[] { Sundaram.RemoteService.RemoteRole.Service, Sundaram.RemoteService.RemoteRole.Oem })
        {
            var c = WebDashboard.RemoteCaller(new Sundaram.RemoteService.RemoteSession("s1", "anita", role, "Call 4711", DateTimeOffset.UtcNow));
            Assert.True(c.Service);
            Assert.False(c.MayLearn || c.MayOperate || c.MayChangeSetup || c.Support);
        }
    }
}
