using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// A remote service request at the line PC (REMOTE-SUPPORT.md 4b). A confirmation: the operator sees what will change (a job
/// file put in place, an action) and answers Yes or No. An approval of a correction: each field's recorded and corrected value
/// and the engineer's reason; a supervisor approves with their own name and PIN (or password), or taps their card. Never who
/// asks, only "Sundaram remote service" (REQ-08). The runtime decides who may approve; the secret is passed on once and never
/// kept, logged or shown. The panel closes when the request is answered, expires or is withdrawn.
/// </summary>
public sealed partial class RemoteRequestViewModel : ObservableObject
{
    private readonly Strings _strings;
    private readonly RuntimeCalls _calls;
    private readonly Action _close;

    public RemoteRequestViewModel(Strings strings, RuntimeCalls calls, RemoteAsk ask, Action close)
    {
        _strings = strings;
        _calls = calls;
        Ask = ask;
        _close = close;
        Changes = ask.Changes.Select(c => string.Format(strings["remote.request.change"], c.Field, c.Old, c.New)).ToArray();
    }

    public RemoteAsk Ask { get; }
    public string Id => Ask.Id;
    public bool IsApproval => Ask.Approval;

    public string Title => _strings[IsApproval ? "remote.request.approve_title" : "remote.request.confirm_title"];
    public string From => _strings["remote.request.from"];
    public string What => Ask.What;
    public string Kind => Ask.Title;
    /// <summary>A correction's fields as "Field: recorded → corrected".</summary>
    public IReadOnlyList<string> Changes { get; }
    public bool HasChanges => Changes.Count > 0;
    public string ReasonLabel => _strings["remote.request.reason"];
    public string Reason => Ask.Reason;
    public bool HasReason => Ask.Reason.Length > 0;
    public string CheckText => _strings[IsApproval ? "remote.request.approve_check" : "remote.request.confirm_check"];
    public string NameLabel => _strings["changeover.accept.name"];
    public string SecretLabel => _strings[UsePassword ? "changeover.accept.password" : "changeover.accept.pin"];
    public string SwitchText => _strings[UsePassword ? "signin.use_pin" : "signin.use_password"];
    public string CardHint => _strings["changeover.accept.card_hint"];
    public string YesText => _strings[IsApproval ? "remote.request.approve" : "remote.request.yes"];
    public string NoText => _strings["remote.request.no"];

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
    private async Task Yes()
    {
        if (Busy) return;
        if (!IsApproval)
        {
            await SendAsync(true, "", "", "");
            return;
        }
        var user = UserName.Trim();
        if (user.Length == 0) { Message = _strings["changeover.accept.name_missing"]; return; }
        if (Secret.Length == 0) { Message = _strings[UsePassword ? "changeover.accept.password_missing" : "changeover.accept.pin_missing"]; return; }
        var secret = Secret;
        Secret = "";
        await SendAsync(true, user, UsePassword ? "password" : "pin", secret);
    }

    [RelayCommand]
    private async Task No()
    {
        if (Busy) return;
        Secret = "";
        await SendAsync(false, "", "", "");
    }

    /// <summary>A badge was read while a correction waits for approval: approves at once with the card, no name.</summary>
    public async Task CardAsync(string card)
    {
        if (Busy || !IsApproval) return;
        UserName = "";
        Secret = "";
        Message = _strings["signin.reading_card"];
        await SendAsync(true, "", "card", card);
    }

    private async Task SendAsync(bool approve, string user, string kind, string secret)
    {
        Busy = true;
        try
        {
            var (reached, result) = await _calls.TryDesignAsync((c, ct) => c.AnswerRemoteAsync(Id, approve, user, kind, secret, ct));
            if (!reached) Message = _strings["run.reason.not_connected"];
            else if (result is null) Message = _strings["run.reason.no_answer"];
            else if (!result.Ok) Message = _strings.KeyOrText(result.ReasonKey, result.ReasonText, "remote.request.refused");
            else _close();
        }
        finally { Busy = false; }
    }
}
