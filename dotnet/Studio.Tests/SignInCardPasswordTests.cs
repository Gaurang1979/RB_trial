using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

public class CardWedgeTests
{
    private readonly ManualTime _time = new();
    private readonly CardWedge _wedge;

    public CardWedgeTests() => _wedge = new CardWedge(_time);

    private void Type(string text, int msPerKey)
    {
        foreach (var c in text)
        {
            _time.Advance(TimeSpan.FromMilliseconds(msPerKey));
            _wedge.Text(c.ToString());
        }
        _time.Advance(TimeSpan.FromMilliseconds(msPerKey));
    }

    [Fact]
    public void A_fast_burst_ending_in_Enter_is_a_card()
    {
        Type("0004211337", 8);
        Assert.Equal("0004211337", _wedge.Enter(out var burst));
        Assert.True(burst);
        Assert.Null(_wedge.Enter(out _));   // handed on once, then forgotten
    }

    [Fact]
    public void A_person_typing_is_not_a_card()
    {
        Type("secret-pass", 120);
        Assert.Null(_wedge.Enter(out var burst));
        Assert.False(burst);   // a person's Enter presses OK as usual
    }

    [Fact]
    public void A_pause_inside_the_burst_starts_it_again()
    {
        Type("12345", 8);
        _time.Advance(TimeSpan.FromMilliseconds(400));
        Type("678", 8);
        Assert.Null(_wedge.Enter(out var burst));   // only "678" counts, too short
        Assert.True(burst);   // but its Enter is the reader's, not a button press
    }

    [Fact]
    public void Short_long_or_slow_to_Enter_is_not_a_card()
    {
        Type("12345", 8);
        Assert.Null(_wedge.Enter(out _));
        Type(new string('7', CardWedge.MaxLength + 1), 2);
        Assert.Null(_wedge.Enter(out var burst));
        Assert.True(burst);
        Type("0004211337", 8);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Null(_wedge.Enter(out burst));
        Assert.False(burst);
    }

    [Fact]
    public void Another_key_ends_the_burst()
    {
        Type("000421", 8);
        _wedge.Clear();
        Type("1337", 8);
        Assert.Null(_wedge.Enter(out _));
    }
}

public class SignInPasswordAndCardTests
{
    private readonly ManualLink _link = new();
    private readonly ScriptedClient _client = new();
    private SignedInUser? _signedIn;
    private readonly SignInViewModel _vm;

    public SignInPasswordAndCardTests()
    {
        _link.Connect(_client, Make.Stopped());
        _vm = new SignInViewModel(Make.Strings, new RuntimeCalls(_link), u => _signedIn = u, () => { });
    }

    [Fact]
    public async Task Use_password_switches_to_name_and_password_and_back()
    {
        await _vm.LoadAsync();
        Assert.Equal("Use password", _vm.SwitchText);
        _vm.SwitchModeCommand.Execute(null);
        Assert.True(_vm.PasswordMode);
        Assert.Equal("Use PIN", _vm.SwitchText);
        Assert.Equal("Enter your name and password.", _vm.Message);
        _vm.Password = "half";
        _vm.SwitchModeCommand.Execute(null);
        Assert.False(_vm.PasswordMode);
        Assert.Equal("", _vm.Password);
    }

    [Fact]
    public async Task Todays_code_goes_with_the_password_and_is_cleared_after_the_try()
    {
        await _vm.LoadAsync();
        _vm.SwitchModeCommand.Execute(null);
        Assert.Equal("Today's code (Support and OEM only)", _vm.CodeText);
        _vm.UserName = "eng";
        _vm.Password = "guess";
        _vm.Code = " 123456 ";
        await _vm.OkCommand.ExecuteAsync(null);
        Assert.Equal([" 123456 "], _client.Codes);
        Assert.Equal("", _vm.Code);
        _vm.Code = "654321";
        _vm.SwitchModeCommand.Execute(null);   // back to the PIN pad: the code goes too
        Assert.Equal("", _vm.Code);
    }

    [Fact]
    public async Task Name_and_password_are_needed_before_asking_the_runtime()
    {
        await _vm.LoadAsync();
        _vm.SwitchModeCommand.Execute(null);
        await _vm.OkCommand.ExecuteAsync(null);
        Assert.Equal("Enter your name.", _vm.Message);
        _vm.UserName = "eng";
        await _vm.OkCommand.ExecuteAsync(null);
        Assert.Equal("Enter your password.", _vm.Message);
        Assert.DoesNotContain(_client.Calls, c => c.StartsWith("SignIn"));
    }

    [Fact]
    public async Task Wrong_password_says_so_and_clears_the_box()
    {
        await _vm.LoadAsync();
        _vm.SwitchModeCommand.Execute(null);
        _vm.UserName = "eng";
        _vm.Password = "guess";
        await _vm.OkCommand.ExecuteAsync(null);
        Assert.Equal("Name or password not right. Try again.", _vm.Message);
        Assert.True(_vm.IsError);
        Assert.Equal("", _vm.Password);
        Assert.Null(_signedIn);
    }

