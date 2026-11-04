using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// The one-click new job (owner, 2026-10-31; UI-UX-SPEC.md 4.6 steps 2-3, requirement AS-01): Grab takes a
/// picture with the camera, Find everything (or a window dragged on the picture) asks auto find what to check,
/// and each suggestion is a ready tool the engineer accepts or leaves out. Nothing found is added to the job
/// until it is accepted (line safety, AS-12); the accepted tools are tried at once so their results show.
/// </summary>
public sealed partial class DesignerViewModel
{
    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];
    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>Suggestions accepted as a tool of the camera shown, by the tool's name: an undo that takes the tool
    /// away offers the suggestion again, and a redo takes it off the list (AS-18). Forgotten with the list; kept with
    /// the history across a restart.</summary>
    private readonly List<(int Camera, string Tool, SuggestionItem Item)> _accepted = [];

    /// <summary>The next window dragged on the picture asks auto find instead of setting the tool's area.</summary>
    [ObservableProperty] private bool _findInWindow;

    partial void OnFindInWindowChanged(bool value)
    {
        if (value) Say("design.find_window_hint", Tone.Info);
    }

    /// <summary>A picture from the camera, kept with its stored pictures; then auto find on it.</summary>
    [RelayCommand]
    private async Task GrabAsync()
    {
        if (Job is null || CameraIndex < 0) return;
        Busy = true;
        string name;
        try
        {
            var camera = CameraIndex;
            var (reached, g) = await _calls.TryDesignAsync((c, ct) => c.GrabPictureAsync(Job.ToJson(), camera, ct), TryLimit);
            if (!reached) { Say("run.reason.not_connected", Tone.Ng); return; }
            if (g is not { Ok: true }) { Refused(g?.Refused); return; }
            // a camera with no pictures folder has one now: keep it in the draft
            if (Camera is { } cam && cam.TestFolder.Length == 0 && g.Json.Length > 0
                && JobDraft.Parse(g.Json).Cameras.ElementAtOrDefault(camera) is { TestFolder.Length: > 0 } got)
            {
                cam.TestFolder = got.TestFolder;
                Changed();
            }
            name = g.Name;
        }
        finally { Busy = false; }
        await LoadPicturesAsync();
        PictureIndex = Pictures.IndexOf(name);
        SayText(_strings.Format("design.grabbed", name), Tone.Ok);
        // a camera with tools shows them on the new picture; one with none is asked what to check
        if (CameraTools.Count > 0) await TryAsync();
        else await FindAsync(null);
    }

    /// <summary>Every code and text on the picture shown, each offered as a tool.</summary>
    [RelayCommand]
    private Task FindEverythingAsync() => FindAsync(null);

    private async Task FindAsync((int X, int Y, int Width, int Height)? area)
    {
        if (Job is null || CameraIndex < 0) return;
        if (PictureIndex < 0) { Say("design.no_pictures", Tone.Warn); return; }
        Busy = true;
        try
        {
            var json = Job.ToJson();
            var camera = CameraIndex;
            var picture = Pictures[PictureIndex];
            var (reached, f) = await _calls.TryDesignAsync((c, ct) => c.FindToolsAsync(json, camera, picture, area, ct), TryLimit);
            if (!reached) { Say("run.reason.not_connected", Tone.Ng); return; }
            if (f is not { Ok: true }) { Refused(f?.Refused); return; }
            Suggestions.Clear();
            ForgetAccepted();
            foreach (var s in f.Suggestions ?? []) Suggestions.Add(new SuggestionItem(s));
            OnPropertyChanged(nameof(HasSuggestions));
            // the picture, also when the camera has no tool yet to try it with
            if (_last is null || CameraTools.Count == 0) ShowFound(f);
            ShowSuggestions();   // the greyed rows, and the numbers after the tools on the picture
            var ms = f.TimeMs.ToString("0", CultureInfo.CurrentCulture);
            if (Suggestions.Count == 0) Say("design.found_none", Tone.Warn);
            else SayText(_strings.Format("design.found", Suggestions.Count, ms), Tone.Info);
        }
        finally { Busy = false; }
    }

    /// <summary>The picture from a find, with no tool results on it.</summary>
    private void ShowFound(FindOutcome f)
    {
        _last = new TryOutcome(true, false, 0, f.Jpeg, f.Width, f.Height, f.Scale, f.ImageWidth, f.ImageHeight, [], [], [], []);
        ShowPicture(_last);   // the reduced picture; a full one of an earlier try is dropped (AS-17)
        ImageWidth = Math.Max(1, f.Width);
        ImageHeight = Math.Max(1, f.Height);
        FrameTone = Tone.Neutral;
        Results.Clear();
        LinesRead.Clear();
        Summary = "";
        DrawGraphics();
    }

    /// <summary>The suggestions' outlines on the shown picture, numbered after the camera's tools, as the greyed
    /// rows of the "Areas and values" panel are (AS-15): a picture with 3 tools numbers its suggestions from 4.</summary>
    private IEnumerable<ResultGraphic> SuggestionGraphics(double scale) =>
        Suggestions.Select((s, i) => new ResultGraphic(GraphicKind.Polygon,
            s.Suggestion.Outline.Select(p => new Point2(p.X * scale, p.Y * scale)).ToArray(), Verdict.Info,
            $"{CameraTools.Count + i + 1}", s.Suggestion.Summary));

    [RelayCommand]
    private async Task AcceptSuggestionAsync(SuggestionItem item)
    {
        if (!Add(item)) return;
        await AfterAcceptAsync(item.Suggestion.Summary);
    }

    [RelayCommand]
    private async Task AcceptAllSuggestionsAsync()
    {
        var added = new List<string>();
        await OneStepAsync(() =>   // every tool accepted is one undo step
        {
            added.AddRange(Suggestions.ToList().Where(Add).Select(s => s.Suggestion.Summary));
            return Task.CompletedTask;
        });
        if (added.Count > 0) await AfterAcceptAsync(string.Join(", ", added));
    }

    [RelayCommand]
    private void DismissSuggestion(SuggestionItem item)
    {
        if (Suggestions.Remove(item) && _accepted.Any(a => a.Item == item)) _historyToKeep = true;   // no longer offered
        OnPropertyChanged(nameof(HasSuggestions));
        ShowSuggestions();
    }

    [RelayCommand]
    private void DismissAllSuggestions()
    {
        Suggestions.Clear();
        ForgetAccepted();
        OnPropertyChanged(nameof(HasSuggestions));
        ShowSuggestions();
    }

    // the suggestion as a tool of the camera shown; false when it cannot be read
    private bool Add(SuggestionItem item)
    {
        if (Job is null || CameraIndex < 0) return false;
        Record(typing: true);   // a setting typed just before is a step of its own
        ToolDraft tool;
        try { tool = Job.AddToolJson(item.Suggestion.ToolJson, CameraIndex); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { SayText(e.Message, Tone.Ng); return false; }
        Suggestions.Remove(item);
        _accepted.Add((CameraIndex, tool.Name, item));
        OnPropertyChanged(nameof(HasSuggestions));
        RefreshTools();
        ShowSuggestions();   // one suggestion fewer, and the rest renumbered after the new tool
        SelectedTool = tool;
        Changed();
        return true;
    }

    /// <summary>After an undo or redo of the camera shown: a tool this step took away that was an accepted suggestion
    /// is offered again; one it put back leaves the list. Tools that were not accepted from a suggestion are left.</summary>
    private void ReconcileSuggestions(ISet<string> before)
    {
        var now = CameraTools.Select(t => t.Name).ToHashSet();
        var changed = false;
        foreach (var (camera, tool, item) in _accepted.Where(a => a.Camera == CameraIndex))
        {
            if (before.Contains(tool) && !now.Contains(tool) && !Suggestions.Contains(item)) { Suggestions.Add(item); changed = true; }
            else if (!before.Contains(tool) && now.Contains(tool) && Suggestions.Remove(item)) changed = true;
        }
        if (!changed) return;
        OnPropertyChanged(nameof(HasSuggestions));
        ShowSuggestions();
    }

    private void ForgetAccepted()
    {
        if (_accepted.Count == 0) return;
        _accepted.Clear();
        _historyToKeep = true;   // the kept history no longer holds them
    }

    /// <summary>The accepted suggestions as kept with the history, with whether each is offered again now.</summary>
    private List<KeptSuggestion> KeptAccepted() =>
        [.. _accepted.Select(a => new KeptSuggestion(a.Camera, a.Tool, a.Item.Suggestion, Suggestions.Contains(a.Item)))];

    /// <summary>The accepted suggestions kept from before a restart, until the pictures of their camera are listed.</summary>
    private (int Camera, string? Picture, IReadOnlyList<KeptSuggestion> Items)? _keptAccepted;

    private void KeepAcceptedForPictures(KeptDraft kept) =>
        _keptAccepted = kept.Accepted is { Count: > 0 } a ? (a[0].Camera, kept.AcceptedOn, a) : null;

    /// <summary>Opening the job: the camera the kept suggestions were found with is shown, its pictures not yet listed.</summary>
    private void ShowAcceptedCamera()
    {
        if (_keptAccepted is not { } k || k.Camera == CameraIndex || k.Camera >= Cameras.Count) return;
        _keepPictures = true;
        try { CameraIndex = k.Camera; }
        finally { _keepPictures = false; }
    }

    /// <summary>The pictures listed: the accepted suggestions kept from before a restart come back on the picture they
    /// were found on, those offered again then listed again, and an undo or redo of their Accept goes on as before
    /// (AS-18). With that picture or camera gone they are forgotten, as a change of picture forgets them.</summary>
    private void RestoreAccepted()
    {
        if (_keptAccepted is not { } k) return;
        _keptAccepted = null;
        var at = k.Picture is null ? -1 : Pictures.IndexOf(k.Picture);
        if (k.Camera != CameraIndex || at < 0) { ForgetAccepted(); _historyToKeep = true; return; }
        Suggestions.Clear();
        PictureIndex = at;
        _accepted.Clear();
        foreach (var kept in k.Items)
        {
            var item = new SuggestionItem(kept.Suggestion);
            _accepted.Add((kept.Camera, kept.Tool, item));
            if (kept.Offered) Suggestions.Add(item);
        }
        OnPropertyChanged(nameof(HasSuggestions));
        ShowSuggestions();
    }

    // the accepted tools tried on the picture, so the engineer checks what they read before saving
    private async Task AfterAcceptAsync(string what)
    {
        await TryAsync();
        SayText(_strings.Format("design.accepted", what), Tone.Ok);
    }
}

/// <summary>One suggestion in the list: what auto find read, each with its field type ("MFG 10/2026 = production date").</summary>
public sealed class SuggestionItem(ToolSuggestion suggestion)
{
    public ToolSuggestion Suggestion { get; } = suggestion;
    public string Summary => Suggestion.Summary;
    public string ReadText => string.Join("\n", Suggestion.Read.Zip(Suggestion.Fields,
        (read, kind) => kind == "code" || kind.Length == 0 ? read : $"{read}  ·  {kind}"));
}
