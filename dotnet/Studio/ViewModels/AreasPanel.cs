using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>A tool auto find offers and the engineer has not accepted yet, listed greyed in the "Areas and values"
/// panel: what it read, each entry with its field type. Key is the designer's own item, handed back on Accept or Not this.</summary>
public sealed record PendingArea(object Key, string Summary, IReadOnlyList<string> Read, IReadOnlyList<string> Fields);

/// <summary>
/// One row of the "Areas and values" panel (requirement AS-15, UI-UX-SPEC.md 4.19): one tool line of the camera
/// shown, with its area number (also on the picture), what it found and the format suggested for it. The edit box
/// and "Use this format" write the job draft only; the running job changes when the engineer saves.
/// </summary>
public sealed partial class AreaRow : ObservableObject
{
    private readonly Func<string>? _get;
    private readonly Action<string>? _set;
    private readonly Action<AreaRow>? _use;

    public AreaRow(int area, string toolName, ToolDraft? tool, string line, string found, string field, string suggested,
        bool failed, string note = "", bool first = true, PendingArea? pending = null,
        Func<string>? get = null, Action<string>? set = null, Action<AreaRow>? use = null, bool adds = false)
    {
        Area = area;
        ToolName = toolName;
        Tool = tool;
        Line = line;
        Found = found;
        Field = field;
        Suggested = suggested;
        Failed = failed;
        Note = note;
        First = first;
        PendingArea = pending;
        _get = get;
        _set = set;
        _use = use;
        Adds = adds;
    }

    /// <summary>The area's number on the picture: the tool's place among the camera's tools, from 1.</summary>
    public int Area { get; }
    public string AreaText => Area.ToString(CultureInfo.CurrentCulture);
    public string ToolName { get; }
    public ToolDraft? Tool { get; }
    /// <summary>"Line 2" for a text tool's line; empty for a code.</summary>
    public string Line { get; }
    public string Found { get; }
    /// <summary>The field type ("expiry date"), a code's GS1 elements or its type.</summary>
    public string Field { get; }
    /// <summary>The format "Use this format" writes; empty when there is none to take.</summary>
    public string Suggested { get; }
    /// <summary>The tool failed on this line, or found nothing here yet: red, at the top.</summary>
    public bool Failed { get; }
    /// <summary>Why the tool failed, on its first row.</summary>
    public string Note { get; }
    /// <summary>The tool's first row (a tool with several lines has one row per line).</summary>
    public bool First { get; }
    /// <summary>A line read that the tool has no line for: "Use this format" adds the line with the box's format.</summary>
    public bool Adds { get; }
    public PendingArea? PendingArea { get; }
    public bool Pending => PendingArea is not null;
    public bool HasFound => Found.Length > 0;
    public bool HasNote => Note.Length > 0;
    public bool HasFormat => _get is not null;
    public string SuggestedText => Suggested.Length == 0 || Suggested == Field ? Field
        : Field.Length == 0 ? Suggested : $"{Field} · {Suggested}";
    public bool HasSuggested => SuggestedText.Length > 0;
    public Tone Tone => Pending ? Tone.Neutral : Failed ? Tone.Ng : Tone.Ok;

    [ObservableProperty] private bool _selected;

    /// <summary>The format as the draft holds it (a line's format or template, a code's GS1 elements or expected text).</summary>
    public string Format
    {
        get => _get?.Invoke() ?? "";
        set
        {
            if (_set is null || value == Format) return;
            _set(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanUse));
        }
    }

    public bool CanUse => _use is not null && (Adds ? Format.Trim().Length > 0 : Suggested.Length > 0 && Suggested != Format);

    [RelayCommand]
    private void Use()
    {
        if (!CanUse) return;
        _use!(this);
        OnPropertyChanged(nameof(Format));
        OnPropertyChanged(nameof(CanUse));
    }
}

/// <summary>
/// The "Areas and values" side panel (requirement AS-15; UI-UX-SPEC.md 4.19 "Right dock, Areas and values" and
/// section 5 "Values after a drop"): every tool line of the camera shown, however its area was made (dropped from
/// the toolbox, drawn, or accepted from auto find), with what it found on the picture last tried and the format
/// suggested for it; auto find's suggestions not yet accepted come last, greyed. Rows that fail or have no value
/// yet come first, in red. It folds to its header with the chevron or Ctrl+Shift+A and remembers whether it was open.
/// Advice only (AS-12): nothing here passes a part; a format taken changes the draft, never the running job.
/// </summary>
public sealed partial class AreasPanelViewModel : ObservableObject
{
    public const string OpenKey = "designer.areas.open";

    private static readonly Regex Ai = new(@"\((\d{2,4})\)", RegexOptions.CultureInvariant);

