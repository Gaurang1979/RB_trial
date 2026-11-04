using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BarcodeReading;

/// <summary>
/// GS1 element string parsed from a code: GTIN, batch, expiry, serial, SSCC and any other
/// application identifiers (AIs). Accepts the raw form with GS (ASCII 29) separators, the
/// human-readable form "(01)...(21)...", and GS1 Digital Link URLs ".../01/{gtin}/21/{serial}".
/// </summary>
public sealed class Gs1Data
{
    private const char GS = '\u001D';

    private Gs1Data(IReadOnlyDictionary<string, string> fields) => Fields = fields;

    /// <summary>Every AI found, in the order read.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; }

    public string? Gtin => Get("01");
    public string? Sscc => Get("00");
    public string? Batch => Get("10");
    public string? Serial => Get("21");
    public DateOnly? Expiry => ParseDate(Get("17"));
    public DateOnly? ProductionDate => ParseDate(Get("11"));

    /// <summary>True when the GTIN (or SSCC) check digit is correct.</summary>
    public bool CheckDigitValid =>
        (Gtin is null || CheckDigitOk(Gtin)) && (Sscc is null || CheckDigitOk(Sscc));

    private string? Get(string ai) => Fields.TryGetValue(ai, out var v) ? v : null;

    public static Gs1Data? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var fields = new Dictionary<string, string>();
        bool ok;
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            ok = ParseDigitalLink(text, fields);
        else if (text.StartsWith('('))
            ok = ParseHri(text, fields);
        else
            ok = ParseRaw(text.StartsWith("]") && text.Length > 3 ? text[3..] : text, fields);
        return ok && (fields.ContainsKey("01") || fields.ContainsKey("00")) ? new Gs1Data(fields) : null;
    }

    private static bool ParseRaw(string s, Dictionary<string, string> fields)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == GS) { i++; continue; }
            string? ai = null;
            for (int len = 2; len <= 4 && i + len <= s.Length; len++)
            {
                if (Gs1Check.Known(s.Substring(i, len))) { ai = s.Substring(i, len); break; }
            }
            if (ai is null) return false;
            i += ai.Length;
            // the AIs of the GS1 Syntax Dictionary (Gs1Check); a predefined length needs no separator
            int fixedLen = Gs1Check.PredefinedLength(ai);
            string value;
            if (fixedLen > 0)
            {
                if (i + fixedLen > s.Length) return false;
                value = s.Substring(i, fixedLen);
                i += fixedLen;
            }
            else
            {
                int end = s.IndexOf(GS, i);
                if (end < 0) end = s.Length;
                value = s[i..end];
                i = end;
            }
            fields[ai] = value;
        }
        return fields.Count > 0;
    }

    private static bool ParseHri(string s, Dictionary<string, string> fields)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] != '(') return false;
            int close = s.IndexOf(')', i);
            if (close < 0) return false;
            string ai = s[(i + 1)..close];
            int next = s.IndexOf('(', close);
            if (next < 0) next = s.Length;
            fields[ai] = s[(close + 1)..next];
            i = next;
        }
        return fields.Count > 0;
    }

    private static bool ParseDigitalLink(string url, Dictionary<string, string> fields)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        // AIs come as /ai/value pairs; the GTIN pair may be preceded by other path segments
        int start = Array.FindIndex(parts, p => p is "01" or "00" or "414" or "8006");
        if (start < 0) return false;
        for (int i = start; i + 1 < parts.Length; i += 2)
            fields[parts[i]] = Uri.UnescapeDataString(parts[i + 1]);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Length is >= 2 and <= 4 && char.IsDigit(kv[0][0]))
                fields[kv[0]] = Uri.UnescapeDataString(kv[1]);
        }
        return fields.Count > 0;
    }

    /// <summary>GS1 mod-10 check digit over all digits including the last.</summary>
    /// <summary>
    /// True when an EAN-13 (or UPC/EAN of any length) is the product's GTIN-14: the same number with
    /// leading zeros. EAN-13 has one mod-10 check digit, which a misread passes one time in ten; a
    /// parent EAN that must equal the product's GTIN cannot be misread into a valid one.
    /// </summary>
    public static bool SameGtin(string ean, string gtin14) =>
        ean.Length is > 0 and <= 14 && ean.All(char.IsAsciiDigit) && gtin14.Length == 14 && ean.PadLeft(14, '0') == gtin14;

    public static bool CheckDigitOk(string digits)
    {
        if (digits.Length < 2) return false;
        int sum = 0;
        for (int i = digits.Length - 2, w = 3; i >= 0; i--, w = 4 - w)
        {
            if (!char.IsDigit(digits[i])) return false;
            sum += (digits[i] - '0') * w;
        }
        int check = (10 - sum % 10) % 10;
        return char.IsDigit(digits[^1]) && digits[^1] - '0' == check;
    }

    private static DateOnly? ParseDate(string? yymmdd)
    {
        if (yymmdd is null || yymmdd.Length != 6 ||
            !int.TryParse(yymmdd, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return null;
        int yy = int.Parse(yymmdd[..2], CultureInfo.InvariantCulture);
        int mm = int.Parse(yymmdd[2..4], CultureInfo.InvariantCulture);
        int dd = int.Parse(yymmdd[4..], CultureInfo.InvariantCulture);
        // GS1 rule: years are within -49..+50 of the current year
        int century = DateTime.UtcNow.Year / 100 * 100;
        int year = century + yy;
        if (year - DateTime.UtcNow.Year > 50) year -= 100;
        else if (DateTime.UtcNow.Year - year > 49) year += 100;
        if (mm is < 1 or > 12) return null;
        // day 00 means the last day of the month
        int day = dd == 0 ? DateTime.DaysInMonth(year, mm) : dd;
        return day <= DateTime.DaysInMonth(year, mm) ? new DateOnly(year, mm, day) : null;
    }
}
