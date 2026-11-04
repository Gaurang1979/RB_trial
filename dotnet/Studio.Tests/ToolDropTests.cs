using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisionSystem.Studio.Controls;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using VisionSystem.Studio.Views;

namespace VisionSystem.Studio.Tests;

/// <summary>A tool dragged from the toolbox onto the picture (UI-UX-SPEC.md 5, AS-05): made where it is dropped,
/// snapped by the runtime's Teach to the nearest code or the text there, tried at once; "!" where nothing is found.</summary>
public class ToolDropViewModelTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    /// <summary>The job open with a picture tried: the JPEG is 640 x 480, half the 1280 x 960 picture.</summary>
    private async Task<DesignerViewModel> Open()
    {
        _client.JobFiles["Tablets"] = JobDraft.New("Tablets", 2).ToJson();
        _client.Products = [new("Tablets", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        await vm.TryCommand.ExecuteAsync(null);
        _client.Tries.Clear();
        return vm;
    }

    /// <summary>The runtime's answer to a teach: the tool in the request changed as a teach would, then tried.</summary>
    private TryOutcome Taught(TryRequest r, Action<ToolDraft> teach, string text, IReadOnlyList<CodeOutcome>? codes = null,
        IReadOnlyList<ResultGraphic>? graphics = null)
    {
        var job = JobDraft.Parse(r.Json);
        teach(job.Tools.Single(t => t.Name == r.TeachTool));
        return _client.TryDefault with { TaughtJson = job.ToJson(), TaughtText = text, Codes = codes ?? [], Graphics = graphics ?? [] };
    }

    private static (int X, int Y, int Width, int Height)? Roi(TryRequest r) =>
        JobDraft.Parse(r.Json).Tools.Single(t => t.Name == r.TeachTool).Roi;

    private static ResultGraphic Box(double x0, double y0, double x1, double y1, string label) =>
        new(GraphicKind.Polygon, [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], Verdict.Ok, label);

    [Fact]
    public async Task A_code_tool_dropped_on_the_picture_snaps_to_the_code_and_is_tried_at_once()
    {
        var vm = await Open();
        (Point2, Point2)? flashed = null;
        vm.AreaSnapped += (a, b) => flashed = (a, b);
        _client.TryWith = r => r.TeachTool is null ? null
            : Taught(r, t => { t.Format = "DataMatrix"; t.ExpectedCount = 1; t.Roi = (600, 450, 80, 80); }, "Found Data Matrix: ABC");

        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(320, 240));   // the JPEG's middle: (640, 480) in the picture

        var teach = Assert.Single(_client.Tries);
        Assert.Equal("Read code 1", teach.TeachTool);
        Assert.Equal("good1.bmp", teach.Stored);
        // it looked in its starting area: a quarter of the 960 px side (240 px) about the drop point
        Assert.Equal((520, 360, 240, 240), Roi(teach));
        Assert.Equal("Read code 1", vm.SelectedTool!.Name);
        Assert.Equal((600, 450, 80, 80), vm.SelectedTool.Roi);
        Assert.Equal("DataMatrix", vm.SelectedTool.Format);
        Assert.Equal("Found Data Matrix: ABC", vm.Message);
        Assert.Equal(Tone.Ok, vm.MessageTone);
        Assert.Single(vm.Results);   // the try of the taught job is shown
        Assert.Equal("✓", vm.Tree.Single(n => n.IsTool).Mark);
        Assert.Equal((new Point2(300, 225), new Point2(340, 265)), flashed);   // the snapped area, in JPEG pixels
        Assert.True(vm.Dirty);
        Assert.False(vm.Busy);
    }

    [Fact]
    public async Task Of_several_codes_found_the_one_nearest_the_drop_point_is_taken()
    {
        var vm = await Open();
        _client.TryWith = r =>
        {
            if (r.TeachTool is null) return null;
            if (Roi(r) is { Width: 240 })   // the first look: two codes of one type, with GS1 rules common to both
                return Taught(r, t => { t.ExpectedCount = 2; t.Gs1Check = true; t.Roi = (420, 260, 440, 440); }, "Found 2 codes of type QR Code",
                    [new("NEAR", "QrCode"), new("FAR", "QrCode")],
                    [Box(400, 300, 420, 320, "FAR"), Box(310, 230, 330, 250, "NEAR"), Box(0, 0, 10, 10, "Area")]);
            return Taught(r, t => { t.ExpectedCount = 1; t.Roi = Roi(r); }, "Found QR Code: NEAR");
        };

        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(316, 236));

        Assert.Equal(2, _client.Tries.Count);
        // the near code, 40 px wide in the picture, with a quarter of it spare at each side
        Assert.Equal((610, 450, 60, 60), Roi(_client.Tries[1]));
        Assert.False(JobDraft.Parse(_client.Tries[1].Json).Tools.Single().Gs1Check);   // taught on that code alone
        Assert.Equal(1, vm.SelectedTool!.ExpectedCount);
        Assert.Equal((610, 450, 60, 60), vm.SelectedTool.Roi);
        Assert.Equal("Found QR Code: NEAR", vm.Message);
    }

    [Fact]
    public async Task Nothing_found_leaves_the_area_where_it_was_dropped_marked_with_a_hint_and_tried()
    {
        var vm = await Open();
        _client.TryWith = r => r.TeachTool is null ? null
            : new TryOutcome(false, Refused: new CommandResult(false, "runtime.refused.teach", "No code was read here."));

        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(320, 240));

        Assert.Equal(3, _client.Tries.Count);
        Assert.Equal((520, 360, 240, 240), Roi(_client.Tries[0]));   // the starting area, then twice its size
        Assert.Equal((400, 240, 480, 480), Roi(_client.Tries[1]));
        Assert.Null(_client.Tries[2].TeachTool);   // then the camera's tools tried as they are
        Assert.Equal((520, 360, 240, 240), vm.SelectedTool!.Roi);
        var node = vm.Tree.Single(n => n.IsTool);
        Assert.Equal("!", node.Mark);
        Assert.Equal(Tone.Warn, node.Tone);
        Assert.Equal("No code was read here. " + Make.Strings["design.drop.left_code"], vm.Message);
        Assert.Equal(Tone.Warn, vm.MessageTone);

        // Save waits while the tool's area is a guess
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Empty(_client.Saved);
        Assert.Equal(Make.Strings.Format("design.drop.save_first", "Read code 1"), vm.Message);

        // another tool taught meanwhile (the draft replaced by its taught copy) leaves the "!"
        vm.AddCheckTextCommand.Execute(null);
        _client.TryWith = r => r.TeachTool == "Check text 1" ? Taught(r, t => t.AddLine("B.NO A1"), "Found 1 line(s) of text") : null;
        await vm.TeachCommand.ExecuteAsync(null);
        Assert.Equal("Check text 1", vm.SelectedTool!.Name);
        Assert.Equal("!", vm.Tree.First(n => n.IsTool).Mark);
        vm.SelectedTool = vm.CameraTools.First();

        // drawing the area round the code takes the "!" away, and Save goes ahead
        vm.AreaDrawn(new Point2(300, 220), new Point2(340, 260));
        Assert.NotEqual("!", vm.Tree.First(n => n.IsTool).Mark);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(_client.Saved);
    }

    [Fact]
    public async Task A_text_tool_is_taught_again_while_its_area_reaches_past_where_it_looked()
    {
        var vm = await Open();
        vm.CameraIndex = 1;
        await vm.TryCommand.ExecuteAsync(null);
        _client.Tries.Clear();
        var area = DesignerViewModel.DefaultArea(ToolKind.CheckText, 640, 480, 1280, 960);
        _client.TryWith = r =>
        {
            if (r.TeachTool is null) return null;
            return Roi(r) == area
                ? Taught(r, t => { t.Roi = (area.X - 60, 400, 700, 120); t.AddLine("B.NO A1"); }, "Found 1 line(s) of text")   // a line cut at the edge
                : Taught(r, t => { t.Roi = (area.X - 50, 405, 680, 110); t.AddLine("B.NO A12345"); }, "Found 1 line(s) of text: B.NO A12345");
        };

        await vm.DropToolAsync(ToolKind.CheckText, new Point2(320, 240));

        Assert.Equal(2, _client.Tries.Count);
        Assert.Equal(area, Roi(_client.Tries[0]));
        Assert.Equal((area.X - 60, 400, 700, 120), Roi(_client.Tries[1]));
        Assert.Equal("Check text 1", vm.SelectedTool!.Name);
        Assert.Equal((area.X - 50, 405, 680, 110), vm.SelectedTool.Roi);
        Assert.Equal("B.NO A12345", vm.SelectedTool.Lines.Last().Template);
        Assert.Equal(1, vm.CameraIndex);
    }

    [Fact]
    public async Task Nothing_in_the_starting_area_looks_in_twice_its_size()
    {
        var vm = await Open();
        var flashes = 0;
        vm.AreaSnapped += (_, _) => flashes++;
        _client.TryWith = r => r.TeachTool is null ? null
            : Roi(r) is { Width: 240 } ? new TryOutcome(false, Refused: new CommandResult(false, "runtime.refused.teach", "No code was read here."))
            : Taught(r, t => { t.ExpectedCount = 1; t.Roi = (420, 260, 60, 60); }, "Found QR Code: X");
        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(320, 240));
        Assert.Equal([(520, 360, 240, 240), (400, 240, 480, 480)], _client.Tries.Select(Roi));
        Assert.Equal((420, 260, 60, 60), vm.SelectedTool!.Roi);
        Assert.Equal(1, flashes);
        Assert.NotEqual("!", vm.Tree.Single(n => n.IsTool).Mark);
    }

    [Fact]
    public async Task A_refused_second_teach_keeps_the_codes_found_first_and_says_why()
    {
        var vm = await Open();
        var flashes = 0;
        vm.AreaSnapped += (_, _) => flashes++;
        _client.TryWith = r => r.TeachTool is null ? null
            : Roi(r) is { Width: 240 }
                ? Taught(r, t => { t.ExpectedCount = 2; t.Roi = (420, 260, 440, 440); }, "Found 2 codes of type QR Code",
                    [new("NEAR", "QrCode"), new("FAR", "QrCode")], [Box(310, 230, 330, 250, "NEAR"), Box(400, 300, 420, 320, "FAR")])
                : new TryOutcome(false, Refused: new CommandResult(false, "runtime.refused.elsewhere", "Stop the line first."));
        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(320, 240));
        Assert.Equal(2, _client.Tries.Count);
        Assert.Equal(2, vm.SelectedTool!.ExpectedCount);
        Assert.Equal((420, 260, 440, 440), vm.SelectedTool.Roi);
        Assert.Equal("Stop the line first.", vm.Message);
        Assert.Equal(Tone.Ng, vm.MessageTone);
        Assert.Equal(0, flashes);
    }

    [Fact]
    public async Task A_drop_needs_a_picture_on_screen_and_a_point_on_it()
    {
        _client.JobFiles["Tablets"] = JobDraft.New("Tablets", 2).ToJson();
        _client.Products = [new("Tablets", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");

        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(10, 10));   // nothing tried yet
        Assert.Empty(vm.CameraTools);
        Assert.Equal(Make.Strings["design.drop.try_first"], vm.Message);
        Assert.Null(vm.DropPreview(ToolKind.ReadCode, new Point2(10, 10)));

        await vm.TryCommand.ExecuteAsync(null);
        _client.Tries.Clear();
        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(700, 100));   // beside the 640 px wide JPEG
        Assert.Empty(vm.CameraTools);
        Assert.Empty(_client.Tries);
        // the dashed preview is the starting area, in JPEG pixels
        Assert.Equal((new Point2(260, 180), new Point2(380, 300)), vm.DropPreview(ToolKind.ReadCode, new Point2(320, 240)));
    }

    [Fact]
    public async Task A_refused_teach_keeps_the_tool_where_it_was_dropped_and_says_why()
    {
        var vm = await Open();
        _client.TryWith = r => new TryOutcome(false, Refused: new CommandResult(false, "runtime.refused.elsewhere", "Stop the line first."));
        await vm.DropToolAsync(ToolKind.ReadText, new Point2(320, 240));
        Assert.Single(_client.Tries);   // no try after a refusal
        Assert.Equal(DesignerViewModel.DefaultArea(ToolKind.ReadText, 640, 480, 1280, 960), vm.SelectedTool!.Roi);
        Assert.Equal("Stop the line first.", vm.Message);
        Assert.NotEqual("!", vm.Tree.Single(n => n.IsTool).Mark);

        // without undo, Remove tool (the Delete key) takes it back
        vm.RemoveToolCommand.Execute(null);
        Assert.Empty(vm.CameraTools);
    }
}

