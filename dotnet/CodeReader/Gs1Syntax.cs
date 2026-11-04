using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BarcodeReading;

/// <summary>
/// The AI table of the GS1 check, built from GS1's Barcode Syntax Dictionary (Gs1Dictionary.g.cs,
/// written by tools/gs1/gen_gs1_table.py): every AI with its components (N digits, X the 82-character
/// set, Y the 39-character set, Z base64url; fixed or variable length; optional parts), each component's
/// checks (check digits, dates, times, country and currency codes, ...), whether it needs no separator,
/// and the rules between AIs (Gs1Associations.cs).
/// </summary>
public static partial class Gs1Check
{
    // one component of an AI's value: type, length, optional, and the checks on it
    private sealed record Part(char Type, int Min, int Max, bool Optional, string[] Linters);

    // Fixed = predefined length: no separator needed after it
    private sealed record Ai(string Name, Part[] Parts, bool Fixed, bool LinkAttribute)
    {
        public int Min => Parts.Where(p => !p.Optional).Sum(p => p.Min);
        public int Max => Parts.Sum(p => p.Max);
    }

    /// <summary>Checks of the dictionary this check does not make: the North American coupon
    /// codes (8110, 8112), which no Vision System line prints. Their type and length are checked.</summary>
    public static readonly IReadOnlyCollection<string> UncheckedLinters = new[] { "couponcode", "couponposoffer" };

    /// <summary>Every check the dictionary names, so a test can find one a refresh added.</summary>
    public static IEnumerable<string> DictionaryLinters =>
        Table.Values.SelectMany(a => a.Parts).SelectMany(p => p.Linters).Distinct();

    /// <summary>True for a check this class makes.</summary>
    public static bool LinterKnown(string linter) => Lint(linter, "0", "0", new DateTime(2000, 1, 1)) is not Unknown;

    // names in the operator's messages; the AIs not named here use GS1's data title ("GDTI", "SHIP TO COMP")
    private static readonly (string Pattern, string Name)[] Names =
    {
        ("00", "SSCC"), ("01", "GTIN"), ("02", "GTIN of contained trade items"), ("03", "GTIN of a made-to-order item"),
        ("10", "batch"), ("11", "production date"), ("12", "due date"), ("13", "packaging date"),
        ("15", "best before date"), ("16", "sell by date"), ("17", "expiry date"), ("20", "variant"),
        ("21", "serial number"), ("22", "consumer product variant"), ("235", "third-party serial extension"),
        ("240", "additional product id"), ("241", "customer part number"), ("242", "made-to-order variant"),
        ("243", "packaging component number"), ("250", "secondary serial number"), ("251", "reference to source entity"),
        ("253", "document type id (GDTI)"), ("254", "GLN extension"), ("255", "coupon code id (GCN)"),
        ("30", "count"), ("37", "count of trade items"),
        ("31nn", "measure"), ("32nn", "measure"), ("33nn", "measure"), ("34nn", "measure"), ("35nn", "measure"), ("36nn", "measure"),
        ("390n", "amount payable"), ("391n", "amount payable with currency"), ("392n", "price"),
        ("393n", "price with currency"), ("394n", "coupon discount percentage"), ("395n", "price per unit of measure"),
        ("400", "order number"), ("401", "consignment number (GINC)"), ("402", "shipment id (GSIN)"), ("403", "routing code"),
        ("410", "ship to GLN"), ("411", "bill to GLN"), ("412", "purchased from GLN"), ("413", "ship for GLN"),
        ("414", "location GLN"), ("415", "pay to GLN"), ("416", "production location GLN"), ("417", "party GLN"),
        ("420", "ship to postal code"), ("421", "ship to postal code with country"), ("422", "country of origin"),
        ("423", "country of initial processing"), ("424", "country of processing"), ("425", "country of disassembly"),
        ("426", "country of full processing"), ("427", "country subdivision of origin"),
        ("7001", "NATO stock number"), ("7002", "meat cut"), ("7003", "expiry date and time"), ("7004", "active potency"),
        ("7006", "first freeze date"), ("7007", "harvest date"), ("7011", "test by date"), ("7023", "assembly asset id"),
        ("703n", "processor approval number"), ("7040", "importer id (UIC)"), ("71n", "national healthcare number"),
        ("723n", "certification reference"), ("7240", "protocol id"), ("7250", "date of birth"),
        ("8001", "roll dimensions"), ("8002", "mobile phone id"), ("8003", "returnable asset id"), ("8004", "individual asset id"),
        ("8005", "price per unit"), ("8006", "trade item component"), ("8007", "IBAN"), ("8008", "production date and time"),
        ("8009", "sensor indicator"), ("8010", "component or part id (CPID)"), ("8011", "CPID serial number"),
        ("8012", "software version"), ("8013", "model number (GMN)"), ("8014", "medical device model (MUDI)"),
        ("8017", "service relation number, provider (GSRN)"), ("8018", "service relation number, recipient (GSRN)"),
        ("8019", "service relation instance number"), ("8020", "payment slip reference"),
        ("8026", "contained trade item component"), ("8030", "digital signature"), ("8040", "IMEI"),
        ("8200", "product URL"), ("90", "internal data"), ("9n", "company internal data"),
    };

