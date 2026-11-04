using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// A row of the Engineer workspace's job tree (UI-UX-SPEC.md 4.19): the job, a camera or a tool,
/// with the tool's ✓ / ✗ and time on the picture last tried. Names follow the draft as it is edited.
/// </summary>
public sealed partial class TreeNode : ObservableObject
{
    private readonly string _text;

    public TreeNode(int level, string icon, string text, int camera = -1, CameraDraft? cameraDraft = null, ToolDraft? tool = null)
    {
        Level = level;
        Icon = icon;
        _text = text;
        CameraIndex = camera;
        Camera = cameraDraft;
        Tool = tool;
        if (tool is not null) tool.PropertyChanged += OnDraftChanged;
        else if (cameraDraft is not null) cameraDraft.PropertyChanged += OnDraftChanged;
    }

    public int Level { get; }
    public double Indent => Level * 14;
    public string Icon { get; }
    public int CameraIndex { get; }
    public CameraDraft? Camera { get; }
    public ToolDraft? Tool { get; }
    public bool IsTool => Tool is not null;
    public string Name => Tool?.Name ?? Camera?.Name ?? _text;
    public string Kind => Tool?.KindText ?? "";

    [ObservableProperty] private bool _selected;
    /// <summary>The tool's result on the picture last tried: Ok, Ng or Neutral (not tried).</summary>
    [ObservableProperty] private Tone _tone = Tone.Neutral;
    [ObservableProperty] private string _mark = "";
    [ObservableProperty] private string _time = "";
    /// <summary>"too few pixels" (AS-14) until an engineer accepts it; empty otherwise.</summary>
    [ObservableProperty] private string _pixels = "";

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolDraft.Name) or nameof(CameraDraft.Name)) OnPropertyChanged(nameof(Name));
        if (e.PropertyName is nameof(ToolDraft.KindText)) OnPropertyChanged(nameof(Kind));
    }

    /// <summary>Stops following the draft (the tree is rebuilt).</summary>
    public void Detach()
    {
        if (Tool is not null) Tool.PropertyChanged -= OnDraftChanged;
        else if (Camera is not null) Camera.PropertyChanged -= OnDraftChanged;
    }
}

/// <summary>One picture of the image set after "Try all pictures": passed or not, how long, why.</summary>
public sealed record PictureResult(int Index, string Name, bool Pass, float TimeMs, string Reason, IImage? Thumb)
{
    public Tone Tone => Pass ? Tone.Ok : Tone.Ng;
    public string Icon => Pass ? Icons.Check : Icons.Cross;
    public string TimeText => TimeMs.ToString("0", CultureInfo.CurrentCulture) + " ms";
}

/// <summary>A line of the workspace's Messages panel.</summary>
public sealed record WorkspaceMessage(string Time, Tone Tone, string Icon, string Text);

/// <summary>The scores a tool gave over the image set, against its pass limit (the margin chart). Grade: the
/// scores are a Read code's print grades, 4 = A .. 0 = F, and Limit is the lowest grade that passes.</summary>
public sealed record ToolMargin(string Tool, IReadOnlyList<double> Values, double Limit, double Low, double High, bool Grade = false)
{
    /// <summary>The worst picture is at least a tenth of the scale above the limit; for grades, a whole grade
    /// above the lowest that passes. A picture with no score (NaN) is never safe.</summary>
    public bool Safe => Values.Count > 0 && (Grade ? Values.Min() >= Limit + 1 : Values.Min() - Limit >= (High - Low) / 10);

    /// <summary>The chart's scale: grades get one band each, centred on the grade.</summary>
    public double From => Grade ? Low - 0.5 : Low;
    public double To => Grade ? High + 0.5 : High;
    public int Bands => Grade ? (int)(High - Low) + 1 : 20;
    /// <summary>Where failing ends on the scale: for grades, between the limit and the grade below it.</summary>
    public double Edge => Grade ? Limit - 0.5 : Limit;

    /// <summary>How many pictures fall in each band of the chart; a picture with no score is in none.</summary>
    public int[] Counts()
    {
        var counts = new int[Bands];
        foreach (var v in Values)
            if (double.IsFinite(v)) counts[Math.Clamp((int)Math.Floor((v - From) / (To - From) * Bands), 0, Bands - 1)]++;
        return counts;
    }

    /// <summary>A score as shown: a grade as its letter.</summary>
    public string Text(double v) => Grade
        ? (int)Math.Round(v) switch { 4 => "A", 3 => "B", 2 => "C", 1 => "D", 0 => "F", _ => "-" }
        : v.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// The Engineer workspace around the job designer (UI-UX-SPEC.md 4.19): the job tree, panels that can be
/// hidden, the selected tool's result and margin, the image set after "Try all pictures", the tools'
/// times and the messages. The job logic stays in the designer; this part only arranges and summarises it.
/// </summary>
public sealed partial class DesignerViewModel
{
    public const int MessagesKept = 200;

