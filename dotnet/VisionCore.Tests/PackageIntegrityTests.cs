using System.Security.Cryptography;
using System.Text.Json;
using VisionCore.Integrity;
using Xunit;

namespace VisionCore.Tests;

public sealed class PackageIntegrityTests : IDisposable
{
    private readonly string _root;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _pub;

    public PackageIntegrityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vs-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _pub = _key.ExportSubjectPublicKeyInfoPem();
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Signs the given files and writes the manifest, as the build does.</summary>
    private void SignPackage(params string[] files)
    {
        var manifest = PackageIntegrity.BuildManifest(_root, files, "1.2.3");
        var signed = SignedManifest.Sign(manifest, _key);
        File.WriteAllText(Path.Combine(_root, PackageIntegrity.ManifestFile), JsonSerializer.Serialize(signed));
    }

    private string ManifestJson() => File.ReadAllText(Path.Combine(_root, PackageIntegrity.ManifestFile));

    [Fact]
    public void Unchanged_package_passes()
    {
        Write("engine.dylib", "native code");
        Write("sub/model.onnx", "weights");
        SignPackage("engine.dylib", "sub/model.onnx");

        var r = PackageIntegrity.Check(_root, _pub, ManifestJson());
        Assert.True(r.Ok);
        Assert.Equal(IntegrityState.Ok, r.State);
    }

    [Fact]
    public void Changed_file_is_caught()
    {
        Write("engine.dylib", "native code");
        SignPackage("engine.dylib");
        Write("engine.dylib", "PATCHED native code");   // attacker replaces a file after signing

        var r = PackageIntegrity.Check(_root, _pub, ManifestJson());
        Assert.False(r.Ok);
        Assert.Equal(IntegrityState.FileChanged, r.State);
        Assert.Equal("engine.dylib", r.OffendingFile);
    }

    [Fact]
    public void Missing_file_is_caught()
    {
        Write("engine.dylib", "native code");
        SignPackage("engine.dylib");
        File.Delete(Path.Combine(_root, "engine.dylib"));

        var r = PackageIntegrity.Check(_root, _pub, ManifestJson());
        Assert.False(r.Ok);
        Assert.Equal(IntegrityState.FileMissing, r.State);
    }

    [Fact]
    public void Manifest_signed_by_another_key_is_rejected()
    {
        Write("engine.dylib", "native code");
        SignPackage("engine.dylib");
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var r = PackageIntegrity.Check(_root, other.ExportSubjectPublicKeyInfoPem(), ManifestJson());
        Assert.False(r.Ok);
        Assert.Equal(IntegrityState.SignatureInvalid, r.State);
    }

    [Fact]
    public void Edited_manifest_payload_is_rejected()
    {
        Write("engine.dylib", "native code");
        SignPackage("engine.dylib");
        // attacker edits the signed manifest's payload but cannot re-sign without the private key
        var signed = JsonSerializer.Deserialize<SignedManifest>(ManifestJson())!;
        signed.Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new PackageManifest { Version = "evil", Files = { ["engine.dylib"] = "00" } }));

        var r = PackageIntegrity.Check(_root, _pub, JsonSerializer.Serialize(signed));
        Assert.Equal(IntegrityState.SignatureInvalid, r.State);
    }

    [Fact]
    public void Missing_key_or_manifest_is_reported()
    {
        Assert.Equal(IntegrityState.KeyMissing, PackageIntegrity.Check(_root, null, "{}").State);
        Assert.Equal(IntegrityState.ManifestMissing, PackageIntegrity.Check(_root, _pub, null).State);
    }

    [Fact]
    public void Required_file_left_out_of_manifest_is_caught()
    {
        Write("engine.dylib", "native code");
        SignPackage("engine.dylib");   // "licence-public.pem" deliberately not covered

        var r = PackageIntegrity.Check(_root, _pub, ManifestJson(), requiredFiles: new[] { "licence-public.pem" });
        Assert.False(r.Ok);
        Assert.Equal(IntegrityState.FileMissing, r.State);
        Assert.Equal("licence-public.pem", r.OffendingFile);
    }
}
