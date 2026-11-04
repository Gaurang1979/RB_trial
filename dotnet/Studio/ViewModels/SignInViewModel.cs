using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

public sealed partial class UserTileViewModel(UserTile user) : ObservableObject
{
    public UserTile User { get; } = user;
    public string Name => User.DisplayName;
    public string Initial => User.DisplayName.Length > 0 ? User.DisplayName[..1].ToUpperInvariant() : "?";
    [ObservableProperty] private bool _selected;
}

/// <summary>
/// Sign in (UI-UX-SPEC.md 4.5): a user tile and PIN, "Use password" with name and password, or a
/// badge on the card reader at any time. The runtime checks every secret, counts wrong tries and
/// locks; the app reads no users file. PIN, password and card number are cleared after each try
/// and never shown.
/// </summary>
public sealed partial class SignInViewModel : ObservableObject
{
    public const int MinPin = 4;
    public const int MaxPin = 8;

    private readonly Strings _strings;
    private readonly RuntimeCalls _calls;
    private readonly Action<SignedInUser> _signedIn;
    private readonly Action _back;
    private string _pin = "";

    public SignInViewModel(Strings strings, RuntimeCalls calls, Action<SignedInUser> signedIn, Action back)
    {
        _strings = strings;
        _calls = calls;
        _signedIn = signedIn;
        _back = back;
        Message = strings["signin.pick_user"];
    }

    public ObservableCollection<UserTileViewModel> Users { get; } = [];
    private IReadOnlyList<UserTile> _all = [];

    [ObservableProperty] private UserTileViewModel? _selectedUser;
    [ObservableProperty] private string _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _busy;
    /// <summary>Flips on each wrong PIN so the view can shake the dots.</summary>
    [ObservableProperty] private int _wrongCount;
    /// <summary>The name and password form instead of the tiles and PIN pad.</summary>
    [ObservableProperty] private bool _passwordMode;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _password = "";
    /// <summary>Today's code that opens a Support or OEM role (AS-04h); empty for everyone else. Cleared after each try.</summary>
    [ObservableProperty] private string _code = "";

    public int PinLength => _pin.Length;

    /// <summary>Filled dots for typed digits, open ones up to four (never the digits).</summary>
    public string PinDots => string.Join(" ", Enumerable.Range(0, Math.Max(MinPin, _pin.Length))
        .Select(i => i < _pin.Length ? "●" : "○"));

    public string Title => _strings["signin.title"];
    public string BackText => _strings["common.back"];
    public string DeleteText => _strings["signin.delete"];
    public string OkText => _strings["signin.ok"];
    public string CardHint => _strings["signin.card_hint"];
    public string NameText => _strings["signin.name"];
    public string PasswordText => _strings["signin.password"];
    public string CodeText => _strings["signin.code"];
    public string SwitchText => _strings[PasswordMode ? "signin.use_pin" : "signin.use_password"];

    partial void OnPasswordModeChanged(bool value) => OnPropertyChanged(nameof(SwitchText));

    public async Task LoadAsync()
    {
        var (reached, users) = await _calls.TryAsync((c, ct) => c.ListUsersAsync(ct));
        if (!reached || users is null)
        {
            SetMessage(reached ? "signin.refused" : "run.reason.not_connected", error: true);
            return;
        }
        _all = users;
        foreach (var u in users.Where(u => u.HasPin)) Users.Add(new UserTileViewModel(u));
        if (Users.Count == 1) PickUser(Users[0]);
    }

    [RelayCommand]
    private void PickUser(UserTileViewModel user)
    {
        foreach (var u in Users) u.Selected = u == user;
        SelectedUser = user;
        SetPin("");
        SetMessage("signin.enter_pin", error: false, user.Name);
    }

    [RelayCommand]
    private void Digit(string digit)
    {
        if (Busy || _pin.Length >= MaxPin || digit.Length != 1 || !char.IsAsciiDigit(digit[0])) return;
        SetPin(_pin + digit);
    }

