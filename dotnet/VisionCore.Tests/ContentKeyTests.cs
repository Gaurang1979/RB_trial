using System;
using System.IO;
using System.Security.Cryptography;
using BarcodeReading.Protection;
using VisionCore.Licensing;
using Xunit;

namespace VisionCore.Tests;

public class ContentKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inst-keys-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    // Stands in for the ERPNext licence server: wrap a content key for the installation's public key.
    private static string WrapForServer(string publicKeyPem, byte[] contentKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return Convert.ToBase64String(rsa.Encrypt(contentKey, RSAEncryptionPadding.OaepSHA256));
    }

    [Fact]
    public void InstallationKeyPersistsAndUnwrapsTheContentKey()
    {
        var keys = InstallationKeys.Load(_dir);
        var contentKey = RandomNumberGenerator.GetBytes(ContentCipher.KeySize);
        var wrapped = WrapForServer(keys.PublicKeyPem, contentKey);

        Assert.Equal(contentKey, keys.UnwrapContentKey(wrapped));

        // Reloaded from disk, the same private key still unwraps it (survives a restart).
        var keys2 = InstallationKeys.Load(_dir);
        Assert.Equal(keys.PublicKeyPem, keys2.PublicKeyPem);
        Assert.Equal(contentKey, keys2.UnwrapContentKey(wrapped));
    }

    [Fact]
    public void AKeyWrappedForAnotherInstallationCannotBeUnwrapped()
    {
        var mine = InstallationKeys.Load(_dir);
        using var other = RSA.Create(2048);
        var wrappedForOther = Convert.ToBase64String(other.Encrypt(RandomNumberGenerator.GetBytes(ContentCipher.KeySize), RSAEncryptionPadding.OaepSHA256));
        Assert.ThrowsAny<CryptographicException>(() => mine.UnwrapContentKey(wrappedForOther));
    }

    [Fact]
    public void EndToEnd_ServerWrappedKeyDecryptsAModelBlob()
    {
        var keys = InstallationKeys.Load(_dir);
        var contentKey = RandomNumberGenerator.GetBytes(ContentCipher.KeySize);
        var model = RandomNumberGenerator.GetBytes(4096);
        var box = ContentCipher.Encrypt(model, contentKey, "codenet.onnx");   // build machine encrypts with the content key

        var unwrapped = keys.UnwrapContentKey(WrapForServer(keys.PublicKeyPem, contentKey));
        Assert.Equal(model, ContentCipher.Decrypt(box, unwrapped, "codenet.onnx"));
    }

    [Fact]
    public void KeySourceHandsOutTheKeyOnlyWhileLicensed()
    {
        var keys = InstallationKeys.Load(_dir);
        var contentKey = RandomNumberGenerator.GetBytes(ContentCipher.KeySize);
        bool licensed = true;
        var src = new LicenceContentKeySource(() => licensed);
        Assert.True(src.SetFromLicence(WrapForServer(keys.PublicKeyPem, contentKey), keys));

        var buf = new byte[ContentCipher.KeySize];
        Assert.True(src.TryGetKey(buf));
        Assert.Equal(contentKey, buf);

        // Licence lapses: the key is withheld (line safety), even though it is still held in memory.
        licensed = false;
        Assert.False(src.TryGetKey(buf));

        // Cleared explicitly: never handed out again.
        licensed = true;
        src.Clear();
        Assert.False(src.TryGetKey(buf));
    }

    [Fact]
    public void AnEmptyOrUnusableWrappedKeyLeavesNoKey()
    {
        var keys = InstallationKeys.Load(_dir);
        var src = new LicenceContentKeySource(() => true);
        Assert.False(src.SetFromLicence("", keys));                       // server has not issued one yet
        Assert.False(src.SetFromLicence("not-base64!!", keys));           // corrupt
        Assert.False(src.TryGetKey(new byte[ContentCipher.KeySize]));
    }
}
