using System.Globalization;
using System.Text.Json;
using StationGuard;
using VisionStudio.Remote.V1;
using VisionStudio.Services;

namespace VisionSystem.Runtime;

/// <summary>
/// The learning session's setting (owner, 2026-11-04: "setting to enable this feature or not can be given in OEM,
/// Support, Supervisor login"): on or off, and the line's idle time before the "pictures to sort" notice. Kept in
/// data/learning-settings.json with who changed it and when. A missing or unreadable file is the default (on, 5
/// minutes). Off stops keeping good pictures and hides the sorting; the supervisor's NG review and Learn from
/// reviews stay as they are.
/// </summary>
public sealed class LearningSettingsStore
{
    public const int DefaultIdleMinutes = 5, MinIdleMinutes = 2, MaxIdleMinutes = 60;

    sealed record Saved(bool Enabled, int IdleMinutes, string ChangedBy, DateTimeOffset? ChangedAt);

    readonly string _file;
    readonly TimeProvider _time;
    readonly object _lock = new();
    Saved _current;

    public LearningSettingsStore(string dataFolder, TimeProvider? time = null)
    {
        _file = Path.Combine(dataFolder, "learning-settings.json");
        _time = time ?? TimeProvider.System;
        _current = Load(_file);
    }

    static Saved Load(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) is { } s)
                return s with { IdleMinutes = Math.Clamp(s.IdleMinutes, MinIdleMinutes, MaxIdleMinutes) };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }
        return new Saved(true, DefaultIdleMinutes, "", null);
    }

    public bool Enabled { get { lock (_lock) return _current.Enabled; } }
    public int IdleMinutes { get { lock (_lock) return _current.IdleMinutes; } }

    public LearningSettings Get()
    {
        lock (_lock)
        {
            var s = new LearningSettings { Enabled = _current.Enabled, IdleMinutes = (uint)_current.IdleMinutes, ChangedBy = _current.ChangedBy };
            if (_current.ChangedAt is { } at) s.ChangedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(at);
            return s;
        }
    }

    /// <summary>Saves the setting. Null when saved, else why not (an idle time outside 2..60 minutes).</summary>
    public string? Set(bool enabled, int idleMinutes, string user)
    {
        if (idleMinutes is < MinIdleMinutes or > MaxIdleMinutes)
            return $"The idle time must be {MinIdleMinutes} to {MaxIdleMinutes} minutes.";
        lock (_lock)
        {
            var next = new Saved(enabled, idleMinutes, user, _time.GetUtcNow());
            var temp = _file + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(next));
            File.Move(temp, _file, overwrite: true);
            _current = next;
        }
        return null;
    }
}

/// <summary>A saved picture as the sorting sees it: its file, job and camera, and what the software decided.</summary>
public sealed record SavedPicture(string Id, string File, string Job, int Camera, bool Ok, string[] Faults, DateTimeOffset Time, bool NearLimit);

/// <summary>
/// The pictures of a job waiting to be sorted (owner, 2026-11-04): the saved bad pictures (the NG image store) and
/// the good pictures kept for learning (<see cref="OkSampler"/>), less those already sorted or reviewed. Read from
/// the stores' files and their .json, so the sorting can come hours after the parts and survives a restart. A
/// picture's .json is read once (the files never change).
/// </summary>
public sealed class SortQueue(NgImageStore ng, OkSampler? ok, ReviewStore reviews, TimeProvider? time = null)
{
    /// <summary>The fault of a bad picture whose .json holds no faults (saved before 2026-11-04, or a part the PLC got
    /// as NG): its cause is not known, so it is treated as clearly bad and never taught as good.</summary>
    public const string UnknownCause = "explain.review.unknown";

    /// <summary>At most this many pictures are listed at once, newest first.</summary>
    public const int MaxListed = 500;
    /// <summary>The count on the Run screen is worked out at most this often.</summary>
    public static readonly TimeSpan CountEvery = TimeSpan.FromSeconds(30);

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly object _lock = new();
    readonly Dictionary<string, SavedPicture?> _read = new(StringComparer.Ordinal);
    (string Job, DateTimeOffset At, Counts Count) _count = ("", DateTimeOffset.MinValue, new Counts(0, 0, 0));
    int _counting;

    IEnumerable<string> AllPictures() => ng.Pictures().Concat(ok?.Store.Pictures() ?? []);

