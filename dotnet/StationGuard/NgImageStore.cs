using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace StationGuard;

/// <summary>Which part images are kept on disk. Counts are always kept, whatever the mode.</summary>
public enum SaveMode { NgOnly, All, EveryNthNg, None }

public sealed record NgImageOptions
{
    public required string Folder { get; init; }
    /// <summary>
    /// Images go under a folder per day, then per job (<c>&lt;Folder&gt;/yyyy-MM-dd/&lt;job&gt;/</c>). False: one folder
    /// per job only (<c>&lt;Folder&gt;/&lt;job&gt;/</c>), the layout the retraining agent reads.
    /// </summary>
    public bool DateFolders { get; init; } = true;
    public SaveMode Mode { get; init; } = SaveMode.NgOnly;
    /// <summary>For <see cref="SaveMode.EveryNthNg"/>: keep one NG image in this many.</summary>
    public int EveryNth { get; init; } = 10;
    /// <summary>Images waiting for the writer may hold at most this much memory; more are counted, not kept.</summary>
    public long MaxQueueBytes { get; init; } = 64L << 20;
    public int MaxFiles { get; init; } = 5000;
    public long MaxBytes { get; init; } = 5L << 30;
    /// <summary>Under High pressure: at most this many images per NG reason per hour, so the variety survives.</summary>
    public int PerReasonPerHourUnderPressure { get; init; } = 20;
    /// <summary>The disk clean-up step deletes the oldest images until this much of the drive is free.</summary>
    public double TargetFreeDiskPct { get; init; } = 15;
    /// <summary>How often the counts are written to counts.json.</summary>
    public TimeSpan SaveCountsEvery { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Why an image was not kept.</summary>
public enum SkipReason { Policy, Pressure, QueueFull, Disk, WriteError }

public sealed record ReasonCount(long Ng, long Saved);

/// <summary>The counts of one batch (or since the store was made).</summary>
public sealed record NgCounts(
    string Batch, DateTimeOffset Since, long Parts, long Ok, long Ng, long Saved,
    IReadOnlyDictionary<SkipReason, long> Skipped, IReadOnlyDictionary<string, ReasonCount> PerReason,
    long QueuedBytes, int FilesKept, long BytesKept);

/// <summary>Where a part's images are, by image id: saved, still waiting for the writer, or not here.</summary>
public enum NgImageState { Found, Queued, Missing }

/// <summary>One saved camera image of a part (an 8-bit grey BMP).</summary>
public sealed record NgImageFile(string Camera, string Path);

/// <summary>
/// The answer of <see cref="NgImageStore.Find"/>. <see cref="NgImageState.Queued"/>: at least one image of the
/// part is still waiting for the writer, so ask again soon. <see cref="NgImageState.Missing"/>: nothing is saved
/// and nothing is waiting (the folder limit removed it, or it was never kept). <see cref="Files"/> lists what is
/// saved now, whatever the state.
/// </summary>
public sealed record NgImageLookup(NgImageState State, IReadOnlyList<NgImageFile> Files);

/// <summary>
/// Keeps NG images with their counts, without ever slowing inspection.
/// The station counts each part once (<see cref="CountPart"/>) and offers the
/// images it may keep (<see cref="KeepImage"/>); a one-camera station can do
/// both with <see cref="Record"/>. Both run on the inspection thread: they never
/// throw and never wait for the disk. An image is copied only when it will be
/// kept (by the mode, the guard's pressure and the free queue memory), into
/// a queue a writer thread of below-normal priority empties. The writer saves
/// an 8-bit BMP and a .json with the reasons, keeps the folder under its
/// limits (oldest first) and writes counts.json now and then. Under High
/// pressure only a few images per reason per hour are kept; under Critical
/// none are, but every part is still counted. A part's images can be given an
/// image id (the station makes one per part): the files are then named
/// <c>&lt;image_id&gt;_&lt;camera&gt;.bmp</c>, the .json holds both, and
/// <see cref="Find"/> returns the part's files, also after a restart (the folder is indexed at start).
/// An image given its <see cref="ImageName"/> (owner, 2026-10-31) is saved as
/// <c>&lt;Folder&gt;/L01/2026-10-31/J045/C1/NG/L01_C1_J045_261031-104512_000123.bmp</c> instead, and any camera's
/// name of the part finds all its images. Old files are never moved or renamed; they are still found by their id.
/// </summary>
public sealed class NgImageStore : IDisposable
{
    sealed record Job(DateTime At, string Batch, string JobName, IReadOnlyList<string> Reasons, int Width, int Height, byte[] Pixels, string? ImageId, string? Camera,
        IReadOnlyDictionary<string, object?>? Meta, ImageName? Name, bool Ok)
    {
        // The key the part's images are found by (Find): one for all its cameras.
        public string? Key => Name is { } n ? n.PartKey : ImageId is null ? null : ImageName.KeyOf(ImageId);
    }

    readonly NgImageOptions _opt;
    readonly Func<Pressure> _pressure;
    readonly TimeProvider _time;
    readonly BlockingCollection<Job> _queue = new();
    readonly object _lock = new();
    readonly LinkedList<(string Path, long Bytes, string? ImageId)> _files = new();
    // Saved files by image id, and how many images of an id still wait for the writer; both under _lock.
    readonly Dictionary<string, List<NgImageFile>> _byId = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);
    readonly Dictionary<(string Reason, long Hour), int> _pressureSaved = [];
    readonly Thread _writer;
    readonly bool _disabled;
    long _queued, _bytesKept;
    string _batch = "";
    DateTimeOffset _since;
    long _parts, _ok, _ng, _saved, _offered;
    readonly Dictionary<SkipReason, long> _skipped = [];
    readonly Dictionary<string, (long Ng, long Saved)> _perReason = [];
    long _lastCountsSave;
    volatile bool _countsDirty;

    public NgImageStore(NgImageOptions options, Func<Pressure>? pressure = null, TimeProvider? time = null)
    {
        _opt = options;
        _pressure = pressure ?? (() => Pressure.Normal);
        _time = time ?? TimeProvider.System;
        _since = _time.GetUtcNow();
        // A folder that cannot be used (a missing drive, no rights) must not stop the station:
        // the store then counts parts and keeps no images.
        try
        {
            Directory.CreateDirectory(_opt.Folder);
            IndexExisting();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _disabled = true;
        }
        LoadCounts();
        _writer = new Thread(WriteLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "ng-image-writer" };
        _writer.Start();
    }

    /// <summary>False when the image folder could not be used; counts still work.</summary>
    public bool ImagesEnabled => !_disabled;

    /// <summary>Whether an NG image offered now could be kept at all (images on, a mode that keeps NG images,
    /// not Critical pressure). A caller that would have to copy an image to offer it later checks this first.</summary>
    public bool KeepsNgImages => !_disabled && _opt.Mode != SaveMode.None && _pressure() != Pressure.Critical;

    /// <summary>Counts one inspected part (all its cameras together): OK, or NG with its reasons.</summary>
    public void CountPart(bool ok, IReadOnlyList<string> reasons)
    {
        lock (_lock)
        {
            _parts++;
            if (ok) _ok++;
            else
            {
                _ng++;
                foreach (var r in reasons.Count > 0 ? reasons.Distinct() : ["unknown"])
                    _perReason[r] = _perReason.TryGetValue(r, out var c) ? (c.Ng + 1, c.Saved) : (1, 0);
            }
            _countsDirty = true;
        }
    }

    /// <summary>
    /// Keeps one camera's image of a part when the rules allow. Never throws and never waits
    /// for the disk. Returns whether the image will be saved. With an <paramref name="imageId"/> (one per part,
    /// the same for all its cameras) the file is named after it and the camera, and <see cref="Find"/> finds it.
    /// <paramref name="meta"/> adds fields to the image's .json (the store's own fields win on a clash).
    /// With a <paramref name="name"/> the file and its folder follow the naming pattern (<see cref="ImageName"/>), the
    /// .json's <c>image_id</c> is that name (unless an <paramref name="imageId"/> is given), and any camera's name of
    /// the part finds it.
    /// </summary>
    public bool KeepImage(string job, bool ok, IReadOnlyList<string> reasons, ReadOnlySpan<byte> gray, int width, int height, int stride = 0,
        string? imageId = null, string? camera = null, IReadOnlyDictionary<string, object?>? meta = null, ImageName? name = null)
    {
        if (string.IsNullOrEmpty(imageId)) imageId = name?.ToString();
        if (stride <= 0) stride = width;
        var len = (long)width * height;
        // Checked before anything is counted or reserved, so a bad frame costs nothing later.
        if (width <= 0 || height <= 0 || stride < width || gray.Length < (long)stride * (height - 1) + width || len > int.MaxValue)
        {
            Skip(SkipReason.WriteError);
            return false;
        }
        var reasonList = reasons.Count > 0 ? reasons.ToArray() : ["unknown"];
        string batch;
        lock (_lock)
        {
            if (Decide(ok, reasonList[0], len) is { } s) { _skipped[s] = _skipped.GetValueOrDefault(s) + 1; _countsDirty = true; return false; }
            _queued += len;
            if (KeyOf(imageId, name) is { } key) _pending[key] = _pending.GetValueOrDefault(key) + 1;
            batch = _batch;
        }
        try
        {
            // A plain array, not a pooled one: when it is written or dropped, the memory really goes back.
            var buf = GC.AllocateUninitializedArray<byte>((int)len);
            if (stride == width) gray[..(int)len].CopyTo(buf);
            else for (var y = 0; y < height; y++) gray.Slice(y * stride, width).CopyTo(buf.AsSpan(y * width));
            _queue.Add(new Job(_time.GetLocalNow().DateTime, batch, job, reasonList, width, height, buf, imageId, camera, meta, name, ok));
            return true;
        }
        catch (Exception)
        {
            lock (_lock) { _queued -= len; Unpend(KeyOf(imageId, name)); }
            Skip(SkipReason.WriteError);
            return false;
        }
    }

    /// <summary><see cref="CountPart"/> and <see cref="KeepImage"/> for a one-camera part.</summary>
    public bool Record(string job, bool ok, IReadOnlyList<string> reasons, ReadOnlySpan<byte> gray, int width, int height, int stride = 0,
        string? imageId = null, string? camera = null, IReadOnlyDictionary<string, object?>? meta = null, ImageName? name = null)
    {
        CountPart(ok, reasons);
        return KeepImage(job, ok, reasons, gray, width, height, stride, imageId, camera, meta, name);
    }

    static string? KeyOf(string? imageId, ImageName? name) => name is { } n ? n.PartKey : imageId is null ? null : ImageName.KeyOf(imageId);

    /// <summary>
    /// The saved images of the part with this image id. Cheap (a dictionary lookup), safe from any thread. A file
    /// can still vanish before the caller reads it (the folder limit); the caller treats that as missing.
    /// For a name in the pattern (<see cref="ImageName"/>), any camera's name of the part finds all its images.
    /// </summary>
    public NgImageLookup Find(string imageId)
    {
        imageId = ImageName.KeyOf(imageId);
        lock (_lock)
        {
            var files = _byId.TryGetValue(imageId, out var l) ? l.ToArray() : [];
            return new NgImageLookup(_pending.ContainsKey(imageId) ? NgImageState.Queued : files.Length > 0 ? NgImageState.Found : NgImageState.Missing, files);
        }
    }

    /// <summary>The saved images, newest first (a snapshot; a file can vanish before the caller reads it).</summary>
    public IReadOnlyList<string> Pictures()
    {
        lock (_lock) return _files.Reverse().Select(f => f.Path).ToArray();
    }

    // Under _lock.
    void Unpend(string? imageId)
    {
        if (imageId is null || !_pending.TryGetValue(imageId, out var n)) return;
        if (n <= 1) _pending.Remove(imageId); else _pending[imageId] = n - 1;
    }

    // Under _lock.
    void Index(string? imageId, string? camera, string path)
    {
        if (imageId is null) return;
        if (!_byId.TryGetValue(imageId, out var l)) _byId[imageId] = l = [];
        l.Add(new NgImageFile(camera ?? "cam", path));
    }

    // Under _lock.
    void Unindex(string? imageId, string path)
    {
        if (imageId is null || !_byId.TryGetValue(imageId, out var l)) return;
        l.RemoveAll(f => f.Path == path);
        if (l.Count == 0) _byId.Remove(imageId);
    }

    void Skip(SkipReason s)
    {
        lock (_lock) { _skipped[s] = _skipped.GetValueOrDefault(s) + 1; _countsDirty = true; }
    }

    // Under _lock. The mode decides first, so a part the mode would never keep is "policy" at any pressure.
    SkipReason? Decide(bool ok, string reason, long bytes)
    {
        var wanted = _opt.Mode switch
        {
            SaveMode.All => true,
            SaveMode.NgOnly => !ok,
            SaveMode.EveryNthNg => !ok && _offered++ % Math.Max(1, _opt.EveryNth) == 0,
            _ => false,
        };
        if (!wanted) return SkipReason.Policy;
        if (_disabled) return SkipReason.Disk;
        var pressure = _pressure();
        if (pressure == Pressure.Critical) return SkipReason.Pressure;
        if (pressure == Pressure.High)
        {
            if (ok) return SkipReason.Pressure;
            var hour = _time.GetUtcNow().ToUnixTimeSeconds() / 3600;
            var n = _pressureSaved.GetValueOrDefault((reason, hour));
            if (n >= _opt.PerReasonPerHourUnderPressure) return SkipReason.Pressure;
            if (_queued + bytes > _opt.MaxQueueBytes) return SkipReason.QueueFull;
            if (_pressureSaved.Count > 1000) _pressureSaved.Clear();
            _pressureSaved[(reason, hour)] = n + 1;
            return null;
        }
        return _queued + bytes > _opt.MaxQueueBytes ? SkipReason.QueueFull : null;
    }

    /// <summary>Starts counting a new batch; the old batch's counts are written first. Images already kept stay.</summary>
    public void StartBatch(string batch)
    {
        SaveCounts(force: true);
        lock (_lock)
        {
            _batch = batch;
            _since = _time.GetUtcNow();
            _parts = _ok = _ng = _saved = _offered = 0;
            _skipped.Clear();
            _perReason.Clear();
            _countsDirty = true;
        }
        SaveCounts(force: true);
    }

    public NgCounts Counts
    {
        get
        {
            lock (_lock)
                return new NgCounts(_batch, _since, _parts, _ok, _ng, _saved, new Dictionary<SkipReason, long>(_skipped),
                    _perReason.ToDictionary(kv => kv.Key, kv => new ReasonCount(kv.Value.Ng, kv.Value.Saved)),
                    _queued, _files.Count, _bytesKept);
        }
    }

    /// <summary>
    /// A memory clean-up step for the guard: drops images still waiting to be written
    /// (counted as skipped for pressure), whose memory the GC can then take back.
    /// </summary>
    public IReliever DropQueuedReliever() => new Reliever("ng-images-drop-queued", Pressure.High, TimeSpan.FromMilliseconds(5), () =>
    {
        long freed = 0;
        while (_queue.TryTake(out var j))
        {
            freed += j.Pixels.Length;
            lock (_lock)
            {
                _queued -= j.Pixels.Length;
                Unpend(j.Key);
                _skipped[SkipReason.Pressure] = _skipped.GetValueOrDefault(SkipReason.Pressure) + 1;
            }
        }
        return freed;
    });

    /// <summary>
    /// A disk clean-up step for the guard (only when the disk is short, never for memory):
    /// deletes the oldest kept images until <see cref="NgImageOptions.TargetFreeDiskPct"/> of the drive is free.
    /// </summary>
    public IReliever TrimDiskReliever() => new Reliever("ng-images-trim-disk", Pressure.High, TimeSpan.FromMilliseconds(50), () =>
    {
        long freed = 0;
        DriveInfo drive;
        try { drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_opt.Folder))!); }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return 0; }
        for (var i = 0; i < 100_000; i++)
        {
            double freePct;
            try { freePct = 100.0 * drive.AvailableFreeSpace / Math.Max(1, drive.TotalSize); }
            catch (IOException) { break; }
            if (freePct >= _opt.TargetFreeDiskPct) break;
            var n = DeleteOldest();
            if (n <= 0) break;
            freed += n;
        }
        return freed;
    }, Relieves.Disk);

    void WriteLoop()
    {
        while (true)
        {
            Job? j;
            try
            {
                if (!_queue.TryTake(out j, TimeSpan.FromSeconds(1)))
                {
                    if (_queue.IsAddingCompleted) return;
                    SaveCounts(force: false);
                    continue;
                }
            }
            catch (Exception) { return; }
            try { Write(j); }
            catch (Exception) { Skip(SkipReason.WriteError); }
            finally { lock (_lock) { _queued -= j.Pixels.Length; Unpend(j.Key); } }
            try
            {
                Trim(_opt.MaxFiles, _opt.MaxBytes);
                SaveCounts(force: false);
            }
            catch (Exception) { }
        }
    }

    void Write(Job j)
    {
        var dir = j.Name is { } n ? n.Folder(_opt.Folder, j.Ok)
            : _opt.DateFolders
            ? Path.Combine(_opt.Folder, j.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Safe(j.JobName))
            : Path.Combine(_opt.Folder, Safe(j.JobName));
        string? bmp = null, meta = null;
        try
        {
            Directory.CreateDirectory(dir);
            var name = j.Name is { } nm ? nm.ToString()
                : j.ImageId is null
                ? $"{j.At.ToString("HHmmss_fff", CultureInfo.InvariantCulture)}_{Safe(j.Reasons[0])}"
                : $"{SafeId(j.ImageId)}_{SafeId(j.Camera ?? "cam")}";
            bmp = Path.Combine(dir, name + ".bmp");
            for (var i = 1; File.Exists(bmp); i++) bmp = Path.Combine(dir, $"{name}_{i}.bmp");
            var bytes = BmpGray.Write(bmp, j.Pixels, j.Width, j.Height);
            meta = Path.ChangeExtension(bmp, ".json");
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (j.Meta is not null) foreach (var kv in j.Meta) fields[kv.Key] = kv.Value;
            fields["at"] = j.At; fields["batch"] = j.Batch; fields["job"] = j.JobName; fields["reasons"] = j.Reasons;
            fields["width"] = j.Width; fields["height"] = j.Height; fields["image_id"] = j.ImageId; fields["camera"] = j.Camera;
            fields["ok"] = j.Ok;
            File.WriteAllText(meta, JsonSerializer.Serialize(fields));
            bytes += new FileInfo(meta).Length;
            lock (_lock)
            {
                _files.AddLast((bmp, bytes, j.Key));
                _bytesKept += bytes;
                Index(j.Key, j.Camera, bmp);
                // An image queued before a new batch started belongs to the old batch's counts.
                if (j.Batch == _batch)
                {
                    _saved++;
                    foreach (var r in j.Reasons.Distinct())
                        if (_perReason.TryGetValue(r, out var c)) _perReason[r] = (c.Ng, c.Saved + 1);
                }
                _countsDirty = true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A half-written image (disk full) is removed, never left uncounted.
            TryDelete(bmp);
            TryDelete(meta);
            Skip(SkipReason.Disk);
        }
    }

    long Trim(int maxFiles, long maxBytes)
    {
        long freed = 0;
        while (true)
        {
            lock (_lock)
                if (_files.Count <= maxFiles && _bytesKept <= maxBytes) return freed;
            var n = DeleteOldest();
            if (n <= 0) return freed;
            freed += n;
        }
    }

    // Deletes the oldest kept image and its .json; returns its bytes, 0 when none is left, or
    // -1 when it could not be deleted (it stays on the list, counted, and is tried again later).
    long DeleteOldest()
    {
        (string Path, long Bytes, string? ImageId) oldest;
        lock (_lock)
        {
            if (_files.First is null) return 0;
            oldest = _files.First.Value;
            _files.RemoveFirst();
            _bytesKept -= oldest.Bytes;
        }
        try
        {
            File.Delete(oldest.Path);
            TryDelete(Path.ChangeExtension(oldest.Path, ".json"));
            lock (_lock) Unindex(oldest.ImageId, oldest.Path);
            return Math.Max(1, oldest.Bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // locked by a viewer or a scanner: keep counting it, try again on a later trim
            lock (_lock)
            {
                _files.AddLast(oldest);
                _bytesKept += oldest.Bytes;
            }
            return -1;
        }
    }

    static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    void IndexExisting()
    {
        // The images kept before a restart still count against the limits.
        var found = new List<(string Path, long Bytes, DateTime At, string? ImageId, string? Camera)>();
        foreach (var f in new DirectoryInfo(_opt.Folder).EnumerateFiles("*.bmp", SearchOption.AllDirectories))
        {
            try
            {
                var meta = new FileInfo(Path.ChangeExtension(f.FullName, ".json"));
                var (id, camera) = meta.Exists ? ReadIdentity(meta.FullName) : (null, null);
                // a file in the naming pattern is found by its name even when its .json is gone
                var stem = Path.GetFileNameWithoutExtension(f.Name);
                if (id is null && ImageName.TryParse(stem, out var n)) (id, camera) = (stem, $"C{n.Camera}");
                found.Add((f.FullName, f.Length + (meta.Exists ? meta.Length : 0), f.LastWriteTimeUtc, id is null ? null : ImageName.KeyOf(id), camera));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var x in found.OrderBy(x => x.At))
        {
            _files.AddLast((x.Path, x.Bytes, x.ImageId));
            _bytesKept += x.Bytes;
            Index(x.ImageId, x.Camera, x.Path);
        }
        Trim(_opt.MaxFiles, _opt.MaxBytes);
    }

    // The image id and camera a .json names; none for an older file or one that cannot be read.
    static (string? ImageId, string? Camera) ReadIdentity(string metaPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            string? Get(string n) => doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } t ? t : null;
            return (Get("image_id"), Get("camera"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return (null, null); }
    }

    string CountsPath => Path.Combine(_opt.Folder, "counts.json");

    void LoadCounts()
    {
        // Counts survive a restart of the station (a crash mid-batch must not reset them).
        try
        {
            if (_disabled || !File.Exists(CountsPath)) return;
            var c = JsonSerializer.Deserialize<NgCounts>(File.ReadAllText(CountsPath));
            if (c is null) return;
            _batch = c.Batch; _since = c.Since; _parts = c.Parts; _ok = c.Ok; _ng = c.Ng; _saved = c.Saved;
            foreach (var kv in c.Skipped) _skipped[kv.Key] = kv.Value;
            foreach (var kv in c.PerReason) _perReason[kv.Key] = (kv.Value.Ng, kv.Value.Saved);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }
    }

    void SaveCounts(bool force)
    {
        if (_disabled) return;
        var now = _time.GetTimestamp();
        if (!force && (!_countsDirty || _time.GetElapsedTime(Interlocked.Read(ref _lastCountsSave), now) < _opt.SaveCountsEvery)) return;
        Interlocked.Exchange(ref _lastCountsSave, now);
        _countsDirty = false;
        try
        {
            // Write then rename, so a power cut never leaves half a file.
            var tmp = CountsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Counts));
            File.Move(tmp, CountsPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _countsDirty = true; }
    }

    static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string(s.Select(ch => bad.Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
        return t.Length == 0 ? "_" : t.Length > 60 ? t[..60] : t;
    }

    // An image id or camera name as a file name part: only [A-Za-z0-9._-], at most 64 characters.
    static string SafeId(string s)
    {
        var t = new string(s.Select(ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' ? ch : '_').ToArray());
        return t.Length == 0 ? "_" : t.Length > 64 ? t[..64] : t;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        // The queue itself is never disposed: a writer still busy on a slow disk must not
        // find it gone. It ends on its own once the queue is empty.
        _writer.Join(TimeSpan.FromSeconds(10));
        SaveCounts(force: true);
    }
}

/// <summary>An 8-bit grey BMP (bottom-up, 256-entry grey palette).</summary>
public static class BmpGray
{
    /// <summary>Reads a file made by <see cref="Write"/>: top-down pixels, rows without padding. Throws <see cref="InvalidDataException"/> for any other BMP.</summary>
    public static (byte[] Pixels, int Width, int Height) Read(string path)
    {
        using var r = Open(path);
        var px = new byte[r.Width * r.Height];
        var row = new byte[r.Width];
        int y;
        while ((y = r.ReadRow(row)) >= 0) row.CopyTo(px, y * r.Width);
        return (px, r.Width, r.Height);
    }

    /// <summary>
    /// Opens a file made by <see cref="Write"/> to read it row by row, so a large image never needs a second
    /// full-size copy in memory. Throws <see cref="InvalidDataException"/> for any other BMP.
    /// </summary>
    public static BmpReader Open(string path) => new(path);

    public static long Write(string path, ReadOnlySpan<byte> gray, int width, int height)
    {
        var stride = (width + 3) & ~3;
        var pixelBytes = stride * height;
        const int header = 14 + 40 + 1024;
        using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        Span<byte> h = stackalloc byte[header];
        h.Clear();
        h[0] = (byte)'B'; h[1] = (byte)'M';
        BitConverter.TryWriteBytes(h[2..], header + pixelBytes);
        BitConverter.TryWriteBytes(h[10..], header);
        BitConverter.TryWriteBytes(h[14..], 40);
        BitConverter.TryWriteBytes(h[18..], width);
        BitConverter.TryWriteBytes(h[22..], height);
        BitConverter.TryWriteBytes(h[26..], (short)1);
        BitConverter.TryWriteBytes(h[28..], (short)8);
        BitConverter.TryWriteBytes(h[34..], pixelBytes);
        BitConverter.TryWriteBytes(h[46..], 256);
        for (var i = 0; i < 256; i++) { h[54 + i * 4] = (byte)i; h[55 + i * 4] = (byte)i; h[56 + i * 4] = (byte)i; }
        f.Write(h);
        var row = new byte[stride];
        for (var y = height - 1; y >= 0; y--)
        {
            gray.Slice(y * width, width).CopyTo(row);
            f.Write(row);
        }
        return header + pixelBytes;
    }
}

/// <summary>Reads an 8-bit grey BMP made by <see cref="BmpGray.Write"/> straight from the file, in file order (bottom row first).</summary>
public sealed class BmpReader : IDisposable
{
    const int HeaderBytes = 14 + 40 + 1024;
    readonly FileStream _f;
    readonly byte[] _row;
    int _next;

    public int Width { get; }
    public int Height { get; }

    internal BmpReader(string path)
    {
        _f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        try
        {
            var b = new byte[HeaderBytes];
            if (_f.Read(b, 0, b.Length) < b.Length || b[0] != 'B' || b[1] != 'M' || BitConverter.ToInt32(b, 14) != 40 || BitConverter.ToInt16(b, 28) != 8
                || BitConverter.ToInt32(b, 30) != 0 || BitConverter.ToInt32(b, 10) != HeaderBytes)
                throw new InvalidDataException("not an 8-bit grey BMP made by this station");
            Width = BitConverter.ToInt32(b, 18);
            Height = BitConverter.ToInt32(b, 22);
            var stride = ((long)Width + 3) & ~3L;
            if (Width <= 0 || Height <= 0 || (long)Width * Height > int.MaxValue || _f.Length < HeaderBytes + stride * Height)
                throw new InvalidDataException("the BMP is cut short");
            _row = new byte[stride];
            _next = Height - 1;
        }
        catch { _f.Dispose(); throw; }
    }

    /// <summary>
    /// Reads the next row in file order into <paramref name="row"/> (at least <see cref="Width"/> bytes) and returns its
    /// y in the picture (the first call returns Height-1, the last 0); -1 after the last row.
    /// </summary>
    public int ReadRow(Span<byte> row)
    {
        if (_next < 0) return -1;
        _f.ReadExactly(_row);
        _row.AsSpan(0, Width).CopyTo(row);
        return _next--;
    }

    public void Dispose() => _f.Dispose();
}
