using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.Settings;
using VisionSystem.Studio.Sound;

namespace VisionSystem.Studio.ViewModels;

/// <summary>One question with two answers, e.g. "Stop checking parts?".</summary>
public sealed partial class ConfirmViewModel(Strings strings, string questionKey, string yesKey, string noKey,
    Func<Task> yes, Action no) : ObservableObject
{
    public string Question => strings[questionKey];
    public string YesText => strings[yesKey];
    public string NoText => strings[noKey];

    [RelayCommand]
    private Task Yes() => yes();

    [RelayCommand]
    private void No() => no();
}

/// <summary>A title and a short text with one "Close" button (Help).</summary>
public sealed partial class MessageViewModel(Strings strings, string titleKey, string bodyKey, Action close) : ObservableObject
{
    public string Title => strings[titleKey];
    public string Body => strings[bodyKey];

    [RelayCommand]
    private void Close() => close();
}

public sealed partial class ProductTile(ProductInfo product, bool current) : ObservableObject
{
    public ProductInfo Product { get; } = product;
    public string Name => Product.Name;
    public string Initial => Product.Name.Length > 0 ? Product.Name[..1].ToUpperInvariant() : "?";
    public bool IsCurrent { get; } = current;
    /// <summary>The product's picture from the runtime; the initial letter shows until it comes, or without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    private IImage? _picture;
    public bool HasPicture => Picture is not null;
}

/// <summary>
/// Change product (a first form of UI-UX-SPEC.md 4.2): the products from the runtime as large
/// tiles with their pictures (JobInfo.picture_id, from GetImage); a tap asks the runtime to change.
/// Pictures are kept in <paramref name="pictures"/> so the next opening shows them at once.
/// </summary>
public sealed partial class ProductPickerViewModel(Strings strings, RuntimeCalls calls, ProductRef? current,
    IOverlayHost overlays, Action<string, object[]> notice, Func<byte[], IImage?>? decode = null,
    Dictionary<string, IImage?>? pictures = null) : ObservableObject
{
    public ObservableCollection<ProductTile> Products { get; } = [];

    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _message = "";

    public string Title => strings["product.title"];

    public async Task LoadAsync()
    {
        var (reached, list) = await calls.TryAsync((c, ct) => c.ListProductsAsync(ct));
        Busy = false;
        if (!reached || list is null)
        {
            Message = strings[reached ? "run.reason.refused" : "run.reason.not_connected"];
            return;
        }
        foreach (var p in list) Products.Add(new ProductTile(p, current is not null && p.Job == current.Job && p.Variant == current.Variant));
        if (Products.Count == 0) Message = strings["product.none"];
        await LoadPicturesAsync();
    }

    /// <summary>One picture at a time, shared by the variants of a job; a picture that fails is not asked again.</summary>
    private async Task LoadPicturesAsync()
    {
        if (decode is null) return;
        pictures ??= [];
        foreach (var tile in Products.ToList())
        {
            var id = tile.Product.PictureId;
            if (id.Length == 0) continue;
            if (!pictures.TryGetValue(id, out var picture))
            {
                var (reached, image) = await calls.TryAsync((c, ct) => c.GetImageAsync(id, ct));
                if (!reached) return;   // the link dropped: the letters stay
                picture = image is null ? null : decode(image.Data);
                pictures[id] = picture;
            }
            tile.Picture = picture;
        }
    }

    [RelayCommand]
    private async Task Pick(ProductTile tile)
    {
        if (Busy) return;
        if (tile.IsCurrent)
        {
            overlays.CloseIf(this);
            return;
        }
        Busy = true;
        var (reached, result) = await calls.TryAsync((c, ct) => c.ChangeProductAsync(tile.Product, ct));
        Busy = false;
        if (!reached) Message = strings["run.reason.not_connected"];
        else if (result is null) Message = strings["run.reason.refused"];
        else if (!result.Ok) Message = strings.KeyOrText(result.ReasonKey, result.ReasonText, "run.reason.refused");
        else
        {
            overlays.CloseIf(this);
            notice("product.changed", [tile.Name]);
        }
    }

    [RelayCommand]
    private void Back() => overlays.Close();
}

