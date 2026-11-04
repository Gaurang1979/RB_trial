using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BarcodeReading.Protection;

/// <summary>
/// The one place the rest of the software reads a protected artifact (AI model, product master,
/// learned data). It resolves plaintext-vs-encrypted automatically by the file's content, and when a
/// file is encrypted it requires the licence content key — so a shipped package (encrypted files, no
/// plaintext) only ever loads with a live licence, while a developer checkout (plaintext files, no key
/// source) keeps working unchanged.
///
/// Line safety (requirements section 12): if a file is encrypted and no key is available, or the file
/// fails authentication, these methods throw <see cref="ContentProtectionException"/>. Callers let that
/// stop the load, so the station reports NG — never a silent wrong read, never a partially decrypted model.
/// </summary>
public static class ContentProtection
{
    /// <summary>
    /// The content-key source, installed once at host startup by the licence client (VisionCore). Null in
    /// a developer checkout, where artifacts are plaintext and no decryption is needed.
    /// </summary>
    public static IContentKeySource? KeySource { get; set; }

    /// <summary>True when protected files can be read here (a key source is installed).</summary>
    public static bool HasKeySource => KeySource is not null;

    /// <summary>
    /// Reads an artifact from <paramref name="path"/> and returns its plaintext bytes. If the file's
    /// content is a <c>.vsenc</c> container it is decrypted in memory with the licence content key
    /// (<paramref name="logicalName"/> defaults to the file name, the container's associated data);
    /// otherwise the bytes are returned as-is. Throws <see cref="FileNotFoundException"/> if the file is
    /// missing, <see cref="ContentProtectionException"/> if it is encrypted but cannot be decrypted.
    /// </summary>
    public static byte[] ReadArtifact(string path, string? logicalName = null)
        => ReadArtifact(path, logicalName, KeySource);

    // Testable core: an explicit key source instead of the global static, so tests never mutate shared state.
    internal static byte[] ReadArtifact(string path, string? logicalName, IContentKeySource? source)
    {
        byte[] raw = File.ReadAllBytes(path);               // FileNotFoundException if absent, as before
        if (!ContentCipher.LooksEncrypted(raw)) return raw; // plaintext (developer / pre-encryption)
        logicalName ??= Path.GetFileName(path);
        byte[] key = RequireKey(path, source);
        try
        {
            return ContentCipher.Decrypt(raw, key, logicalName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Reads a protected text artifact (product master JSON, learned data) as a UTF-8 string, decrypting
    /// if the content is encrypted. Same failure rules as <see cref="ReadArtifact"/>.
    /// </summary>
    public static string ReadText(string path)
        => Encoding.UTF8.GetString(ReadArtifact(path));

    /// <summary>
    /// Serialises <paramref name="plaintext"/> to <paramref name="path"/>, encrypting it when a content
    /// key is available (production), or writing it in the clear when none is (developer checkout). The
    /// file name does not change: the content is a <c>.vsenc</c> container that <see cref="ReadArtifact"/>
    /// detects by its magic. Returns true when the bytes were encrypted.
    /// </summary>
    public static bool WriteBytes(string path, ReadOnlySpan<byte> plaintext, string? logicalName = null)
        => WriteBytes(path, plaintext, logicalName, KeySource);

    internal static bool WriteBytes(string path, ReadOnlySpan<byte> plaintext, string? logicalName, IContentKeySource? source)
    {
        logicalName ??= Path.GetFileName(path);
        byte[] toWrite;
        bool encrypted = false;
        if (TryGetKey(source, out byte[]? key))
        {
            try { toWrite = ContentCipher.Encrypt(plaintext, key, logicalName); encrypted = true; }
            finally { CryptographicOperations.ZeroMemory(key!); }
        }
        else
        {
            toWrite = plaintext.ToArray();
        }
        File.WriteAllBytes(path, toWrite);
        return encrypted;
    }

    /// <summary>Writes a UTF-8 text artifact, encrypted when a key is available. See <see cref="WriteBytes"/>.</summary>
    public static bool WriteText(string path, string text, string? logicalName = null)
        => WriteBytes(path, Encoding.UTF8.GetBytes(text), logicalName);

    /// <summary>
    /// Gets a copy of the content key, or false when none is available. The caller must zero the returned
    /// array after use (<see cref="CryptographicOperations.ZeroMemory"/>).
    /// </summary>
    public static bool TryGetKey(out byte[]? key) => TryGetKey(KeySource, out key);

    internal static bool TryGetKey(IContentKeySource? source, out byte[]? key)
    {
        if (source is null) { key = null; return false; }
        var buf = new byte[ContentCipher.KeySize];
        if (!source.TryGetKey(buf))
        {
            CryptographicOperations.ZeroMemory(buf);
            key = null;
            return false;
        }
        key = buf;
        return true;
    }

    private static byte[] RequireKey(string path, IContentKeySource? source)
    {
        if (source is null)
            throw new ContentProtectionException(
                $"'{Path.GetFileName(path)}' is protected but no licence content key is installed: the software is not licensed on this PC.");
        if (!TryGetKey(source, out byte[]? key) || key is null)
            throw new ContentProtectionException(
                $"'{Path.GetFileName(path)}' is protected but the licence content key is not available (not licensed, or offline grace expired).");
        return key;
    }
}
