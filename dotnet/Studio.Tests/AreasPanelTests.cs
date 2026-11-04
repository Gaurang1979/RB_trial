using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using VisionSystem.Studio.Views;

namespace VisionSystem.Studio.Tests;

internal static class TestPanelMemory
{
    /// <summary>The tests never write the engineer's own workspace file.</summary>
    [ModuleInitializer]
    internal static void InMemory() => PanelMemory.Shared = new PanelMemory(null);
}

/// <summary>
/// Several areas on one picture and the "Areas and values" panel (requirement AS-15; UI-UX-SPEC.md 4.19 and 5
/// "Values after a drop"): one row per tool line with what it found and its suggested format, failing and empty
/// rows first, selection both ways, formats taken into the draft only, suggestions greyed, the panel remembered.
/// </summary>
public class AreasPanelTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    private const string Gs1 = "0108901234567890 17271031 10A123 2100042";

    /// <summary>A code tool and a two-line Read text tool on camera 1, both with an area; the try reads a GS1 code
    /// and one line of the two (the second line was not found, so the text tool fails).</summary>
    private async Task<DesignerViewModel> Open()
    {
        var draft = JobDraft.New("Tablets", 1);
        var code = draft.AddTool(ToolKind.ReadCode, 0);
        code.Format = "DataMatrix";
        code.Roi = (100, 100, 200, 200);
        var text = draft.AddTool(ToolKind.ReadText, 0);
        text.Roi = (400, 100, 600, 200);
        text.AddLine("");
        text.AddLine("B.NO {X:5}");
        _client.JobFiles["Tablets"] = draft.ToJson();
        _client.Products = [new("Tablets", "")];
        _client.TryDefault = new TryOutcome(true, false, 30, [1, 2, 3], 640, 480, 0.5, 1280, 960,
            [new ToolTried("Read code 1", "barcode", true, "Read", 8), new ToolTried("Read text 1", "ocr", false, "Line 2 not found", 20)],
            [new CodeOutcome(Gs1, "DataMatrix", "", [new(120, 120), new(280, 120), new(280, 280), new(120, 280)],
                new Dictionary<string, string> { ["01"] = "08901234567890", ["17"] = "271031", ["10"] = "A123", ["21"] = "00042" },
                "GS1: GTIN (01), expiry date (17), batch (10), serial (21)", "Read code 1")],
            [], [new TextLineRead("Read text 1", "EXP 10/2027", "", true, 92, "EXP {EXP:MM/YYYY}", "expiry date")]);
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        return vm;
    }

    [Fact]
    public async Task Each_tool_line_has_a_row_with_its_area_value_and_format_failing_and_empty_first()
    {
        var vm = await Open();
        var rows = vm.Areas.Rows;
        // nothing tried yet: every line has no value, in red
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.Failed && !r.HasFound));

        await vm.TryCommand.ExecuteAsync(null);
        rows = vm.Areas.Rows;
        Assert.Equal(["2 Line 1", "2 Line 2", "1 "], rows.Select(r => $"{r.Area} {r.Line}"));
        var missing = rows[1];
        Assert.True(missing.Failed);
        Assert.Equal(Tone.Ng, missing.Tone);
        Assert.Equal("", missing.Found);

        var code = rows[2];
        Assert.Equal("Read code 1", code.ToolName);
        Assert.False(code.Failed);
        Assert.Equal(Gs1, code.Found);
        Assert.Equal("GS1: GTIN (01), expiry date (17), batch (10), serial (21)", code.Field);
        Assert.Equal("01, 17, 10, 21", code.Suggested);

        var line = rows[0];
        Assert.Equal("EXP 10/2027", line.Found);
        Assert.Equal("expiry date · EXP {EXP:MM/YYYY}", line.SuggestedText);
        Assert.Equal("Line 2 not found", line.Note);   // the tool's reason on its first line
        Assert.True(line.Failed);                       // the tool failed: its first row is red too
        Assert.Equal("Lines 3, to look at 2", vm.Areas.CountText);
    }

    [Fact]
    public async Task Use_this_format_changes_the_draft_only()
    {
        var vm = await Open();
        await vm.TryCommand.ExecuteAsync(null);
        var line = vm.Areas.Rows.First(r => r.Found == "EXP 10/2027");
        Assert.True(line.CanUse);
        line.UseCommand.Execute(null);
        var text = vm.CameraTools[1];
        Assert.Equal("EXP {EXP:MM/YYYY}", text.Lines[0].Template);
        Assert.Equal("EXP {EXP:MM/YYYY}", line.Format);
        Assert.False(line.CanUse);   // taken

        var code = vm.Areas.Rows.First(r => r.Tool == vm.CameraTools[0]);
        code.UseCommand.Execute(null);
        Assert.True(vm.CameraTools[0].Gs1Check);
        Assert.Equal("01, 17, 10, 21", vm.CameraTools[0].Gs1Required);

        // the box changes the format too; a Check text line keeps the template the engineer wrote
        line.Format = "EXP {EXP:MM/YY}";
        Assert.Equal("EXP {EXP:MM/YY}", text.Lines[0].Template);
        Assert.True(vm.Dirty);
        Assert.Empty(_client.Saved);   // nothing reaches the runtime's job until Save
    }

    [Fact]
    public async Task A_line_read_that_the_tool_has_no_line_for_is_added_with_its_format()
    {
        var vm = await Open();
        var text = vm.CameraTools[1];
        text.RemoveLine(text.Lines[1]);
        text.RemoveLine(text.Lines[0]);
        await vm.TryCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Areas.Rows, r => r.Tool == text);
        Assert.Equal("EXP 10/2027", row.Found);
        row.UseCommand.Execute(null);
        Assert.Equal("EXP {EXP:MM/YYYY}", Assert.Single(text.Lines).Template);
        Assert.Equal(text.Lines[0].Template, Assert.Single(vm.Areas.Rows, r => r.Tool == text).Format);   // its row now edits the line
    }

    [Fact]
    public async Task Selecting_a_row_selects_its_area_and_the_other_way_round()
    {
        var vm = await Open();
        await vm.TryCommand.ExecuteAsync(null);
        var text = vm.CameraTools[1];
        vm.Areas.SelectedRow = vm.Areas.Rows.First(r => r.Tool == text);
        Assert.Same(text, vm.SelectedTool);
        Assert.All(vm.Areas.Rows, r => Assert.Equal(r.Tool == text, r.Selected));   // both its lines

        vm.SelectedTool = vm.CameraTools[0];
        Assert.Same(vm.CameraTools[0], vm.Areas.SelectedRow?.Tool);

        // a tap on the picture (JPEG pixels, scale 0.5) inside the text tool's area selects it
        vm.PictureTapped(new Point2(300, 100), new Point2(301, 100));
        Assert.Same(text, vm.SelectedTool);
        Assert.Same(text, vm.Areas.SelectedRow?.Tool);
        // on the code: the code's tool; a drag is not a tap
        vm.PictureTapped(new Point2(100, 100), new Point2(100, 100));
        Assert.Same(vm.CameraTools[0], vm.SelectedTool);
        vm.PictureTapped(new Point2(300, 100), new Point2(340, 140));
        Assert.Same(vm.CameraTools[0], vm.SelectedTool);
    }

    [Fact]
    public async Task Each_area_carries_its_number_on_the_picture()
    {
        var vm = await Open();
        await vm.TryCommand.ExecuteAsync(null);
        vm.SelectedTool = vm.CameraTools[0];
        var selected = Assert.Single(vm.Graphics, g => g.Verdict == Verdict.Info);
        Assert.Equal("1 · Area", selected.Label);
        var other = Assert.Single(vm.Graphics, g => g.Verdict == Verdict.None);
        Assert.Equal("2", other.Label);
        Assert.Equal(200, other.Points[0].X);   // full-image 400 × the JPEG's scale
        Assert.Equal("Read text 1", other.Tooltip);
    }

    [Fact]
    public async Task A_tool_added_shows_at_once_and_its_values_after_the_next_try()
    {
        var vm = await Open();
        await vm.TryCommand.ExecuteAsync(null);
        vm.AddReadCodeCommand.Execute(null);   // as a tool dropped or drawn: a new area of the camera
        var added = vm.SelectedTool!;
        var row = Assert.Single(vm.Areas.Rows, r => r.Tool == added);
        Assert.Equal(3, row.Area);
        Assert.True(row.Failed && !row.HasFound);
        Assert.True(vm.Areas.Rows.IndexOf(row) < vm.Areas.Rows.IndexOf(vm.Areas.Rows.First(r => r.Tool == vm.CameraTools[0])));   // no value yet: above the rows that read
        Assert.True(row.Selected);

        var tried = _client.TryDefault;
        _client.TryDefault = tried with
        {
            Tools = [.. tried.Tools!, new ToolTried("Read code 2", "barcode", true, "Read", 5)],
            Codes = [.. tried.Codes!, new CodeOutcome("ABC123", "QrCode", Tool: "Read code 2")],
        };
        await vm.TryCommand.ExecuteAsync(null);
        row = Assert.Single(vm.Areas.Rows, r => r.Tool == added);
        Assert.Equal("ABC123", row.Found);
        Assert.Equal("QrCode", row.SuggestedText);
        Assert.False(row.CanUse);   // no GS1 data: nothing to take; the box holds the expected text
    }

    [Fact]
    public async Task A_tool_dropped_from_the_toolbox_shows_its_value_at_once()
    {
        _client.JobFiles["Tablets"] = JobDraft.New("Tablets", 1).ToJson();
        _client.Products = [new("Tablets", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        await vm.TryCommand.ExecuteAsync(null);
        Assert.True(vm.Areas.IsEmpty);
        // the runtime's teach snaps the dropped tool onto the code and tries it (UI-UX-SPEC.md 5 "Values after a drop")
        _client.TryWith = r =>
        {
            if (r.TeachTool is null) return null;
            var job = JobDraft.Parse(r.Json);
            var tool = job.Tools.Single(t => t.Name == r.TeachTool);
            (tool.Format, tool.Roi) = ("DataMatrix", (600, 450, 80, 80));
            return _client.TryDefault with
            {
                TaughtJson = job.ToJson(), TaughtText = "Found Data Matrix",
                Codes = [new CodeOutcome(Gs1, "DataMatrix", "", [new(610, 460), new(670, 460), new(670, 520), new(610, 520)],
                    null, "GS1: GTIN (01), expiry date (17), batch (10), serial (21)", "Read code 1")],
            };
        };
        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(320, 240));
        var row = Assert.Single(vm.Areas.Rows);
        Assert.Equal(1, row.Area);
        Assert.Same(vm.SelectedTool, row.Tool);
        Assert.True(row.Selected);
        Assert.False(row.Failed);
        Assert.Equal(Gs1, row.Found);
        Assert.Equal("01, 17, 10, 21", row.Suggested);
        Assert.Equal("1 · Area", Assert.Single(vm.Graphics, g => g.Verdict == Verdict.Info).Label);
    }

    [Fact]
    public void Suggestions_not_yet_accepted_come_last_greyed_with_Accept_and_Not_this()
    {
        var panel = new AreasPanelViewModel(Make.Strings, new PanelMemory(null));
        var draft = JobDraft.New("J", 1);
        var tool = draft.AddTool(ToolKind.ReadCode, 0);
        var key = new object();
        var p = new PendingArea(key, "Text, 2 lines: expiry date, lot / batch", ["EXP 10/2027", "B.NO A123"], ["expiry date", "lot / batch"]);
        panel.Show([tool], null, [p], null);
        Assert.Equal(3, panel.Rows.Count);
        Assert.False(panel.Rows[0].Pending);
        Assert.Equal([2, 2], panel.Rows.Skip(1).Select(r => r.Area));
        Assert.All(panel.Rows.Skip(1), r => Assert.True(r.Pending && !r.Failed && r.Tone == Tone.Neutral && !r.HasFormat));
        Assert.Equal("lot / batch", panel.Rows[2].Field);
        Assert.Equal("Lines 1, to look at 1", panel.CountText);

        PendingArea? accepted = null, dismissed = null;
        panel.Accepted += a => accepted = a;
        panel.Dismissed += a => dismissed = a;
        panel.AcceptCommand.Execute(panel.Rows[1]);
        panel.NotThisCommand.Execute(panel.Rows[2]);
        Assert.Same(key, accepted?.Key);
        Assert.Same(key, dismissed?.Key);
        Assert.False(panel.Rows[0].Pending);
    }

    [Fact]
    public async Task The_panel_folds_with_Ctrl_Shift_A_and_remembers_it()
    {
        var file = Path.Combine(Path.GetTempPath(), $"vs-areas-{Guid.NewGuid():N}.json");
        try
        {
            var memory = new PanelMemory(file);
            var panel = new AreasPanelViewModel(Make.Strings, memory);
            Assert.True(panel.Open);   // open the first time
            panel.ToggleCommand.Execute(null);
            Assert.False(panel.Open);
            Assert.False(new AreasPanelViewModel(Make.Strings, new PanelMemory(file)).Open);   // a new session: still folded
            File.WriteAllText(file, "not json");
            Assert.True(new AreasPanelViewModel(Make.Strings, new PanelMemory(file)).Open);   // unreadable: the default
        }
        finally { File.Delete(file); }

        var vm = await Open();
        var open = vm.Areas.Open;
        vm.Shortcut("areas");
        Assert.NotEqual(open, vm.Areas.Open);
        vm.Shortcut("areas");
        Assert.Equal(open, vm.Areas.Open);
    }

    private static readonly ToolSuggestion Lot = new("text", "Text, 2 lines: production date, lot / batch",
        ["MFG 10/2026", "B.NO AB123"], ["production date", "lot / batch"],
        [new(400, 500), new(800, 500), new(800, 600), new(400, 600)],
        """{"kind":"text","Name":"Text 1","Camera":0,"Enabled":true,"Mode":"Read","Lines":[{"Template":"MFG {MFG:MM/YYYY}"}]}""");

    private static readonly ToolSuggestion Qr = new("code", "QR Code", ["A1"], ["code"],
        [new(900, 100), new(1100, 100), new(1100, 300), new(900, 300)],
        """{"kind":"code","Name":"QR Code","Camera":0,"Enabled":true,"Format":"QrCode","ExpectedCount":1}""");

    /// <summary>The greyed rows are auto find's own suggestion list (AS-01): they are numbered after the camera's
    /// tools on the picture as in the panel, and Accept and Not this are its accept and dismiss, not a second list.</summary>
    [Fact]
    public async Task Auto_finds_suggestions_are_the_greyed_rows_and_Accept_and_Not_this_are_its_own()
    {
        var vm = await Open();
        await vm.TryCommand.ExecuteAsync(null);
        _client.FindAnswer = new FindOutcome(true, [Lot, Qr], 840, [1, 2, 3], 640, 480, 0.5, 1280, 960);
        await vm.FindEverythingCommand.ExecuteAsync(null);

        // the camera has 2 tools, so the suggestions are areas 3 and 4, here and on the picture
        var pending = vm.Areas.Rows.Where(r => r.Pending).ToList();
        Assert.Equal([3, 3, 4], pending.Select(r => r.Area));
        Assert.Equal(["MFG 10/2026", "B.NO AB123", "A1"], pending.Select(r => r.Found));
        Assert.Equal(["production date", "lot / batch", ""], pending.Select(r => r.Field));
        Assert.All(pending, r => Assert.True(r.Tone == Tone.Neutral && !r.HasFormat));
        Assert.Equal([("3", Lot.Summary), ("4", Qr.Summary)],
            vm.Graphics.Where(g => g.Tooltip == Lot.Summary || g.Tooltip == Qr.Summary).Select(g => (g.Label, g.Tooltip)).Order());

        // Accept on the QR row adds that tool through auto find's own accept: one list, and the rest renumbered
        vm.Areas.AcceptCommand.Execute(pending[2]);
        await vm.PanelAccept;
        Assert.Equal("QR Code", vm.Job!.Tools.Single(t => t.IsCode && t.Format == "QrCode").Name);
        Assert.Equal(3, vm.CameraTools.Count);
        Assert.Equal(["Text, 2 lines: production date, lot / batch"], vm.Suggestions.Select(s => s.Summary));
        Assert.Equal([4, 4], vm.Areas.Rows.Where(r => r.Pending).Select(r => r.Area));
        Assert.Equal("4", Assert.Single(vm.Graphics, g => g.Tooltip == Lot.Summary).Label);

        // Not this drops it from auto find's list, so no greyed row is left
        vm.Areas.NotThisCommand.Execute(vm.Areas.Rows.First(r => r.Pending));
        Assert.Empty(vm.Suggestions);
        Assert.False(vm.HasSuggestions);
        Assert.DoesNotContain(vm.Areas.Rows, r => r.Pending);
        Assert.DoesNotContain(vm.Graphics, g => g.Label == "4");
    }

    /// <summary>Above Fit the full picture is in camera pixels (AS-17): the area numbers and the tap use
    /// <c>ShownScale</c>, not the try's JPEG scale, so both stay on the picture under them.</summary>
    [Fact]
    public async Task The_area_numbers_and_the_tap_follow_the_picture_shown_above_fit()
    {
        var vm = await Open();
        _client.TryDefault = _client.TryDefault with { ImageId = "try-1" };
        _client.Images["try-1"] = new FullImage([1], 1280, 960);
        await vm.TryCommand.ExecuteAsync(null);
        var code = vm.CameraTools[0];
        var text = vm.CameraTools[1];        // area (400, 100, 600, 200) in camera pixels
        vm.SelectedTool = code;

        // at fit the picture is the 640 x 480 JPEG: the other tool's number sits on its half-size area
        Assert.Equal(0.5, vm.ShownScale);
        var atFit = Assert.Single(vm.Graphics, g => g.Label == "2").Points;
        Assert.Equal(new Point2[] { new(200, 50), new(500, 50), new(500, 150), new(200, 150) }, atFit);
        vm.PictureTapped(new Point2(210, 60), new Point2(213, 63));   // 3 JPEG pixels: still a tap
        Assert.Same(text, vm.SelectedTool);
        vm.SelectedTool = code;
        vm.PictureTapped(new Point2(210, 60), new Point2(215, 60));   // 5 >= 8 x 0.5: a drag, not a tap
        Assert.Same(code, vm.SelectedTool);

        // above fit the full picture is swapped in: the same area, now on camera pixels
        vm.ShowZoom(2);
        await vm.FullPictureFetch;
        Assert.True(vm.ShowsFullPicture);
        Assert.Equal(1, vm.ShownScale);
        var onFull = Assert.Single(vm.Graphics, g => g.Label == "2").Points;
        Assert.Equal(new Point2[] { new(400, 100), new(1000, 100), new(1000, 300), new(400, 300) }, onFull);
        vm.PictureTapped(new Point2(420, 120), new Point2(426, 120));   // 6 < 8 camera pixels: a tap in its area
        Assert.Same(text, vm.SelectedTool);
        vm.SelectedTool = text;
        vm.PictureTapped(new Point2(200, 200), new Point2(200, 200));   // the code's own area, read from the full picture
        Assert.Same(code, vm.SelectedTool);
    }

    [AvaloniaFact]
    public async Task The_panel_shows_its_rows_in_the_right_dock_and_folds_to_its_header()
    {
        var vm = await Open();
        await vm.TryCommand.ExecuteAsync(null);
        vm.Areas.Open = true;
        var view = new DesignerView { DataContext = vm };
        var window = new Window { Width = 1366, Height = 768, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var body = window.FindDescendant<Control>("AreasBody");
        Assert.True(body.IsEffectivelyVisible);
        Assert.Equal(3, window.FindDescendant<ListBox>("AreaRows").ItemCount);
        window.FindDescendant<Button>("AreasChevron").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(body.IsEffectivelyVisible);
        Assert.True(window.FindDescendant<Control>("AreasCount").IsEffectivelyVisible);
        window.Close();
    }
}
