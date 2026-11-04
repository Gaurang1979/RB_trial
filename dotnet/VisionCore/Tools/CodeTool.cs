using System.Diagnostics;
using BarcodeReading;

namespace VisionCore;

/// <summary>Read code: 1D / 2D codes with the codereader engine, optionally checked against expected text.</summary>
public sealed class CodeToolConfig : ToolConfig
{
    public CodeFormat Format { get; set; } = CodeFormat.Ean13;
    public int ExpectedCount { get; set; } = 1;
    /// <summary>Text the code must hold; may use batch fields, e.g. "{GTIN}". Empty = any content.</summary>
    public string Expected { get; set; } = "";
    public int TimeBudgetMs { get; set; } = 100;
    /// <summary>1D only: fewest and most characters (0 = any). Teaching locks both to the code's length
    /// for types without a fixed length, so a partial read of a damaged barcode fails.</summary>
    public int MinLength { get; set; }
    public int MaxLength { get; set; }
    /// <summary>Check digit of Code 39, ITF and Codabar.</summary>
    public CheckDigitRule CheckDigit { get; set; } = CheckDigitRule.AsRead;
    /// <summary>Pharmacode only: which way its bars are read, across (bars upright, the default) or down (bars
    /// lying flat). Never guessed: read the other way the bars give another valid value.</summary>
    public PharmacodeDirection PharmacodeDirection { get; set; } = PharmacodeDirection.Across;
    /// <summary>1D only, not DataBar (tool specification 5.2): the ends of the barcode must be clear for the
    /// type's quiet zone (about 10 bar widths); a mark in it, or a zone that could not be measured, fails.</summary>
    public bool QuietZoneCheck { get; set; } = true;
    /// <summary>Lowest print grade a code may have (4 = A .. 1 = D, tool specification 6.1-6.2); -1 = not graded.
    /// Without a reflectance calibration the grade is process control, not ISO verification.</summary>
    public int MinGrade { get; set; } = -1;
    /// <summary>Grade one part in this many (1 = every part). Grading costs time on fast lines; print quality
    /// changes slowly, so a sample shows its trend. Parts between samples are not judged on their grade.</summary>
    public int GradeEvery { get; set; } = 1;
    /// <summary>Grade and keep the grades with each result (for the plant trend) even without a lowest print grade.</summary>
    public bool RecordGrades { get; set; }
    /// <summary>Grading is on: a lowest print grade is set or the grades are recorded.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Grading => MinGrade >= 0 || RecordGrades;
    /// <summary>What MinGrade grades against: ISO/IEC 15415 (printed labels) or ISO/IEC TR 29158 (codes marked
    /// directly on the part: dot peen, laser, ink jet; Data Matrix and QR only, tool specification 6.3).</summary>
    public GradeStandard GradeStandard { get; set; } = GradeStandard.Iso15415;
    /// <summary>ISO/IEC TR 29158: the lighting the camera grades under, written after the grade (e.g. "30Q").</summary>
    public DpmLighting DpmLighting { get; set; } = DpmLighting.Diffuse90;
    /// <summary>ISO/IEC TR 29158: synthetic aperture in % of the cell, 80 or 50 (0 = the better grade of the two).</summary>
    public int DpmAperturePct { get; set; } = 80;
    /// <summary>Check the codes' GS1 data (tool specification 6.4); null = not checked.</summary>
    public Gs1Rules? Gs1 { get; set; }
    /// <summary>With Gs1: values some AIs must hold, as templates with batch fields, e.g. 01 = "{GTIN}", 10 = "{BATCH}".</summary>
    public Dictionary<string, string> Gs1Values { get; set; } = new();
    /// <summary>EAN/UPC only: every code must carry a 2- or 5-digit add-on. A missing or unreadable add-on fails.</summary>
    public bool AddOnRequired { get; set; }
    /// <summary>EAN/UPC only: text the add-on must hold, e.g. "05" or "{ISSUE}"; empty = any. Setting it makes
    /// the add-on required.</summary>
    public string ExpectedAddOn { get; set; } = "";

    /// <summary>The add-on is checked: required, or an expected add-on text is set.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ChecksAddOn => AddOnRequired || !string.IsNullOrWhiteSpace(ExpectedAddOn);

    public override IEnumerable<FieldRequest> Fields() =>
        TextTemplate.FixedFields(Expected)
            .Concat(TextTemplate.FixedFields(ExpectedAddOn))
            .Concat(Gs1 is null ? Enumerable.Empty<string>() : Gs1Values.Values.SelectMany(TextTemplate.FixedFields))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(f => new FieldRequest(f, f));
}

