using System.Security.Cryptography;
using System.Text.Json;
using VisionCore.Integrity;
using VisionStudio.Services;
using Xunit;

public sealed class PackageIntegrityServiceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("vs-integrity-svc-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    void SignPackage()
    {
        File.WriteAllText(Path.Combine(_dir, "engine.dylib"), "native code");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(Path.Combine(_dir, PackageIntegrity.PublicKeyFile), key.ExportSubjectPublicKeyInfoPem());
        var manifest = PackageIntegrity.BuildManifest(_dir, new[] { "engine.dylib" }, "1.0.0");
        var signed = SignedManifest.Sign(manifest, key);
        File.WriteAllText(Path.Combine(_dir, PackageIntegrity.ManifestFile), JsonSerializer.Serialize(signed));
    }

    [Fact]
    public void Not_required_runs_without_a_manifest()
    {
        // a plain source/dev build has no manifest; the line must still run when integrity is off
        var svc = new PackageIntegrityService(_dir, required: false);
        Assert.True(svc.Ok);
    }

    [Fact]
    public void Required_without_a_manifest_blocks()
    {
        // a key is present but no signed manifest: the gate still blocks
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(Path.Combine(_dir, PackageIntegrity.PublicKeyFile), key.ExportSubjectPublicKeyInfoPem());
        var svc = new PackageIntegrityService(_dir, required: true);
        Assert.False(svc.Ok);
        Assert.Equal(IntegrityState.ManifestMissing, svc.Result.State);
    }

    [Fact]
    public void Required_with_a_good_package_runs()
    {
        SignPackage();
        var svc = new PackageIntegrityService(_dir, required: true);
        Assert.True(svc.Ok);
    }

    [Fact]
    public void Required_with_a_tampered_file_blocks()
    {
        SignPackage();
        File.WriteAllText(Path.Combine(_dir, "engine.dylib"), "PATCHED");
        var svc = new PackageIntegrityService(_dir, required: true);
        Assert.False(svc.Ok);
        Assert.Equal(IntegrityState.FileChanged, svc.Result.State);
    }
}
