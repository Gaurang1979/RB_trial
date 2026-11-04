using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>One line of the NG viewer's check list: shape, colour and the runtime's words. Header: a
/// camera's heading in the Run screen's "This picture" list.</summary>
public sealed record CheckLine(Tone Tone, string Icon, string Text, bool Header = false);

/// <summary>
/// NG image viewer (UI-UX-SPEC.md 4.3), first form: the bad part's full image from GetImage (the
/// live picture until it comes, or when the runtime no longer keeps it) with its result graphics,
/// the failed checks in the runtime's words, a tap on a graphic for its tooltip, and Previous /
/// Next through the last bad parts; zoom on the picture; and for a supervisor the review (NgReview.cs).
/// "Show good part" comes later.
/// </summary>
public sealed partial class NgViewerViewModel : ObservableObject
{
    /// <summary>At most this many failed checks are listed; the rest are counted.</summary>
    public const int MaxListed = 8;

    private readonly Strings _strings;
    private readonly RuntimeCalls _calls;
    private readonly IReadOnlyList<NgThumb> _parts;
    private readonly Func<byte[], IImage?> _decode;
    private readonly IOverlayHost _overlays;
    private readonly Dictionary<int, IImage?> _full = [];
    private int _index;

    public NgViewerViewModel(Strings strings, RuntimeCalls calls, IReadOnlyList<NgThumb> parts, int index,
        Func<byte[], IImage?> decode, IOverlayHost overlays)
    {
        _strings = strings;
        _calls = calls;
        _parts = parts;
        _decode = decode;
        _overlays = overlays;
        _index = Math.Clamp(index, 0, Math.Max(0, parts.Count - 1));
        Show();
    }

    public ObservableCollection<CheckLine> Checks { get; } = [];

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private Tone _tone = Tone.Ng;
    [ObservableProperty] private string _icon = Icons.Cross;
    [ObservableProperty] private IImage? _image;
    [ObservableProperty] private double _imageWidth = 1;
    [ObservableProperty] private double _imageHeight = 1;
    [ObservableProperty] private IReadOnlyList<ResultGraphic> _graphics = [];
    [ObservableProperty] private string _positionText = "";
    /// <summary>Why the picture is not the full image (loading, no longer kept), or empty.</summary>
    [ObservableProperty] private string _imageNote = "";
    [ObservableProperty] private bool _hasPrevious;
    [ObservableProperty] private bool _hasNext;
    [ObservableProperty] private string? _tooltipText;
    [ObservableProperty] private Point2? _tooltipAt;

    public int Index => _index;
    /// <summary>The full image of the part shown has come from the runtime.</summary>
    public bool ShowsFullImage => _full.GetValueOrDefault(_index) is not null;

    private void Show()
    {
        TooltipText = null;
        TooltipAt = null;
        Checks.Clear();
        if (_parts.Count == 0) return;
        var part = _parts[_index];
        var frame = part.Frame;
        Title = _strings.Format("ng.title", part.Time);
        Tone = part.Tone;
        Icon = part.Icon;
        Image = _full.GetValueOrDefault(_index) ?? part.Image;
        ImageWidth = Math.Max(1, frame.Width);
        ImageHeight = Math.Max(1, frame.Height);
        Graphics = frame.Graphics;
        PositionText = _strings.Format("ng.position", _index + 1, _parts.Count);
        HasPrevious = _index > 0;
        HasNext = _index < _parts.Count - 1;
        ImageNote = ShowsFullImage ? ""
            : frame.ImageId is not null && !_full.ContainsKey(_index) ? _strings["ng.image.loading"]
            : _strings["ng.image.live_only"];

        // The failed checks first, worst first, in the runtime's words; the passed ones counted.
        var failed = frame.Graphics.Where(g => g.Verdict is not (Verdict.Ok or Verdict.Info or Verdict.None))
            .OrderByDescending(g => Tones.Rank(g.Verdict)).ToList();
        foreach (var g in failed.Take(MaxListed))
            Checks.Add(new CheckLine(Tones.For(g.Verdict), Icons.For(g.Verdict),
                GraphicHit.Text(g, frame.Codes, _strings) ?? _strings[WordFor(g.Verdict)]));
        if (failed.Count > MaxListed)
            Checks.Add(new CheckLine(Tone.Neutral, Icons.Info, _strings.Format("ng.more", failed.Count - MaxListed)));
        var passed = frame.Graphics.Count(g => g.Verdict == Verdict.Ok);
        if (passed > 0) Checks.Add(new CheckLine(Tone.Ok, Icons.Check, _strings.Format("ng.passed", passed)));
        if (Checks.Count == 0) Checks.Add(new CheckLine(part.Tone, part.Icon, _strings[WordFor(frame.Result)]));
        foreach (var line in Measured(frame, _strings)) Checks.Add(line);
        ShowReview();
    }

