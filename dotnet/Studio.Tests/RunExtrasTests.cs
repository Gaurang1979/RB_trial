using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Studio.Tests;

public class GraphicHitTests
{
    private static ResultGraphic Box(double x0, double y0, double x1, double y1, Verdict v, string tip) =>
        new(GraphicKind.Polygon, [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], v, null, tip);

    [Fact]
    public void A_tap_inside_a_box_finds_it_and_outside_finds_nothing()
    {
        var g = new[] { Box(10, 10, 50, 50, Verdict.Ok, "Code 1: A") };
        Assert.Equal("Code 1: A", GraphicHit.Text(GraphicHit.Find(g, new(30, 30), 2)!));
        Assert.Null(GraphicHit.Find(g, new(80, 80), 2));
        Assert.NotNull(GraphicHit.Find(g, new(52, 30), 3));   // near the edge counts
    }

    [Fact]
    public void Where_shapes_overlap_the_worst_and_then_the_smallest_wins()
    {
        var region = Box(0, 0, 100, 100, Verdict.Ok, "Label");
        var bad = Box(20, 20, 40, 40, Verdict.Ng, "Code not read");
        var small = Box(60, 60, 70, 70, Verdict.Ok, "Code 2");
        Assert.Same(bad, GraphicHit.Find([region, bad, small], new(30, 30), 1));
        Assert.Same(small, GraphicHit.Find([region, bad, small], new(65, 65), 1));
    }

    [Fact]
    public void Lines_and_crosses_are_hit_within_the_slop()
    {
        var line = new ResultGraphic(GraphicKind.Polyline, [new(0, 0), new(100, 0)], Verdict.Warn, "Edge");
        var cross = new ResultGraphic(GraphicKind.Cross, [new(200, 200)], Verdict.Info, null, "Centre");
        Assert.Same(line, GraphicHit.Find([line, cross], new(50, 4), 5));
        Assert.Null(GraphicHit.Find([line, cross], new(50, 12), 5));
        Assert.Same(cross, GraphicHit.Find([line, cross], new(206, 200), 5));
    }
}

public class RunExtrasTests
{
    private sealed class Overlays : IOverlayHost
    {
        public object? Shown { get; private set; }
        public void Show(object overlay) => Shown = overlay;
        public void Close() => Shown = null;
        public void CloseIf(object overlay)
        {
            if (ReferenceEquals(Shown, overlay)) Shown = null;
        }
    }

    private sealed class Decoded(byte[] data) : Avalonia.Media.IImage
    {
        public byte[] Data { get; } = data;
        public Avalonia.Size Size => new(10, 10);
        public void Draw(Avalonia.Media.DrawingContext context, Avalonia.Rect sourceRect, Avalonia.Rect destRect) { }
    }

    private readonly ManualLink _link = new();
    private readonly ScriptedClient _client = new();
    private readonly ManualTime _time = new();
    private readonly Overlays _overlays = new();
    private readonly RunViewModel _vm;

    public RunExtrasTests()
    {
        _vm = new RunViewModel(Make.Strings, new RuntimeCalls(_link), _overlays, _time, d => new Decoded(d));
        _link.StateChanged += s => _vm.Apply(s, null);
        _link.StatusReceived += s => _vm.Apply(_link.State, s);
        _link.FrameReceived += _vm.OnFrame;
    }

    private void Connect(RuntimeStatus status)
    {
        _client.Status = status;
        _link.Connect(_client, status);
    }

    private static LiveFrame Bad(ulong seq, string? imageId) =>
        new(seq, DateTimeOffset.Now, [9], 800, 600, Verdict.Ng,
        [
            new(GraphicKind.Polygon, [new(10, 10), new(90, 10), new(90, 90), new(10, 90)], Verdict.Ng, "Code", "Code 1 not read: blur"),
            new(GraphicKind.Polygon, [new(110, 10), new(190, 10), new(190, 90), new(110, 90)], Verdict.Ok, "Code", "Code 2: RJ1"),
            new(GraphicKind.Polygon, [new(210, 10), new(290, 10), new(290, 90), new(210, 90)], Verdict.Ok, "Code", "Code 3: RJ2"),
        ], imageId);

