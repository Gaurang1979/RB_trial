namespace VisionStudio.Services;

/// <summary>
/// Ready-made sets for the Teach page's "Must have AIs" field (Req 17.13 GS1-PH-01). A preset only fills the
/// existing required-AIs setting; the engineer can still edit it.
/// </summary>
public static class Gs1RequiredPresets
{
    /// <summary>Pharmaceutical serialisation: GTIN, expiry date, batch, serial number.</summary>
    public static readonly IReadOnlyList<string> Pharma = ["01", "17", "10", "21"];

    /// <summary>The AIs as the field shows them, space separated.</summary>
    public static string Format(IEnumerable<string> ais) => string.Join(" ", ais);

    /// <summary>True when <paramref name="required"/> is the pharma set, in any order.</summary>
    public static bool IsPharma(IEnumerable<string> required) =>
        required.Distinct().Order().SequenceEqual(Pharma.Order());
}