    /// <summary>
    /// What the tools measured on the part, after the checks: each Check text tool's print quality
    /// (in the colour of its pass or fail) and each code's EAN/UPC add-on.
    /// </summary>
    internal static IEnumerable<CheckLine> Measured(LiveFrame frame, Strings strings)
    {
        foreach (var t in frame.Tools ?? [])
            if (t.PrintQuality is { } quality)
                yield return new CheckLine(ToolMark.Tone(t.Pass, t.ReportOnly), ToolMark.Icon(t.Pass, t.ReportOnly),
                    strings.Format("ng.print_quality", t.Name, quality.ToString("0", CultureInfo.CurrentCulture)));
        foreach (var c in frame.Codes ?? [])
            if (c.AddOn.Length > 0)
                yield return new CheckLine(Tone.Neutral, Icons.Info, strings.Format("ng.addon", c.Text, c.AddOn));
    }

    private static string WordFor(Verdict v) => v switch
    {
        Verdict.Duplicate => "result.duplicate",
        Verdict.Warn => "result.warn",
        Verdict.Ok => "result.ok",
        _ => "result.ng",
    };

    /// <summary>Asks the runtime for the full image of the part shown, once per part.</summary>
    public async Task LoadAsync()
    {
        if (_parts.Count == 0) return;
        var index = _index;
        if (_full.ContainsKey(index) || _parts[index].Frame.ImageId is not { } id) return;
        var (_, image) = await _calls.TryAsync((c, ct) => c.GetImageAsync(id, ct));
        var decoded = image is null ? null : _decode(image.Data);
        _full[index] = decoded;
        if (index == _index) Show();
    }

    public void TapImage(Point2 at, double slop)
    {
        var hit = GraphicHit.Find(Graphics, at, slop);
        TooltipText = hit is null ? null : GraphicHit.Text(hit, _parts.Count == 0 ? null : _parts[_index].Frame.Codes, _strings);
        TooltipAt = TooltipText is null ? null : at;
    }

    [RelayCommand]
    private async Task Previous()
    {
        if (_index == 0) return;
        _index--;
        Show();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Next()
    {
        if (_index >= _parts.Count - 1) return;
        _index++;
        Show();
        await LoadAsync();
    }

    [RelayCommand]
    private void Back() => _overlays.CloseIf(this);
}

public sealed partial class HelpStep(int number, string text) : ObservableObject
{
    public int Number { get; } = number;
    public string NumberText => Number.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string Text { get; } = text;
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// "What do I do?" (UI-UX-SPEC.md 4.4), first form: the alarm and its steps from the runtime's
/// help text, one per line; "Done" ticks a step and moves to the next. "Try again", "Call
/// supervisor" and "Read aloud" need runtime calls the contract does not have yet.
/// </summary>
public sealed partial class AlarmHelpViewModel : ObservableObject
{
    private static readonly Regex Numbering = new(@"^\s*(\d+[.)]|[-•*])\s*");
    private readonly Action _close;

    public AlarmHelpViewModel(Strings strings, string alarm, Tone tone, string help, Action close)
    {
        _close = close;
        Title = strings["alarm.help.title"];
        Alarm = alarm;
        Tone = tone;
        var lines = help.Split('\n').Select(l => Numbering.Replace(l.Trim(), "").Trim()).Where(l => l.Length > 0).ToList();
        for (var i = 0; i < lines.Count; i++) Steps.Add(new HelpStep(i + 1, lines[i]));
        if (Steps.Count > 0) Steps[0].IsCurrent = true;
    }

    public string Title { get; }
    public string Alarm { get; }
    public Tone Tone { get; }
    public ObservableCollection<HelpStep> Steps { get; } = [];

    [RelayCommand]
    private void Done(HelpStep step)
    {
        step.Done = !step.Done;
        var current = Steps.FirstOrDefault(s => !s.Done);
        foreach (var s in Steps) s.IsCurrent = s == current;
    }

    [RelayCommand]
    private void Back() => _close();
}
