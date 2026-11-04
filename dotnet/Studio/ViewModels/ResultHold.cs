using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// Which result the big result block shows (UI-UX-SPEC.md 4.1): a bad part (NG or duplicate)
/// stays up for at least <see cref="BadHold"/>, and at high rates the worst result of the last
/// <see cref="Window"/> is shown so it stays readable. An OK never hides a recent bad part.
/// </summary>
public sealed class ResultHold
{
    public static readonly TimeSpan BadHold = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(200);
    /// <summary>With no new part for this long the last result is no longer shown as current.</summary>
    public static readonly TimeSpan LastFor = TimeSpan.FromSeconds(5);

    private readonly Queue<(DateTimeOffset At, Verdict Verdict)> _recent = new();
    private (DateTimeOffset At, Verdict Verdict)? _lastBad;
    private (DateTimeOffset At, Verdict Verdict) _last = (DateTimeOffset.MinValue, Verdict.None);

    public void Add(Verdict verdict, DateTimeOffset at)
    {
        if (verdict == Verdict.None) return;   // a preview frame is no part
        _recent.Enqueue((at, verdict));
        _last = (at, verdict);
        if (verdict is Verdict.Ng or Verdict.Duplicate)
        {
            if (_lastBad is not { } held || at - held.At >= BadHold || Tones.Rank(verdict) >= Tones.Rank(held.Verdict))
            {
                _lastBad = (at, verdict);
            }
        }
    }

    public void Clear()
    {
        _recent.Clear();
        _lastBad = null;
        _last = (DateTimeOffset.MinValue, Verdict.None);
    }

    /// <summary>A bad part was shown since this time.</summary>
    public bool BadSince(DateTimeOffset since) => _lastBad is { } bad && bad.At >= since;

    public Verdict Current(DateTimeOffset now)
    {
        while (_recent.Count > 0 && now - _recent.Peek().At > Window) _recent.Dequeue();
        if (_lastBad is { } bad && now - bad.At < BadHold) return bad.Verdict;
        if (_recent.Count == 0) return now - _last.At < LastFor ? _last.Verdict : Verdict.None;
        return _recent.MaxBy(r => Tones.Rank(r.Verdict)).Verdict;
    }
}
