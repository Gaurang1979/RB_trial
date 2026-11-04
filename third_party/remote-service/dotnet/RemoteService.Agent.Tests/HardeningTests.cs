using Xunit;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Sundaram.RemoteService;

/// <summary>docs/COMPARABLE-PROJECTS.md items 1-3: no old packages, confirmed log delivery, the chain head kept outside.</summary>
public sealed class HardeningTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-hard-" + Guid.NewGuid().ToString("N")[..8]);
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string Pub => _key.ExportSubjectPublicKeyInfoPem();

    public HardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("v2.5", "2.4.9", 1)]
    [InlineData("2.5.0-rc1", "2.5.0", -1)]
    [InlineData("2.5.0-rc2", "2.5.0-rc1", 1)]
    [InlineData("10.0", "9.9", 1)]
    [InlineData("2.5.0+build7", "2.5.0", 0)]
    public void Versions_are_ordered(string a, string b, int expected) => Assert.Equal(expected, VersionOrder.Compare(a, b));

    [Fact]
    public void Versions_that_cannot_be_read_have_no_order() => Assert.Null(VersionOrder.Compare("latest", "1.0"));

    string Build(string version, string? minVersion = null, DateTime? expires = null)
    {
        var folder = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "app.dll"), "app " + version);
        var output = Path.Combine(_dir, $"{version}-{Guid.NewGuid():N}.rspkg");
        PackageFile.Pack(folder, "Test Product", version, _key, output, minVersion, expires);
        return output;
    }

    static async Task<(int Status, string State, string Text)> Install(Fixture f, string cookie, string package)
    {
        var bytes = await File.ReadAllBytesAsync(package);
        var start = await f.PostJson("/_rs/packages", new { name = "p.rspkg", size = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() }, cookie, "oem");
        if (start.Status != 200) return (start.Status, "", start.Text);
        var id = start.Json.GetProperty("id").GetString()!;
        Assert.Equal(200, (await f.Ask("POST", $"/_rs/uploads/{id}", cookie, bytes, "application/octet-stream", role: "oem", query: "?offset=0")).Status);
        var inst = await f.Ask("POST", $"/_rs/packages/{id}/install", cookie, role: "oem");
        if (inst.Status != 202) return (inst.Status, "", inst.Text);
        var j = await f.WaitJob(inst.Json.GetProperty("job").GetString()!, cookie);
        return (202, j.GetProperty("state").GetString()!, j.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task The_agent_refuses_older_packages_even_when_the_host_would_allow_them()
    {
        // a host that allows going back: the agent's own rule still holds
        var installer = new FolderPackageInstaller(Path.Combine(_dir, "program"), "Test Product", [Pub]) { AllowDowngrade = true };
        await using var f = await Fixture.Start((fx, _) => fx.Packages = installer);
        var oem = await f.SignIn("anita", "oem");
        Assert.Equal("done", (await Install(f, oem, Build("1.1.0"))).State);
        var (status, _, text) = await Install(f, oem, Build("1.0.5"));
        Assert.Equal(400, status);
        Assert.Contains("older than 1.1.0", text);
        Assert.Equal("done", (await Install(f, oem, Build("1.2.0", minVersion: "1.2.0"))).State);

        // the kept previous version (1.1.0) is below the floor 1.2.0 set by the last package: no going back to it
        var r = await f.Ask("POST", "/_rs/packages/rollback", oem, role: "oem");
        Assert.Equal(409, r.Status);
        Assert.Contains("lowest version allowed", r.Text);
        Assert.Equal(2, f.Host.Kinds().Count(k => k == "package_refused"));

        var expired = await Install(f, oem, Build("1.3.0", expires: DateTime.UtcNow.AddDays(-1)));
        Assert.Contains("expired", expired.Text);

        // remembered across a restart
        var state = AgentState.Load(Path.Combine(f.Folder, "agent-state"));
        Assert.Equal(("1.2.0", "1.2.0"), (state.HighestVersion, state.FloorVersion));
    }

    [Fact]
    public async Task Without_a_data_folder_or_with_a_damaged_state_file_no_package_goes_on()
    {
        var installer = new FolderPackageInstaller(Path.Combine(_dir, "program"), "Test Product", [Pub]);
        await using (var f = await Fixture.Start((fx, _) => fx.Packages = installer, dataFolder: false))
        {
            var oem = await f.SignIn("anita", "oem");
            Assert.Contains("no data folder", (await Install(f, oem, Build("1.0.0"))).Text);
        }
        var folder = Path.Combine(_dir, "damaged");
        Directory.CreateDirectory(Path.Combine(folder, "agent-state"));
        File.WriteAllText(Path.Combine(folder, "agent-state", AgentState.FileName), "{not json");
        await using (var f = await Fixture.Start((fx, _) => fx.Packages = installer, folder: folder))
        {
            var oem = await f.SignIn("anita", "oem");
            Assert.Contains("cannot be read", (await Install(f, oem, Build("1.0.0"))).Text);
        }
        Assert.Equal("", installer.CurrentVersion);
    }

    [Fact]
    public async Task Log_entries_are_numbered_kept_until_confirmed_and_sent_again_after_a_drop()
    {
        await using var f = await Fixture.Start();
        f.AutoAck = false;
        var cookie = await f.SignIn();
        await f.Ask("GET", "/_rs/file", cookie, query: "?root=logs&path=today.log");
        await f.Eventually(() => f.LogFrames.Count >= 2);
        var first = f.LogFrames.ToArray();
        Assert.All(first, x => Assert.Equal(f.Agent.Log.Epoch, (string?)x["epoch"]));
        Assert.Equal([1L, 2L], first.Select(x => (long)x["seq"]!).Take(2));
        Assert.Equal(2, f.Agent.Log.Unconfirmed);

        // the link drops before the relay confirmed: the same numbers come again on the next link
        await f.DropLink();
        await f.Eventually(() => f.LogFrames.Count(x => (long)x["seq"]! == 1) >= 2);

        f.Agent.Log.Confirmed(f.Agent.Log.Epoch, 1);
        Assert.Equal(f.Agent.Log.Unconfirmed, f.Host.Entries.Count(e => e.Session.Length > 0) - 1);
    }

    [Fact]
    public async Task Confirmed_entries_are_dropped_and_unconfirmed_ones_survive_a_restart()
    {
        var folder = Path.Combine(_dir, "restart");
        string epoch;
        await using (var f = await Fixture.Start(folder: folder))
        {
            f.KeepFolder = true;
            await f.SignIn();
            await f.Eventually(() => f.Agent.Log.Unconfirmed == 0);   // the fake relay confirmed everything
            f.AutoAck = false;
            await f.Ask("GET", "/_rs/file", "rs_session=none", query: "?root=logs&path=today.log");   // refused: not logged
            await f.Form("/_rs/sign-in", "code=" + FakeHost.Code + "&reason=second+session");
            await f.Eventually(() => f.Agent.Log.Unconfirmed >= 1);
            epoch = f.Agent.Log.Epoch;
        }
        var kept = new RemoteLog(new FakeHost(), TimeProvider.System, Path.Combine(folder, "agent-state"));
        Assert.NotEqual(epoch, kept.Epoch);
        var due = kept.ToSend(1);
        Assert.NotEmpty(due);
        Assert.All(due, p => Assert.Equal(epoch, p.Epoch));   // still under the old run's mark, so the relay can tell repeats
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task The_session_end_carries_the_hosts_chain_head()
    {
        await using var f = await Fixture.Start();
        var cookie = await f.SignIn();
        await f.Ask("POST", "/_rs/sign-out", cookie);
        var end = f.Host.Entries.Single(e => e.Kind == "session_end");
        Assert.StartsWith("head-", end.Data["chainHead"]);
        await f.Eventually(() => f.Logs.Any(l => (string?)l["kind"] == "session_end"));
        var sent = f.Logs.Single(l => (string?)l["kind"] == "session_end");
        Assert.Equal(end.Data["chainHead"], (string?)((JsonObject)sent["data"]!)["chainHead"]);
    }
}
