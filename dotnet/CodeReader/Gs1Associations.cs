using System;
using System.Collections.Generic;
using System.Linq;

namespace BarcodeReading;

/// <summary>
/// The GS1 General Specifications' rules between AIs (section 4.13: invalid pairs and mandatory
/// associations) and GS1 Digital Link's URI structure (primary key, key qualifiers in their order,
/// data attributes in the query), read from the GS1 Barcode Syntax Dictionary's rows
/// (Gs1Dictionary.g.cs), so they move unchanged into the C++ Code Verify parent tool. AI patterns:
/// "n" stands for any digit ("310n" = 3100..3109).
/// </summary>
public static partial class Gs1Check
{
    // the dictionary's rules: "req=01+21,02" = (01 with 21) or (02) must be in the same code, several
    // req= of one AI all apply; "ex=01,310n" = none of these may be in the same code; "dlpkey=22,10,21|235"
    // = a Digital Link primary key whose path may hold the qualifiers of one of the sequences, in order
    private static IEnumerable<(string Ai, string Key, string Value)> Rules() =>
        from row in Dictionary
        from ai in Range(row.From, row.To)
        from rule in row.Rules.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        let kv = rule.Split('=', 2)
        select (ai, kv[0], kv.Length == 2 ? kv[1] : "");

    private static (string, string[][])[] BuildRequires() =>
        Rules().Where(r => r.Key == "req").Select(r => (r.Ai, r.Value.Split(',').Select(g => g.Split('+')).ToArray())).ToArray();

    private static (string, string[])[] BuildExcludes() =>
        Rules().Where(r => r.Key == "ex").Select(r => (r.Ai, r.Value.Split(','))).ToArray();

    private static Dictionary<string, string[][]> BuildLinkKeys() =>
        Rules().Where(r => r.Key == "dlpkey").ToDictionary(r => r.Ai,
            r => r.Value.Split('|').Select(seq => seq.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray());

    private static (string Ai, string[][] AnyOf)[] Requires => Tables.Requires;
    private static (string Ai, string[] Patterns)[] Excludes => Tables.Excludes;
    private static Dictionary<string, string[][]> LinkKeys => Tables.LinkKeys;

    /// <summary>Named sets of required AIs. "pharma": GTIN, expiry, batch and serial number, as
    /// GS1 Healthcare, the EU Falsified Medicines Directive and the US DSCSA ask of a pack.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RequiredSets = new Dictionary<string, string[]>
    {
        ["pharma"] = new[] { "01", "17", "10", "21" },
    };

    private static bool Matches(string pattern, string ai) =>
        pattern.Length == ai.Length && pattern.Zip(ai).All(p => p.First == 'n' ? char.IsAsciiDigit(p.Second) : p.First == p.Second);

    // "GTIN (01) with serial number (21)"; a pattern ("31nn") takes the name of its first AI
    private static string Group(string[] group) => string.Join(" with ", group.Select(a =>
        $"{NameOf(a.Contains('n') ? Table.Keys.Where(k => Matches(a, k)).DefaultIfEmpty(a).Min()! : a)} ({a})"));

    private static string AnyOf(string[][] groups)
    {
        var s = groups.Select(Group).ToList();
        return s.Count == 1 ? s[0] : string.Join(", ", s.Take(s.Count - 1)) + " or " + s[^1];
    }

    /// <summary>The association problems of a code's elements: a mandatory partner missing, an
    /// invalid pair (one measure with two decimal positions is one).</summary>
    private static void CheckAssociations(List<(string Ai, string Value)> elements, List<Gs1Problem> problems)
    {
        var ais = elements.Select(e => e.Ai).Distinct().ToList();
        bool Has(string pattern) => ais.Any(ai => Matches(pattern, ai));
        foreach (var ai in ais)
            foreach (var (_, anyOf) in Requires.Where(r => r.Ai == ai))
                if (!anyOf.Any(g => g.All(Has)))
                    problems.Add(new($"The {NameOf(ai)} ({ai}) needs a {AnyOf(anyOf)} in the same code", "explain.gs1.association"));
        var reported = new HashSet<(string, string)>();
        foreach (var a in ais)
            foreach (var (_, patterns) in Excludes.Where(x => x.Ai == a))
                // an AI does not exclude itself, even when it matches a pattern
                foreach (var b in ais.Where(b => b != a && patterns.Any(p => Matches(p, b))))
                    if (reported.Add(string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a)))
                        problems.Add(new($"The {NameOf(a)} ({a}) and the {NameOf(b)} ({b}) may not be in one code", "explain.gs1.association"));
    }

