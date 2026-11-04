using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VisionCore;

public enum TextMode
{
    /// <summary>Read whatever is printed (OCR); passes when text is found.</summary>
    Read,
    /// <summary>Check every line against what should be printed (OCV).</summary>
    Check,
}

public enum LineEntry
{
    /// <summary>A fixed layout with fields: "₹ {MRP}, {BATCH}"; the operator enters the fields at batch start.</summary>
    Template,
    /// <summary>The operator types the whole line at batch start ("?" = any one character).</summary>
    FullText,
    /// <summary>Read text mode: the line must fit a format such as "LOT {A:1}{D:5}" or "EXP {EXP:MM/YY}" (TextFormat).</summary>
    Format,
}

/// <summary>Where a Check text line's expected text comes from (tool spec 7.3).</summary>
public enum ExpectedSource
{
    /// <summary>The job: a template with batch fields, or the full text typed at batch start.</summary>
    Job,
    /// <summary>A value the PLC sends with each trigger (<see cref="PlcValues.Fields"/>), printed as is.</summary>
    Plc,
    /// <summary>The text another tool gave in the same run, such as the code a Read code tool decoded.</summary>
    Tool,
}

public sealed class TextLineSpec
{
    public LineEntry Entry { get; set; } = LineEntry.Template;
    /// <summary>Check text: where the expected text comes from. Plc and Tool lines are checked against the value as
    /// printed ("?" and braces are characters); a value that is missing or empty fails the part, never passes it.</summary>
    public ExpectedSource Source { get; set; } = ExpectedSource.Job;
    /// <summary>The PLC field (Source = Plc) or the tool's name (Source = Tool).</summary>
    public string SourceName { get; set; } = "";
    /// <summary>Source = Plc with SourceName "Text": where the string lies in the PLC's text block (register, length,
    /// encoding, byte order; PLC-INTERFACE.md section 3.7). Null for every other line.</summary>
    public VisionCore.Plc.PlcTextSpec? PlcText { get; set; }
    /// <summary>Template mode: literal text with {FIELD}, {TIME:HH:mm+-3}, {DATE:MM/YY+730d} parts.
    /// Format mode: a TextFormat ("LOT {A:1}{D:5}", "EXP {EXP:MM/YY}").</summary>
    public string Template { get; set; } = "";
}

/// <summary>Read text / Check text (laser, TTO, CIJ). See engine/include/vs_ocr.h.</summary>
public sealed class TextToolConfig : ToolConfig
{
    public TextMode Mode { get; set; } = TextMode.Check;
    public List<TextLineSpec> Lines { get; set; } = new();
    public PrintType Print { get; set; } = PrintType.Auto;
    public TextPolarity Polarity { get; set; } = TextPolarity.Auto;
    public int CharHeightPx { get; set; }
    public float MinQuality { get; set; } = 60;
    public float MaxSkewDeg { get; set; } = 15;
    public int TimeBudgetMs { get; set; } = 100;
    /// <summary>The AI recogniser, or a font taught from sample print (classical OCR: fixed fonts, very fast
    /// lines, PCs without AI acceleration).</summary>
    public OcrReader Reader { get; set; } = OcrReader.Ai;
    /// <summary>The taught font (vs_ocr_get_font text) when Reader is Font; taught on the Teach page.</summary>
    public string Font { get; set; } = "";
    /// <summary>Reader Font with no teaching (tool spec 7.3): the font is drawn from an installed system font
    /// (<see cref="FontRender"/>) into <see cref="Font"/> when the job is saved; null = taught from sample print.</summary>
    public RenderedFont? Rendered { get; set; }
    /// <summary>Check text mode: print quality learned from good parts (vs_ocr_get_print text): each character's
    /// contrast, ink and pitch must stay within mean ± 4σ of those parts. Empty = only the recogniser decides.</summary>
    public string PrintReference { get; set; } = "";
    /// <summary>Check text (tool spec 7.3, Allow shift): each line within this many image pixels of where the learned
    /// good parts had it (measured from the area's corner, so it follows a fixture); 0 = not checked. Learn a good
    /// part after setting it: until then every part is NOT OK.</summary>
    public float AllowShiftPx { get; set; }
    /// <summary>Dot print (tool spec 7.5): dot diameter in image pixels, learned with <see cref="TextTool.LearnDots"/>; 0 = from the letter height.</summary>
    public float DotSizePx { get; set; }
    /// <summary>Dot print and inkjet: 0.5–3 times the normal joining of dots or grain; 0 = normal (auto).</summary>
    public float JoinStrength { get; set; }
    /// <summary>Read text mode: rules across the dates of the Format lines (expiry after production, ...).</summary>
    public DateRules Dates { get; set; } = new();
    /// <summary>
    /// Read text mode (line safety): a second reader must read the same text, each at or above the strictness, or the
    /// line is NOT OK. Auto: every independent second reader there is (a second AI model installed beside the first,
    /// or the AI model when the taught font reads first), else the reader again on the line moved by half a step.
    /// Off is refused in Read text. Check text compares with the expected text instead and ignores it.
    /// </summary>
    public ReadAgreement Agreement { get; set; } = ReadAgreement.Auto;
    /// <summary>Read text mode: the languages whose month names an MMM date accepts (TextFormat.MonthNames); empty = English.</summary>
    public List<string> MonthLanguages { get; set; } = new() { "en" };
    /// <summary>Read text mode: the tool shows what it reads and never decides OK/NG (its result passes, marked
    /// <see cref="ToolResult.ReportOnly"/>). A Read text tool that decides needs a format on every line that
    /// limits the characters (<see cref="TextTool.OpenFormat"/>): free reading passes a misread line.</summary>
    public bool ReportOnly { get; set; }
    /// <summary>
    /// The text block's layout (tool study 7.4.6): each line's groups of characters apart by word gaps, lines top to
    /// bottom apart by "|": "2 2 2-4" is one line of three groups (2, 2 and 2 to 4 characters); "6 | 4 2" two lines.
    /// Characters count as printed (punctuation too). The line finder joins the pieces of one line by it, Read text
    /// lines are read with as many characters as it allows, and a block that does not fit it fails the part with the
    /// reason. Set it here or learn it from a good part (<see cref="TextTool.LearnLayout"/>); empty = none.
    /// </summary>
    public string Layout { get; set; } = "";
    /// <summary>Dot print spaced tight: the gap between characters is no wider than a gap between the dots of one
    /// character, so the dots are joined along the line only within a character (tool study 7.5.1).</summary>
    public bool TightSpacing { get; set; }

