using System.Security.Cryptography;
using System.Text.Json;
using BarcodeReading.Protection;

namespace VisionCore.Licensing;

/// <summary>
/// The content key for a host that loads models but does not check the licence itself (LineRunner, the
/// Dashboard station; requirements 18.3, ENC-HOST-01). It reads the licence answer that Vision Studio's
/// licence check keeps on this PC (<c>licence.json</c> and <c>installation-key.bin</c> in its data folder) and
/// hands out the content key only while that answer is good:
/// <list type="bullet">
/// <item>signed by the licence server, checked with <c>licence-public.pem</c> from the program folder only (a
/// key in the data folder could be swapped together with a forged answer);</item>
/// <item>for this installation (<c>installation-id.txt</c>), with a content key this PC's key pair unwraps;</item>
/// <item>Active, or within its offline grace. This host never asks the server, so once the next check is due it
/// counts as offline: the key goes when the grace ends unless Vision Studio has checked again meanwhile.</item>
/// </list>
/// The file is read again whenever it changes, so a renewal by Vision Studio reaches a running host. The key
/// is asked for only when a model or product loads, so an entitlement that ends mid-batch takes effect at
/// the next start or product load (PROT-06). Any doubt is no key: the load fails closed and the host refuses
/// to inspect (PROT-04, PROT-05).
/// </summary>
public sealed class StoredLicenceKeySource : IContentKeySource
{
    private readonly string _folder;
    private readonly string? _publicKeyPem;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private (DateTime Written, long Length)? _readStamp;
    private byte[]? _key;
    private LicencePayload? _payload;
    private DateTime? _lastGoodUtc;
    private string _detail = "The licence answer has not been read yet.";

    // The part of Vision Studio's data/licence.json (VisionStudio.Services.LicenceFile) this host needs;
    // the names follow that class, which writes them.
    private sealed class StoredAnswer
    {
        public SignedLicence? Last { get; set; }
        public DateTime? LastGoodUtc { get; set; }
    }

    /// <param name="licenceFolder">Vision Studio's data folder on this PC (holds licence.json and the installation key).</param>
    /// <param name="publicKeyPem">The licence server's public key, from the program folder; null when it is missing.</param>
    public StoredLicenceKeySource(string licenceFolder, string? publicKeyPem, Func<DateTime>? utcNow = null)
    {
        _folder = licenceFolder;
        _publicKeyPem = string.IsNullOrWhiteSpace(publicKeyPem) ? null : publicKeyPem;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Makes the source for a host and installs it as <see cref="ContentProtection.KeySource"/>, so every model
    /// and product load decrypts through it. Returns null, and installs nothing, when no licence folder is set
    /// (a development PC: artifacts stay plaintext, and an encrypted one still fails closed).
    /// </summary>
    public static StoredLicenceKeySource? Install(string? licenceFolder, string? publicKeyPath = null)
    {
        if (string.IsNullOrWhiteSpace(licenceFolder)) return null;
        publicKeyPath ??= Path.Combine(AppContext.BaseDirectory, "licence-public.pem");
        string? pem = File.Exists(publicKeyPath) ? File.ReadAllText(publicKeyPath) : null;
        var source = new StoredLicenceKeySource(Path.GetFullPath(licenceFolder), pem);
        ContentProtection.KeySource = source;
        return source;
    }

    /// <summary>The folder the licence answer is read from.</summary>
    public string LicenceFolder => _folder;

    /// <summary>
    /// Null when an inspection may start (the content key is available now); otherwise the reason it may not,
    /// for the NG message, the alarm and the audit trail. The key itself is never in the text.
    /// </summary>
    public string? InspectionBlockedReason()
    {
        Span<byte> probe = stackalloc byte[ContentCipher.KeySize];
        try
        {
            if (TryGetKey(probe)) return null;
            lock (_lock) return "licence content key not available: " + _detail;
        }
        finally { CryptographicOperations.ZeroMemory(probe); }
    }

    /// <inheritdoc />
    public bool TryGetKey(Span<byte> key)
    {
        if (key.Length != ContentCipher.KeySize) return false;
        lock (_lock)
        {
            Refresh();
            if (_key is null) return false;
            // the clock moves on while the file stays the same: judge the answer again on every ask.
            // lastAttemptFailed: true, because this host never checks: an overdue check counts as offline
            var status = LicenceStatus.Evaluate(_payload, _lastGoodUtc, _utcNow(), lastAttemptFailed: true, _utcNow());
            if (!status.Licensed) { _detail = status.Detail; return false; }
            _key.CopyTo(key);
            return true;
        }
    }

    // Reads licence.json again when it changed (or was never read). Called under _lock.
    private void Refresh()
    {
        var path = Path.Combine(_folder, "licence.json");
        (DateTime, long)? stamp = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) stamp = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (stamp is not null && stamp == _readStamp) return;
        _readStamp = stamp;
        Forget();
        if (_publicKeyPem is null) { _detail = "licence-public.pem is missing from the program folder."; return; }
        if (stamp is null) { _detail = $"no licence answer on this PC ({path}): check the licence in Vision Studio."; return; }

        StoredAnswer? stored;
        try { stored = JsonSerializer.Deserialize<StoredAnswer>(File.ReadAllText(path)); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _detail = "the stored licence answer cannot be read.";
            return;
        }
        if (stored?.Last is null || stored.LastGoodUtc is null) { _detail = "the licence has not been checked on this PC yet."; return; }
        var payload = stored.Last.Verify(_publicKeyPem);
        if (payload is null) { _detail = "the stored licence answer is not signed by the licence server."; return; }
        if (payload.Installation.Length > 0 && payload.Installation != StoredInstallationId())
        {
            _detail = "the stored licence answer is for another installation.";
            return;
        }
        if (payload.ContentKey.Length == 0) { _detail = "the licence answer carries no content key."; return; }

        InstallationKeys? keys;
        try { keys = InstallationKeys.TryLoad(_folder); }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException) { keys = null; }
        if (keys is null) { _detail = "this PC's installation key is missing or damaged."; return; }
        byte[]? unwrapped = null;
        try
        {
            unwrapped = keys.UnwrapContentKey(payload.ContentKey);
            if (unwrapped.Length != ContentCipher.KeySize) { _detail = "the content key in the licence answer is not valid."; return; }
            _key = (byte[])unwrapped.Clone();
            _payload = payload;
            _lastGoodUtc = stored.LastGoodUtc;
            _detail = "licence active.";
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            _detail = "the content key in the licence answer cannot be unwrapped on this PC.";
        }
        finally
        {
            if (unwrapped is not null) CryptographicOperations.ZeroMemory(unwrapped);
        }
    }

    // Read only: Installation.Id would make a new id, which no licence answer names.
    private string? StoredInstallationId()
    {
        try
        {
            var p = Path.Combine(_folder, "installation-id.txt");
            return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Forget()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _payload = null;
        _lastGoodUtc = null;
    }
}
