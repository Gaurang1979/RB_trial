using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>Remote service in Vision Studio (REMOTE-SUPPORT.md 4b): the neutral badge, the operator's confirmation and a
/// supervisor's approval of a correction at the line PC.</summary>
public class RemoteServiceTests
{
    private readonly ManualLink _link = new();
    private readonly ScriptedClient _client = new();
    private readonly MainViewModel _vm;

    public RemoteServiceTests()
    {
        _vm = new MainViewModel(Make.Strings, _link, new ImmediateUi(), new ManualTime(), _ => null, AppTheme.Dark, _ => { });
    }

    private static RemoteAsk Confirm(string id = "c1") =>
        new(id, false, "Remote service: put a file in place", "jobs/Labels.json (2,048 bytes)", [], "", null);

    private static RemoteAsk Approve(string id = "a1") =>
        new(id, true, "Remote service: correct recorded values", "batch B-0457",
            [new RemoteFieldChange("lot", "LOT-0475", "LOT-0457")], "Typed wrong at batch start; label shows LOT-0457", null);

    private RuntimeStatus With(bool active, params RemoteAsk[] asks) => Make.Stopped() with { Remote = new RemoteServiceInfo(active, asks) };

    [Fact]
    public void The_badge_shows_only_while_a_session_is_open_and_never_who()
    {
        _link.Connect(_client, Make.Stopped());
        Assert.False(_vm.RemoteActive);
        _link.Push(With(true));
        Assert.True(_vm.RemoteActive);
        Assert.Equal("Remote service active", Make.Strings["top.remote_service"]);
        _link.Push(Make.Stopped());
        Assert.False(_vm.RemoteActive);
    }

    [Fact]
    public async Task A_confirmation_opens_and_Yes_answers_with_no_secret()
    {
        _link.Connect(_client, Make.Stopped());
        _link.Push(With(true, Confirm()));
        var vm = Assert.IsType<RemoteRequestViewModel>(_vm.Overlay);
        Assert.False(vm.IsApproval);
        Assert.Equal("jobs/Labels.json (2,048 bytes)", vm.What);
        Assert.Contains("Sundaram remote service", vm.From);   // who asks is never named (REQ-08)
        await vm.YesCommand.ExecuteAsync(null);
        Assert.Equal([("c1", true, "", "", "")], _client.RemoteAnswers);
        Assert.Null(_vm.Overlay);
    }

    [Fact]
    public async Task An_approval_needs_a_name_and_PIN_and_shows_old_and_new_values()
    {
        _link.Connect(_client, Make.Stopped());
        _link.Push(With(true, Approve()));
        var vm = Assert.IsType<RemoteRequestViewModel>(_vm.Overlay);
        Assert.True(vm.IsApproval);
        Assert.Equal(["lot: LOT-0475 → LOT-0457"], vm.Changes);
        Assert.Contains("label shows", vm.Reason);
        await vm.YesCommand.ExecuteAsync(null);
        Assert.Empty(_client.RemoteAnswers);           // no name: nothing sent
        Assert.Equal("Enter your name.", vm.Message);
        vm.UserName = "priya";
        vm.Secret = "4321";
        await vm.YesCommand.ExecuteAsync(null);
        Assert.Equal([("a1", true, "priya", "pin", "4321")], _client.RemoteAnswers);
        Assert.Equal("", vm.Secret);                   // never kept
    }

    [Fact]
    public async Task A_supervisors_card_approves_and_No_rejects_without_a_secret()
    {
        _link.Connect(_client, Make.Stopped());
        _link.Push(With(true, Approve()));
        await _vm.CardReadAsync("CARD-0042");
        Assert.Equal([("a1", true, "", "card", "CARD-0042")], _client.RemoteAnswers);

        _link.Push(With(true, Approve("a2")));
        var vm = Assert.IsType<RemoteRequestViewModel>(_vm.Overlay);
        await vm.NoCommand.ExecuteAsync(null);
        Assert.Equal(("a2", false, "", "", ""), _client.RemoteAnswers[^1]);
    }

    [Fact]
    public void A_refused_answer_keeps_the_panel_with_the_reason()
    {
        _client.RemoteAnswerResult = new CommandResult(false, "runtime.remote.notSupervisor", "Only a supervisor, QA or an administrator approves a correction.");
        _link.Connect(_client, Make.Stopped());
        _link.Push(With(true, Approve()));
        var vm = Assert.IsType<RemoteRequestViewModel>(_vm.Overlay);
        vm.UserName = "op";
        vm.Secret = "1111";
        vm.YesCommand.Execute(null);
        Assert.Same(vm, _vm.Overlay);
        Assert.Contains("supervisor", vm.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_request_answered_elsewhere_or_expired_closes_and_never_covers_another_panel()
    {
        _link.Connect(_client, Make.Stopped());
        _link.Push(With(true, Confirm()));
        Assert.IsType<RemoteRequestViewModel>(_vm.Overlay);
        _link.Push(With(true));                         // gone
        Assert.Null(_vm.Overlay);

        var other = new object();
        _vm.Show(other);
        _link.Push(With(true, Confirm("c2")));
        Assert.Same(other, _vm.Overlay);                // waits for the panel open now
        _vm.Close();
        _link.Push(With(true, Confirm("c2")));
        Assert.Equal("c2", Assert.IsType<RemoteRequestViewModel>(_vm.Overlay).Id);
    }

    [Fact]
    public void A_link_drop_closes_the_panel()
    {
        _link.Connect(_client, Make.Stopped());
        _link.Push(With(true, Confirm()));
        Assert.IsType<RemoteRequestViewModel>(_vm.Overlay);
        _link.Drop();
        Assert.Null(_vm.Overlay);
    }
}
