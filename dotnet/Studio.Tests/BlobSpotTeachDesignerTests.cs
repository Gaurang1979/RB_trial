using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Studio.Tests;

/// <summary>"Teach a spot" on a Blob tool in the job designer (requirements 17.12 BLB-OUT-07): the spot of the selected
/// tool under the tap is found from the try's graphics (only the tool's numbered outlines, not its marks or another
/// tool's spots) and sent as TryJob's teach spot; beside every spot nothing is sent.</summary>
public class BlobSpotTeachDesignerTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    private static ResultGraphic Square(double x, double y, double side, string label, string? tool, Verdict verdict = Verdict.Ok) =>
        new(GraphicKind.Polygon, [new(x, y), new(x + side, y), new(x + side, y + side), new(x, y + side)], verdict, label) { Tool = tool };

    private static ResultGraphic Cross(double x, double y, string label, string tool) =>
        new(GraphicKind.Cross, [new(x, y)], Verdict.Ng, label) { Tool = tool };

    /// <summary>Spots 1 and 2 of "Blob 1", a dent cross on spot 2, and spot 1 of "Blob 2" further right.</summary>
    private static readonly ResultGraphic[] Tried =
    [
        Square(10, 10, 50, "1", "Blob 1"),
        Square(100, 100, 50, "2", "Blob 1", Verdict.Ng),
        Cross(125, 100, "dent 0.8 px", "Blob 1"),
        Square(200, 100, 50, "1", "Blob 2"),
    ];

    private async Task<DesignerViewModel> Open()
    {
        var job = JobDraft.New("Lids", 1);
        _client.JobFiles["Lids"] = job.ToJson();
        _client.Products = [new("Lids", "")];
        _client.TryDefault = _client.TryDefault with { Graphics = Tried };
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Lids");
        vm.AddBlobCommand.Execute(null);
        await vm.TryCommand.ExecuteAsync(null);
        _client.Tries.Clear();
        return vm;
    }

    [Fact]
    public void The_spot_under_the_point_is_the_tools_numbered_outline()
    {
        Assert.Equal(1, DesignerViewModel.SpotAt(Tried, "Blob 1", new Point2(30, 30)));
        Assert.Equal(2, DesignerViewModel.SpotAt(Tried, "Blob 1", new Point2(140, 140)));
        Assert.Equal(2, DesignerViewModel.SpotAt(Tried, "Blob 1", new Point2(125, 101)));   // on the dent cross: still spot 2
        Assert.Null(DesignerViewModel.SpotAt(Tried, "Blob 1", new Point2(80, 80)));          // between the spots
        Assert.Null(DesignerViewModel.SpotAt(Tried, "Blob 1", new Point2(220, 120)));        // another tool's spot
        Assert.Equal(1, DesignerViewModel.SpotAt(Tried, "Blob 2", new Point2(220, 120)));
        // an older runtime names no tool: no spot is guessed
        Assert.Null(DesignerViewModel.SpotAt(Tried.Select(g => g with { Tool = null }).ToList(), "Blob 1", new Point2(30, 30)));
    }

    [Fact]
    public async Task A_tap_on_a_spot_teaches_that_spot_and_a_tap_beside_teaches_nothing()
    {
        var vm = await Open();
        Assert.Equal("Blob 1", vm.SelectedTool!.Name);
        vm.PickingSpot = true;
        Assert.Equal(Make.Strings.Format("design.blob.pick_spot", "Blob 1"), vm.Message);

        vm.PictureTapped(new Point2(80, 80), new Point2(80, 80));
        Assert.Empty(_client.Tries);
        Assert.True(vm.PickingSpot);                                    // still waiting for a spot
        Assert.Equal(Make.Strings["design.blob.no_spot_here"], vm.Message);

        vm.PictureTapped(new Point2(140, 140), new Point2(141, 140));
        for (var i = 0; i < 200 && _client.Tries.Count == 0; i++) await Task.Delay(10);
        var teach = Assert.Single(_client.Tries);
        Assert.Equal(("Blob 1", 2), (teach.TeachTool, teach.TeachSpot));
        Assert.False(vm.PickingSpot);

        // Teach (F7) still teaches the first spot, with no spot sent
        _client.Tries.Clear();
        await vm.TeachCommand.ExecuteAsync(null);
        Assert.Equal(0, Assert.Single(_client.Tries).TeachSpot);
    }

    [Fact]
    public async Task After_a_change_to_the_job_the_spots_are_tried_again_before_one_is_taught()
    {
        var vm = await Open();
        vm.SelectedTool!.MinArea = 40;   // the spots may be numbered otherwise now
        vm.PickingSpot = true;
        vm.PictureTapped(new Point2(140, 140), new Point2(140, 140));
        Assert.Empty(_client.Tries);
        Assert.True(vm.PickingSpot);
        Assert.Equal(Make.Strings["design.blob.try_first"], vm.Message);

        await vm.TryCommand.ExecuteAsync(null);
        _client.Tries.Clear();
        vm.PictureTapped(new Point2(140, 140), new Point2(140, 140));
        for (var i = 0; i < 200 && _client.Tries.Count == 0; i++) await Task.Delay(10);
        Assert.Equal(2, Assert.Single(_client.Tries).TeachSpot);
    }

    [Fact]
    public async Task Teach_a_spot_is_only_for_a_blob_tool()
    {
        var vm = await Open();
        vm.SelectedTool = null;
        vm.PickingSpot = true;
        Assert.False(vm.PickingSpot);
        vm.PictureTapped(new Point2(140, 140), new Point2(140, 140));
        Assert.Empty(_client.Tries);
    }

    [Fact]
    public void A_try_graphic_keeps_the_tool_the_runtime_names()
    {
        var g = new V1.Graphic { Kind = V1.Graphic.Types.Kind.Cross, Colour = "status.ng", Label = "dent 0.8 px", Tool = "Lid" };
        g.Points.Add(new V1.Point { X = 3, Y = 4 });
        var mapped = GrpcRuntimeClient.Map(g)!;
        Assert.Equal((GraphicKind.Cross, Verdict.Ng, "Lid"), (mapped.Kind, mapped.Verdict, mapped.Tool));
        g.Tool = "";
        Assert.Null(GrpcRuntimeClient.Map(g)!.Tool);
    }
}
