using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>Which saved pictures the sorting screen shows: WasOk, the bad ones an operator marked "Was OK"; Reviewed,
/// the pictures already decided, to change or remove a decision (AS-04j, AS-04k).</summary>
public enum SortFilter { All, Good, Bad, WasOk, Reviewed }

/// <summary>
/// The learning session (owner, 2026-11-04): "Once supervisor, support or OEM team start learning with any button ...
/// segregate the images and show images in almost full screen mode with zoom, pan, accept, reject, back, next
/// buttons. Still NG image which can not be read will ask for second validation from user and still can skip the
/// learning for good to avoid wrong product accept." The saved good and bad pictures of the open job, one at a
/// time, nearly full screen: Accept (the part is good), Reject (the part is bad), Back, Next and Skip. The
/// runtime decides what each answer teaches:
/// <list type="bullet">
/// <item>a bad picture the software found clearly bad, accepted all the same, is a record only;</item>
/// <item>a bad picture it could not read is validated a second time: good (it teaches the job) or a record only;</item>
/// <item>a good picture rejected is a possible escape: the plant is told and the job learns to be stricter.</item>
/// </list>
/// Then Learn works out settings from the sorted pictures, with the acceptable % and not acceptable % before and
/// after, and Apply saves them as a new job version with the line stopped, as the NG review does. Supervisor, QA,
/// Engineer and Admin only (the runtime checks each call); the setting that turns the session on is here too.
/// <para>The review follow-ups (AS-04j, AS-04k): the filter "Was OK" shows the bad parts an operator marked, and
/// "Reviewed" the pictures already decided, newest first, with who and when: Accept or Reject changes a decision by the
/// same rules (the runtime's double-check and second validation), Remove takes it out. The reviewed list is shown
/// whatever the setting.</para>
/// </summary>
public sealed partial class SortViewModel : ObservableObject
{
    private readonly Strings _strings;
    private readonly RuntimeCalls _calls;
    private readonly Func<byte[], IImage?> _decode;
    private readonly IOverlayHost _overlays;
    private readonly Dictionary<string, IImage?> _images = [];
    private readonly Dictionary<string, string> _decided = [];
    private List<SortPicture> _all = [];
    private List<SortPicture> _shown = [];
    private List<SortPicture> _reviewed = [];
    private int _reviewedTotal;
    private int _index;
    private int _total;
    private string _proposalId = "";

    public SortViewModel(Strings strings, RuntimeCalls calls, Func<byte[], IImage?> decode, IOverlayHost overlays)
    {
        _strings = strings;
        _calls = calls;
        _decode = decode;
        _overlays = overlays;
    }

    public ObservableCollection<string> Faults { get; } = [];
    public ObservableCollection<string> CheckFaults { get; } = [];
    public ObservableCollection<LearnLine> LearnLines { get; } = [];

    [ObservableProperty] private IImage? _image;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private Tone _tone = Tone.Neutral;
    [ObservableProperty] private string _icon = Icons.Camera;
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private string _imageNote = "";
    [ObservableProperty] private bool _hasPicture;
    [ObservableProperty] private bool _hasFaults;
    [ObservableProperty] private bool _hasPrevious;
    [ObservableProperty] private bool _hasNext;
    [ObservableProperty] private SortFilter _filter;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private Tone _messageTone = Tone.Info;
    /// <summary>The runtime asked to look again: the software found the part clearly bad, or could not read it.</summary>
    [ObservableProperty] private bool _doubleCheck;
    [ObservableProperty] private bool _unreadable;
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private bool _hasLearning;
    [ObservableProperty] private string _learnText = "";
    [ObservableProperty] private bool _canApplyLearning;
    /// <summary>Learning runs: Stop learning cancels it (AS-04h).</summary>
    [ObservableProperty] private bool _learning;
    private CancellationTokenSource? _learnStop;
    /// <summary>The setting: the learning session (keeping good pictures, the sorting) on or off, and the idle minutes.</summary>
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private decimal _idleMinutes = 5;
    [ObservableProperty] private string _settingsText = "";
    /// <summary>"Ramesh marked this part Was OK at 14:32", or empty.</summary>
    [ObservableProperty] private string _flagText = "";
    /// <summary>The decision kept for a picture of the reviewed list, with who and when, or empty.</summary>
    [ObservableProperty] private string _reviewedText = "";
    [ObservableProperty] private bool _isReviewed;