    public override IEnumerable<FieldRequest> Fields()
    {
        if (Mode != TextMode.Check) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Lines.Count; i++)
        {
            var l = Lines[i];
            if (l.Entry == LineEntry.Format || l.Source != ExpectedSource.Job) continue;
            if (l.Entry == LineEntry.FullText)
            {
                yield return new FieldRequest(TextTemplate.FullTextKey(Name, i), $"{Name}: line {i + 1}", l.Template);
                continue;
            }
            foreach (var f in TextTemplate.FixedFields(l.Template))
                if (seen.Add(f)) yield return new FieldRequest(f, f);
        }
    }

    public OcrSettings ToSettings() => new()
    {
        Roi = Roi, Print = Print, Polarity = Polarity, MinQuality = MinQuality,
        // a rendered font knows the letter height it was drawn at
        CharHeightPx = CharHeightPx > 0 || Reader != OcrReader.Font ? CharHeightPx : FontRender.SizeOf(this),
        MaxSkewDeg = MaxSkewDeg, TimeBudgetMs = TimeBudgetMs, Reader = Reader,
        AllowShiftPx = Mode == TextMode.Check ? AllowShiftPx : 0, DotSizePx = DotSizePx, JoinStrength = JoinStrength,
        // a value the engine does not know is a setup error (SetupProblem); until then the safe default
        Agreement = Enum.IsDefined(Agreement) ? Agreement : ReadAgreement.Auto,
        TightSpacing = TightSpacing,
    };
}

/// <summary>Turns line templates and batch values into engine patterns (vs_ocr.h syntax).</summary>
public static class TextTemplate
{
    private static readonly Regex Field = new(@"\{([^}]*)\}", RegexOptions.Compiled);

    public static string FullTextKey(string tool, int line) => $"{tool}.line{line + 1}";

    /// <summary>Fields the operator must fill (everything except TIME and DATE, which come from the clock).</summary>
    public static IEnumerable<string> FixedFields(string template) =>
        Field.Matches(template).Select(m => m.Groups[1].Value).Where(n => !IsClockField(n)).Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsClockField(string body) =>
        body.StartsWith("TIME:", StringComparison.OrdinalIgnoreCase) || body.StartsWith("DATE:", StringComparison.OrdinalIgnoreCase);

    public static string Pattern(TextLineSpec spec, int line, string tool, BatchFields batch, RunContext? context = null)
    {
        if (spec.Source == ExpectedSource.Plc)
        {
            if (context?.Plc is null) throw new ExpectedTextMissingException($"Line {line + 1}: no values from the PLC for {spec.SourceName}");
            if (string.Equals(spec.SourceName, PlcValues.TextField, StringComparison.OrdinalIgnoreCase))
            {
                var t = context.Plc.GetText(spec.PlcText, out var why);
                if (string.IsNullOrWhiteSpace(t)) throw new ExpectedTextMissingException($"Line {line + 1}: PLC text: {why}");
                return OcrEngine.Literal(t);
            }
            var v = context.Plc.Get(spec.SourceName);
            if (string.IsNullOrWhiteSpace(v)) throw new ExpectedTextMissingException($"Line {line + 1}: the PLC sent no {spec.SourceName}");
            return OcrEngine.Literal(v);
        }
        if (spec.Source == ExpectedSource.Tool)
        {
            string why = $"Line {line + 1}: no result from {spec.SourceName}";
            var v = context?.TextOf(spec.SourceName, out why);
            if (string.IsNullOrWhiteSpace(v)) throw new ExpectedTextMissingException($"Line {line + 1}: {why}");
            return OcrEngine.Literal(v);
        }
        if (spec.Entry == LineEntry.FullText)
        {
            var text = batch.Get(FullTextKey(tool, line)) ?? throw new MissingFieldException($"Enter the text of {tool} line {line + 1}");
            // "?" stays a wildcard; braces and backslashes are printed characters here
            var b = new StringBuilder();
            foreach (var c in text)
            {
                if (c is '{' or '\\') b.Append('\\');
                b.Append(c);
            }
            return b.ToString();
        }
        var sb = new StringBuilder();
        int at = 0;
        foreach (Match m in Field.Matches(spec.Template))
        {
            sb.Append(OcrEngine.Literal(spec.Template[at..m.Index]));
            var name = m.Groups[1].Value;
            if (IsClockField(name)) sb.Append(m.Value);
            else sb.Append(OcrEngine.Literal(batch.Get(name) ?? throw new MissingFieldException($"Enter {name} at batch start")));
            at = m.Index + m.Length;
        }
        sb.Append(OcrEngine.Literal(spec.Template[at..]));
        return sb.ToString();
    }
}

/// <summary>The expected text of a line is not known for this part (the PLC or another tool gave none): the part fails.</summary>
public sealed class ExpectedTextMissingException(string message) : Exception(message);

public sealed class TextTool : ITool
{
    private readonly TextToolConfig _cfg;
    private readonly OcrEngine _engine;
    private string _lastPatterns = "\0";
    private RoiBox? _lastRoi;
    private readonly List<TextFormat> _formats = new();
    private readonly HashSet<int> _engineFormats = new();
    private string? _formatError;

    public TextTool(TextToolConfig cfg, ModelPaths models)
    {
        _cfg = cfg;
        // a taught font's characters are checked when the job is saved, not here: the Teach page runs a font
        // still being taught, and a character not taught yet is explained on the part (explain.ocr.unknown_char).
        // The AI reader's characters are fixed by its model, so a job saved without them (an older job, a store
        // without the models) is refused here too, with the reason, rather than failing every part unexplained
        _formatError = SetupProblem(cfg, _formats,
            cfg.Reader == OcrReader.Ai && ModelCharset(models) is { } ai ? (ai, "The AI reader") : null);
        _engine = new OcrEngine(cfg.ToSettings());
        _lastRoi = cfg.Roi;
        if (cfg.Reader == OcrReader.Font)
        {
            // a rendered font is drawn when the job is saved, never here: one not drawn or changed since fails every part
            if (_formatError is null) _formatError = FontRender.Kept(cfg);
            // a damaged font fails every part (the setup error below), never a fallback to another reader
            if (_formatError is null && !string.IsNullOrEmpty(cfg.Font))
            {
                try { _engine.SetFont(cfg.Font); }
                catch (InvalidOperationException e) { _formatError = $"The taught font cannot be used: {e.Message}. Teach it again"; }
            }
        }
        else
        {
            _engine.LoadModel(models.OcrNet, models.OcrCharset);
        }
        if (_formatError is null && cfg.Mode == TextMode.Read) _formatError = LoadSecondReader(cfg, models);
        // each format line is read under its format: only the characters the format allows at each place
        if (_formatError is null && _formats.Count > 0)
        {
            var engineFormats = _formats.Select(f => f.EngineFormat).ToList();
            try
            {
                _engine.SetFormats(engineFormats);
                for (int i = 0; i < engineFormats.Count; i++) if (engineFormats[i] is not null) _engineFormats.Add(i);
            }
            catch (ArgumentException e) { _formatError = $"The formats cannot be read: {e.Message}"; }
        }
        // a layout that cannot be used fails every part, never a silent read without it
        if (_formatError is null && !string.IsNullOrWhiteSpace(cfg.Layout))
        {
            try { _engine.SetLayout(cfg.Layout); }
            catch (ArgumentException e) { _formatError = $"The layout cannot be read: {e.Message}"; }
        }
        // a damaged reference fails every part, never a silent check without print limits
        if (_formatError is null && cfg.Mode == TextMode.Check && !string.IsNullOrEmpty(cfg.PrintReference))
        {
            try { _engine.SetPrint(cfg.PrintReference); }
            catch (InvalidOperationException e) { _formatError = $"The learned print quality cannot be used: {e.Message}. Learn it again from good parts"; }
        }
    }