    [RelayCommand]
    private void Delete()
    {
        if (_pin.Length > 0) SetPin(_pin[..^1]);
    }

    /// <summary>"Use password" and back to "Use PIN".</summary>
    [RelayCommand]
    private void SwitchMode()
    {
        if (Busy) return;
        PasswordMode = !PasswordMode;
        SetPin("");
        Password = "";
        Code = "";
        if (PasswordMode)
        {
            UserName = SelectedUser?.User is { HasPassword: true } u ? u.Id : "";
            SetMessage("signin.enter_password", error: false);
        }
        else if (SelectedUser is { } user) SetMessage("signin.enter_pin", error: false, user.Name);
        else SetMessage("signin.pick_user", error: false);
    }

    [RelayCommand]
    private async Task Ok()
    {
        if (Busy) return;
        if (PasswordMode)
        {
            await PasswordAsync();
            return;
        }
        if (SelectedUser is null)
        {
            SetMessage("signin.pick_user", error: true);
            return;
        }
        if (_pin.Length < MinPin)
        {
            SetMessage("signin.pin_short", error: true, MinPin);
            return;
        }
        Busy = true;
        var pin = _pin;
        var userId = SelectedUser.User.Id;
        var (reached, result) = await _calls.TryAsync((c, ct) => c.SignInWithPinAsync(userId, pin, ct));
        Busy = false;
        SetPin("");
        Show(reached, result, "signin.wrong_pin");
    }

    private async Task PasswordAsync()
    {
        // the name as typed, or as on a tile ("Anita" for "operator")
        var typed = UserName.Trim();
        var userId = _all.FirstOrDefault(u => string.Equals(u.DisplayName, typed, StringComparison.OrdinalIgnoreCase))?.Id ?? typed;
        if (userId.Length == 0)
        {
            SetMessage("signin.name_missing", error: true);
            return;
        }
        if (Password.Length == 0)
        {
            SetMessage("signin.password_missing", error: true);
            return;
        }
        Busy = true;
        var password = Password;
        var code = Code;
        Password = "";
        Code = "";
        var (reached, result) = await _calls.TryAsync((c, ct) => c.SignInWithPasswordAsync(userId, password, code, ct));
        Busy = false;
        Show(reached, result, "signin.wrong_password");
    }

    /// <summary>A badge was read (the window's <see cref="CardWedge"/>): signs in at once, whatever
    /// is on the screen. A name or password half typed is dropped, since the reader's keys went into it.</summary>
    public async Task CardAsync(string card)
    {
        if (Busy) return;
        // the reader's keys went into whichever box had the focus
        Password = "";
        Code = "";
        UserName = "";
        SetPin("");
        Busy = true;
        SetMessage("signin.reading_card", error: false);
        var (reached, result) = await _calls.TryAsync((c, ct) => c.SignInWithCardAsync(card, ct));
        Busy = false;
        Show(reached, result, "signin.card_unknown");
    }

    private void Show(bool reached, SignInResult? result, string wrongKey)
    {
        if (!reached || result is null)
        {
            SetMessage(reached ? "signin.refused" : "run.reason.not_connected", error: true);
            return;
        }
        switch (result.Outcome)
        {
            case SignInOutcome.Ok when result.User is not null:
                _signedIn(result.User);
                break;
            case SignInOutcome.Refused:
                Message = result.Text is { Length: > 0 } text ? text : _strings["signin.refused"];
                IsError = true;
                break;
            default:
                WrongCount++;
                SetMessage(wrongKey, error: true);
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        Password = "";
        Code = "";
        SetPin("");
        _back();
    }

    private void SetPin(string pin)
    {
        _pin = pin;
        OnPropertyChanged(nameof(PinLength));
        OnPropertyChanged(nameof(PinDots));
    }

    private void SetMessage(string key, bool error, params object[] args)
    {
        Message = _strings.Format(key, args);
        IsError = error;
    }
}