    public SortPicture? Current => _index >= 0 && _index < _shown.Count ? _shown[_index] : null;

    /// <summary>Loads the setting and the pictures waiting to be sorted, and shows the first.</summary>
    public async Task LoadAsync()
    {
        var (gotSettings, settings) = await _calls.TryReviewAsync((c, ct) => c.GetLearningSettingsAsync(ct));
        if (gotSettings && settings is not null) ShowSettings(settings);
        var (reached, list) = await _calls.TryReviewAsync((c, ct) => c.ListToSortAsync(-1, ct));
        if (!reached || list is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
        if (!list.Result.Ok) { Say(_strings.KeyOrText(list.Result.ReasonKey, list.Result.ReasonText, "run.reason.refused"), Tone.Ng); return; }
        _all = list.Items.ToList();
        _total = list.Total;
        if (Filter == SortFilter.Reviewed) await LoadReviewedAsync();
        Apply(0);
        // the setting off: no picture is offered (AS-04b); the screen stays open to turn it on; the reviewed list stays
        Say(Filter == SortFilter.Reviewed ? (_reviewed.Count == 0 ? _strings["sort.reviewed.none"] : "")
            : !Enabled ? _strings["sort.off"] : _shown.Count == 0 ? _strings["sort.none"] : "", Tone.Info);
        await LoadImageAsync();
    }

    /// <summary>The reviewed pictures of the open job, newest decision first (AS-04j).</summary>
    private async Task LoadReviewedAsync()
    {
        var (reached, list) = await _calls.TryReviewAsync((c, ct) => c.ListReviewedAsync(-1, ct));
        if (!reached || list is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
        if (!list.Result.Ok) { Say(_strings.KeyOrText(list.Result.ReasonKey, list.Result.ReasonText, "run.reason.refused"), Tone.Ng); return; }
        _reviewed = list.Items.ToList();
        _reviewedTotal = list.Total;
    }

    private void ShowSettings(LearningSettingsInfo s)
    {
        Enabled = s.Enabled;
        IdleMinutes = s.IdleMinutes;
        SettingsText = s.ChangedBy.Length == 0 ? ""
            : _strings.Format("sort.settings.changed", s.ChangedBy, s.ChangedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "");
    }

    partial void OnFilterChanged(SortFilter value) => _ = ChangeFilterAsync(value);

    private async Task ChangeFilterAsync(SortFilter value)
    {
        if (value == SortFilter.Reviewed)
        {
            await LoadReviewedAsync();
            if (Filter != value) return;
        }
        Apply(0);
        if (value == SortFilter.Reviewed && _reviewed.Count == 0) Say(_strings["sort.reviewed.none"], Tone.Info);
        await LoadImageAsync();
    }

    private void Apply(int index)
    {
        _shown = Filter == SortFilter.Reviewed ? _reviewed
            : _all.Where(p => Filter switch
            {
                SortFilter.Good => p.Ok,
                SortFilter.Bad => !p.Ok,
                SortFilter.WasOk => !p.Ok && p.FlaggedBy.Length > 0,
                _ => true,
            }).ToList();
        _index = Math.Clamp(index, 0, Math.Max(0, _shown.Count - 1));
        Show();
    }

    private void Show()
    {
        DoubleCheck = false;
        Unreadable = false;
        CheckFaults.Clear();
        Faults.Clear();
        var p = Current;
        HasPicture = p is not null;
        HasFaults = false;
        HasPrevious = _index > 0;
        HasNext = _index < _shown.Count - 1;
        // all pictures: the runtime's total, beyond the 500 listed
        var of = Filter switch
        {
            SortFilter.All => Math.Max(_shown.Count, _total),
            SortFilter.Reviewed => Math.Max(_shown.Count, _reviewedTotal),
            _ => _shown.Count,
        };
        PositionText = _shown.Count == 0 ? "" : _strings.Format("ng.position", _index + 1, of);
        FlagText = p is { FlaggedBy.Length: > 0 }
            ? _strings.Format("sort.flagged", p.FlaggedBy, p.FlaggedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "") : "";
        IsReviewed = p?.Reviewed is not null;
        ReviewedText = p?.Reviewed is { } decided ? Decision(decided, _strings) : "";
        if (p is null)
        {
            Image = null;
            Title = _strings[Filter == SortFilter.Reviewed ? "sort.reviewed.none" : "sort.none"];
            Tone = Tone.Neutral;
            Icon = Icons.Info;
            ImageNote = "";
            OnPropertyChanged(nameof(Current));
            return;
        }
        Title = _strings.Format(p.Ok ? (p.NearLimit ? "sort.software.ok_near" : "sort.software.ok") : "sort.software.ng",
            p.CameraName, p.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        Tone = p.Ok ? Tone.Ok : Tone.Ng;
        Icon = p.Ok ? Icons.Check : Icons.Cross;
        foreach (var f in p.Faults) Faults.Add(f);
        HasFaults = Faults.Count > 0;
        Image = _images.GetValueOrDefault(p.ImageId);
        ImageNote = _images.ContainsKey(p.ImageId) ? (Image is null ? _strings["sort.image.gone"] : "") : _strings["ng.image.loading"];
        if (_decided.TryGetValue(p.ImageId, out var done)) Say(done, Tone.Ok);
        else if (MessageTone != Tone.Ng) Say("", Tone.Info);
        OnPropertyChanged(nameof(Current));
    }

    /// <summary>"Accepted, learned from: Ramesh, 04/11 14:32" (AS-04j).</summary>
    internal static string Decision(ReviewedInfo r, Strings strings) =>
        strings.Format(r.Decision == ReviewDecision.ConfirmedNg ? "sort.reviewed.ng" : r.Learned ? "sort.reviewed.ok" : "sort.reviewed.record",
            r.User.Length == 0 ? "–" : r.User, r.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

    private async Task LoadImageAsync()
    {
        if (Current is not { } p || _images.ContainsKey(p.ImageId)) return;
        var (_, image) = await _calls.TryAsync((c, ct) => c.GetImageAsync(p.ImageId, ct));
        _images[p.ImageId] = image is null ? null : _decode(image.Data);
        if (Current?.ImageId == p.ImageId) Show();
    }

    private void Say(string text, Tone tone)
    {
        Message = text;
        MessageTone = tone;
    }

    [RelayCommand]
    private Task Accept() => Decide(ReviewDecision.Accepted, ReviewConfirm.None);

    [RelayCommand]
    private Task Reject() => Decide(ReviewDecision.ConfirmedNg, ReviewConfirm.None);

    /// <summary>Looked at again and still good, but not to teach the job: kept as a record only.</summary>
    [RelayCommand]
    private Task KeepAsRecord() => Decide(ReviewDecision.Accepted, Unreadable ? ReviewConfirm.KeepAsRecord : ReviewConfirm.LooksBad);

    /// <summary>A part the software could not read, checked again and good: it teaches the job.</summary>
    [RelayCommand]
    private Task AcceptChecked() => Decide(ReviewDecision.Accepted, ReviewConfirm.Unreadable);

    [RelayCommand]
    private void CancelCheck()
    {
        DoubleCheck = false;
        Unreadable = false;
        CheckFaults.Clear();
    }

    private async Task Decide(ReviewDecision decision, ReviewConfirm confirm)
    {
        if (Busy || Current is not { } p) return;
        Busy = true;
        try
        {
            var (reached, r) = await _calls.TryReviewAsync((c, ct) => c.ReviewPartAsync(p.ImageId, decision, confirm, "", ct));
            if (Current?.ImageId != p.ImageId) return;
            if (!reached || r is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            if (r.LooksBad || r.Unreadable)
            {
                CheckFaults.Clear();
                foreach (var f in r.Faults) CheckFaults.Add(f);
                Unreadable = r.Unreadable;
                DoubleCheck = true;
                Say("", Tone.Warn);
                return;
            }
            if (!r.Result.Ok) { Say(_strings.KeyOrText(r.Result.ReasonKey, r.Result.ReasonText, "run.reason.refused"), Tone.Ng); return; }
            var text = _strings[r.PossibleEscape ? "sort.done.escape"
                : decision == ReviewDecision.ConfirmedNg ? "review.done.ng" : r.Learned ? "review.done.ok" : "review.done.record"];
            ShowCounts(r.Cameras, p.CameraName);
            if (Filter == SortFilter.Reviewed)
            {
                // a decision changed: the list as the runtime keeps it now (newest decision first), on the same picture
                await LoadReviewedAsync();
                Apply(Math.Max(0, _reviewed.FindIndex(x => x.ImageId == p.ImageId)));
                Say(text, r.PossibleEscape ? Tone.Warn : Tone.Ok);
                await LoadImageAsync();
                return;
            }
            _decided[p.ImageId] = text;
            // the next picture not decided yet; the decided one stays reachable with Back
            var next = _shown.FindIndex(_index + 1, x => !_decided.ContainsKey(x.ImageId));
            if (next >= 0) { _index = next; Show(); await LoadImageAsync(); }
            else Show();
            Say(text, r.PossibleEscape ? Tone.Warn : Tone.Ok);
        }
        finally { Busy = false; }
    }

    private void ShowCounts(IReadOnlyList<CameraReviewCounts> cameras, string name)
    {
        var c = cameras.FirstOrDefault(x => x.Name == name) ?? cameras.FirstOrDefault();
        CountsText = c is null ? "" : _strings.Format("review.counts", c.Accepted, c.Confirmed, c.RecordOnly);
    }

    [RelayCommand]
    private async Task Previous()
    {
        if (_index == 0) return;
        _index--;
        Show();
        await LoadImageAsync();
    }

    [RelayCommand]
    private async Task Next()
    {
        if (_index >= _shown.Count - 1) return;
        _index++;
        Show();
        await LoadImageAsync();
    }

    /// <summary>Leaves the picture unsorted and shows the next one.</summary>
    [RelayCommand]
    private Task Skip() => Next();

    [RelayCommand]
    private void Close()
    {
        _learnStop?.Cancel();
        _overlays.CloseIf(this);
    }

    /// <summary>Saves the setting (Supervisor and above; the runtime checks it).</summary>
    [RelayCommand]
    private async Task SaveSettings()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var (reached, r) = await _calls.TryReviewAsync((c, ct) => c.SetLearningSettingsAsync(Enabled, (int)IdleMinutes, ct));
            if (!reached || r is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            if (!r.Ok) { Say(_strings.KeyOrText(r.ReasonKey, r.ReasonText, "run.reason.refused"), Tone.Ng); return; }
            var (got, s) = await _calls.TryReviewAsync((c, ct) => c.GetLearningSettingsAsync(ct));
            if (got && s is not null) ShowSettings(s);
            Say(_strings["sort.settings.saved"], Tone.Ok);
        }
        finally { Busy = false; }
    }

    /// <summary>Start learning: settings worked out from the sorted pictures of the camera shown (or the first
    /// camera with sorted pictures), proved on them, with the acceptable and not acceptable percentages.</summary>
    [RelayCommand]
    private async Task Learn()
    {
        if (Busy) return;
        Busy = true;
        LearnLines.Clear();
        HasLearning = false;
        CanApplyLearning = false;
        _proposalId = "";
        using var stop = new CancellationTokenSource();
        _learnStop = stop;
        Learning = true;
        try
        {
            var (reached, cameras) = await _calls.TryReviewAsync((c, ct) => c.GetReviewsAsync(ct));
            if (!reached || cameras is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            var camera = Current?.Camera
                ?? cameras.FirstOrDefault(c => c.Accepted > 0 && c.Confirmed > 0)?.Camera
                ?? cameras.FirstOrDefault()?.Camera ?? 0;
            Say(_strings["review.learning"], Tone.Info);
            var (got, r) = await _calls.TryReviewAsync((c, ct) => c.LearnAsync(camera, ct), GrpcRuntimeClient.LearnTimeout, stop.Token);
            if (stop.IsCancellationRequested) { Say(_strings["sort.learn.stopped"], Tone.Warn); return; }
            if (!got || r is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            if (!r.Result.Ok) { Say(_strings.KeyOrText(r.Result.ReasonKey, r.Result.ReasonText, "run.reason.refused"), Tone.Ng); return; }
            HasLearning = true;
            foreach (var s in r.Settings)
                LearnLines.Add(new LearnLine(_strings.Format(s.Looser ? "review.setting.looser" : "review.setting.stricter",
                    s.Tool, s.Setting, s.Current, s.Proposed), s.Looser));
            var parts = new List<string>();
            if (NgViewerViewModel.Percentages(r, _strings) is { } pct) parts.Add(pct);
            if (r.Before.Length > 0) parts.Add(_strings.Format("review.before", r.Before));
            if (r.After.Length > 0) parts.Add(_strings.Format("review.after", r.After));
            if (r.Problem.Length > 0) parts.Add(r.Problem);
            if (r.StillRejected.Count > 0) parts.Add(_strings.Format("review.still_rejected", string.Join(", ", r.StillRejected.Take(8))));
            LearnText = string.Join("\n", parts);
            _proposalId = r.ProposalId;
            CanApplyLearning = r.CanApply && r.ProposalId.Length > 0;
            Say(_strings[CanApplyLearning ? "review.learned" : "review.nothing_to_apply"], CanApplyLearning ? Tone.Ok : Tone.Warn);
        }
        finally
        {
            _learnStop = null;
            Learning = false;
            Busy = false;
        }
    }

    /// <summary>Stops learning: the runtime ends it at once and nothing is proposed.</summary>
    [RelayCommand]
    private void StopLearning() => _learnStop?.Cancel();

    /// <summary>The proposal becomes a new job version (refused while the line runs).</summary>
    [RelayCommand]
    private async Task ApplyLearning()
    {
        if (!CanApplyLearning || Busy) return;
        Busy = true;
        try
        {
            var (reached, r) = await _calls.TryReviewAsync((c, ct) => c.ApplyLearningAsync(_proposalId, ct));
            if (!reached || r is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            if (!r.Result.Ok) { Say(_strings.KeyOrText(r.Result.ReasonKey, r.Result.ReasonText, "run.reason.refused"), Tone.Ng); return; }
            CanApplyLearning = false;
            _proposalId = "";
            Say(_strings.Format("review.applied", r.Job, r.Version), Tone.Ok);
        }
        finally { Busy = false; }
    }

    /// <summary>Removes the decision on the reviewed picture shown (AS-04j): it no longer teaches the job, and a
    /// learning proposal made with it cannot be applied.</summary>
    [RelayCommand]
    private async Task RemoveReview()
    {
        if (Busy || Current is not { Reviewed: not null } p) return;
        Busy = true;
        try
        {
            var (reached, r) = await _calls.TryReviewAsync((c, ct) => c.RemoveReviewAsync(p.ImageId, ct));
            if (!reached || r is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            if (!r.Result.Ok) { Say(_strings.KeyOrText(r.Result.ReasonKey, r.Result.ReasonText, "run.reason.refused"), Tone.Ng); return; }
            ShowCounts(r.Cameras, p.CameraName);
            _decided.Remove(p.ImageId);
            var at = _index;
            await LoadReviewedAsync();
            // the picture waits for a review again while its saved picture is kept
            var (got, list) = await _calls.TryReviewAsync((c, ct) => c.ListToSortAsync(-1, ct));
            if (got && list is { Result.Ok: true })
            {
                _all = list.Items.ToList();
                _total = list.Total;
            }
            Apply(at);
            Say(_strings["sort.reviewed.removed"], Tone.Ok);
            await LoadImageAsync();
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void ShowWasOk() => Filter = SortFilter.WasOk;

    [RelayCommand]
    private void ShowReviewed() => Filter = SortFilter.Reviewed;

    [RelayCommand]
    private void ShowAll() => Filter = SortFilter.All;

    [RelayCommand]
    private void ShowGood() => Filter = SortFilter.Good;

    [RelayCommand]
    private void ShowBad() => Filter = SortFilter.Bad;
}