    private readonly Strings _strings;
    private readonly PanelMemory _memory;
    /// <summary>Selecting a row from the designer must not hand the tool back to it.</summary>
    private bool _selecting;

    public AreasPanelViewModel(Strings strings, PanelMemory memory)
    {
        _strings = strings;
        _memory = memory;
        _open = memory.Get(OpenKey, true);
    }

    public ObservableCollection<AreaRow> Rows { get; } = [];

    /// <summary>A row was picked: its tool is selected in the designer (and so on the picture).</summary>
    public event Action<ToolDraft>? ToolPicked;
    /// <summary>A format was taken: the draft changed. A format typed in a row's box changes the draft as the property
    /// table does, seen by the designer's watch.</summary>
    public event Action? DraftChanged;
    public event Action<PendingArea>? Accepted;
    public event Action<PendingArea>? Dismissed;

    [ObservableProperty] private bool _open;
    [ObservableProperty] private AreaRow? _selectedRow;

    public string Chevron => Open ? "▾" : "▸";
    public bool IsEmpty => Rows.Count == 0;
    public string CountText => _strings.Format("areas.count", Rows.Count(r => !r.Pending), Rows.Count(r => r.Failed && !r.Pending));

    partial void OnOpenChanged(bool value)
    {
        _memory.Set(OpenKey, value);
        OnPropertyChanged(nameof(Chevron));
    }

    [RelayCommand]
    private void Toggle() => Open = !Open;

    partial void OnSelectedRowChanged(AreaRow? value)
    {
        foreach (var r in Rows) r.Selected = value is not null && (value.Tool is not null ? r.Tool == value.Tool : r == value);
        if (!_selecting && value?.Tool is { } tool) ToolPicked?.Invoke(tool);
    }

    /// <summary>The designer selected a tool (on the picture, in the tree): its rows are selected here.</summary>
    public void Select(ToolDraft? tool)
    {
        if (SelectedRow?.Tool == tool && tool is not null) return;
        _selecting = true;
        try { SelectedRow = tool is null ? null : Rows.FirstOrDefault(r => r.Tool == tool); }
        finally { _selecting = false; }
    }

    [RelayCommand]
    private void Accept(AreaRow row)
    {
        if (row.PendingArea is { } p) Accepted?.Invoke(p);
    }

    [RelayCommand]
    private void NotThis(AreaRow row)
    {
        if (row.PendingArea is { } p) Dismissed?.Invoke(p);
    }

    /// <summary>
    /// The rows for the camera's tools (in area order) on the picture last tried, then the suggestions not yet
    /// accepted. <paramref name="tried"/> null: nothing tried yet, every tool has no value. A tool switched off
    /// is listed too: it found nothing on the picture.
    /// </summary>
    public void Show(IReadOnlyList<ToolDraft> tools, TryOutcome? tried, IReadOnlyList<PendingArea> pending, ToolDraft? selected)
    {
        var rows = new List<AreaRow>();
        for (var i = 0; i < tools.Count; i++) rows.AddRange(ToolRows(i + 1, tools[i], tried));
        var pendingRows = new List<AreaRow>();
        for (var j = 0; j < pending.Count; j++) pendingRows.AddRange(PendingRows(tools.Count + j + 1, pending[j]));
        _selecting = true;
        try
        {
            SelectedRow = null;
            Rows.Clear();
            // failing and empty rows first, each group in area order (the sort is stable)
            foreach (var r in rows.OrderBy(r => r.Failed ? 0 : 1)) Rows.Add(r);
            foreach (var r in pendingRows) Rows.Add(r);
        }
        finally { _selecting = false; }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        Select(selected);
    }

    private IEnumerable<AreaRow> ToolRows(int area, ToolDraft tool, TryOutcome? tried)
    {
        var result = tried?.Tools?.FirstOrDefault(t => t.Name == tool.Name);
        var note = result is { Pass: false } ? result.Reason : "";
        if (tool.IsBlob) return [];   // a Blob tool reads no code or text: nothing to compare here
        return tool.IsCode ? CodeRows(area, tool, tried, result, note) : LineRows(area, tool, tried, result, note);
    }

