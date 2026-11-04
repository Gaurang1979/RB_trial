using System.Security.Cryptography;
using System.Text.Json;

namespace Sundaram.RemoteService;

/// <summary>Signed software installs (DESIGN.md section 3, capability 7; PROTOCOL.md section 4). OEM only.</summary>
public sealed partial class RemoteAgent
{
    sealed record PackageStartBody(string? Name, long Size, string? Sha256);

    async Task PackagesApi(RemoteSession s, RemoteRequest req, string[] segs, bool post, RemoteResponse res)
    {
        if (_host.Packages is not { } installer) { await res.Error(404, "This machine takes no software packages.", "rs.notFound"); return; }
        if (!post && segs.Length == 1)
        {
            await res.Json(200, new
            {
                product = installer.Product, current = installer.CurrentVersion, previous = installer.PreviousVersion,
                maxBytes = installer.MaxBytes, mayInstall = s.MayWrite,
            });
            return;
        }
        if (!s.MayWrite) { await res.Error(403, "Your role may not install software.", "rs.role"); return; }
        if (post && segs.Length == 1) { await PackageStart(s, req, installer, res); return; }
        if (post && segs.Length == 2 && segs[1] == "rollback") { await Rollback(s, installer, res); return; }
        if (post && segs.Length == 3 && segs[2] == "install") { await Install(s, segs[1], installer, res); return; }
        await res.Error(404, "Not found.", "rs.notFound");
    }

