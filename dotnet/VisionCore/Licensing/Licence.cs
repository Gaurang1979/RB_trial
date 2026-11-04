using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionCore.Licensing;

/// <summary>What the licence server said about one device of this installation.</summary>
public sealed record DeviceVerdict(
    [property: JsonPropertyName("serial")] string Serial,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("item")] string? Item,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>
/// The signed answer of the licence server (docs/vision-system/LICENSING.md). The server signs the
/// exact payload bytes with its private key (ECDSA P-256, SHA-256, DER signature); this PC checks
/// them with the public key built into the software, so the answer cannot be edited or forged.
/// </summary>
public sealed class LicencePayload
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";              // "active" or the reason it is not
    [JsonPropertyName("licence")] public string Licence { get; set; } = "";            // licence key
    [JsonPropertyName("customer")] public string Customer { get; set; } = "";
    [JsonPropertyName("installation")] public string Installation { get; set; } = "";  // installation id of this PC
    [JsonPropertyName("tools")] public List<string> Tools { get; set; } = new();       // licensed parent tools
    [JsonPropertyName("max_cameras")] public int MaxCameras { get; set; }
    [JsonPropertyName("valid_until")] public DateTime? ValidUntil { get; set; }        // UTC; null = no end date
    [JsonPropertyName("issued_at")] public DateTime IssuedAt { get; set; }             // UTC, server clock
    [JsonPropertyName("next_check_hours")] public double NextCheckHours { get; set; } = 24;
    [JsonPropertyName("grace_days")] public double GraceDays { get; set; } = 7;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("devices")] public List<DeviceVerdict> Devices { get; set; } = new();
    [JsonPropertyName("message")] public string? Message { get; set; }
    /// <summary>
    /// The content key that decrypts the AI models, product master and learned data at rest
    /// (docs/vision-system/LICENSING.md section 7.4), wrapped (RSA-OAEP-SHA256) for this installation's
    /// public key and base64-encoded. Empty when the server has not issued one yet. The signature covers
    /// it, so it cannot be substituted; a copy of the data on another PC cannot unwrap it. Never logged.
    /// </summary>
    [JsonPropertyName("content_key")] public string ContentKey { get; set; } = "";

    public bool Active => Status == "active";
}

/// <summary>A signed answer as it comes from the server and as it is kept in data/licence.json.</summary>
public sealed class SignedLicence
{
    [JsonPropertyName("payload")] public string Payload { get; set; } = "";      // base64 of the signed JSON bytes
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";  // base64 DER ECDSA signature

    /// <summary>The payload when the signature is good; null when it is not (edited, other key, damaged).</summary>
    public LicencePayload? Verify(string publicKeyPem)
    {
        try
        {
            var bytes = Convert.FromBase64String(Payload);
            var sig = Convert.FromBase64String(Signature);
            using var ec = ECDsa.Create();
            ec.ImportFromPem(publicKeyPem);
            if (!ec.VerifyData(bytes, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return null;
            return JsonSerializer.Deserialize<LicencePayload>(bytes);
        }
        catch { return null; }
    }

    /// <summary>Signs a payload (licence server side, and tests).</summary>
    public static SignedLicence Sign(LicencePayload p, ECDsa key)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(p);
        return new SignedLicence
        {
            Payload = Convert.ToBase64String(bytes),
            Signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
        };
    }
}

public enum LicenceState { NotActivated, Active, OfflineGrace, Expired, Rejected, Invalid }

/// <summary>The licence as this PC sees it now: the last good server answer, and the clock.</summary>
public sealed record LicenceStatus(LicenceState State, LicencePayload? Payload, DateTime? LastCheckUtc, DateTime? NextCheckUtc, string Detail)
{
    /// <summary>Production (cameras, PLC) may run.</summary>
    public bool Licensed => State is LicenceState.Active or LicenceState.OfflineGrace;

    public bool ToolLicensed(string tool) => Licensed && Payload is { } p &&
        (p.Tools.Count == 0 || p.Tools.Contains(tool, StringComparer.OrdinalIgnoreCase));

