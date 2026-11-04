using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VisionSystem.Runtime;

/// <summary>One graded part of one Read code tool: its grade (4 = A .. 0 = F; null = a code could not be graded).</summary>
public sealed record GradeSample(DateTime TimeUtc, double? Grade, double? LowestGrade);

/// <summary>The grade over time of one hour (UTC): samples, mean, worst, ungraded (an ungraded part counts as F).</summary>
public sealed record GradeBucket(DateTime HourUtc, int Samples, double Mean, double Worst, int Ungraded);

/// <summary>Drift status of one tool (requirements 17.3 PQ-03/PQ-04, 6.4 WEB-05): the plant server's rule, so the
/// browser and the plant dashboard agree.</summary>
public sealed record GradeDrift(string State, int Samples, int Ungraded, double Lowest, string LowestFrom, double WarnAt,
    double? Mean, double? Slope, double? Projected);

/// <summary>
/// The print grades of the graded parts since the runtime started, per Read code tool, for the browser
/// dashboard's print-quality trend and drift status (read only; the line's own lowest-grade check decides each
/// part, PQ-05). Kept in memory, the newest <see cref="Keep"/> per tool, so a week at one graded part in N is
/// bounded. With a file (WEB-06) the samples are also written to it, within a second, and read back at start, so
/// a restart keeps the trend; the file holds only these samples, never the results ledger. A file that cannot be
/// written only loses the history: <see cref="Add"/> never waits on the disk or throws for it. The numbers are those of plant_server/backend/vision/grades.py: the mean of the latest 20 samples
/// within 0.5 of the lowest grade, or falling so fast it reaches it within the next 20 samples; at least 10 samples;
/// an ungraded sample counts as F (doubt is never read as good).
/// </summary>
public sealed class GradeTrail : IDisposable
{
    public const int Keep = 2000, Window = 20, MinSamples = 10, MaxAgeHours = 8, MaxTools = 64;
    /// <summary>Samples older than the longest range the dashboard shows (a week) are not read back.</summary>
    public const int KeepHours = 168;
    public const double Margin = 0.5, DefaultLowest = 2.0, Top = 4.0;

    readonly object _lock = new(), _fileLock = new();
    readonly Dictionary<string, List<GradeSample>> _tools = new(StringComparer.Ordinal);
    readonly string? _file;
    readonly StringBuilder _pending = new();
    readonly Timer? _flush;
    int _linesOnDisk;

    /// <summary>Kept in memory only.</summary>
    public GradeTrail() { }

    /// <summary>Kept in memory and in <paramref name="file"/>: the samples it holds within the limits are read back
    /// first (those older than <see cref="KeepHours"/> before <paramref name="nowUtc"/> are dropped), and the file is
    /// rewritten to them.</summary>
    public GradeTrail(string file, DateTime? nowUtc = null)
    {
        _file = file;
        var cut = (nowUtc ?? DateTime.UtcNow).AddHours(-KeepHours);
        try
        {
            if (File.Exists(file))
                foreach (var line in File.ReadLines(file))
                    if (Parse(line) is { } s && s.Sample.TimeUtc >= cut) Hold(s.Tool, s.Sample);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        List<(string, GradeSample)> all;
        lock (_lock)
        {
            foreach (var list in _tools.Values)
            {
                var sorted = list.OrderBy(x => x.TimeUtc).ToList();   // stable: parts of the same moment keep their order
                list.Clear();
                list.AddRange(sorted);
            }
            all = Snapshot();
        }
        lock (_fileLock) Compact(all);
        _flush = new Timer(_ => Flush(), null, 1000, 1000);
    }

    /// <summary>A graded part. A grade outside 0..4 is not a grade: the part counts as ungraded.</summary>
    public void Add(string tool, DateTime timeUtc, double? grade, double? lowestGrade = null)
    {
        if (string.IsNullOrEmpty(tool) || tool.Length > 128) return;
        var s = new GradeSample(timeUtc, Grade(grade), Lowest(lowestGrade));
        lock (_lock)
        {
            if (!Hold(tool, s) || _file is null) return;
            _pending.Append(JsonSerializer.Serialize(new Line(tool, timeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), s.Grade, s.LowestGrade))).Append('\n');
        }
    }

    static double? Grade(double? grade) => grade is { } v && v >= 0 && v <= Top && !double.IsNaN(v) ? v : null;
    static double? Lowest(double? lowest) => lowest is { } l && l >= 1 && l <= Top ? l : null;

    bool Hold(string tool, GradeSample s)
    {
        lock (_lock)
        {
            if (!_tools.TryGetValue(tool, out var list))
            {
                if (_tools.Count >= MaxTools) return false;
                _tools[tool] = list = [];
            }
            list.Add(s);
            if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
            return true;
        }
    }

