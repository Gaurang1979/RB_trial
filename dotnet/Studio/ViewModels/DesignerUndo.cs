using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// Undo and redo in the job designer (UI-UX-SPEC.md principle 9 and 5 "Undo / Redo", requirement AS-18): every
/// create, move, resize, setting change and delete of the draft is one step in <see cref="DraftHistory"/>. A tool
/// dropped from the toolbox is one step with its snapping and teaching, and so is "Accept all". The history is of
/// the job open: opening, making or saving as another job clears it. Only the draft changes, until Save. The history
/// is kept across a restart of Vision Studio for the draft (<see cref="DraftHistoryStore"/>): opening the job again
/// brings it back, unless the job was saved, reloaded or closed since, or its saved file changed outside Vision Studio.
/// The accepted auto find suggestions are kept with it, so an undo of Accept behaves the same after the restart.
/// </summary>
public sealed partial class DesignerViewModel
{
    private readonly DraftHistory _history = new();
    /// <summary>Above 0 while an edit of several parts runs (a drop, Accept all): it is recorded as one step when it ends.</summary>
    private int _historyHeld;
    /// <summary>The saved job the kept history is filed under; null for a job never saved (not kept).</summary>
    private string? _historyKey;
    /// <summary>The history changed since it was last kept.</summary>
    private bool _historyToKeep;
    /// <summary>The job open was last saved here, not opened as it was: the kept history is matched by its version.</summary>
    private bool _historySavedHere;
    private long _historyKeptAt = long.MinValue / 2;

    /// <summary>Where the history is kept across a restart; the tests give their own.</summary>
    public DraftHistoryStore HistoryStore { get; init; } = DraftHistoryStore.Shared;

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    /// <summary>The draft as a step: without the saved version number, which Save gives and undo never takes back.</summary>
    private string Snapshot()
    {
        var root = (JsonObject)Job!.Root.DeepClone();
        root.Remove("Version");
        return root.ToJsonString();
    }

    /// <summary>The draft now as a step, unless it is the step already or an edit of several parts is running.
    /// Typing: a setting typed into (one step while the same setting is typed). Commands call it before they change
    /// the draft too, so a setting typed a moment before (not yet seen by Watch) stays a step of its own.</summary>
    private void Record(bool typing = false)
    {
        if (Job is null || _historyHeld > 0) return;
        if (_history.Record(Snapshot(), typing)) NotifyHistory();
    }

    /// <summary>Another job is open: its history starts here.</summary>
    private void ResetHistory()
    {
        _history.Reset(Job is null ? null : Snapshot());
        NotifyHistory();
    }