    async Task PackageStart(RemoteSession s, RemoteRequest req, IPackageInstaller installer, RemoteResponse res)
    {
        PackageStartBody? b;
        try { b = JsonSerializer.Deserialize<PackageStartBody>(req.Body, JsonIn); }
        catch (JsonException) { b = null; }
        if (b is null || b.Size <= 0 || b.Size > installer.MaxBytes) { await res.Error(413, "The package is larger than this machine takes.", "rs.tooLarge"); return; }
        if (b.Sha256 is not { Length: 64 } || !b.Sha256.All(Uri.IsHexDigit)) { await res.Error(400, "The package's SHA-256 is missing.", "rs.hash"); return; }
        if (_uploads.Values.Any(u => u.IsPackage)) { await res.Error(429, "Another package is being uploaded.", "rs.busy"); return; }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var temp = Path.Combine(Path.GetTempPath(), "rs-uploads", id);
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        await File.WriteAllBytesAsync(temp, []);
        var name = new string((b.Name ?? "package.rspkg").Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').Take(80).ToArray());
        _uploads[id] = new Upload(id, s, new FileRoot("package", Path.GetTempPath(), MaxBytes: installer.MaxBytes), name, temp, b.Size,
            b.Sha256.ToLowerInvariant(), temp) { IsPackage = true };
        await res.Json(200, new { id, chunk = MaxUploadChunk });
    }

    /// <summary>
    /// Checks the uploaded package completely before anything is asked or changed (size, SHA-256, safe unpacking, signature,
    /// product, every file, the host's own rule on versions), then installs it as a job: only while the machine is safe to
    /// change and after the operator confirms on its screen. Every refusal is recorded with its reason.
    /// </summary>
    async Task Install(RemoteSession s, string id, IPackageInstaller installer, RemoteResponse res)
    {
        if (!_uploads.TryRemove(id, out var u) || u.Session.Id != s.Id || !u.IsPackage) { await res.Error(404, "No such upload.", "rs.notFound"); return; }
        var staging = Path.Combine(Path.GetTempPath(), "rs-packages", id);
        try
        {
            string hash;
            await using (var f = File.OpenRead(u.Temp)) hash = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
            if (u.Written != u.Size || hash != u.Sha256) throw new PackageRefused("the package arrived damaged (size or SHA-256 differ); upload it again");
            Directory.CreateDirectory(staging);
            var m = PackageFile.Open(u.Temp, staging, installer.PublicKeysPem, installer.Product, installer.MaxBytes * 4);
            Older(m);
            if (installer.Check(m) is { } why) throw new PackageRefused(why);
            TryDelete(u.Temp);

            if (!await Record(s, res, "package_uploaded", $"Package {m.Product} {m.Version} uploaded and checked",
                    new() { ["product"] = m.Product, ["version"] = m.Version, ["sha256"] = hash, ["files"] = m.Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
            {
                DeleteFolder(staging);
                return;
            }
            var from = installer.CurrentVersion;
            var job = NewJob(s);
            _ = Task.Run(() => RunJob(job, s, needsStopped: true, needsConfirmation: true,
                new ConfirmRequest("Remote service: install software", $"{m.Product} {m.Version} (now {(from.Length > 0 ? from : "none")})", s.User),
                async ct =>
                {
                    Log.Write(s, s.User, s.Role, "package_install", $"Installing {m.Product} {m.Version} (was {from})",
                        new Dictionary<string, string> { ["version"] = m.Version, ["from"] = from, ["sha256"] = hash });
                    var result = await installer.InstallAsync(staging, m, ct);
                    Log.Write(s, s.User, s.Role, "package_installed", result, new Dictionary<string, string> { ["version"] = m.Version, ["from"] = from });
                    // remembered at once: from now on nothing older than this, nor below its floor, goes on
                    _state!.Installed(m.Version, m.MinVersion);
                    return result;
                },
                onEnd: () => DeleteFolder(staging)));
            await res.Json(202, new { job = job.Id, version = m.Version });
        }
        catch (Exception e) when (e is PackageRefused or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDelete(u.Temp);
            DeleteFolder(staging);
            var why = e is PackageRefused ? e.Message : "the package cannot be opened (" + e.Message + ")";
            if (await Record(s, res, "package_refused", "Package refused: " + why, new() { ["why"] = why }))
                await res.Error(400, "Not installed: " + why, "rs.packageRefused");
        }
    }

    async Task Rollback(RemoteSession s, IPackageInstaller installer, RemoteResponse res)
    {
        if (installer.PreviousVersion is not { } back) { await res.Error(409, "There is no previous version to go back to.", "rs.noPrevious"); return; }
        if (_state?.FloorVersion is { } floor && VersionOrder.Compare(back, floor) is not >= 0)
        {
            var why = $"{back} is below {floor}, the lowest version allowed on this machine";
            if (await Record(s, res, "package_refused", "Rollback refused: " + why, new() { ["version"] = back, ["why"] = why }))
                await res.Error(409, "Not rolled back: " + why, "rs.belowFloor");
            return;
        }
        var now = installer.CurrentVersion;
        var job = NewJob(s);
        _ = Task.Run(() => RunJob(job, s, needsStopped: true, needsConfirmation: true,
            new ConfirmRequest("Remote service: go back to the previous software", $"{installer.Product} {back} (now {now})", s.User),
            async ct =>
            {
                Log.Write(s, s.User, s.Role, "package_rollback", $"Rolling back {installer.Product} from {now} to {back}",
                    new Dictionary<string, string> { ["version"] = back, ["from"] = now });
                var result = await installer.RollbackAsync(ct);
                Log.Write(s, s.User, s.Role, "package_rolled_back", result, new Dictionary<string, string> { ["version"] = back, ["from"] = now });
                return result;
            }));
        await res.Json(202, new { job = job.Id, version = back });
    }

    /// <summary>
    /// The agent's own rule against old packages (docs/COMPARABLE-PROJECTS.md item 1), whatever the host allows: no package after
    /// its expiry, none below the floor any installed package set, none older than the newest ever installed here (going back
    /// is the rollback, to the kept previous version only). Versions that cannot be ordered are refused.
    /// </summary>
    void Older(PackageManifest m)
    {
        if (_state is null) throw new PackageRefused(_stateError!);
        if (m.ExpiresUtc is { } expires && expires < _time.GetUtcNow().UtcDateTime)
            throw new PackageRefused($"the package expired on {expires:yyyy-MM-dd}");
        if (m.MinVersion is { Length: > 0 } min && VersionOrder.Compare(m.Version, min) is not >= 0)
            throw new PackageRefused($"the package's version {m.Version} is below its own floor {min}");
        if (_state.FloorVersion is { } floor && VersionOrder.Compare(m.Version, floor) is not >= 0)
            throw new PackageRefused($"{m.Version} is below {floor}, the lowest version allowed on this machine");
        if (_state.HighestVersion is { } highest && VersionOrder.Compare(m.Version, highest) is not >= 0)
            throw new PackageRefused($"{m.Version} is older than {highest}, the newest ever installed here; use rollback to go back one version");
    }

    static void DeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
