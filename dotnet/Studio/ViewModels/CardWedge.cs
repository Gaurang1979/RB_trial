using System.Text;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// Tells a badge reader from a person at the keyboard (UI-UX-SPEC.md 4.5). A USB keyboard-wedge
/// reader types the card number as a fast burst of keys ending in Enter; a person types slower.
/// Every key of the burst, Enter included, must follow the one before within <see cref="MaxGap"/>.
/// The number is handed on once and never kept, logged or shown.
/// </summary>
public sealed class CardWedge(TimeProvider time)
{
    public const int MinLength = VisionStudio.Remote.RuntimeAccess.MinCardLength;
    public const int MaxLength = 64;
    public static readonly TimeSpan MaxGap = TimeSpan.FromMilliseconds(50);

    private readonly StringBuilder _typed = new(MaxLength);
    private DateTimeOffset _last;
    private int _keys;
    private bool _tooLong;

    /// <summary>Typed text, as the window's text input sees it.</summary>
    public void Text(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var now = time.GetUtcNow();
        if (now - _last > MaxGap) Clear();
        _last = now;
        foreach (var c in text)
        {
            if (char.IsControl(c)) continue;
            _keys++;
            if (_typed.Length >= MaxLength) _tooLong = true;
            else _typed.Append(c);
        }
    }

    /// <summary>Enter was pressed: the card number when the keys before it were a reader's burst, else
    /// null. Burst is true for any machine-fast keys before it (a card too short or too long, a burst
    /// split by a stall): that Enter is the reader's and must not press a button.</summary>
    public string? Enter(out bool burst)
    {
        var fast = time.GetUtcNow() - _last <= MaxGap;
        burst = fast && _keys >= 2;
        var card = fast && !_tooLong && _typed.Length >= MinLength ? _typed.ToString() : null;
        Clear();
        return card;
    }

    /// <summary>Any other key (Tab, arrows, Backspace) ends a burst: a reader never sends one.</summary>
    public void Clear()
    {
        _typed.Clear();
        _keys = 0;
        _tooLong = false;
    }
}