    /// <summary>
    /// A GS1 Digital Link URI: any path prefix, then a primary key and its value, then that key's
    /// qualifiers in their defined order, then data attributes in the query. A GTIN-8, -12 or -13 in
    /// the path is read as a GTIN-14 with leading zeros. Non-AI query parameters (linkType, ...) are other link data.
    /// </summary>
    private static string? ParseDigitalLink(string url, List<(string, string)> elements)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "not a valid link";
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        // the key is the first primary key from which the rest of the path is AI and value pairs
        int start = Enumerable.Range(0, parts.Length).FirstOrDefault(i => LinkKeys.ContainsKey(parts[i]) && (parts.Length - i) % 2 == 0, -1);
        if (start < 0)
            return Array.FindIndex(parts, LinkKeys.ContainsKey) is var k and >= 0
                ? $"/{parts[k]} is not followed by AI and value pairs"
                : "the link holds no GS1 key (GTIN, SSCC, GLN, ...)";
        string key = parts[start];
        var sequences = LinkKeys[key];
        bool Qualifier(string ai) => sequences.Any(q => q.Contains(ai));
        for (int i = 0; i < start; i++)
            if (Qualifier(parts[i]) || LinkKeys.ContainsKey(parts[i]))
                return $"/{parts[i]} comes before the primary key /{key}";
        var path = new List<string>();
        for (int i = start; i < parts.Length; i += 2)
        {
            string ai = parts[i], value = Uri.UnescapeDataString(parts[i + 1]);
            if (i > start)
            {
                if (!Table.ContainsKey(ai)) return $"/{ai} is not a known AI";
                if (!Qualifier(ai))
                    return LinkKeys.ContainsKey(ai) ? $"/{ai} is a second primary key in the path"
                        : $"/{ai} is not a key qualifier of /{key}: it belongs in the query (?{ai}=...)";
                path.Add(ai);
                // the qualifiers so far must be one sequence's, in its order
                if (!sequences.Any(q => InOrder(path, q)))
                    return $"/{ai} is out of order: after /{key} the path holds "
                        + string.Join(" or ", sequences.Select(q => string.Join(", ", q.Select(x => "/" + x)))) + " in that order";
            }
            else if (key == "01" && value.Length is 8 or 12 or 13 && value.All(char.IsAsciiDigit))
                value = value.PadLeft(14, '0');
            elements.Add((ai, value));
        }
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2 || kv[0].Length is < 2 or > 4 || !kv[0].All(char.IsAsciiDigit)) continue; // not an AI: other link data
            if (!Table.TryGetValue(kv[0], out var a)) return $"?{kv[0]}= is not a known AI";
            if (LinkKeys.ContainsKey(kv[0]) || Qualifier(kv[0]))
                return $"?{kv[0]}= belongs in the path: keys and their qualifiers are path segments";
            if (!a.LinkAttribute) return $"?{kv[0]}= is not allowed as a Digital Link data attribute";
            elements.Add((kv[0], Uri.UnescapeDataString(kv[1])));
        }
        return null;
    }

    // true when every AI of the path is in the sequence, each after the one before
    private static bool InOrder(List<string> path, string[] sequence)
    {
        int last = -1;
        foreach (var ai in path)
        {
            int at = Array.IndexOf(sequence, ai);
            if (at <= last) return false;
            last = at;
        }
        return true;
    }
}
