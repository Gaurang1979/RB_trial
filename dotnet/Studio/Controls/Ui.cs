using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Controls;

/// <summary>
/// <c>ui:Ui.Tone="{Binding ResultTone}"</c> gives the control the class tone-ok, tone-ng, ...
/// so Theme/Controls.axaml can colour it from the tokens, and colours follow theme changes.
/// </summary>
public static class Ui
{
    public static readonly AttachedProperty<Tone> ToneProperty =
        AvaloniaProperty.RegisterAttached<StyledElement, Tone>("Tone", typeof(Ui), (Tone)(-1));

    static Ui()
    {
        ToneProperty.Changed.AddClassHandler<StyledElement>((element, e) =>
        {
            foreach (var tone in Enum.GetValues<Tone>()) element.Classes.Remove(ClassFor(tone));
            element.Classes.Add(ClassFor((Tone)e.NewValue!));
        });
    }

    public static string ClassFor(Tone tone) => "tone-" + tone.ToString().ToLowerInvariant();

    public static Tone GetTone(StyledElement element) => element.GetValue(ToneProperty);
    public static void SetTone(StyledElement element, Tone value) => element.SetValue(ToneProperty, value);
}

/// <summary>Turns an Icon.* key (see <see cref="Icons"/>) into its geometry from Theme/Icons.axaml.</summary>
public sealed class IconConverter : IValueConverter
{
    public static readonly IconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryGetResource(key, null, out var geometry) == true
            ? geometry as Geometry
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A tree row's depth in pixels to its left margin.</summary>
public sealed class IndentConverter : IValueConverter
{
    public static readonly IndentConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        new Avalonia.Thickness(value is double d ? d : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
