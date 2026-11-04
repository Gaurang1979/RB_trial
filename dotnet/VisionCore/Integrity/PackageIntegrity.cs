using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionCore.Integrity;

/// <summary>
/// The list of every file shipped in the package and the SHA-256 of each, as of the build that made
/// the package (docs/vision-system/PACKAGE-PROTECTION.md). The build signs it with the vendor's
/// package key (ECDSA P-256, SHA-256, DER signature); the software checks it at start with the public
/// key built into it, so no file can be swapped or patched without the check failing.
/// </summary>
public sealed class PackageManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("created")] public DateTime CreatedUtc { get; set; }
    /// <summary>Relative path (forward slashes, from the package root) → SHA-256 hex (upper-case).</summary>
    [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A signed manifest as it is written next to the program (package.manifest.json).</summary>
public sealed class SignedManifest
{
    [JsonPropertyName("payload")] public string Payload { get; set; } = "";      // base64 of the signed JSON bytes
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";  // base64 DER ECDSA signature

    /// <summary>The manifest when the signature is good; null when it is not (edited, other key, damaged).</summary>
    public PackageManifest? Verify(string publicKeyPem)
    {
        try
        {
            var bytes = Convert.FromBase64String(Payload);
            var sig = Convert.FromBase64String(Signature);
            using var ec = ECDsa.Create();
            ec.ImportFromPem(publicKeyPem);
            if (!ec.VerifyData(bytes, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return null;
            return JsonSerializer.Deserialize<PackageManifest>(bytes);
        }
        catch { return null; }
    }

    /// <summary>Signs a manifest (the package build, and tests).</summary>
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

public enum IntegrityState
{
    /// <summary>Every signed file is present and unchanged.</summary>
    Ok,
    /// <summary>No public key shipped with the software.</summary>
    KeyMissing,
    /// <summary>No manifest next to the program.</summary>
    ManifestMissing,
    /// <summary>The manifest is not signed by the vendor key (edited, forged or damaged).</summary>
    SignatureInvalid,
    /// <summary>A file the manifest covers is no longer on disk.</summary>
    FileMissing,
    /// <summary>A file the manifest covers has a different content now (patched or replaced).</summary>
    FileChanged,
}

/// <summary>The result of the one integrity check, with the first fault found.</summary>
public sealed record IntegrityResult(IntegrityState State, string Detail, string? OffendingFile = null)
{
    /// <summary>The software may run: every shipped file is as it was signed.</summary>
    public bool Ok => State == IntegrityState.Ok;
}

/// <summary>
/// Builds and checks the package manifest. The check is the line-safety gate for tampering
/// (requirements section 12): when it fails the software must not run altered code — the line is
/// told and parts are NG, never a silent wrong read. See <see cref="PackageIntegrityService"/> for
/// how the runtime applies it.
/// </summary>
public static class PackageIntegrity
{
    public const string ManifestFile = "package.manifest.json";
    public const string PublicKeyFile = "package-public.pem";

    /// <summary>SHA-256 of a file as upper-case hex, read in a stream so a large model costs little memory.</summary>
    public static string Sha256Hex(string path)
    {
        using var s = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(s));
    }

    /// <summary>
    /// Builds a manifest for <paramref name="files"/> (relative paths under <paramref name="root"/>).
    /// The package build passes the files it shipped; the signer (PackageSign) then signs it.
    /// </summary>
    public static PackageManifest BuildManifest(string root, IEnumerable<string> files, string version)
    {
        var m = new PackageManifest { Version = version, CreatedUtc = DateTime.UtcNow };
        foreach (var rel in files)
        {
            var norm = rel.Replace('\\', '/');
            m.Files[norm] = Sha256Hex(Path.Combine(root, norm));
        }
        return m;
    }

    /// <summary>
    /// Checks every file the manifest covers against disk, after checking the manifest's own signature.
    /// Returns at the first fault. <paramref name="requiredFiles"/> (optional) must each appear in the
    /// manifest, so a signer cannot leave a critical file out — defence in depth beside the signature.
    /// </summary>
    public static IntegrityResult Check(string root, string? publicKeyPem, string? signedManifestJson,
        IEnumerable<string>? requiredFiles = null)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
            return new(IntegrityState.KeyMissing, $"{PublicKeyFile} is missing from the program folder.");
        if (string.IsNullOrWhiteSpace(signedManifestJson))
            return new(IntegrityState.ManifestMissing, $"{ManifestFile} is missing from the program folder.");

        SignedManifest? signed;
        try { signed = JsonSerializer.Deserialize<SignedManifest>(signedManifestJson); }
        catch { signed = null; }
        var manifest = signed?.Verify(publicKeyPem);
        if (manifest is null)
            return new(IntegrityState.SignatureInvalid, $"{ManifestFile} is not signed by the vendor key (edited, forged or damaged).");

        if (requiredFiles is not null)
            foreach (var req in requiredFiles)
            {
                var norm = req.Replace('\\', '/');
                if (!manifest.Files.ContainsKey(norm))
                    return new(IntegrityState.FileMissing, $"The signed manifest does not cover {norm}.", norm);
            }

        foreach (var (rel, want) in manifest.Files)
        {
            var path = Path.Combine(root, rel);
            if (!File.Exists(path))
                return new(IntegrityState.FileMissing, $"A shipped file is missing: {rel}.", rel);
            string got;
            try { got = Sha256Hex(path); }
            catch (Exception e) { return new(IntegrityState.FileChanged, $"{rel} could not be read: {e.Message}", rel); }
            if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
                return new(IntegrityState.FileChanged, $"A shipped file was changed: {rel}.", rel);
        }

        return new(IntegrityState.Ok, $"All {manifest.Files.Count} shipped files are present and unchanged (package {manifest.Version}).");
    }
}
