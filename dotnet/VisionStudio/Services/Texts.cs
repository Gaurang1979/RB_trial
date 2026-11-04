using System.Text.Json;

namespace VisionStudio.Services;

/// <summary>
/// UI text by key (docs/vision-system/LOCALISATION.md: screen.area.item). Strings live in
/// i18n/&lt;language&gt;.json; a key missing in the language falls back to English, then to the key.
/// </summary>
public sealed class Texts
{
    private readonly Dictionary<string, string> _lang, _en;

    public Texts(string folder, string language)
    {
        _en = Load(Path.Combine(folder, "en.json"));
        _lang = language == "en" ? _en : Load(Path.Combine(folder, language + ".json"));
        Language = language;
    }

    public string Language { get; }

    public string this[string key] => _lang.TryGetValue(key, out var s) ? s : _en.TryGetValue(key, out var e) ? e : key;

    public string Format(string key, params object[] args) => string.Format(this[key], args);

    private static Dictionary<string, string> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
            : new();
}