    [Fact]
    public void A_tap_on_a_graphic_shows_its_tooltip_for_five_seconds_on_the_same_spot()
    {
        Connect(Make.Running());
        _link.Push(Bad(1, null));
        _vm.TapImage(new Point2(150, 50), 4);
        Assert.Equal("Code 2: RJ1", _vm.Tiles[0].TooltipText);
        Assert.Equal(new Point2(150, 50), _vm.Tiles[0].TooltipAt);

        // The next part: the same spot explains the new part's graphic.
        _link.Push(Bad(2, null) with { Graphics = [new(GraphicKind.Polygon, [new(110, 10), new(190, 10), new(190, 90), new(110, 90)], Verdict.Duplicate, null, "Code 2 seen before: RJ1")] });
        Assert.Equal("Code 2 seen before: RJ1", _vm.Tiles[0].TooltipText);

        _time.Advance(TimeSpan.FromSeconds(5));
        _vm.Tick();
        Assert.Null(_vm.Tiles[0].TooltipText);

        _vm.TapImage(new Point2(150, 50), 4);
        Assert.NotNull(_vm.Tiles[0].TooltipText);
        _vm.TapImage(new Point2(700, 500), 4);   // on nothing: hides it
        Assert.Null(_vm.Tiles[0].TooltipText);
    }

    [Fact]
    public void Yield_ring_and_cycle_time_come_from_GetCounters_every_two_seconds()
    {
        Connect(Make.Running(good: 90, bad: 10));
        Assert.Equal(90, _vm.YieldValue, 3);   // from the status until GetCounters answers
        Assert.Equal("–", _vm.CycleText);

        _client.Counters = new RunCounters(200, 199, 1, 99.5, 52.4, 88);
        _vm.Tick();
        Assert.Equal(99.5, _vm.YieldValue, 3);
        Assert.Equal("52 ms", _vm.CycleText);
        Assert.Equal("max 88 ms", _vm.CycleMaxText);
        Assert.Equal((99.5).ToString("0.0") + " %", _vm.GoodPercentText);

        _vm.Tick();
        Assert.Single(_client.Calls, c => c == "GetCounters");
        _time.Advance(RunViewModel.CountersEvery);
        _vm.Tick();
        Assert.Equal(2, _client.Calls.Count(c => c == "GetCounters"));

        // Not reachable: the counters keep the last numbers the runtime sent (as Good / Bad do);
        // the cycle time of a batch that may have ended is not shown.
        _link.Drop();
        Assert.Equal(90, _vm.YieldValue, 3);
        Assert.Equal("–", _vm.CycleText);
    }

    [Fact]
    public async Task A_tap_on_an_NG_thumbnail_opens_the_viewer_with_the_full_image()
    {
        Connect(Make.Running());
        _client.Images["img-1"] = new FullImage([1, 1, 1], 1600, 1200);
        _link.Push(Bad(1, "img-1"));
        _link.Push(Bad(2, "img-2"));   // the runtime no longer keeps this one
        Assert.Equal(2, _vm.NgStrip.Count);

        _vm.OpenNgCommand.Execute(_vm.NgStrip[1]);   // the older part, img-1
        var viewer = Assert.IsType<NgViewerViewModel>(_overlays.Shown);
        await Until(() => viewer.ShowsFullImage);
        Assert.Contains("GetImage img-1", _client.Calls);
        Assert.Equal([1, 1, 1], Assert.IsType<Decoded>(viewer.Image).Data);
        Assert.Equal("", viewer.ImageNote);
        Assert.StartsWith("Bad part ", viewer.Title);
        Assert.Equal("Image 2 of 2", viewer.PositionText);
        Assert.Equal(["Code 1 not read: blur", "Passed: 2"], viewer.Checks.Select(c => c.Text));
        Assert.Equal(Tone.Ng, viewer.Checks[0].Tone);
        Assert.True(viewer.HasPrevious);
        Assert.False(viewer.HasNext);

        await viewer.PreviousCommand.ExecuteAsync(null);
        Assert.Equal("Image 1 of 2", viewer.PositionText);
        Assert.False(viewer.ShowsFullImage);
        Assert.Equal("The full image is no longer kept. This is the live picture.", viewer.ImageNote);
        Assert.Equal([9], Assert.IsType<Decoded>(viewer.Image).Data);   // the live picture

        viewer.TapImage(new Point2(50, 50), 4);
        Assert.Equal("Code 1 not read: blur", viewer.TooltipText);
        viewer.BackCommand.Execute(null);
        Assert.Null(_overlays.Shown);
    }

