using System.Security.Cryptography;
using BarcodeReading.Protection;

namespace VisionCore.Licensing;

/// <summary>
/// Holds the content key that decrypts the models, product master and learned data at rest, and hands it
/// out only while the licence is active (docs/vision-system/LICENSING.md section 7.4). The key lives in
/// memory only: it is set from the signed licence answer (unwrapped with the installation private key) and
/// returned by <see cref="TryGetKey"/> only while <c>licensed()</c> is true, so a lapsed or offline-expired
/// licence stops decryption and the station goes NG rather than reading with a stale key.
///
/// Install this once at host startup: <c>ContentProtection.KeySource = source;</c>.
/// </summary>
public sealed class LicenceContentKeySource : IContentKeySource
{
    private readonly Func<bool> _licensed;
    private readonly object _lock = new();
    private byte[]? _key;

    /// <param name="licensed">Returns true while production may run (the licence is Active or in offline grace).</param>
    public LicenceContentKeySource(Func<bool> licensed) => _licensed = licensed;

    /// <summary>Unwraps and stores the content key from a verified licence answer. An empty wrapped key, or one
    /// that cannot be unwrapped (wrapped for another installation, corrupt), clears the key: decryption then
    /// fails closed. Returns true when a key is now held.</summary>
    public bool SetFromLicence(string wrappedContentKeyBase64, InstallationKeys keys)
    {
        if (string.IsNullOrEmpty(wrappedContentKeyBase64)) { Clear(); return false; }
        byte[]? unwrapped = null;
        try
        {
            unwrapped = keys.UnwrapContentKey(wrappedContentKeyBase64);
            if (unwrapped.Length != ContentCipher.KeySize) { Clear(); return false; }
            lock (_lock)
            {
                if (_key is not null) CryptographicOperations.ZeroMemory(_key);
                _key = (byte[])unwrapped.Clone();
            }
            return true;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            Clear();
            return false;
        }
        finally
        {
            if (unwrapped is not null) CryptographicOperations.ZeroMemory(unwrapped);
        }
    }

    /// <summary>Forgets the content key (e.g. when the licence is removed). Decryption then fails closed.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_key is not null) CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }

    /// <inheritdoc />
    public bool TryGetKey(Span<byte> key)
    {
        if (key.Length != ContentCipher.KeySize) return false;
        if (!_licensed()) return false;        // line safety: no key once the licence is not active
        lock (_lock)
        {
            if (_key is null) return false;
            _key.CopyTo(key);
            return true;
        }
    }
}
