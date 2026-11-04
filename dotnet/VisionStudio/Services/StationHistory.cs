namespace VisionStudio.Services;

/// <summary>One cycle for the live charts.</summary>
public readonly record struct CycleRecord(DateTime At, bool Ok, double Ms);

/// <summary>Yield and parts in one minute.</summary>
public readonly record struct MinuteRecord(DateTime Minute, int Parts, int Ok, double MedianMs);

/// <summary>
/// The recent past of the running station, for the Run screen's live charts: the last cycles
/// (fixed-size ring, so it never grows) and one record per minute for the last hours.
/// Thread-safe; adding a cycle costs a few hundred nanoseconds.
/// </summary>
public sealed class StationHistory(int keepCycles = 600, int keepMinutes = 480)
{
    private readonly CycleRecord[] _ring = new CycleRecord[keepCycles];
    private readonly LinkedList<MinuteRecord> _minutes = new();
    private readonly List<double> _minuteMs = new();
    private readonly object _lock = new();
    private int _count, _head;
    private DateTime _minute;
    private int _minParts, _minOk;

    public void Add(DateTime at, bool ok, double ms)
    {
        lock (_lock)
        {
            _ring[_head] = new CycleRecord(at, ok, ms);
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
            var m = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, at.Kind);
            if (m != _minute) CloseMinute(m);
            _minParts++;
            if (ok) _minOk++;
            if (_minuteMs.Count < 10_000) _minuteMs.Add(ms);
        }
    }

    private void CloseMinute(DateTime next)
    {
        if (_minParts > 0)
        {
            _minuteMs.Sort();
            _minutes.AddLast(new MinuteRecord(_minute, _minParts, _minOk, _minuteMs[_minuteMs.Count / 2]));
            while (_minutes.Count > keepMinutes) _minutes.RemoveFirst();
        }
        _minute = next;
        _minParts = _minOk = 0;
        _minuteMs.Clear();
    }

    /// <summary>The last cycles, oldest first.</summary>
    public IReadOnlyList<CycleRecord> Cycles(int max = 600)
    {
        lock (_lock)
        {
            var n = Math.Min(max, _count);
            var list = new List<CycleRecord>(n);
            for (var i = n; i > 0; i--) list.Add(_ring[(_head - i + _ring.Length) % _ring.Length]);
            return list;
        }
    }

    /// <summary>Closed minutes, oldest first, plus the minute in progress.</summary>
    public IReadOnlyList<MinuteRecord> Minutes()
    {
        lock (_lock)
        {
            var list = _minutes.ToList();
            if (_minParts > 0)
            {
                var ms = _minuteMs.Order().ToList();
                list.Add(new MinuteRecord(_minute, _minParts, _minOk, ms[ms.Count / 2]));
            }
            return list;
        }
    }

    public void Clear()
    {
        lock (_lock) { _count = _head = 0; _minutes.Clear(); _minuteMs.Clear(); _minParts = _minOk = 0; }
    }
}