    /// <summary>One row per code the tool read; a GS1 code suggests its elements as the AIs the code must carry.</summary>
    private IEnumerable<AreaRow> CodeRows(int area, ToolDraft tool, TryOutcome? tried, ToolTried? result, string note)
    {
        var codes = (tried?.Codes ?? []).Where(c => c.Tool == tool.Name).ToList();
        if (result is null || codes.Count == 0)
        {
            yield return new AreaRow(area, tool.Name, tool, "", "", tool.Format, "", failed: true, note, get: () => tool.Expected,
                set: v => tool.Expected = v);
            yield break;
        }
        for (var k = 0; k < codes.Count; k++)
        {
            var c = codes[k];
            var found = c.AddOn.Length > 0 ? $"{c.Text} + {c.AddOn}" : c.Text;
            var line = codes.Count > 1 ? _strings.Format("areas.code_n", k + 1) : "";
            if (c.Field.Length > 0)
            {
                // the AIs in the order printed, measures apart: their AI changes with each pack's decimals
                var ais = string.Join(", ", Ai.Matches(c.Field).Select(m => m.Groups[1].Value).Where(ai => !IsMeasure(ai)));
                var local = "";
                yield return new AreaRow(area, tool.Name, tool, line, found, c.Field, ais, !result.Pass, k == 0 ? note : "", k == 0,
                    get: () => tool.Gs1Check ? tool.Gs1Required : local,
                    set: v => { if (tool.Gs1Check) tool.Gs1Required = v; else local = v; },
                    use: r => { tool.Gs1Check = true; tool.Gs1Required = r.Suggested; DraftChanged?.Invoke(); });
            }
            else
            {
                yield return new AreaRow(area, tool.Name, tool, line, found, c.Format, "", !result.Pass, k == 0 ? note : "", k == 0,
                    get: () => tool.Expected, set: v => tool.Expected = v);
            }
        }
    }

    /// <summary>One row per line: each line of the tool with what was read for it, then any line read that the tool has
    /// no line for yet (taking its format adds the line). A Check text line keeps the template the engineer wrote: its
    /// suggestion is offered only while the template is empty.</summary>
    private IEnumerable<AreaRow> LineRows(int area, ToolDraft tool, TryOutcome? tried, ToolTried? result, string note)
    {
        var read = (tried?.Lines ?? []).Where(l => l.Tool == tool.Name).ToList();
        var count = Math.Max(Math.Max(tool.Lines.Count, read.Count), 1);
        for (var i = 0; i < count; i++)
        {
            var line = i < tool.Lines.Count ? tool.Lines[i] : null;
            var got = result is not null && i < read.Count ? read[i] : null;
            var name = count > 1 || line is not null ? _strings.Format("areas.line_n", i + 1) : "";
            var failed = got is null || got.Text.Length == 0 || !got.Pass || (i == 0 && result is { Pass: false });
            var rowNote = i == 0 ? note : "";
            var suggested = got?.Format ?? "";
            if (line is not null)
            {
                if (tool.IsCheck && line.Template.Length > 0) suggested = "";
                yield return new AreaRow(area, tool.Name, tool, name, got?.Text ?? "", got?.Field ?? "", suggested, failed, rowNote, i == 0,
                    get: () => line.Template, set: v => line.Template = v,
                    use: r => { line.Template = r.Suggested; DraftChanged?.Invoke(); });
            }
            else if (got is not null)
            {
                var local = suggested;
                yield return new AreaRow(area, tool.Name, tool, name, got.Text, got.Field, suggested, failed, rowNote, i == 0,
                    get: () => local, set: v => local = v,
                    use: _ => { tool.AddLine(local.Trim()); DraftChanged?.Invoke(); }, adds: true);
            }
            else
            {
                // a text tool with no line and nothing read
                yield return new AreaRow(area, tool.Name, tool, "", "", "", "", failed: true, rowNote);
            }
        }
    }

    private static IEnumerable<AreaRow> PendingRows(int area, PendingArea p)
    {
        var count = Math.Max(p.Read.Count, 1);
        for (var k = 0; k < count; k++)
        {
            var field = k < p.Fields.Count && p.Fields[k] != "code" ? p.Fields[k] : "";
            yield return new AreaRow(area, p.Summary, null, "", k < p.Read.Count ? p.Read[k] : "", field, "", failed: false,
                first: k == 0, pending: p);
        }
    }

    /// <summary>Measures (31nn-36nn) change AI with the decimals of each pack.</summary>
    private static bool IsMeasure(string ai) => ai.Length == 4 && ai[0] == '3' && ai[1] is >= '1' and <= '6';
}

/// <summary>
/// Small per-user workspace choices kept between sessions (whether a panel was open), as a JSON object in the
/// user's local application data. No file (tests): kept in memory only. A file that cannot be read or written
/// leaves the defaults; nothing here touches a job.
/// </summary>
public sealed class PanelMemory(string? path)
{
    /// <summary>The app's own; the tests put an in-memory one here.</summary>
    public static PanelMemory Shared { get; set; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", "workspace.json"));

    private JsonObject? _values;

    public bool Get(string key, bool fallback) =>
        Values()[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public void Set(string key, bool value)
    {
        Values()[key] = value;
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Values().ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // not remembered; the panel still works
    }

    private JsonObject Values()
    {
        if (_values is not null) return _values;
        try { _values = path is not null && File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return _values ??= new JsonObject();
    }
}
