using Xunit;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sundaram.RemoteService;

/// <summary>Signed software installs (DESIGN.md section 3, capability 7).</summary>
public sealed class PackageTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-pkg-" + Guid.NewGuid().ToString("N")[..8]);
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly ECDsa _other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string Pub => _key.ExportSubjectPublicKeyInfoPem();

    public PackageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        _other.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string Build(string version, string product = "Test Product", ECDsa? key = null, Action<string>? tamperFolder = null)
    {
        var folder = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "bin", "app.dll"), "app " + version);
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{}");
        var output = Path.Combine(_dir, $"{version}-{Guid.NewGuid():N}.rspkg");
        PackageFile.Pack(folder, product, version, key ?? _key, output);
        return output;
    }

    string Into() { var d = Path.Combine(_dir, "into-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }

    static void Edit(string package, Action<ZipArchive> change)
    {
        using var zip = ZipFile.Open(package, ZipArchiveMode.Update);
        change(zip);
    }

    [Fact]
    public void A_signed_package_opens_with_its_manifest()
    {
        var m = PackageFile.Open(Build("1.2.0"), Into(), [Pub], "Test Product", 1 << 20);
        Assert.Equal(("Test Product", "1.2.0", 2), (m.Product, m.Version, m.Files.Count));
    }

    [Fact]
    public void Tampered_extra_foreign_or_other_product_packages_are_refused()
    {
        var changed = Build("1.0");
        Edit(changed, z => { z.GetEntry("bin/app.dll")!.Delete(); using var w = new StreamWriter(z.CreateEntry("bin/app.dll").Open()); w.Write("patched"); });
        Assert.Contains("changed after signing", Assert.Throws<PackageRefused>(() => PackageFile.Open(changed, Into(), [Pub], "Test Product", 1 << 20)).Message);

        var extra = Build("1.0");
        Edit(extra, z => { using var w = new StreamWriter(z.CreateEntry("bin/evil.dll").Open()); w.Write("x"); });
        Assert.Contains("differ from its signed list", Assert.Throws<PackageRefused>(() => PackageFile.Open(extra, Into(), [Pub], "Test Product", 1 << 20)).Message);

        Assert.Contains("not signed", Assert.Throws<PackageRefused>(() => PackageFile.Open(Build("1.0", key: _other), Into(), [Pub], "Test Product", 1 << 20)).Message);
        Assert.Contains("for Other", Assert.Throws<PackageRefused>(() => PackageFile.Open(Build("1.0", "Other"), Into(), [Pub], "Test Product", 1 << 20)).Message);

        var unsigned = Build("1.0");
        Edit(unsigned, z => z.GetEntry(SignedManifest.FileName)!.Delete());
        Assert.Contains("no signed manifest", Assert.Throws<PackageRefused>(() => PackageFile.Open(unsigned, Into(), [Pub], "Test Product", 1 << 20)).Message);

        // a manifest re-signed after editing it, by someone without the key, does not verify
        var forged = Build("1.0");
        Edit(forged, z =>
        {
            var e = z.GetEntry(SignedManifest.FileName)!;
            SignedManifest sm;
            using (var r = e.Open()) sm = JsonSerializer.Deserialize<SignedManifest>(r)!;
            e.Delete();
            var m = JsonSerializer.Deserialize<PackageManifest>(Convert.FromBase64String(sm.Payload))!;
            m.Version = "9.9.9";
            sm.Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(m));
            using var w = z.CreateEntry(SignedManifest.FileName).Open();
            JsonSerializer.Serialize(w, sm);
        });
        Assert.Throws<PackageRefused>(() => PackageFile.Open(forged, Into(), [Pub], "Test Product", 1 << 20));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("/abs.dll")]
    [InlineData("bin/../../x.dll")]
    [InlineData("C:x.dll")]
    public void Unsafe_paths_are_refused_before_anything_is_written_outside(string name)
    {
        var p = Build("1.0");
        Edit(p, z => { using var w = new StreamWriter(z.CreateEntry(name).Open()); w.Write("x"); });
        var into = Into();
        Assert.Throws<PackageRefused>(() => PackageFile.Open(p, into, [Pub], "Test Product", 1 << 20));
        Assert.False(File.Exists(Path.Combine(_dir, "outside.dll")));
        Assert.False(File.Exists(Path.Combine(_dir, "x.dll")));
    }

    [Fact]
    public void Links_and_oversized_packages_are_refused()
    {
        var link = Build("1.0");
        Edit(link, z => { var e = z.CreateEntry("bin/link"); e.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); using var w = new StreamWriter(e.Open()); w.Write("/etc/passwd"); });
        Assert.Contains("link", Assert.Throws<PackageRefused>(() => PackageFile.Open(link, Into(), [Pub], "Test Product", 1 << 20)).Message);
        Assert.Contains("larger", Assert.Throws<PackageRefused>(() => PackageFile.Open(Build("1.0"), Into(), [Pub], "Test Product", 5)).Message);
    }

    [Fact]
    public async Task The_folder_installer_switches_versions_keeps_one_previous_and_goes_forward_only()
    {
        var root = Path.Combine(_dir, "program");
        var restarts = new List<string>();
        var inst = new FolderPackageInstaller(root, "Test Product", [Pub], (folder, _) => { restarts.Add(Path.GetFileName(folder)); return Task.CompletedTask; });
        foreach (var v in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            var into = Into();
            var m = PackageFile.Open(Build(v), into, [Pub], "Test Product", 1 << 20);
            Assert.Null(inst.Check(m));
            await inst.InstallAsync(into, m, default);
        }
        Assert.Equal(("1.2.0", "1.1.0"), (inst.CurrentVersion, inst.PreviousVersion));
        Assert.Equal("app 1.2.0", File.ReadAllText(Path.Combine(inst.CurrentFolder!, "bin", "app.dll")));
        Assert.Equal(["1.1.0", "1.2.0"], Directory.GetDirectories(Path.Combine(root, "versions")).Select(Path.GetFileName).Order());

        Assert.Contains("older", inst.Check(new PackageManifest { Version = "1.0.5" }));
        Assert.Contains("already", inst.Check(new PackageManifest { Version = "1.2.0" }));
        Assert.NotNull(inst.Check(new PackageManifest { Version = "../../x" }));

        await inst.RollbackAsync(default);
        Assert.Equal(("1.1.0", "1.2.0"), (inst.CurrentVersion, inst.PreviousVersion));
        Assert.Equal(["1.0.0", "1.1.0", "1.2.0", "1.1.0"], restarts);
    }

    async Task<(int Status, string? Job, string Error)> Send(Fixture f, string cookie, string package, string role = "oem")
    {
        var bytes = await File.ReadAllBytesAsync(package);
        var start = await f.PostJson("/_rs/packages", new { name = Path.GetFileName(package), size = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() }, cookie, role);
        if (start.Status != 200) return (start.Status, null, start.Text);
        var id = start.Json.GetProperty("id").GetString()!;
        var chunk = start.Json.GetProperty("chunk").GetInt32();
        for (var off = 0; off < bytes.Length; off += chunk)
            Assert.Equal(200, (await f.Ask("POST", $"/_rs/uploads/{id}", cookie, bytes.AsSpan(off, Math.Min(chunk, bytes.Length - off)).ToArray(),
                "application/octet-stream", role: role, query: "?offset=" + off)).Status);
        Assert.Equal(404, (await f.Ask("POST", $"/_rs/uploads/{id}/finish", cookie, role: role)).Status);   // never put in a folder
        var inst = await f.Ask("POST", $"/_rs/packages/{id}/install", cookie, role: role);
        return inst.Status == 202 ? (202, inst.Json.GetProperty("job").GetString(), "") : (inst.Status, null, inst.Text);
    }

    [Fact]
    public async Task Over_the_link_an_oem_installs_and_rolls_back_with_the_operators_confirmation()
    {
        var root = Path.Combine(_dir, "program");
        var installer = new FolderPackageInstaller(root, "Test Product", [Pub]);
        await using var f = await Fixture.Start((fx, _) => fx.Packages = installer);
        var service = await f.SignIn("ravi", "service");
        Assert.Equal(403, (await f.Ask("POST", "/_rs/packages", service, "{}"u8.ToArray(), "application/json", user: "ravi")).Status);

        var oem = await f.SignIn("anita", "oem");
        var (status, job, _) = await Send(f, oem, Build("1.0.0"));
        Assert.Equal(202, status);
        Assert.Equal("done", (await f.WaitJob(job!, oem)).GetProperty("state").GetString());
        (status, job, _) = await Send(f, oem, Build("1.1.0"));
        Assert.Equal("done", (await f.WaitJob(job!, oem)).GetProperty("state").GetString());
        Assert.Equal(("1.1.0", "1.0.0"), (installer.CurrentVersion, installer.PreviousVersion));
        Assert.Contains(f.Host.Asked, a => a.What.Contains("1.1.0") && a.What.Contains("now 1.0.0"));

        var p = (await f.Ask("GET", "/_rs/packages", oem)).Json;
        Assert.Equal("1.1.0", p.GetProperty("current").GetString());

        var r = await f.Ask("POST", "/_rs/packages/rollback", oem);
        Assert.Equal("done", (await f.WaitJob(r.Json.GetProperty("job").GetString()!, oem)).GetProperty("state").GetString());
        Assert.Equal("1.0.0", installer.CurrentVersion);
        Assert.Equal(["package_uploaded", "package_install", "package_installed"],
            f.Host.Kinds().Where(k => k.StartsWith("package_", StringComparison.Ordinal)).Take(3));
        Assert.Contains("package_rolled_back", f.Host.Kinds());
    }

    [Fact]
    public async Task Bad_packages_are_refused_and_recorded_and_nothing_installs_while_running_or_without_the_operator()
    {
        var installer = new FolderPackageInstaller(Path.Combine(_dir, "program"), "Test Product", [Pub]);
        await using var f = await Fixture.Start((fx, _) => fx.Packages = installer);
        var oem = await f.SignIn("anita", "oem");

        var (status, _, error) = await Send(f, oem, Build("1.0.0", key: _other));
        Assert.Equal(400, status);
        Assert.Contains("not signed", error);
        Assert.Contains("package_refused", f.Host.Kinds());

        f.Host.Running = true;
        var (_, job, _) = await Send(f, oem, Build("1.0.0"));
        Assert.Equal("refused", (await f.WaitJob(job!, oem)).GetProperty("state").GetString());
        f.Host.Running = false;
        f.Host.OperatorSays = false;
        (_, job, _) = await Send(f, oem, Build("1.0.0"));
        Assert.Equal("refused", (await f.WaitJob(job!, oem)).GetProperty("state").GetString());
        Assert.Equal("", installer.CurrentVersion);
        Assert.Equal(409, (await f.Ask("POST", "/_rs/packages/rollback", oem)).Status);   // nothing to go back to
    }
}
