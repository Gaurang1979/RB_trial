using System.Globalization;
using VisionCore;

namespace VisionStudio.Services;

/// <summary>The Read text tool's month-name languages (TextToolConfig.MonthLanguages) as ticked on the Teach page.</summary>
public static class MonthLanguages
{
    /// <summary>Ticked; an empty list reads English month names, so English shows ticked then.</summary>
    public static bool Has(TextToolConfig text, string code) =>
        text.MonthLanguages.Contains(code, StringComparer.OrdinalIgnoreCase)
        || (text.MonthLanguages.Count == 0 && code.Equals("en", StringComparison.OrdinalIgnoreCase));

    /// <summary>Ticks or unticks a language; unticking the last one leaves English.</summary>
    public static void Set(TextToolConfig text, string code, bool on)
    {
        text.MonthLanguages.RemoveAll(l => l.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (on) text.MonthLanguages.Add(code);
        if (text.MonthLanguages.Count == 0) text.MonthLanguages.Add("en");
    }

    /// <summary>The tool's languages that have month names, for Teach: an unknown code from a job file is left
    /// out here (the setup check reports it) so Auto find still proposes formats.</summary>
    public static List<string> Known(TextToolConfig text) =>
        TextTeach.KnownMonthLanguages(text);

    /// <summary>The language's own name (Español, Deutsch), so it needs no translation; the code when unknown.</summary>
    public static string Name(string code)
    {
        try
        {
            var n = new CultureInfo(code).NativeName;
            return n.Length > 0 ? char.ToUpper(n[0], CultureInfo.InvariantCulture) + n[1..] : code;
        }
        catch (CultureNotFoundException) { return code; }
    }
}
