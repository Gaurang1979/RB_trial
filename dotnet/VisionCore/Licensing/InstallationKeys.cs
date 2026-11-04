using System.Security.Cryptography;

namespace VisionCore.Licensing;

/// <summary>
/// This installation's own RSA key pair (docs/vision-system/LICENSING.md section 7.4). The public key is
/// sent with the licence check; the licence server wraps the content key (which decrypts the models and
/// data at rest) for this public key and returns it in the signed answer. The private key never leaves the
/// PC and unwraps the content key here.
///
/// The private key is created once and kept in the data folder. On Windows it must be protected with DPAPI
/// (CurrentUser) before shipping; this build stores it in a file with the protection left pluggable
/// (<see cref="_protect"/>/<see cref="_unprotect"/>) so the DPAPI wrap drops in with the ERPNext content-key
/// work. Wrapping uses RSA-OAEP-SHA256; the content key itself is a 32-byte AES-256 key.
/// </summary>
public sealed class InstallationKeys
{
    private readonly RSA _rsa;

    private InstallationKeys(RSA rsa) => _rsa = rsa;

    /// <summary>The public key in PEM, to send to the licence server so it can wrap the content key.</summary>
    public string PublicKeyPem => _rsa.ExportSubjectPublicKeyInfoPem();

    /// <summary>
    /// Loads the installation key pair from <paramref name="dataFolder"/>, creating it on first use.
    /// </summary>
    public static InstallationKeys Load(string dataFolder,
                                        Func<byte[], byte[]>? protect = null,
                                        Func<byte[], byte[]>? unprotect = null)
    {
        Directory.CreateDirectory(dataFolder);
        var path = Path.Combine(dataFolder, "installation-key.bin");
        var rsa = RSA.Create(2048);
        if (File.Exists(path))
        {
            var stored = File.ReadAllBytes(path);
            var pkcs8 = unprotect is null ? stored : unprotect(stored);
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            if (unprotect is not null) CryptographicOperations.ZeroMemory(pkcs8);
            return new InstallationKeys(rsa);
        }
        var fresh = rsa.ExportPkcs8PrivateKey();
        var toStore = protect is null ? fresh : protect(fresh);
        // Write then restrict: the private key is a secret. On Windows it should also be DPAPI-wrapped (protect).
        File.WriteAllBytes(path, toStore);
        TryRestrict(path);
        if (protect is not null) CryptographicOperations.ZeroMemory(fresh);
        return new InstallationKeys(rsa);
    }

    /// <summary>
    /// Loads the key pair <see cref="Load"/> made in <paramref name="dataFolder"/>, or returns null when there is
    /// none. For hosts that only read the licence another program checks (LineRunner, Dashboard): they must
    /// never make a key pair of their own, which no licence answer would be wrapped for.
    /// </summary>
    public static InstallationKeys? TryLoad(string dataFolder, Func<byte[], byte[]>? unprotect = null)
        => File.Exists(Path.Combine(dataFolder, "installation-key.bin")) ? Load(dataFolder, unprotect: unprotect) : null;

    /// <summary>
    /// Unwraps the base64, RSA-OAEP-SHA256 wrapped content key from the licence answer into its 32 bytes.
    /// Throws <see cref="CryptographicException"/> if it was wrapped for another key or is corrupt; the
    /// caller treats that as "no content key" so the line goes NG rather than reading with a wrong key.
    /// </summary>
    public byte[] UnwrapContentKey(string wrappedBase64)
    {
        var wrapped = Convert.FromBase64String(wrappedBase64);
        return _rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
    }

    private static void TryRestrict(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // dev/Unix: owner only
            // On Windows, DPAPI (the `protect` delegate) is the real protection; see the class note.
        }
        catch { /* best-effort; DPAPI is the real protection on Windows */ }
    }
}
