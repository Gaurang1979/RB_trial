using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// A second person accepts the changeover at the line PC while the plant server cannot be reached
/// (CHANGEOVER-VERIFY.md; owner 2026-11-01: "Yes, allow at PC"). The dialog shows the product, every
/// batch value and the code as the runtime holds them now; the code goes back with the secret so the
/// person accepts exactly what they saw. The runtime decides who may (Supervisor, QA, Admin, not the
/// operator and not the person who made the change). The PIN, password and card number are passed on
/// once and never kept, logged or shown.
/// </summary>
public sealed partial class AcceptChangeoverViewModel : ObservableObject
{
    private readonly Strings _strings;
    private readonly RuntimeCalls _calls;
    private readonly Action _close;
    private readonly Action _accepted;

    public AcceptChangeoverViewModel(Strings strings, RuntimeCalls calls, string product, IReadOnlyList<BatchEntry> batch,
        string code, Action close, Action accepted)
    {
        _strings = strings;
        _calls = calls;
        Product = product;
        Values = batch.Select(b => $"{b.Label}: {b.Value}").ToArray();
        Code = code;
        _close = close;
        _accepted = accepted;
    }

    public string Product { get; }
    /// <summary>Every batch value as "Label: value", in tool order.</summary>
    public IReadOnlyList<string> Values { get; }
    public bool HasValues => Values.Count > 0;
    public string Code { get; }

    public string Title => _strings["changeover.accept.title"];
    public string ProductLabel => _strings["changeover.accept.product"];
    public string CodeLabel => _strings["changeover.accept.code"];
    public string CheckText => _strings["changeover.accept.check"];
    public string NameLabel => _strings["changeover.accept.name"];
    public string SecretLabel => _strings[UsePassword ? "changeover.accept.password" : "changeover.accept.pin"];
    public string SwitchText => _strings[UsePassword ? "signin.use_pin" : "signin.use_password"];
    public string CardHint => _strings["changeover.accept.card_hint"];
    public string AcceptText => _strings["changeover.accept.button"];
    public string CancelText => _strings["changeover.accept.cancel"];

    [ObservableProperty] private string _userName = "";
    /// <summary>The PIN or the password, as typed; cleared after every try.</summary>
    [ObservableProperty] private string _secret = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecretLabel), nameof(SwitchText))]
    private bool _usePassword;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _busy;

    partial void OnUsePasswordChanged(bool value) => Secret = "";

    [RelayCommand]
    private void SwitchSecret() => UsePassword = !UsePassword;

    [RelayCommand]
    private async Task Accept()
    {
        if (Busy) return;
        var user = UserName.Trim();
        if (user.Length == 0)
        {
            Message = _strings["changeover.accept.name_missing"];
            return;
        }
        if (Secret.Length == 0)
        {
            Message = _strings[UsePassword ? "changeover.accept.password_missing" : "changeover.accept.pin_missing"];
            return;
        }
        var kind = UsePassword ? "password" : "pin";
        var secret = Secret;
        Secret = "";
        await SendAsync(user, kind, secret);
    }

    /// <summary>A badge was read (the window's CardWedge): accepts at once, with no name. A name or PIN half
    /// typed is dropped, since the reader's keys went into whichever box had the focus.</summary>
    public async Task CardAsync(string card)
    {
        if (Busy) return;
        UserName = "";
        Secret = "";
        Message = _strings["signin.reading_card"];
        await SendAsync("", "card", card);
    }

    private async Task SendAsync(string user, string kind, string secret)
    {
        Busy = true;
        try
        {
            var (reached, result) = await _calls.TryDesignAsync((c, ct) => c.AcceptChangeoverAtLineAsync(user, kind, secret, Code, ct));
            if (!reached) Message = _strings["run.reason.not_connected"];
            else if (result is null) Message = _strings["run.reason.no_answer"];
            else if (!result.Ok) Message = _strings.KeyOrText(result.ReasonKey, result.ReasonText, "changeover.accept.refused");
            else
            {
                _close();
                _accepted();
            }
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        Secret = "";
        _close();
    }
}
