using VisionStudio.Services;
using Xunit;

namespace VisionStudio.Tests;

public class RunLiveTests
{
    [Fact]
    public void Filmstrip_NoNg_IsEmpty() => Assert.Empty(RunLive.Filmstrip([]));

    [Fact]
    public void Filmstrip_NewestFirstAndActive_KeepsFour()
    {
        var ng = Enumerable.Range(0, 6).Select(i => ($"10:00:0{i}", $"R{i}"));
        var f = RunLive.Filmstrip(ng);
        Assert.Equal(RunLive.FilmstripKeep, f.Length);
        Assert.Equal("R0", f[0].Reason);
        Assert.True(f[0].IsActive);
        Assert.All(f.Skip(1), x => Assert.False(x.IsActive));
    }

    [Fact]
    public void Filmstrip_OnlyShowsWhatWasGiven()
    {
        var f = RunLive.Filmstrip([("12:00:00", "Blob")]);
        Assert.Single(f);
        Assert.Equal(("12:00:00", "Blob"), (f[0].Time, f[0].Reason));
    }

    [Theory]
    [InlineData(null, "Code", "Code")]
    [InlineData(null, null, "NG")]
    [InlineData("camera lost", "Code", "Error")]
    public void ReasonOf_ErrorThenToolThenNg(string? error, string? tool, string expected) =>
        Assert.Equal(expected, RunLive.ReasonOf(error, tool));

    [Fact]
    public void Tabs_NoFeed_OnlyCockpit() => Assert.Equal(["cockpit"], RunLive.Tabs(false, false, false));

    [Fact]
    public void Tabs_ShowOnlyTabsWithData() =>
        Assert.Equal(["cockpit", "tooling"], RunLive.Tabs(false, true, false));
}
