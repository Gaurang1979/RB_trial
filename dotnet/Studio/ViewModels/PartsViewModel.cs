using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>One row of the parts list as shown: shape and colour, then the words. Image is the part's
/// image name (requirements 12, one name for each image), "" when the runtime kept no image.</summary>
public sealed record PartRow(Tone Tone, string Icon, string Time, string Product, string PartId, string Result,
    string Why, string Cycle, bool IsGap, string Image = "");

/// <summary>
/// Parts (requirements 6.1, UI-UX-SPEC.md 4.1): every part's own result, newest first, from the
/// <see cref="PartLog"/>. Redrawn at most once a second so a fast line never slows the app;
/// "Hold the list" stops the redraw so a row can be read, "Bad only" hides the good parts.
/// </summary>
public sealed partial class PartsViewModel : ObservableObject
{
    /// <summary>At most this many rows are drawn; the summary counts every part listed.</summary>
    public const int MaxRows = 300;
    public static readonly TimeSpan RedrawEvery = TimeSpan.FromSeconds(1);

    private readonly Strings _strings;
    private readonly PartLog _log;
    private readonly TimeProvider _time;
    private readonly Action _close;
    private int _drawnVersion = -1;
    private DateTimeOffset _drawnAt;

    public PartsViewModel(Strings strings, PartLog log, TimeProvider time, Action close)
    {
        _strings = strings;
        _log = log;
        _time = time;
        _close = close;
        Redraw();
    }

    public ObservableCollection<PartRow> Rows { get; } = [];

    [ObservableProperty] private bool _badOnly;
    [ObservableProperty] private bool _holding;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private bool _isEmpty = true;

    public string HoldText => _strings[Holding ? "parts.follow" : "parts.hold"];

    partial void OnBadOnlyChanged(bool value) => Redraw();

    partial void OnHoldingChanged(bool value)
    {
        OnPropertyChanged(nameof(HoldText));
        if (!value) Redraw();
    }

    /// <summary>About ten times a second: redraws when new parts came, at most once a second.</summary>
    public void Tick()
    {
        if (Holding || _log.Version == _drawnVersion) return;
        if (_time.GetUtcNow() - _drawnAt < RedrawEvery) return;
        Redraw();
    }

    private void Redraw()
    {
        _drawnVersion = _log.Version;
        _drawnAt = _time.GetUtcNow();
        Rows.Clear();
        var entries = _log.Entries;
        for (var i = entries.Count - 1; i >= 0 && Rows.Count < MaxRows; i--)
        {
            var e = entries[i];
            if (BadOnly && e.Part is { Result: Verdict.Ok }) continue;
            Rows.Add(RowFor(e));
        }
        var c = CultureInfo.CurrentCulture;
        SummaryText = _strings.Format("parts.summary", _log.Listed.ToString("N0", c), _log.Good.ToString("N0", c),
            _log.Bad.ToString("N0", c), _log.SameCode.ToString("N0", c));
        IsEmpty = _log.Listed == 0;
        NoteText = IsEmpty ? _strings["parts.empty"]
            : _strings.Format("parts.kept", PartLog.Capacity.ToString("N0", c), MaxRows.ToString("N0", c));
    }

    private PartRow RowFor(PartEntry e)
    {
        var time = e.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (e.Part is not { } p)
        {
            var text = e.LinkLost ? _strings["parts.gap.link"]
                : _strings.Format("parts.gap.missed", e.Missed.ToString("N0", CultureInfo.CurrentCulture));
            return new PartRow(Tone.Warn, Icons.Bang, time, "", "", "", text, "", true);
        }
        var result = _strings[p.Result switch
        {
            Verdict.Ok => "result.ok",
            Verdict.Duplicate => "result.duplicate",
            _ => "result.ng",
        }];
        var why = p.Reason;
        if (p.CodesRead + p.CodesMissed + p.CodesDuplicate > 0)
        {
            var codes = _strings.Format("parts.codes", p.CodesRead, p.CodesRead + p.CodesMissed + p.CodesDuplicate);
            why = why.Length > 0 ? $"{why} · {codes}" : codes;
        }
        var cycle = p.CycleMs > 0
            ? _strings.Format("run.cycle.ms", p.CycleMs.ToString("0", CultureInfo.CurrentCulture)) : "";
        return new PartRow(Tones.For(p.Result), Icons.For(p.Result), time, p.Product, p.PartId, result, why, cycle, false,
            p.ImageId ?? "");
    }

    [RelayCommand]
    private void ToggleBadOnly() => BadOnly = !BadOnly;

    [RelayCommand]
    private void ToggleHold() => Holding = !Holding;

    [RelayCommand]
    private void Back() => _close();
}