    public ObservableCollection<TreeNode> Tree { get; } = [];
    public ObservableCollection<PictureResult> ImageSet { get; } = [];
    public ObservableCollection<WorkspaceMessage> Messages { get; } = [];
    /// <summary>The selected tool's lines read on the picture last tried.</summary>
    public ObservableCollection<TextLineRead> SelectedLines { get; } = [];

    /// <summary>The tools' times on the picture last tried, a new list each time (the chart redraws).</summary>
    [ObservableProperty] private IReadOnlyList<ToolTried> _toolTimes = [];
    [ObservableProperty] private ToolTried? _selectedResult;
    [ObservableProperty] private ToolMargin? _selectedMargin;
    [ObservableProperty] private string _imageSetSummary = "";
    [ObservableProperty] private int _bottomTab;

    [ObservableProperty] private bool _showJobPanel = true;
    [ObservableProperty] private bool _showPropertiesPanel = true;
    [ObservableProperty] private bool _showDocks = true;

    /// <summary>Each tool's quality (text) or grade (code) per picture of the last "Try all pictures", by camera
    /// and tool name; NaN where the tool gave none (it found or graded nothing), which counts as not safe.</summary>
    private readonly Dictionary<(int Camera, string Tool), List<double>> _margins = [];
    private int _setCamera = -1;
    /// <summary>The draft as last looked at by <see cref="Watch"/>; an edit anywhere changes it.</summary>
    private string? _watchedJson;
    private long _watchedAt;

    public bool HasSelectedResult => SelectedResult is not null;
    public bool HasMargin => SelectedMargin is not null;
    public string MarginTitle => SelectedMargin is { } m ? _strings.Format(m.Grade ? "ws.margin.grade" : "ws.margin", m.Values.Count) : "";
    public string MarginVerdict => SelectedMargin is { } m ? _strings[m.Safe ? "ws.margin.safe" : "ws.margin.close"] : "";
    public Tone MarginTone => SelectedMargin is { Safe: true } ? Tone.Ok : Tone.Warn;
    public string ToolTimesText => ToolTimes.Count == 0 ? "" : _strings.Format("ws.tools_time",
        ToolTimes.Sum(t => t.TimeMs).ToString("0.0", CultureInfo.CurrentCulture), ToolTimes.Count);

    partial void OnSelectedMarginChanged(ToolMargin? value)
    {
        OnPropertyChanged(nameof(HasMargin));
        OnPropertyChanged(nameof(MarginTitle));
        OnPropertyChanged(nameof(MarginVerdict));
        OnPropertyChanged(nameof(MarginTone));
    }

    partial void OnToolTimesChanged(IReadOnlyList<ToolTried> value) => OnPropertyChanged(nameof(ToolTimesText));
    public bool HasImageSet => ImageSet.Count > 0;
    public string StatusText => Busy ? _strings["design.status.busy"] : Dirty ? _strings["design.status.unsaved"] : _strings["design.status.saved"];

