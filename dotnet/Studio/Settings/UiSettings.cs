using System.Text.Json;
using System.Text.Json.Serialization;
using VisionSystem.Studio.Sound;

namespace VisionSystem.Studio.Settings;

/// <summary>This PC's look-and-feel settings: sounds, spoken alarms and Reduce motion (requirements
/// 17.15 NXT-04..06). They only change what is seen and heard, never a result, so they are kept by
/// Vision Studio on this PC, not in the job or the runtime.</summary>
public sealed record UiSettings
{
    public SoundSettings Sound { get; init; } = SoundSettings.Default;

    /// <summary>Turns off non-essential animation (glows, sweeps, pulses). The OS setting turns it off too.</summary>
    public bool ReduceMotion { get; init; }

    public static UiSettings Default { get; } = new();
}

/// <summary>Reads and writes <see cref="UiSettings"/> as a small JSON file. A missing or damaged file
/// gives the defaults; a file that cannot be written keeps the settings for this run only.</summary>
public sealed class UiSettingsStore
{
    private readonly string _path;

    public UiSettingsStore(string path) => _path = path;

    /// <summary>%LOCALAPPDATA%\VisionStudio\ui-settings.json (on Windows), or the platform's own place.</summary>
    public static UiSettingsStore ForThisUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", "ui-settings.json"));

    public UiSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return UiSettings.Default;
            var file = JsonSerializer.Deserialize<FileForm>(File.ReadAllText(_path), Json);
            return file is null ? UiSettings.Default : file.ToSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return UiSettings.Default;
        }
    }

    /// <summary>Writes the settings; false when the file could not be written.</summary>
    public bool Save(UiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(FileForm.From(settings), Json));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The file keeps each sound by name with on or off, so a sound added later starts at its default.</summary>
    private sealed record FileForm
    {
        public double Volume { get; init; } = SoundSettings.Default.Volume;
        public Dictionary<string, bool> Sounds { get; init; } = [];
        public bool SpokenAlarms { get; init; } = SoundSettings.Default.SpokenAlarms;
        public bool ReduceMotion { get; init; }

        public UiSettings ToSettings()
        {
            var sound = new SoundSettings { Volume = Math.Clamp(Volume, 0, 1), SpokenAlarms = SpokenAlarms };
            foreach (var (name, on) in Sounds)
            {
                if (Enum.TryParse<SoundKind>(name, ignoreCase: true, out var kind)) sound = sound.With(kind, on);
            }
            return new UiSettings { Sound = sound, ReduceMotion = ReduceMotion };
        }

        public static FileForm From(UiSettings s) => new()
        {
            Volume = s.Sound.Volume,
            Sounds = Enum.GetValues<SoundKind>().ToDictionary(k => JsonNamingPolicy.CamelCase.ConvertName(k.ToString()), k => s.Sound.IsOn(k)),
            SpokenAlarms = s.Sound.SpokenAlarms,
            ReduceMotion = s.ReduceMotion,
        };
    }
}

/// <summary>The settings of this run: read once at start, saved on every change, and told to whoever
/// shows them (the sound service reads them on each sound, the window on each change).</summary>
public sealed class UiPreferences
{
    private readonly UiSettingsStore? _store;

    public UiPreferences(UiSettingsStore? store)
    {
        _store = store;
        Settings = store?.Load() ?? UiSettings.Default;
    }

    public UiSettings Settings { get; private set; }

    public event Action? Changed;

    public void Update(Func<UiSettings, UiSettings> change)
    {
        Settings = change(Settings);
        _store?.Save(Settings);
        Changed?.Invoke();
    }
}
