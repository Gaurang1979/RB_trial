using System.Text.Json.Nodes;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>The pixel check per tool in the Engineer workspace (requirements AS-14, UI-UX-SPEC.md 4.19): sizes in the
/// labels, a yellow outline below the need, the advice, "too few pixels" in the tree until "Accept as is".</summary>
public class PixelCheckTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    private static readonly Point2[] Square = [new(100, 100), new(142, 100), new(142, 142), new(100, 142)];

    private async Task<DesignerViewModel> Open()
    {
        var draft = JobDraft.New("Tablets", 1);
        draft.AddTool(ToolKind.ReadCode, 0);
        _client.JobFiles["Tablets"] = draft.ToJson();
        _client.Products = [new("Tablets", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { }) { UserName = "Asha Patel" };
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        return vm;
    }

    private static PixelCheckTried Small(float lensMm = 25, float lensAdvised = 30, bool accepted = false) =>
        new("module", 2.5f, 1.8f, 2.1f, 1.2f, 1.5f, lensMm, lensAdvised, accepted, accepted ? "Ravi" : "", accepted ? DateTimeOffset.Now : null,
            [new PixelItemTried("QR", "module", 2.1f, true, Square), new PixelItemTried("QR", "module", 3.0f, false, Square)]);

    private void Answer(PixelCheckTried pixels) =>
        _client.TryDefault = _client.TryDefault with { Tools = [new ToolTried("Read code 1", "barcode", true, "Read: ABC", 9, Pixels: pixels)] };

    [Fact]
    public async Task Too_few_pixels_shows_in_the_tree_with_the_advice_a_yellow_outline_and_the_size_in_the_label()
    {
        var vm = await Open();
        Answer(Small());
        await vm.TryCommand.ExecuteAsync(null);

        var node = vm.Tree.Single(n => n.Name == "Read code 1");
        Assert.Equal("too few pixels", node.Pixels);
        Assert.Equal((Tone.Ok, "✓"), (node.Tone, node.Mark));   // advice only: the tool's own result is kept
        Assert.Equal("Read code 1: needs 1.2× more pixels: camera 1.2× nearer, a 30 mm lens instead of 25 mm, or a camera with 1.5× the pixels",
            vm.PixelAdviceText);
        Assert.Equal("Smallest detail 2.1 px/module, the tool needs 2.5 px/module", vm.PixelNeedText);
        Assert.True(vm.CanAcceptPixels);
        Assert.Contains(vm.Messages, m => m.Tone == Tone.Warn && m.Text == vm.PixelAdviceText);

        // the small code: a yellow outline with no tag of its own, and its size under it; the other only its size
        var outline = Assert.Single(vm.Graphics, g => g.Kind == GraphicKind.Polygon && g.Verdict == Verdict.Warn);
        Assert.False(outline.Tagged);
        Assert.Equal(50, outline.Points[0].X);   // full-image pixels × the JPEG's scale (0.5)
        var labels = vm.Graphics.Where(g => g.Kind == GraphicKind.Text).ToList();
        Assert.Equal(["QR 2.1 px/module", "QR 3.0 px/module"], labels.Select(g => g.Label));
        Assert.Equal([Verdict.Warn, Verdict.Info], labels.Select(g => g.Verdict));
    }

    [Fact]
    public async Task Without_the_lens_the_advice_gives_its_focal_length_as_a_factor_and_says_when_the_read_is_unsure()
    {
        var vm = await Open();
        Answer(new PixelCheckTried("bar", 2.0f, 1.5f, 1.2f, 1.7f, 2.9f, 0, 0, false, "", null,
            [new PixelItemTried("EAN-13", "bar", 1.2f, true, Square)]));
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Equal("Read code 1: needs 1.7× more pixels: camera 1.7× nearer, a lens with 1.7× the focal length, or a camera with 2.9× the pixels"
            + " At this size the read is unsure.", vm.PixelAdviceText);
        Assert.Equal("EAN-13 1.2 px/bar", vm.Graphics.Single(g => g.Kind == GraphicKind.Text).Label);
    }

    [Fact]
    public async Task Accept_as_is_records_the_engineer_time_and_size_in_the_draft_and_clears_the_tree_mark()
    {
        var vm = await Open();
        Answer(Small());
        await vm.TryCommand.ExecuteAsync(null);
        var before = DateTimeOffset.Now.AddSeconds(-1);

        vm.AcceptPixelsCommand.Execute(null);

        var node = vm.Tree.Single(n => n.Name == "Read code 1");
        Assert.Equal("", node.Pixels);
        Assert.False(vm.CanAcceptPixels);
        Assert.StartsWith("Accepted as is by Asha Patel", vm.PixelAcceptedText);
        Assert.True(vm.HasPixelAdvice);   // the advice stays: the pixels are still too few
        Assert.True(vm.Dirty);
        var tool = (JsonObject)JsonNode.Parse(vm.Job!.ToJson())!["Tools"]![0]!;
        var accepted = (JsonObject)tool["PixelsAccepted"]!;
        Assert.Equal("Asha Patel", accepted["By"]!.GetValue<string>());
        Assert.InRange(DateTimeOffset.Parse(accepted["At"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture), before, DateTimeOffset.Now);
        Assert.Equal(2.1f, accepted["FoundPx"]!.GetValue<float>());
    }

    [Fact]
    public async Task An_accepted_tool_or_one_with_enough_pixels_shows_no_mark()
    {
        var vm = await Open();
        Answer(Small(accepted: true));
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Tree.Single(n => n.Name == "Read code 1").Pixels);
        Assert.StartsWith("Accepted as is by Ravi", vm.PixelAcceptedText);
        Assert.DoesNotContain(vm.Messages, m => m.Tone == Tone.Warn);

        Answer(new PixelCheckTried("letter", 20, 12, 24, 1, 1, 0, 0, false, "", null, [new PixelItemTried("text", "letter", 24, false, Square)]));
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Tree.Single(n => n.Name == "Read code 1").Pixels);
        Assert.False(vm.HasPixelAdvice);
        Assert.Equal("text 24 px high", vm.Graphics.Single(g => g.Kind == GraphicKind.Text).Label);
        Assert.DoesNotContain(vm.Graphics, g => g.Verdict == Verdict.Warn);
    }

    [Fact]
    public async Task Saving_gives_the_advice_again_and_still_saves()
    {
        var vm = await Open();
        Answer(Small());
        await vm.TryCommand.ExecuteAsync(null);
        var before = vm.Messages.Count(m => m.Tone == Tone.Warn);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Single(_client.Saved);   // advice only: the job is saved
        Assert.Equal(before + 1, vm.Messages.Count(m => m.Tone == Tone.Warn && m.Text == vm.PixelAdviceText));
        Assert.Contains("Advice on the picture last tried (1)", vm.Message);
        Assert.Equal(Tone.Warn, vm.MessageTone);
    }
}
