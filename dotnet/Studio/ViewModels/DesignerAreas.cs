using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// Several areas on one picture (requirement AS-15): every tool of the camera shown is an area with its number on
/// the picture and its row in the "Areas and values" panel (<see cref="AreasPanelViewModel"/>). Selecting a row
/// selects its tool, and the other way round; a tap on the picture selects the area under it. The rows follow each
/// try, so a tool dropped from the toolbox, drawn, or accepted from auto find shows what it found at once.
/// </summary>
public sealed partial class DesignerViewModel
{
    private AreasPanelViewModel? _areas;
    /// <summary>What the rows were made from: the picture tried, the camera, its tools and lines, the suggestions.</summary>
    private object?[] _areasMadeFrom = [];

    public AreasPanelViewModel Areas => _areas ??= NewAreas();

    private AreasPanelViewModel NewAreas()
    {
        var areas = new AreasPanelViewModel(_strings, PanelMemory.Shared);
        areas.ToolPicked += tool => { if (CameraTools.Contains(tool)) SelectedTool = tool; };
        areas.DraftChanged += () =>
        {
            Changed();
            SyncAreas();   // a line added from a read line has its own row now
        };
        // Accept and Not this on a greyed row are auto find's own accept and dismiss (AS-01): one list, one meaning
        areas.Accepted += p => { if (p.Key is SuggestionItem s) PanelAccept = AcceptSuggestionAsync(s); };
        areas.Dismissed += p => { if (p.Key is SuggestionItem s) DismissSuggestion(s); };
        return areas;
    }

    /// <summary>An Accept from the panel in flight (it tries the accepted tool), if any; done when nothing is.</summary>
    public Task PanelAccept { get; private set; } = Task.CompletedTask;

    /// <summary>Suggestions auto find offers on the picture, not yet accepted: listed greyed after the tools, each
    /// keyed by the designer's own <see cref="SuggestionItem"/> so Accept and Not this hand it straight back.</summary>
    private IReadOnlyList<PendingArea> PendingAreas() =>
        Suggestions.Select(s => new PendingArea(s, s.Suggestion.Summary, s.Suggestion.Read, s.Suggestion.Fields)).ToList();

    /// <summary>The suggestion list changed: its greyed rows and its numbers on the picture follow.</summary>
    private void ShowSuggestions()
    {
        SyncAreas();
        DrawGraphics();
    }

    /// <summary>The rows made again when what they show changed (a try, another camera, a tool added, taken out or
    /// given a line); the selection follows the designer's always. Called with each <see cref="ShowSelection"/>.</summary>
    private void SyncAreas()
    {
        var pending = PendingAreas();
        object?[] from = [_last, CameraIndex, .. CameraTools, .. CameraTools.Select(t => t.Lines.Count), .. pending];
        if (!from.SequenceEqual(_areasMadeFrom))
        {
            _areasMadeFrom = from;
            Areas.Show(CameraTools.ToList(), _last, pending, SelectedTool);
        }
        else Areas.Select(SelectedTool);
    }

    /// <summary>The number of a tool's area on the picture: its place among the camera's tools, from 1.</summary>
    public int AreaNumber(ToolDraft tool) => CameraTools.IndexOf(tool) + 1;

    /// <summary>The selected tool's area label, with its number.</summary>
    private string AreaLabel(ToolDraft tool) => AreaNumber(tool) is > 0 and var n ? $"{n} · {_strings["design.area"]}" : _strings["design.area"];

    /// <summary>The areas of the camera's other tools, each with its number, on the picture shown.</summary>
    private IEnumerable<ResultGraphic> AreaGraphics(double scale)
    {
        foreach (var tool in CameraTools)
        {
            if (tool == SelectedTool || tool.Roi is not { } a) continue;
            double x0 = a.X * scale, y0 = a.Y * scale, x1 = (a.X + a.Width) * scale, y1 = (a.Y + a.Height) * scale;
            yield return new ResultGraphic(GraphicKind.Polygon, [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], Verdict.None,
                AreaNumber(tool).ToString(System.Globalization.CultureInfo.CurrentCulture), tool.Name);
        }
    }

    /// <summary>A tap on the picture (pixels of the picture shown: JPEG, or camera once the full picture is
    /// shown, <see cref="ShownScale"/>): the tool whose code is there, else the
    /// smallest area holding the point, is selected. A drag is an area, not a tap (<see cref="AreaDrawn"/>).</summary>
    public void PictureTapped(Point2 from, Point2 to)
    {
        if (_last is not { } r) return;
        // pixels of the picture shown: the try's JPEG scale, or camera pixels once the full picture is shown (AS-17)
        var s = ShownScale;
        if (Math.Abs(to.X - from.X) >= 8 * s || Math.Abs(to.Y - from.Y) >= 8 * s) return;
        if (PickingSpot) { _ = SpotTappedAsync(from); return; }   // "Teach a spot" waits for this tap (BLB-OUT-07)
        double x = from.X / s, y = from.Y / s;
        var byCode = (r.Codes ?? []).Where(c => c.Corners is { Count: > 0 } k
                && x >= k.Min(p => p.X) && x <= k.Max(p => p.X) && y >= k.Min(p => p.Y) && y <= k.Max(p => p.Y))
            .Select(c => CameraTools.FirstOrDefault(t => t.Name == c.Tool)).FirstOrDefault(t => t is not null);
        var byArea = CameraTools.Where(t => t.Roi is { } a && x >= a.X && y >= a.Y && x <= a.X + a.Width && y <= a.Y + a.Height)
            .OrderBy(t => t.Roi!.Value.Width * (long)t.Roi!.Value.Height).FirstOrDefault();
        if ((byCode ?? byArea) is { } tool) SelectedTool = tool;
    }
}
