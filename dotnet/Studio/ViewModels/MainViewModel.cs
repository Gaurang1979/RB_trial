using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.Settings;
using VisionSystem.Studio.Sound;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// The window frame (UI-UX-SPEC.md 2): top bar with product, status pill, language and user,
/// the Run screen, and one overlay panel at a time. Holds no state the line depends on: all
/// of it comes from the runtime and is thrown away when the link drops.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IOverlayHost
{
    private readonly Strings _strings;
    private readonly IRuntimeLink _link;
    private readonly RuntimeCalls _calls;
    private readonly IUi _ui;
    private readonly Action<AppTheme> _applyTheme;
    private readonly TimeProvider _time;
    private readonly Func<byte[], IImage?> _decode;
    private DateTimeOffset _lastInput;
    private RuntimeStatus? _status;
    private PillState _pill;

    public MainViewModel(Strings strings, IRuntimeLink link, IUi ui, TimeProvider time,
        Func<byte[], IImage?> decode, AppTheme theme, Action<AppTheme> applyTheme,
        ISoundService? sound = null, UiPreferences? preferences = null)
    {
        Preferences = preferences ?? new UiPreferences(null);
        _strings = strings;
        _link = link;
        _calls = new RuntimeCalls(link);
        _ui = ui;
        _applyTheme = applyTheme;
        _time = time;
        _decode = decode;
        _lastInput = time.GetUtcNow();
        Card = new CardWedge(time);
        Theme = theme;
        Run = new RunViewModel(strings, _calls, this, time, decode) { Sound = sound ?? SoundService.None };
        // supervisors and above decide bad parts (owner, 2026-10-31), and Support and OEM sort and learn with today's code
        // (AS-04h; WhoAmI may_learn); the runtime checks it again
        Run.MayReview = () => User?.MayLearn ?? User is { Role: not Role.Operator };
        // the PLC test signal buttons: Engineer and Admin (WhoAmI); the runtime checks the call again
        Run.MayTestPlc = () => User?.MayTestPlc == true;
        Run.SignInNeeded += () => User = null;
        _pill = PillState.From(link.State, null);

        link.StateChanged += s => _ui.Post(() => OnLinkState(s));
        link.StatusReceived += s => _ui.Post(() => OnStatus(s));
        link.FrameReceived += f => _ui.Post(() => Run.OnFrame(f));
        link.ResultReceived += r => _ui.Post(() => Run.Parts.Add(r));
        strings.LanguageChanged += () => _ui.Post(OnLanguageChanged);
        OnLinkState(link.State);
    }

    public RunViewModel Run { get; }
    /// <summary>Sounds, spoken alarms and Reduce motion of this PC (requirements 17.15 NXT-04..06).</summary>
    public UiPreferences Preferences { get; }
    /// <summary>The badge reader: the window feeds it every key.</summary>
    public CardWedge Card { get; }
    public AppTheme Theme { get; private set; }

    [ObservableProperty] private object? _overlay;
    [ObservableProperty] private SignedInUser? _user;
    /// <summary>The job designer while it is open (in place of the Run screen); null on the Run screen.</summary>
    [ObservableProperty] private DesignerViewModel? _designer;

    public bool ShowRun => Designer is null;
    /// <summary>The job designer button: an engineer or admin is signed in.</summary>
    public bool MayDesign => User?.MayDesignJobs == true;

    public LinkState LinkState => _link.State;
    public bool HasOverlay => Overlay is not null;

    public string ProductText => _status?.Product?.Name ?? _strings["top.no_product"];
    public string ProductInitial => ProductText.Length > 0 ? ProductText[..1].ToUpperInvariant() : "?";
    public Tone PillTone => _pill.Tone;
    public string PillIcon => _pill.Icon;
    public string PillText => _strings[_pill.TextKey];
    public string UserText => User?.DisplayName ?? _strings["top.operator"];
    public string LanguageGlyph => _strings["lang.glyph"];

    partial void OnOverlayChanged(object? value)
    {
        OnPropertyChanged(nameof(HasOverlay));
        ShortcutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A Run screen shortcut (UI-UX-SPEC.md principle 4): F5 start, Shift+F5 stop, F2 change
    /// product, F3 start batch, F4 parts, F1 help; in the Engineer workspace F5 try, F6 try all, F7 teach,
    /// Ctrl+S save, Page Up / Page Down the pictures, Ctrl+Shift+A the "Areas and values" panel. Only while no panel is open: a tap cannot reach the toolbar behind
    /// a panel either, and a shortcut must never start the line or replace a half-done sign-in or question.</summary>
    [RelayCommand(CanExecute = nameof(MayShortcut))]
    private void Shortcut(string action)
    {
        // In the Engineer workspace the keys run its own actions (UI-UX-SPEC.md 4.19).
        if (Designer is { } designer)
        {
            designer.Shortcut(action == "start" ? "try" : action);
            return;
        }
        var command = action switch
        {
            "start" => Run.StartCommand,
            "stop" => Run.StopCommand,
            "change" => Run.ChangeProductCommand,
            "batch" => Run.BatchCommand,
            "parts" => Run.OpenPartsCommand,
            "help" => (System.Windows.Input.ICommand)Run.HelpCommand,
            _ => null,
        };
        if (command?.CanExecute(null) == true) command.Execute(null);
    }

    private static readonly HashSet<string> RunKeys = ["start", "stop", "change", "batch", "parts", "help"];
    private static readonly HashSet<string> WorkspaceKeys = ["start", "tryall", "teach", "save", "prev", "next", "areas"];

    /// <summary>A key is taken only where it does something, so Page Up and the like still scroll a list on Run.</summary>
    private bool MayShortcut(string? action) =>
        Overlay is null && action is not null && (Designer is null ? RunKeys : WorkspaceKeys).Contains(action);

    partial void OnUserChanged(SignedInUser? value)
    {
        OnPropertyChanged(nameof(UserText));
        OnPropertyChanged(nameof(MayDesign));
        // the designer closes with the engineer who opened it (sign-out, idle, another card)
        if (value?.MayDesignJobs != true) Designer = null;
        Run.UserChanged();
    }

    partial void OnDesignerChanged(DesignerViewModel? value)
    {
        OnPropertyChanged(nameof(ShowRun));
        ShortcutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The job designer (UI-UX-SPEC.md 4.7), in place of the Run screen until it is closed.</summary>
    [RelayCommand]
    private async Task OpenDesigner()
    {
        if (!MayDesign) return;
        var designer = new DesignerViewModel(_strings, _calls, _decode, () => Designer = null) { UserName = User?.DisplayName ?? "" };
        Designer = designer;
        await designer.LoadOpenJobAsync();
    }

    internal void OnLinkState(LinkState state)
    {
        if (state == LinkState.Connected)
        {
            // The runtime may still know this app's session (a dropped pipe) or not (a restart).
            _ = RestoreUserAsync();
        }
        else
        {
            // Not known while the runtime cannot be reached: show Operator, close panels that need it.
            User = null;
            if (state == LinkState.NotConnected) Run.Parts.NoteLinkLost(_time.GetUtcNow());
            // The steps of an alarm seen before the drop may no longer apply; a bad part's picture still does.
            if (Overlay is SignInViewModel or ProductPickerViewModel or ConfirmViewModel or AlarmHelpViewModel or AcceptChangeoverViewModel
                or RemoteRequestViewModel) Overlay = null;
        }
        // The status of a lost connection is not the line's state now; the next connection sends its own.
        var status = state == LinkState.Connected ? _status : null;
        _status = status;
        UpdatePill();
        Run.Apply(state, status);
        OnPropertyChanged(nameof(ProductText));
        OnPropertyChanged(nameof(ProductInitial));
        OnPropertyChanged(nameof(LinkState));
    }

    internal void OnStatus(RuntimeStatus status)
    {
        _status = status;
        UpdatePill();
        OnPropertyChanged(nameof(RemoteActive));
        ShowRemoteRequest(status.Remote?.Requests ?? []);
        Run.Apply(_link.State, status);
        OnPropertyChanged(nameof(ProductText));
        OnPropertyChanged(nameof(ProductInitial));
    }

    /// <summary>The neutral "Remote service active" badge (REQ-11, REQ-08): a session is open; never who.</summary>
    public bool RemoteActive => _status?.Remote?.Active == true;

    /// <summary>
    /// A remote service request waiting at the line PC opens its panel when no other panel is open (one at a time, oldest
    /// first); a panel whose request was answered elsewhere, expired or was withdrawn closes.
    /// </summary>
    private void ShowRemoteRequest(IReadOnlyList<RemoteAsk> requests)
    {
        if (Overlay is RemoteRequestViewModel open && requests.All(r => r.Id != open.Id)) Overlay = null;
        if (Overlay is null && requests.FirstOrDefault() is { } next)
            Show(new RemoteRequestViewModel(_strings, _calls, next, Close));
    }

    /// <summary>A touch, click or key anywhere in the window: the user is still there.</summary>
    public void NoteInput() => _lastInput = _time.GetUtcNow();

    /// <summary>Every 100 ms: the Run screen's timers, and the auto sign-out after the plant's idle
    /// time without a touch or key (UI-UX-SPEC.md 4.5), back to Operator.</summary>
    public void Tick()
    {
        Run.Tick();
        if (Overlay is PartsViewModel parts) parts.Tick();
        Designer?.Watch();
        var idle = _time.GetUtcNow() - _lastInput;
        // a sign-in left open forgets its half-typed PIN or password
        if (Overlay is SignInViewModel open && idle >= SignedInUser.DefaultIdleSignOut) open.BackCommand.Execute(null);
        // an approval left half typed forgets its PIN or password, as a sign-in does
        if (Overlay is RemoteRequestViewModel { IsApproval: true } remote && idle >= SignedInUser.DefaultIdleSignOut) remote.Secret = "";
        if (User is { } user && idle >= (user.IdleSignOut ?? SignedInUser.DefaultIdleSignOut))
        {
            // panels that change the line close with the user who opened them
            if (Overlay is ProductPickerViewModel or ConfirmViewModel) Overlay = null;
            _ = SignOutAsync();
        }
    }

    /// <summary>A badge was read: the card holder signs in at once and replaces whoever was signed in.
    /// A card the runtime does not know (or a barcode scanner's burst) leaves the signed-in user be.</summary>
    public async Task CardReadAsync(string card)
    {
        NoteInput();
        // The second person's badge accepts the changeover; it never signs anybody in.
        if (Overlay is AcceptChangeoverViewModel accept)
        {
            await accept.CardAsync(card);
            return;
        }
        // A supervisor's badge approves a remote correction; it never signs anybody in either.
        if (Overlay is RemoteRequestViewModel { IsApproval: true } remote)
        {
            await remote.CardAsync(card);
            return;
        }
        if (Overlay is not SignInViewModel signIn)
        {
            signIn = NewSignIn();
            Show(signIn);
            await signIn.LoadAsync();
        }
        await signIn.CardAsync(card);
    }

    private async Task SignOutAsync()
    {
        User = null;
        await _calls.TryAsync(async (c, ct) => { await c.SignOutAsync(ct); return true; });
    }

    private SignInViewModel NewSignIn() => new(_strings, _calls, user =>
    {
        NoteInput();
        User = user;
        Close();
    }, Close);

    private async Task RestoreUserAsync()
    {
        var before = User;
        var (reached, user) = await _calls.TryAsync((c, ct) => c.WhoAmIAsync(ct));
        // a sign-in or sign-out meanwhile is newer than this answer
        if (reached) _ui.Post(() => { if (ReferenceEquals(User, before)) User = user; });
    }

    private void UpdatePill()
    {
        _pill = PillState.From(_link.State, _status);
        OnPropertyChanged(nameof(PillTone));
        OnPropertyChanged(nameof(PillIcon));
        OnPropertyChanged(nameof(PillText));
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(string.Empty);
        Run.RefreshTexts();
    }

    public void Show(object overlay) => Overlay = overlay;

    public void Close() => Overlay = null;

    public void CloseIf(object overlay)
    {
        if (ReferenceEquals(Overlay, overlay)) Overlay = null;
    }

    [RelayCommand]
    private async Task UserTap()
    {
        if (User is not null)
        {
            await SignOutAsync();
            return;
        }
        var signIn = NewSignIn();
        Show(signIn);
        await signIn.LoadAsync();
    }

    [RelayCommand]
    private void Display() => Show(new DisplayOptionsViewModel(_strings, Theme, t =>
    {
        Theme = t;
        _applyTheme(t);
    }, Close, Preferences));
}
