using System;
using System.Security.Cryptography;
using System.Text;

namespace BarcodeReading.Protection;

/// <summary>Thrown when a protected artifact cannot be decrypted (wrong or missing key, tampered or
/// truncated file, wrong role). The caller treats this as a hard stop: the model or product is not
/// loaded and the station goes NG (requirements section 12), never a silent wrong read.</summary>
public sealed class ContentProtectionException : Exception
{
    public ContentProtectionException(string message) : base(message) { }
    public ContentProtectionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// At-rest protection of the AI models, product master and learned data (owner, 2026-11-01;
/// docs/vision-system/LICENSING.md section 7.4). A <c>.vsenc</c> container is AES-256-GCM: a copy of
/// the file on another PC is useless without the content key, which comes only from the licence.
///
/// Container layout (all little-endian offsets, fixed sizes):
///   [0..5]   magic "VSENC1"      (6 bytes, ASCII)
///   [6]      version             (1 = this format)
///   [7]      reserved            (0)
///   [8..19]  nonce               (12 bytes, random per file)
///   [20..35] tag                 (16 bytes, GCM authentication tag)
///   [36..]   ciphertext          (same length as the plaintext)
/// The artifact's logical name (e.g. "codenet.onnx") is the GCM associated data, so a file encrypted
/// for one role cannot be swapped in for another even under the same key.
/// </summary>
public static class ContentCipher
{
    public const int KeySize = 32;     // AES-256
    private const int NonceSize = 12;  // AesGcm standard nonce
    private const int TagSize = 16;    // AesGcm full tag
    private static readonly byte[] Magic = "VSENC1"u8.ToArray();
    private const byte Version = 1;
    private const int HeaderSize = 8 + NonceSize + TagSize; // 36

    /// <summary>True when <paramref name="data"/> begins with the VSENC magic (not a validity check).</summary>
    public static bool LooksEncrypted(ReadOnlySpan<byte> data)
        => data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    /// <summary>Encrypts <paramref name="plaintext"/> into a <c>.vsenc</c> container. Build-time only.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, string logicalName)
    {
        if (key.Length != KeySize) throw new ArgumentException($"content key must be {KeySize} bytes", nameof(key));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        var aad = Encoding.UTF8.GetBytes(logicalName ?? "");
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plaintext, cipher, tag, aad);

        var outBuf = new byte[HeaderSize + cipher.Length];
        Magic.CopyTo(outBuf.AsSpan(0));
        outBuf[6] = Version;
        outBuf[7] = 0;
        nonce.CopyTo(outBuf.AsSpan(8));
        tag.CopyTo(outBuf.AsSpan(20));
        cipher.CopyTo(outBuf.AsSpan(HeaderSize));
        return outBuf;
    }

    /// <summary>
    /// Decrypts a <c>.vsenc</c> container into memory. Throws <see cref="ContentProtectionException"/> on
    /// any problem (bad magic or version, truncated, wrong key, tampered, wrong role) and returns no partial
    /// output: GCM verifies the whole file before a single plaintext byte is released.
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> container, ReadOnlySpan<byte> key, string logicalName)
    {
        if (key.Length != KeySize) throw new ContentProtectionException($"content key must be {KeySize} bytes");
        if (container.Length < HeaderSize) throw new ContentProtectionException("protected file is too short");
        if (!LooksEncrypted(container)) throw new ContentProtectionException("not a protected (.vsenc) file");
        if (container[6] != Version) throw new ContentProtectionException($"unknown protected-file version {container[6]}");
        if (container[7] != 0) throw new ContentProtectionException("protected file header is damaged"); // reserved byte (Req 17.10 AUD-09)

        var nonce = container.Slice(8, NonceSize);
        var tag = container.Slice(20, TagSize);
        var cipher = container[HeaderSize..];
        var plain = new byte[cipher.Length];
        var aad = Encoding.UTF8.GetBytes(logicalName ?? "");
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipher, tag, plain, aad);
        }
        catch (CryptographicException e)
        {
            // Authentication failed: wrong key, tampered bytes, or wrong role. Do not keep any output.
            CryptographicOperations.ZeroMemory(plain);
            throw new ContentProtectionException("protected file failed authentication (wrong key or tampered)", e);
        }
        return plain;
    }
}