/// <summary>The drag itself in the Engineer workspace at the minimum screen, 1366 x 768, with the picture zoomed.</summary>
public class ToolDropLayoutTests
{
    private static Rect BoundsIn(Visual root, Control c) =>
        c.TranslatePoint(new Point(0, 0), root) is { } p ? new Rect(p, c.Bounds.Size) : default;

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_tile_dragged_onto_the_zoomed_picture_lands_where_it_is_dropped_and_Delete_takes_it_back()
    {
        Application.Current!.RequestedThemeVariant = App.VariantFor(AppTheme.Dark);
        var link = new ManualLink();
        var client = new ScriptedClient();
        var vm = new MainViewModel(Make.Strings, link, new ImmediateUi(), TimeProvider.System, App.Decode, AppTheme.Dark,
            t => Application.Current!.RequestedThemeVariant = App.VariantFor(t));
        var window = new MainWindow { DataContext = vm, Width = 1366, Height = 768 };
        window.Show();
        client.JobFiles["Tablets"] = JobDraft.New("Tablets", 2).ToJson();
        client.Products = [new("Tablets", "")];
        link.Connect(client, Make.Stopped());
        vm.User = new SignedInUser("eng", "Vision Engineer", Role.Engineer, true, MayDesignJobs: true);
        await vm.OpenDesignerCommand.ExecuteAsync(null);
        var designer = vm.Designer!;
        designer.SelectedJob = "Tablets";
        await Task.Delay(50);
        await designer.TryCommand.ExecuteAsync(null);
        window.FindDescendant<TabItem>("ToolboxTab").IsSelected = true;
        Settle(window);

        var screen = new Rect(0, 0, 1366, 768);
        foreach (var name in new[] { "AddReadCode", "AddReadText", "AddCheckText" })
        {
            var tile = BoundsIn(window, window.FindDescendant<Button>(name));
            Assert.True(screen.Contains(tile), $"{name} at {tile} is cut by the screen's edge");
            Assert.True(tile.Height >= 32, $"{name} only {tile.Height} px high");
        }

        var zoom = window.FindDescendant<ZoomBox>("Zoom");
        zoom.ZoomBy(2);
        Settle(window);
        var picture = window.FindDescendant<AreaDrag>("AreaDrag");
        var box = BoundsIn(window, zoom);
        // 40, 20 px right and down of the box's middle: half that from the middle of the picture zoomed 2x about it
        var drop = box.Center + new Point(40, 20);
        var (fit, ox, oy) = ResultOverlay.Fit(picture.Bounds.Size, 640, 480);
        var mid = new Point(picture.Bounds.Width / 2 + 20, picture.Bounds.Height / 2 + 10);
        var expected = new Point((mid.X - ox) / fit / 0.5, (mid.Y - oy) / fit / 0.5);   // full-image pixels
        client.TryWith = r => r.TeachTool is null ? null : client.TryDefault with { TaughtJson = r.Json, TaughtText = "Found QR Code: ABC" };

        Assert.NotNull(designer.DropPreview(ToolKind.ReadCode, new Point2(320, 240)));   // a picture is on screen
        var tileButton = window.FindDescendant<Button>("AddReadCode");
        var from = BoundsIn(window, tileButton).Center;
        window.MouseDown(from, MouseButton.Left);
        Assert.True(tileButton.IsPressed, "the tile did not take the press");
        window.MouseMove(from + new Point(12, 0), RawInputModifiers.LeftMouseButton);
        Assert.True(tileButton.Cursor is not null, "the press did not become a drag");
        window.MouseMove(drop, RawInputModifiers.LeftMouseButton);
        Settle(window);
        Assert.True(picture.ShowsDrop, $"no area shown at {drop} over {box}; picture {picture.Bounds} {picture.ImageWidth}x{picture.ImageHeight} visible {picture.IsEffectivelyVisible}");   // the starting area follows the pointer
        window.MouseUp(drop, MouseButton.Left);
        await Task.Delay(50);
        Settle(window);

        Assert.False(picture.ShowsDrop);
        var teach = Assert.Single(client.Tries, t => t.TeachTool is not null);
        Assert.Equal("Read code 1", teach.TeachTool);
        var roi = JobDraft.Parse(teach.Json).Tools.Single().Roi!.Value;
        Assert.InRange(roi.X + roi.Width / 2.0, expected.X - 2, expected.X + 2);
        Assert.InRange(roi.Y + roi.Height / 2.0, expected.Y - 2, expected.Y + 2);
        Assert.Equal("Read code 1", designer.SelectedTool!.Name);
        Assert.Equal(1, picture.Flashes);   // the snapped area lit once

        // Delete (not in a text box) takes the dropped tool back
        window.FindDescendant<Button>("AddReadCode").Focus();
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Settle(window);
        Assert.Empty(designer.CameraTools);

        // a click without moving still adds the tool (tap-to-add), with no teach
        client.Tries.Clear();
        var text = BoundsIn(window, window.FindDescendant<Button>("AddReadText")).Center;
        window.MouseDown(text, MouseButton.Left);
        window.MouseUp(text, MouseButton.Left);
        Settle(window);
        Assert.Equal("Read text 1", Assert.Single(designer.CameraTools).Name);
        Assert.Empty(client.Tries);

        // a drag let go beside the picture drops nothing
        window.MouseDown(text, MouseButton.Left);
        window.MouseMove(text + new Point(12, 0), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(4, 760), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(4, 760), MouseButton.Left);
        Settle(window);
        Assert.Single(designer.CameraTools);
        Assert.Empty(client.Tries);
        window.Close();
    }
}
