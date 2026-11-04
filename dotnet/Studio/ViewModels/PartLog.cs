using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>A line of the parts list: one part, or a gap where parts are missing from the list.
/// Missed: how many parts the list does not have (0 when not known, as after a lost link).</summary>
public sealed record PartEntry(PartResult? Part, DateTimeOffset Time, long Missed = 0, bool LinkLost = false)
{
    public bool IsGap => Part is null;
}

/// <summary>
/// Every part's result as the runtime streams it (owner, 2026-10-30: each part's own result is
/// listed at any line speed, "as report is required"), newest last, at most <see cref="Capacity"/>.
/// The list is for looking; the runtime's results log keeps every part for the reports. Where
/// parts are missing from the list (the link was down, the stream fell behind) a gap says so,
/// so the list never looks complete when it is not. Used on the UI thread only.
/// </summary>
public sealed class PartLog
{
    public const int Capacity = 2000;
    private readonly List<PartEntry> _entries = [];
    private ulong? _lastId;
    private bool _linkLostNoted;
    /// <summary>Good and bad parts per minute (minutes since the epoch, UTC), the last hour and a bit.</summary>
    private readonly SortedDictionary<long, (int Good, int Bad)> _minutes = [];
    private readonly Dictionary<(string Reason, bool SameCode), int> _reasons = [];

    /// <summary>Changes with every added line, so a view knows when to redraw.</summary>
    public int Version { get; private set; }
    public long Good { get; private set; }
    public long Bad { get; private set; }
    public long SameCode { get; private set; }
    public long Listed => Good + Bad + SameCode;

    public IReadOnlyList<PartEntry> Entries => _entries;

    /// <summary>Kept per minute for the Overview chart, whatever the line speed: at 225 parts a
    /// minute the list itself holds less than ten minutes.</summary>
    public const int MinutesKept = 61;
    /// <summary>Distinct reasons kept; a new one beyond this counts as "no reason given".</summary>
    public const int MaxReasons = 100;

    /// <summary>Good and bad parts of each minute with parts, by the minute's start (UTC).</summary>
    public IEnumerable<(DateTimeOffset Start, int Good, int Bad)> Minutes =>
        _minutes.Select(m => (DateTimeOffset.FromUnixTimeSeconds(m.Key * 60), m.Value.Good, m.Value.Bad));

    /// <summary>How many bad parts each reason failed since the app started ("" when the runtime gave none).</summary>
    public IReadOnlyDictionary<(string Reason, bool SameCode), int> Reasons => _reasons;

    public void Add(PartResult part)
    {
        if (_lastId is { } last && part.Id > last + 1)
        {
            // The runtime counts on; ids lower than before mean it restarted, which is no gap of ours.
            Append(new PartEntry(null, part.Time, (long)(part.Id - last - 1)));
        }
        _lastId = part.Id;
        _linkLostNoted = false;
        switch (part.Result)
        {
            case Verdict.Ok: Good++; break;
            case Verdict.Duplicate: SameCode++; break;
            default: Bad++; break;
        }
        Tally(part);
        Append(new PartEntry(part, part.Time));
    }

    /// <summary>The link dropped: parts made until it is back are not in the list.</summary>
    public void NoteLinkLost(DateTimeOffset at)
    {
        if (_linkLostNoted || _lastId is null) return;
        _linkLostNoted = true;
        Append(new PartEntry(null, at, LinkLost: true));
    }

    private void Tally(PartResult part)
    {
        var minute = part.Time.ToUnixTimeSeconds() / 60;
        _minutes.TryGetValue(minute, out var m);
        _minutes[minute] = part.Result == Verdict.Ok ? (m.Good + 1, m.Bad) : (m.Good, m.Bad + 1);
        // Kept around this part's own minute, so one part with a wrong clock never empties the hour.
        foreach (var stale in _minutes.Keys.Where(k => k <= minute - MinutesKept || k >= minute + MinutesKept).ToList())
            _minutes.Remove(stale);
        if (part.Result == Verdict.Ok) return;
        var key = (part.Reason.Trim(), part.Result == Verdict.Duplicate);
        // Free texts (an exception message, "3 of 24 codes read") must not grow the table for ever.
        if (!_reasons.ContainsKey(key) && _reasons.Count >= MaxReasons) key = ("", key.Item2);
        _reasons[key] = _reasons.GetValueOrDefault(key) + 1;
    }

    private void Append(PartEntry entry)
    {
        _entries.Add(entry);
        if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
        Version++;
    }
}