/// <summary>The print grading standard of a Read code tool's lowest grade.</summary>
public enum GradeStandard
{
    /// <summary>ISO/IEC 15415 / 15416: printed labels (tool specification 6.1, 6.2).</summary>
    Iso15415,
    /// <summary>ISO/IEC TR 29158 (AIM DPM): Data Matrix and QR marked on the part (tool specification 6.3).</summary>
    Iso29158,
}

/// <summary>The decision on what a code tool read: pass, the operator's sentence, and which codes failed.</summary>
public sealed record CodeVerdict(bool Pass, string Summary, string? Explain, IReadOnlyList<bool> CodeOk, IReadOnlyList<string> Details);

/// <summary>Teaching a code tool from what a probe read (every type, every code in the area).</summary>
public static class CodeTeach
{
    /// <summary>
    /// Locks the tool to the type read most often: its count and, for 1D types without a fixed length, the
    /// length as printed (check digit included, as the engine counts it), so a partial read fails. An ITF
    /// whose reads all carry a correct GS1 check digit also gets CheckDigit = Verify. Returns the codes
    /// of the chosen type; empty (tool unchanged) when nothing was read.
    /// </summary>
    public static IReadOnlyList<(CodeFormat Format, string Text)> Lock(CodeToolConfig code, IReadOnlyList<(CodeFormat Format, string Text)> read)
    {
        if (read.Count == 0) return Array.Empty<(CodeFormat, string)>();
        var f = read.GroupBy(c => c.Format).OrderByDescending(g => g.Count()).First().Key;
        var mine = read.Where(c => c.Format == f).ToList();
        code.Format = f;
        code.ExpectedCount = mine.Count;
        if (CodeFormats.IsPharmacode(f))
        {
            // no check digit: the bar count is its lock, and the value taught is the one it must read
            // (bars alone cannot tell a one-width code under ink spread from its all-wide twin, nor a
            // pack turned round)
            code.MinLength = mine.Min(c => CodeFormats.PharmacodeBars(c.Text));
            code.MaxLength = mine.Max(c => CodeFormats.PharmacodeBars(c.Text));
            if (string.IsNullOrWhiteSpace(code.Expected) && mine.All(c => c.Text == mine[0].Text))
                code.Expected = mine[0].Text;
        }
        else if (CodeFormats.IsLinear(f) && !CodeFormats.HasFixedLength(f))
        {
            code.MinLength = mine.Min(c => Chars(c.Text));
            code.MaxLength = mine.Max(c => Chars(c.Text));
        }
        else
        {
            code.MinLength = code.MaxLength = 0;
        }
        if (f == CodeFormat.Itf && code.CheckDigit == CheckDigitRule.AsRead && mine.All(c => Gs1Mod10Ok(c.Text)))
            code.CheckDigit = CheckDigitRule.Verify;
        // GS1 data read: its check is switched on and every AI read becomes required, measures apart (an
        // engineer's rules already set are kept)
        if (code.Gs1 is null)
        {
            var g = mine.Select(c => Gs1Data(f, c.Text)).ToList();
            if (g.All(r => r is not null))
                code.Gs1 = new Gs1Rules
                {
                    Required = g[0]!.Elements.Select(e => e.Ai)
                        .Where(ai => !IsMeasure(ai))
                        .Where(ai => g.All(r => r!.Get(ai) is not null)).ToList(),
                };
        }
        return mine;
    }

    /// <summary>
    /// The GS1 data of a code, or null when it carries none: only types that carry GS1 data, and only with a
    /// GS1 key (GTIN, SSCC, GLN, ...), so an EAN-13 starting 400 or an ITF-14 starting 10 is not GS1 element data.
    /// </summary>
    public static Gs1Result? Gs1Data(CodeFormat format, string text)
    {
        if (format is not (CodeFormat.Gs1_128 or CodeFormat.DataMatrix or CodeFormat.QrCode or CodeFormat.DataBar)) return null;
        var g = Gs1Check.Check(text, new Gs1Rules { ExpiryNotPast = false }, DateTime.Now);
        return g.Valid && g.Elements.Any(e => e.Ai is "00" or "01" or "02" or "414" or "8003" or "8004" or "8006") ? g : null;
    }

