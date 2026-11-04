using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// The job designer (UI-UX-SPEC.md 4.7, 4.8), for engineers: a job's cameras and their tools
/// (Read code, Read text, Check text) with their essential settings, tried on the camera's stored
/// pictures as the line would run them, taught from a good part's picture, and saved as a new
/// version. The runtime checks every save (a job that can never pass is refused) and refuses a
/// try or a save of the running job while the line runs. Nothing here changes the line until Save.
/// </summary>
public sealed partial class DesignerViewModel : ObservableObject
{
    /// <summary>Building the tools (AI models) and reading a full picture take a while.</summary>
    public static readonly TimeSpan TryLimit = TimeSpan.FromSeconds(90);

    private readonly Strings _strings;
    private readonly RuntimeCalls _calls;
    private readonly Func<byte[], IImage?> _decode;
    private readonly Action _close;
    private TryOutcome? _last;
    /// <summary>The job as last opened or saved; null for a new job never saved.</summary>
    private string? _savedJson;
    /// <summary>What to do when the engineer answers yes to <see cref="Question"/>.</summary>
    private Func<Task>? _onYes;
    /// <summary>The newest picture list asked for: an older answer arriving later is dropped.</summary>
    private int _picturesAsked;
    /// <summary>The draft is replaced by its taught copy: same cameras, same pictures, no reload.</summary>
    private bool _keepPictures;

    public DesignerViewModel(Strings strings, RuntimeCalls calls, Func<byte[], IImage?> decode, Action close)
    {
        _strings = strings;
        _calls = calls;
        _decode = decode;
        _close = close;
    }

    public ObservableCollection<string> Jobs { get; } = [];
    public ObservableCollection<CameraDraft> Cameras { get; } = [];
    public ObservableCollection<ToolDraft> CameraTools { get; } = [];
    public ObservableCollection<string> Pictures { get; } = [];
    public ObservableCollection<ToolTried> Results { get; } = [];
    public ObservableCollection<TextLineRead> LinesRead { get; } = [];

    public IReadOnlyList<string> CodeFormats => ToolDraft.CodeFormats;
    public IReadOnlyList<string> CheckDigits => ToolDraft.CheckDigits;
    public IReadOnlyList<string> Grades => ToolDraft.Grades;
    public IReadOnlyList<string> PrintTypes => ToolDraft.PrintTypes;
    public IReadOnlyList<string> Polarities => ToolDraft.Polarities;
    public IReadOnlyList<string> Sources => LineDraft.Sources;

    public JobDraft? Job { get; private set; }
    public bool HasJob => Job is not null;

    [ObservableProperty] private string? _selectedJob;
    [ObservableProperty] private string _newJobName = "";
    [ObservableProperty] private int _cameraIndex = -1;
    [ObservableProperty] private ToolDraft? _selectedTool;
    [ObservableProperty] private int _pictureIndex = -1;
    /// <summary>Batch values to try with, "BATCH=A123; MRP=45.00"; empty: those entered at the line.</summary>
    [ObservableProperty] private string _tryBatch = "";

    [ObservableProperty] private IImage? _image;
    [ObservableProperty] private double _imageWidth = 1;
    [ObservableProperty] private double _imageHeight = 1;
    [ObservableProperty] private IReadOnlyList<ResultGraphic> _graphics = [];
    [ObservableProperty] private Tone _frameTone = Tone.Neutral;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private Tone _messageTone = Tone.Info;
    [ObservableProperty] private bool _busy;

    public string JobTitle => Job is null ? _strings["design.title"]
        : _strings.Format("design.title.job", Job.Name.Length > 0 ? Job.Name : _strings["design.unnamed"], Job.Version);

    public string JobName
    {
        get => Job?.Name ?? "";
        set { if (Job is null) return; Job.Name = value; Changed(typing: true); OnPropertyChanged(nameof(JobTitle)); }
    }

