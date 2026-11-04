using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>One value of the batch as the operator types it.</summary>
public sealed partial class BatchFieldEntry(BatchEntry entry) : ObservableObject
{
    public string Key { get; } = entry.Key;
    public string Label { get; } = entry.Label;
    public string Example { get; } = entry.Example;
    public string Entered { get; } = entry.Value;
    [ObservableProperty] private string _value = entry.Value;
}

/// <summary>
/// Start batch: the values the job checks against (lot, dates, MRP), typed before the line starts.
/// Every value is needed (the runtime refuses Start without them); a value is sent only when it
/// changed. The runtime refuses a change while the line runs: values changed mid-run would judge
/// parts against two batches.
/// </summary>
public sealed partial class BatchViewModel : ObservableObject
{
    private readonly Strings _strings;
    private readonly RuntimeCalls _calls;
    private readonly Action _close;
    private readonly Func<Task>? _then;

    /// <param name="then">Called after the values are saved (Start pressed with the batch missing starts the line).</param>
    public BatchViewModel(Strings strings, RuntimeCalls calls, IReadOnlyList<BatchEntry> batch, Action close, Func<Task>? then = null)
    {
        _strings = strings;
        _calls = calls;
        _close = close;
        _then = then;
        foreach (var b in batch) Fields.Add(new BatchFieldEntry(b));
    }

    public ObservableCollection<BatchFieldEntry> Fields { get; } = [];
    public string Title => _strings["batch.title"];
    public bool HasFields => Fields.Count > 0;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _busy;

    [RelayCommand]
    private async Task Save()
    {
        var missing = Fields.Where(f => string.IsNullOrWhiteSpace(f.Value)).Select(f => f.Label).ToList();
        if (missing.Count > 0)
        {
            Message = _strings.Format("batch.missing", string.Join(", ", missing));
            return;
        }
        Busy = true;
        try
        {
            foreach (var f in Fields.Where(f => f.Value.Trim() != f.Entered))
            {
                var (reached, result) = await _calls.TryDesignAsync((c, ct) => c.SetBatchAsync(f.Key, f.Value.Trim(), ct));
                if (!reached) { Message = _strings["run.reason.not_connected"]; return; }
                if (result is not { Ok: true })
                {
                    Message = _strings.KeyOrText(result?.ReasonKey, result?.ReasonText, "batch.reason.refused");
                    return;
                }
            }
        }
        finally { Busy = false; }
        _close();
        if (_then is not null) await _then();
    }

    [RelayCommand]
    private void Back() => _close();
}