    // Read text: the independent second readers there are. A second AI model beside the first (ocrnet_b.onnx);
    // with the taught font reading first, the AI model too when it reads every taught character (a character it
    // lacks would fail every part). A model that is there but cannot be loaded fails every part, never a quiet
    // fall-back to a weaker check
    private string? LoadSecondReader(TextToolConfig cfg, ModelPaths models)
    {
        if (cfg.Agreement != ReadAgreement.Auto) return null;
        try
        {
            if (cfg.Reader == OcrReader.Ai && File.Exists(models.OcrNetB)) _engine.LoadSecondModel(models.OcrNetB, models.OcrCharset);
            if (cfg.Reader == OcrReader.Font && File.Exists(models.OcrNet) && ModelCharset(models) is { } ai
                && FontCharset(cfg.Font).All(c => c == ' ' || ai.Contains(c)))
                _engine.LoadModel(models.OcrNet, models.OcrCharset);
        }
        catch (InvalidOperationException e) { return $"The second reader cannot be loaded: {e.Message}"; }
        return null;
    }

    /// <summary>Limits apply from this many good parts (tool spec 1: 10–30).</summary>
    public const int PrintPartsNeeded = 10;

    /// <summary>
    /// Check text: checks a good part and, when it passes, adds its print to the reference kept in the tool
    /// (PrintReference). A part that fails is never learned. Learn 10 to 30 good parts; limits apply from 10.
    /// </summary>
    public (bool Learned, int Parts, string Message) LearnPrint(GrayImage image, BatchFields batch, RunContext? context = null)
    {
        if (_cfg.Mode != TextMode.Check) return (false, 0, "Print quality is learned in Check text mode");
        var r = Run(image, batch, null, context);
        // a part failing only because Allow shift has no place learned yet is a good part: learning it learns the place
        bool onlyUnlearned = r.Lines is { Count: > 0 } ls && ls.Where(l => l.Checked).All(l => l.Pass || l.Shift == ShiftState.Unlearned)
                             && ls.Any(l => l.Checked);
        if (!r.Pass && !onlyUnlearned) return (false, _engine.PrintParts, $"Not learned: {r.Summary}. Learn from good parts only");
        int n;
        try { n = _engine.LearnPrint(); }
        catch (InvalidOperationException e) { return (false, _engine.PrintParts, $"Not learned: {e.Message}"); }
        _cfg.PrintReference = _engine.GetPrint();
        return (true, n, n < PrintPartsNeeded ? $"{n} good parts learned; print limits apply from {PrintPartsNeeded}" : $"{n} good parts learned");
    }

    /// <summary>
    /// Dot print (tool spec 7.5): measures the dots of a good print in the tool's area and keeps their size in the
    /// tool (DotSizePx), so dots are joined by their real size. Fails when the area shows no dot print.
    /// </summary>
    public (bool Learned, float SizePx, float PitchPx, string Message) LearnDots(GrayImage image)
    {
        try
        {
            var (size, pitch) = _engine.MeasureDots(image.Pixels, image.Width, image.Height, image.Stride);
            _cfg.DotSizePx = MathF.Round(size, 1);
            var st = _cfg.ToSettings();
            st.Roi = _lastRoi;
            _engine.Apply(st);
            return (true, size, pitch, $"Dots {size:0.0} px, {pitch:0.0} px apart");
        }
        catch (InvalidOperationException e) { return (false, 0, 0, $"Not learned: {e.Message}"); }
    }

    /// <summary>
    /// Teach (tool study 7.4.6): reads a good part with no layout and, when it passes, keeps its lines' layout in the
    /// tool (Layout): each line's groups of characters as printed. A part that fails is never learned and the layout
    /// set before is kept.
    /// </summary>
    public (bool Learned, string Layout, string Message) LearnLayout(GrayImage image, BatchFields batch, RunContext? context = null)
    {
        if (_formatError is not null) return (false, _cfg.Layout, $"Not learned: {_formatError}");
        var before = _cfg.Layout;
        bool learned = false;
        _engine.SetLayout(null);
        try
        {
            var r = Run(image, batch, null, context);
            if (!r.Pass) return (false, before, $"Not learned: {r.Summary}. Learn from a good part only");
            _cfg.Layout = _engine.LearnLayout();
            learned = true;
            return (true, _cfg.Layout, $"Layout {_cfg.Layout} learned");
        }
        catch (InvalidOperationException e) { return (false, before, $"Not learned: {e.Message}"); }
        finally
        {
            // whatever happened, the engine runs with the tool's layout: the one learned, or the one set before
            if (!learned) _engine.SetLayout(before);
        }
    }

