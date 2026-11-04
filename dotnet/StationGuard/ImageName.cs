using System.Globalization;
using System.Text.Json;

namespace StationGuard;

/// <summary>
/// The name of one camera image of a part, the same everywhere: file name (.bmp and .json), image id in
/// results.db, the plant events and GetImage (owner, 2026-10-31: "short but identical").
/// <c>L01_C2_J045_261031-104512_000123</c>: line (L + 2 digits), camera on that line (C + 1 digit), job
/// number as the PLC selects it (J + 3 digits, J000 when the job has none), local date and time
/// (yymmdd-hhmmss), and the part serial (the station's part count for the day, 6 digits). 32 characters,
/// only [A-Z0-9_-]. Both cameras' images of one part differ only in C1/C2. Everything else (product,
/// OK/NG, reasons, PLC trigger and part id, time zone, milliseconds, batch) goes in the .json beside it.
/// </summary>
public readonly record struct ImageName
{
    public const int Length = 32;

    /// <summary>Line number, 1-99.</summary>
    public int Line { get; }
    /// <summary>Camera on the line, 1-9 (camera index + 1).</summary>
    public int Camera { get; }
    /// <summary>The PLC job number, 0-999 (0: the job has none).</summary>
    public int Job { get; }
    /// <summary>Local time of the part, to the second.</summary>
    public DateTime Local { get; }
    /// <summary>The part's serial for the day, 1-999999.</summary>
    public int Serial { get; }

    public ImageName(int line, int camera, int job, DateTime local, int serial)
    {
        if (line is < 1 or > 99) throw new ArgumentOutOfRangeException(nameof(line), "The line number is 1 to 99");
        if (camera is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(camera), "The camera is 1 to 9");
        if (job is < 0 or > 999) throw new ArgumentOutOfRangeException(nameof(job), "The job number is 0 to 999");
        if (serial is < 1 or > PartSerial.Max) throw new ArgumentOutOfRangeException(nameof(serial), "The serial is 1 to 999999");
        if (local.Year is < 2000 or > 2099) throw new ArgumentOutOfRangeException(nameof(local), "The year is 2000 to 2099");
        Line = line; Camera = camera; Job = job; Serial = serial;
        Local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second, DateTimeKind.Unspecified);
    }

    /// <summary>The same part seen by another camera of the line.</summary>
    public ImageName WithCamera(int camera) => new(Line, camera, Job, Local, Serial);

    public override string ToString() =>
        $"L{Line:D2}_C{Camera:D1}_J{Job:D3}_{Local.ToString("yyMMdd-HHmmss", CultureInfo.InvariantCulture)}_{Serial:D6}";

    /// <summary>The name without the camera: the same for every camera's image of the part (finds them all).</summary>
    public string PartKey =>
        $"L{Line:D2}_J{Job:D3}_{Local.ToString("yyMMdd-HHmmss", CultureInfo.InvariantCulture)}_{Serial:D6}";

    /// <summary>The folder the image goes in: <c>&lt;root&gt;/L01/2026-10-31/J045/C1/OK</c> (or <c>NG</c>).</summary>
    public string Folder(string root, bool ok) => Path.Combine(root, $"L{Line:D2}",
        Local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), $"J{Job:D3}", $"C{Camera:D1}", ok ? "OK" : "NG");

    /// <summary>A name in this pattern, exactly (no extension); false for anything else, old forms included.</summary>
    public static bool TryParse(string? s, out ImageName name)
    {
        name = default;
        if (s is not { Length: Length }) return false;
        if (s[0] != 'L' || s[3] != '_' || s[4] != 'C' || s[6] != '_' || s[7] != 'J' || s[11] != '_' || s[18] != '-' || s[25] != '_') return false;
        if (!Digits(s, 1, 2, out var line) || !Digits(s, 5, 1, out var cam) || !Digits(s, 8, 3, out var job) || !Digits(s, 26, 6, out var serial)) return false;
        if (!DateTime.TryParseExact(s.AsSpan(12, 13), "yyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return false;
        if (line < 1 || cam < 1 || serial < 1) return false;
        name = new ImageName(line, cam, job, at, serial);
        return true;
    }

    static bool Digits(string s, int at, int n, out int v)
    {
        v = 0;
        for (var i = at; i < at + n; i++)
        {
            if (s[i] is < '0' or > '9') return false;
            v = v * 10 + (s[i] - '0');
        }
        return true;
    }

    /// <summary>
    /// Any image id or file name (without extension) the station has made, old forms included, so NG review
    /// and Teach still open images saved before 2026-10-31 (they are never renamed or moved):
    /// <list type="bullet">
    /// <item>this pattern;</item>
    /// <item>the station's old image id <c>20261030T101530123Z-000042</c> (UTC time and a counter), and its
    /// files <c>20261030T101530123Z-000042_cam1</c> (with the camera's name);</item>
    /// <item>an old image without an id, <c>101530_123_&lt;reason&gt;</c> (local time of day; the date is its day
    /// folder, given as <paramref name="day"/>).</item>
    /// </list>
    /// Anything else (a runtime's <c>12-0</c>, a hand-copied picture) comes back as <see cref="ImageNameForm.Other"/>
    /// with itself as the key.
    /// </summary>
    public static ParsedImageName ParseAny(string? s, DateTime? day = null)
    {
        s ??= "";
        if (TryParse(s, out var n))
            return new ParsedImageName(ImageNameForm.Current, n.PartKey, n, n.Local, n.Serial, $"C{n.Camera}", null);
        // 20261030T101530123Z-000042, maybe followed by _<camera>
        if (s.Length >= 26 && s[8] == 'T' && s[18] == 'Z' && s[19] == '-' && Digits(s, 20, 6, out var seq)
            && DateTime.TryParseExact(s.AsSpan(0, 18), "yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            && (s.Length == 26 || s[26] == '_'))
            return new ParsedImageName(ImageNameForm.OldStationId, s[..26], null, DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime(), seq,
                s.Length > 27 ? s[27..] : null, null);
        // 101530_123_<reason>
        if (s.Length >= 10 && s[6] == '_' && DateTime.TryParseExact(s.AsSpan(0, 10), "HHmmss_fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return new ParsedImageName(ImageNameForm.OldTimeReason, s, null, day is { } d ? d.Date + t.TimeOfDay : null, null, null,
                s.Length > 11 && s[10] == '_' ? s[11..] : null);
        return new ParsedImageName(ImageNameForm.Other, s, null, null, null, null, null);
    }

    /// <summary>The key a part's images are found by: <see cref="PartKey"/> for this pattern (any camera's name finds
    /// all the part's images), the id itself for an old one.</summary>
    public static string KeyOf(string id) => TryParse(id, out var n) ? n.PartKey : id;
}

public enum ImageNameForm { Current, OldStationId, OldTimeReason, Other }

/// <summary>What <see cref="ImageName.ParseAny"/> found. <see cref="Time"/> is local; <see cref="Camera"/> is "C2" for
/// this pattern and the camera's name for an old file; <see cref="Reason"/> only for an old image without an id.</summary>
public sealed record ParsedImageName(ImageNameForm Form, string Key, ImageName? Name, DateTime? Time, int? Serial, string? Camera, string? Reason);

/// <summary>
/// The part serial of <see cref="ImageName"/>: the station's part count for the day, 1-999999, back to 1 at local
/// midnight, kept across a restart. One count per key: the whole part ("", all cameras fired together) or one
/// camera ("C2", a camera fired on its own when the part reaches it). Next never waits for the disk: the count
/// is saved by a timer (at most half a second of parts can be lost on a power cut; the time in the name keeps
/// the names apart even then). A file that cannot be used only means the count starts again at 1.
/// </summary>
public sealed class PartSerial : IDisposable
{
    public const int Max = 999_999;

    sealed record Saved(string Day, Dictionary<string, int> Counts);

    readonly string? _file;
    readonly TimeProvider _time;
    readonly object _lock = new(), _saveLock = new();
    readonly Timer? _timer;
    Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    DateOnly _day;
    bool _dirty;

    public PartSerial(string? file, TimeProvider? time = null, TimeSpan? saveEvery = null)
    {
        _file = string.IsNullOrWhiteSpace(file) ? null : file;
        _time = time ?? TimeProvider.System;
        _day = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        Load();
        if (_file is not null)
        {
            var every = saveEvery ?? TimeSpan.FromMilliseconds(500);
            _timer = new Timer(_ => Save(), null, every, every);
        }
    }

    /// <summary>The next serial of <paramref name="key"/> and the local time it was taken at (seconds and below kept).</summary>
    public (int Serial, DateTimeOffset Local) Next(string key = "")
    {
        var now = _time.GetLocalNow();
        lock (_lock)
        {
            var day = DateOnly.FromDateTime(now.DateTime);
            if (day != _day) { _day = day; _counts.Clear(); }
            var n = _counts.GetValueOrDefault(key) % Max + 1;   // past 999999 in one day: 1 again (the time keeps names apart)
            _counts[key] = n;
            _dirty = true;
            return (n, now);
        }
    }

    void Load()
    {
        if (_file is null) return;
        try
        {
            if (!File.Exists(_file)) return;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file));
            if (s?.Counts is null || !DateOnly.TryParseExact(s.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d != _day) return;
            foreach (var (k, v) in s.Counts) if (v is > 0 and <= Max) _counts[k] = v;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
    }

    /// <summary>Writes the counts now when they changed (the timer does this on its own).</summary>
    public void Save()
    {
        if (_file is null) return;
        lock (_saveLock) SaveLocked();
    }

    void SaveLocked()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(new Saved(_day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), new Dictionary<string, int>(_counts)));
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file!))!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file!, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { lock (_lock) _dirty = true; }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        Save();
    }
}
