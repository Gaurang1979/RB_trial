using System.Reflection;
using System.Text.Json;
using BarcodeReading.Protection;
using Microsoft.Extensions.Options;
using VisionCore;
using VisionCore.Licensing;

namespace VisionStudio.Services;

/// <summary>Licence settings and the last verified answer, kept in data/licence.json.</summary>
public sealed class LicenceFile
{
    public string ServerUrl { get; set; } = "";
    public string LicenceKey { get; set; } = "";
    /// <summary>Serials the software cannot read itself (PLC, printer ...), typed from the nameplate.</summary>
    public List<DeviceReport> ManualDevices { get; set; } = new();
    public SignedLicence? Last { get; set; }
    public DateTime? LastGoodUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public bool LastAttemptFailed { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Checks the licence with the licence server at start and then periodically (interval and offline
/// grace set by the server), and tells the rest of the software what is licensed: production runs,
/// parent tools, and which devices of this installation are licensed (unlicensed ones are listed).
/// </summary>
public sealed class LicenceService : BackgroundService
{
    private readonly string _path, _dataFolder;
    private readonly string? _publicKey;
    private readonly CameraDrivers _cameras;
    private readonly LicenceClient _client;
    private readonly ILogger<LicenceService> _log;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private LicenceFile _file;
    private LicencePayload? _payload;
    private readonly AutoResetEvent _now = new(false);
    private readonly InstallationKeys _keys;
    private readonly LicenceContentKeySource _contentKeys;

    public LicenceService(IOptions<StudioOptions> opt, CameraDrivers cameras, ILogger<LicenceService> log, IWebHostEnvironment env)
    {
        _dataFolder = opt.Value.DataFolder;
        _path = Path.Combine(_dataFolder, "licence.json");
        _cameras = cameras;
        _log = log;
        _client = new LicenceClient(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        // the public key ships with the software (licence-public.pem next to the program)
        var keyPath = Path.Combine(AppContext.BaseDirectory, "licence-public.pem");
        _publicKey = File.Exists(keyPath) ? File.ReadAllText(keyPath) : null;
        _file = File.Exists(_path) ? JsonSerializer.Deserialize<LicenceFile>(File.ReadAllText(_path)) ?? new() : new();
        _payload = _publicKey is null ? null : _file.Last?.Verify(_publicKey);
        Installation = VisionCore.Licensing.Installation.Id(_dataFolder);
        Required = opt.Value.LicenceRequired;

        // Content-key machinery for at-rest encryption (LICENSING.md 7.4): this PC's key pair, and the
        // source that holds the unwrapped content key only while the licence is active. Install it so the
        // model/product loaders decrypt through it. If no licence is required (dev), nothing is installed and
        // artifacts stay plaintext.
        _keys = InstallationKeys.Load(_dataFolder);
        _contentKeys = new LicenceContentKeySource(() => Licensed);
        if (Required) ContentProtection.KeySource = _contentKeys;
        if (_payload is not null && Status.Licensed) _contentKeys.SetFromLicence(_payload.ContentKey, _keys);
    }

    /// <summary>This installation's public key (PEM), sent to the licence server so it can wrap the content key.</summary>
    public string InstallationPublicKeyPem => _keys.PublicKeyPem;

    public string Installation { get; }
    /// <summary>False on development and demo PCs (appsettings Studio:LicenceRequired).</summary>
    public bool Required { get; }
    public LicenceFile Settings { get { lock (_lock) return _file; } }
    public event Action? Changed;

    public LicenceStatus Status
    {
        get
        {
            lock (_lock)
            {
                if (_publicKey is null) return new(LicenceState.Invalid, null, null, null, "licence-public.pem is missing from the program folder.");
                if (_file.Last is not null && _payload is null) return new(LicenceState.Invalid, null, _file.LastGoodUtc, null, "The stored licence answer is not signed by the licence server.");
                return LicenceStatus.Evaluate(_payload, _file.LastGoodUtc, _file.LastAttemptUtc, _file.LastAttemptFailed, DateTime.UtcNow);
            }
        }
    }

    /// <summary>Production may run (cameras, PLC). Always true when no licence is required on this PC.</summary>
    public bool Licensed => !Required || Status.Licensed;

    public bool ToolLicensed(string tool) => !Required || Status.ToolLicensed(tool);

    /// <summary>A device of this installation is licensed (listed by the server as OK).</summary>
    public bool DeviceLicensed(string serial) => !Required || Status.Device(serial)?.Ok == true;

    /// <summary>Devices found on this PC (or entered) that the server did not confirm, with the reason.</summary>
    public List<(DeviceReport Device, string Reason)> Unlicensed()
    {
        var st = Status;
        var list = new List<(DeviceReport, string)>();
        foreach (var d in Devices())
        {
            var v = st.Device(d.Serial);
            if (v is null) list.Add((d, st.Payload is null ? "not checked yet" : "not in the last licence check (new or changed part)"));
            else if (!v.Ok) list.Add((d, v.Reason ?? "not licensed"));
        }
        return list;
    }

    private (DateTime At, List<(DeviceReport Device, string Reason)> List) _cache = (DateTime.MinValue, new());

    /// <summary><see cref="Unlicensed"/>, worked out at most every 30 s (for screens that refresh often).</summary>
    public List<(DeviceReport Device, string Reason)> UnlicensedCached()
    {
        lock (_lock) if (DateTime.UtcNow - _cache.At < TimeSpan.FromSeconds(30)) return _cache.List;
        var list = Required ? Unlicensed() : new();
        lock (_lock) _cache = (DateTime.UtcNow, list);
        return list;
    }

    /// <summary>Cameras found on the network / USB plus the serials entered by hand.</summary>
    public List<DeviceReport> Devices()
    {
        var list = new List<DeviceReport>();
        try { list.AddRange(_cameras.List().Select(c => new DeviceReport("camera", c.Serial))); } catch { }
        lock (_lock) list.AddRange(_file.ManualDevices.Where(d => d.Serial.Length > 0));
        return list.DistinctBy(d => (d.Kind, d.Serial.ToUpperInvariant())).ToList();
    }

    public void Save(string serverUrl, string licenceKey, List<DeviceReport> manual)
    {
        lock (_lock)
        {
            _file.ServerUrl = serverUrl.Trim();
            _file.LicenceKey = licenceKey.Trim();
            _file.ManualDevices = manual.Where(d => d.Serial.Trim().Length > 0).Select(d => d with { Serial = d.Serial.Trim() }).ToList();
            Write();
        }
        _now.Set();
    }

    public void CheckSoon() => _now.Set();

    private void Write() => File.WriteAllText(_path, JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>One check with the server now. Returns the error text, or null when it answered.</summary>
    public async Task<string?> CheckAsync(CancellationToken token = default)
    {
        await _checking.WaitAsync(token);
        try
        {
            LicenceFile f;
            lock (_lock) f = _file;
            if (string.IsNullOrWhiteSpace(f.ServerUrl) || string.IsNullOrWhiteSpace(f.LicenceKey)) return "Enter the licence server and the licence key.";
            if (_publicKey is null) return "licence-public.pem is missing from the program folder.";
            var nonce = VisionCore.Licensing.Installation.NewNonce();
            string? error = null;
            try
            {
                var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0";
                var signed = await _client.VerifyAsync(f.ServerUrl, f.LicenceKey, Installation, version, Devices(), nonce, token, _keys.PublicKeyPem);
                var p = signed.Verify(_publicKey);
                if (p is null) error = "The answer is not signed by the licence server.";
                else if (p.Nonce != nonce) error = "The answer does not belong to this request.";
                else if (p.Installation.Length > 0 && p.Installation != Installation) error = "The answer is for another installation.";
                else
                {
                    lock (_lock)
                    {
                        _file.Last = signed;
                        _file.LastGoodUtc = DateTime.UtcNow;
                        _payload = p;
                    }
                    // Unwrap and hold the content key for decryption while this answer is active; an empty or
                    // unusable key clears it, so encrypted artifacts fail closed (NG) rather than reading wrong.
                    _contentKeys.SetFromLicence(p.ContentKey, _keys);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { error = "Licence server not reached: " + e.Message; }
            lock (_lock)
            {
                _file.LastAttemptUtc = DateTime.UtcNow;
                _file.LastAttemptFailed = error is not null;
                _file.LastError = error;
                Write();
            }
            if (error is not null) _log.LogWarning("Licence check: {Error}", error);
            lock (_lock) _cache = (DateTime.MinValue, new());
            Changed?.Invoke();
            return error;
        }
        finally { _checking.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), token);
        while (!token.IsCancellationRequested)
        {
            await CheckAsync(token);
            // next check: the server's interval after a good check, hourly retries after a failure
            var st = Status;
            var wait = Settings.LastAttemptFailed || st.NextCheckUtc is null
                ? TimeSpan.FromHours(1)
                : st.NextCheckUtc.Value - DateTime.UtcNow;
            if (wait < TimeSpan.FromMinutes(1)) wait = TimeSpan.FromMinutes(1);
            await Task.Run(() => WaitHandle.WaitAny(new[] { _now, token.WaitHandle }, wait), token);
        }
    }
}