    public string JobNumber
    {
        get => Job is { Number: > 0 } j ? j.Number.ToString(CultureInfo.InvariantCulture) : "";
        set
        {
            if (Job is null) return;
            Job.Number = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            Changed(typing: true);
        }
    }

    public CameraDraft? Camera => CameraIndex >= 0 && CameraIndex < Cameras.Count ? Cameras[CameraIndex] : null;
    public bool HasTool => SelectedTool is not null;
    public string PictureText => PictureIndex >= 0 && PictureIndex < Pictures.Count
        ? _strings.Format("design.picture", Pictures[PictureIndex], PictureIndex + 1, Pictures.Count)
        : _strings["design.no_pictures"];

    // a change is seen by comparing the draft with the saved job (Dirty); nothing to record here

    // --- jobs ---------------------------------------------------------------------------------

    public async Task LoadAsync()
    {
        var (reached, products) = await _calls.TryAsync((c, ct) => c.ListProductsAsync(ct));
        Jobs.Clear();
        if (!reached || products is null) { Say("run.reason.not_connected", Tone.Ng); return; }
        foreach (var name in products.Select(p => p.Job).Distinct()) Jobs.Add(name);
    }

    /// <summary>The job list, then the job the line has open, so the designer starts where the line is.</summary>
    public async Task LoadOpenJobAsync()
    {
        await LoadAsync();
        var (_, file) = await _calls.TryDesignAsync((c, ct) => c.GetJobAsync("", ct));
        if (file is { Ok: true } && JobDraft.Parse(file.Json).Name is { Length: > 0 } open) SelectedJob = open;
    }

    partial void OnSelectedJobChanged(string? value)
    {
        // the job open already (also the list put back after "No"): Reload opens it again on purpose
        if (value is null || value == Job?.Name) return;
        if (Dirty) Ask("design.drop_changes", () => OpenAsync(value));
        else _ = OpenAsync(value);
    }

    public async Task OpenAsync(string name)
    {
        var (reached, file) = await _calls.TryDesignAsync((c, ct) => c.GetJobAsync(name, ct));
        if (!reached) { Say("run.reason.not_connected", Tone.Ng); return; }
        if (file is not { Ok: true }) { Refused(file?.Refused); return; }
        try
        {
            var job = JobDraft.Parse(file.Json);
            ForgetKeptHistory();   // the job open before: its changes dropped, or another job opened
            SetJob(job);
            _savedJson = Job!.ToJson();
            _keptAccepted = null;
            if (!RestoreHistory(Job.Name)) ResetHistory();
            ShowAcceptedCamera();
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { SayText(e.Message, Tone.Ng); return; }
        Say(null);
        await LoadPicturesAsync();
        RestoreAccepted();   // on the picture they were found on
    }

    /// <summary>A new job with two cameras (the usual: a code on one, the batch print on the other).</summary>
    [RelayCommand]
    private async Task NewJob()
    {
        var name = NewJobName.Trim();
        if (name.Length == 0) { Say("design.name_first", Tone.Warn); return; }
        if (Jobs.Contains(name, StringComparer.OrdinalIgnoreCase)) { Say("design.name_taken", Tone.Warn); return; }
        ForgetKeptHistory();
        SetJob(JobDraft.New(name, 2));
        _savedJson = null;
        ResetHistory();
        Say("design.new_saved_later", Tone.Info);
        await LoadPicturesAsync();
    }

    private void SetJob(JobDraft job)
    {
        Job = job;
        Cameras.Clear();
        foreach (var c in job.Cameras) Cameras.Add(c);
        CameraIndex = -1;
        CameraIndex = Cameras.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(HasJob));
        OnPropertyChanged(nameof(JobTitle));
        OnPropertyChanged(nameof(JobName));
        OnPropertyChanged(nameof(JobNumber));
        RebuildTree();
        ClearImageSet();
        _watchedJson = job.ToJson();
    }

