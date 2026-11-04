namespace StationGuard;

/// <summary>What the leak check found.</summary>
public sealed record LeakVerdict(bool Suspected, double MegabytesPerHour, int Points);

/// <summary>
/// Spots memory that keeps growing while the station runs. Memory goes up
/// and down with every part, so the check uses the floor of each bucket
/// (the lowest reading in it: what stays after the work is done) and fits a
/// robust slope through those floors (Theil-Sen: the median of all pairwise
/// slopes, so a few odd readings do not make or hide a leak). A leak is
/// suspected only when the floors rise steadily for the whole window and
/// faster than the allowed rate.
/// </summary>
public sealed class LeakDetector(TimeSpan bucket, int buckets, double allowedMbPerHour, double steadyShare = .75)
{
    readonly List<(DateTimeOffset Start, long Floor)> _floors = [];

    public void Add(DateTimeOffset at, long bytes)
    {
        if (_floors.Count > 0 && at - _floors[^1].Start < bucket)
        {
            if (bytes < _floors[^1].Floor) _floors[^1] = (_floors[^1].Start, bytes);
            return;
        }
        _floors.Add((at, bytes));
        if (_floors.Count > buckets) _floors.RemoveAt(0);
    }

    /// <summary>Forget the history (after a clean-up step changed the baseline).</summary>
    public void Reset() => _floors.Clear();

    public LeakVerdict Check()
    {
        // The last bucket is still filling; judge only closed ones.
        var n = _floors.Count - 1;
        if (n < Math.Max(4, buckets / 2)) return new LeakVerdict(false, 0, n);
        var slopes = new List<double>(n * (n - 1) / 2);
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var hours = (_floors[j].Start - _floors[i].Start).TotalHours;
                if (hours > 0) slopes.Add((_floors[j].Floor - _floors[i].Floor) / 1048576.0 / hours);
            }
        slopes.Sort();
        var mbPerHour = slopes.Count == 0 ? 0 : slopes[slopes.Count / 2];
        var rising = 0;
        for (var i = 1; i < n; i++) if (_floors[i].Floor >= _floors[i - 1].Floor) rising++;
        var steady = rising >= steadyShare * (n - 1);
        return new LeakVerdict(steady && mbPerHour > allowedMbPerHour, mbPerHour, n);
    }
}
