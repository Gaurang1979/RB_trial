using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BarcodeReading;

/// <summary>What a GS1 code's data must hold (tool specification 6.4, "Check code data").</summary>
public sealed class Gs1Rules
{
    /// <summary>AIs the code must carry, e.g. 01, 10, 17, 21.</summary>
    public List<string> Required { get; set; } = new();
    /// <summary>The expiry (17) must not be before the picture's date. An expired code is never good.</summary>
    public bool ExpiryNotPast { get; set; } = true;
}

/// <summary>One problem found in a code's GS1 data: a sentence for the operator and its explain key.</summary>
public sealed record Gs1Problem(string Message, string Explain);

/// <summary>A code's GS1 data: its elements in order, and every problem found.</summary>
public sealed record Gs1Result(IReadOnlyList<(string Ai, string Value)> Elements, IReadOnlyList<Gs1Problem> Problems)
{
    public bool Valid => Problems.Count == 0;
    public string? Get(string ai) => Elements.FirstOrDefault(e => e.Ai == ai).Value;
}

/// <summary>
/// GS1 element strings checked against the GS1 Barcode Syntax Dictionary (every AI of the GS1 General
/// Specifications; the table is in Gs1Syntax.cs): each AI known, each component of its value of the right
/// type and length, a variable-length value ended by a separator (FNC1, sent as ASCII 29) unless last,
/// the components' checks (check digits, real dates and times, country and currency codes, ...), expiry
/// not before production, and the job's own rules; the rules between AIs and Digital Link's structure are
/// in Gs1Associations.cs. The raw form and GS1 Digital Link URLs are read; the human-readable form
/// "(01)...(17)..." only from typed or OCR text (in a symbol it is a print defect). Problems are listed, never repaired: a code with any problem fails.
/// </summary>
public static partial class Gs1Check
{
    private const char GS = '\u001D';
    // symbology identifiers of GS1 data: GS1-128, DataBar, GS1 DataMatrix, GS1 QR, GS1 DotCode
    private static readonly HashSet<string> GsIds = new() { "]C1", "]e0", "]d2", "]Q3", "]J1" };

    /// <summary>True for a symbology identifier of GS1 data (FNC1 first, or DataBar).</summary>
    public static bool IsGs1Id(string id) => GsIds.Contains(id);

    // built on first use, after the generated dictionary's fields (static fields of a partial class
    // across files have no set order)
    private static class Tables
    {
        public static readonly Dictionary<string, Ai> Ais = BuildTable();
        public static readonly (string Ai, string[][] AnyOf)[] Requires = BuildRequires();
        public static readonly (string Ai, string[] Patterns)[] Excludes = BuildExcludes();
        public static readonly Dictionary<string, string[][]> LinkKeys = BuildLinkKeys();
    }

    private static Dictionary<string, Ai> Table => Tables.Ais;

    /// <summary>The name of an AI for messages ("GTIN", "expiry date"), or the AI itself.</summary>
    public static string NameOf(string ai) => Table.TryGetValue(ai, out var a) ? a.Name : $"AI {ai}";

    /// <summary>True for an AI this check knows.</summary>
    public static bool Known(string ai) => Table.ContainsKey(ai);

    /// <summary>The length of an AI's value when it needs no separator after it (predefined length), else -1.</summary>
    public static int PredefinedLength(string ai) => Table.TryGetValue(ai, out var a) && a.Fixed ? a.Max : -1;

    /// <summary>Parses and checks a decoded text; <paramref name="now"/> is the picture's time.</summary>
    /// <remarks>
    /// <paramref name="decoded"/> = the text a code reader decoded from a symbol (the default): the
    /// human-readable form "(01)..." is then a print defect (the label's text line encoded into the
    /// symbol, which GS1 scanners reject), and a symbology identifier must be a GS1 one. Typed or
    /// read text (OCR) may use the human-readable form.
    /// </remarks>
    public static Gs1Result Check(string text, Gs1Rules rules, DateTime now, bool decoded = true)
    {
        var elements = new List<(string, string)>();
        var problems = new List<Gs1Problem>();
        if (decoded && text.StartsWith('('))
        {
            problems.Add(new("Code data is not GS1: the code holds the printed text with brackets \"(01)...\" instead of GS1 data. Check the printer setup", "explain.gs1.syntax"));
            return new Gs1Result(elements, problems);
        }
        if (text.StartsWith(']') && !(text.Length > 3 && GsIds.Contains(text[..3])))
        {
            problems.Add(new($"Code data is not GS1: symbology identifier {text[..Math.Min(3, text.Length)]}", "explain.gs1.syntax"));
            return new Gs1Result(elements, problems);
        }
        string? why = text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? ParseDigitalLink(text, elements)
            : text.StartsWith('(') ? ParseHri(text, elements)
            : ParseRaw(text.StartsWith(']') ? text[3..] : text, elements);
        if (why is not null)
        {
            problems.Add(new($"Code data is not GS1: {why}", "explain.gs1.syntax"));
            return new Gs1Result(elements, problems);
        }

        var seen = new HashSet<string>();
        foreach (var (ai, value) in elements)
        {
            if (!seen.Add(ai)) problems.Add(new($"The {NameOf(ai)} ({ai}) is in the code twice", "explain.gs1.syntax"));
            if (CheckValue(ai, value, now) is { } problem) problems.Add(problem);
        }

        CheckAssociations(elements, problems);

        foreach (var ai in rules.Required)
            if (!seen.Contains(ai)) problems.Add(new($"Code has no {NameOf(ai)} ({ai})", "explain.gs1.missing_ai"));

        // a production date with day 00 is read as the month's first day, an expiry as its last (the
        // cautious readings); an expiry with a time (7003) is checked to the minute
        var made = Date(ValueOf(elements, "11"), true, now, monthStart: true);
        foreach (var ai in new[] { "17", "7003" })
        {
            var v = ValueOf(elements, ai);
            if (Date(v, true, now) is not { } e) continue;
            if (made is { } m && e < m)
                problems.Add(new($"Expiry {e:dd/MM/yy} is before production {m:dd/MM/yy}", "explain.gs1.bad_date"));
            bool past = ai == "7003" ? DateTimeOf(v!, e) < now : e < DateOnly.FromDateTime(now);
            if (rules.ExpiryNotPast && past)
                problems.Add(new($"Expiry {(ai == "7003" ? DateTimeOf(v!, e).ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture) : e.ToString("dd/MM/yy", CultureInfo.InvariantCulture))} is already past", "explain.gs1.bad_date"));
        }
        return new Gs1Result(elements, problems);
    }

