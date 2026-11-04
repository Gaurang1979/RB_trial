using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

public class SignInViewModelTests
{
    private readonly ManualLink _link = new();
    private readonly ScriptedClient _client = new();
    private SignedInUser? _signedIn;
    private bool _back;
    private readonly SignInViewModel _vm;

    public SignInViewModelTests()
    {
        _link.Connect(_client, Make.Stopped());
        _vm = new SignInViewModel(Make.Strings, new RuntimeCalls(_link), u => _signedIn = u, () => _back = true);
    }

    [Fact]
    public async Task Lists_users_from_the_runtime_and_asks_for_a_name_first()
    {
        await _vm.LoadAsync();
        Assert.Equal(["Anita", "Ramesh"], _vm.Users.Select(u => u.Name));
        await _vm.OkCommand.ExecuteAsync(null);
        Assert.Equal("Tap your name.", _vm.Message);
        Assert.True(_vm.IsError);
    }

    [Fact]
    public async Task Pin_is_shown_as_dots_up_to_eight_digits_and_digits_only()
    {
        await _vm.LoadAsync();
        _vm.PickUserCommand.Execute(_vm.Users[0]);
        Assert.Equal("Anita, enter your PIN.", _vm.Message);
        Assert.Equal("○ ○ ○ ○", _vm.PinDots);
        foreach (var d in "12x") _vm.DigitCommand.Execute(d.ToString());
        Assert.Equal("● ● ○ ○", _vm.PinDots);
        for (var i = 0; i < 10; i++) _vm.DigitCommand.Execute("9");
        Assert.Equal(SignInViewModel.MaxPin, _vm.PinLength);
        Assert.DoesNotContain("9", _vm.PinDots);
        _vm.DeleteCommand.Execute(null);
        Assert.Equal(SignInViewModel.MaxPin - 1, _vm.PinLength);
    }

    [Fact]
    public async Task A_short_PIN_is_not_sent()
    {
        await _vm.LoadAsync();
        _vm.PickUserCommand.Execute(_vm.Users[0]);
        _vm.DigitCommand.Execute("1");
        await _vm.OkCommand.ExecuteAsync(null);
        Assert.Equal("The PIN has at least 4 digits.", _vm.Message);
        Assert.DoesNotContain(_client.Calls, c => c.StartsWith("SignIn"));
    }

    [Fact]
    public async Task Wrong_PIN_says_so_clears_and_shakes()
    {
        await _vm.LoadAsync();
        _vm.PickUserCommand.Execute(_vm.Users[0]);
        foreach (var d in "9999") _vm.DigitCommand.Execute(d.ToString());
        await _vm.OkCommand.ExecuteAsync(null);
        Assert.Equal("PIN not right. Try again.", _vm.Message);
        Assert.Equal(0, _vm.PinLength);
        Assert.Equal(1, _vm.WrongCount);
        Assert.Null(_signedIn);
    }

    [Fact]
    public async Task Right_PIN_signs_in_through_the_runtime()
    {
        await _vm.LoadAsync();
        _vm.PickUserCommand.Execute(_vm.Users[1]);
        foreach (var d in "1234") _vm.DigitCommand.Execute(d.ToString());
        await _vm.OkCommand.ExecuteAsync(null);
        Assert.Contains("SignIn supervisor", _client.Calls);
        Assert.Equal(("Ramesh", Role.Supervisor), (_signedIn!.DisplayName, _signedIn.Role));
    }

    [Fact]
    public void Back_leaves_without_signing_in()
    {
        _vm.BackCommand.Execute(null);
        Assert.True(_back);
    }
}