public sealed partial class Choice(string id, string text, bool selected) : ObservableObject
{
    public string Id { get; } = id;
    public string Text { get; } = text;
    [ObservableProperty] private bool _selected = selected;
}

/// <summary>Language, colours, sounds and motion of this application (not of the line).</summary>
public sealed partial class DisplayOptionsViewModel : ObservableObject
{
    private readonly Strings _strings;
    private readonly Action<AppTheme> _setTheme;
    private readonly Action _close;
    private readonly UiPreferences _preferences;

    /// <summary>The sounds in the order the panel lists them, with their label keys.</summary>
    public static readonly (SoundKind Kind, string Key)[] SoundRows =
    [
        (SoundKind.BadPart, "display.sound.bad_part"),
        (SoundKind.Alarm, "display.sound.alarm"),
        (SoundKind.Start, "display.sound.start"),
        (SoundKind.Stop, "display.sound.stop"),
        (SoundKind.Saved, "display.sound.saved"),
        (SoundKind.GoodPart, "display.sound.good_part"),
        (SoundKind.Tap, "display.sound.tap"),
        (SoundKind.Hover, "display.sound.hover"),
    ];

    public DisplayOptionsViewModel(Strings strings, AppTheme theme, Action<AppTheme> setTheme, Action close,
        UiPreferences? preferences = null)
    {
        _strings = strings;
        _setTheme = setTheme;
        _close = close;
        _preferences = preferences ?? new UiPreferences(null);
        foreach (var lang in strings.Languages)
        {
            Languages.Add(new Choice(lang, strings.NativeName(lang), lang == strings.Language));
        }
        foreach (var (id, key) in new[] { (AppTheme.Dark, "display.theme.dark"), (AppTheme.Light, "display.theme.light"), (AppTheme.HighContrast, "display.theme.high_contrast") })
        {
            Themes.Add(new Choice(id.ToString(), strings[key], id == theme));
        }
        var s = _preferences.Settings;
        foreach (var (kind, key) in SoundRows)
        {
            var toggle = new SoundToggle(kind, strings[key], s.Sound.IsOn(kind));
            toggle.PropertyChanged += (_, _) => _preferences.Update(u => u with { Sound = u.Sound.With(toggle.Kind, toggle.On) });
            Sounds.Add(toggle);
        }
        _volume = Math.Round(s.Sound.Volume * 100);
        _spokenAlarms = s.Sound.SpokenAlarms;
        _reduceMotion = s.ReduceMotion;
    }

    public ObservableCollection<Choice> Languages { get; } = [];
    public ObservableCollection<Choice> Themes { get; } = [];
    public ObservableCollection<SoundToggle> Sounds { get; } = [];
    public string Title => _strings["display.title"];
    public string LanguageLabel => _strings["display.language"];
    public string ThemeLabel => _strings["display.colours"];

    /// <summary>Master volume in % (0 is silent).</summary>
    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _spokenAlarms;
    [ObservableProperty] private bool _reduceMotion;

    partial void OnVolumeChanged(double value) => _preferences.Update(u => u with { Sound = u.Sound with { Volume = Math.Clamp(value, 0, 100) / 100 } });
    partial void OnSpokenAlarmsChanged(bool value) => _preferences.Update(u => u with { Sound = u.Sound with { SpokenAlarms = value } });
    partial void OnReduceMotionChanged(bool value) => _preferences.Update(u => u with { ReduceMotion = value });

    [RelayCommand]
    private void PickLanguage(Choice choice)
    {
        foreach (var c in Languages) c.Selected = c == choice;
        _strings.SetLanguage(choice.Id);
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void PickTheme(Choice choice)
    {
        foreach (var c in Themes) c.Selected = c == choice;
        _setTheme(Enum.Parse<AppTheme>(choice.Id));
    }

    [RelayCommand]
    private void Close() => _close();
}

/// <summary>One sound with its on/off switch in the Display panel.</summary>
public sealed partial class SoundToggle(SoundKind kind, string text, bool on) : ObservableObject
{
    public SoundKind Kind { get; } = kind;
    public string Text { get; } = text;
    [ObservableProperty] private bool _on = on;
}