    [RelayCommand]
    private void AddCamera()
    {
        Record(typing: true);   // a setting typed just before is a step of its own
        if (Job?.AddCamera() is not { } cam) { Say("design.max_cameras", Tone.Warn); return; }
        Cameras.Add(cam);
        CameraIndex = Cameras.Count - 1;
        Changed();
    }

    partial void OnCameraIndexChanged(int value)
    {
        ClearImageSet();
        OnPropertyChanged(nameof(Camera));
        BlobToolChanged();
        RefreshTools();
        SelectedTool = CameraTools.FirstOrDefault();
        if (_keepPictures) return;
        ClearResult();
        if (value >= 0) _ = LoadPicturesAsync();
    }

    private void RefreshTools()
    {
        var keep = SelectedTool;
        CameraTools.Clear();
        if (Job is not null && CameraIndex >= 0)
            foreach (var t in Job.ToolsOf(CameraIndex)) CameraTools.Add(t);
        if (keep is not null && CameraTools.Contains(keep)) SelectedTool = keep;
    }

    partial void OnSelectedToolChanged(ToolDraft? value)
    {
        OnPropertyChanged(nameof(HasTool));
        BlobToolChanged();
        DrawGraphics();
        ShowSelection();
    }

    [RelayCommand]
    private void AddReadCode() => AddTool(ToolKind.ReadCode);

    [RelayCommand]
    private void AddReadText() => AddTool(ToolKind.ReadText);

    [RelayCommand]
    private void AddCheckText() => AddTool(ToolKind.CheckText);

    [RelayCommand]
    private void AddBlob() => AddTool(ToolKind.Blob);

    private void AddTool(ToolKind kind)
    {
        if (Job is null || CameraIndex < 0) return;
        Record(typing: true);
        var tool = Job.AddTool(kind, CameraIndex);
        RefreshTools();
        SelectedTool = tool;
        Changed();
    }

    [RelayCommand]
    private void RemoveTool()
    {
        if (Job is null || SelectedTool is not { } tool) return;
        Record(typing: true);
        Job.RemoveTool(tool);
        RefreshTools();
        SelectedTool = CameraTools.FirstOrDefault();
        Changed();
        SayText(_strings.Format("design.removed", tool.Name), Tone.Info);   // no question: Undo puts it back (UI-UX-SPEC 5)
    }

    [RelayCommand]
    private void AddLine()
    {
        Record(typing: true);
        SelectedTool?.AddLine();
        Changed();
    }

    [RelayCommand]
    private void RemoveLine(LineDraft line)
    {
        Record(typing: true);
        SelectedTool?.RemoveLine(line);
        Changed();
    }

    // --- pictures and trying ------------------------------------------------------------------

