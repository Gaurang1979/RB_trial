using System;

namespace BarcodeReading.Protection;

/// <summary>
/// Supplies the 32-byte content key that decrypts the AI models, product master and learned data at
/// rest. The key is never on disk in the package: it comes only from the signed ERPNext licence answer,
/// wrapped for this installation (docs/vision-system/LICENSING.md section 7.4). An implementation holds
/// the key in memory only while the licence is active (plus its offline grace) and returns false
/// otherwise, so a copy of the data on another PC — or a PC whose licence has lapsed — cannot decrypt it.
///
/// This interface lives in the low CodeReader project (where the model loaders are); the licence client
/// in VisionCore implements it and installs it via <see cref="ContentProtection.KeySource"/> at startup.
/// </summary>
public interface IContentKeySource
{
    /// <summary>
    /// Copies the content key into <paramref name="key"/> (length <see cref="ContentCipher.KeySize"/>)
    /// and returns true, or returns false when no key is available (not licensed, grace expired, or the
    /// wrapped key could not be unwrapped). Returning false is line-safe: callers refuse to decrypt and
    /// the station goes NG rather than reading without the model it was told to use.
    /// </summary>
    bool TryGetKey(Span<byte> key);
}
