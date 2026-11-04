using System.Reflection;
using System.Text.Json;

namespace VisionSystem.Studio.Localization;

/// <summary>
/// The string table: one flat JSON file per language in i18n/ (keys like "run.start", the
/// style of dotnet/VisionStudio/i18n/en.json), embedded in the app. A key missing in a
/// language falls back to English; a key missing in English shows as the key, so it is seen.
/// Changing the language takes effect at once, without restart (UI-UX-SPEC.md 2).
/// </summary>
public sealed class Strings
{
    public const string English = "en";

    public static Strings Instance { get; } = new(LoadEmbedded());

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _tables;
    private IReadOnlyDictionary<string, string> _current;

    public Strings(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tables)
    {
        _tables = tables;
        _current = tables.GetValueOrDefault(English) ?? new Dictionary<string, string>();
        Language = English;
    }

    public string Language { get; private set; }

    /// <summary>Language tags shipped, English first.</summary>
    public IReadOnlyList<string> Languages =>
        _tables.Keys.OrderBy(k => k == English ? "" : k, StringComparer.Ordinal).ToArray();

    public event Action? LanguageChanged;

    public string this[string key] =>
        _current.TryGetValue(key, out var text) ? text
        : _tables.TryGetValue(English, out var en) && en.TryGetValue(key, out var fallback) ? fallback
        : key;

    public bool Has(string key) => _current.ContainsKey(key) || (_tables.TryGetValue(English, out var en) && en.ContainsKey(key));

    /// <summary>Our text for the key when we have one, else the runtime's own text, else a general one.</summary>
    public string KeyOrText(string? key, string? text, string fallbackKey) =>
        key is not null && Has(key) ? this[key] : text is { Length: > 0 } ? text : this[fallbackKey];

    /// <summary>The text with {0}, {1} ... filled in.</summary>
    public string Format(string key, params object[] args) => string.Format(this[key], args);

    /// <summary>The language's own name, for the picker.</summary>
    public string NativeName(string language) =>
        _tables.TryGetValue(language, out var t) && t.TryGetValue("lang.name", out var name) ? name : language;

    public void SetLanguage(string language)
    {
        if (!_tables.TryGetValue(language, out var table) || language == Language) return;
        _current = table;
        Language = language;
        LanguageChanged?.Invoke();
    }

    /// <summary>The text of a key now and after every language change.</summary>
    public IObservable<string> Observe(string key) => new KeyObservable(this, key);

    public static IReadOnlyDictionary<string, string> Parse(Stream json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("i18n.", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            tables[name["i18n.".Length..^".json".Length]] = Parse(stream);
        }
        return tables;
    }

    private sealed class KeyObservable(Strings strings, string key) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            void Push() => observer.OnNext(strings[key]);
            strings.LanguageChanged += Push;
            Push();
            return new Unsubscribe(() => strings.LanguageChanged -= Push);
        }
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
