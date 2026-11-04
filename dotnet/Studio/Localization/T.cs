using Avalonia;
using Avalonia.Markup.Xaml;

namespace VisionSystem.Studio.Localization;

/// <summary>
/// A translated text in XAML: <c>Text="{l:T run.start}"</c>. Follows language changes.
/// </summary>
public sealed class T(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Strings.Instance.Observe(Key).ToBinding();
}