    /// <summary>Reads a picture's .json: null when it is not one of a part (no image name, no product).</summary>
    public static SavedPicture? Read(string bmp)
    {
        try
        {
            var id = Path.GetFileNameWithoutExtension(bmp);
            if (!ImageName.TryParse(id, out var name)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(bmp, ".json")));
            var r = doc.RootElement;
            var job = r.TryGetProperty("product", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(job)) return null;
            var isOk = r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            string[] faults = r.TryGetProperty("faults", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                : [];
            // a bad picture without its faults: only the failed tools' names are known, not why they failed
            if (!isOk && faults.Length == 0)
                faults = (r.TryGetProperty("reasons", out var rs) && rs.ValueKind == JsonValueKind.Array
                    ? rs.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : []).DefaultIfEmpty("").Select(t => $"{t}|{UnknownCause}").ToArray();
            var at = r.TryGetProperty("time", out var t) && t.TryGetDateTimeOffset(out var dt) ? dt
                : new DateTimeOffset(name.Local, TimeZoneInfo.Local.GetUtcOffset(name.Local));
            var near = r.TryGetProperty("near_limit", out var n) && n.ValueKind == JsonValueKind.True;
            return new SavedPicture(id, bmp, job, name.Camera - 1, isOk, faults, at, near);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    SavedPicture? Cached(string bmp)
    {
        lock (_lock) if (_read.TryGetValue(bmp, out var s)) return s;
        var p = Read(bmp);
        lock (_lock)
        {
            if (_read.Count > 50_000) _read.Clear();
            _read[bmp] = p;
        }
        return p;
    }

    /// <summary>A saved picture by its image name, from either store; null when it is not kept.</summary>
    public SavedPicture? Find(string imageId)
    {
        foreach (var store in ok is null ? [ng] : new[] { ng, ok.Store })
        {
            var f = store.Find(imageId).Files.FirstOrDefault(x => Path.GetFileNameWithoutExtension(x.Path) == imageId);
            if (f is not null && File.Exists(f.Path) && Read(f.Path) is { } p) return p;
        }
        return null;
    }

    /// <summary>The pictures of a job not sorted yet (camera -1: every camera), newest first, and how many in all.</summary>
    public (IReadOnlyList<SavedPicture> Items, int Total) Unsorted(string job, int camera)
    {
        var list = AllUnsorted(job, camera);
        return (list.Take(MaxListed).ToList(), list.Count);
    }

    List<SavedPicture> AllUnsorted(string job, int camera)
    {
        var sorted = new Dictionary<int, HashSet<string>>();
        var list = new List<SavedPicture>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bmp in AllPictures())
        {
            if (Cached(bmp) is not { } p || p.Job != job || (camera >= 0 && p.Camera != camera)) continue;
            // the NG image first: a part kept in both stores (a held part the PLC rejected) is listed once, as bad
            if (!seen.Add(p.Id)) continue;
            if (!sorted.TryGetValue(p.Camera, out var done)) sorted[p.Camera] = done = reviews.Decided(job, p.Camera);
            if (!done.Contains(p.Id)) list.Add(p);
        }
        list.Sort((a, b) => b.Time.CompareTo(a.Time));
        return list;
    }

    /// <summary>What waits in a job (AS-04g, AS-04i): every picture not sorted, the bad ones among them (no review yet), and
    /// how many of those an operator marked "Was OK".</summary>
    public sealed record Counts(int All, int Bad, int Flagged);

    /// <summary>The counts worked out now, from the files.</summary>
    public Counts Count(string job)
    {
        var list = AllUnsorted(job, -1);
        var flags = reviews.Flags(job);
        var bad = list.Where(p => !p.Ok).ToList();
        return new Counts(list.Count, bad.Count, bad.Count(p => flags.ContainsKey(p.Id)));
    }

    /// <summary>How many pictures of the job wait (for the Run screen): the last count, worked out again in the
    /// background when older than <see cref="CountEvery"/>, so the status never waits for the disk.</summary>
    public int Waiting(string job) => WaitingCounts(job).All;

    /// <summary>The last counts of a job (see <see cref="Waiting"/>): never waits for the disk.</summary>
    public Counts WaitingCounts(string job)
    {
        (string Job, DateTimeOffset At, Counts Count) c;
        lock (_lock) c = _count;
        var now = _time.GetUtcNow();
        if ((c.Job != job || now - c.At > CountEvery) && Interlocked.Exchange(ref _counting, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var n = Count(job);
                    lock (_lock) _count = (job, _time.GetUtcNow(), n);
                }
                catch (Exception) { /* a count is advice only */ }
                finally { Interlocked.Exchange(ref _counting, 0); }
            });
        }
        return c.Job == job ? c.Count : new Counts(0, 0, 0);
    }

    public static SortItem Item(SavedPicture p, string cameraName, ReviewStore.Flag? flag = null)
    {
        var i = new SortItem
        {
            ImageId = p.Id, Camera = p.Camera, CameraName = cameraName, Ok = p.Ok, NearLimit = p.NearLimit,
            Time = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(p.Time),
        };
        i.Faults.AddRange(ReviewDesk.ClearlyBad(p.Faults).Words);
        if (flag is not null && !p.Ok)
        {
            i.FlaggedOkBy = flag.User;
            i.FlaggedOkAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(flag.At);
        }
        return i;
    }
}
