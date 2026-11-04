using Avalonia;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio;

public sealed record StudioOptions
{
    public AppTheme Theme { get; init; } = AppTheme.Dark;
    public string? Language { get; init; }
    public bool FullScreen { get; init; }

    public const string Usage = """
        vs-studio [options]
          --theme dark|light|high-contrast   colours (default dark)
          --lang <tag>                       language (default en)
          --fullscreen                       full screen, for the line PC
        The runtime is reached on \\.\pipe\vs-runtime (Windows) or $TMPDIR/vs-runtime.sock;
        set VS_RUNTIME_ENDPOINT to another socket path or pipe:<name>.
        """;

    public static StudioOptions Parse(IReadOnlyList<string> args)
    {
        var options = new StudioOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--theme":
                    options = options with
                    {
                        Theme = Next() switch
                        {
                            "light" => AppTheme.Light,
                            "high-contrast" or "hc" => AppTheme.HighContrast,
                            "dark" => AppTheme.Dark,
                            var other => throw new ArgumentException($"unknown theme {other}"),
                        },
                    };
                    break;
                case "--lang": options = options with { Language = Next() }; break;
                case "--fullscreen": options = options with { FullScreen = true }; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        return options;
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            App.Options = StudioOptions.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(StudioOptions.Usage);
            return 2;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