    partial void OnSelectedResultChanged(ToolTried? value)
    {
        OnPropertyChanged(nameof(HasSelectedResult));
        OnPixelsChanged();
    }
    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    /// <summary>The draft changed: an undo step (typing: into a setting), the tree, the "unsaved" status.</summary>
    private void Changed(bool typing = false)
    {
        Record(typing);
        _watchedAt = 0;
        Watch();
        RebuildTree();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Dirty));
    }

    /// <summary>The job tree from the draft: the job, each camera, each camera's tools.</summary>
    private void RebuildTree()
    {
        foreach (var n in Tree) n.Detach();
        Tree.Clear();
        if (Job is null) return;
        Tree.Add(new TreeNode(0, Icons.Product, Job.Name));
        for (var c = 0; c < Cameras.Count; c++)
        {
            Tree.Add(new TreeNode(1, Icons.Camera, "", c, Cameras[c]));
            foreach (var t in Job.ToolsOf(c)) Tree.Add(new TreeNode(2, ToolIcon(t), "", c, tool: t));
        }
        MarkTree();
    }

    private static string ToolIcon(ToolDraft t) => t.IsCode ? Icons.Code : t.IsBlob ? Icons.Blob : Icons.Text;

    /// <summary>Selection and each tool's result on the picture last tried (tools of the camera tried).</summary>
    private void MarkTree()
    {
        foreach (var n in Tree)
        {
            n.Selected = n.Tool is not null ? n.Tool == SelectedTool : n.Camera is not null && n.CameraIndex == CameraIndex && SelectedTool is null;
            var r = n.Tool is not null && n.CameraIndex == CameraIndex ? Results.FirstOrDefault(x => x.Name == n.Tool.Name) : null;
            n.Tone = r is null ? Tone.Neutral : ToolMark.Tone(r.Pass, r.ReportOnly);
            n.Mark = r is null ? "" : ToolMark.Mark(r.Pass, r.ReportOnly);
            n.Time = r is null ? "" : r.TimeMs.ToString("0.0", CultureInfo.CurrentCulture) + " ms";
            n.Pixels = r?.Pixels is { TooFew: true } ? _strings["px.too_few"] : "";
            if (n.Tool is not null && NotSnapped(n.Tool)) (n.Tone, n.Mark) = (Tone.Warn, "!");   // dropped where nothing was found
        }
    }

    /// <summary>A click on the tree: a camera shows its pictures, a tool also opens its properties.</summary>
    [RelayCommand]
    private void SelectNode(TreeNode node)
    {
        if (node.CameraIndex >= 0 && node.CameraIndex != CameraIndex) CameraIndex = node.CameraIndex;
        if (node.Tool is not null) SelectedTool = node.Tool;
        MarkTree();
    }

    /// <summary>The selected tool's result and lines on the picture last tried, and its margin over the image set.</summary>
    private void ShowSelection()
    {
        MarkTree();
        SyncAreas();
        var name = SelectedTool?.Name;
        SelectedResult = name is null ? null : Results.FirstOrDefault(r => r.Name == name);
        SelectedLines.Clear();
        foreach (var l in LinesRead.Where(l => l.Tool == name)) SelectedLines.Add(l);
        SelectedMargin = name is null || SelectedTool is not { } t || !_margins.TryGetValue((CameraIndex, name), out var values) || values.Count == 0 ? null
            : t.IsText ? new ToolMargin(name, values, t.MinQuality, 0, 100)
            : t.IsCode && t.GradeLimit >= 1 ? new ToolMargin(name, values, t.GradeLimit, 0, 4, Grade: true)
            : null;
    }

    /// <summary>A picture was tried: the tree's marks, the tool times, the selection.</summary>
    private void AfterShow(TryOutcome r)
    {
        ToolTimes = (r.Tools ?? []).ToList();
        ShowSelection();
        CheckPicture(r);
        LogPixels(r);
    }

    private void AfterClear()
    {
        ToolTimes = [];
        ShowSelection();
        ClearPictureCheck();
    }

    /// <summary>"Try all pictures" starts: the set and the margins are gathered again.</summary>
    private void BeginImageSet()
    {
        ClearImageSet();
        _setCamera = CameraIndex;
    }

    /// <summary>The image set and margins are of the draft and camera they were made with: gone when either changes.</summary>
    private void ClearImageSet()
    {
        ImageSet.Clear();
        _margins.Clear();
        ImageSetSummary = "";
        SelectedMargin = null;
        OnPropertyChanged(nameof(HasImageSet));
    }

    private void AddToImageSet(string name, TryOutcome r)
    {
        var failed = (r.Tools ?? []).Where(t => !t.Pass).Select(t => $"{t.Name}: {t.Reason}");
        ImageSet.Add(new PictureResult(ImageSet.Count, name, r.Pass, r.TimeMs, string.Join("; ", failed), Thumbnail(r.Jpeg)));
        if (Job is null) return;
        // text tools by their print quality; code tools with a lowest grade by their grade (a try grades every picture)
        foreach (var tool in Job.ToolsOf(_setCamera).Where(t => t.IsText || t.IsCode && t.GradeLimit >= 1))
        {
            var tried = (r.Tools ?? []).FirstOrDefault(t => t.Name == tool.Name);
            if (tried is null) continue;   // switched off: not part of this set
            var key = (_setCamera, tool.Name);
            if (!_margins.TryGetValue(key, out var list)) _margins[key] = list = [];
            list.Add((tool.IsCode ? tried.CodeGrade : tried.PrintQuality) is { } q ? q : double.NaN);
        }
        OnPropertyChanged(nameof(HasImageSet));
    }

    /// <summary>A small picture for the image set: a full one per picture would hold megabytes each.</summary>
    private static IImage? Thumbnail(byte[]? jpeg)
    {
        if (jpeg is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(jpeg);
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 224);
        }
        catch (Exception)   // not a picture Avalonia can read: the set shows the name without one
        {
            return null;
        }
    }

    /// <summary>"Try all pictures" stopped part way (not connected, refused): what is there is said to be partial.</summary>
    private void StopImageSet(int done, int of)
    {
        ImageSetSummary = _strings.Format("design.set.stopped", done, of);
        SelectedMargin = null;
        _margins.Clear();
    }

    /// <summary>Called about once a second by the main window: an edit in the property table changes the draft
    /// without a command, so the status ("Unsaved changes"), the margin's limit and the stale image set follow here.</summary>
    public void Watch()
    {
        KeepHistory();   // the steps of the last second kept for a restart
        var now = Environment.TickCount64;
        if (Job is null || now - _watchedAt < 500) return;
        _watchedAt = now;
        var json = Job.ToJson();
        if (json == _watchedJson) return;
        _watchedJson = json;
        Record(typing: true);   // an edit in the property table is an undo step
        ClearImageSet();
        OnPropertyChanged(nameof(Dirty));
        OnPropertyChanged(nameof(StatusText));
    }

    private void EndImageSet(int pass, int fail, double slowest)
    {
        ImageSetSummary = _strings.Format("design.set.summary", pass, fail, slowest.ToString("0", CultureInfo.CurrentCulture));
        BottomTab = 2;
        ShowSelection();
    }

    /// <summary>A click on a picture of the image set: tried again and shown, as with Next.</summary>
    [RelayCommand]
    private async Task OpenSetPicture(PictureResult picture)
    {
        var index = Pictures.IndexOf(picture.Name);   // by name: the list may have been read again since
        if (index < 0) return;
        PictureIndex = index;
        await TryAsync();
    }

    private void Log(string text, Tone tone)
    {
        if (text.Length == 0) return;
        Messages.Insert(0, new WorkspaceMessage(DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture), tone, Tones.IconFor(tone), text));
        while (Messages.Count > MessagesKept) Messages.RemoveAt(Messages.Count - 1);
    }

    // --- picture readout and property filter (UI-UX-SPEC.md 4.19) -------------------------------

    /// <summary>Where the pointer is on the picture, in full-image pixels, with the grey value there.</summary>
    [ObservableProperty] private string _pointerText = "";
    [ObservableProperty] private string _zoomText = "";
    /// <summary>Words to find among the property table's settings; empty shows them all.</summary>
    [ObservableProperty] private string _propertySearch = "";
    /// <summary>All settings, or only the essential ones (UI-UX-SPEC.md 4.8: 3-5 per tool). Engineers start on All.</summary>
    [ObservableProperty] private bool _showAllProperties = true;

    /// <summary>The pointer at a pixel of the shown picture (its own pixels); null when it left the picture.</summary>
    public void PointAt(int? x, int? y, int? grey)
    {
        if (x is not { } px || y is not { } py || _last is null) { PointerText = ""; return; }
        var s = ShownScale;
        PointerText = _strings.Format(grey is { } g ? "ws.pointer" : "ws.pointer.nogrey",
            ((int)(px / s)).ToString("N0", CultureInfo.CurrentCulture), ((int)(py / s)).ToString("N0", CultureInfo.CurrentCulture), grey ?? 0);
    }

    [RelayCommand]
    private void ShowEssential() => ShowAllProperties = false;

    [RelayCommand]
    private void ShowAll() => ShowAllProperties = true;

    /// <summary>Whether a property row shows: essential ones always, others with All; and its label holds the search words.</summary>
    public bool PropertyShown(string label, bool essential)
    {
        if (!essential && !ShowAllProperties) return false;
        var words = PropertySearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.All(w => label.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    [RelayCommand]
    private void ToggleJobPanel() => ShowJobPanel = !ShowJobPanel;

    [RelayCommand]
    private void TogglePropertiesPanel() => ShowPropertiesPanel = !ShowPropertiesPanel;

    [RelayCommand]
    private void ToggleDocks() => ShowDocks = !ShowDocks;

    [RelayCommand]
    private void ResetLayout() => (ShowJobPanel, ShowPropertiesPanel, ShowDocks) = (true, true, true);

    /// <summary>A workspace shortcut (UI-UX-SPEC.md 4.19): F5 try, F6 try all, F7 teach, Ctrl+S save,
    /// Page Up / Page Down the previous and next picture, Ctrl+Shift+A the "Areas and values" panel,
    /// Ctrl+Z undo, Ctrl+Y / Ctrl+Shift+Z redo. Nothing while the designer is busy.</summary>
    public void Shortcut(string action)
    {
        if (Busy) return;
        System.Windows.Input.ICommand? command = action switch
        {
            "try" => TryCommand,
            "tryall" => RunAllCommand,
            "teach" => TeachCommand,
            "save" => SaveCommand,
            "prev" => PreviousPictureCommand,
            "next" => NextPictureCommand,
            "areas" => Areas.ToggleCommand,
            "undo" => UndoCommand,
            "redo" => RedoCommand,
            _ => null,
        };
        if (command?.CanExecute(null) == true) command.Execute(null);
    }
}
