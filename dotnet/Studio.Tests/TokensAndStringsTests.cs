using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VisionSystem.Studio.Localization;

namespace VisionSystem.Studio.Tests;

public class TokensTests
{
    private static readonly string Root = Make.RepoRoot();
    private static readonly string[] Themes = ["dark", "light", "highContrast"];

    private static JsonElement Colours() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "design/tokens/vision-tokens.json"))).RootElement.GetProperty("color");

    private static string Colour(string path, string theme)
    {
        var node = Colours();
        foreach (var part in path.Split('.')) node = node.GetProperty(part);
        return node.GetProperty(theme).GetString()!;
    }

    private static double Luminance(string hex)
    {
        var rgb = hex.TrimStart('#');
        if (rgb.Length == 8) rgb = rgb[2..];   // #AARRGGBB
        double Channel(int i)
        {
            var c = int.Parse(rgb.Substring(i * 2, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void Generated_resources_match_the_token_file()
    {
        var xaml = XDocument.Load(Path.Combine(Root, "dotnet/Studio/Theme/Tokens.g.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = new Dictionary<string, string>();
        foreach (var dict in xaml.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(x + "Key") is not null))
        {
            foreach (var c in dict.Elements().Where(e => e.Name.LocalName == "Color"))
            {
                var theme = dict.Attribute(x + "Key")!.Value;
                if (theme.Contains("App.HighContrast")) theme = "HighContrast";
                keys[theme + "/" + c.Attribute(x + "Key")!.Value] = c.Value;
            }
        }
        var count = 0;
        void Walk(JsonElement node, string path)
        {
            if (node.TryGetProperty("dark", out _))
            {
                foreach (var (theme, key) in new[] { ("dark", "Dark"), ("light", "Light"), ("highContrast", "HighContrast") })
                {
                    var name = string.Join('.', path.Split('.').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
                    Assert.True(keys.TryGetValue($"{key}/Vs.Color.{name}", out var value), $"{key}/Vs.Color.{name} missing: run tools/tokens/gen_tokens.py");
                    Assert.Equal(node.GetProperty(theme).GetString(), value);
                    count++;
                }
                return;
            }
            foreach (var p in node.EnumerateObject()) Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name);
        }
        Walk(Colours(), "");
        Assert.Equal(count, keys.Count);
    }

    [Theory]
    [InlineData("dark", 4.5)]
    [InlineData("light", 4.5)]
    [InlineData("highContrast", 7.0)]
    public void Text_meets_the_contrast_rule_of_each_theme(string theme, double minimum)
    {
        foreach (var bg in new[] { "bg", "surface", "surfaceRaised" })
        {
            Assert.True(Contrast(Colour("text", theme), Colour(bg, theme)) >= minimum, $"text on {bg} in {theme}");
        }
        Assert.True(Contrast(Colour("textMuted", theme), Colour("surface", theme)) >= minimum, $"textMuted in {theme}");
        Assert.True(Contrast(Colour("onAccent", theme), Colour("accent", theme)) >= 4.5, $"onAccent in {theme}");
        foreach (var s in new[] { "ok", "ng", "warn", "duplicate", "stopped", "info" })
        {
            // Words on a status colour (pill, result block, banner) are text: 4.5:1 in every theme.
            Assert.True(Contrast(Colour("onStatus." + s, theme), Colour("status." + s, theme)) >= 4.5, $"onStatus.{s} in {theme}");
            // The status colour itself as a shape on the screen: 3:1 for graphics.
            Assert.True(Contrast(Colour("status." + s, theme), Colour("surface", theme)) >= 3.0, $"status.{s} on surface in {theme}");
        }
    }

    [Fact]
    public void Ok_and_NG_differ_in_lightness_not_only_hue()
    {
        foreach (var theme in Themes)
        {
            Assert.True(Contrast(Colour("status.ok", theme), Colour("status.ng", theme)) >= 1.3, theme);
        }
    }

    [Fact]
    public void No_colour_is_written_outside_the_generated_tokens()
    {
        var hex = new Regex("#[0-9A-Fa-f]{3,8}\\b");
        var named = new Regex("\"(Red|Green|Yellow|Orange|Blue|White|Black|Gray|Grey)\"");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "dotnet/Studio"), "*.axaml", SearchOption.AllDirectories))
        {
            if (file.EndsWith("Tokens.g.axaml", StringComparison.Ordinal) || file.Contains("/obj/") || file.Contains("/bin/")) continue;
            var text = File.ReadAllText(file);
            Assert.False(hex.IsMatch(text), $"{Path.GetFileName(file)} has a colour: {hex.Match(text).Value}");
            Assert.False(named.IsMatch(text), $"{Path.GetFileName(file)} has a named colour");
        }
    }
}

public class StringsTests
{
    private static readonly string Studio = Path.Combine(Make.RepoRoot(), "dotnet/Studio");

    private static IReadOnlyDictionary<string, string> English() =>
        Strings.Parse(File.OpenRead(Path.Combine(Studio, "i18n/en.json")));

    /// <summary>Every key the code and views use, found by the patterns they are written in.</summary>
    private static IEnumerable<string> UsedKeys()
    {
        var patterns = new[]
        {
            new Regex("\\{l:T ([a-z_.]+)\\}"),
            new Regex("\"((?:status|result|banner|run|help|product|display|signin|top|common|app|lang|ng|alarm)\\.[a-z_.]+)\""),
        };
        foreach (var file in Directory.EnumerateFiles(Studio, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".axaml")) && !f.Contains("/obj/") && !f.Contains("/bin/")
                         && !f.Contains("/Runtime/")))   // the contract adapter names colour tokens, not strings
        {
            var text = File.ReadAllText(file);
            foreach (var p in patterns)
                foreach (Match m in p.Matches(text))
                    yield return m.Groups[1].Value;
        }
    }

    [Fact]
    public void Every_key_used_is_in_the_English_table()
    {
        var en = English();
        var used = UsedKeys().Distinct().ToList();
        Assert.True(used.Count > 50, $"only {used.Count} keys found: the scan is broken");
        Assert.DoesNotContain(used, k => !en.ContainsKey(k));
    }

    [Fact]
    public void Every_shipped_language_has_the_same_placeholders_as_English()
    {
        var en = English();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Studio, "i18n"), "*.json"))
        {
            foreach (var (key, text) in Strings.Parse(File.OpenRead(file)))
            {
                Assert.True(en.ContainsKey(key), $"{Path.GetFileName(file)}: {key} not in English");
                var holes = new Regex("\\{\\d\\}");
                Assert.Equal(holes.Matches(en[key]).Select(m => m.Value).Order(), holes.Matches(text).Select(m => m.Value).Order());
            }
        }
    }

    [Fact]
    public void Missing_keys_fall_back_to_English_then_to_the_key()
    {
        var strings = new Strings(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string> { ["a"] = "Apple", ["b"] = "Ball" },
            ["hi"] = new Dictionary<string, string> { ["a"] = "सेब" },
        });
        var changes = 0;
        strings.LanguageChanged += () => changes++;
        strings.SetLanguage("hi");
        Assert.Equal(("सेब", "Ball", "c"), (strings["a"], strings["b"], strings["c"]));
        Assert.Equal(1, changes);
        strings.SetLanguage("xx");   // not shipped: unchanged
        Assert.Equal("hi", strings.Language);
        Assert.Equal("Ball", strings.KeyOrText("b", "runtime text", "a"));
        Assert.Equal("runtime text", strings.KeyOrText("zz", "runtime text", "a"));
        Assert.Equal("सेब", strings.KeyOrText(null, null, "a"));
    }

    [Fact]
    public void Observed_texts_follow_language_changes()
    {
        var strings = new Strings(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string> { ["run.start"] = "Start" },
            ["hi"] = new Dictionary<string, string> { ["run.start"] = "शुरू" },
        });
        var seen = new List<string>();
        using (strings.Observe("run.start").Subscribe(new Observer(seen.Add)))
        {
            strings.SetLanguage("hi");
        }
        strings.SetLanguage("en");
        Assert.Equal(["Start", "शुरू"], seen);
    }

    private sealed class Observer(Action<string> next) : IObserver<string>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(string value) => next(value);
    }
}
