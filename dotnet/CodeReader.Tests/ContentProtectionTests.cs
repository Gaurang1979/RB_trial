using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BarcodeReading.Protection;
using Xunit;

namespace BarcodeReading.Tests;

public class ContentProtectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "protect-" + Guid.NewGuid().ToString("N"));
    private static byte[] Key() => RandomNumberGenerator.GetBytes(ContentCipher.KeySize);

    public ContentProtectionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private sealed class FixedKey(byte[]? key) : IContentKeySource
    {
        public bool TryGetKey(Span<byte> dst) { if (key is null) return false; key.CopyTo(dst); return true; }
    }

    [Fact]
    public void EncryptThenDecryptRoundTrips()
    {
        var key = Key();
        var plain = RandomNumberGenerator.GetBytes(5000);
        var box = ContentCipher.Encrypt(plain, key, "codenet.onnx");
        Assert.True(ContentCipher.LooksEncrypted(box));
        Assert.NotEqual(plain, box[36..]);                       // ciphertext is not the plaintext
        Assert.Equal(plain, ContentCipher.Decrypt(box, key, "codenet.onnx"));
    }

    [Fact]
    public void WrongKeyFailsClosed()
    {
        var box = ContentCipher.Encrypt(RandomNumberGenerator.GetBytes(100), Key(), "m");
        Assert.Throws<ContentProtectionException>(() => ContentCipher.Decrypt(box, Key(), "m"));
    }

    [Fact]
    public void WrongRoleFailsClosed()
    {
        var key = Key();
        var box = ContentCipher.Encrypt(RandomNumberGenerator.GetBytes(100), key, "codenet.onnx");
        // A file encrypted for one role cannot be loaded as another (associated data mismatch).
        Assert.Throws<ContentProtectionException>(() => ContentCipher.Decrypt(box, key, "ocrnet.onnx"));
    }

    [Fact]
    public void TamperedCiphertextFailsClosed()
    {
        var key = Key();
        var box = ContentCipher.Encrypt(RandomNumberGenerator.GetBytes(200), key, "m");
        box[^1] ^= 0xFF;                                         // flip a ciphertext byte
        Assert.Throws<ContentProtectionException>(() => ContentCipher.Decrypt(box, key, "m"));
    }

    [Fact]
    public void TruncatedOrWrongMagicFailsClosed()
    {
        var key = Key();
        Assert.Throws<ContentProtectionException>(() => ContentCipher.Decrypt(new byte[10], key, "m"));           // too short
        Assert.Throws<ContentProtectionException>(() => ContentCipher.Decrypt(new byte[64], key, "m"));           // right length, bad magic
    }

    [Fact]
    public void ResolverReadsPlaintextFileUnchanged()
    {
        var path = Path.Combine(_dir, "plain.onnx");
        var bytes = RandomNumberGenerator.GetBytes(1234);
        File.WriteAllBytes(path, bytes);
        // No key source and a plaintext file: returned as-is (developer checkout).
        Assert.Equal(bytes, ContentProtection.ReadArtifact(path, null, null));
    }

    [Fact]
    public void ResolverDecryptsEncryptedFileWithKey()
    {
        var key = Key();
        var model = RandomNumberGenerator.GetBytes(3000);
        var path = Path.Combine(_dir, "codenet.onnx");          // same name even when encrypted
        File.WriteAllBytes(path, ContentCipher.Encrypt(model, key, "codenet.onnx"));
        Assert.Equal(model, ContentProtection.ReadArtifact(path, null, new FixedKey(key)));
    }

    [Fact]
    public void ResolverThrowsOnEncryptedFileWithoutKey()
    {
        var path = Path.Combine(_dir, "codenet.onnx");
        File.WriteAllBytes(path, ContentCipher.Encrypt(RandomNumberGenerator.GetBytes(100), Key(), "codenet.onnx"));
        // Encrypted file, no key installed -> hard stop (line safety), not a silent plaintext read.
        Assert.Throws<ContentProtectionException>(() => ContentProtection.ReadArtifact(path, null, null));
        Assert.Throws<ContentProtectionException>(() => ContentProtection.ReadArtifact(path, null, new FixedKey(null)));
    }

    [Fact]
    public void WriteEncryptsWhenKeyPresentAndRoundTripsThroughRead()
    {
        var key = Key();
        var path = Path.Combine(_dir, "ABC-48.json");
        var text = "{\"product\":\"ABC-48\",\"learned\":[12,24,48]}";
        bool encrypted = ContentProtection.WriteBytes(path, Encoding.UTF8.GetBytes(text), null, new FixedKey(key));
        Assert.True(encrypted);
        Assert.True(ContentCipher.LooksEncrypted(File.ReadAllBytes(path)));          // on disk it is a container
        Assert.Equal(text, Encoding.UTF8.GetString(ContentProtection.ReadArtifact(path, null, new FixedKey(key))));
    }

    [Fact]
    public void WriteStaysPlaintextWhenNoKey()
    {
        var path = Path.Combine(_dir, "ABC-48.json");
        bool encrypted = ContentProtection.WriteBytes(path, Encoding.UTF8.GetBytes("{}"), null, null);
        Assert.False(encrypted);
        Assert.False(ContentCipher.LooksEncrypted(File.ReadAllBytes(path)));
    }
}
