using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VisionCore.Licensing;

namespace VisionStudio.Services;

/// <summary>
/// Which roles a signed-in user gets (Req 17.10 MRG-09 item 4; REQ-14, REQ-21). The Service role needs today's
/// service OTP for this PC's licence key, the OEM role today's factory OTP; the other roles need only the password.
/// No licence key or no secret: no code is accepted (fail closed).
/// </summary>
public static class OemSignIn
{
    /// <param name="Roles">The roles to sign in with; empty means the sign-in is refused.</param>
    /// <param name="Granted">Service or OEM roles the code opened.</param>
    /// <param name="Refused">Service or OEM roles held but not opened (no code, wrong code, no secret, locked).</param>
    /// <param name="Locked">Too many wrong codes: the code was not checked (<see cref="OtpAttempts"/>).</param>
    public sealed record Outcome(IReadOnlyList<string> Roles, IReadOnlyList<string> Granted, IReadOnlyList<string> Refused, bool Locked = false);

    /// <param name="day">UTC day (yyyy-MM-dd); today when null.</param>
    public static Outcome Decide(IEnumerable<string> userRoles, string? otp, string? licenceKey,
                                 string? serviceSecret, string? factorySecret, string? day = null)
    {
        var lic = licenceKey ?? "";
        var roles = new List<string>();
        var granted = new List<string>();
        var refused = new List<string>();
        foreach (var r in userRoles.Distinct())
        {
            bool? opened = r switch
            {
                Services.Roles.Service => OemOtpVerifier.VerifyDailyOtp(lic, otp, day, serviceSecret),
                Services.Roles.OEM => OemOtpVerifier.VerifyFactoryOtp(lic, otp, day, factorySecret),
                _ => null,
            };
            if (opened is null) roles.Add(r);
            else if (opened.Value) { roles.Add(r); granted.Add(r); }
            else refused.Add(r);
        }
        return new Outcome(roles, granted, refused);
    }
}

