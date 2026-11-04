using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>Parts finished in one minute. Bad counts NG and same code.</summary>
public sealed record MinuteBar(DateTimeOffset Start, int Good, int Bad)
{
    public int Total => Good + Bad;
}

/// <summary>One NG reason and how many parts it failed.</summary>
public sealed record ReasonBar(string Reason, int Count);

/// <summary>Parts whose cycle time was from FromMs up to (not including) ToMs.</summary>
public sealed record CycleBin(double FromMs, double ToMs, int Count);

/// <summary>
/// The Overview charts of the Run screen (UI-UX-SPEC.md 4.1 and principle 15), worked out in the
/// app from the parts list: every part's own result already reaches the app, so no runtime call is
/// needed. Minutes: the last <see cref="MinutesShown"/> minutes, oldest first, the current minute
/// last, from the list's per-minute counts (the list itself holds only the newest parts). Reasons:
/// every bad part since the app started, largest first, the rest folded into one "other" bar.
/// Cycle: the last <see cref="CyclePartsUsed"/> parts that report a cycle time.
/// </summary>
public sealed record OverviewData(IReadOnlyList<MinuteBar> Minutes, IReadOnlyList<ReasonBar> Reasons,
    IReadOnlyList<CycleBin> Cycle, double MedianMs, double P95Ms, double SlowestMs, int CycleParts)
{
    public const int MinutesShown = 60;
    public const int ReasonsShown = 5;
    public const int CyclePartsUsed = 300;
    public const int MaxBins = 16;

    public static readonly OverviewData Empty = new([], [], [], 0, 0, 0, 0);

    public bool HasParts => Minutes.Any(m => m.Total > 0);

    /// <summary>The good share of each minute in percent, or null for a minute without parts.</summary>
    public IReadOnlyList<double?> GoodPercentByMinute =>
        Minutes.Select(m => m.Total > 0 ? 100.0 * m.Good / m.Total : (double?)null).ToList();

    /// <param name="sameCode">Label of a same-code part that has no reason text.</param>
    /// <param name="noReason">Label of an NG part that has no reason text.</param>
    /// <param name="other">Label of the bar that folds the smaller reasons.</param>
    /// <param name="words">The reason in the user's words: the runtime sends some reasons as string-table keys.</param>
    public static OverviewData From(PartLog log, DateTimeOffset now, string sameCode, string noReason, string other,
        Func<string, string>? words = null)
    {
        if (log.Listed == 0) return Empty;
        var thisMinute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        var first = thisMinute.AddMinutes(-(MinutesShown - 1));
        var good = new int[MinutesShown];
        var bad = new int[MinutesShown];
        foreach (var (start, g, b) in log.Minutes)
        {
            var i = (int)Math.Round((start - first).TotalMinutes);
            if (i < 0 || i >= MinutesShown) continue;
            good[i] += g;
            bad[i] += b;
        }
        var minutes = Enumerable.Range(0, MinutesShown).Select(i => new MinuteBar(first.AddMinutes(i), good[i], bad[i])).ToList();

        var byReason = log.Reasons
            .GroupBy(r => r.Key.Reason is { Length: > 0 } text ? words?.Invoke(text) ?? text : r.Key.SameCode ? sameCode : noReason, r => r.Value)
            .Select(g => new ReasonBar(g.Key, g.Sum()))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();
        var reasons = byReason.Count <= ReasonsShown ? byReason
            : byReason.Take(ReasonsShown - 1).Append(new ReasonBar(other, byReason.Skip(ReasonsShown - 1).Sum(r => r.Count))).ToList();

        var parts = log.Entries.Where(e => e.Part is not null).Select(e => e.Part!);
        var times = parts.Where(p => double.IsFinite(p.CycleMs) && p.CycleMs > 0).TakeLast(CyclePartsUsed).Select(p => p.CycleMs).ToList();
        if (times.Count == 0) return new OverviewData(minutes, reasons, [], 0, 0, 0, 0);
        var sorted = times.Order().ToList();
        var slowest = sorted[^1];
        var width = NiceStep(slowest / MaxBins);
        var binCount = Math.Min(MaxBins, (int)Math.Floor(slowest / width) + 1);
        var counts = new int[binCount];
        foreach (var t in times) counts[Math.Min(binCount - 1, (int)Math.Floor(t / width))]++;
        var bins = Enumerable.Range(0, binCount).Select(i => new CycleBin(i * width, (i + 1) * width, counts[i])).ToList();
        return new OverviewData(minutes, reasons, bins, Percentile(sorted, 0.5), Percentile(sorted, 0.95), slowest, times.Count);
    }

    /// <summary>The value at or below which the given share of the sorted values lie (nearest rank).</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double share) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>The smallest of 1, 2, 2.5 and 5 times a power of ten that is at least the given value.</summary>
    public static double NiceStep(double atLeast)
    {
        if (atLeast <= 0) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(atLeast)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * power >= atLeast) return m * power;
        return 10 * power;
    }
}