    /// <summary>Measures (31nn-36nn) change AI with the decimals of each pack, so they are never made required.</summary>
    public static bool IsMeasure(string ai) => ai.Length == 4 && ai[0] == '3' && ai[1] is >= '1' and <= '6';

    /// <summary>"10={BATCH}; 01={GTIN}" -> AI values; throws FormatException with a sentence when a part is not "AI=value".</summary>
    public static Dictionary<string, string> ParseAiValues(string text)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) throw new FormatException($"\"{part}\" is not AI=value");
            var ai = part[..eq].Trim().Trim('(', ')');
            if (!Gs1Check.Known(ai)) throw new FormatException($"{ai} is not a known AI");
            d[ai] = part[(eq + 1)..].Trim();
        }
        return d;
    }

    public static string FormatAiValues(IReadOnlyDictionary<string, string> values) =>
        string.Join("; ", values.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>"01 10 17 21" (spaces or commas) -> AIs; throws FormatException for an unknown one.</summary>
    public static List<string> ParseAis(string text)
    {
        var ais = text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim('(', ')')).ToList();
        foreach (var a in ais) if (!Gs1Check.Known(a)) throw new FormatException($"{a} is not a known AI");
        return ais.Distinct().ToList();
    }

    /// <summary>Characters as the engine counts them (Unicode code points).</summary>
    public static int Chars(string s) => s.EnumerateRunes().Count();

    /// <summary>GS1 modulo 10: the last digit checks the others (ITF-14, GTIN, SSCC).</summary>
    public static bool Gs1Mod10Ok(string digits)
    {
        if (digits.Length < 2 || !digits.All(char.IsAsciiDigit)) return false;
        int sum = 0;
        for (int k = 1; k < digits.Length; k++) sum += (digits[^(k + 1)] - '0') * (k % 2 == 1 ? 3 : 1);
        return (10 - sum % 10) % 10 == digits[^1] - '0';
    }
}

public sealed class CodeTool : ITool
{
    private readonly CodeToolConfig _cfg;
    private readonly CodeReader _reader;
    private readonly ReaderSettings _settings;
    private RoiBox? _lastRoi;
    private readonly string? _setupError;
    private long _runs;
    private readonly DpmSettings? _dpm;   // ISO/IEC TR 29158 grading, switched with the grading samples

    /// <param name="teaching">Only for the probe of Auto find and Teach, which looks for every type
    /// (<see cref="CodeFormat.Any"/>) to lock the tool to the one read; a job's tool never is (AS-12a).</param>
    public CodeTool(CodeToolConfig cfg, ModelPaths models, bool teaching = false)
    {
        _cfg = cfg;
        _setupError = SetupProblem(cfg, teaching);
        var s = _settings = new ReaderSettings
        {
            Format = cfg.Format,
            ExpectedCount = cfg.ExpectedCount,
            TimeBudgetMs = cfg.TimeBudgetMs,
            GradeQuality = cfg.Grading,
            MinLength = cfg.MinLength,
            MaxLength = cfg.MaxLength,
            CheckDigit = cfg.CheckDigit,
            PharmacodeDirection = cfg.PharmacodeDirection,
            Roi = cfg.Roi is { } r ? (r.X, r.Y, r.Width, r.Height) : null,
        };
        _reader = new CodeReader(s);
        if (cfg.Grading && cfg.GradeStandard == GradeStandard.Iso29158 && _setupError is null)
            _reader.SetDpm(_dpm = new DpmSettings { Enabled = true, Lighting = cfg.DpmLighting, AperturePct = cfg.DpmAperturePct });
        _lastRoi = cfg.Roi;
        if ((cfg.Format & (CodeFormat.QrCode | CodeFormat.DataMatrix)) != 0 && File.Exists(models.CodeNet))
            _reader.LoadModel(models.CodeNet);
    }

    /// <summary>The types that carry a 2- or 5-digit add-on.</summary>
    public const CodeFormat EanUpc = CodeFormat.Ean13 | CodeFormat.Ean8 | CodeFormat.UpcA | CodeFormat.UpcE;

    /// <summary>"Any type", including an older job's "any" (0xFFFF, 0x3FFFF), which the engine reads as every type.</summary>
    public static bool IsAnyType(CodeFormat f) => (f & (CodeFormat)0xFFFF) == (CodeFormat)0xFFFF;

    /// <summary>2 or 5 digits, as an add-on holds.</summary>
    public static bool IsAddOnText(string s) => s.Length is 2 or 5 && s.All(char.IsAsciiDigit);