    private static Dictionary<string, Ai> BuildTable()
    {
        var t = new Dictionary<string, Ai>();
        foreach (var (from, to, flags, spec, _, title) in Dictionary)
            foreach (var ai in Range(from, to))
                t[ai] = new(Names.FirstOrDefault(n => Matches(n.Pattern, ai)).Name ?? title, Parts(spec), flags.Contains('*'), flags.Contains('?'));
        return t;
    }

    private static IEnumerable<string> Range(string from, string to)
    {
        int a = int.Parse(from, CultureInfo.InvariantCulture), b = int.Parse(to, CultureInfo.InvariantCulture);
        for (int i = a; i <= b; i++) yield return i.ToString(CultureInfo.InvariantCulture).PadLeft(from.Length, '0');
    }

    // "N6,yymmdd [N4],hhmi X..20" -> parts
    private static Part[] Parts(string spec) => spec.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(c =>
    {
        var bits = c.Split(',');
        string type = bits[0];
        bool optional = type.StartsWith('[');
        if (optional) type = type.Trim('[', ']');
        bool variable = type.Contains("..");
        int len = int.Parse(type[(variable ? 3 : 1)..], CultureInfo.InvariantCulture);
        return new Part(type[0], variable ? 1 : len, len, optional, bits[1..]);
    }).ToArray();

    /// <summary>The problem in one element's value, or null: length, character set, then each
    /// component's checks in order.</summary>
    private static Gs1Problem? CheckValue(string ai, string value, DateTime now)
    {
        var a = Table[ai];
        if (value.Length < a.Min || value.Length > a.Max)
            return new(a.Min == a.Max
                ? $"The {a.Name} ({ai}) has {value.Length} characters, it must have {a.Min}"
                : $"The {a.Name} ({ai}) has {value.Length} characters, it may have {a.Min} to {a.Max}", "explain.gs1.syntax");
        // the components take their lengths from the front; only the last may be variable
        var pieces = new List<(Part P, string V)>();
        int at = 0;
        foreach (var p in a.Parts)
        {
            if (p.Optional && at == value.Length) break;
            int len = p.Min == p.Max ? p.Max : value.Length - at;
            if (at + len > value.Length)
                return new($"The {a.Name} ({ai}) has {value.Length} characters, which do not make whole parts", "explain.gs1.syntax");
            pieces.Add((p, value.Substring(at, len)));
            at += len;
        }
        if (at != value.Length)
            return new($"The {a.Name} ({ai}) has {value.Length} characters, which do not make whole parts", "explain.gs1.syntax");
        foreach (var (p, v) in pieces)
        {
            // base64url may end with up to two "=" (as GS1's syntax engine reads it)
            int pads = p.Type == 'Z' ? v.Length - v.TrimEnd('=').Length : 0;
            if (pads > 2 || (pads > 0 && v.Length % 3 != 0))
                return new($"The {a.Name} ({ai}) ends with a bad base64 padding", "explain.gs1.syntax");
            int bad = v[..^pads].ToList().FindIndex(c => !InSet(p.Type, c));
            if (p.Type == 'N' && bad >= 0)
                return new($"The {a.Name} ({ai}) must be digits only: {value}", "explain.gs1.syntax");
            if (bad >= 0)
                return new($"The {a.Name} ({ai}) holds a character GS1 does not allow: {Show(v[bad])}", "explain.gs1.syntax");
        }
        foreach (var (p, v) in pieces)
            foreach (var l in p.Linters)
                switch (Lint(l, v, value, now))
                {
                    case null: break;
                    case Unknown: return new($"The {a.Name} ({ai}) cannot be checked: GS1 check \"{l}\" is not known", "explain.gs1.syntax");
                    case var why:
                        return new($"The {a.Name} ({ai}) {(why.StartsWith("check") ? why + ": " + v : v + " " + why)}",
                            why.StartsWith("check") ? "explain.gs1.check_digit" : why.Contains("date") || why.Contains("time") ? "explain.gs1.bad_date" : "explain.gs1.syntax");
                }
        return null;
    }

