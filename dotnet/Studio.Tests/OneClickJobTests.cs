using System.Text.Json.Nodes;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>The one-click new job (owner, 2026-10-31): Grab a picture, auto find offers the tools (on the whole
/// picture or in a dragged window), the engineer accepts or leaves out each one, and the accepted tools are tried.
/// Nothing found is added to the job until it is accepted.</summary>
public class OneClickJobTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    private static readonly ToolSuggestion Qr = new("code", "QR Code x 2", ["A1", "A2"], ["code", "code"],
        [new(100, 100), new(300, 100), new(300, 300), new(100, 300)],
        """{"kind":"code","Name":"QR Code","Camera":0,"Enabled":true,"Format":"QrCode","ExpectedCount":2}""");

    private static readonly ToolSuggestion Batch = new("text", "Text, 2 lines: production date, lot / batch", ["MFG 10/2026", "B.NO AB123"],
        ["production date", "lot / batch"], [new(400, 500), new(800, 500), new(800, 600), new(400, 600)],
        """{"kind":"text","Name":"Text 1","Camera":0,"Enabled":true,"Mode":"Read","Lines":[{"Template":"MFG {MFG:MM/YYYY}"},{"Template":"B.NO {A:2}{D:3}"}]}""");

    private async Task<DesignerViewModel> Open(JobDraft draft)
    {
        _client.JobFiles["Tablets"] = draft.ToJson();
        _client.Products = [new("Tablets", "")];
        _client.FindAnswer = new FindOutcome(true, [Qr, Batch], 840, [1, 2, 3], 640, 480, 0.5, 1280, 960);
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        return vm;
    }

    [Fact]
    public async Task Grab_on_a_camera_with_no_tools_shows_the_picture_and_what_auto_find_offers()
    {
        var vm = await Open(JobDraft.New("Tablets", 1));
        await vm.GrabCommand.ExecuteAsync(null);

        Assert.Equal("grab-1.bmp", vm.Pictures[vm.PictureIndex]);
        var (stored, area) = Assert.Single(_client.Finds);
        Assert.Equal(("grab-1.bmp", null), (stored, area));
        Assert.Empty(_client.Tries);                 // no tool to try yet: the picture comes with the find
        Assert.NotNull(vm.Image);
        Assert.Equal(Tone.Neutral, vm.FrameTone);
        Assert.Equal(["QR Code x 2", "Text, 2 lines: production date, lot / batch"], vm.Suggestions.Select(s => s.Summary));
        Assert.Contains("MFG 10/2026  ·  production date", vm.Suggestions[1].ReadText);
        // each suggestion outlined and numbered on the picture, in JPEG pixels (full-image × 0.5)
        var first = Assert.Single(vm.Graphics, g => g.Label == "1");
        Assert.Equal((50, 150), (first.Points[0].X, first.Points[2].X));
        Assert.Empty(vm.Job!.Tools);                 // nothing added until it is accepted
    }

    [Fact]
    public async Task A_camera_with_no_pictures_folder_keeps_the_one_the_runtime_made()
    {
        var draft = JobDraft.New("Tablets", 1);
        var withFolder = JobDraft.Parse(draft.ToJson());
        withFolder.Cameras[0].TestFolder = "/data/pictures/Tablets/camera 1";
        _client.GrabAnswer = new GrabOutcome(true, "grab-1.bmp", withFolder.ToJson());
        var vm = await Open(draft);
        await vm.GrabCommand.ExecuteAsync(null);
        Assert.Equal("/data/pictures/Tablets/camera 1", vm.Cameras[0].TestFolder);
        Assert.True(vm.Dirty);
    }

    [Fact]
    public async Task Accepting_adds_the_tool_to_the_camera_shown_and_tries_it()
    {
        var draft = JobDraft.New("Tablets", 2);
        draft.AddToolJson(Qr.ToolJson, 1);           // a tool of that name already, on the other camera
        var vm = await Open(draft);
        await vm.FindEverythingCommand.ExecuteAsync(null);

        await vm.AcceptSuggestionCommand.ExecuteAsync(vm.Suggestions[0]);
        var added = vm.Job!.Tools.Single(t => t.Camera == 0);
        Assert.Equal("QR Code 2", added.Name);       // its own name: the other camera has "QR Code"
        Assert.Equal(2, added.ExpectedCount);
        Assert.Same(added, vm.SelectedTool);
        Assert.Contains("\"QR Code 2\"", _client.Tries[^1].Json);   // tried at once with the new tool
        Assert.Equal(["Text, 2 lines: production date, lot / batch"], vm.Suggestions.Select(s => s.Summary));

        vm.DismissSuggestionCommand.Execute(vm.Suggestions[0]);
        Assert.False(vm.HasSuggestions);
        Assert.Single(vm.Job.Tools, t => t.Camera == 0);
        Assert.DoesNotContain(vm.Graphics, g => g.Label is "1" or "2");
    }

    [Fact]
    public async Task Accept_all_adds_every_suggestion_and_tries_once()
    {
        var vm = await Open(JobDraft.New("Tablets", 1));
        await vm.FindEverythingCommand.ExecuteAsync(null);
        var tries = _client.Tries.Count;
        await vm.AcceptAllSuggestionsCommand.ExecuteAsync(null);
        Assert.Equal(["QR Code", "Text 1"], vm.CameraTools.Select(t => t.Name));
        Assert.Equal(tries + 1, _client.Tries.Count);
        var lines = JsonNode.Parse(vm.Job!.ToJson())!["Tools"]![1]!["Lines"]!.AsArray();
        Assert.Equal("MFG {MFG:MM/YYYY}", lines[0]!["Template"]!.ToString());   // the formats auto find proposed, to approve
    }

    [Fact]
    public async Task A_window_dragged_with_no_tool_selected_or_in_find_mode_asks_auto_find_in_it()
    {
        var vm = await Open(JobDraft.New("Tablets", 1));
        await vm.FindEverythingCommand.ExecuteAsync(null);   // shows the picture (scale 0.5)
        Assert.Null(vm.SelectedTool);
        vm.AreaDrawn(new Point2(20, 10), new Point2(220, 110));
        await WaitFor(() => _client.Finds.Count == 2);
        Assert.Equal((40, 20, 400, 200), _client.Finds[^1].Area);

        // with a tool selected a window is its area, unless "Find in a window" is on
        await vm.AcceptSuggestionCommand.ExecuteAsync(vm.Suggestions[0]);
        vm.AreaDrawn(new Point2(0, 0), new Point2(100, 100));
        Assert.Equal((0, 0, 200, 200), vm.SelectedTool!.Roi);
        Assert.Equal(2, _client.Finds.Count);
        vm.FindInWindow = true;
        vm.AreaDrawn(new Point2(10, 10), new Point2(60, 60));
        await WaitFor(() => _client.Finds.Count == 3);
        Assert.Equal((20, 20, 100, 100), _client.Finds[^1].Area);
        Assert.False(vm.FindInWindow);               // one window per press
        Assert.Equal((0, 0, 200, 200), vm.SelectedTool.Roi);
    }

    [Fact]
    public async Task A_refused_grab_says_why_and_another_picture_drops_the_suggestions()
    {
        var vm = await Open(JobDraft.New("Tablets", 1));
        _client.GrabAnswer = new GrabOutcome(false, Refused: new CommandResult(false, null, "Stop the line to take a picture for teaching."));
        await vm.GrabCommand.ExecuteAsync(null);
        Assert.Equal("Stop the line to take a picture for teaching.", vm.Message);
        Assert.Empty(_client.Finds);

        await vm.FindEverythingCommand.ExecuteAsync(null);
        Assert.True(vm.HasSuggestions);
        vm.PictureIndex = 1;
        Assert.False(vm.HasSuggestions);
    }

    private static async Task WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }
}