    [RelayCommand]
    private async Task LoadPicturesAsync()
    {
        var asked = ++_picturesAsked;
        if (Job is null || CameraIndex < 0)
        {
            Pictures.Clear();
            PictureIndex = -1;
            return;
        }
        var (reached, list) = await _calls.TryDesignAsync((c, ct) => c.ListPicturesAsync(Job.ToJson(), CameraIndex, ct));
        if (asked != _picturesAsked) return;   // another camera or folder was asked for meanwhile
        Pictures.Clear();
        PictureIndex = -1;
        if (!reached) { Say("run.reason.not_connected", Tone.Ng); return; }
        if (list is not { Ok: true }) { Refused(list?.Refused); return; }
        foreach (var n in list.Names) Pictures.Add(n);
        PictureIndex = Pictures.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(PictureText));
    }

    partial void OnPictureIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PictureText));
        // what auto find offered belongs to the picture it looked at
        if (Suggestions.Count > 0) DismissAllSuggestions();
    }

    [RelayCommand]
    private async Task NextPicture()
    {
        if (Pictures.Count == 0) return;
        PictureIndex = (PictureIndex + 1) % Pictures.Count;
        await TryAsync();
    }

    [RelayCommand]
    private async Task PreviousPicture()
    {
        if (Pictures.Count == 0) return;
        PictureIndex = (PictureIndex + Pictures.Count - 1) % Pictures.Count;
        await TryAsync();
    }

    /// <summary>The camera's tools on the picture shown, as the line would run them.</summary>
    [RelayCommand]
    private Task TryAsync() => RunAsync(null);

    /// <summary>The selected tool set up from the picture (a good part): type, area, letter height, lines.</summary>
    [RelayCommand]
    private Task TeachAsync() => SelectedTool is { } t ? RunAsync(t.Name) : Task.CompletedTask;

    /// <param name="spot">With <paramref name="teach"/> on a Blob tool: the spot taught, by its number on the picture; 0 = the first.</param>
    private async Task RunAsync(string? teach, int spot = 0)
    {
        if (Job is null || CameraIndex < 0) return;
        if (PictureIndex < 0) { Say("design.no_pictures", Tone.Warn); return; }
        Busy = true;
        try
        {
            var request = new TryRequest(Job.ToJson(), CameraIndex, Stored: Pictures[PictureIndex], TeachTool: teach, Batch: ParseBatch(TryBatch),
                PlcText: ParsePlcText(TryBatch), TeachSpot: spot);
            var (reached, r) = await _calls.TryDesignAsync((c, ct) => c.TryJobAsync(request, ct), TryLimit);
            if (!reached) { Say("run.reason.not_connected", Tone.Ng); return; }
            if (r is not { Ok: true }) { Refused(r?.Refused); return; }
            if (teach is not null && r.TaughtJson.Length > 0)
            {
                ApplyTaught(r.TaughtJson, teach);
                Record();   // a Teach is one undo step
            }
            Show(r);
            _spotsOf = Job.ToJson();   // the draft the spots on the picture were numbered for (BLB-OUT-07)
            if (teach is not null) SayText(r.TaughtText, Tone.Ok);
            else Say(null);
        }
        finally { Busy = false; }
    }

    /// <summary>The taught draft replaces the draft; the same camera and tool stay selected.</summary>
    private void ApplyTaught(string json, string tool)
    {
        var camera = CameraIndex;
        var picture = PictureIndex;
        _keepPictures = true;
        try
        {
            SetJob(JobDraft.Parse(json));
            CameraIndex = camera;
        }
        finally { _keepPictures = false; }
        PictureIndex = picture;
        SelectedTool = CameraTools.FirstOrDefault(t => t.Name == tool) ?? CameraTools.FirstOrDefault();
    }

    /// <summary>Every stored picture of the camera: how many pass. Bad parts in the set should fail.</summary>
    [RelayCommand]
    private async Task RunAllAsync()
    {
        if (Job is null || CameraIndex < 0 || Pictures.Count == 0) { Say("design.no_pictures", Tone.Warn); return; }
        Busy = true;
        BeginImageSet();
        int pass = 0, fail = 0;
        var failed = new List<string>();
        double slowest = 0;
        try
        {
            var json = Job.ToJson();
            var batch = ParseBatch(TryBatch);
            var plcText = ParsePlcText(TryBatch);
            var names = Pictures.ToList();
            var camera = CameraIndex;   // the camera the run started on, whatever is clicked meanwhile
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                var (reached, r) = await _calls.TryDesignAsync((c, ct) => c.TryJobAsync(new TryRequest(json, camera, Stored: name, Batch: batch, PlcText: plcText, SkipPictureCheck: true, NotShown: true), ct), TryLimit);
                if (!reached) { StopImageSet(i, names.Count); Say("run.reason.not_connected", Tone.Ng); return; }
                if (r is not { Ok: true }) { StopImageSet(i, names.Count); Refused(r?.Refused); return; }
                if (r.Pass) pass++;
                else { fail++; failed.Add(name); }
                AddToImageSet(name, r);
                slowest = Math.Max(slowest, r.TimeMs);   // the picture on screen (and its scale) stays the one shown
            }
        }
        finally { Busy = false; }
        Summary = _strings.Format("design.run_all", pass, fail, slowest.ToString("0", CultureInfo.CurrentCulture))
                  + (failed.Count > 0 ? " " + _strings.Format("design.failed", string.Join(", ", failed.Take(8))) : "");
        EndImageSet(pass, fail, slowest);
        Log(Summary, fail > 0 ? Tone.Warn : Tone.Ok);
        Say(null);
    }

    private void Show(TryOutcome r)
    {
        _last = r;
        ShowPicture(r);
        ImageWidth = Math.Max(1, r.Width);
        ImageHeight = Math.Max(1, r.Height);
        FrameTone = r.Pass ? Tone.Ok : Tone.Ng;
        Results.Clear();
        foreach (var t in r.Tools ?? []) Results.Add(t);
        LinesRead.Clear();
        foreach (var l in r.Lines ?? []) LinesRead.Add(l);
        Summary = _strings.Format(r.Pass ? "design.try.ok" : "design.try.ng", r.TimeMs.ToString("0", CultureInfo.CurrentCulture));
        DrawGraphics();
        AfterShow(r);
        if (_zoom > 1) AskFullPicture();   // zoomed in already: the new picture at full resolution too
    }

    private void ClearResult()
    {
        _last = null;
        ShowPicture(null);
        Results.Clear();
        LinesRead.Clear();
        Summary = "";
        FrameTone = Tone.Neutral;
        DrawGraphics();
        AfterClear();
    }

    /// <summary>The tried picture's result graphics, the selected tool's area (dashed, in the info colour) and the picture check's marks.</summary>
    private void DrawGraphics()
    {
        var list = new List<ResultGraphic>(ShownGraphics());
        if (_last is { } tried) AddPixelGraphics(list, tried);
        if (_last is not null) list.AddRange(SuggestionGraphics(ShownScale));
        if (_last is not null && SelectedTool?.Roi is { } a)
        {
            double s = ShownScale, x0 = a.X * s, y0 = a.Y * s, x1 = (a.X + a.Width) * s, y1 = (a.Y + a.Height) * s;
            list.Add(new ResultGraphic(GraphicKind.Polygon, [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], Verdict.Info,
                AreaLabel(SelectedTool), SelectedTool.Name));
        }
        if (_last is not null) list.AddRange(AreaGraphics(ShownScale));
        list.AddRange(PictureCheckGraphics());
        Graphics = list;
    }

    /// <summary>An area drawn on the picture (pixels of the picture shown: JPEG, or camera once the full picture is shown)
    /// for the selected tool; with "Find in a window", or with no tool selected, auto find looks in it instead.</summary>
    public void AreaDrawn(Point2 from, Point2 to)
    {
        if (_last is not { } r) return;
        var s = ShownScale;
        int x0 = (int)Math.Clamp(Math.Min(from.X, to.X) / s, 0, r.ImageWidth), y0 = (int)Math.Clamp(Math.Min(from.Y, to.Y) / s, 0, r.ImageHeight);
        int x1 = (int)Math.Clamp(Math.Max(from.X, to.X) / s, 0, r.ImageWidth), y1 = (int)Math.Clamp(Math.Max(from.Y, to.Y) / s, 0, r.ImageHeight);
        if (x1 - x0 < 8 || y1 - y0 < 8) return;   // a tap, not an area
        if (FindInWindow || SelectedTool is null)
        {
            FindInWindow = false;
            _ = FindAsync((x0, y0, x1 - x0, y1 - y0));
            return;
        }
        var tool = SelectedTool;
        Record(typing: true);
        tool.Roi = (x0, y0, x1 - x0, y1 - y0);
        Changed();
        DrawGraphics();
    }

    [RelayCommand]
    private void WholePicture()
    {
        if (SelectedTool is null) return;
        Record(typing: true);
        SelectedTool.Roi = null;
        Changed();
        DrawGraphics();
    }

    /// <summary>"BATCH=A123; MRP=45.00" as batch values; parts without "=" are left out, and so are the
    /// PLC's strings ("PLC501=B.NO A123", see <see cref="ParsePlcText"/>).</summary>
    public static IReadOnlyDictionary<string, string>? ParseBatch(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Pairs(text))
            if (PlcRegister(k) is null) d[k] = v;
        return d.Count > 0 ? d : null;
    }

    /// <summary>"PLC501=B.NO A123": the string the PLC's text block holds at register 501, to try a line
    /// that takes its text from the PLC.</summary>
    public static IReadOnlyDictionary<int, string>? ParsePlcText(string text)
    {
        var d = new Dictionary<int, string>();
        foreach (var (k, v) in Pairs(text))
            if (PlcRegister(k) is { } r) d[r] = v;
        return d.Count > 0 ? d : null;
    }

    private static IEnumerable<(string Key, string Value)> Pairs(string text)
    {
        foreach (var part in text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.IndexOf('=') is var eq and > 0) yield return (part[..eq].Trim(), part[(eq + 1)..].Trim());
    }

    private static int? PlcRegister(string key) =>
        key.StartsWith("PLC", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;

    // --- save and close -----------------------------------------------------------------------

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Job is null || DroppedToolUnsure()) return;
        var advice = AdviseBeforeSave();
        Busy = true;
        try
        {
            var (reached, r) = await _calls.TryDesignAsync((c, ct) => c.SaveJobAsync(Job.ToJson(), ct));
            if (!reached) { Say("run.reason.not_connected", Tone.Ng); return; }
            if (r is not { Ok: true }) { Refused(r?.Refused); return; }
            Job.Root["Version"] = r.Version;
            _savedJson = Job.ToJson();
            KeptHistorySaved(r.Name);
            if (!Jobs.Contains(r.Name)) Jobs.Add(r.Name);
            OnPropertyChanged(nameof(JobTitle));
            SayText(Saved(_strings.Format("design.saved", r.Name, r.Version), advice), advice > 0 ? Tone.Warn : Tone.Ok);
        }
        finally { Busy = false; }
    }

    /// <summary>The draft differs from the job as last opened or saved.</summary>
    public bool Dirty => Job is not null && Job.ToJson() != _savedJson;

    // --- Job menu: Save as, Reload (UI-UX-SPEC.md 4.7) -----------------------------------------

    [ObservableProperty] private bool _savingAs;
    [ObservableProperty] private string _saveAsName = "";
    /// <summary>A yes/no question over the designer ("Drop the changes since the last save?").</summary>
    [ObservableProperty] private string? _question;

    /// <summary>Save as: asks for the new name.</summary>
    [RelayCommand]
    private void SaveAs()
    {
        if (Job is null) return;
        SaveAsName = Job.Name + " copy";
        SavingAs = true;
    }

    /// <summary>The draft saved under the new name as a job of its own (version 1). Its PLC number is cleared:
    /// two jobs with one number would leave the PLC's choice to chance; give it its own number later.</summary>
    [RelayCommand]
    private async Task SaveAsConfirm()
    {
        if (Job is null) return;
        var name = SaveAsName.Trim();
        if (name.Length == 0) { Say("design.name_first", Tone.Warn); return; }
        if (Jobs.Contains(name, StringComparer.OrdinalIgnoreCase)) { Say("design.name_taken", Tone.Warn); return; }
        if (DroppedToolUnsure()) return;
        var copy = JobDraft.Parse(Job.ToJson());
        copy.Name = name;
        copy.Number = 0;
        copy.Root.Remove("Version");
        var advice = AdviseBeforeSave();
        Busy = true;
        try
        {
            var (reached, r) = await _calls.TryDesignAsync((c, ct) => c.SaveJobAsync(copy.ToJson(), ct));
            if (!reached) { Say("run.reason.not_connected", Tone.Ng); return; }
            if (r is not { Ok: true }) { Refused(r?.Refused); return; }
            copy.Root["Version"] = r.Version;
            var camera = CameraIndex;
            _keepPictures = true;
            try { SetJob(copy); CameraIndex = camera; }
            finally { _keepPictures = false; }
            _savedJson = copy.ToJson();
            KeptHistorySaved(r.Name);
            ResetHistory();   // a job of its own now: its history starts here
            if (!Jobs.Contains(r.Name)) Jobs.Add(r.Name);
            SelectedJob = r.Name;   // the list shows the job now open (no reload: it is the open one)
            SavingAs = false;
            SayText(Saved(_strings.Format("design.saved_as", r.Name, r.Version), advice), advice > 0 ? Tone.Warn : Tone.Ok);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void SaveAsCancel() => SavingAs = false;

    /// <summary>Reload: the last saved version, after asking when there are changes to drop.</summary>
    [RelayCommand]
    private void Reload()
    {
        if (Job is null) return;
        if (_savedJson is null) { Say("design.never_saved", Tone.Warn); return; }
        var saved = JobDraft.Parse(_savedJson).Name;
        if (Dirty) Ask("design.drop_changes", () => OpenAsync(saved));
        else _ = OpenAsync(saved);
    }

    private void Ask(string key, Func<Task> yes)
    {
        Question = _strings[key];
        _onYes = yes;
    }

    [RelayCommand]
    private async Task AnswerYes()
    {
        var yes = _onYes;
        Question = null;
        _onYes = null;
        if (yes is not null) await yes();
    }

    [RelayCommand]
    private void AnswerNo()
    {
        Question = null;
        _onYes = null;
        // the job list shows the job still open
        if (Job is not null && SelectedJob != Job.Name) SelectedJob = Jobs.FirstOrDefault(j => j == Job.Name);
    }

    [RelayCommand]
    private void Close()
    {
        if (Dirty) { Ask("design.drop_changes", () => { ForgetKeptHistory(); _close(); return Task.CompletedTask; }); return; }
        ForgetKeptHistory();
        _close();
    }

    // --- messages -----------------------------------------------------------------------------

    private void Say(string? key, Tone tone = Tone.Info)
    {
        Message = key is null ? null : _strings[key];
        MessageTone = tone;
        if (Message is not null) Log(Message, tone);
        OnPropertyChanged(nameof(StatusText));
    }

    private void SayText(string text, Tone tone)
    {
        Message = text.Length > 0 ? text : null;
        MessageTone = tone;
        Log(text, tone);
        OnPropertyChanged(nameof(StatusText));
    }

    private void Refused(CommandResult? r)
    {
        if (r?.ReasonKey == "run.reason.no_rights") { Say("design.no_rights", Tone.Ng); return; }
        SayText(_strings.KeyOrText(r?.ReasonKey, r?.ReasonText, "run.reason.refused"), Tone.Ng);
    }
}

/// <summary>Pass and fail as words and marks in the designer's result lists.</summary>
public static class DesignerConverters
{
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, string> PassText =
        new(pass => Strings.Instance[pass ? "result.ok" : "result.ng"]);

    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, string> PassMark = new(pass => pass ? "✓" : "✗");

    /// <summary>A tool's result in a word: OK, NG, or Info for a report-only tool (never OK).</summary>
    public static readonly Avalonia.Data.Converters.FuncValueConverter<ToolTried?, string> ToolText =
        new(t => t is null ? "" : Strings.Instance[ViewModels.ToolMark.Word(t.Pass, t.ReportOnly)]);

    /// <summary>A tool's result as a mark: ✓, ✗, or ⓘ for a report-only tool.</summary>
    public static readonly Avalonia.Data.Converters.FuncValueConverter<ToolTried?, string> ToolMark =
        new(t => t is null ? "" : ViewModels.ToolMark.Mark(t.Pass, t.ReportOnly));
}