    private const string Unknown = "\0unknown";

    private static bool InSet(char type, char c) => type switch
    {
        'N' => char.IsAsciiDigit(c),
        'X' => Cset82(c),
        'Y' => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c) || c is '#' or '-' or '/',
        'Z' => char.IsAsciiLetterOrDigit(c) || c is '-' or '_',
        _ => false,
    };

    /// <summary>One GS1 syntax check ("linter") on a component <paramref name="v"/> of the element's
    /// value; null when good, else what is wrong ("check digit is wrong", "is not a real date", ...).</summary>
    private static string? Lint(string linter, string v, string value, DateTime now) => linter switch
    {
        "csum" => v.Length >= 2 && Gs1Data.CheckDigitOk(v) ? null : "check digit is wrong",
        "csumalpha" => CheckPairOk(v) ? null : "check characters are wrong",
        // the GS1 Company Prefix: its at least four digits from the first or second position; the
        // prefix itself is not looked up (that needs GS1's online list)
        "gcppos1" => v.Length >= 4 && v[..4].All(char.IsAsciiDigit) ? null : "has no GS1 Company Prefix",
        "gcppos2" => v.Length >= 5 && v[1..5].All(char.IsAsciiDigit) ? null : "has no GS1 Company Prefix",
        "yymmdd" => Date(v, false, now) is null ? "is not a real date" : null,
        "yymmd0" => Date(v, true, now) is null ? "is not a real date" : null,
        "yyyymmdd" => FullDate(v, false) ? null : "is not a real date",
        "yyyymmd0" => FullDate(v, true) ? null : "is not a real date",
        "hhmi" => v.Length == 4 && Num(v[..2]) <= 23 && Num(v[2..]) <= 59 ? null : "is not a real time",
        "hh" => v.Length == 2 && Num(v) <= 23 ? null : "is not a real time",
        "mi" or "ss" => v.Length == 2 && Num(v) <= 59 ? null : "is not a real time",
        "iso3166" => CodeLists["iso3166"].Contains(v) ? null : "is not a country code (ISO 3166)",
        "iso3166999" => v == "999" || CodeLists["iso3166"].Contains(v) ? null : "is not a country code (ISO 3166) or 999",
        "iso3166alpha2" => CodeLists["iso3166alpha2"].Contains(v) ? null : "is not a two-letter country code (ISO 3166)",
        "iso4217" => CodeLists["iso4217"].Contains(v) ? null : "is not a currency code (ISO 4217)",
        "packagetype" => CodeLists["packagetype"].Contains(v) ? null : "is not a package type code",
        "iso5218" => v is "0" or "1" or "2" or "9" ? null : "is not a sex code (ISO/IEC 5218)",
        "zero" => v == "0" ? null : "must be 0",
        "nonzero" => v.All(c => c == '0') ? "must not be zero" : null,
        "nozeroprefix" => v.Length > 0 && v[0] == '0' ? "must not start with 0" : null,
        "hasnondigit" => v.All(char.IsAsciiDigit) ? "must hold a character that is not a digit" : null,
        "yesno" => v is "0" or "1" ? null : "must be 0 or 1",
        "hyphen" => v == "-" ? null : "must be \"-\"",
        "winding" => v is "0" or "1" or "9" ? null : "is not a winding direction (0, 1 or 9)",
        "mediatype" => v.Length == 2 && Num(v) is (>= 1 and <= 10) or (>= 80 and <= 99) ? null : "is not an AIDC media type",
        "importeridx" => v.Length == 1 && (char.IsAsciiLetterOrDigit(v[0]) || v[0] is '-' or '_') ? null : "is not an importer index",
        "pieceoftotal" => PieceOfTotalOk(v) ? null : "is not a piece number within the total",
        "posinseqslash" => PositionOk(v) ? null : "is not a position in a sequence (n/total)",
        "pcenc" => PercentOk(v) ? null : "holds a bad percent escape",
        "latitude" => v.Length == 10 && long.Parse(v, CultureInfo.InvariantCulture) <= 1800000000 ? null : "is not a latitude",
        "longitude" => v.Length == 10 && long.Parse(v, CultureInfo.InvariantCulture) <= 3600000000 ? null : "is not a longitude",
        "iban" => IbanOk(v) ? null : "check digits are wrong",
        "couponcode" or "couponposoffer" => null,
        _ => Unknown,
    };

    private static int Num(string s) => s.All(char.IsAsciiDigit) && s.Length is > 0 and < 10 ? int.Parse(s, CultureInfo.InvariantCulture) : int.MaxValue;

    private static bool FullDate(string v, bool day00)
    {
        if (v.Length != 8 || !v.All(char.IsAsciiDigit)) return false;
        int y = Num(v[..4]), m = Num(v[4..6]), d = Num(v[6..]);
        return y >= 1 && m is >= 1 and <= 12 && (d == 0 ? day00 : d <= DateTime.DaysInMonth(y, m));
    }

    // GS1 check character pair (GMN, MUDI): weighted sum of the 82-set values over primes, mod 1021
    private static bool CheckPairOk(string v)
    {
        const string set82 = "!\"%&'()*+,-./0123456789:;<=>?ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";
        const string set32 = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        int[] primes = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89 };
        if (v.Length < 2 || v.Length > primes.Length + 2) return false;
        int sum = 0;
        for (int i = 0; i < v.Length - 2; i++)
        {
            int w = set82.IndexOf(v[i]);
            if (w < 0) return false;
            sum += w * primes[v.Length - 3 - i];
        }
        sum %= 1021;
        return v[^2] == set32[sum >> 5] && v[^1] == set32[sum & 31];
    }

    // piece number and total in two halves of equal length: neither zero, piece not above total
    private static bool PieceOfTotalOk(string v) =>
        v.Length % 2 == 0 && v.Length > 0 && v.All(char.IsAsciiDigit) && v[..(v.Length / 2)] is var p && v[(v.Length / 2)..] is var t
        && p.Any(c => c != '0') && t.Any(c => c != '0') && string.CompareOrdinal(p, t) <= 0;

    // "n/total": both without leading zero, n not above total
    private static bool PositionOk(string v)
    {
        var s = v.Split('/');
        return s.Length == 2 && s.All(x => x.Length > 0 && x.All(char.IsAsciiDigit) && x[0] != '0')
            && (s[0].Length < s[1].Length || (s[0].Length == s[1].Length && string.CompareOrdinal(s[0], s[1]) <= 0));
    }

    private static bool PercentOk(string v)
    {
        for (int i = 0; i < v.Length; i++)
            if (v[i] == '%')
            {
                if (i + 2 >= v.Length || !char.IsAsciiHexDigit(v[i + 1]) || !char.IsAsciiHexDigit(v[i + 2])) return false;
                i += 2;
            }
        return true;
    }

    // ISO 13616: country code, then mod 97 of the rearranged number is 1
    private static bool IbanOk(string v)
    {
        if (v.Length is <= 10 or > 34 || !CodeLists["iso3166alpha2"].Contains(v[..2])) return false;
        int sum = 0;
        foreach (var c in v[4..] + v[..4])
        {
            if (char.IsAsciiDigit(c)) sum = (sum * 10 + (c - '0')) % 97;
            else if (char.IsAsciiLetterUpper(c)) sum = (sum * 100 + (c - 'A' + 10)) % 97;
            else return false;
        }
        return sum == 1;
    }
}
