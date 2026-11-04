using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VisionCore;
using Xunit;

namespace VisionCore.Tests;

// .vsrecipe bundles are signed with the plant key and bounded in size; an import refuses anything else (Req 17.11 AUD-06).
public sealed class RecipeBundleTests : IDisposable
{
    private readonly ECDsa _plant = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"test_recipe_{Guid.NewGuid():N}.vsrecipe");
    private string PublicPem => _plant.ExportSubjectPublicKeyInfoPem();

    public void Dispose()
    {
        _plant.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void Export(string toolsJson = "{\"tools\":[]}", byte[]? golden = null) =>
        RecipeBundle.ExportToFile(_path, _plant, new RecipeManifest { Name = "Tamper Test" }, toolsJson, golden ?? [0x01, 0x02, 0x03]);

    private void Replace(string entry, byte[] bytes)
    {
        using var archive = ZipFile.Open(_path, ZipArchiveMode.Update);
        archive.GetEntry(entry)?.Delete();
        using var s = archive.CreateEntry(entry).Open();
        s.Write(bytes);
    }

    [Fact]
    public void ExportAndImport_SignedBundle_RestoresAllFields()
    {
        var manifest = new RecipeManifest
        {
            Name = "Blister Packaging Line 4",
            Sku = "MED-BLIST-2026",
            Version = "2.1.0",
            Author = "Gaurang",
            CameraCount = 2,
            TargetCycleTimeMs = 35
        };
        string toolsJson = "{\"tools\":[{\"name\":\"Locate1\",\"type\":\"Locate\"},{\"name\":\"SealCheck\",\"type\":\"Seal\"}], \"note\":\"₹ ünï\"}";
        byte[] goldenBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x11, 0x22, 0x33];
        byte[] maskBytes = [0x01, 0x02, 0x03, 0x04];
        byte[] weightsBytes = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];
        string plcJson = "{\"reject_coil\":12,\"heartbeat\":1}";

        RecipeBundle.ExportToFile(_path, _plant, manifest, toolsJson, goldenBytes, maskBytes, weightsBytes, plcJson);
        var got = RecipeBundle.ImportFromFile(_path, PublicPem);

        Assert.Equal("Blister Packaging Line 4", got.Manifest.Name);
        Assert.Equal("MED-BLIST-2026", got.Manifest.Sku);
        Assert.Equal("2.1.0", got.Manifest.Version);
        Assert.Equal(2, got.Manifest.CameraCount);
        Assert.Equal(35, got.Manifest.TargetCycleTimeMs);
        Assert.Equal(toolsJson, got.ToolsJson);
        Assert.Equal(goldenBytes, got.GoldenImage);
        Assert.Equal(maskBytes, got.MaskImage);
        Assert.Equal(weightsBytes, got.Weights);
        Assert.Equal(plcJson, got.PlcMappingJson);
    }

    [Fact]
    public void ExportAndImport_TaughtPictures_AreSignedEntries()
    {
        byte[] p0 = [1, 2, 3], r3 = [4, 5], r12 = [6];
        RecipeBundle.ExportToFile(_path, _plant, new RecipeManifest(), "{}", [9],
            patterns: new Dictionary<int, byte[]> { [0] = p0 }, references: new Dictionary<int, byte[]> { [3] = r3, [12] = r12 });
        var got = RecipeBundle.ImportFromFile(_path, PublicPem);
        Assert.Equal(p0, got.Patterns[0]);
        Assert.Equal(r3, got.References[3]);
        Assert.Equal(r12, got.References[12]);

        Replace(RecipeBundle.ReferenceEntry(3), [4, 6]);
        Assert.Contains("changed", Assert.Throws<CryptographicException>(() => RecipeBundle.ImportFromFile(_path, PublicPem)).Message);
    }

    [Fact]
    public void Import_BundleWithoutTaughtPictures_StillReads()
    {
        Export();
        var got = RecipeBundle.ImportFromFile(_path, PublicPem);
        Assert.Empty(got.Patterns);
        Assert.Empty(got.References);
    }

    [Theory]
    [InlineData("pattern-01.bmp")]
    [InlineData("pattern-1000.bmp")]
    [InlineData("reference-x.bmp")]
    [InlineData("pattern-1.bmp\n")]
    [InlineData("patterns/pattern-1.bmp")]
    public void Import_TaughtPictureWithAnotherName_IsRefused(string name)
    {
        Export();
        Replace(name, [1]);
        Assert.Contains("unknown entry", Assert.Throws<InvalidDataException>(() => RecipeBundle.ImportFromFile(_path, PublicPem)).Message);
    }

    [Fact]
    public void Export_OversizeTaughtPicture_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => RecipeBundle.ExportToFile(_path, _plant, new RecipeManifest(), "{}", [9],
            patterns: new Dictionary<int, byte[]> { [0] = new byte[RecipeBundle.MaxTaughtImageBytes + 1] }));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Import_UnsignedBundle_IsRefused()
    {
        Export();
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Update))
            archive.GetEntry(RecipeBundle.SignatureEntry)!.Delete();
        var e = Assert.Throws<CryptographicException>(() => RecipeBundle.ImportFromFile(_path, PublicPem));
        Assert.Contains("not signed", e.Message);
    }

    [Fact]
    public void Import_TamperedEntry_IsRefused()
    {
        Export();
        Replace(RecipeBundle.ToolsEntry, Encoding.UTF8.GetBytes("{\"tools\":[{\"tampered\":true}]}"));
        var e = Assert.Throws<CryptographicException>(() => RecipeBundle.ImportFromFile(_path, PublicPem));
        Assert.Contains("changed", e.Message);
    }

    [Fact]
    public void Import_AddedOrRemovedEntry_IsRefused()
    {
        Export();
        Replace(RecipeBundle.WeightsEntry, [1, 2, 3]);
        Assert.Throws<CryptographicException>(() => RecipeBundle.ImportFromFile(_path, PublicPem));

        Export();
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Update))
            archive.GetEntry(RecipeBundle.GoldenImageEntry)!.Delete();
        Assert.Throws<CryptographicException>(() => RecipeBundle.ImportFromFile(_path, PublicPem));
    }

    [Fact]
    public void Import_OtherKey_IsRefused()
    {
        Export();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => RecipeBundle.ImportFromFile(_path, other.ExportSubjectPublicKeyInfoPem()));
        Assert.Throws<CryptographicException>(() => RecipeBundle.ImportFromFile(_path, ""));
    }

    [Fact]
    public void Import_EntryWithFolderPath_IsRefused()
    {
        Export();
        Replace("../escape.json", [1]);
        var e = Assert.Throws<InvalidDataException>(() => RecipeBundle.ImportFromFile(_path, PublicPem));
        Assert.Contains("unknown entry", e.Message);
    }

    [Fact]
    public void Import_OversizeEntry_IsRefusedWhileReading()
    {
        Export();
        // more than tools.json may hold, even though the bytes compress to almost nothing
        Replace(RecipeBundle.ToolsEntry, new byte[JobFile.MaxBytes + 1]);
        var e = Assert.Throws<InvalidDataException>(() => RecipeBundle.ImportFromFile(_path, PublicPem));
        Assert.Contains("larger than", e.Message);
    }

    [Fact]
    public void Export_OversizeEntry_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => Export(golden: new byte[32 * 1024 * 1024 + 1]));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Import_TooManyEntries_IsRefused()
    {
        Export();
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Update))
            for (int i = 0; i < RecipeBundle.MaxEntries; i++) archive.CreateEntry($"x{i}");
        var e = Assert.Throws<InvalidDataException>(() => RecipeBundle.ImportFromFile(_path, PublicPem));
        Assert.Contains("entries", e.Message);
    }
}
