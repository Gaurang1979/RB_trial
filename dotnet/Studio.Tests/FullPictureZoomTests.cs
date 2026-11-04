using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using VisionSystem.Studio.Controls;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>The full-size picture with zoom and pan in job setup (requirements AS-17, UI-UX-SPEC.md 4.19
/// "Centre, zoom and pan"): zoom limits, 100 %, pan limits, overview, and the full-resolution picture
/// swapped in above fit with the graphics and area drawing still on the same image pixels.</summary>
public class FullPictureZoomTests
{
    private static ZoomBox Box()
    {
        var box = new ZoomBox { Width = 400, Height = 300, Child = new Panel() };
        var window = new Window { Width = 400, Height = 300, Content = box };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return box;
    }

    [Fact]
    public void One_hundred_percent_is_one_camera_pixel_per_screen_pixel_within_the_zoom_limits()
    {
        // a 2448 x 2048 camera in an 800 x 600 box: fitted at 600 / 2048 box pixels per camera pixel
        Assert.Equal(2048 / 600.0, ZoomBox.OneToOneZoom(new Size(800, 600), 2448, 2048, 1), 9);
        // at 150 % display scaling a box pixel is 1.5 screen pixels
        Assert.Equal(2048 / 600.0 / 1.5, ZoomBox.OneToOneZoom(new Size(800, 600), 2448, 2048, 1.5), 9);
        // a picture smaller than the box stays at fit; a huge one stops at 16 times
        Assert.Equal(ZoomBox.MinZoom, ZoomBox.OneToOneZoom(new Size(800, 600), 640, 480, 1));
        Assert.Equal(ZoomBox.MaxZoom, ZoomBox.OneToOneZoom(new Size(200, 150), 20000, 15000, 1));
        // nothing known: fit
        Assert.Equal(ZoomBox.MinZoom, ZoomBox.OneToOneZoom(new Size(0, 0), 2448, 2048, 1));
        Assert.Equal(ZoomBox.MinZoom, ZoomBox.OneToOneZoom(new Size(800, 600), 0, 0, 1));
    }

    [AvaloniaFact]
    public void Zoom_runs_from_fit_to_sixteen_times_and_100_percent_zooms_about_the_centre()
    {
        var box = Box();
        box.ZoomTo(100);
        Assert.Equal(ZoomBox.MaxZoom, box.Zoom);
        box.ZoomTo(0.01);
        Assert.Equal(ZoomBox.MinZoom, box.Zoom);
        box.ZoomTo(double.NaN);
        Assert.Equal(ZoomBox.MinZoom, box.Zoom);
        box.OneToOne(1600, 1200);   // fitted at 0.25: 100 % is 4 times, about the middle
        Assert.Equal(4, box.Zoom, 9);
        Assert.Equal(new Vector(200 - 200 * 4, 150 - 150 * 4), box.Offset);
        Assert.Equal(new Rect(0.375, 0.375, 0.25, 0.25), box.View);
    }

    [AvaloniaFact]
    public void Panning_never_leaves_the_picture_and_the_arrow_keys_move_it()
    {
        var box = Box();
        box.PanBy(new Vector(-50, -50));
        Assert.Equal(default, box.Offset);   // at fit the picture never moves
        box.ZoomTo(2, new Point(0, 0));
        box.PanTo(new Vector(100, 100));
        Assert.Equal(default, box.Offset);   // no gap at the top-left
        box.PanTo(new Vector(-10000, -10000));
        Assert.Equal(new Vector(400 - 800, 300 - 600), box.Offset);   // nor at the bottom-right
        Assert.Equal(new Vector(-400, -300), ZoomBox.Clamp(new Vector(-10000, -10000), new Size(400, 300), 2));
        Assert.Equal(default, ZoomBox.Clamp(new Vector(-10, -10), new Size(400, 300), 1));

        box.PanTo(new Vector(-200, -150));
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
        Assert.Equal(new Vector(-200 + 400 * ZoomBox.KeyPan, -150), box.Offset);   // more of the left side
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        Assert.Equal(new Vector(-160, -150 - 300 * ZoomBox.KeyPan), box.Offset);   // more of the bottom

        box.CentreOn(new Point(0.5, 0.5));   // the overview's box dragged to the middle
        Assert.Equal(new Vector(-200, -150), box.Offset);
        Assert.Equal(new Rect(0.25, 0.25, 0.5, 0.5), box.View);
        box.CentreOn(new Point(1, 1));       // past the corner: stops at the edge
        Assert.Equal(new Rect(0.5, 0.5, 0.5, 0.5), box.View);
    }

    [AvaloniaFact]
    public void The_pan_tool_drags_with_the_left_button_instead_of_drawing_an_area()
    {
        var area = new AreaDrag { ImageWidth = 400, ImageHeight = 300 };
        var drawn = 0;
        area.AreaDrawn += (_, _) => drawn++;
        var box = new ZoomBox { Width = 400, Height = 300, Child = new Panel { Children = { area } } };
        var window = new Window { Width = 400, Height = 300, Content = box };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        box.ZoomTo(2, new Point(0, 0));
        box.PanTool = true;
        window.MouseDown(new Point(200, 150), MouseButton.Left);
        window.MouseMove(new Point(150, 100), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(150, 100), MouseButton.Left);
        Assert.Equal(new Vector(-50, -50), box.Offset);
        Assert.Equal(0, drawn);
    }

    [AvaloniaFact]
    public void Two_fingers_zoom_about_the_point_between_them()
    {
        var box = Box();
        box.RaiseEvent(new PinchEventArgs(1.5, new Point(100, 100)));   // fingers 1.5 times as far apart as at the start
        Assert.Equal(1.5, box.Zoom, 9);
        Assert.Equal(new Vector(-50, -50), box.Offset);                  // (100, 100) stays between the fingers
        box.RaiseEvent(new PinchEventArgs(3, new Point(100, 100)));     // the scale counts from the start of the pinch
        Assert.Equal(3, box.Zoom, 9);
        box.RaiseEvent(new PinchEndedEventArgs());
        box.RaiseEvent(new PinchEventArgs(0.5, new Point(200, 150)));   // a new pinch: half of 3
        Assert.Equal(1.5, box.Zoom, 9);
        box.Fit();
        // a touch pad's two-finger spread
        var mouse = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        box.RaiseEvent(new PointerDeltaEventArgs(Gestures.PointerTouchPadGestureMagnifyEvent, box, mouse, box, new Point(0, 0), 0,
            new PointerPointProperties(), KeyModifiers.None, new Vector(1, 0)));
        Assert.Equal(2, box.Zoom, 9);
        Assert.Equal(default, box.Offset);   // about the top-left corner, where the pointer is
    }

    // --- the full-resolution picture --------------------------------------------------------------

    private sealed record Setup(DesignerViewModel Vm, ScriptedClient Client, List<byte[]> Decoded);

    /// <summary>A job with one tool; each try answers a 640 x 480 JPEG of a 1280 x 960 camera picture (scale 0.5)
    /// with a cross at JPEG pixel (100, 50). Bytes starting 0xFF cannot be decoded.</summary>
    private static async Task<Setup> Designer(string imageId, FullImage? full)
    {
        var client = new ScriptedClient();
        var link = new ManualLink();
        var draft = JobDraft.New("Tablets", 1);
        draft.AddTool(ToolKind.CheckText, 0);
        client.JobFiles["Tablets"] = draft.ToJson();
        client.Products = [new("Tablets", "")];
        client.TryDefault = client.TryDefault with
        {
            Graphics = [new ResultGraphic(GraphicKind.Cross, [new Point2(100, 50)], Verdict.Ok)],
            ImageId = imageId,
        };
        if (full is not null) client.Images[imageId] = full;
        link.Connect(client, Make.Stopped());
        var decoded = new List<byte[]>();
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(link), b => { lock (decoded) decoded.Add(b); return b is [0xFF, ..] ? null : new FakeImage(); }, () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        vm.SelectedTool = vm.CameraTools[0];
        await vm.TryCommand.ExecuteAsync(null);
        return new Setup(vm, client, decoded);
    }

    private static Point OnScreen(DesignerViewModel vm, Point2 p, Size box)
    {
        var (scale, x, y) = ResultOverlay.Fit(box, vm.ImageWidth, vm.ImageHeight);
        return new Point(x + p.X * scale, y + p.Y * scale);
    }

    [Fact]
    public async Task Above_fit_the_full_picture_is_swapped_in_and_graphics_and_areas_stay_on_the_same_pixels()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var (vm, client, decoded) = await Designer("try-1", new FullImage(png, 1280, 960));
        var box = new Size(500, 400);
        var cross = vm.Graphics.Single(g => g.Kind == GraphicKind.Cross).Points[0];
        var before = OnScreen(vm, cross, box);
        Assert.Equal((640.0, 480.0, 0.5), (vm.ImageWidth, vm.ImageHeight, vm.ShownScale));
        Assert.Equal((1280.0, 960.0), (vm.CameraWidth, vm.CameraHeight));
        var reduced = vm.Image;

        vm.ShowZoom(1);   // at fit the reduced picture is enough
        Assert.DoesNotContain("GetImage try-1", client.Calls);
        vm.ShowZoom(2);
        await vm.FullPictureFetch;

        Assert.Single(client.Calls, "GetImage try-1");
        Assert.True(vm.ShowsFullPicture);
        Assert.Same(png, decoded[^1]);
        Assert.NotSame(reduced, vm.Image);
        Assert.Same(reduced, vm.OverviewImage);   // the overview keeps the small one
        Assert.Equal((1280.0, 960.0, 1.0), (vm.ImageWidth, vm.ImageHeight, vm.ShownScale));
        Assert.Equal("", vm.FullPictureText);
        Assert.Equal("200 % · full picture", vm.ZoomText);
        // the cross is now in camera pixels, at the same place on the screen
        var after = vm.Graphics.Single(g => g.Kind == GraphicKind.Cross).Points[0];
        Assert.Equal(new Point2(200, 100), after);
        Assert.Equal(before, OnScreen(vm, after, box));

        // an area drawn on the full picture is in camera pixels; drawn on screen it lands on the same place
        vm.AreaDrawn(new Point2(200, 100), new Point2(400, 300));
        Assert.Equal((200, 100, 200, 200), vm.SelectedTool!.Roi!.Value);
        var area = vm.Graphics.Single(g => g.Label == $"1 · {Make.Strings["design.area"]}").Points;   // the selected tool's area is numbered (AS-15)
        Assert.Equal(new Point2[] { new(200, 100), new(400, 100), new(400, 300), new(200, 300) }, area);
        vm.PointAt(200, 100, 77);   // the readout is in camera pixels, read from the full picture
        Assert.Equal("x 200  y 100  grey 77", vm.PointerText);

        vm.ShowZoom(4);   // asked once per picture
        Assert.Single(client.Calls, "GetImage try-1");
    }

    [Fact]
    public async Task The_same_area_comes_out_of_the_reduced_and_the_full_picture()
    {
        var (vm, _, _) = await Designer("try-1", new FullImage([1], 1280, 960));
        vm.AreaDrawn(new Point2(100, 50), new Point2(200, 150));   // JPEG pixels
        var onReduced = vm.SelectedTool!.Roi;
        var screenReduced = vm.Graphics.Single(g => g.Label == $"1 · {Make.Strings["design.area"]}").Points.Select(p => OnScreen(vm, p, new Size(500, 400))).ToList();
        vm.ShowZoom(2);
        await vm.FullPictureFetch;
        Assert.True(vm.ShowsFullPicture);
        Assert.Equal(onReduced, vm.SelectedTool.Roi);
        var screenFull = vm.Graphics.Single(g => g.Label == $"1 · {Make.Strings["design.area"]}").Points.Select(p => OnScreen(vm, p, new Size(500, 400))).ToList();
        Assert.Equal(screenReduced, screenFull);   // the drawn area did not move on the screen
        vm.AreaDrawn(new Point2(200, 100), new Point2(400, 300));   // camera pixels, the same place
        Assert.Equal(onReduced, vm.SelectedTool.Roi);
    }

    [Fact]
    public async Task A_tool_dropped_on_the_full_picture_starts_on_the_same_camera_pixels()
    {
        var (vm, _, _) = await Designer("try-1", new FullImage([1], 1280, 960));
        var onReduced = vm.DropPreview(ToolKind.ReadCode, new Point2(320, 240))!.Value;   // JPEG pixels
        vm.ShowZoom(2);
        await vm.FullPictureFetch;
        Assert.True(vm.ShowsFullPicture);
        var onFull = vm.DropPreview(ToolKind.ReadCode, new Point2(640, 480))!.Value;     // camera pixels, the same place
        Assert.Equal(new Point2(onReduced.From.X * 2, onReduced.From.Y * 2), onFull.From);
        Assert.Equal(new Point2(onReduced.To.X * 2, onReduced.To.Y * 2), onFull.To);
    }

    [Fact]
    public async Task Without_an_image_id_the_reduced_picture_stays_and_says_so()
    {
        var (vm, client, _) = await Designer("", null);   // an older runtime
        var reduced = vm.Image;
        vm.ShowZoom(2);
        await vm.FullPictureFetch;
        Assert.False(vm.ShowsFullPicture);
        Assert.Same(reduced, vm.Image);
        Assert.Equal((640.0, 0.5), (vm.ImageWidth, vm.ShownScale));
        Assert.Equal(Make.Strings["ws.full.none"], vm.FullPictureText);
        Assert.DoesNotContain(client.Calls, c => c.StartsWith("GetImage"));
        Assert.Equal("200 %", vm.ZoomText);
        Assert.Equal(new Point2(100, 50), vm.Graphics.Single(g => g.Kind == GraphicKind.Cross).Points[0]);
    }

    [Fact]
    public async Task A_picture_no_longer_kept_or_of_another_size_leaves_the_reduced_one()
    {
        var (vm, client, _) = await Designer("try-gone", null);   // the runtime answers "no longer kept"
        vm.ShowZoom(2);
        await vm.FullPictureFetch;
        Assert.Contains("GetImage try-gone", client.Calls);
        Assert.False(vm.ShowsFullPicture);
        Assert.Equal(Make.Strings["ws.full.none"], vm.FullPictureText);
        Assert.Equal(640.0, vm.ImageWidth);

        (vm, _, _) = await Designer("try-2", new FullImage([1], 1024, 768));   // not the tried picture's size
        vm.ShowZoom(2);
        await vm.FullPictureFetch;
        Assert.False(vm.ShowsFullPicture);
        Assert.Equal((640.0, 0.5), (vm.ImageWidth, vm.ShownScale));

        (vm, _, _) = await Designer("try-3", new FullImage([0xFF], 1280, 960));   // cannot be read
        vm.ShowZoom(2);
        await vm.FullPictureFetch;
        Assert.False(vm.ShowsFullPicture);
        Assert.Equal(Make.Strings["ws.full.none"], vm.FullPictureText);

        var nothingTried = new DesignerViewModel(Make.Strings, new RuntimeCalls(new ManualLink()), _ => null, () => { });
        nothingTried.ShowZoom(2);
        await nothingTried.FullPictureFetch;
        Assert.Equal("", nothingTried.FullPictureText);   // no picture: nothing to fetch
    }

    [Fact]
    public async Task The_label_is_in_camera_pixels_and_a_failed_fetch_is_not_asked_again_at_once()
    {
        var (vm, client, _) = await Designer("try-gone", null);
        vm.ShowZoom(2.5);
        Assert.Equal("250 %", vm.ZoomText);           // the view does not know the camera's pixels: times the fit
        vm.ShowZoom(2.5, 0.5859);
        Assert.Equal("59 %", vm.ZoomText);            // 0.59 screen pixels per camera pixel
        await vm.FullPictureFetch;
        Assert.Equal(Make.Strings["ws.full.none"], vm.FullPictureText);
        client.Images["try-gone"] = new FullImage([1], 1280, 960);
        vm.ShowZoom(3, 0.7);                          // a wheel step right after: not asked again yet
        await vm.FullPictureFetch;
        Assert.Single(client.Calls, "GetImage try-gone");
        Assert.False(vm.ShowsFullPicture);
        vm.ShowZoom(1, 0.23);                         // back at fit: the note goes
        Assert.Equal("", vm.FullPictureText);
        Assert.Equal("23 %", vm.ZoomText);
    }

    [Fact]
    public async Task Try_all_asks_the_runtime_not_to_keep_its_pictures()
    {
        var (vm, client, _) = await Designer("try-1", new FullImage([1], 1280, 960));
        var shown = client.Tries.Count;
        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.False(client.Tries[shown - 1].NotShown);   // the picture on screen is kept for zooming in
        Assert.All(client.Tries.Skip(shown), t => Assert.True(t.NotShown));
        Assert.True(client.Tries.Count > shown);
    }

    [Fact]
    public async Task A_new_try_shows_the_reduced_picture_first_and_fetches_its_own_full_picture_when_zoomed_in()
    {
        var (vm, client, _) = await Designer("try-1", new FullImage([1], 1280, 960));
        vm.ShowZoom(2);
        await vm.FullPictureFetch;
        Assert.True(vm.ShowsFullPicture);

        client.TryDefault = client.TryDefault with { ImageId = "try-2" };
        client.Images["try-2"] = new FullImage([2], 1280, 960);
        await vm.TryCommand.ExecuteAsync(null);
        await vm.FullPictureFetch;   // still zoomed in: asked at once
        Assert.Contains("GetImage try-2", client.Calls);
        Assert.True(vm.ShowsFullPicture);
        Assert.Equal(new Point2(200, 100), vm.Graphics.Single(g => g.Kind == GraphicKind.Cross).Points[0]);
    }
}
