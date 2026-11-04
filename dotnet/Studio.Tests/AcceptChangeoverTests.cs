using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>A second person accepts the changeover at the line PC while the plant server cannot be reached.</summary>
public class AcceptChangeoverTests
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

    private readonly ManualLink _link = new();
    private readonly ScriptedClient _client = new();
    private readonly ManualTime _time = new();
    private readonly Overlays _overlays = new();
    private readonly RunViewModel _vm;

    public AcceptChangeoverTests()
    {
        _vm = new RunViewModel(Make.Strings, new RuntimeCalls(_link), _overlays, _time, _ => new FakeImage());
        _link.StateChanged += s => _vm.Apply(s, null);
        _link.StatusReceived += s => _vm.Apply(_link.State, s);
    }

    private static RuntimeStatus With(RuntimeStatus status, ChangeoverCheck state, bool atLine = false) => status with
    {
        Changeover = new ChangeoverInfo(state, "3fa1c2d0", "", null, "", atLine),
        Batch = [new BatchEntry("lot", "Lot", "L1", "L2611"), new BatchEntry("mrp", "MRP", "100", "125.00")],
    };

    private void Connect(RuntimeStatus status)
    {
        _client.Status = status;
        _link.Connect(_client, status);
    }

    private AcceptChangeoverViewModel Open()
    {
        Connect(With(Make.Stopped(), ChangeoverCheck.NoServer));
        _vm.AcceptHereCommand.Execute(null);
        return Assert.IsType<AcceptChangeoverViewModel>(_overlays.Shown);
    }

    [Theory]
    [InlineData(ChangeoverCheck.NoServer, true)]
    [InlineData(ChangeoverCheck.Waiting, false)]
    [InlineData(ChangeoverCheck.Refused, false)]
    [InlineData(ChangeoverCheck.Accepted, false)]
    [InlineData(ChangeoverCheck.NotNeeded, false)]
    public void The_button_shows_only_without_a_plant_server(ChangeoverCheck state, bool shown)
    {
        Connect(With(Make.Stopped(), state));
        Assert.Equal(shown, _vm.CanAcceptHere);
    }

    [Fact]
    public void The_button_is_not_shown_while_the_line_runs()
    {
        Connect(With(Make.Running(), ChangeoverCheck.NoServer));
        Assert.False(_vm.CanAcceptHere);
    }

    [Fact]
    public void The_button_is_not_shown_when_not_connected_or_when_the_runtime_sends_no_changeover()
    {
        Connect(Make.Stopped());
        Assert.False(_vm.CanAcceptHere);
        Connect(With(Make.Stopped(), ChangeoverCheck.NoServer));
        Assert.True(_vm.CanAcceptHere);
        _link.Drop();
        Assert.False(_vm.CanAcceptHere);
    }

    [Fact]
    public void The_dialog_shows_the_product_every_value_and_the_code()
    {
        var dialog = Open();
        Assert.Equal("Whisky 750 ml", dialog.Product);
        Assert.Equal(["Lot: L2611", "MRP: 125.00"], dialog.Values);
        Assert.Equal("3fa1c2d0", dialog.Code);
        Assert.Equal("Check these values. You accept them as the second person.", dialog.CheckText);
    }

    [Fact]
    public async Task Accept_sends_the_name_the_pin_and_the_code_then_closes_with_a_notice()
    {
        var dialog = Open();
        dialog.UserName = " ramesh ";
        dialog.Secret = "1234";
        await dialog.AcceptCommand.ExecuteAsync(null);
        Assert.Equal([("ramesh", "pin", "1234", "3fa1c2d0")], _client.Accepts);
        Assert.Null(_overlays.Shown);
        Assert.True(_vm.HasNotice);
        Assert.Equal("Changeover accepted. You can start now.", _vm.NoticeText);
        Assert.Equal("", dialog.Secret);
    }

    [Fact]
    public async Task The_password_switch_sends_the_password()
    {
        var dialog = Open();
        dialog.SwitchSecretCommand.Execute(null);
        Assert.Equal("Password", dialog.SecretLabel);
        Assert.Equal("Use PIN", dialog.SwitchText);
        dialog.UserName = "ramesh";
        dialog.Secret = "secret-pass";
        await dialog.AcceptCommand.ExecuteAsync(null);
        Assert.Equal([("ramesh", "password", "secret-pass", "3fa1c2d0")], _client.Accepts);
    }

    [Fact]
    public async Task A_refusal_shows_the_runtime_text_and_clears_the_pin()
    {
        _client.AcceptResult = new CommandResult(false, "runtime.changeover.samePerson", "raw text");
        var dialog = Open();
        dialog.UserName = "anita";
        dialog.Secret = "1234";
        await dialog.AcceptCommand.ExecuteAsync(null);
        Assert.Same(dialog, _overlays.Shown);
        Assert.Equal("You cannot accept your own changeover. A second person must do it.", dialog.Message);
        Assert.Equal("", dialog.Secret);
        Assert.Equal("anita", dialog.UserName);
        Assert.False(_vm.HasNotice);
    }

    [Fact]
    public async Task A_refusal_without_our_key_shows_the_runtime_own_text()
    {
        _client.AcceptResult = new CommandResult(false, null, "PIN not right.");
        var dialog = Open();
        dialog.UserName = "ramesh";
        dialog.Secret = "0000";
        await dialog.AcceptCommand.ExecuteAsync(null);
        Assert.Equal("PIN not right.", dialog.Message);
    }

    [Fact]
    public async Task An_empty_name_or_secret_does_not_call_the_runtime()
    {
        var dialog = Open();
        dialog.Secret = "1234";
        await dialog.AcceptCommand.ExecuteAsync(null);
        Assert.Equal("Enter your name.", dialog.Message);
        dialog.UserName = "ramesh";
        dialog.Secret = "";
        await dialog.AcceptCommand.ExecuteAsync(null);
        Assert.Equal("Enter your PIN.", dialog.Message);
        dialog.UserName = "   ";
        dialog.Secret = "1234";
        await dialog.AcceptCommand.ExecuteAsync(null);
        Assert.Empty(_client.Accepts);
        Assert.Same(dialog, _overlays.Shown);
    }

    [Fact]
    public async Task A_badge_accepts_with_no_name_and_is_not_kept()
    {
        var dialog = Open();
        dialog.UserName = "half typed";
        await dialog.CardAsync("0004211337");
        Assert.Equal([("", "card", "0004211337", "3fa1c2d0")], _client.Accepts);
        Assert.Null(_overlays.Shown);
        Assert.Equal("", dialog.UserName);
    }

    [Fact]
    public void Cancel_closes_and_clears_the_secret()
    {
        var dialog = Open();
        dialog.Secret = "1234";
        dialog.CancelCommand.Execute(null);
        Assert.Null(_overlays.Shown);
        Assert.Equal("", dialog.Secret);
        Assert.Empty(_client.Accepts);
    }
}