    /// <summary>
    /// Why the tool cannot run safely as set up (every part then fails); null when it can. A partial read of
    /// an ITF (a scratch across the bars) decodes to a valid shorter code, so ITF runs only with a length
    /// lock, a check digit or an expected text to tell it from the real one. "Any type" is for teaching only
    /// (AS-12a): a job's tool left on it would count a stray code of a type nobody chose.
    /// </summary>
    public static string? SetupProblem(CodeToolConfig cfg, bool teaching = false)
    {
        if (cfg.Format == 0) return "Pick the code type to read";
        if (!teaching && IsAnyType(cfg.Format))
            return "Pick the code type to read: \"any type\" is for teaching only. Teach it again";
        // the length lock as the engine takes it (codereader.h): 0 = any; a lowest above the highest reads nothing
        if (cfg.MinLength < 0 || cfg.MaxLength < 0) return "Fewest and most characters must be 0 (any) or more";
        if (cfg.MaxLength > 0 && cfg.MinLength > cfg.MaxLength)
            return $"Fewest characters ({cfg.MinLength}) is more than most characters ({cfg.MaxLength}). Teach it again";
        if (cfg.ChecksAddOn && (cfg.Format & ~EanUpc) != 0)
            return "Add-on check is for EAN-13, EAN-8, UPC-A and UPC-E only: switch it off or choose one of these types";
        if (cfg.ExpectedAddOn.Trim() is { Length: > 0 } a && !TextTemplate.FixedFields(a).Any() && !IsAddOnText(a))
            return "Expected add-on must be 2 or 5 digits";
        if (cfg.Format == CodeFormat.Itf && cfg.MinLength == 0 && cfg.MaxLength == 0 && cfg.CheckDigit == CheckDigitRule.AsRead &&
            string.IsNullOrWhiteSpace(cfg.Expected))
            return "ITF needs a length, a check digit or the text it must read. Teach it again";
        if (CodeFormats.IsPharmacode(cfg.Format) && (cfg.MinLength < 2 || cfg.MaxLength < cfg.MinLength || cfg.MaxLength > 16))
            return "Pharmacode needs its number of bars. Teach it again";
        if (CodeFormats.IsPharmacode(cfg.Format) && string.IsNullOrWhiteSpace(cfg.Expected))
            return "Pharmacode needs the value it must read. Teach it again";
        // a fixed expected value (no batch field) is compared with the decimal the engine reports: it must be a
        // Pharmacode value written as the engine writes it, with as many bars as the lock lets through
        if (CodeFormats.IsPharmacode(cfg.Format) && !cfg.Expected.Contains('{'))
        {
            var value = cfg.Expected;
            int bars = CodeFormats.PharmacodeBars(value);
            if (bars == 0 || value != int.Parse(value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture))
                return $"Pharmacode values are 3 to 131070, written without leading zeros: {value} can never be read. Teach it again";
            if (bars < cfg.MinLength || bars > cfg.MaxLength)
                return $"Pharmacode {value} has {bars} bars, but the tool reads {cfg.MinLength}-{cfg.MaxLength} bars. Teach it again";
        }
        if (cfg.MinGrade is not (-1 or 1 or 2 or 3 or 4)) return "Lowest print grade must be A, B, C, D or not graded";
        if (cfg.GradeEvery is < 1 or > 10000) return "Grade every: 1 (every part) to 10000";
        if (cfg.Grading && cfg.GradeStandard == GradeStandard.Iso29158 && (cfg.Format & ~(CodeFormat.DataMatrix | CodeFormat.QrCode)) != 0)
            return "Part mark grading (ISO/IEC TR 29158) is for Data Matrix and QR only: choose one of these types or ISO 15415";
        if (cfg.GradeStandard == GradeStandard.Iso29158 && cfg.DpmAperturePct is not (0 or 50 or 80))
            return "Part mark aperture must be 80 %, 50 % or the better of the two";
        if (cfg.Gs1 is { } g)
        {
            if (g.Required.Count == 0 && cfg.Gs1Values.Count == 0)
                return "GS1 check: name the AIs the code must have (for example 01 10 17 21)";
            foreach (var ai in g.Required.Concat(cfg.Gs1Values.Keys))
                if (!Gs1Check.Known(ai)) return $"GS1 check: {ai} is not a known AI";
            foreach (var (ai, value) in cfg.Gs1Values)
                if (string.IsNullOrWhiteSpace(value)) return $"GS1 check: give the value of {Gs1Check.NameOf(ai)} ({ai}), or remove it";
        }
        return null;
    }

