using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BarcodeReading.Protection;
using VisionCore.Licensing;
using Xunit;

namespace VisionCore.Tests;

// Requirements 18.3 ENC-HOST-01: a host that does not check the licence itself (LineRunner, Dashboard) reads the
// answer Vision Studio keeps on this PC and holds the content key only while that answer is good.
public class StoredLicenceKeySourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stored-licence-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _server = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _contentKey = RandomNumberGenerator.GetBytes(ContentCipher.KeySize);
    private readonly DateTime _checked = new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);
    private DateTime _now;
    private const string InstallationId = "VS-0011223344556677AABB";

    public StoredLicenceKeySourceTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "installation-id.txt"), InstallationId);
        _now = _checked.AddHours(1);
    }

    public void Dispose()
    {
        _server.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string ServerPem => _server.ExportSubjectPublicKeyInfoPem();

    private StoredLicenceKeySource Source(string? pem = null) => new(_dir, pem ?? ServerPem, () => _now);

    // What Vision Studio's LicenceService writes after a good check (VisionStudio.Services.LicenceFile).
    private void WriteAnswer(Action<LicencePayload>? edit = null, ECDsa? signer = null, DateTime? lastGood = null, string? wrapped = null)
    {
        var keys = InstallationKeys.Load(_dir);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(keys.PublicKeyPem);
        var p = new LicencePayload
        {
            Status = "active", Licence = "LIC-1", Installation = InstallationId, IssuedAt = _checked,
            NextCheckHours = 24, GraceDays = 7,
            ContentKey = wrapped ?? Convert.ToBase64String(rsa.Encrypt(_contentKey, RSAEncryptionPadding.OaepSHA256)),
        };
        edit?.Invoke(p);
        var file = new { Last = SignedLicence.Sign(p, signer ?? _server), LastGoodUtc = lastGood ?? _checked, LastAttemptFailed = false };
        var path = Path.Combine(_dir, "licence.json");
        File.WriteAllText(path, JsonSerializer.Serialize(file));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000))); // a new stamp for each write
    }

    private static byte[]? Key(IContentKeySource s)
    {
        var k = new byte[ContentCipher.KeySize];
        return s.TryGetKey(k) ? k : null;
    }

    [Fact]
    public void AGoodAnswerGivesTheKeyThatDecryptsAModel()
    {
        WriteAnswer();
        var src = Source();
        Assert.Null(src.InspectionBlockedReason());
        var model = RandomNumberGenerator.GetBytes(2048);
        var box = ContentCipher.Encrypt(model, _contentKey, "codenet.onnx");
        Assert.Equal(model, ContentCipher.Decrypt(box, Key(src)!, "codenet.onnx"));
    }

    [Fact]
    public void NoAnswerOnThisPcIsNoKey()
    {
        var src = Source();
        Assert.Null(Key(src));
        Assert.Contains("no licence answer", src.InspectionBlockedReason());
    }

    [Fact]
    public void AnAnswerSignedByAnotherKeyIsNoKey()
    {
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteAnswer(signer: forger);
        var src = Source();
        Assert.Null(Key(src));
        Assert.Contains("not signed", src.InspectionBlockedReason());
    }

    [Fact]
    public void WithoutTheProgramFolderPublicKeyThereIsNoKey()
    {
        WriteAnswer();
        var src = new StoredLicenceKeySource(_dir, null, () => _now);
        Assert.Null(Key(src));
        Assert.Contains("licence-public.pem", src.InspectionBlockedReason());
    }

    [Fact]
    public void AnAnswerForAnotherInstallationIsNoKey()
    {
        WriteAnswer(p => p.Installation = "VS-OTHERPC000000000000");
        Assert.Contains("another installation", Source().InspectionBlockedReason());
    }

    [Fact]
    public void AnAnswerWithoutAContentKeyOrWithACorruptOneIsNoKey()
    {
        WriteAnswer(wrapped: "");
        Assert.Contains("no content key", Source().InspectionBlockedReason());
        WriteAnswer(wrapped: Convert.ToBase64String(RandomNumberGenerator.GetBytes(256)));
        Assert.Contains("cannot be unwrapped", Source().InspectionBlockedReason());
    }

    [Fact]
    public void ARejectedLicenceIsNoKey()
    {
        WriteAnswer(p => p.Status = "suspended");
        Assert.Null(Key(Source()));
    }

    [Fact]
    public void OverdueChecksCountAsOfflineAndTheKeyGoesWhenTheGraceEnds()
    {
        WriteAnswer();
        var src = Source();
        _now = _checked.AddDays(3);                  // check overdue, within the 7-day grace
        Assert.NotNull(Key(src));
        _now = _checked.AddDays(8);                  // grace over, nobody checked since
        Assert.Null(Key(src));
        Assert.Contains("No licence check since", src.InspectionBlockedReason());
    }

    [Fact]
    public void ARenewalWrittenByVisionStudioReachesARunningHost()
    {
        WriteAnswer();
        var src = Source();
        _now = _checked.AddDays(8);
        Assert.Null(Key(src));
        WriteAnswer(p => p.IssuedAt = _now, lastGood: _now.AddMinutes(-5)); // Vision Studio checked again
        Assert.NotNull(Key(src));
    }

    [Fact]
    public void AHostNeverMakesAnInstallationKeyOfItsOwn()
    {
        var src = Source();
        Assert.Null(Key(src));
        Assert.False(File.Exists(Path.Combine(_dir, "installation-key.bin")));
        Assert.Null(InstallationKeys.TryLoad(_dir));
    }

    [Fact]
    public void InstallWithoutALicenceFolderInstallsNothing()
    {
        var before = ContentProtection.KeySource;
        Assert.Null(StoredLicenceKeySource.Install(null));
        Assert.Null(StoredLicenceKeySource.Install(" "));
        Assert.Same(before, ContentProtection.KeySource);
    }
}