    /// <summary>The server's verdict for a device serial: null when the server did not list it.</summary>
    public DeviceVerdict? Device(string serial) => Payload?.Devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Works out the state from the last verified answer. Offline (no answer since the last good one):
    /// the licence stays usable until last good check + grace days; a valid-until date ends it anyway.
    /// A clock set back before the server's issue time is treated as invalid.
    /// </summary>
    public static LicenceStatus Evaluate(LicencePayload? p, DateTime? lastGoodUtc, DateTime? lastAttemptUtc, bool lastAttemptFailed, DateTime nowUtc)
    {
        if (p is null || lastGoodUtc is null) return new(LicenceState.NotActivated, null, null, null, "Not activated: enter the licence key and check online.");
        var next = lastGoodUtc.Value.AddHours(Math.Max(1, p.NextCheckHours));
        if (!p.Active) return new(LicenceState.Rejected, p, lastGoodUtc, next, p.Message ?? $"Licence {p.Status}.");
        if (nowUtc < p.IssuedAt.AddHours(-24)) return new(LicenceState.Invalid, p, lastGoodUtc, next, "The PC clock is earlier than the last licence check: set the date and time.");
        if (p.ValidUntil is { } until && nowUtc > until) return new(LicenceState.Expired, p, lastGoodUtc, next, $"Licence ended on {until.ToLocalTime():dd-MM-yyyy}.");
        var graceEnd = lastGoodUtc.Value.AddDays(Math.Max(0, p.GraceDays));
        if (nowUtc > next && lastAttemptFailed)
        {
            if (nowUtc > graceEnd) return new(LicenceState.Expired, p, lastGoodUtc, next, $"No licence check since {lastGoodUtc.Value.ToLocalTime():dd-MM-yyyy HH:mm}: connect the PC to the internet.");
            return new(LicenceState.OfflineGrace, p, lastGoodUtc, next, $"Licence server not reached; works offline until {graceEnd.ToLocalTime():dd-MM-yyyy HH:mm}.");
        }
        return new(LicenceState.Active, p, lastGoodUtc, next, "Licence active.");
    }
}

/// <summary>A random id for this installation, made once and kept in the data folder.</summary>
public static class Installation
{
    public static string Id(string dataFolder)
    {
        var p = Path.Combine(dataFolder, "installation-id.txt");
        if (File.Exists(p) && File.ReadAllText(p).Trim() is { Length: >= 16 } id) return id;
        Directory.CreateDirectory(dataFolder);
        id = "VS-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        File.WriteAllText(p, id);
        return id;
    }

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}

/// <summary>
/// Daily rolling OTP for the Service (Tier 3) and OEM Factory (Tier 4) logins (Req 17.10 REQ-14, REQ-21; PROT-29, PROT-36).
/// HMAC-SHA256 over "vs-otp-v1|tier|licence|yyyy-MM-dd" (UTC day), cut to 6 digits as RFC 4226 does. The ERPNext app
/// (erpnext/vision_licence/vision_licence/core.py) makes the same codes: keep both in step. The secrets are never in the
/// repository (<see cref="CommissioningSecrets"/>). No secret, or a short one: no code, and every code is refused (fail closed).
/// </summary>
public static class OemOtpVerifier
{
    public const string ServiceTier = "service";
    public const string FactoryTier = "factory";
    public const int MinSecretBytes = 16;
    public const string AuditWarningNotice = "Do not share OTP with other users as change log is generated with values, It will be audited in your name - Risky.";

    /// <summary>The 6-digit code of one tier, licence and UTC day (yyyy-MM-dd; today when empty), or null without a usable secret.</summary>
    public static string? Calculate(string tier, string licenceKey, string? dateStr, string? secret)
    {
        var lic = licenceKey?.Trim() ?? "";
        if (lic.Length == 0 || secret is null || Encoding.UTF8.GetByteCount(secret) < MinSecretBytes) return null;
        var day = string.IsNullOrWhiteSpace(dateStr) ? DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : dateStr.Trim();
        if (!DateTime.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) return null;

        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"vs-otp-v1|{tier}|{lic}|{day}"));
        var off = mac[^1] & 0x0F;
        var num = ((mac[off] & 0x7F) << 24) | (mac[off + 1] << 16) | (mac[off + 2] << 8) | mac[off + 3];
        return (num % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool Verify(string tier, string licenceKey, string? candidateOtp, string? dateStr, string? secret)
    {
        var expected = Calculate(tier, licenceKey, dateStr, secret);
        var candidate = candidateOtp?.Trim() ?? "";
        return expected is not null && candidate.Length == 6
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(expected));
    }

    /// <summary>Service (Tier 3) code.</summary>
    public static string? CalculateDailyOtp(string licenceKey, string? dateStr, string? secret) => Calculate(ServiceTier, licenceKey, dateStr, secret);
    public static bool VerifyDailyOtp(string licenceKey, string? candidateOtp, string? dateStr, string? secret) => Verify(ServiceTier, licenceKey, candidateOtp, dateStr, secret);

    /// <summary>OEM Factory (Tier 4) code, from its own secret so a service engineer cannot open factory settings.</summary>
    public static string? CalculateFactoryOtp(string licenceKey, string? dateStr, string? secret) => Calculate(FactoryTier, licenceKey, dateStr, secret);
    public static bool VerifyFactoryOtp(string licenceKey, string? candidateOtp, string? dateStr, string? secret) => Verify(FactoryTier, licenceKey, candidateOtp, dateStr, secret);
}