    private static string? ValueOf(List<(string Ai, string Value)> e, string ai) => e.FirstOrDefault(x => x.Ai == ai).Value;

    private static string Show(char c) => c < ' ' || c == 127 ? $"code {(int)c}" : $"\"{c}\"";

    // the GS1 AI encodable character set 82
    private static bool Cset82(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!\"%&'()*+,-./:;<=>?_".Contains(c);

    /// <summary>A GS1 date YYMMDD (YYMMDDHHMM for date and time); the century is the one within -49..+50
    /// years of <paramref name="now"/>; day 00 is the month's last day where allowed. Null when not a real date.</summary>
    public static DateOnly? Date(string? v, bool day00, DateTime now, bool monthStart = false)
    {
        if (v is null || (v.Length != 6 && v.Length != 10) || !v.All(char.IsAsciiDigit)) return null;
        int yy = int.Parse(v[..2], CultureInfo.InvariantCulture), mm = int.Parse(v[2..4], CultureInfo.InvariantCulture),
            dd = int.Parse(v[4..6], CultureInfo.InvariantCulture);
        int year = now.Year / 100 * 100 + yy;
        if (year - now.Year > 50) year -= 100;
        else if (now.Year - year > 49) year += 100;
        if (mm is < 1 or > 12) return null;
        if (dd == 0 && !day00) return null;
        int day = dd == 0 ? (monthStart ? 1 : DateTime.DaysInMonth(year, mm)) : dd;
        if (day > DateTime.DaysInMonth(year, mm)) return null;
        if (v.Length == 10)
        {
            int hh = int.Parse(v[6..8], CultureInfo.InvariantCulture), mi = int.Parse(v[8..], CultureInfo.InvariantCulture);
            if (hh > 23 || mi > 59) return null;
        }
        return new DateOnly(year, mm, day);
    }

    private static DateTime DateTimeOf(string yymmddhhmm, DateOnly d) =>
        d.ToDateTime(new TimeOnly(int.Parse(yymmddhhmm[6..8], CultureInfo.InvariantCulture), int.Parse(yymmddhhmm[8..10], CultureInfo.InvariantCulture)));

    // ---- the three forms; each returns null when parsed, else why not

    private static string? ParseRaw(string s, List<(string, string)> elements)
    {
        int i = 0;
        if (s.Length > 0 && s[0] == GS) i++; // a leading FNC1 some readers pass on
        while (i < s.Length)
        {
            string? ai = null;
            for (int len = 2; len <= 4 && i + len <= s.Length; len++)
                if (Table.ContainsKey(s.Substring(i, len))) { ai = s.Substring(i, len); break; }
            if (ai is null) return $"no known AI at \"{Clip(s, i)}\"";
            i += ai.Length;
            var a = Table[ai];
            string value;
            if (a.Fixed)
            {
                if (i + a.Max > s.Length) return $"the {a.Name} ({ai}) is cut short";
                value = s.Substring(i, a.Max);
                i += a.Max;
                if (i < s.Length && s[i] == GS)
                {
                    if (i + 1 == s.Length) return "the code ends with a separator";
                    i++; // a needless separator between elements is allowed
                }
            }
            else
            {
                int end = s.IndexOf(GS, i);
                // a variable-length value runs to the separator or the end; a GTIN-like run after a
                // batch without its separator reads as part of the batch and fails its length or
                // the required AIs, never as a silently shortened value
                if (end < 0) end = s.Length;
                if (end + 1 == s.Length) return "the code ends with a separator";
                value = s[i..end];
                i = end < s.Length ? end + 1 : end;
            }
            elements.Add((ai, value));
        }
        return elements.Count > 0 ? null : "no data";
    }

    private static string? ParseHri(string s, List<(string, string)> elements)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] != '(') return $"expected \"(\" at \"{Clip(s, i)}\"";
            int close = s.IndexOf(')', i);
            if (close < 0) return "an AI has no \")\"";
            string ai = s[(i + 1)..close];
            if (!Table.ContainsKey(ai)) return $"({ai}) is not a known AI";
            int next = s.IndexOf('(', close);
            // a "(" inside a value would start a new AI: GS1 allows "(" in values, so the next "(" only
            // ends this value when an AI and ")" follow
            while (next >= 0 && !(s.IndexOf(')', next) is var c2 and > 0 && Table.ContainsKey(s[(next + 1)..c2])))
                next = s.IndexOf('(', next + 1);
            if (next < 0) next = s.Length;
            elements.Add((ai, s[(close + 1)..next]));
            i = next;
        }
        return elements.Count > 0 ? null : "no data";
    }

    private static string Clip(string s, int i) => s.Length - i > 12 ? s.Substring(i, 12) + "..." : s[i..];
}