/// <summary>
/// Wrong Service / OEM codes per user (Req 17.10 MRG-09 item 5): after <see cref="MaxFailures"/> wrong codes within
/// <see cref="Window"/> no code of that user is checked until the window has passed, so a known password cannot be
/// used to try the million codes. A right code clears the count, unless another role of the user was refused.
/// The count is kept on disk (<see cref="FileName"/> in the data folder, one protected blob like the commissioning
/// secrets), so a restart keeps the lock. A file that is missing, cannot be opened, differs from what was last written
/// or cannot be written holds every user's codes for <see cref="Window"/>, which is at least as long as any lock it
/// could have held; the hold is written too, so a restart does not end it. Without a protector (bench PCs) the count
/// is kept in memory only.
/// </summary>
public sealed class OtpAttempts
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public const string FileName = "signin.dat";
    /// <summary>The runtime's own count for the Service code taken through remote support (REQ-11). A file of its own:
    /// each process holds every code when the file is not what it last wrote, so two processes must never share one.</summary>
    public const string RemoteFileName = "signin-remote.dat";

    private readonly Func<DateTime> _now;
    private readonly string? _path;
    private readonly string _fileName;
    private readonly ISecretProtector? _protector;
    private readonly ILogger? _log;
    private readonly Dictionary<string, List<DateTime>> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private DateTime _holdUntil = DateTime.MinValue;
    private byte[]? _written;   // the file as last written or read; anything else there is tampering

    /// <summary>The store of this PC: DPAPI on Windows, memory only elsewhere.</summary>
    public OtpAttempts(IOptions<StudioOptions> options, ILogger<OtpAttempts> log)
        : this(options.Value.DataFolder, OperatingSystem.IsWindows() ? new DpapiSecretProtector() : null, null, log) { }

    /// <param name="dataFolder">Where the file is kept; memory only when null or without a protector.</param>
    /// <param name="fileName">The file: <see cref="FileName"/> for Vision Studio, <see cref="RemoteFileName"/> for the runtime.</param>
    /// <param name="utcNow">The clock; the system's when null.</param>
    public OtpAttempts(string? dataFolder, ISecretProtector? protector, Func<DateTime>? utcNow = null, ILogger? log = null, string fileName = FileName)
    {
        _fileName = fileName;
        _now = utcNow ?? (() => DateTime.UtcNow);
        _log = log;
        if (dataFolder is null || protector is null) return;
        _path = Path.Combine(dataFolder, fileName);
        _protector = protector;
        lock (_lock) Load();
    }

    /// <summary>In memory only (tests).</summary>
    public static OtpAttempts InMemory(Func<DateTime>? utcNow = null) => new(null, null, utcNow);

    /// <summary>Why every code is held now (never a code), or null.</summary>
    public string? Hold { get; private set; }

    public bool Locked(string user)
    {
        lock (_lock)
        {
            CheckFile();
            return Held() || Recent(user).Count >= MaxFailures;
        }
    }

    /// <summary>Signs in through <see cref="OemSignIn.Decide"/> and counts the result: a code given while the user is
    /// locked is not checked (the Service and OEM roles are refused), a wrong code counts, a right one clears.</summary>
    public OemSignIn.Outcome Decide(string user, IEnumerable<string> userRoles, string? otp, string? licenceKey,
                                    string? serviceSecret, string? factorySecret, string? day = null)
    {
        lock (_lock)
        {
            var given = !string.IsNullOrWhiteSpace(otp);
            if (given) CheckFile();
            var locked = given && (Held() || Recent(user).Count >= MaxFailures);
            var o = OemSignIn.Decide(userRoles, locked ? null : otp, licenceKey, serviceSecret, factorySecret, day);
            if (locked) return o with { Locked = true };
            // a user with both roles opens one with each code: the code they know never clears the count of guesses at
            // the other (review of PR #328), so the count clears only when no role was refused
            if (o.Granted.Count > 0 && o.Refused.Count == 0) { if (_failures.Remove(user)) Save(); }
            else if (given && o.Granted.Count == 0 && o.Refused.Count > 0) { Recent(user).Add(_now()); Save(); }
            return o;
        }
    }

    private bool Held()
    {
        if (_now() < _holdUntil) return true;
        Hold = null;
        return false;
    }

    private List<DateTime> Recent(string user)
    {
        if (!_failures.TryGetValue(user, out var list)) _failures[user] = list = new();
        var cut = _now() - Window;
        list.RemoveAll(t => t <= cut);
        return list;
    }

    private sealed record Stored(DateTime HoldUntil, Dictionary<string, List<DateTime>> Failures);

    private void Load()
    {
        if (!File.Exists(_path))
        {
            HoldAll($"{_fileName} is missing (first start, or deleted)");
            return;
        }
        try
        {
            var blob = File.ReadAllBytes(_path!);
            var plain = _protector!.Unprotect(blob);
            var stored = JsonSerializer.Deserialize<Stored>(plain) ?? throw new JsonException("empty");
            foreach (var (user, times) in stored.Failures ?? []) _failures[user] = times ?? [];
            _holdUntil = stored.HoldUntil;
            if (Held()) Hold = $"{_fileName} held all codes before the restart";
            _written = blob;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Copy(_path!, _path + ".bad", overwrite: true); } catch (Exception c) when (c is IOException or UnauthorizedAccessException) { }
            HoldAll($"{_fileName} cannot be opened (edited, damaged, or from another PC)");
        }
    }

    /// <summary>Holds every code when the file is no longer what was last written (deleted, edited, put back).</summary>
    private void CheckFile()
    {
        if (_path is null) return;
        byte[]? now;
        try { now = File.Exists(_path) ? File.ReadAllBytes(_path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { now = null; }
        if (now is null || _written is null || !now.AsSpan().SequenceEqual(_written))
            HoldAll($"{_fileName} was deleted or changed while the program ran");
    }

    private void HoldAll(string why)
    {
        Hold = why;
        _holdUntil = _now() + Window;
        _log?.LogError("{Problem}: Service and OEM codes are held for {Minutes} minutes", why, (int)Window.TotalMinutes);
        Save();
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(new Stored(_holdUntil, _failures.Where(f => f.Value.Count > 0).ToDictionary()));
            var blob = _protector!.Protect(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, _path, overwrite: true);
            _written = blob;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // not on disk, so a restart would forget it: hold every code instead (no new write, which would fail too)
            Hold = $"{_fileName} cannot be written";
            _holdUntil = _now() + Window;
            _log?.LogError("{Problem}: Service and OEM codes are held for {Minutes} minutes", Hold, (int)Window.TotalMinutes);
        }
    }
}
