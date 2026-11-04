using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sundaram.RemoteService;

/// <summary>
/// What a software package holds, as signed by the vendor (DESIGN.md section 3, capability 7). The JSON is the one Vision
/// System's PackageSign writes (version, created, files), with the product it is for added.
/// </summary>
public sealed class PackageManifest
{
    [JsonPropertyName("product")] public string Product { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("created")] public DateTime CreatedUtc { get; set; }
    /// <summary>Once this package is installed, no version below this may be installed again (a release that fixes a flaw
    /// sets it to itself). Optional.</summary>
    [JsonPropertyName("minVersion")] public string? MinVersion { get; set; }
    /// <summary>After this time the package is refused, so an old package cannot be held back and installed later. Optional.</summary>
    [JsonPropertyName("expires")] public DateTime? ExpiresUtc { get; set; }
    /// <summary>Relative path (forward slashes) → SHA-256, hex.</summary>
    [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>package.manifest.json: the manifest's bytes and the vendor's ECDSA P-256 / SHA-256 signature (DER) over them.</summary>
public sealed class SignedManifest
{
    public const string FileName = "package.manifest.json";
    [JsonPropertyName("payload")] public string Payload { get; set; } = "";
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";

    /// <summary>The manifest when one of the keys signed it; null otherwise (edited, other key, damaged).</summary>
    public PackageManifest? Verify(IEnumerable<string> publicKeysPem)
    {
        byte[] bytes, sig;
        try { bytes = Convert.FromBase64String(Payload); sig = Convert.FromBase64String(Signature); }
        catch (FormatException) { return null; }
        foreach (var pem in publicKeysPem)
        {
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportFromPem(pem);
                if (ec.VerifyData(bytes, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                    return JsonSerializer.Deserialize<PackageManifest>(bytes);
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException or JsonException) { }
        }
        return null;
    }

    public static SignedManifest Sign(PackageManifest m, ECDsa key)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(m);
        return new SignedManifest
        {
            Payload = Convert.ToBase64String(bytes),
            Signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
        };
    }
}

/// <summary>
/// Installs software packages on the host (capability 7). The agent has already checked the signature, every file and the
/// product; the host decides whether the version may go on, swaps it in so that a failure leaves the old version running,
/// and keeps the previous version for rollback. <see cref="FolderPackageInstaller"/> does this for a program in a folder.
/// </summary>
public interface IPackageInstaller
{
    /// <summary>The product name packages must carry.</summary>
    string Product { get; }
    /// <summary>The vendor's public keys (PEM) a package must be signed with; normally the one shipped with the program.</summary>
    IReadOnlyList<string> PublicKeysPem { get; }
    string CurrentVersion { get; }
    /// <summary>The version a rollback would bring back, or null.</summary>
    string? PreviousVersion { get; }
    /// <summary>Largest package accepted.</summary>
    long MaxBytes => 1L << 30;
    /// <summary>Why this package may not go on (older than what runs, for example), or null.</summary>
    string? Check(PackageManifest manifest);
    /// <summary>Puts the checked files of <paramref name="folder"/> in place; returns one line for the engineer.</summary>
    Task<string> InstallAsync(string folder, PackageManifest manifest, CancellationToken ct);
    /// <summary>Brings the previous version back; returns one line for the engineer.</summary>
    Task<string> RollbackAsync(CancellationToken ct);
}

/// <summary>Opening a package: a zip with package.manifest.json and the files it lists, nothing else.</summary>
public static class PackageFile
{
    public const int MaxEntries = 20_000;

    /// <summary>
    /// Unpacks <paramref name="package"/> into the empty folder <paramref name="into"/> and checks it: entry names are plain
    /// relative paths (no "..", no root, no drive, no link), the total size stays under <paramref name="maxUnpacked"/>, the
    /// manifest is signed by one of <paramref name="keys"/>, its product is <paramref name="product"/>, and the files are
    /// exactly those it lists with the SHA-256 it gives. Returns the manifest, or throws <see cref="PackageRefused"/>.
    /// </summary>
    public static PackageManifest Open(string package, string into, IEnumerable<string> keys, string product, long maxUnpacked)
    {
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count > MaxEntries) throw new PackageRefused("too many files in the package");
        long total = 0;
        var top = Path.GetFullPath(into) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;   // a folder
            var unixMode = (e.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new PackageRefused($"link in the package: {name}");
            var parts = name.Split('/');
            if (name.StartsWith('/') || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':')) || !seen.Add(name))
                throw new PackageRefused($"path not allowed in the package: {name}");
            total += e.Length;
            if (total > maxUnpacked) throw new PackageRefused("the package unpacks larger than allowed");
            var target = Path.GetFullPath(Path.Combine([into, .. parts]));
            if (!target.StartsWith(top, StringComparison.Ordinal)) throw new PackageRefused($"path not allowed in the package: {name}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = e.Open();
            using var dst = File.Create(target);
            // the declared length is checked while copying too: a lying header cannot fill the disk
            var buf = new byte[1 << 16];
            long n = 0;
            int r;
            while ((r = src.Read(buf)) > 0)
            {
                n += r;
                if (n > e.Length) throw new PackageRefused($"damaged entry: {name}");
                dst.Write(buf, 0, r);
            }
        }

        var manifestPath = Path.Combine(into, SignedManifest.FileName);
        if (!File.Exists(manifestPath)) throw new PackageRefused("no signed manifest in the package");
        SignedManifest? signed;
        try { signed = JsonSerializer.Deserialize<SignedManifest>(File.ReadAllBytes(manifestPath)); }
        catch (JsonException) { signed = null; }
        var manifest = signed?.Verify(keys) ?? throw new PackageRefused("the package is not signed by Sundaram's key");
        if (!string.Equals(manifest.Product, product, StringComparison.Ordinal))
            throw new PackageRefused($"the package is for {(manifest.Product.Length == 0 ? "no product" : manifest.Product)}, not {product}");
        if (manifest.Version.Length is 0 or > 40) throw new PackageRefused("the package has no version");

        var onDisk = seen.Where(n => !n.Equals(SignedManifest.FileName, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!onDisk.SetEquals(manifest.Files.Keys)) throw new PackageRefused("the package's files differ from its signed list");
        foreach (var (rel, hash) in manifest.Files)
        {
            using var f = File.OpenRead(Path.Combine([into, .. rel.Split('/')]));
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(f)), hash, StringComparison.OrdinalIgnoreCase))
                throw new PackageRefused($"changed after signing: {rel}");
        }
        return manifest;
    }

    /// <summary>Makes a package from a folder (the vendor's build; RemoteService.PackageTool).</summary>
    public static PackageManifest Pack(string folder, string product, string version, ECDsa key, string output,
        string? minVersion = null, DateTime? expiresUtc = null)
    {
        var m = new PackageManifest { Product = product, Version = version, CreatedUtc = DateTime.UtcNow, MinVersion = minVersion, ExpiresUtc = expiresUtc };
        var root = Path.GetFullPath(folder);
        foreach (var abs in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
            if (rel.Equals(SignedManifest.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            using var f = File.OpenRead(abs);
            m.Files[rel] = Convert.ToHexString(SHA256.HashData(f));
        }
        if (File.Exists(output)) File.Delete(output);
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (var rel in m.Files.Keys) zip.CreateEntryFromFile(Path.Combine(root, rel), rel, CompressionLevel.Optimal);
        var e = zip.CreateEntry(SignedManifest.FileName);
        using (var s = e.Open()) JsonSerializer.Serialize(s, SignedManifest.Sign(m, key));
        return m;
    }
}

public sealed class PackageRefused(string why) : Exception(why);

/// <summary>
/// An installer for a program kept in versioned folders: each version goes into versions/&lt;version&gt;, and the file
/// "current" names the one in use (switched last, in one step, so a failed install leaves the old version running). The
/// host's launcher starts the program from the folder "current" names. Versions only go forward unless the host allows
/// otherwise; two versions are kept (current and previous) and older ones removed.
/// </summary>
public sealed class FolderPackageInstaller(string root, string product, IReadOnlyList<string> publicKeysPem, Func<string, CancellationToken, Task>? restart = null)
    : IPackageInstaller
{
    readonly string _versions = Path.Combine(root, "versions");
    string CurrentFile => Path.Combine(root, "current");
    string PreviousFile => Path.Combine(root, "previous");

    public string Product => product;
    public IReadOnlyList<string> PublicKeysPem => publicKeysPem;
    public bool AllowDowngrade { get; init; }
    public string CurrentVersion => Read(CurrentFile) ?? "";
    public string? PreviousVersion => Read(PreviousFile) is { } p && Directory.Exists(Path.Combine(_versions, p)) ? p : null;
    /// <summary>The folder of the version in use.</summary>
    public string? CurrentFolder => CurrentVersion is { Length: > 0 } v ? Path.Combine(_versions, v) : null;

    static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } v ? v : null : null;

    public string? Check(PackageManifest m)
    {
        if (!m.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) return "the version has characters a folder cannot have";
        if (m.Version == CurrentVersion) return $"version {m.Version} is already in use";
        if (!AllowDowngrade && Version.TryParse(m.Version.Split('-')[0], out var next) && Version.TryParse(CurrentVersion.Split('-')[0], out var now) && next < now)
            return $"version {m.Version} is older than {CurrentVersion}; use rollback to go back";
        return null;
    }

    public async Task<string> InstallAsync(string folder, PackageManifest m, CancellationToken ct)
    {
        Directory.CreateDirectory(_versions);
        var target = Path.Combine(_versions, m.Version);
        if (Directory.Exists(target)) Directory.Delete(target, true);   // a half-finished earlier try
        try { Directory.Move(folder, target); }
        catch (IOException) { Copy(folder, target); }   // the unpacked package is on another disk
        var before = CurrentVersion;
        Switch(m.Version, before);
        if (restart is not null) await restart(target, ct);
        Prune();
        return before.Length > 0 ? $"Installed {m.Version} (was {before})" : $"Installed {m.Version}";
    }

    public async Task<string> RollbackAsync(CancellationToken ct)
    {
        var back = PreviousVersion ?? throw new InvalidOperationException("no previous version to go back to");
        var now = CurrentVersion;
        Switch(back, now);
        if (restart is not null) await restart(Path.Combine(_versions, back), ct);
        return $"Rolled back to {back} (was {now})";
    }

    static void Copy(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    void Switch(string to, string from)
    {
        // "previous" first: a crash between the two writes leaves current unchanged and previous pointing at it
        if (from.Length > 0) WriteAtomic(PreviousFile, from);
        WriteAtomic(CurrentFile, to);
    }

    static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    void Prune()
    {
        var keep = new[] { CurrentVersion, Read(PreviousFile) };
        foreach (var d in Directory.EnumerateDirectories(_versions))
            if (!keep.Contains(Path.GetFileName(d)))
                try { Directory.Delete(d, true); } catch (IOException) { }
    }
}