    public ToolConfig Config => _cfg;

    /// <summary>The grade each code is judged on: its ISO 15415 / 15416 quality, or under ISO/IEC TR 29158 its
    /// part mark grade (null when it could not be graded, which fails the code).</summary>
    public static IEnumerable<CodeQuality?> Grades(IEnumerable<DecodedCode> codes, CodeToolConfig cfg) =>
        codes.Select(c => cfg.GradeStandard == GradeStandard.Iso29158 ? c.Dpm?.ToQuality() : c.Quality);

    /// <summary>The type and text of each code of the last run (teaching locks the tool to what it read).</summary>
    public IReadOnlyList<(CodeFormat Format, string Text)> LastCodes { get; private set; } = Array.Empty<(CodeFormat, string)>();

    /// <summary>Job setup only: measure each code's size (the engine's quality measure) for the pixel check, even
    /// when the tool does not grade. The verdict ignores what was measured unless the tool grades this part.</summary>
    public bool MeasureSize { get; set; }

    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null)
    {
        var sw = Stopwatch.StartNew();
        if (_setupError is not null)
            return new ToolResult(_cfg.Name, false, _setupError, Array.Empty<string>(), 0, Array.Empty<Graphic>()) { Explain = "explain.code.setup" };
        if (_cfg.Roi is { } r0 && fixture is not null)
        {
            var moved = fixture.Map(r0, image.Width, image.Height);
            if (moved != _lastRoi)
            {
                _settings.Roi = (moved.X, moved.Y, moved.Width, moved.Height);
                _reader.Settings = _settings;
                _lastRoi = moved;
            }
        }
        string? expected = null;
        if (!string.IsNullOrWhiteSpace(_cfg.Expected))
        {
            try { expected = Fill(_cfg.Expected, batch); }
            catch (MissingFieldException e)
            {
                return new ToolResult(_cfg.Name, false, e.Message, Array.Empty<string>(), 0, Array.Empty<Graphic>())
                    { Explain = "explain.batch.missing_field" };
            }
        }
        string? expectedAddOn = null;
        if (!string.IsNullOrWhiteSpace(_cfg.ExpectedAddOn))
        {
            try { expectedAddOn = Fill(_cfg.ExpectedAddOn.Trim(), batch); }
            catch (MissingFieldException e)
            {
                return new ToolResult(_cfg.Name, false, e.Message, Array.Empty<string>(), 0, Array.Empty<Graphic>())
                    { Explain = "explain.batch.missing_field" };
            }
        }
        Dictionary<string, string> gs1Values;
        try { gs1Values = _cfg.Gs1 is null ? new() : _cfg.Gs1Values.ToDictionary(kv => kv.Key, kv => Fill(kv.Value, batch)); }
        catch (MissingFieldException e)
        {
            return new ToolResult(_cfg.Name, false, e.Message, Array.Empty<string>(), 0, Array.Empty<Graphic>())
                { Explain = "explain.batch.missing_field" };
        }
        // grading every Nth part: the reader grades only the sampled parts (switched before the decode)
        bool graded = _cfg.Grading && IsSample(_runs++, _cfg.GradeEvery);
        if (_settings.GradeQuality != (graded || MeasureSize))
        {
            _settings.GradeQuality = graded || MeasureSize;
            _reader.Settings = _settings;
            if (_dpm is not null) _reader.SetDpm(_dpm with { Enabled = graded });
        }
        var r = _reader.Decode(image.Pixels, image.Width, image.Height, image.Stride);
        LastCodes = r.Codes.Select(c => (c.Format, c.Text)).ToList();
        var grades = Grades(r.Codes, _cfg).ToList();
        var v = Judge(LastCodes, _cfg, expected, gs1Values, image.TakenAt ?? DateTime.Now, grades,
                      r.Codes.Select(c => c.SymbologyId).ToList(),
                      new Linear1d(r.Codes.Select(c => c.QuietZoneOk).ToList(), r.Stats.RejectedLength, r.Stats.RejectedCheckDigit)
                          { AddOns = r.Codes.Select(c => c.AddOn).ToList() },
                      expectedAddOn, graded, r.Stats.CopiesOver);
        var graphics = r.Codes.Select((c, i) =>
            new Graphic(c.Corners.Select(p => new PointF(p.X, p.Y)).ToArray(), v.CodeOk[i] ? GraphicKind.Ok : GraphicKind.Fail, c.Text)).ToList();
        return new ToolResult(_cfg.Name, v.Pass, v.Summary, v.Details, sw.Elapsed.TotalMilliseconds, graphics)
        {
            Explain = v.Explain,
            Values = graded ? GradeValues(grades, _cfg.MinGrade) : new Dictionary<string, double>(),
            Codes = r.Codes.Select(c => new CodeRead(c.Format, c.Text, c.Corners.Select(p => new PointF(p.X, p.Y)).ToArray(), c.Quality)
            {
                ScanAngle = c.Angle,
                CheckDigitOk = c.CheckDigitOk,
                QuietZoneOk = c.QuietZoneOk,
                AddOn = c.AddOn,
                SymbologyId = c.SymbologyId,
            }).ToList(),
        };
    }

    /// <summary>What the engine measured for the 1D rules of spec 5.2: each code's quiet zone (same order as the
    /// codes; null = not checked) and how many reads its length lock and check digit dropped. AddOns: each
    /// code's EAN/UPC add-on ("" = none or not read; null = not reported).</summary>
    public sealed record Linear1d(IReadOnlyList<bool?> QuietZoneOk, int RejectedLength, int RejectedCheckDigit)
    {
        public IReadOnlyList<string>? AddOns { get; init; }
    }

    /// <summary>Whether part number `run` (from 0) is graded when one in `every` is: the first, then every Nth.</summary>
    public static bool IsSample(long run, int every) => run % Math.Max(1, every) == 0;

    /// <summary>
    /// The grades of a graded part for its result and the plant trend: the worst over its codes of the overall
    /// grade ("grade"), of each parameter ("grade.modulation", ...), symbol contrast in % and modulation;
    /// "grade.ungraded" counts the codes that could not be graded. Grades 4 = A .. 0 = F. With the tool's lowest print
    /// grade set (<paramref name="minGrade"/> 1..4) it goes along as "grade_min", so the plant server's drift alarm
    /// warns against the line's own limit (Req 17.3 PQ-04).
    /// </summary>
    public static Dictionary<string, double> GradeValues(IReadOnlyList<CodeQuality?> qualities, int minGrade = -1)
    {
        var v = new Dictionary<string, double>();
        if (minGrade >= 1) v["grade_min"] = minGrade;
        var q = qualities.Where(x => x is not null).Select(x => x!).ToList();
        int ungraded = qualities.Count - q.Count;
        if (ungraded > 0) v["grade.ungraded"] = ungraded;
        if (q.Count == 0) return v;
        v["grade"] = q.Min(x => x.OverallGrade);
        foreach (var (name, g) in q.SelectMany(x => x.Grades))
        {
            string key = "grade." + string.Concat(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            v[key] = v.TryGetValue(key, out var old) ? Math.Min(old, g) : g;
        }
        v["symbol_contrast_pct"] = q.Min(x => x.SymbolContrast);
        v["modulation"] = q.Min(x => x.Modulation);
        return v;
    }

    /// <summary>
    /// Decides on the codes read: as many as expected (fewer or more fails: more means a foreign or extra
    /// pack in the case; an expected count of 0 sets no upper limit), each holding the expected text (when set) and, with GS1
    /// checks on, valid GS1 data with the required AIs and the expected AI values, and (1D, with the quiet
    /// zone check on) clear quiet zones. The first problem found names the part's reason. A short read
    /// names the 1D rule that dropped reads, when one did. <paramref name="symbologyIds"/>: each code's AIM
    /// identifier from the engine ("" not known); with GS1 checks on, a symbol whose identifier is not a GS1
    /// one (no FNC1 first) fails, and so does one whose identifier is not known (FNC1 in doubt is NG). Texts
    /// judged without identifiers (null: no engine read) are checked on their data alone. Without
    /// <paramref name="linear"/> (texts judged alone, no image) the quiet zone is not checked. With the add-on
    /// check on (<see cref="CodeToolConfig.ChecksAddOn"/>), each EAN/UPC code must carry an add-on (from
    /// <paramref name="linear"/>; none reported is missing) equal to <paramref name="expectedAddOn"/> when that is given.
    /// <paramref name="copiesOver"/>: codes the engine saw more often than the product's label carries (an extra pack
    /// with a copy of a code in the case merges with it); with an expected count set, any fails as an extra pack (explain.code.extra_copy).
    /// </summary>
    public static CodeVerdict Judge(IReadOnlyList<(CodeFormat Format, string Text)> codes, CodeToolConfig cfg, string? expected,
                                    IReadOnlyDictionary<string, string> gs1Values, DateTime now, IReadOnlyList<CodeQuality?>? qualities = null,
                                    IReadOnlyList<string>? symbologyIds = null, Linear1d? linear = null, string? expectedAddOn = null,
                                    bool graded = true, int copiesOver = 0)
    {
        var ok = new List<bool>();
        var details = new List<string>();
        string? problem = null, explain = null;
        string IdOf(int i) => symbologyIds is not null && i < symbologyIds.Count ? symbologyIds[i] : "";
        for (int ci = 0; ci < codes.Count; ci++)
        {
            var (format, text) = codes[ci];
            bool good = true;
            details.Add($"{CodeFormats.Name(format)}: {text}");
            if (expected is not null && text != expected)
            {
                good = false;
                if (problem is null) { problem = $"Code reads \"{text}\", expected \"{expected}\""; explain = "explain.code.wrong"; }
            }
            if (cfg.Gs1 is not null)
            {
                // with its identifier in front, the check fails a symbol without FNC1 first ("]Q1", "]d1", "]C0").
                // A GS1 Digital Link URL is carried in a plain symbol (no FNC1): its identifier is left off
                string id = IdOf(ci);
                bool url = text.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                var g = Gs1Check.Check(url ? text : id + text, cfg.Gs1, now);
                foreach (var (ai, value) in g.Elements) details.Add($"  ({ai}) {Gs1Check.NameOf(ai)}: {value}");
                var issues = g.Problems.ToList();
                // the engine reports the identifier from every decoder that decodes the symbol; a code it only
                // matched to a known text as a pattern has none, so whether it starts with FNC1 is in doubt
                if (symbologyIds is not null && id.Length == 0 && !url && issues.Count == 0)
                    issues.Add(new Gs1Problem("Code could not be checked for GS1 (FNC1 first): the reader did not decode the symbol itself", "explain.gs1.syntax"));
                foreach (var (ai, want) in gs1Values)
                    if (g.Valid && g.Get(ai) is { } got && got != want)
                        issues.Add(new Gs1Problem($"The {Gs1Check.NameOf(ai)} ({ai}) is {got}, expected {want}", "explain.gs1.wrong_value"));
                    else if (g.Valid && g.Get(ai) is null)
                        issues.Add(new Gs1Problem($"Code has no {Gs1Check.NameOf(ai)} ({ai})", "explain.gs1.missing_ai"));
                foreach (var i in issues) details.Add($"  NG {i.Message}");
                if (issues.Count > 0)
                {
                    good = false;
                    if (problem is null) { problem = issues[0].Message; explain = issues[0].Explain; }
                }
            }
            string addOn = linear?.AddOns is { } adds && ci < adds.Count ? adds[ci] : "";
            if (addOn.Length > 0) details.Add($"  Add-on: {addOn}");
            if (cfg.ChecksAddOn && (format & EanUpc) != 0)
            {
                // after the text and GS1 checks: the engine reports an add-on only when its parity check passed
                // and every read of this text agreed on it, so "" covers missing, unreadable and in doubt, all NG
                string? why = null, key = null;
                if (addOn.Length == 0) { why = "Add-on (2 or 5 digits) is missing or could not be read"; key = "explain.code1d.addon_missing"; }
                else if (expectedAddOn is not null && addOn != expectedAddOn)
                    { why = $"Add-on reads \"{addOn}\", expected \"{expectedAddOn}\""; key = "explain.code1d.addon_wrong"; }
                if (why is not null)
                {
                    good = false;
                    details.Add($"  NG {why}");
                    if (problem is null) { problem = why; explain = key; }
                }
            }
            if (linear is not null && cfg.QuietZoneCheck && CodeFormats.IsLinear(format) && format != CodeFormat.DataBar)
            {
                // after the text and GS1 checks (a wrong label is named as one); a zone not measured is
                // in doubt, so it fails too
                var qz = ci < linear.QuietZoneOk.Count ? linear.QuietZoneOk[ci] : null;
                if (qz != true)
                {
                    good = false;
                    details.Add(qz == false ? "  NG quiet zone: a mark is too close to the barcode ends" : "  NG quiet zone not measured");
                    if (problem is null)
                    {
                        problem = qz == false ? "Something is printed too close to the barcode ends" : "Quiet zone of the barcode could not be checked";
                        explain = "explain.code1d.quiet_zone";
                    }
                }
            }
            // parts between grading samples (GradeEvery > 1) are not judged on their grade
            if (cfg.MinGrade >= 0 && graded)
            {
                // graded, after the text and GS1 checks (a wrong label is named as one, not as a print
                // problem): a code that could not be graded fails too (a grade in doubt is NG)
                var q = qualities is not null && ci < qualities.Count ? qualities[ci] : null;
                string? why = null, key = null;
                if (q is null) { why = "Print grade could not be measured"; key = "explain.verify.not_graded"; }
                else
                {
                    details.Add($"  Grade {CodeQuality.Letter(q.OverallGrade)}{(q.Calibrated ? "" : " (process control)")}: " +
                                string.Join(", ", q.Grades.Select(g => $"{g.Parameter} {CodeQuality.Letter(g.Grade)}")));
                    if (q.OverallGrade < cfg.MinGrade)
                    {
                        // the overall grade is the mean of the scan grades; Rmin and the quiet zones are in Grades
                        // as their scans' means, so a parameter mean as low names it. When none is, the scans
                        // failed on different parameters
                        var worst = q.Grades.Count > 0 ? q.Grades.MinBy(g => g.Grade) : ("overall", q.OverallGrade);
                        string worstText = worst.Item2 > q.OverallGrade
                            ? "different parameters on different scans"
                            : $"{worst.Item1.ToLowerInvariant()} ({CodeQuality.Letter(worst.Item2)})";
                        why = $"Code grade is {CodeQuality.Letter(q.OverallGrade)}, minimum {CodeQuality.Letter(cfg.MinGrade)}. Worst: {worstText}";
                        key = "explain.verify.grade_low";
                    }
                }
                if (why is not null)
                {
                    good = false;
                    if (problem is null) { problem = why; explain = key; }
                }
            }
            ok.Add(good);
        }
        int need = Math.Max(1, cfg.ExpectedCount);
        // short: a read the 1D rules dropped says why. A full-length read with a wrong check digit is
        // the stronger sign (partial reads of a good barcode are common), so it is named first
        var (shortWhy, shortKey) = linear switch
        {
            { RejectedCheckDigit: > 0 } => (" (a read with a wrong check digit was dropped)", "explain.code1d.check_digit"),
            { RejectedLength: > 0 } => (" (only part of a barcode was read)", "explain.code1d.too_short"),
            _ => ("", (string?)null),
        };
        if (codes.Count == 0) return new CodeVerdict(false, "No code read" + shortWhy, shortKey ?? "explain.code.none", ok, details);
        if (problem is not null) return new CodeVerdict(false, problem, explain, ok, details);
        if (codes.Count < need) return new CodeVerdict(false, $"{codes.Count} of {need} codes read{shortWhy}", shortKey ?? "explain.code.short", ok, details);
        if (cfg.ExpectedCount > 0 && codes.Count > cfg.ExpectedCount)
            return new CodeVerdict(false, $"{codes.Count} codes read, {cfg.ExpectedCount} expected: a foreign or extra pack in the case", "explain.code.extra", ok, details);
        if (cfg.ExpectedCount > 0 && copiesOver > 0)
            return new CodeVerdict(false, $"{copiesOver} code(s) seen more often than the label carries: an extra pack with a copy of a code in the case",
                                   "explain.code.extra_copy", ok, details);
        return new CodeVerdict(true, codes.Count == 1 ? codes[0].Text : $"{codes.Count} codes read", null, ok, details);
    }

    /// <summary>The tool's expected GS1 AI values filled from the batch; null when a field is not entered.</summary>
    public static Dictionary<string, string>? FillAiValues(CodeToolConfig cfg, BatchFields batch)
    {
        try { return cfg.Gs1Values.ToDictionary(kv => kv.Key, kv => Fill(kv.Value, batch)); }
        catch (MissingFieldException) { return null; }
    }

    private static string Fill(string template, BatchFields batch) =>
        System.Text.RegularExpressions.Regex.Replace(template, @"\{([^}]*)\}", m =>
            batch.Get(m.Groups[1].Value) ?? throw new MissingFieldException($"Enter {m.Groups[1].Value} at batch start"));

    public void Dispose() => _reader.Dispose();
}
