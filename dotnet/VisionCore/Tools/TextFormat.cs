using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VisionCore;

/// <summary>
/// The format of one printed line in Read text mode (tool specification 7.4, "Check date / lot"):
/// literal text with fields in braces.
/// <list type="bullet">
/// <item><c>{D:5}</c> five digits, <c>{D:4-6}</c> four to six, <c>{D}</c> one or more; <c>{A:1}</c> letters;
/// <c>{X:6}</c> letters or digits; <c>{?:3}</c> any characters.</item>
/// <item><c>{MFD:DD/MM/YY}</c> a date named MFD laid out with DD, MM, MMM (month names in the tool's languages,
/// <see cref="MonthNames"/>; JAN-DEC by default), YY and YYYY. It must be a real date. The names in
/// <see cref="DateRules"/> (MFD and EXP by default) get the date rules.</item>
/// <item>Legal Metrology (Packaged Commodities Rules 2011) fields: <c>{MRP}</c> the label (MRP, M.R.P.,
/// MAXIMUM RETAIL PRICE, a colon or not), the currency (₹, Rs., Rs, INR) and rupees with paise (46.00);
/// <c>{PRICE}</c> the currency and amount alone (a unit sale price); <c>{MRP:whole}</c> whole rupees (Rs. 10);
/// <c>{MRP:46.00}</c> exactly that amount. An amount of 0 is not a price. <c>{PER}</c> the unit of a unit
/// sale price (per g, /ml, per 100 g, per kg ...). <c>{LIFE}</c> the months of "Best before 18 months from
/// manufacture": the date rules then count the expiry from the MFD date.</item>
/// <item><c>{re:[A-Z]\d+}</c> a regular expression, for the engineer.</item>
/// <item><c>\{</c> and <c>\\</c> are a printed brace and backslash.</item>
/// </list>
/// A space in the format matches any spacing (OCR spacing varies), and letters match in either case.
/// A read character is never corrected (an O read where a digit should be fails): a format check must
/// not turn a misprint into a pass.
/// <para>The format is also given to the OCR engine (<see cref="EngineFormat"/>), which then reads each
/// place only with the characters the format allows there (tool specification 7.1, lexicon-limited
/// decoding); a character still keeps its own score, so a weak one fails as before.</para>
/// </summary>
public sealed class TextFormat
{
    /// <summary>
    /// Printed month abbreviations by language tag, January first; each month lists the spellings seen on
    /// packs (with and without accents, as printers often drop them). English is the default; the others
    /// are the Latin-script languages of the application (Spanish) and of the main export markets.
    /// "en-IN" is English as Indian packs print it (Legal Metrology labels): the short names, the four-letter
    /// JUNE, JULY and SEPT, and the full names (BEST BEFORE MARCH 2027). Hindi and the other Indian scripts
    /// are not here yet: the AI reader's charset (ml/models/ocr_charset.txt) has no Devanagari.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[][]> MonthNames = new Dictionary<string, string[][]>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = M("JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP|SEPT", "OCT", "NOV", "DEC"),
        ["en-IN"] = M("JAN|JANUARY", "FEB|FEBRUARY", "MAR|MARCH", "APR|APRIL", "MAY", "JUN|JUNE", "JUL|JULY", "AUG|AUGUST",
                      "SEP|SEPT|SEPTEMBER", "OCT|OCTOBER", "NOV|NOVEMBER", "DEC|DECEMBER"),
        ["es"] = M("ENE", "FEB", "MAR", "ABR", "MAY", "JUN", "JUL", "AGO", "SEP|SET", "OCT", "NOV", "DIC"),
        ["fr"] = M("JANV|JAN", "FÉVR|FEVR|FÉV|FEV", "MARS|MAR", "AVR", "MAI", "JUIN", "JUIL", "AOÛT|AOUT|AOU", "SEPT|SEP", "OCT", "NOV", "DÉC|DEC"),
        ["de"] = M("JAN", "FEB", "MÄR|MRZ|MAR", "APR", "MAI", "JUN", "JUL", "AUG", "SEP", "OKT", "NOV", "DEZ"),
        ["pt"] = M("JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"),
        ["it"] = M("GEN", "FEB", "MAR", "APR", "MAG", "GIU", "LUG", "AGO", "SET", "OTT", "NOV", "DIC"),
    };

    private static string[][] M(params string[] months) => months.Select(m => m.Split('|')).ToArray();

    /// <summary>Month number (1-12) of each spelling in the given languages; throws for an unknown language.</summary>
    public static Dictionary<string, int> MonthSpellings(IEnumerable<string>? languages)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in languages is null || !languages.Any() ? new[] { "en" } : languages)
        {
            if (!MonthNames.TryGetValue(lang, out var months))
                throw new FormatException($"No month names for the language \"{lang}\": use {string.Join(", ", MonthNames.Keys)}");
            for (int i = 0; i < 12; i++)
                foreach (var n in months[i]) map.TryAdd(n, i + 1);
        }
        return map;
    }

    // a regex alternation of the spellings, longest first (SEPT before SEP)
    private static string MonthAlternation(Dictionary<string, int> months) =>
        string.Join("|", months.Keys.OrderByDescending(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).Select(Regex.Escape));
    private static readonly TimeSpan RegexTime = TimeSpan.FromMilliseconds(50);

    private readonly Regex _regex;
    private readonly List<DateField> _dates = new();
    private Dictionary<string, int> _months = new();
    // what the reader must be able to read: the printed characters, and the kinds of the fields (D A X ?)
    private readonly List<char> _literals = new();
    private readonly HashSet<char> _kinds = new();
    // the prices and the shelf life in the format, and the word lists of which one spelling must be readable
    private readonly List<ValueField> _values = new();
    private readonly List<string[]> _wordSets = new();

    private sealed record DateField(string Name, string Group, bool HasDay, bool MonthName, bool FourDigitYear);
    private sealed record ValueField(string Name, string Group, bool Life);

    // Legal Metrology words as Indian packs print them: the MRP label (with a colon or not), the rupee,
    // and the unit of a unit sale price (per 1 g, per 100 ml ...); spaces in a word match any spacing
    private static readonly string[] MrpLabels =
        new[] { "MRP", "M.R.P.", "M.R.P", "MAXIMUM RETAIL PRICE" }.SelectMany(l => new[] { l, l + " :" }).ToArray();
    private static readonly string[] Currencies = { "₹", "RS.", "RS", "INR" };
    private static readonly string[] PerWords = { "PER", "/" };
    private static readonly string[] Units =
        new[] { "G", "GM", "GMS", "GRAM", "KG", "ML", "L", "LTR", "LITRE", "PC", "PCS", "N", "UNIT" }
        .Concat(new[] { "G", "GM", "KG", "ML", "L", "LTR", "PC", "UNIT" }.Select(u => "1 " + u))
        .Concat(new[] { "G", "GM", "ML" }.Select(u => "100 " + u)).ToArray();

    /// <summary>True when a line holds a shelf life ({LIFE}).</summary>
    public bool HasShelfLife => _values.Any(v => v.Life);

    public string Template { get; }

    /// <summary>
    /// The format in the OCR engine's syntax (vs_ocr_set_format): each place limited to the characters it
    /// allows, DD to 01-31, MM to 01-12 and MMM to the month names. Null when the engine cannot take it
    /// ({re:} parts, counts above 64): that line is then read freely and checked here only.
    /// </summary>
    public string? EngineFormat { get; private init; }

    /// <summary>The names of the dates in the format, in order.</summary>
    public IEnumerable<string> DateNames => _dates.Select(d => d.Name);

    private TextFormat(string template, string pattern)
    {
        Template = template;
        _regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTime);
    }

    /// <summary>
    /// Compiles a format; throws <see cref="FormatException"/> with a sentence for the engineer.
    /// <paramref name="monthLanguages"/>: the languages whose month names MMM accepts (<see cref="MonthNames"/>); none = English.
    /// </summary>
    public static TextFormat Parse(string template, IEnumerable<string>? monthLanguages = null)
    {
        var months = MonthSpellings(monthLanguages);
        var dates = new List<DateField>();
        var literals = new List<char>();
        var kinds = new HashSet<char>();
        var values = new List<ValueField>();
        var wordSets = new List<string[]>();
        var sb = new StringBuilder("^\\s*");
        var eng = new StringBuilder();
        bool engineOk = true;
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '\\' && i + 1 < template.Length) { c = template[++i]; sb.Append(Literal(c)); eng.Append(EngineLiteral(c)); literals.Add(c); continue; }
            if (c != '{') { sb.Append(Literal(c)); eng.Append(EngineLiteral(c)); literals.Add(c); continue; }
            int end = ClosingBrace(template, i);
            if (end < 0) throw new FormatException($"A field in \"{template}\" has no closing }}");
            var (pattern, engine) = Field(template[(i + 1)..end], dates, months, literals, kinds, values, wordSets);
            sb.Append(pattern);
            if (engine is null) engineOk = false; else eng.Append(engine);
            i = end;
        }
        sb.Append("\\s*$");
        TextFormat f;
        // a {re:} part valid alone can still break the whole pattern ((?x) makes the rest a comment)
        try { f = new TextFormat(template, sb.ToString()) { EngineFormat = engineOk && eng.ToString().Trim().Length > 0 ? eng.ToString() : null }; }
        catch (ArgumentException e) { throw new FormatException($"\"{template}\" is not a valid format: {e.Message}"); }
        f._dates.AddRange(dates);
        f._months = months;
        f._literals.AddRange(literals.Where(l => !char.IsWhiteSpace(l)).Distinct());
        f._kinds.UnionWith(kinds);
        f._values.AddRange(values);
        f._wordSets.AddRange(wordSets);
        return f;
    }

    // a printed character in engine syntax: { | and \ escaped, any spacing one space
    private static string EngineLiteral(char c) => char.IsWhiteSpace(c) ? " " : c is '{' or '|' or '\\' ? "\\" + c : c.ToString();

    private static string EngineWords(IEnumerable<string> words) => "{|" + string.Join("|", words.Select(w => string.Concat(w.Select(EngineLiteral)))) + "}";

    // the } that closes the field opened at `open`: braces inside it nest ({re:\d{2}}), \ escapes one character
    private static int ClosingBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static string Literal(char c) => char.IsWhiteSpace(c) ? "\\s*" : Regex.Escape(c.ToString());

    // the regex part of a field and its engine format (null = the engine cannot take it)
    private static (string Pattern, string? Engine) Field(string body, List<DateField> dates, Dictionary<string, int> months,
                                                          List<char> literals, HashSet<char> kinds, List<ValueField> values,
                                                          List<string[]> wordSets)
    {
        int colon = body.IndexOf(':');
        string name = colon < 0 ? body : body[..colon], arg = colon < 0 ? "" : body[(colon + 1)..];
        switch (name)
        {
            case "D" or "A" or "X" or "?":
                var (count, lo, hi) = Count(arg, body);
                string cls = name switch { "D" => "\\d", "A" => "\\p{L}", "X" => "[\\p{L}\\d]", _ => "\\S" };
                string? engine = hi > 64 ? null : lo == 0 ? $"{{{name}}}" : lo == hi ? $"{{{name}:{lo}}}" : $"{{{name}:{lo}-{hi}}}";
                kinds.Add(name[0]);
                return (cls + count, engine);
            case "re":
                try { _ = new Regex(arg, RegexOptions.None, RegexTime); }
                catch (ArgumentException e) { throw new FormatException($"{{re:{arg}}} is not a valid expression: {e.Message}"); }
                return ($"(?:{arg})", null);
            case "MRP" or "PRICE":
            {
                var (amount, amountEngine) = Amount(arg, body, $"v{values.Count}", literals);
                kinds.Add('D');
                int same = values.Count(v => v.Name.StartsWith(name, StringComparison.Ordinal) && !v.Life);
                values.Add(new ValueField(same == 0 ? name : $"{name}{same + 1}", $"v{values.Count}", false));
                string pattern = Words(Currencies) + "\\s*" + amount, words = EngineWords(Currencies) + amountEngine;
                wordSets.Add(Currencies);
                if (name == "MRP")
                {
                    pattern = Words(MrpLabels) + "\\s*" + pattern;
                    words = EngineWords(MrpLabels) + words;
                    wordSets.Add(MrpLabels);
                }
                return (pattern, words);
            }
            case "PER":
                if (arg.Length > 0) throw new FormatException($"{{{body}}}: {{PER}} takes nothing after it");
                wordSets.Add(PerWords);
                wordSets.Add(Units);
                return (Words(PerWords) + "\\s*" + Words(Units) + "(?!\\p{L})", EngineWords(PerWords) + EngineWords(Units));
            case "LIFE":
                if (arg.Length > 0) throw new FormatException($"{{{body}}}: {{LIFE}} takes nothing after it");
                if (values.Any(v => v.Life)) throw new FormatException("The shelf life {LIFE} appears twice");
                kinds.Add('D');
                values.Add(new ValueField("LIFE", $"v{values.Count}", true));
                return ($"(?<v{values.Count - 1}>\\d{{1,3}})", "{D:1-3}");
        }
        if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]*$") || arg.Length == 0)
            throw new FormatException($"{{{body}}} is not a field: use {{D:5}}, {{A:1}}, {{X:6}}, {{?:3}}, {{MFD:DD/MM/YY}}, {{MRP}}, {{PRICE}}, {{PER}}, {{LIFE}} or {{re:...}}");
        kinds.Add('D'); // every date has a numeric year
        return DatePattern(name, arg, dates, months, literals);
    }

    // one of the words, longest first (RS. before RS): regex (a space = any spacing) and engine
    private static string Words(IEnumerable<string> words) =>
        "(?:" + string.Join("|", words.OrderByDescending(w => w.Length).Select(w => string.Concat(w.Select(Literal)))) + ")";

    // the amount of a price: rupees and paise (no arg), whole rupees ("whole") or exactly the given amount
    // (1,250.00 in Indian grouping); the regex group holds it
    private static (string Pattern, string Engine) Amount(string arg, string body, string group, List<char> literals)
    {
        if (arg.Length == 0) { literals.Add('.'); return ($"(?<{group}>\\d{{1,6}}\\.\\d{{2}})", "{D:1-6}.{D:2}"); }
        if (arg.Equals("whole", StringComparison.OrdinalIgnoreCase)) return ($"(?<{group}>\\d{{1,6}})(?![.,]?\\d)", "{D:1-6}");
        if (!Regex.IsMatch(arg, @"^(\d{1,2}(,\d{2})*,\d{3}|\d{1,7})(\.\d{1,2})?$") || ParseAmount(arg) <= 0)
            throw new FormatException($"{{{body}}}: give the amount as printed (46.00 or 1,250.00), \"whole\" for whole rupees, or nothing for rupees and paise");
        literals.AddRange(arg.Where(c => !char.IsAsciiDigit(c)));
        return ($"(?<{group}>{Regex.Escape(arg)})(?!\\d)", string.Concat(arg.Select(EngineLiteral)));
    }

    private static decimal ParseAmount(string s) => decimal.Parse(s.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    // regex count, and the range (0, 0 = one or more)
    private static (string Count, int Lo, int Hi) Count(string arg, string body)
    {
        if (arg.Length == 0) return ("+", 0, 0);
        var m = Regex.Match(arg, @"^(\d{1,3})(?:-(\d{1,3}))?$");
        if (!m.Success) throw new FormatException($"{{{body}}}: the count must be a number or a range such as 4-6");
        int lo = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int hi = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : lo;
        if (lo < 1 || hi < lo) throw new FormatException($"{{{body}}}: the count must be 1 or more, the range low to high");
        return (lo == hi ? $"{{{lo}}}" : $"{{{lo},{hi}}}", lo, hi);
    }

    private static readonly string EngineDay = EngineWords(Enumerable.Range(1, 31).Select(d => d.ToString("00", CultureInfo.InvariantCulture)));
    private static readonly string EngineMonth = EngineWords(Enumerable.Range(1, 12).Select(d => d.ToString("00", CultureInfo.InvariantCulture)));

    private static (string Pattern, string Engine) DatePattern(string name, string layout, List<DateField> dates, Dictionary<string, int> months,
                                                               List<char> literals)
    {
        if (dates.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new FormatException($"The date {name} appears twice");
        string g = $"d{dates.Count}";
        var sb = new StringBuilder();
        var eng = new StringBuilder();
        bool day = false, month = false, monthName = false, year = false, year4 = false;
        for (int i = 0; i < layout.Length;)
        {
            string rest = layout[i..];
            if (rest.StartsWith("YYYY", StringComparison.Ordinal)) { sb.Append($"(?<{g}y>\\d{{4}})"); eng.Append("{D:4}"); year = year4 = true; i += 4; }
            else if (rest.StartsWith("YY", StringComparison.Ordinal)) { sb.Append($"(?<{g}y>\\d{{2}})"); eng.Append("{D:2}"); year = true; i += 2; }
            else if (rest.StartsWith("MMM", StringComparison.Ordinal))
            {
                sb.Append($"(?<{g}m>{MonthAlternation(months)})");
                eng.Append(EngineWords(months.Keys));
                month = monthName = true;
                i += 3;
            }
            else if (rest.StartsWith("MM", StringComparison.Ordinal)) { sb.Append($"(?<{g}m>\\d{{2}})"); eng.Append(EngineMonth); month = true; i += 2; }
            else if (rest.StartsWith("DD", StringComparison.Ordinal)) { sb.Append($"(?<{g}d>\\d{{2}})"); eng.Append(EngineDay); day = true; i += 2; }
            else { sb.Append(Literal(layout[i])); eng.Append(EngineLiteral(layout[i])); literals.Add(layout[i]); i++; }
        }
        if (!month || !year) throw new FormatException($"The date {name} needs a month (MM or MMM) and a year (YY or YYYY)");
        if (sb.ToString().Split($"(?<{g}").Length > (day ? 4 : 3))
            throw new FormatException($"The date {name} has a part twice");
        dates.Add(new DateField(name, g, day, monthName, year4));
        return (sb.ToString(), eng.ToString());
    }

    // the engine's format decoder (engine/src/ocr/format.cpp): at most 1024 places, and (columns x nodes)
    // up to 16e6 for the path back; a line is at most 2048 px wide at 32 px high (lines.cpp), 512 columns
    private const int EngineMaxPlaces = 1024, EngineMaxColumns = 512;
    private const double EngineMaxCells = 16e6;

    /// <summary>
    /// Why a reader that knows only <paramref name="charset"/> (code points) can never read a line of this
    /// format, so every part would fail; null when it can. Checked: each printed character (either case),
    /// each kind of field ({D} needs all ten digits, as a date or a lot number may hold any of them; {A},
    /// {X} and {?} at least one character of their kind), each month name (one spelling per month), and
    /// the size of the format for the engine's decoder (a line the format allows must fit it).
    /// <paramref name="reader"/> names the reader in the sentence ("the taught font").
    /// </summary>
    public string? ReaderProblem(IReadOnlySet<int> charset, string reader)
    {
        bool Has(char c) => charset.Contains(c) || charset.Contains(char.ToUpperInvariant(c)) || charset.Contains(char.ToLowerInvariant(c));
        var missing = _literals.Where(c => !Has(c)).ToList();
        if (missing.Count > 0) return $"{reader} cannot read {string.Join(" ", missing)}. Change the format or teach it";
        if (_kinds.Contains('D'))
        {
            var digits = "0123456789".Where(c => !charset.Contains(c)).ToList();
            if (digits.Count > 0) return $"{reader} has no {string.Join(" ", digits)}. Teach every digit";
        }
        if (_kinds.Contains('A') && !charset.Any(EngineLetter)) return $"{reader} reads no letters, but the format needs one";
        if (_kinds.Contains('X') && !charset.Any(c => EngineLetter(c) || c is >= '0' and <= '9')) return $"{reader} reads no letters or digits, but the format needs one";
        if (_kinds.Contains('?') && !charset.Any(c => c != ' ')) return $"{reader} reads no characters";
        foreach (var words in _wordSets)
            if (!words.Any(w => w.Where(c => c != ' ').All(Has)))
                return $"{reader} cannot read {string.Join(" or ", words.Where(w => !w.EndsWith(':')))}. Teach its letters";
        if (_dates.Any(d => d.MonthName))
            for (int m = 1; m <= 12; m++)
            {
                var spellings = _months.Where(kv => kv.Value == m).Select(kv => kv.Key).ToList();
                if (!spellings.Any(w => w.All(Has)))
                    return $"{reader} cannot read the month {string.Join(" or ", spellings)}. Teach its letters";
            }
        if (EngineFormat is { } ef && EngineSize(ef, charset) is var (places, nodes) &&
            (places > EngineMaxPlaces || nodes * (double)EngineMaxColumns > EngineMaxCells))
            return "The format allows too many characters to read in one line. Use fewer or shorter {?} and {X} fields";
        return null;
    }

    // a letter to the engine's format decoder (format.cpp isLetter): Latin letters and letters up to U+2000
    private static bool EngineLetter(int c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ||
        (c >= 0xC0 && c != 0xD7 && c != 0xF7 && c < 0x2000 && !(c >= 0x966 && c <= 0x96F));

    // the places of an engine format and the decoder's nodes over the charset (format.cpp compileFormat and
    // readFormat): one blank, one blank after each place, and each character a place allows (one when none)
    private static (int Places, int Nodes) EngineSize(string format, IReadOnlySet<int> charset)
    {
        int places = 0, nodes = 1;
        void Place(int classes) { places++; nodes += 1 + Math.Max(1, classes); }
        int Literal(char c) => (charset.Contains(c) ? 1 : 0) + (char.ToUpperInvariant(c) != c && charset.Contains(char.ToUpperInvariant(c)) ? 1 : 0)
                               + (char.ToLowerInvariant(c) != c && charset.Contains(char.ToLowerInvariant(c)) ? 1 : 0);
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '\\' && i + 1 < format.Length) { Place(Literal(format[++i])); continue; }
            if (c is ' ' or '\t') continue;
            if (c != '{') { Place(Literal(c)); continue; }
            int end = i + 1;
            var body = new StringBuilder();
            for (; end < format.Length && format[end] != '}'; end++)
            {
                if (format[end] == '\\' && end + 1 < format.Length) end++;
                body.Append(format[end]);
            }
            i = end;
            var b = body.ToString();
            if (b.StartsWith('|'))
            {
                foreach (var w in b[1..].Split('|')) foreach (var wc in w) if (wc != ' ') Place(Literal(wc));
                continue;
            }
            int classes = charset.Count(k => k != 0 && b[0] switch
            {
                'D' => k is >= '0' and <= '9',
                'A' => EngineLetter(k),
                'X' => EngineLetter(k) || k is >= '0' and <= '9',
                _ => k != ' ',
            });
            var m = Regex.Match(b, @":(\d+)(?:-(\d+))?$");
            int hi = !m.Success ? 1 : int.Parse(m.Groups[m.Groups[2].Success ? 2 : 1].Value, CultureInfo.InvariantCulture);
            for (int n = 0; n < hi; n++) Place(classes);
        }
        return (places, nodes);
    }

    /// <summary>The result of checking one line: its dates (a month-only date is the 1st of its month).</summary>
    public sealed record Result(bool Ok, string? Problem, string? Explain, IReadOnlyDictionary<string, (DateOnly Date, bool HasDay)> Dates)
    {
        /// <summary>The prices read, by field (MRP, PRICE, PRICE2 ...), in rupees.</summary>
        public IReadOnlyDictionary<string, decimal> Amounts { get; init; } = new Dictionary<string, decimal>();
        /// <summary>The shelf life in months ({LIFE}), when the line holds one.</summary>
        public int? ShelfMonths { get; init; }
    }

    /// <summary>Checks a read line against the format: layout, then that every date is a real date.</summary>
    public Result Check(string text)
    {
        var none = new Dictionary<string, (DateOnly, bool)>(StringComparer.OrdinalIgnoreCase);
        Match m;
        try { m = _regex.Match(text); }
        catch (RegexMatchTimeoutException) { return new Result(false, $"\"{text}\" took too long to check against {Template}", "explain.format.mismatch", none); }
        if (!m.Success) return new Result(false, $"\"{text}\" does not match {Template}", "explain.format.mismatch", none);
        var dates = new Dictionary<string, (DateOnly, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _dates)
        {
            string ys = m.Groups[d.Group + "y"].Value, ms = m.Groups[d.Group + "m"].Value, ds = m.Groups[d.Group + "d"].Value;
            int year = int.Parse(ys, CultureInfo.InvariantCulture) + (d.FourDigitYear ? 0 : 2000);
            int month = d.MonthName
                ? _months.GetValueOrDefault(ms)
                : int.Parse(ms, CultureInfo.InvariantCulture);
            int day = d.HasDay ? int.Parse(ds, CultureInfo.InvariantCulture) : 1;
            if (month < 1 || month > 12 || year < 1 || day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                string shown = string.Join("/", new[] { ds, ms, ys }.Where(s => s.Length > 0));
                return new Result(false, $"{d.Name} {shown} is not a real date", "explain.format.date_invalid", none);
            }
            dates[d.Name] = (new DateOnly(year, month, day), d.HasDay);
        }
        var amounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        int? life = null;
        foreach (var v in _values)
        {
            string s = m.Groups[v.Group].Value;
            if (v.Life)
            {
                life = int.Parse(s, CultureInfo.InvariantCulture);
                if (life is < 1 or > DateRules.MaxExpiryMonths)
                    return new Result(false, $"A shelf life of {s} months is not real", "explain.format.date_invalid", none);
                continue;
            }
            decimal amount = ParseAmount(s);
            // Rs. 0.00 is a misprint, never a price (Legal Metrology: the retail sale price is printed)
            if (amount <= 0) return new Result(false, $"{v.Name} {s} is not a real price", "explain.format.price_invalid", none);
            amounts[v.Name] = amount;
        }
        return new Result(true, null, null, dates) { Amounts = amounts, ShelfMonths = life };
    }

    private static readonly Regex MfdLabel = new(
        @"(MFD|MFG|MFGD|MFG\.?\s*(DATE|DT)|MFD\.?\s*DT|DATE\s*OF\s*(MFG|MANUFACTURE|PACKING|PKG)|MANUFACTURED(\s*ON)?|PKD|PKD\.?\s*ON|PKG|PACKED(\s*ON)?|DOM|MFG\.?\s*/\s*PKD)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExpLabel = new(
        @"(EXP|EXPY|EXPIRY(\s*DATE)?|EXP\.?\s*(DATE|DT)|EXPIRES(\s*ON)?|USE\s*(BY|BEFORE)|BEST\s*BEFORE(\s*END)?|BB|BBE)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // a printed price as teaching proposes it: an MRP (label, rupee, amount) or a rupee amount, then a unit
    // ("per g"); "N months" after a best-before label is a shelf life
    private static readonly Regex ProposePrice = new(
        @"(?<!\p{L})(?<label>" + string.Join("|", MrpLabels.OrderByDescending(w => w.Length).Select(w => Regex.Escape(w).Replace("\\ ", "\\s*"))) + @")?\s*"
        + @"(?<cur>₹|RS\.?|INR)\s*(?<amount>\d{1,2}(?:,\d{2})*,\d{3}(?:\.\d{2})?|\d{1,6}\.\d{2}|\d{1,6}(?![.,]?\d))"
        + @"(?<per>\s*(?:PER|/)\s*(?:" + string.Join("|", Units.OrderByDescending(w => w.Length).Select(w => Regex.Escape(w).Replace("\\ ", "\\s*"))) + @")(?!\p{L}))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ProposeLife = new(
        @"(?<=(?:BEST\s*BEFORE|USE\s*(?:BY|BEFORE)|EXPIRY|EXPIRES|SHELF\s*LIFE)\D{0,12})(?<!\d)\d{1,3}(?=\s*MONTHS?(?!\p{L}))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A format proposed from a line read at teaching: dates become date fields, numbers digit fields, codes
    /// that mix letters and digits letter-or-digit fields; words stay as printed. A date is named by the label
    /// before it (MFD, MFG, PKD ... = MFD; EXP, USE BY, BEST BEFORE ... = EXP), else the first of MFD, EXP,
    /// DATE3 ... not yet in `used` (pass one set for all lines of a block, so two lines never share a name).
    /// Day before month (DD/MM), as printed in India. Two numbers joined by a dot (12.50) are a date only
    /// after a date label: a price must not become a production date. A price becomes {MRP} or {PRICE}
    /// ({MRP:whole} for whole rupees, the amount itself in Indian grouping 1,250.00), its unit {PER}, and
    /// the months of "Best before 18 months from MFG" {LIFE}.
    /// </summary>
    public static string Propose(string text, ISet<string>? used = null, IEnumerable<string>? monthLanguages = null)
    {
        used ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var date = new Regex(
            @"(?<dmy>(?<!\d)\d{2}([/.\-])\d{2}\1(?:\d{4}|\d{2})(?!\d))|(?<my>(?<!\d)\d{2}([/.\-])(?:\d{4}|\d{2})(?![\d/.\-]\d))|(?<mon>(?<!\p{L})(?<name>"
            + MonthAlternation(MonthSpellings(monthLanguages)) + @")(?<sep>[ ./\-]?)(?<year>\d{4}|\d{2})(?!\d))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTime);
        var sb = new StringBuilder();
        int at = 0;
        var prices = ProposePrice.Matches(text).Cast<Match>().Concat(ProposeLife.Matches(text)).OrderBy(m => m.Index).ToList();
        var taken = prices.Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        foreach (Match m in date.Matches(text).Cast<Match>().Where(d => !taken.Any(t => d.Index < t.End && t.Index < d.Index + d.Length))
                                              .Concat(prices).OrderBy(m => m.Index))
        {
            if (m.Groups["cur"].Success || !m.Groups["dmy"].Success && !m.Groups["my"].Success && !m.Groups["mon"].Success)
            {
                sb.Append(ProposePlain(text[at..m.Index]));
                if (!m.Groups["cur"].Success) sb.Append("{LIFE}");
                else
                {
                    string amount = m.Groups["amount"].Value;
                    string field = m.Groups["label"].Success ? "MRP" : "PRICE";
                    sb.Append('{').Append(field).Append(amount.Contains(',') ? ":" + amount : amount.Contains('.') ? "" : ":whole").Append('}');
                    if (m.Groups["per"].Success) sb.Append(" {PER}");
                }
                at = m.Index + m.Length;
                continue;
            }
            string before = text[at..m.Index].TrimEnd(' ', ':', '.', '-', '/');
            string? label = MfdLabel.IsMatch(before) ? "MFD" : ExpLabel.IsMatch(before) ? "EXP" : null;
            string v = m.Value;
            if (m.Groups["my"].Success && v[2] == '.' && label is null) continue; // 12.50: a number, not a date
            string name = label is not null && !used.Contains(label)
                ? label
                : new[] { "MFD", "EXP" }.Concat(Enumerable.Range(3, 97).Select(k => $"DATE{k}")).First(n => !used.Contains(n));
            used.Add(name);
            sb.Append(ProposePlain(text[at..m.Index]));
            string layout;
            if (m.Groups["dmy"].Success) layout = $"DD{v[2]}MM{v[2]}{(v.Length == 10 ? "YYYY" : "YY")}";
            else if (m.Groups["my"].Success) layout = $"MM{v[2]}{(v.Length == 7 ? "YYYY" : "YY")}";
            else layout = $"MMM{m.Groups["sep"].Value}{(m.Groups["year"].Length == 4 ? "YYYY" : "YY")}";
            sb.Append('{').Append(name).Append(':').Append(layout).Append('}');
            at = m.Index + m.Length;
        }
        sb.Append(ProposePlain(text[at..]));
        return sb.ToString();
    }

    private static string ProposePlain(string s)
    {
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(s, @"[\p{L}\d]+|[^\p{L}\d]"))
        {
            string t = m.Value;
            bool digits = t.All(char.IsAsciiDigit), letters = t.All(char.IsLetter);
            if (digits) sb.Append($"{{D:{t.Length}}}");
            else if (!letters && t.Length > 1) sb.Append($"{{X:{t.Length}}}");
            else foreach (char c in t) sb.Append(c is '{' or '\\' ? "\\" + c : c.ToString());
        }
        return sb.ToString();
    }
}

/// <summary>Date rules of a Read text tool across its lines' dates (tool specification 7.4).</summary>
public sealed class DateRules
{
    public const int MaxExpiryMonths = 240, MaxWithinDays = 366;

    /// <summary>Name of the production date field.</summary>
    public string MfdField { get; set; } = "MFD";
    /// <summary>Name of the expiry date field.</summary>
    public string ExpField { get; set; } = "EXP";
    /// <summary>Expiry is this many months after production; 0 = not checked. When both dates have a day,
    /// expiry must be production + N months, or one day before that; otherwise compared by month.</summary>
    public int ExpiryMonths { get; set; }
    /// <summary>The production date is within this many days of the picture's time; 0 = not checked.
    /// Teaching sets it when the taught production date is the picture's date.</summary>
    public int MfdWithinDays { get; set; }

    /// <summary>Why the rules cannot be used as set; null when they can.</summary>
    public string? SetupProblem() =>
        ExpiryMonths is < 0 or > MaxExpiryMonths ? $"Expiry months must be 0 to {MaxExpiryMonths}"
        : MfdWithinDays is < 0 or > MaxWithinDays ? $"Days from today must be 0 to {MaxWithinDays}"
        : null;

    /// <summary>
    /// Null when the dates keep the rules, else a sentence for the operator. Always checked: expiry not
    /// before production, and expiry not before the picture's date (an expired print is never good).
    /// <paramref name="shelfMonths"/>: the printed shelf life ("Best before 18 months from MFG", {LIFE}). It
    /// counts from the production date, which must then be printed; the expiry it gives must not be past;
    /// it must equal <see cref="ExpiryMonths"/> when that is set, and stands in for it when not.
    /// </summary>
    public string? Check(IReadOnlyDictionary<string, (DateOnly Date, bool HasDay)> dates, DateTime now, int? shelfMonths = null)
    {
        if (SetupProblem() is { } bad) return bad;
        var today = DateOnly.FromDateTime(now);
        bool hasMfd = dates.TryGetValue(MfdField, out var mfd), hasExp = dates.TryGetValue(ExpField, out var exp);
        if ((ExpiryMonths > 0 || MfdWithinDays > 0) && !hasMfd) return $"No {MfdField} date found";
        int expiryMonths = ExpiryMonths;
        if (shelfMonths is { } life)
        {
            if (!hasMfd) return $"No {MfdField} date found to count the shelf life of {life} months from";
            if (ExpiryMonths > 0 && life != ExpiryMonths) return $"Shelf life {life} months should be {ExpiryMonths} months";
            expiryMonths = life;
            // the earlier end the rules accept (production + N months, less a day; a month-only date lasts its month)
            var end = mfd.HasDay ? mfd.Date.AddMonths(life).AddDays(-1) : EndOfMonth(mfd.Date.AddMonths(life));
            if (!hasExp && end < today)
                return $"Best before {life} months from {MfdField} {Show(mfd)} is already past ({Show((today, true))})";
        }
        if (ExpiryMonths > 0 && !hasExp) return $"No {ExpField} date found";
        if (hasExp)
        {
            // a month-only expiry runs to the end of its month
            var expLast = exp.HasDay ? exp.Date : exp.Date.AddDays(DateTime.DaysInMonth(exp.Date.Year, exp.Date.Month) - 1);
            if (expLast < today) return $"{ExpField} {Show(exp)} is already past ({today:dd/MM/yy})";
        }
        if (hasMfd && hasExp)
        {
            int months = (exp.Date.Year * 12 + exp.Date.Month) - (mfd.Date.Year * 12 + mfd.Date.Month);
            bool before = months < 0 || (months == 0 && mfd.HasDay && exp.HasDay && exp.Date < mfd.Date);
            if (before) return $"{ExpField} {Show(exp)} is before {MfdField} {Show(mfd)}";
            if (expiryMonths > 0)
            {
                bool ok = mfd.HasDay && exp.HasDay
                    ? exp.Date == mfd.Date.AddMonths(expiryMonths) || exp.Date == mfd.Date.AddMonths(expiryMonths).AddDays(-1)
                    : months == expiryMonths;
                if (!ok) return $"{ExpField} {Show(exp)} should be {expiryMonths} months after {MfdField} {Show(mfd)}";
            }
        }
        if (MfdWithinDays > 0 && hasMfd)
        {
            // a month-only date is anywhere in its month
            var first = mfd.Date;
            var last = mfd.HasDay ? first : first.AddDays(DateTime.DaysInMonth(first.Year, first.Month) - 1);
            if (last < today.AddDays(-MfdWithinDays) || first > today.AddDays(MfdWithinDays))
                return $"{MfdField} {Show(mfd)} is not within {MfdWithinDays} days of today ({today:dd/MM/yy}). Check the printer date";
        }
        return null;
    }

    private static DateOnly EndOfMonth(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    private static string Show((DateOnly Date, bool HasDay) d) => d.HasDay ? d.Date.ToString("dd/MM/yy", CultureInfo.InvariantCulture) : d.Date.ToString("MM/yy", CultureInfo.InvariantCulture);
}
