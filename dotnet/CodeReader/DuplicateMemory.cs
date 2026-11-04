using System;
using System.Collections.Generic;

namespace BarcodeReading;

/// <summary>
/// The last <see cref="Capacity"/> codes accepted on the line, oldest forgotten first. A code
/// read again while it is remembered is a duplicate (a re-used or copied label). Thread-safe.
/// </summary>
public sealed class DuplicateMemory
{
    private readonly object _lock = new();
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, int> _count = new(StringComparer.Ordinal);
    private int _capacity;

    public DuplicateMemory(int capacity) => _capacity = Math.Max(0, capacity);

    /// <summary>How many codes are remembered. Lowering it forgets the oldest at once.</summary>
    public int Capacity
    {
        get { lock (_lock) return _capacity; }
        set
        {
            lock (_lock)
            {
                _capacity = Math.Max(0, value);
                Trim();
            }
        }
    }

    public int Count
    {
        get { lock (_lock) return _order.Count; }
    }

    public bool Contains(string code)
    {
        lock (_lock) return _count.ContainsKey(code);
    }

    /// <summary>The codes of <paramref name="codes"/> that are remembered (in their order).</summary>
    public List<string> Duplicates(IEnumerable<string> codes)
    {
        var dup = new List<string>();
        lock (_lock)
        {
            foreach (var c in codes)
                if (_count.ContainsKey(c)) dup.Add(c);
        }
        return dup;
    }

    /// <summary>Remembers accepted codes; the oldest are forgotten beyond the capacity.</summary>
    public void Add(IEnumerable<string> codes)
    {
        lock (_lock)
        {
            foreach (var c in codes)
            {
                if (string.IsNullOrEmpty(c)) continue;
                _order.Enqueue(c);
                _count[c] = _count.TryGetValue(c, out int n) ? n + 1 : 1;
            }
            Trim();
        }
    }

    public void Add(string code) => Add(new[] { code });

    public void Clear()
    {
        lock (_lock)
        {
            _order.Clear();
            _count.Clear();
        }
    }

    private void Trim()
    {
        while (_order.Count > _capacity)
        {
            var old = _order.Dequeue();
            if (--_count[old] == 0) _count.Remove(old);
        }
    }
}
