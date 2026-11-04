using System.Security.Cryptography;
using System.Text;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>AES-GCM with a key of its own, like DPAPI bound to "this user": another instance cannot unwrap.</summary>
internal sealed class TestDraftProtector : IDraftProtector
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public byte[] Protect(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var sealedText = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, sealedText, tag);
        return [.. nonce, .. tag, .. sealedText];
    }

    public byte[] Unprotect(byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException("short");
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
        return plain;
    }
}

/// <summary>The kept history file (UI-UX-SPEC.md 5 "survives a UI restart", AS-18): protected, matched, dropped.</summary>
public sealed class DraftHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs-drafts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static KeptDraft Draft() => new(DraftHistoryStore.Hash("{\"Name\":\"Tablets\"}"), 0,
        ["{\"Name\":\"Tablets\",\"Tool\":\"Read code 1\"}"], "{\"Name\":\"Tablets\",\"Tool\":\"Secret tool\"}", [],
        [new KeptArea(0, "Read code 1", 1, 2, 3, 4)]);

    [Fact]
    public void A_kept_draft_comes_back_and_its_file_shows_no_job_or_tool_name()
    {
        var store = new DraftHistoryStore(_dir, new TestDraftProtector());
        store.Save("Tablets", Draft());
        var file = store.FileOf("Tablets")!;
        Assert.DoesNotContain("Tablets", Path.GetFileName(file));
        var bytes = File.ReadAllBytes(file);
        Assert.DoesNotContain("Tablets", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("Secret", Encoding.UTF8.GetString(bytes));

        var kept = store.Load("Tablets")!;
        Assert.Equal(Draft().Current, kept.Current);
        Assert.Equal(Draft().Undo, kept.Undo);
        Assert.Equal(new KeptArea(0, "Read code 1", 1, 2, 3, 4), Assert.Single(kept.NotSnapped));
        Assert.Null(store.Load("Syrup"));
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_dropped()
    {
        new DraftHistoryStore(_dir, new TestDraftProtector()).Save("Tablets", Draft());
        var otherUser = new DraftHistoryStore(_dir, new TestDraftProtector());
        Assert.Null(otherUser.Load("Tablets"));
        Assert.False(File.Exists(otherUser.FileOf("Tablets")));
    }

    [Fact]
    public void With_no_protection_nothing_is_written()
    {
        var store = new DraftHistoryStore(_dir, null);
        store.Save("Tablets", Draft());
        Assert.False(Directory.Exists(_dir));
        Assert.Null(store.Load("Tablets"));
    }

    [Fact]
    public void The_history_exported_is_restored_in_order()
    {
        var h = new DraftHistory();
        h.Reset("{\"N\":0}");
        h.Record("{\"N\":1}");
        h.Record("{\"N\":2}");
        h.Record("{\"N\":3}");
        h.Undo();
        var (undo, current, redo) = h.Export();

        var again = new DraftHistory();
        again.Restore(undo, current!, redo);
        Assert.Equal(2, again.Steps);
        Assert.Equal("{\"N\":3}", again.Redo());
        Assert.Equal("{\"N\":2}", again.Undo());
        Assert.Equal("{\"N\":1}", again.Undo());
        Assert.Equal("{\"N\":0}", again.Undo());
        Assert.Null(again.Undo());
    }
}

/// <summary>The designer's draft and its steps after a restart of Vision Studio (AS-18).</summary>
public sealed class DraftHistoryKeptViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs-drafts-" + Guid.NewGuid().ToString("N"));
    private readonly TestDraftProtector _protector = new();
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    public DraftHistoryKeptViewModelTests()
    {
        _client.JobFiles["Tablets"] = JobDraft.New("Tablets", 2).ToJson();
        _client.JobFiles["Syrup"] = JobDraft.New("Syrup", 1).ToJson();
        _client.Products = [new("Tablets", ""), new("Syrup", "")];
        _link.Connect(_client, Make.Stopped());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private DraftHistoryStore Store => new(_dir, _protector);
    private bool Kept(string job) => File.Exists(Store.FileOf(job));

    /// <summary>Vision Studio started (again): the designer opens the job.</summary>
    private async Task<DesignerViewModel> Start(string job = "Tablets")
    {
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { }) { HistoryStore = Store };
        await vm.LoadAsync();
        await vm.OpenAsync(job);
        return vm;
    }

    /// <summary>Two tools added, the second taken back: steps to undo, one to redo, kept.</summary>
    private async Task<DesignerViewModel> Edited()
    {
        var vm = await Start();
        await vm.TryCommand.ExecuteAsync(null);   // a picture on screen to draw on
        vm.AddReadCodeCommand.Execute(null);
        vm.AreaDrawn(new Point2(20, 10), new Point2(220, 110));
        vm.AddReadTextCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.KeepHistory(now: true);
        return vm;
    }

    [Fact]
    public async Task The_draft_and_its_steps_come_back_after_a_restart()
    {
        await Edited();
        Assert.True(Kept("Tablets"));

        var vm = await Start();
        Assert.True(vm.Dirty);   // still unsaved
        Assert.Equal("Read code 1", Assert.Single(vm.CameraTools).Name);
        Assert.Equal((40, 20, 400, 200), vm.CameraTools[0].Roi);
        Assert.True(vm.CanUndo);
        Assert.True(vm.CanRedo);

        vm.RedoCommand.Execute(null);
        Assert.Equal(2, vm.CameraTools.Count);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.Null(vm.CameraTools[0].Roi);
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.CameraTools);
        Assert.False(vm.Dirty);   // back to the job as saved
        Assert.False(vm.CanUndo);
        Assert.Empty(_client.Saved);   // only the draft changed
    }

    [Fact]
    public async Task A_save_drops_what_was_kept_and_the_next_step_keeps_it_again()
    {
        var vm = await Edited();
        vm.SelectedTool!.Format = "QR";
        vm.SelectedTool.ExpectedCount = 1;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(_client.Saved);
        Assert.False(Kept("Tablets"));
        var stored = JobDraft.Parse(_client.JobFiles["Tablets"]);
        stored.Root["Version"] = 4;   // the runtime writes the file its own way, with the version it gave
        _client.JobFiles["Tablets"] = stored.ToJson();

        var restarted = await Start();
        Assert.False(restarted.Dirty);
        Assert.False(restarted.CanUndo);
        Assert.Single(restarted.CameraTools);

        vm.AddReadTextCommand.Execute(null);   // the first session goes on after its save
        vm.KeepHistory(now: true);
        var again = await Start();
        Assert.Equal(2, again.CameraTools.Count);
        again.UndoCommand.Execute(null);
        Assert.False(again.Dirty);   // as saved
        Assert.Equal(4, again.Job!.Version);
    }

    [Fact]
    public async Task A_job_changed_outside_Vision_Studio_opens_as_saved()
    {
        await Edited();
        var outside = JobDraft.Parse(_client.JobFiles["Tablets"]);
        outside.Root["Version"] = 9;   // a new version saved on another PC
        _client.JobFiles["Tablets"] = outside.ToJson();

        var vm = await Start();
        Assert.Empty(vm.CameraTools);
        Assert.False(vm.Dirty);
        Assert.False(vm.CanUndo);
        Assert.False(vm.CanRedo);
        Assert.False(Kept("Tablets"));
    }

    [Fact]
    public async Task Reload_another_job_or_close_drops_what_was_kept()
    {
        var vm = await Edited();
        vm.ReloadCommand.Execute(null);
        await vm.AnswerYesCommand.ExecuteAsync(null);
        Assert.False(Kept("Tablets"));
        Assert.Empty(vm.CameraTools);

        vm = await Edited();
        await vm.OpenAsync("Syrup");
        Assert.False(Kept("Tablets"));

        vm = await Edited();
        vm.CloseCommand.Execute(null);
        await vm.AnswerYesCommand.ExecuteAsync(null);
        Assert.False(Kept("Tablets"));
    }

    [Fact]
    public async Task Another_jobs_kept_draft_waits_until_that_job_is_opened()
    {
        await Edited();
        var vm = await Start("Syrup");   // the line has another job open at the restart
        Assert.True(Kept("Tablets"));
        await vm.OpenAsync("Tablets");
        Assert.Single(vm.CameraTools);
        Assert.True(vm.CanUndo);
    }

    [Fact]
    public async Task A_dropped_tool_that_found_nothing_still_stops_Save_after_a_restart()
    {
        var vm = await Start();
        await vm.TryCommand.ExecuteAsync(null);
        _client.TryWith = r => r.TeachTool is null ? null
            : new TryOutcome(false, Refused: new CommandResult(false, "runtime.refused.teach", "No code was read here."));
        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(320, 240));
        Assert.Equal("!", vm.Tree.Single(n => n.IsTool).Mark);
        vm.KeepHistory(now: true);

        var restarted = await Start();
        Assert.Equal("!", restarted.Tree.Single(n => n.IsTool).Mark);
        await restarted.SaveCommand.ExecuteAsync(null);
        Assert.Empty(_client.Saved);
        Assert.Equal(Make.Strings.Format("design.drop.save_first", "Read code 1"), restarted.Message);
    }

    [Fact]
    public async Task A_new_job_never_saved_is_not_kept()
    {
        var vm = await Start();
        vm.NewJobName = "Fresh";
        await vm.NewJobCommand.ExecuteAsync(null);
        vm.AddReadCodeCommand.Execute(null);
        vm.KeepHistory(now: true);
        Assert.False(Kept("Fresh"));
    }

    private static readonly ToolSuggestion Qr = new("code", "QR Code", ["A1"], ["code"],
        [new(900, 100), new(1100, 100), new(1100, 300), new(900, 300)],
        """{"kind":"code","Name":"QR Code","Camera":0,"Enabled":true,"Format":"QrCode","ExpectedCount":1}""");

    private async Task<DesignerViewModel> Accepted()
    {
        var vm = await Start();
        await vm.TryCommand.ExecuteAsync(null);
        _client.FindAnswer = new FindOutcome(true, [Qr], 10, [1, 2, 3], 640, 480, 0.5, 1280, 960);
        await vm.FindEverythingCommand.ExecuteAsync(null);
        await vm.AcceptSuggestionCommand.ExecuteAsync(vm.Suggestions[0]);
        return vm;
    }

    [Fact]
    public async Task Undo_of_Accept_after_a_restart_offers_the_suggestion_again_with_no_Find()
    {
        var vm = await Accepted();
        vm.KeepHistory(now: true);
        var file = File.ReadAllBytes(Store.FileOf("Tablets")!);
        Assert.DoesNotContain("QR Code", Encoding.UTF8.GetString(file));   // the suggestion stays protected too
        _client.Finds.Clear();

        var restarted = await Start();
        Assert.Single(restarted.CameraTools);
        Assert.Empty(restarted.Suggestions);
        restarted.UndoCommand.Execute(null);
        Assert.Empty(restarted.CameraTools);
        Assert.Equal([Qr.Summary], restarted.Suggestions.Select(s => s.Summary));   // offered again, as before the restart
        Assert.Equal(Qr.ToolJson, restarted.Suggestions[0].Suggestion.ToolJson);
        Assert.Equal(Qr.Outline, restarted.Suggestions[0].Suggestion.Outline);
        restarted.RedoCommand.Execute(null);
        Assert.Single(restarted.CameraTools);
        Assert.Empty(restarted.Suggestions);
        Assert.Empty(_client.Finds);   // no Find again
    }

    [Fact]
    public async Task On_another_camera_and_picture_the_restart_shows_them_and_the_undo_offers_it_again()
    {
        var vm = await Start();
        vm.CameraIndex = 1;
        await vm.NextPictureCommand.ExecuteAsync(null);
        Assert.Equal("good2.bmp", vm.Pictures[vm.PictureIndex]);
        _client.FindAnswer = new FindOutcome(true, [Qr], 10, [1, 2, 3], 640, 480, 0.5, 1280, 960);
        await vm.FindEverythingCommand.ExecuteAsync(null);
        await vm.AcceptSuggestionCommand.ExecuteAsync(vm.Suggestions[0]);
        vm.KeepHistory(now: true);

        var restarted = await Start();
        Assert.Equal(1, restarted.CameraIndex);
        Assert.Equal("good2.bmp", restarted.Pictures[restarted.PictureIndex]);
        restarted.UndoCommand.Execute(null);
        Assert.Equal([Qr.Summary], restarted.Suggestions.Select(s => s.Summary));
    }

    [Fact]
    public async Task With_their_picture_gone_the_accepted_suggestions_are_forgotten()
    {
        var vm = await Accepted();
        vm.KeepHistory(now: true);
        _client.PictureNames = ["good2.bmp", "bad1.bmp"];

        var restarted = await Start();
        Assert.Single(restarted.CameraTools);   // the draft still comes back
        restarted.UndoCommand.Execute(null);
        Assert.Empty(restarted.Suggestions);   // as a change of picture forgets them
    }

    [Fact]
    public async Task An_Accept_undone_before_a_restart_is_still_offered_and_its_redo_takes_it_off()
    {
        var vm = await Accepted();
        vm.UndoCommand.Execute(null);
        vm.KeepHistory(now: true);

        Assert.Equal([Qr.Summary], vm.Suggestions.Select(s => s.Summary));
        var restarted = await Start();
        Assert.True(restarted.CanRedo);
        Assert.Empty(restarted.CameraTools);
        Assert.Equal([Qr.Summary], restarted.Suggestions.Select(s => s.Summary));
        restarted.RedoCommand.Execute(null);
        Assert.Single(restarted.CameraTools);
        Assert.Empty(restarted.Suggestions);
    }

    [Fact]
    public async Task Accepted_suggestions_dismissed_are_not_brought_back()
    {
        var vm = await Accepted();
        vm.DismissAllSuggestionsCommand.Execute(null);   // forgotten: an undo no longer offers it
        vm.KeepHistory(now: true);
        var restarted = await Start();
        restarted.UndoCommand.Execute(null);
        Assert.Empty(restarted.Suggestions);
    }

    [Fact]
    public async Task A_save_drops_the_accepted_suggestions_with_the_draft()
    {
        var vm = await Accepted();
        vm.KeepHistory(now: true);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(_client.Saved);
        Assert.False(Kept("Tablets"));   // dropped with the draft
        Assert.Empty((await Start()).Suggestions);
    }
}
