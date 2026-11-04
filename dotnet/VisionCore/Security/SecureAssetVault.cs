using System.Security.Cryptography;
using BarcodeReading.Protection;

namespace VisionCore.Security;

/// <summary>
/// In-memory asset vault for model weights and recipe templates (AGENTS.md IP rule 3).
/// Encrypts files into opaque .dat blobs on disk and decrypts strictly into RAM buffers.
///
/// There is one asset envelope in the product (Req 17.10 AUD-09): AES-256-GCM in the
/// <see cref="ContentCipher"/> ".vsenc" format, with a random 12-byte nonce per encryption,
/// a 32-byte key and the asset's logical name (for example "codenet.onnx") as associated data,
/// so a blob made for one asset cannot be swapped in for another. There is no native or
/// fallback path. Every failure to open a blob is a <see cref="CryptographicException"/>:
/// the caller does not load the asset and the station goes NG (requirements section 12).
/// </summary>
public static class SecureAssetVault
{
    /// <summary>Key length in bytes (AES-256).</summary>
    public const int KeySize = ContentCipher.KeySize;

    /// <summary>Encrypts raw model or recipe bytes for <paramref name="assetName"/>.</summary>
    public static byte[] Encrypt(byte[] plainData, byte[] key, string assetName)
    {
        ArgumentNullException.ThrowIfNull(plainData);
        CheckKeyAndName(key, assetName);
        return ContentCipher.Encrypt(plainData, key, assetName);
    }

    /// <summary>
    /// Decrypts a blob made for <paramref name="assetName"/> in volatile RAM. Throws
    /// <see cref="CryptographicException"/> for a wrong key, a wrong asset name, a tampered or
    /// truncated blob, or an unknown format; no partial plaintext is returned.
    /// </summary>
    public static byte[] Decrypt(byte[] encryptedBlob, byte[] key, string assetName)
    {
        ArgumentNullException.ThrowIfNull(encryptedBlob);
        CheckKeyAndName(key, assetName);
        try
        {
            return ContentCipher.Decrypt(encryptedBlob, key, assetName);
        }
        catch (ContentProtectionException e)
        {
            throw new CryptographicException(e.Message, e);
        }
    }

    /// <summary>Encrypts a source file (e.g. model.onnx) to an opaque cache file (.dat).</summary>
    public static void EncryptFile(string plainFilePath, string targetDatPath, byte[] key, string assetName)
    {
        byte[] plain = File.ReadAllBytes(plainFilePath);
        byte[] encrypted = Encrypt(plain, key, assetName);

        string? dir = Path.GetDirectoryName(targetDatPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        File.WriteAllBytes(targetDatPath, encrypted);
    }

    /// <summary>
    /// Decrypts an opaque cache file (.dat) strictly into memory; never writes a decrypted file.
    /// </summary>
    public static byte[] DecryptFileToMemory(string datFilePath, byte[] key, string assetName)
    {
        byte[] encrypted = File.ReadAllBytes(datFilePath);
        return Decrypt(encrypted, key, assetName);
    }

    private static void CheckKeyAndName(byte[] key, string assetName)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize) throw new ArgumentException($"Key must be {KeySize} bytes (AES-256).", nameof(key));
        ArgumentException.ThrowIfNullOrEmpty(assetName);
    }
}
