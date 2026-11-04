namespace VisionStudio.Components.Shared;

/// <summary>How a value reads: colour and shape (✓ ! ✗) of the chart pieces that show it.</summary>
public enum ChartLevel { None, Ok, Warn, Ng }

/// <summary>One bar of <see cref="Bars"/>.</summary>
public readonly record struct BarItem(string Label, long Value);

/// <summary>Number helpers the chart pieces share.</summary>
internal static class ChartNum
{
    public static bool Good(double? v) => v is { } d && double.IsFinite(d);

    /// <summary>The value as text with the unit, or "–" when it is missing or not a number.</summary>
    public static string Text(double? v, int decimals, string unit) =>
        Good(v) ? v!.Value.ToString("F" + decimals) + unit : "–";

    public static string Symbol(ChartLevel level) => level switch
    {
        ChartLevel.Ok => "✓", ChartLevel.Warn => "!", ChartLevel.Ng => "✗", _ => "",
    };

    public static string Css(ChartLevel level) => level switch
    {
        ChartLevel.Ok => "vc-ok", ChartLevel.Warn => "vc-warn", ChartLevel.Ng => "vc-ng", _ => "vc-none",
    };
}
