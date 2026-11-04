using VisionStudio.Remote;
using VisionStudio.Remote.Stub;
using VisionStudio.Remote.V1;
using Xunit;

public class AccessTests
{
    [Fact]
    public void BrowserNeverChangesAnything()
    {
        Role[] all = [Role.Operator, Role.Supervisor, Role.Engineer, Role.Admin, Role.Qa];
        Assert.False(RuntimeAccess.MayOperate(ClientKind.Browser, all, operateToken: true));
        Assert.False(RuntimeAccess.MayChangeSetup(ClientKind.Browser, all));
    }

    [Fact]
    public void PlantSoftwareOperatesOnlyWithOperateTokenAndNeverChangesSetup()
    {
        Assert.True(RuntimeAccess.MayOperate(ClientKind.PlantSoftware, [], operateToken: true));
        Assert.False(RuntimeAccess.MayOperate(ClientKind.PlantSoftware, [], operateToken: false));
        Assert.False(RuntimeAccess.MayChangeSetup(ClientKind.PlantSoftware, [Role.Admin]));
    }

    [Fact]
    public void StudioOperatorRunsTheLineSupervisorAndAboveChangeSetup()
    {
        Assert.True(RuntimeAccess.MayOperate(ClientKind.Studio, [Role.Operator]));
        Assert.False(RuntimeAccess.MayChangeSetup(ClientKind.Studio, [Role.Operator]));
        Assert.True(RuntimeAccess.MayChangeSetup(ClientKind.Studio, [Role.Supervisor]));
        Assert.True(RuntimeAccess.MayChangeSetup(ClientKind.Studio, [Role.Engineer]));
        Assert.False(RuntimeAccess.MayOperate(ClientKind.Studio, []));
        Assert.False(RuntimeAccess.MayOperate(ClientKind.Unspecified, [Role.Admin]));
    }

    [Fact]
    public void SupportAndOemInStudioSortAndLearnButNeverRunTheLineOrChangeSetup()
    {
        foreach (var r in new[] { Role.Service, Role.Oem })
        {
            Assert.True(RuntimeAccess.MayLearn(ClientKind.Studio, [r]));
            Assert.False(RuntimeAccess.MayOperate(ClientKind.Studio, [r]));
            Assert.False(RuntimeAccess.MayChangeSetup(ClientKind.Studio, [r]));
            Assert.False(RuntimeAccess.MayDesignJobs(ClientKind.Studio, [r]));
            Assert.False(RuntimeAccess.MayTestPlc(ClientKind.Studio, [r]));
            Assert.False(RuntimeAccess.MayLearn(ClientKind.Browser, [r]));
            Assert.False(RuntimeAccess.MayLearn(ClientKind.PlantSoftware, [r]));
        }
        Assert.True(RuntimeAccess.MayLearn(ClientKind.Studio, [Role.Supervisor]));
        Assert.False(RuntimeAccess.MayLearn(ClientKind.Studio, [Role.Operator]));
        Assert.True(RuntimeAccess.MayOperate(ClientKind.Studio, [Role.Operator, Role.Oem]));
    }

    [Fact]
    public void OnlyAnEngineerOrAdminInStudioSendsPlcTestSignals()
    {
        Assert.True(RuntimeAccess.MayTestPlc(ClientKind.Studio, [Role.Engineer]));
        Assert.True(RuntimeAccess.MayTestPlc(ClientKind.Studio, [Role.Admin]));
        Assert.False(RuntimeAccess.MayTestPlc(ClientKind.Studio, [Role.Operator, Role.Supervisor, Role.Qa]));
        Assert.False(RuntimeAccess.MayTestPlc(ClientKind.Studio, []));
        Assert.False(RuntimeAccess.MayTestPlc(ClientKind.Browser, [Role.Engineer, Role.Admin]));
        Assert.False(RuntimeAccess.MayTestPlc(ClientKind.PlantSoftware, [Role.Engineer, Role.Admin]));
    }

    [Fact]
    public void EndpointParsesPipeAndSocket()
    {
        Assert.Equal("vs-runtime", RuntimeEndpoint.Parse("pipe:vs-runtime").PipeName);
        Assert.Equal("/tmp/x.sock", RuntimeEndpoint.Parse("/tmp/x.sock").SocketPath);
        Assert.Equal("pipe:abc", RuntimeEndpoint.Parse("pipe:abc").ToString());
    }

    [Fact]
    public void JpegSizeReadsStartOfFrame()
    {
        // SOI, APP0 (length 4, 2 bytes payload), SOF0 with height 960 and width 1280
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x03, 0xC0, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0];
        Assert.Equal((1280, 960), StubRuntime.JpegSize(jpeg));
        Assert.Null(StubRuntime.JpegSize([0x89, 0x50, 0x4E, 0x47]));
    }

    [Fact]
    public void StepMakesOneCodePerSlotWithGraphics()
    {
        using var rt = new StubRuntime(seed: 3) { NgRate = 0 };
        var r = rt.Step("P1");
        Assert.True(r.Pass);
        Assert.Equal(48, r.Codes.Count);
        Assert.All(r.Codes, c => Assert.Equal(CodeRead.Types.State.Read, c.State));
        Assert.Equal(48, r.Codes.Select(c => c.Text).Distinct().Count());
        Assert.Equal(1UL, rt.Counters().Total);
    }

    [Fact]
    public void ChangeJobWhileRunningIsRefusedWithAKey()
    {
        using var rt = new StubRuntime { Interval = TimeSpan.FromHours(1) };
        Assert.True(rt.Start(new StartRequest()).Ok);
        var r = rt.ChangeJob(new ChangeJobRequest { Number = 2 });
        Assert.False(r.Ok);
        Assert.Equal("runtime.refused.stopFirst", r.ErrorKey);
        rt.Stop();
        Assert.True(rt.ChangeJob(new ChangeJobRequest { Number = 2, Variant = "PH-1010-12" }).Ok);
        Assert.Equal("PH-1010-12", rt.Status().ActiveVariant);
    }
}