    /// <summary>Writes the samples added since the last call; when the file has grown to twice what is kept, it
    /// is rewritten to what is kept. Called every second, and at <see cref="Dispose"/>. The disk is written outside
    /// the lock <see cref="Add"/> takes, so a part never waits on it.</summary>
    public void Flush()
    {
        if (_file is null) return;
        lock (_fileLock)
        {
            string lines;
            List<(string Tool, GradeSample Sample)>? all = null;
            lock (_lock)
            {
                if (_pending.Length == 0) return;
                lines = _pending.ToString();
                _pending.Clear();
                var added = lines.Count(c => c == '\n');
                if (_linesOnDisk + added > 2 * Math.Max(Keep, _tools.Values.Sum(l => l.Count))) all = Snapshot();
                else _linesOnDisk += added;
            }
            if (all is not null) { Compact(all); return; }   // the rewrite holds the new samples too
            try { File.AppendAllText(_file, lines); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // Under _lock.
    List<(string Tool, GradeSample Sample)> Snapshot() => [.. _tools.SelectMany(t => t.Value.Select(s => (t.Key, s)))];

    /// <summary>Rewrites the file to these samples (a new file moved over the old one). Under _fileLock.</summary>
    void Compact(List<(string Tool, GradeSample Sample)> all)
    {
        if (_file is null) return;
        _linesOnDisk = all.Count;
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(_file)) is { } dir) Directory.CreateDirectory(dir);
            var temp = _file + ".new";
            using (var w = new StreamWriter(temp, false, new UTF8Encoding(false)))
                foreach (var (tool, s) in all)
                {
                    w.Write(JsonSerializer.Serialize(new Line(tool, s.TimeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), s.Grade, s.LowestGrade)));
                    w.Write('\n');
                }
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>One sample as written to the file.</summary>
    sealed record Line(string T, string At, double? G, double? L);

    /// <summary>A line of the file, or null for a torn or unreadable one; a grade outside 0..4 reads as ungraded.</summary>
    static (string Tool, GradeSample Sample)? Parse(string line)
    {
        try
        {
            if (JsonSerializer.Deserialize<Line>(line) is not { T: { Length: > 0 and <= 128 } tool, At: { } at } l) return null;
            if (!DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return null;
            return (tool, new GradeSample(t, Grade(l.G), Lowest(l.L)));
        }
        catch (JsonException) { return null; }
    }

    public void Dispose()
    {
        _flush?.Dispose();
        Flush();
    }

    public IReadOnlyList<string> Tools()
    {
        lock (_lock) return [.. _tools.Keys.Order(StringComparer.Ordinal)];
    }

    public (IReadOnlyList<GradeBucket> Buckets, GradeDrift Drift)? Of(string tool, DateTime fromUtc, DateTime nowUtc)
    {
        List<GradeSample> all;
        lock (_lock)
        {
            if (!_tools.TryGetValue(tool, out var list)) return null;
            all = [.. list];
        }
        var buckets = all.Where(s => s.TimeUtc >= fromUtc)
            .GroupBy(s => new DateTime(s.TimeUtc.Year, s.TimeUtc.Month, s.TimeUtc.Day, s.TimeUtc.Hour, 0, 0, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var gs = g.Select(s => s.Grade ?? 0).ToList();
                return new GradeBucket(g.Key, gs.Count, gs.Average(), gs.Min(), g.Count(s => s.Grade is null));
            }).ToList();
        var recent = all.Where(s => s.TimeUtc >= nowUtc.AddHours(-MaxAgeHours)).TakeLast(Window).ToList();
        return (buckets, DriftOf(recent));
    }

    /// <summary>The drift status of the samples (oldest first). The station's own lowest grade (the latest
    /// sample's) wins over the default of C.</summary>
    public static GradeDrift DriftOf(IReadOnlyList<GradeSample> samples)
    {
        var mins = samples.Where(s => s.LowestGrade is not null).Select(s => s.LowestGrade!.Value).ToList();
        var lowest = mins.Count > 0 ? mins[^1] : DefaultLowest;
        var warn = lowest + Margin;
        int n = samples.Count, ungraded = samples.Count(s => s.Grade is null);
        string from = mins.Count > 0 ? "tool" : "default";
        if (n == 0) return new GradeDrift("too_few", 0, 0, lowest, from, warn, null, null, null);
        var gs = samples.Select(s => s.Grade ?? 0).ToList();
        var mean = gs.Average();
        var xm = (n - 1) / 2.0;
        var sxx = Enumerable.Range(0, n).Sum(i => (i - xm) * (i - xm));
        var slope = sxx > 0 ? Enumerable.Range(0, n).Sum(i => (i - xm) * (gs[i] - mean)) / sxx : 0.0;
        var projected = mean + slope * ((n - 1) - xm + Window);
        var state = "too_few";
        if (n >= MinSamples)
            state = mean <= warn || (slope < 0 && projected <= lowest) ? "drift" : "ok";
        return new GradeDrift(state, n, ungraded, lowest, from, warn, Math.Round(mean, 3), Math.Round(slope, 4), Math.Round(projected, 3));
    }

    public static string Letter(double g) => "FDCBA"[(int)Math.Min(Top, Math.Max(0, Math.Floor(g + 1e-9)))].ToString();
}
