using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.Settings;
using VisionSystem.Studio.Sound;
using VisionSystem.Studio.ViewModels;
using VisionSystem.Studio.Views;

namespace VisionSystem.Studio;

public sealed class App : Application
{
    /// <summary>High contrast falls back to the Dark Fluent resources for anything the tokens do not set.</summary>
    public static readonly ThemeVariant HighContrast = new("HighContrast", ThemeVariant.Dark);

    private RuntimeLink? _link;
    private DispatcherTimer? _tick;

    public static StudioOptions Options { get; set; } = new();

    /// <summary>Tests render views headless with their own view-models: no runtime link, no window.</summary>
    public static bool Headless { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public static ThemeVariant VariantFor(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.HighContrast => HighContrast,
        _ => ThemeVariant.Dark,
    };

    /// <summary>Reduce motion on a window: the setting or the operating system (requirements 17.15 NXT-06).</summary>
    public static void ApplyMotion(Window window, UiSettings settings, Func<bool>? osAsks = null) =>
        window.Classes.Set(Motion.ReduceClass, Motion.Reduced(settings.ReduceMotion, osAsks));

    public static IImage? Decode(byte[] jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var options = Options;
        RequestedThemeVariant = VariantFor(options.Theme);
        if (options.Language is { } lang) Strings.Instance.SetLanguage(lang);
        if (Headless)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // The runtime service (or the stub runtime on a Mac) over the local pipe or socket.
        _link = new RuntimeLink(GrpcRuntimeConnector.ForDefaultEndpoint(() => Strings.Instance.Language));

        // Sounds and spoken alarms (requirements 17.15 NXT-04, NXT-05), read from this PC's settings.
        var preferences = new UiPreferences(UiSettingsStore.ForThisUser());
        var sound = new SoundService(SoundPlatform.Output(), SoundPlatform.Speech(), () => preferences.Settings.Sound);
        SoundCues.Attach(sound);

        var vm = new MainViewModel(Strings.Instance, _link, new DispatcherUi(), TimeProvider.System, Decode,
            options.Theme, t => RequestedThemeVariant = VariantFor(t), sound, preferences);
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => vm.Tick());
        _tick.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = vm };
            ApplyMotion(window, preferences.Settings);
            preferences.Changed += () => ApplyMotion(window, preferences.Settings);
            if (options.FullScreen) window.WindowState = WindowState.FullScreen;
            desktop.MainWindow = window;
            // Closing the app only closes the view; the runtime keeps inspecting (principle 13).
            desktop.ShutdownRequested += async (_, _) =>
            {
                _tick.Stop();
                await _link.DisposeAsync();
            };
        }
        _link.Start();
        base.OnFrameworkInitializationCompleted();
    }

    private sealed class DispatcherUi : IUi
    {
        public void Post(Action action) => Dispatcher.UIThread.Post(action);
    }
}
