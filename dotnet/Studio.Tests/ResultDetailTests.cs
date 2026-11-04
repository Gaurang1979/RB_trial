using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Studio.Tests;

/// <summary>The Read code add-on and Check text's PrintQuality on the result (tool spec 5.2, 7.3).</summary>
public class ResultDetailTests
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

    private static ResultGraphic Box(double x0, Verdict v, string label) =>
        new(GraphicKind.Polygon, [new(x0, 10), new(x0 + 80, 10), new(x0 + 80, 90), new(x0, 90)], v, label, label);

    // A Check text tool that failed with print quality 41, a Read code tool whose EAN-13 carries the
    // add-on 12, and a second code with none.
    private static LiveFrame Part() =>
        new(5, DateTimeOffset.Now, [9], 800, 600, Verdict.Ng,
            [Box(10, Verdict.Ok, "4006381333931"), Box(110, Verdict.Ng, "LOT A123"), Box(210, Verdict.Ok, "9780201379624")],
            null,
            [new ToolOutcome("Check text 1", false, 41.4f), new ToolOutcome("Read code 1", true)],
            [new CodeOutcome("4006381333931", "Ean13", "12"), new CodeOutcome("9780201379624", "Ean13")]);

    [Fact]
    public void A_live_frame_maps_its_tools_print_quality_and_code_add_ons()
    {
        var f = new V1.LiveFrame { InspectionId = 3, Pass = false, Width = 640, Height = 480 };
        f.Tools.Add(new V1.ToolResult { Name = "Check text 1", Pass = false, PrintQuality = 0 });
        f.Tools.Add(new V1.ToolResult { Name = "Read code 1", Pass = true });
        f.Codes.Add(new V1.CodeRead { Text = "4006381333931", Format = "Ean13", AddOn = "12" });
        f.Codes.Add(new V1.CodeRead { Text = "ABC", Format = "QrCode" });

        var m = GrpcRuntimeClient.Map(f);
        Assert.Equal([new ToolOutcome("Check text 1", false, 0f), new ToolOutcome("Read code 1", true)], m.Tools!);
        Assert.Equal([new CodeOutcome("4006381333931", "Ean13", "12"), new CodeOutcome("ABC", "QrCode", "")], m.Codes!);
    }

    [Fact]
    public void A_frame_from_an_older_runtime_has_no_tools_or_codes()
    {
        var m = GrpcRuntimeClient.Map(new V1.LiveFrame { InspectionId = 3, Pass = true });
        Assert.Empty(m.Tools!);
        Assert.Empty(m.Codes!);
        Assert.Empty(NgViewerViewModel.Measured(m, Make.Strings));
    }

    [Fact]
    public void A_code_tooltip_carries_its_add_on_and_other_tooltips_are_unchanged()
    {
        var part = Part();
        Assert.Equal("4006381333931 · add-on 12", GraphicHit.Text(part.Graphics[0], part.Codes, Make.Strings));
        Assert.Equal("LOT A123", GraphicHit.Text(part.Graphics[1], part.Codes, Make.Strings));
        Assert.Equal("9780201379624", GraphicHit.Text(part.Graphics[2], part.Codes, Make.Strings));
        Assert.Equal("4006381333931", GraphicHit.Text(part.Graphics[0], null, Make.Strings));
    }

    [Fact]
    public void The_NG_viewer_lists_print_quality_and_the_add_on_after_the_checks()
    {
        var part = Part();
        var thumb = new NgThumb(null, "08:00:05", Tone.Ng, Icons.Cross, part);
        var viewer = new NgViewerViewModel(Make.Strings, new RuntimeCalls(new ManualLink()), [thumb], 0, _ => null, new Overlays());

        Assert.Equal(["LOT A123", "Passed: 2", "Check text 1: print quality 41", "Code 4006381333931: add-on 12"],
            viewer.Checks.Select(c => c.Text));
        Assert.Equal(Tone.Ng, viewer.Checks[2].Tone);   // the tool failed
        Assert.Equal(Tone.Neutral, viewer.Checks[3].Tone);

        viewer.TapImage(new Point2(50, 50), 4);
        Assert.Equal("4006381333931 · add-on 12", viewer.TooltipText);
    }

    [Fact]
    public void A_passed_check_text_shows_its_print_quality_as_OK()
    {
        var lines = NgViewerViewModel.Measured(Part() with { Tools = [new ToolOutcome("Check text 1", true, 88f)] }, Make.Strings).ToList();
        Assert.Equal(Tone.Ok, lines[0].Tone);
        Assert.Equal("Check text 1: print quality 88", lines[0].Text);
    }

    [Fact]
    public void A_tap_on_a_code_on_the_Run_screen_shows_its_add_on()
    {
        var link = new ManualLink();
        var vm = new RunViewModel(Make.Strings, new RuntimeCalls(link), new Overlays(), new ManualTime(), _ => null);
        link.FrameReceived += vm.OnFrame;
        link.Connect(new ScriptedClient { Status = Make.Running() }, Make.Running());
        link.Push(Part());

        vm.TapImage(new Point2(50, 50), 4);
        Assert.Equal("4006381333931 · add-on 12", vm.Tiles[0].TooltipText);
        vm.TapImage(new Point2(250, 50), 4);
        Assert.Equal("9780201379624", vm.Tiles[0].TooltipText);
    }
}
