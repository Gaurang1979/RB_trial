using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VisionSystem.Studio.Controls;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>Zoom, pan and pixel readout on the workspace picture; property search and Essential / All (UI-UX-SPEC.md 4.19).</summary>
public class WorkspaceToolsTests
{
    private static ZoomBox Box()
    {
        var box = new ZoomBox { Width = 400, Height = 300, Child = new Panel() };
        var window = new Window { Width = 400, Height = 300, Content = box };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return box;
    }

    [AvaloniaFact]
    public void Zoom_keeps_the_point_under_the_pointer_and_never_leaves_the_picture()
    {
        var box = Box();
        box.ZoomBy(2, new Point(100, 100));
        Assert.Equal(2, box.Zoom);
        Assert.Equal(new Vector(-100, -100), box.Offset);   // (100,100) of the picture stays under the pointer
        box.ZoomBy(2, new Point(400, 300));
        Assert.Equal(4, box.Zoom);
        Assert.Equal(new Vector(-600, -500), box.Offset);   // the screen point (400, 300) keeps the same picture point
        box.ZoomBy(1, new Point(0, 0));
        Assert.True(box.Offset.X >= 400 - 400 * 4 && box.Offset.Y >= 300 - 300 * 4);   // never past the picture's edge
        box.ZoomBy(1 / 2.0, new Point(200, 150));   // zooming out keeps the point under the pointer too
        Assert.Equal(2, box.Zoom);
        Assert.Equal(new Vector(-200, -175), box.Offset);   // from (-600, -500): 200 - (200 + 600) / 2, 150 - (150 + 500) / 2
        box.Zoom = 100;
        Assert.Equal(ZoomBox.MaxZoom, box.Zoom);
        box.ZoomBy(0.01);
        Assert.Equal(ZoomBox.MinZoom, box.Zoom);
        Assert.Equal(default, box.Offset);                  // at fit the picture is never moved
        box.ZoomBy(3);
        box.Fit();
        Assert.Equal((1.0, default(Vector)), (box.Zoom, box.Offset));
    }

    [Fact]
    public async Task The_pixel_readout_is_in_full_image_pixels_with_the_grey_value()
    {
        var client = new ScriptedClient();
        var link = new ManualLink();
        var draft = JobDraft.New("Tablets", 1);
        draft.AddTool(ToolKind.CheckText, 0);
        client.JobFiles["Tablets"] = draft.ToJson();
        client.Products = [new("Tablets", "")];
        link.Connect(client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        vm.PointAt(10, 10, 99);
        Assert.Equal("", vm.PointerText);   // nothing tried: no picture to read
        await vm.TryCommand.ExecuteAsync(null);   // the JPEG is half size (scale 0.5)
        vm.PointAt(100, 50, 128);
        Assert.Equal("x 200  y 100  grey 128", vm.PointerText);
        vm.PointAt(100, 50, null);
        Assert.Equal("x 200  y 100", vm.PointerText);
        vm.PointAt(null, null, null);
        Assert.Equal("", vm.PointerText);
        vm.ShowZoom(2.5);
        Assert.Equal("250 %", vm.ZoomText);
    }

    [Fact]
    public void A_property_shows_by_its_words_and_advanced_ones_only_with_All()
    {
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(new ManualLink()), _ => null, () => { });
        Assert.True(vm.PropertyShown("Letter height in pixels", essential: false));
        vm.PropertySearch = "  HEIGHT  pixels ";
        Assert.True(vm.PropertyShown("Letter height in pixels", essential: false));
        Assert.False(vm.PropertyShown("Lowest quality", essential: true));
        vm.PropertySearch = "";
        vm.ShowEssentialCommand.Execute(null);
        Assert.False(vm.PropertyShown("Letter height in pixels", essential: false));
        Assert.True(vm.PropertyShown("Lowest quality", essential: true));
        vm.ShowAllCommand.Execute(null);
        Assert.True(vm.ShowAllProperties);
    }
}