    private void NotifyHistory()
    {
        _historyToKeep = true;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    /// <summary>The history kept for a restart when it changed, at most once a second unless <paramref name="now"/>
    /// (called by <see cref="Watch"/>, which the main window calls about once a second): with no step to undo or
    /// redo, nothing is kept.</summary>
    internal void KeepHistory(bool now = false)
    {
        if (!_historyToKeep || _historyHeld > 0) return;
        var at = Environment.TickCount64;
        if (!now && at - _historyKeptAt < 1000) return;
        _historyKeptAt = at;
        _historyToKeep = false;
        if (Job is null || _historyKey is null || _savedJson is null) return;
        if (!_history.CanUndo && !_history.CanRedo) { HistoryStore.Drop(_historyKey); return; }
        var (undo, current, redo) = _history.Export();
        if (current is null) return;
        HistoryStore.Save(_historyKey, new KeptDraft(_historySavedHere ? null : DraftHistoryStore.Hash(_savedJson),
            _historySavedHere ? Job.Version : 0, undo, current, redo,
            [.. _notSnapped.Select(n => new KeptArea(n.Key.Camera, n.Key.Name, n.Value.X, n.Value.Y, n.Value.Width, n.Value.Height))],
            KeptAccepted(), _accepted.Count > 0 && PictureIndex >= 0 ? Pictures[PictureIndex] : null));
    }

    /// <summary>The kept history of the job open is dropped (saved, reloaded, another job opened, closed).</summary>
    private void ForgetKeptHistory()
    {
        if (_historyKey is not null) HistoryStore.Drop(_historyKey);
        _historyKey = null;
        _historyToKeep = false;
        _historySavedHere = false;
    }

    /// <summary>Saved here under <paramref name="key"/>: nothing unsaved to bring back; the history goes on and is kept
    /// again from its next step, matched by the version this save gave.</summary>
    private void KeptHistorySaved(string key)
    {
        ForgetKeptHistory();
        _historyKey = key;
        _historySavedHere = true;
    }

    /// <summary>The job just opened under <paramref name="key"/>: its draft and steps kept from before a restart come
    /// back when they started from this very saved file; else they are dropped (the job changed outside Vision Studio,
    /// or the file cannot be read) and the job opens as saved. True when restored.</summary>
    private bool RestoreHistory(string key)
    {
        _historyKey = key;
        if (HistoryStore.Load(key) is not { } kept) return false;
        var same = kept.Saved is not null ? kept.Saved == DraftHistoryStore.Hash(_savedJson!) : kept.SavedVersion == Job!.Version;
        if (!same || !Readable(kept.Current))
        {
            HistoryStore.Drop(key);
            return false;
        }
        _historySavedHere = kept.Saved is null;
        foreach (var a in kept.NotSnapped) _notSnapped[(a.Camera, a.Name)] = (a.X, a.Y, a.Width, a.Height);   // "!" stays
        _history.Restore(kept.Undo, kept.Current, kept.Redo);
        Apply(kept.Current);
        KeepAcceptedForPictures(kept);   // an undo of Accept offers the suggestion again, as before the restart
        NotifyHistory();
        _historyToKeep = false;   // as kept
        return true;
    }

    private static bool Readable(string state)
    {
        try { JobDraft.Parse(state); return true; }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { return false; }
    }

    /// <summary>Runs an edit of several parts as one step.</summary>
    private async Task OneStepAsync(Func<Task> edit)
    {
        Record(typing: true);   // a setting typed just before is a step of its own
        _historyHeld++;
        try { await edit(); }
        finally
        {
            _historyHeld--;
            Record();
        }
    }

    /// <summary>Takes the last step back (Ctrl+Z).</summary>
    [RelayCommand]
    private void Undo() => Step(back: true);

    /// <summary>Puts the step last taken back in again (Ctrl+Y, Ctrl+Shift+Z).</summary>
    [RelayCommand]
    private void Redo() => Step(back: false);

    private void Step(bool back)
    {
        if (Job is null || Busy || _historyHeld > 0) return;
        Record(typing: true);   // a setting typed a moment ago and not yet seen by Watch is the step taken back
        var state = back ? _history.Undo() : _history.Redo();
        NotifyHistory();
        if (state is null) return;
        Apply(state);
    }

    /// <summary>A step's draft replaces the draft: the same camera (the last one when it is gone), picture and tool
    /// stay shown; a tool the step brings back (a delete undone, an add redone) is selected.</summary>
    private void Apply(string state)
    {
        var job = JobDraft.Parse(state);
        if (Job!.Root["Version"] is { } version) job.Root["Version"] = version.DeepClone();
        var camera = Math.Min(CameraIndex, job.Cameras.Count - 1);
        var sameCamera = camera == CameraIndex;
        var picture = PictureIndex;
        var tool = SelectedTool?.Name;
        var before = CameraTools.Select(t => t.Name).ToHashSet();
        _keepPictures = sameCamera;
        try
        {
            SetJob(job);
            CameraIndex = camera;
        }
        finally { _keepPictures = false; }
        if (sameCamera) PictureIndex = picture;
        SelectedTool = (sameCamera ? CameraTools.FirstOrDefault(t => !before.Contains(t.Name)) : null)
                       ?? CameraTools.FirstOrDefault(t => t.Name == tool) ?? CameraTools.FirstOrDefault();
        if (sameCamera) ReconcileSuggestions(before);
        OnPropertyChanged(nameof(Dirty));
        OnPropertyChanged(nameof(StatusText));
        ShowSelection();
        DrawGraphics();
    }
}