    [Fact]
    public async Task Change_product_shows_the_pictures_and_asks_for_each_only_once()
    {
        Connect(Make.Stopped());
        _client.Products = [new("Whisky 750 ml", "LIQ-750-48", "picture-1"), new("Tablets", "PH-24", "picture-2"),
            new("Tablets", "PH-12", "picture-2"), new("Label", "", "")];
        _client.Images["picture-1"] = new FullImage([7], 320, 240);
        _vm.ChangeProductCommand.Execute(null);
        var picker = Assert.IsType<ProductPickerViewModel>(_overlays.Shown);
        await Until(() => picker.Products.Count == 4 && picker.Products[0].HasPicture);
        Assert.False(picker.Products[1].HasPicture);   // the runtime has none: the letter stays
        Assert.False(picker.Products[3].HasPicture);
        Assert.Single(_client.Calls, c => c == "GetImage picture-2");   // two variants, one picture

        _overlays.Close();
        _vm.ChangeProductCommand.Execute(null);
        var again = Assert.IsType<ProductPickerViewModel>(_overlays.Shown);
        await Until(() => again.Products.Count == 4 && again.Products[0].HasPicture);
        Assert.Single(_client.Calls, c => c == "GetImage picture-1");   // kept from the first time
    }

    [Fact]
    public void What_do_I_do_shows_the_alarm_steps_and_Done_moves_to_the_next()
    {
        Connect(Make.Running() with { Alarm = "Image too dark. Check the light.", AlarmHelp = "1. Look at the light.\n2) Check the cable.\n\nCall your supervisor." });
        Assert.True(_vm.HasBannerHelp);
        _vm.WhatToDoCommand.Execute(null);
        var help = Assert.IsType<AlarmHelpViewModel>(_overlays.Shown);
        Assert.Equal("What do I do?", help.Title);
        Assert.Equal("Image too dark. Check the light.", help.Alarm);
        Assert.Equal(["Look at the light.", "Check the cable.", "Call your supervisor."], help.Steps.Select(s => s.Text));
        Assert.True(help.Steps[0].IsCurrent);
        help.DoneCommand.Execute(help.Steps[0]);
        Assert.True(help.Steps[0].Done);
        Assert.True(help.Steps[1].IsCurrent);
        help.BackCommand.Execute(null);
        Assert.Null(_overlays.Shown);

        // The app's own banners and an alarm without steps have no button.
        _link.Push(Make.Running() with { Alarm = "Reject bin full." });
        Assert.False(_vm.HasBannerHelp);
        _link.Push(Make.Running() with { PlcOk = false, Alarm = "x", AlarmHelp = "y" });
        Assert.False(_vm.HasBannerHelp);
        _link.Push(Make.Running() with { Warning = "Print faint.", WarningHelp = "Clean the head." });
        Assert.True(_vm.HasBannerHelp);
    }

    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(condition());
    }
}

/// <summary>The contract's new fields reach the app: tooltips, image ids, counters, pictures, alarm help.</summary>
public class ContractExtrasMappingTests
{
    [Fact]
    public void Tooltip_and_image_id_come_with_the_frame()
    {
        var f = new V1.LiveFrame { InspectionId = 4, Width = 100, Height = 80, Pass = false, ImageId = "img-4" };
        var g = new V1.Graphic { Kind = V1.Graphic.Types.Kind.Rect, Colour = "status.ng", Tooltip = "Code 3 not read: blur" };
        g.Points.Add(new V1.Point { X = 1, Y = 2 });
        g.Points.Add(new V1.Point { X = 9, Y = 8 });
        f.Graphics.Add(g);
        var m = GrpcRuntimeClient.Map(f);
        Assert.Equal("img-4", m.ImageId);
        Assert.Equal("Code 3 not read: blur", m.Graphics[0].Tooltip);
        Assert.Null(GrpcRuntimeClient.Map(new V1.LiveFrame { InspectionId = 5 }).ImageId);
    }

    [Fact]
    public void Counters_and_alarm_help_are_mapped()
    {
        var c = GrpcRuntimeClient.Map(new V1.Counters { Total = 10, Ok = 9, Ng = 1, YieldPercent = 90, MedianCycleMs = 41, MaxCycleMs = 77 });
        Assert.Equal(new RunCounters(10, 9, 1, 90, 41, 77), c);

        var s = new V1.Status { PlcConnected = true, LicenceOk = true };
        s.Alarms.Add(new V1.Alarm { Level = V1.Alarm.Types.Level.Alarm, Text = "Too dark.", Help = "Check the light." });
        s.Alarms.Add(new V1.Alarm { Level = V1.Alarm.Types.Level.Warning, Text = "Print faint." });
        var m = GrpcRuntimeClient.Map(s);
        Assert.Equal(("Too dark.", "Check the light."), (m.Alarm, m.AlarmHelp));
        Assert.Equal("Print faint.", m.Warning);
        Assert.Null(m.WarningHelp);   // empty help: no "What do I do?" button
    }
}