    [Fact]
    public async Task Right_password_signs_in_by_the_shown_name_too()
    {
        await _vm.LoadAsync();
        _vm.SwitchModeCommand.Execute(null);
        _vm.UserName = " suresh ";
        _vm.Password = "secret-pass";
        await _vm.OkCommand.ExecuteAsync(null);
        Assert.Contains("SignIn password eng", _client.Calls);
        Assert.Equal(("Suresh", Role.Engineer), (_signedIn!.DisplayName, _signedIn.Role));
        Assert.Equal("", _vm.Password);
    }

    [Fact]
    public async Task A_card_signs_in_at_once_and_drops_a_half_typed_password()
    {
        await _vm.LoadAsync();
        _vm.SwitchModeCommand.Execute(null);
        _vm.Password = "0004211337";   // the reader's keys landed in the box
        await _vm.CardAsync("0004211337");
        Assert.Equal("", _vm.Password);
        Assert.Equal("Ramesh", _signedIn!.DisplayName);
    }

    [Fact]
    public async Task An_unknown_card_says_so_and_never_shows_the_number()
    {
        await _vm.LoadAsync();
        _vm.SwitchModeCommand.Execute(null);
        _vm.UserName = "9999999999";   // the Name box had the focus
        await _vm.CardAsync("9999999999");
        Assert.Equal("This card is not known. Enter your PIN or call your supervisor.", _vm.Message);
        Assert.DoesNotContain("9999", _vm.Message);
        Assert.Equal("", _vm.UserName);
        Assert.Null(_signedIn);
    }
}

public class IdleSignOutTests
{
    private readonly ManualLink _link = new();
    private readonly ScriptedClient _client = new();
    private readonly ManualTime _time = new();
    private readonly MainViewModel _vm;

    public IdleSignOutTests()
    {
        _vm = new MainViewModel(Make.Strings, _link, new ImmediateUi(), _time, _ => null, AppTheme.Dark, _ => { });
    }

    private async Task SignInAnita()
    {
        _link.Connect(_client, Make.Running());
        await _vm.UserTapCommand.ExecuteAsync(null);
        var signIn = Assert.IsType<SignInViewModel>(_vm.Overlay);
        signIn.PickUserCommand.Execute(signIn.Users[0]);
        foreach (var d in "1234") signIn.DigitCommand.Execute(d.ToString());
        await signIn.OkCommand.ExecuteAsync(null);
        Assert.Equal("Anita", _vm.UserText);
    }

    [Fact]
    public async Task Signs_out_after_ten_minutes_without_a_touch_when_the_runtime_does_not_say()
    {
        _time.Advance(TimeSpan.FromHours(1));   // time before sign-in does not count
        await SignInAnita();
        _time.Advance(TimeSpan.FromMinutes(9));
        _vm.Tick();
        Assert.Equal("Anita", _vm.UserText);
        _time.Advance(TimeSpan.FromMinutes(1));
        _vm.Tick();
        Assert.Equal("Operator", _vm.UserText);
        Assert.Contains("SignOut", _client.Calls);
    }

    [Fact]
    public async Task A_touch_keeps_the_user_signed_in()
    {
        await SignInAnita();
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(8));
            _vm.NoteInput();
            _vm.Tick();
        }
        Assert.Equal("Anita", _vm.UserText);
    }

    [Fact]
    public void The_plant_idle_time_from_the_runtime_is_used_and_closes_change_panels()
    {
        _client.Who = new SignedInUser("supervisor", "Ramesh", Role.Supervisor, true, TimeSpan.FromMinutes(2));
        _link.Connect(_client, Make.Stopped());
        _vm.Run.ChangeProductCommand.Execute(null);
        Assert.IsType<ProductPickerViewModel>(_vm.Overlay);
        _time.Advance(TimeSpan.FromMinutes(2));
        _vm.Tick();
        Assert.Equal("Operator", _vm.UserText);
        Assert.Null(_vm.Overlay);
    }

    [Fact]
    public async Task A_card_on_the_Run_screen_replaces_the_signed_in_user()
    {
        await SignInAnita();
        await _vm.CardReadAsync("0004211337");
        Assert.Equal("Ramesh", _vm.UserText);
        Assert.Null(_vm.Overlay);
    }

    [Fact]
    public async Task An_unknown_card_or_a_scanned_barcode_leaves_the_signed_in_user_be()
    {
        await SignInAnita();
        await _vm.CardReadAsync("4006381333931");
        Assert.Equal("Anita", _vm.UserText);
        Assert.DoesNotContain("SignOut", _client.Calls);
    }

    [Fact]
    public async Task A_sign_in_left_open_forgets_its_password()
    {
        _link.Connect(_client, Make.Running());
        await _vm.UserTapCommand.ExecuteAsync(null);
        var signIn = Assert.IsType<SignInViewModel>(_vm.Overlay);
        signIn.SwitchModeCommand.Execute(null);
        signIn.Password = "half";
        _time.Advance(SignedInUser.DefaultIdleSignOut);
        _vm.Tick();
        Assert.Null(_vm.Overlay);
        Assert.Equal("", signIn.Password);
    }

    [Fact]
    public async Task An_unknown_card_opens_sign_in_with_the_reason()
    {
        _link.Connect(_client, Make.Running());
        await _vm.CardReadAsync("1111111111");
        var signIn = Assert.IsType<SignInViewModel>(_vm.Overlay);
        Assert.Equal("This card is not known. Enter your PIN or call your supervisor.", signIn.Message);
        Assert.Equal("Operator", _vm.UserText);
    }
}