public class MainViewModelTests
{
    private readonly ManualLink _link = new();
    private readonly ScriptedClient _client = new();
    private readonly List<AppTheme> _themes = [];
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        _vm = new MainViewModel(Make.Strings, _link, new ImmediateUi(), new ManualTime(), _ => null,
            AppTheme.Dark, _themes.Add);
    }

    [Fact]
    public void Starts_connecting_then_shows_the_runtime_state()
    {
        Assert.Equal("Connecting", _vm.PillText);
        _link.Connect(_client, Make.Running());
        Assert.Equal(("Running", Tone.Ok), (_vm.PillText, _vm.PillTone));
        Assert.Equal("Whisky 750 ml", _vm.ProductText);
        Assert.Equal("Operator", _vm.UserText);
    }

    [Fact]
    public async Task Sign_in_then_a_drop_returns_to_Operator_and_closes_runtime_panels()
    {
        _link.Connect(_client, Make.Running());
        await _vm.UserTapCommand.ExecuteAsync(null);
        var signIn = Assert.IsType<SignInViewModel>(_vm.Overlay);
        signIn.PickUserCommand.Execute(signIn.Users[0]);
        foreach (var d in "1234") signIn.DigitCommand.Execute(d.ToString());
        await signIn.OkCommand.ExecuteAsync(null);
        Assert.Null(_vm.Overlay);
        Assert.Equal("Anita", _vm.UserText);

        _vm.Run.ChangeProductCommand.Execute(null);
        Assert.IsType<ProductPickerViewModel>(_vm.Overlay);
        _link.Drop();
        Assert.Null(_vm.Overlay);
        Assert.Equal("Operator", _vm.UserText);
        Assert.Equal(("Not connected", Tone.Ng), (_vm.PillText, _vm.PillTone));
    }

    [Fact]
    public void A_drop_closes_What_do_I_do_but_keeps_the_NG_viewer()
    {
        _link.Connect(_client, Make.Running() with { Alarm = "Too dark.", AlarmHelp = "Check the light." });
        _vm.Run.WhatToDoCommand.Execute(null);
        Assert.IsType<AlarmHelpViewModel>(_vm.Overlay);
        _link.Drop();
        Assert.Null(_vm.Overlay);

        _link.Connect(_client, Make.Running());
        _link.Push(Make.Frame(Verdict.Ng));
        _vm.Run.OpenNgCommand.Execute(_vm.Run.NgStrip[0]);
        _link.Drop();
        Assert.IsType<NgViewerViewModel>(_vm.Overlay);
    }

    [Fact]
    public void On_reconnect_the_runtime_says_who_is_still_signed_in()
    {
        _client.Who = new SignedInUser("supervisor", "Ramesh", Role.Supervisor, true);
        _link.Connect(_client, Make.Running());
        Assert.Contains("WhoAmI", _client.Calls);
        Assert.Equal("Ramesh", _vm.UserText);
    }

    [Fact]
    public async Task A_command_refused_for_sign_in_shows_Operator_again()
    {
        _client.Who = new SignedInUser("operator", "Anita", Role.Operator, true);
        _link.Connect(_client, Make.Stopped());
        Assert.Equal("Anita", _vm.UserText);
        _client.StartResult = new CommandResult(false, "run.reason.sign_in");
        await _vm.Run.StartCommand.ExecuteAsync(null);
        Assert.Equal("Operator", _vm.UserText);
        Assert.Equal("Sign in first. Tap Operator at the top right.", _vm.Run.NoticeText);
    }

    [Fact]
    public async Task Tapping_the_signed_in_user_signs_out()
    {
        _client.Who = new SignedInUser("operator", "Anita", Role.Operator, true);
        _link.Connect(_client, Make.Running());
        await _vm.UserTapCommand.ExecuteAsync(null);
        Assert.Equal("Operator", _vm.UserText);
        Assert.Contains("SignOut", _client.Calls);
    }

    [Fact]
    public void Display_panel_changes_theme_of_the_app_only()
    {
        _vm.DisplayCommand.Execute(null);
        var display = Assert.IsType<DisplayOptionsViewModel>(_vm.Overlay);
        Assert.Contains(display.Languages, l => l.Id == "en" && l.Selected);
        display.PickThemeCommand.Execute(display.Themes.First(t => t.Id == nameof(AppTheme.HighContrast)));
        Assert.Equal([AppTheme.HighContrast], _themes);
        Assert.Equal(AppTheme.HighContrast, _vm.Theme);
        Assert.Empty(_client.Calls);
    }
}
