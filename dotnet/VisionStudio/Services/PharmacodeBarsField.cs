using BarcodeReading;
using VisionCore;

namespace VisionStudio.Services;

/// <summary>
/// The Teach page's "bars" field for a Pharmacode: its length lock counts bars, and the engine needs both bounds
/// (a lock with only a top lets a missing end bar read as a shorter code). One number sets both; teaching may
/// set a range when the codes it found differ, which the field shows until the engineer types a number.
/// </summary>
public static class PharmacodeBarsField
{
    public const int Fewest = 2, Most = 16;

    /// <summary>The bar count when the lock is one number; null when unset or a range.</summary>
    public static int? Get(CodeToolConfig c) => c.MinLength > 0 && c.MinLength == c.MaxLength ? c.MinLength : null;

    /// <summary>The lock as taught when it is a range ("5–7"); null otherwise.</summary>
    public static string? Range(CodeToolConfig c) => c.MinLength > 0 && c.MaxLength > c.MinLength ? $"{c.MinLength}–{c.MaxLength}" : null;

    /// <summary>Sets both bounds to <paramref name="bars"/>; empty or out of 2-16 clears the lock (a setup error, never "any").</summary>
    public static void Set(CodeToolConfig c, int? bars)
    {
        var n = bars is >= Fewest and <= Most ? bars.Value : 0;
        c.MinLength = n;
        c.MaxLength = n;
    }
}