    /// <summary>Why a layout (TextToolConfig.Layout) cannot be used; null when it can (or is empty).</summary>
    public static string? LayoutProblem(string? layout)
    {
        if (string.IsNullOrWhiteSpace(layout)) return null;
        var lines = layout.Split('|');
        if (lines.Length > 32) return "The layout has more than 32 lines";
        for (int i = 0; i < lines.Length; i++)
        {
            var groups = lines[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (groups.Length == 0) return $"Layout line {i + 1} has no groups. Write each line's groups, such as 2 2 2-4";
            if (groups.Length > 16) return $"Layout line {i + 1} has more than 16 groups";
            foreach (var g in groups)
            {
                var m = Regex.Match(g, @"^(\d{1,4})(?:-(\d{1,4}))?$");
                if (!m.Success) return $"Layout line {i + 1}: \"{g}\" is not a group size (such as 2 or 2-4)";
                int lo = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                int hi = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : lo;
                if (lo < 1 || hi < lo || hi > 64) return $"Layout line {i + 1}: group {g} must be 1 to 64 characters, the lower count first";
            }
        }
        return null;
    }

    /// <summary>Forgets the learned print quality (only the recogniser decides again).</summary>
    public void ClearPrint()
    {
        _engine.ClearPrint();
        _cfg.PrintReference = "";
    }

    public ToolConfig Config => _cfg;
    public OcrEngine Engine => _engine;

    /// <summary>Distinct characters in a taught font's text (one sample per line after the header, code point first).</summary>
    public static int FontGlyphs(string font) =>
        string.IsNullOrEmpty(font) ? 0
        : font.Split('\n').Skip(1).Select(l => l.Split(' ')[0]).Where(c => c.Length > 0).Distinct().Count();

    /// <summary>
    /// Why the tool cannot run safely as set up (every part then fails); null when it can. With
    /// <paramref name="models"/> the AI reader's characters are known too, so a line it can never read is
    /// refused (a taught font's characters are always known).
    /// </summary>
    public static string? SetupProblem(TextToolConfig cfg, ModelPaths? models = null) =>
        SetupProblem(cfg, new List<TextFormat>(), Charset(cfg, models));

    /// <summary>The characters (code points) a taught font reads: its taught ones and the space.</summary>
    public static HashSet<int> FontCharset(string font)
    {
        var set = new HashSet<int> { ' ' };
        foreach (var l in font.Split('\n').Skip(1))
            if (int.TryParse(l.Split(' ')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int c)) set.Add(c);
        return set;
    }

    /// <summary>The characters (code points) of the AI recogniser's charset file (one per line); null when it cannot be read.</summary>
    public static HashSet<int>? ModelCharset(ModelPaths models)
    {
        string[] lines;
        try { lines = File.ReadAllLines(models.OcrCharset, Encoding.UTF8); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var set = new HashSet<int>();
        foreach (var l in lines)
            if (l.Length > 0 && l.EnumerateRunes().Count() == 1) set.Add(l.EnumerateRunes().First().Value);
        return set.Count > 0 ? set : null;
    }

    // the reader's characters and its name for the operator; null when not known (AI reader without models)
    private static (HashSet<int> Chars, string Name)? Charset(TextToolConfig cfg, ModelPaths? models) =>
        cfg.Reader == OcrReader.Font && cfg.Rendered is not null ? (FontRender.Charset(cfg), "The rendered font")
        : cfg.Reader == OcrReader.Font ? (FontCharset(cfg.Font), "The taught font")
        : models is not null && ModelCharset(models) is { } ai ? (ai, "The AI reader")
        : null;

    /// <summary>
    /// Read text mode: every line is a format (a template line left from Check mode included, so it is never
    /// silently unchecked); each must compile and not be empty; a date name appears once across the lines,
    /// and every date is the production or the expiry date, so the date rules see it. Check mode needs a line,
    /// takes no format lines, and its template lines are not empty. With the reader's characters known
    /// (<paramref name="reader"/>), a format or the fixed text of a template line it can never read is
    /// refused. The compiled formats are added to `formats`.
    /// </summary>
    private static string? SetupProblem(TextToolConfig cfg, List<TextFormat> formats, (HashSet<int> Chars, string Name)? reader)
    {
        if (cfg.Dates.SetupProblem() is { } rules) return rules;
        if (LayoutProblem(cfg.Layout) is { } layout) return layout;
        // a character scores 0..100 and passes at the strictness: 100 or more never passes, 0 or less passes anything
        if (!(cfg.MinQuality is >= 1 and <= 99)) return "Strictness must be 1 to 99";
        if (FontRender.Problem(cfg) is { } rendered) return rendered;
        if (cfg.Reader == OcrReader.Font && cfg.Rendered is null && string.IsNullOrWhiteSpace(cfg.Font))
            return "No font taught. Teach the characters from sample print";
        if (cfg.Mode == TextMode.Check && !string.IsNullOrEmpty(cfg.PrintReference) && !cfg.PrintReference.StartsWith("VSPRINT1 ", StringComparison.Ordinal))
            return "The learned print quality is damaged. Learn it again from good parts";
        if (cfg.Mode == TextMode.Read && cfg.Lines.Any(l => l.Source != ExpectedSource.Job))
            return "Expected text from the PLC or another tool works only in Check text mode";
        if (cfg.Mode != TextMode.Read && cfg.ReportOnly)
            return "Report only works in Read text mode: Check text always decides OK/NG";
        // free reading passes a misread line (full system test: 22 of 103 wrong laser lines scored at or above
        // strictness 60): a Read text tool that decides OK/NG needs a format on every line
        if (cfg.Mode == TextMode.Read && !cfg.ReportOnly && cfg.Lines.Count == 0)
            return NoRule;
        // a Check text tool with no line checks nothing and would pass any part (OCV-01): refused like Read text without a format
        if (cfg.Mode != TextMode.Read && cfg.Lines.Count == 0)
            return NoLines;
        for (int i = 0; i < cfg.Lines.Count && cfg.Mode == TextMode.Check; i++)
        {
            var l = cfg.Lines[i];
            if (l.Source == ExpectedSource.Job) continue;
            if (string.IsNullOrWhiteSpace(l.SourceName))
                return $"Line {i + 1}: name the {(l.Source == ExpectedSource.Plc ? "PLC field" : "tool")} its expected text comes from";
            if (l.Source == ExpectedSource.Plc && !PlcValues.Fields.Contains(l.SourceName, StringComparer.OrdinalIgnoreCase))
                return $"Line {i + 1}: the PLC sends no {l.SourceName} (it sends {string.Join(", ", PlcValues.Fields)})";
            if (l.Source == ExpectedSource.Plc && string.Equals(l.SourceName, PlcValues.TextField, StringComparison.OrdinalIgnoreCase)
                && (l.PlcText is null ? "say where in the text block the string lies" : l.PlcText.Problem()) is { } place)
                return $"Line {i + 1}: PLC text: {place}";
            if (l.Source == ExpectedSource.Tool && string.Equals(l.SourceName, cfg.Name, StringComparison.OrdinalIgnoreCase))
                return $"Line {i + 1}: a tool cannot check its own text";
        }
        if (!Enum.IsDefined(cfg.Agreement) || (cfg.Mode == TextMode.Read && cfg.Agreement == ReadAgreement.Off))
            return "Read text needs a second reader to read the same text: set the agreement to Auto or Shifted re-read";
        if (cfg.AllowShiftPx < 0 || cfg.DotSizePx < 0 || cfg.JoinStrength < 0 || cfg.JoinStrength > 3)
            return "Allow shift and dot size cannot be negative; join strength is 0 (auto) or 0.5 to 3";
        if (cfg.Mode != TextMode.Read)
        {
            if (cfg.Lines.Any(l => l.Entry == LineEntry.Format)) return "Format lines work only in Read text mode";
            // the fixed text of a template line is printed as written (case too): a character the reader
            // lacks can never be read. Fields are filled at batch start and checked when read
            for (int i = 0; i < cfg.Lines.Count; i++)
            {
                // a line whose text comes from the PLC or another tool has no template of its own
                if (cfg.Lines[i].Entry != LineEntry.Template || cfg.Lines[i].Source != ExpectedSource.Job) continue;
                var t = cfg.Lines[i].Template;
                if (string.IsNullOrWhiteSpace(t)) return $"Line {i + 1}: the text is empty";
                if (reader is not { } r) continue;
                var fixedText = Regex.Replace(t, @"\{[^}]*\}", " ");
                var missing = fixedText.EnumerateRunes().Where(c => !Rune.IsWhiteSpace(c) && !r.Chars.Contains(c.Value)).Distinct().ToList();
                if (missing.Count > 0) return $"Line {i + 1}: {r.Name} cannot read {string.Join(" ", missing)}. Change the line or teach it";
            }
            return null;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { TextFormat.MonthSpellings(cfg.MonthLanguages); }
        catch (FormatException e) { return e.Message; }
        for (int i = 0; i < cfg.Lines.Count; i++)
        {
            var t = cfg.Lines[i].Template;
            if (string.IsNullOrWhiteSpace(t)) return $"Line {i + 1}: the format is empty";
            TextFormat f;
            try { f = TextFormat.Parse(t, cfg.MonthLanguages); }
            catch (FormatException e) { return $"Line {i + 1}: {e.Message}"; }
            if (reader is { } rd && f.ReaderProblem(rd.Chars, rd.Name) is { } unreadable) return $"Line {i + 1}: {unreadable}";
            if (!cfg.ReportOnly && OpenFormat(t))
                return $"Line {i + 1}: the format {t.Trim()} accepts any text, so a misread line passes. Add the printed text, "
                       + "digits {D}, letters {A}, a count {X:6} or a date, or set the tool to report only";
            foreach (var n in f.DateNames)
            {
                if (!names.Add(n)) return $"Line {i + 1}: the date {n} is on two lines. Give each date its own name";
                if (!n.Equals(cfg.Dates.MfdField, StringComparison.OrdinalIgnoreCase) && !n.Equals(cfg.Dates.ExpField, StringComparison.OrdinalIgnoreCase))
                    return $"Line {i + 1}: name the date {n} {cfg.Dates.MfdField} or {cfg.Dates.ExpField}, so the date rules check it";
            }
            if (f.HasShelfLife && formats.Any(g => g.HasShelfLife)) return $"Line {i + 1}: the shelf life {{LIFE}} is on two lines";
            formats.Add(f);
        }
        // a shelf life counts from the production date (Legal Metrology: best before N months from manufacture)
        if (formats.Any(f => f.HasShelfLife) && !names.Contains(cfg.Dates.MfdField))
            return $"The shelf life {{LIFE}} needs the {cfg.Dates.MfdField} date on a line, to count from";
        return null;
    }

    /// <summary>The setup problem of a Read text tool that would decide OK/NG with no format (Studio explains it
    /// with <see cref="NoRuleExplain"/>).</summary>
    public const string NoRule = "Read text with no format passes a misread line. Add a format on each line, use Check text, "
                                 + "or set the tool to report only (it then does not decide OK/NG)";
    public const string NoRuleExplain = "explain.text.no_rule";
    /// <summary>The setup problem of a Check text tool with no expected line (OCV-01): it would pass any part.</summary>
    public const string NoLines = "Check text with no line passes any part. Add the text each line should print";
    public const string ReportOnlyExplain = "explain.text.report_only";

    // fields that take any characters ({?}, {?:3}, {?:4-8}) or any number of letters and digits ({X})
    private static readonly Regex OpenField = new(@"\{\?(?::\d{1,3}(?:-\d{1,3})?)?\}|\{X\}", RegexOptions.Compiled);

    /// <summary>
    /// True when a Read text format accepts any text, so a misread passes it: nothing but {?} fields and {X} without
    /// a count, with no printed text. A format that decides has printed text, digits {D}, letters {A}, a counted
    /// {X:6}, a date, a price or {re:...} (the engineer's own rule).
    /// </summary>
    public static bool OpenFormat(string format) =>
        OpenField.Replace(format, "").All(char.IsWhiteSpace);

    /// <summary>True when the tool's result decides the part's OK/NG (<see cref="ToolArea.Decides"/>).</summary>
    public static bool Decides(ToolConfig tool) => ToolArea.Decides(tool);

    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null) => Run(image, batch, fixture, null);

    /// <summary>Runs with the PLC values and the earlier tools' results of this run (expected text from the PLC or a tool).</summary>
    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture, RunContext? context)
    {
        float? noQuality = _cfg.Mode == TextMode.Check ? 0f : null;
        // nothing checked: the lowest margin (MarginOf), so the trend has no gap on these NG parts
        var noMargin = _cfg.Mode == TextMode.Check ? new Dictionary<string, double> { ["margin"] = -99 } : new Dictionary<string, double>();
        var sw = Stopwatch.StartNew();
        if (_formatError is not null)
            return new ToolResult(_cfg.Name, false, _formatError, Array.Empty<string>(), sw.Elapsed.TotalMilliseconds, Array.Empty<Graphic>())
                { Explain = "explain.format.setup", PrintQuality = noQuality, Values = noMargin };
        // the area follows the part (the line finder handles the turn inside it)
        var roi = _cfg.Roi is { } r0 && fixture is not null ? fixture.Map(r0, image.Width, image.Height) : _cfg.Roi;
        if (roi != _lastRoi)
        {
            var st = _cfg.ToSettings();
            st.Roi = roi;
            _engine.Apply(st);
            _lastRoi = roi;
        }
        var patterns = new List<string>();
        if (_cfg.Mode == TextMode.Check)
        {
            try
            {
                for (int i = 0; i < _cfg.Lines.Count; i++) patterns.Add(TextTemplate.Pattern(_cfg.Lines[i], i, _cfg.Name, batch, context));
            }
            catch (MissingFieldException e)
            {
                return new ToolResult(_cfg.Name, false, e.Message, Array.Empty<string>(), sw.Elapsed.TotalMilliseconds, Array.Empty<Graphic>())
                    { Explain = "explain.batch.missing_field", PrintQuality = noQuality, Values = noMargin };
            }
            catch (ExpectedTextMissingException e)
            {
                return new ToolResult(_cfg.Name, false, e.Message, Array.Empty<string>(), sw.Elapsed.TotalMilliseconds, Array.Empty<Graphic>())
                    { Explain = "explain.ocv.no_expected", PrintQuality = noQuality, Values = noMargin };
            }
        }
        var key = string.Join("\n", patterns);
        if (key != _lastPatterns)
        {
            _engine.SetExpected(patterns);
            _lastPatterns = key;
        }
        _engine.SetClock(image.TakenAt);
        var r = _engine.Run(image.Pixels, image.Width, image.Height, image.Stride);

        var graphics = new List<Graphic>();
        var details = new List<string>();
        string? firstProblem = null, firstExplain = null;
        foreach (var l in r.Lines)
        {
            // Read text with no format: every line read must pass (read clearly, the same by the second reader)
            bool freeLine = _cfg.Mode == TextMode.Read && _formats.Count == 0 && string.IsNullOrWhiteSpace(_cfg.Layout) && !l.Checked && l.Text.Trim().Length > 0;
            bool bad = (l.Checked || freeLine) && !l.Pass;
            bool formatLine = l.Format >= 0;   // read under a format: CheckFormats below says what is wrong
            // Read text: read under its layout line; the layout's reason, or LayoutLineProblem below, says what is wrong
            bool layoutLine = !formatLine && l.LayoutLine >= 0 && _cfg.Mode == TextMode.Read;
            if (l.Checked || _cfg.Mode == TextMode.Read)
            {
                if (l.Corners.Any(p => p.X != 0 || p.Y != 0))
                    graphics.Add(new Graphic(l.Corners, bad ? GraphicKind.Fail : GraphicKind.Ok, l.Text));
                foreach (var c in l.Chars.Where(c => c.Status != CharStatus.Ok && c.Corners.Any(p => p.X != 0)))
                    graphics.Add(new Graphic(c.Corners, GraphicKind.Fail, c.Expected));
                details.Add(layoutLine ? $"{(l.Pass ? "OK" : "NG")}  {l.Text}   (layout line {l.LayoutLine + 1}, {l.Quality:0})"
                          : l.Checked && !formatLine ? $"{(l.Pass ? "OK" : "NG")}  {l.Text}   (expected {l.Expected}, {l.Quality:0}, margin {l.Margin:0.0})"
                          : formatLine ? $"{(l.Pass ? "OK" : "NG")}  {l.Text}   ({_formats[l.Format].Template}, {l.Quality:0})"
                          : freeLine ? $"{(l.Pass ? "OK" : "NG")}  {l.Text}   ({l.Quality:0})"
                          : $"{l.Text}   ({l.Quality:0})");
            }
            if (bad && freeLine && firstProblem is null) (firstProblem, firstExplain) = DescribeRead(l);
            else if (bad && !formatLine && !layoutLine && firstProblem is null) { firstProblem = Describe(l, _cfg.Print); firstExplain = ExplainOf(l, _cfg.Print); }
        }
        // the layout first: a block that does not fit it may hold any of the faults above only because of that
        if (r.LayoutReason.Length > 0)
        {
            firstProblem = $"The text does not fit its layout: {r.LayoutReason}";
            firstExplain = "explain.ocr.layout";
        }
        else if (_cfg.Mode == TextMode.Read && LayoutLineProblem(r.Lines) is { } weak)
        {
            (firstProblem, firstExplain) = weak;
        }
        // read under formats, the engine fails a line that does not pass its format; the formats' own check
        // below reports it, so here the tool needs only some text read
        bool pass = _cfg.Mode == TextMode.Read && _engineFormats.Count > 0 ? r.Lines.Any(l => l.Text.Trim().Length > 0) : r.Pass;
        // a layout the lines do not fit, or a line read under it that failed, fails the part whatever else passed
        if (r.LayoutReason.Length > 0 || (_cfg.Mode == TextMode.Read && LayoutLineProblem(r.Lines) is not null)) pass = false;
        string? explain = pass ? null : r.LayoutReason.Length > 0 ? "explain.ocr.layout"
            : r.Lines.Count == 0 ? "explain.ocr.no_text"
            : r.Lines.Any(l => l.Checked && !l.Pass && Unknown(l) > 0) ? "explain.ocr.unknown_char"
            : firstExplain ?? "explain.ocv.mismatch";
        if (pass && _cfg.Mode == TextMode.Read && (_formats.Count > 0 || _cfg.Dates.ExpiryMonths > 0 || _cfg.Dates.MfdWithinDays > 0))
        {
            var (problem, why, fields) = CheckFormats(r.Lines, _formats, _cfg.Dates, _cfg.MinQuality, image.TakenAt ?? DateTime.Now, _engineFormats);
            details.AddRange(fields);
            if (problem is not null) { pass = false; firstProblem = problem; explain = why; }
        }
        string summary = pass
            ? (_cfg.Mode == TextMode.Check ? "Text OK" : string.Join(" | ", r.Lines.Where(l => l.Text.Length > 0).Select(l => l.Text)))
            : firstProblem ?? (r.Lines.Count == 0 ? "No text found here" : "Text not as expected");
        // report only: what was read, and what a format found wrong, shown but never deciding the part
        if (_cfg.ReportOnly)
        {
            var read = string.Join(" | ", r.Lines.Where(l => l.Text.Length > 0).Select(l => l.Text));
            summary = "Report only: " + (read.Length > 0 ? read : "no text found") + (pass ? "" : $" ({summary})");
            return new ToolResult(_cfg.Name, true, summary, details, sw.Elapsed.TotalMilliseconds, graphics)
                { Lines = r.Lines, Explain = ReportOnlyExplain, ReportOnly = true };
        }
        return new ToolResult(_cfg.Name, pass, summary, details, sw.Elapsed.TotalMilliseconds, graphics)
        {
            Lines = r.Lines,
            Explain = explain,
            PrintQuality = _cfg.Mode == TextMode.Check ? PrintQualityOf(r.Lines, patterns.Count) : null,
            Values = _cfg.Mode == TextMode.Check ? new Dictionary<string, double> { ["margin"] = MarginOf(r.Lines, patterns.Count) }
                                                 : new Dictionary<string, double>(),
        };
    }

    /// <summary>
    /// The verification margin (tool spec 7.3.3), kept with the part's result for trends: the lowest character margin in
    /// nats over every expected line, how far the print is from being read as another text. Reported only; the verdict
    /// does not use it. -99 when an expected line was not checked (not found) or nothing was expected.
    /// </summary>
    public static float MarginOf(IReadOnlyList<OcrLine> lines, int expectedLines)
    {
        var checkedLines = lines.Where(l => l.Checked && l.Format < 0).ToList();
        if (expectedLines == 0 || checkedLines.Count < expectedLines) return -99;
        return Math.Clamp(checkedLines.Min(l => l.Margin), -99, 99);
    }

    /// <summary>
    /// PrintQuality (tool spec 7.3): the lowest character score over every expected line; 0 when an expected line
    /// was not checked (not found) or nothing was expected.
    /// </summary>
    public static float PrintQualityOf(IReadOnlyList<OcrLine> lines, int expectedLines)
    {
        var checkedLines = lines.Where(l => l.Checked && l.Format < 0).ToList();
        if (expectedLines == 0 || checkedLines.Count < expectedLines) return 0;
        return Math.Clamp(checkedLines.Min(l => l.Quality), 0, 100);
    }

    /// <summary>
    /// Read text mode: each format must fit its own read line (the first unused one, top to bottom, so a
    /// stray line of other print does not shift the rest), read at or above minQuality; then the date
    /// rules across the dates found. Returns the first problem (null = pass), its explain key and one
    /// detail per date. <paramref name="engineRead"/>: the formats the engine read lines with (OcrLine.Format):
    /// such a format takes the engine's line, which must have passed under it (no weak character, no extra mark).
    /// </summary>
    public static (string? Problem, string? Explain, List<string> Fields) CheckFormats(
        IReadOnlyList<OcrLine> lines, IReadOnlyList<TextFormat> formats, DateRules rules, float minQuality, DateTime now,
        IReadOnlySet<int>? engineRead = null)
    {
        var fields = new List<string>();
        var used = lines.Select(l => engineRead is not null && l.Format >= 0).ToArray();   // the engine's lines are taken
        var dates = new Dictionary<string, (DateOnly Date, bool HasDay)>(StringComparer.OrdinalIgnoreCase);
        int? shelfMonths = null;
        for (int k = 0; k < formats.Count; k++)
        {
            TextFormat.Result? first = null;
            int hit = -1;
            if (engineRead is not null && engineRead.Contains(k))
            {
                int own = -1;
                for (int i = 0; i < lines.Count && own < 0; i++) if (lines[i].Format == k) own = i;
                if (own < 0 || lines[own].Text.Trim().Length == 0)
                    return ($"No line fits {formats[k].Template}", "explain.format.mismatch", fields);
                var l = lines[own];
                if (!l.Pass)
                {
                    if (l.CutShort) return (CutShortText(l.Text), "explain.ocr.cut_short", fields);
                    if (l.Chars.Any(c => c.Status == CharStatus.Extra))
                        return ($"\"{l.Text}\" has print the format {formats[k].Template} has no place for", "explain.format.mismatch", fields);
                    if (Unknown(l) is > 0 and var unknown)
                        return ($"Character {unknown} is not in the taught font. Teach it", "explain.ocr.unknown_char", fields);
                    int weak = -1;
                    for (int c = 0; c < l.Chars.Count; c++)
                        if (weak < 0 || l.Chars[c].Score < l.Chars[weak].Score) weak = c;
                    return weak < 0
                        ? ($"No line fits {formats[k].Template}", "explain.format.mismatch", fields)
                        : ($"Character {weak + 1} of \"{l.Text}\" is unclear (read as {l.Chars[weak].Expected}, {l.Chars[weak].Score:0}). Check the print",
                           "explain.ocr.unsure", fields);
                }
                var res = formats[k].Check(l.Text);
                if (!res.Ok) return (res.Problem, res.Explain, fields);
                hit = own;
                first = res;
            }
            for (int i = 0; i < lines.Count && hit < 0; i++)
            {
                if (used[i] || lines[i].Text.Trim().Length == 0) continue;
                var res = formats[k].Check(lines[i].Text);
                if (res.Ok) { hit = i; first = res; }
                else first ??= res;
            }
            if (hit < 0)
                return (first?.Problem ?? $"No line fits {formats[k].Template}", first?.Explain ?? "explain.format.mismatch", fields);
            used[hit] = true;
            if (lines[hit].Quality < minQuality && Unknown(lines[hit]) is > 0 and var at)
                return ($"Character {at} is not in the taught font. Teach it", "explain.ocr.unknown_char", fields);
            if (lines[hit].CutShort)
                return (CutShortText(lines[hit].Text), "explain.ocr.cut_short", fields);
            if (lines[hit].Quality < minQuality)
                return ($"\"{lines[hit].Text}\" is unclear ({lines[hit].Quality:0}). Check the print", "explain.ocr.unsure", fields);
            foreach (var (name, d) in first!.Dates)
            {
                dates[name] = d;
                fields.Add($"{name} {(d.HasDay ? d.Date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) : d.Date.ToString("MM/yyyy", System.Globalization.CultureInfo.InvariantCulture))}");
            }
            foreach (var (name, amount) in first.Amounts)
                fields.Add($"{name} {amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            if (first.ShelfMonths is { } life)
            {
                shelfMonths = life;
                fields.Add($"LIFE {life} months");
            }
        }
        var rule = rules.Check(dates, now, shelfMonths);
        return rule is null ? (null, null, fields) : (rule, "explain.format.date_wrong", fields);
    }

    /// <summary>
    /// Read text mode: the first line read under its layout line (OcrLine.LayoutLine, no format) that failed, as the
    /// operator's sentence and its explain key; null when none did. The layout's own reason (OcrResult.LayoutReason)
    /// covers a line that does not fit it; this one a line that fits but is cut short or read unclear.
    /// </summary>
    public static (string Problem, string Explain)? LayoutLineProblem(IReadOnlyList<OcrLine> lines)
    {
        foreach (var l in lines)
        {
            if (l.LayoutLine < 0 || l.Format >= 0 || !l.Checked || l.Pass) continue;
            if (l.CutShort) return (CutShortText(l.Text), "explain.ocr.cut_short");
            if (Unknown(l) is > 0 and var unknown) return ($"Character {unknown} is not in the taught font. Teach it", "explain.ocr.unknown_char");
            if (l.Chars.Any(c => c.Status == CharStatus.Extra))
                return ($"\"{l.Text}\" has print its layout line {l.LayoutLine + 1} has no place for", "explain.ocr.layout");
            int weak = -1;
            for (int c = 0; c < l.Chars.Count; c++)
                if (weak < 0 || l.Chars[c].Score < l.Chars[weak].Score) weak = c;
            return weak < 0
                ? ($"Layout line {l.LayoutLine + 1}: nothing read", "explain.ocr.layout")
                : ($"Character {weak + 1} of \"{l.Text}\" is unclear (read as {l.Chars[weak].Expected}, {l.Chars[weak].Score:0}). Check the print",
                   "explain.ocr.unsure");
        }
        return null;
    }

    /// <summary>The place (1 = first character, spaces not counted) of the first character the taught font does not know; 0 = none.</summary>
    public static int Unknown(OcrLine l)
    {
        for (int i = 0; i < l.Chars.Count; i++)
            if (l.Chars[i].Status == CharStatus.Unknown) return i + 1;
        return 0;
    }

    /// <summary>
    /// Read text, a line read with no format that failed: why, for the operator, and its explain key. A line cut
    /// short; a character the taught font does not know; the first unclear character (a second reader reading it
    /// otherwise makes it unclear); or the whole line unclear (its line confidence).
    /// </summary>
    public static (string Text, string Explain) DescribeRead(OcrLine l)
    {
        if (l.CutShort) return (CutShortText(l.Text), "explain.ocr.cut_short");
        if (Unknown(l) is > 0 and var at) return ($"Character {at} of \"{l.Text}\" is not in the taught font. Teach it", "explain.ocr.unknown_char");
        string other = l.Agreement == AgreeState.Different ? ": the second reader reads it otherwise" : "";
        for (int i = 0; i < l.Chars.Count; i++)
            if (l.Chars[i].Status != CharStatus.Ok)
                return ($"Character {i + 1} of \"{l.Text}\" is unclear (read as {l.Chars[i].Found}, {l.Chars[i].Score:0}){other}. Check the print",
                        "explain.ocr.unsure");
        return ($"\"{l.Text}\" is unclear ({l.Quality:0}){other}. Check the print", "explain.ocr.unsure");
    }

    /// <summary>One sentence for the operator about what is wrong with a line.</summary>
    public static string Describe(OcrLine l, PrintType print = PrintType.Auto)
    {
        if (l.Corners.All(p => p.X == 0 && p.Y == 0)) return $"Line \"{l.Expected}\" not found";
        if (Unknown(l) is > 0 and var at) return $"Character {at} is not in the taught font. Teach it";
        // the area's edge or more print in line: what is missing, wrong or extra may be only that
        if (l.CutShort) return CutShortText(l.Text);
        if (l.Shift == ShiftState.Unlearned && l.Chars.All(c => c.Status == CharStatus.Ok))
            return $"Learn a good part: Allow shift does not know where \"{l.Expected}\" belongs yet";
        if (l.Shift == ShiftState.Beyond && l.Chars.All(c => c.Status == CharStatus.Ok))
            return $"\"{l.Expected}\" is {l.ShiftPx:0} px from where the good parts had it. Check the printer position";
        string exp = l.Expected.Replace(" ", ""), got = l.Text.Replace(" ", "");
        var wrong = l.Chars.Where(c => c.Status is CharStatus.Wrong or CharStatus.Missing).ToList();
        if (wrong.Count > 0 && exp != got) return $"Expected \"{l.Expected}\", found \"{l.Text}\"";
        var extra = l.Chars.Where(c => c.Status == CharStatus.Extra).Select(c => c.Found).ToList();
        if (extra.Count > 0) return $"Extra print \"{string.Concat(extra)}\" beside \"{l.Expected}\"";
        var poor = l.Chars.Where(c => c.Status != CharStatus.Ok).OrderBy(c => c.Score).FirstOrDefault();
        if (poor is not null) return $"\"{poor.Expected}\" in \"{l.Expected}\" {Fault(poor.Quality?.Reason ?? PrintReason.Unclear, print)} ({poor.Score:0})";
        return $"Expected \"{l.Expected}\", found \"{l.Text}\"";
    }

    /// <summary>The operator's sentence for a line cut short (OcrLine.CutShort).</summary>
    public static string CutShortText(string text) =>
        $"The text may go on past \"{text}\": the area's edge cuts it, or more print is in line with it. Check the area and the print";

    private static string Fault(PrintReason reason, PrintType print) => reason switch
    {
        PrintReason.Faint => "is printed faint",
        PrintReason.Broken => print is PrintType.DotMatrix or PrintType.Inkjet ? "has dots missing" : "is broken",
        PrintReason.Smeared => "is smeared",
        PrintReason.Position => "is out of place",
        _ => "is printed poorly",
    };

    /// <summary>
    /// The explain key (tool spec 7.3, 7.5) for a failed checked line: a line cut short first (what it misses
    /// or has extra may be only that), then a missing or wrong character, then extra print, then a character
    /// printed poorly (dots missing on dot print).
    /// </summary>
    public static string ExplainOf(OcrLine l, PrintType print = PrintType.Auto)
    {
        if (l.Corners.All(p => p.X == 0 && p.Y == 0)) return "explain.ocv.missing";
        if (l.CutShort) return "explain.ocr.cut_short";
        if (l.Chars.Any(c => c.Status == CharStatus.Missing)) return "explain.ocv.missing";
        if (l.Chars.Any(c => c.Status == CharStatus.Wrong)) return "explain.ocv.wrong";
        if (l.Chars.Any(c => c.Status == CharStatus.Extra)) return "explain.ocv.extra";
        var poor = l.Chars.Where(c => c.Status == CharStatus.Unsure).OrderBy(c => c.Score).FirstOrDefault();
        if (poor?.Quality?.Reason == PrintReason.Broken && print is PrintType.DotMatrix or PrintType.Inkjet) return "explain.ocr.dots_missing";
        if (poor is null && l.Shift == ShiftState.Beyond) return "explain.ocv.shift";
        if (poor is null && l.Shift == ShiftState.Unlearned) return "explain.ocv.shift_unlearned";
        return poor is null ? "explain.ocv.mismatch" : "explain.ocv.broken";
    }

    public void Dispose() => _engine.Dispose();
}
