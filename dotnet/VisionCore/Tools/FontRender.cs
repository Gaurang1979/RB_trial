using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace VisionCore;

/// <summary>
/// Check text with a rendered system font (tool spec 7.3): an installed font and the height of a capital letter,
/// instead of a font taught from sample print. Kept in <see cref="TextToolConfig.Rendered"/>.
/// </summary>
public sealed class RenderedFont
{
    /// <summary>What a new rendered font draws besides the fixed text of the lines.</summary>
    public const string DefaultCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/.:";

    /// <summary>An installed font family ("Arial", "Consolas").</summary>
    public string Family { get; set; } = "";
    public bool Bold { get; set; }
    /// <summary>Height of a capital letter as printed, in image pixels; 0 = the tool's letter height.</summary>
    public int SizePx { get; set; }
    /// <summary>The characters the print may hold (the fields' values, PLC and tool texts); the fixed text of the
    /// lines is added by itself. A character outside these is never taught, so a part printed with it is NOT OK.</summary>
    public string Characters { get; set; } = DefaultCharacters;
    /// <summary>What the font text in <see cref="TextToolConfig.Font"/> was rendered from (<see cref="FontRender.Key"/>);
    /// "" = not rendered yet. Any other value renders it again.</summary>
    public string RenderedAs { get; set; } = "";
}

/// <summary>
/// Draws the characters of a <see cref="RenderedFont"/> and teaches them to the taught-font reader as if they were
/// sample print, so the engine's classical reader and check are the same as for a taught font. The font text is
/// kept with the job, so the line PC reads with what was checked at save even without the font installed.
/// Print quality limits still come from good parts (Learn good part); until then the recogniser's score against
/// the rendered characters decides, as for a taught font.
/// </summary>
public static class FontRender
{
    public const int MinSizePx = 10;
    public const int MaxSizePx = 300;
    // characters taught per drawn line: the engine cuts one line into this many characters
    private const int PerLine = 8;

    /// <summary>The letter height the font is drawn at: its own, else the tool's.</summary>
    public static int SizeOf(TextToolConfig cfg) => cfg.Rendered is { SizePx: > 0 } r ? r.SizePx : cfg.CharHeightPx;

    /// <summary>
    /// The characters to draw, in order: the font's characters and, in Check text mode, the fixed text of the
    /// template lines (fields are filled at batch start). Spaces are never drawn.
    /// </summary>
    public static IReadOnlyList<string> Needed(TextToolConfig cfg)
    {
        var text = new StringBuilder(cfg.Rendered?.Characters ?? "");
        if (cfg.Mode == TextMode.Check)
            foreach (var l in cfg.Lines.Where(l => l.Entry == LineEntry.Template && l.Source == ExpectedSource.Job))
                text.Append(Regex.Replace(l.Template, @"\{[^}]*\}", " "));
        return text.ToString().EnumerateRunes().Where(r => !Rune.IsWhiteSpace(r) && !Rune.IsControl(r))
                   .Select(r => r.ToString()).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>What the font text is rendered from: family, weight, size and characters.</summary>
    public static string Key(TextToolConfig cfg) =>
        cfg.Rendered is { } r ? $"{r.Family.Trim()}|{(r.Bold ? "bold" : "regular")}|{SizeOf(cfg)}|{string.Concat(Needed(cfg))}" : "";

    /// <summary>The characters (code points) a rendered font reads: the ones it draws and the space.</summary>
    public static HashSet<int> Charset(TextToolConfig cfg)
    {
        var set = new HashSet<int> { ' ' };
        foreach (var c in Needed(cfg)) set.Add(char.ConvertToUtf32(c, 0));
        return set;
    }

    /// <summary>
    /// Why the rendered font cannot be used (every part would fail); null when it can: the font is installed on
    /// this PC, the size is in range and every character to check has a printable glyph in it. Does not draw.
    /// </summary>
    public static string? Problem(TextToolConfig cfg)
    {
        // the AI reader ignores it (kept so switching back keeps the settings)
        if (cfg.Rendered is not { } r || cfg.Reader != OcrReader.Font) return null;
        // drawn shapes have no dots: the engine's dot-by-dot check (8 from B, 0 from O) would not run
        if (cfg.Print is PrintType.DotMatrix or PrintType.Inkjet)
            return "A system font checks solid print (laser, TTO). Teach dot and inkjet print from sample print";
        if (string.IsNullOrWhiteSpace(r.Family)) return "Pick the font the text is printed with";
        int size = SizeOf(cfg);
        if (size is < MinSizePx or > MaxSizePx)
            return $"Give the height of a capital letter, {MinSizePx} to {MaxSizePx} image pixels";
        var needed = Needed(cfg);
        if (needed.Count == 0) return "Give the characters the print may hold";
        // a font kept with the job reads on a PC without the font installed; drawing it again needs the font
        if (!string.IsNullOrEmpty(cfg.Font) && r.RenderedAs == Key(cfg)) return null;
        try
        {
            using var face = Typeface(r);
            if (face is null) return $"The font {r.Family} is not installed on this PC";
            var missing = Unprintable(face, needed);
            return missing.Count > 0 ? $"The font {r.Family} cannot print {string.Join(" ", missing)}. Pick another font or change the text" : null;
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
        {
            return "Fonts cannot be drawn on this PC (SkiaSharp is missing)";
        }
    }

    /// <summary>
    /// Why a tool cannot read with the rendered font kept in the job; null when it can. The line never draws one
    /// itself: a font not drawn yet, or changed since it was drawn, was never checked at save.
    /// </summary>
    public static string? Kept(TextToolConfig cfg)
    {
        if (cfg.Rendered is not { } r || cfg.Reader != OcrReader.Font) return null;
        return string.IsNullOrEmpty(cfg.Font) || r.RenderedAs != Key(cfg)
            ? "The system font changed since the job was saved, or was never drawn. Draw the font and save the job again" : null;
    }

    /// <summary>
    /// Renders the font into <see cref="TextToolConfig.Font"/> when it is not rendered yet or its family, size or
    /// characters changed (characters taught by hand since are then dropped). Returns why it cannot, or null.
    /// </summary>
    public static string? Prepare(TextToolConfig cfg)
    {
        if (cfg.Rendered is not { } r || cfg.Reader != OcrReader.Font) return null;
        if (Problem(cfg) is { } p) return p;
        var key = Key(cfg);
        if (!string.IsNullOrEmpty(cfg.Font) && r.RenderedAs == key) return null;
        try
        {
            cfg.Font = Build(cfg);
            r.RenderedAs = key;
            return null;
        }
        catch (InvalidOperationException e) { return e.Message; }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
        {
            return "The OCR engine or SkiaSharp is not installed, so the rendered font cannot be made";
        }
    }

    /// <summary>Draws the characters <see cref="PerLine"/> to a line and teaches each line; returns the font text.
    /// Throws with the reason when a line does not cut into its characters (the size is too small for the font).</summary>
    public static string Build(TextToolConfig cfg)
    {
        var r = cfg.Rendered ?? throw new InvalidOperationException("No rendered font");
        int size = SizeOf(cfg);
        using var engine = new OcrEngine(new OcrSettings
        {
            Reader = OcrReader.Font, Print = PrintType.Solid, Polarity = TextPolarity.DarkOnLight,
            CharHeightPx = size, MaxLines = 1, TimeBudgetMs = 2000,
        });
        using var face = Typeface(r) ?? throw new InvalidOperationException($"The font {r.Family} is not installed on this PC");
        foreach (var line in Lines(Needed(cfg), face))
        {
            var text = string.Concat(line);
            var img = Draw(r.Family, r.Bold, size, text);
            try { engine.TeachFont(img.Pixels, img.Width, img.Height, img.Stride, 0, text); }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"The font {r.Family} at {size} px cannot be taught \"{text}\" ({e.Message}). Try a larger size or another font");
            }
        }
        return engine.GetFont();
    }

    // capitals and digits carry the line (the engine scales each taught line by its own height); every other
    // character (small letters, signs) sits between two of them, so it is taught at its place beside a capital.
    // With none needed, the first of H and 0 the font prints carries it
    private static IEnumerable<List<string>> Lines(IReadOnlyList<string> needed, SKTypeface face)
    {
        static bool Tall(string c)
        {
            var r = Rune.GetRuneAt(c, 0);
            return Rune.IsDigit(r) || Rune.IsUpper(r) || (Rune.IsLetter(r) && !Rune.IsLower(r));
        }
        var tall = needed.Where(Tall).ToList();
        var small = needed.Where(c => !Tall(c)).ToList();
        if (tall.Count == 0)
            tall.Add(new[] { "H", "0" }.Except(Unprintable(face, new[] { "H", "0" })).FirstOrDefault()
                     ?? throw new InvalidOperationException("The font has no capital H or digit 0 to set the letter height by"));
        var lines = tall.Chunk(PerLine).ToList();
        var smalls = lines.Select(_ => new Queue<string>()).ToList();
        for (int i = 0; i < small.Count; i++) smalls[i % lines.Count].Enqueue(small[i]);
        for (int k = 0; k < lines.Count; k++)
        {
            var line = new List<string>();
            foreach (var t in lines[k])
            {
                line.Add(t);
                if (smalls[k].TryDequeue(out var s)) line.Add(s);
            }
            while (smalls[k].TryDequeue(out var s)) { line.Add(s); line.Add(lines[k][0]); }
            if (!Tall(line[^1])) line.Add(lines[k][0]);
            yield return line;
        }
    }

    /// <summary>
    /// Draws text dark on light with the font, a capital letter <paramref name="capPx"/> high, the characters
    /// apart by half a letter height (<paramref name="gapEm"/> letter heights; 0 = as the font sets them).
    /// </summary>
    public static GrayImage Draw(string family, bool bold, int capPx, string text, float gapEm = 0.5f)
    {
        using var face = Typeface(new RenderedFont { Family = family, Bold = bold })
                         ?? throw new InvalidOperationException($"The font {family} is not installed on this PC");
        using var font = new SKFont(face, 100);
        float capAt100 = font.Metrics.CapHeight > 0 ? font.Metrics.CapHeight : Ink(font, "H").Height;
        font.Size = 100f * capPx / Math.Max(1f, capAt100);
        font.Edging = SKFontEdging.Antialias;
        // a family without a bold face gives its regular one: thicken it instead
        font.Embolden = bold && face.FontWeight < (int)SKFontStyleWeight.SemiBold;
        var chars = text.EnumerateRunes().Select(c => c.ToString()).ToList();
        float gap = gapEm * capPx, margin = capPx;
        float width = margin * 2;
        foreach (var c in chars) width += (gapEm > 0 ? Math.Max(Advance(font, c), Ink(font, c).Right) : Advance(font, c)) + gap;
        float height = margin * 2 + font.Metrics.Descent - font.Metrics.Ascent;
        int w = (int)Math.Ceiling(width), h = (int)Math.Ceiling(height);
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            float x = margin, y = margin - font.Metrics.Ascent;
            if (gapEm > 0)
                foreach (var c in chars)
                {
                    canvas.DrawText(c, x, y, font, paint);
                    x += Math.Max(Advance(font, c), Ink(font, c).Right) + gap;
                }
            else canvas.DrawText(text, x, y, font, paint);
        }
        var px = new byte[w * h];
        var src = bmp.Bytes;
        for (int i = 0; i < px.Length; i++) px[i] = src[i * 4];
        return new GrayImage(px, w, h);
    }

    /// <summary>Installed font families on this PC, for the font picker.</summary>
    public static IReadOnlyList<string> Families() =>
        SKFontManager.Default.FontFamilies.Where(f => !f.StartsWith('.')).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();

    // the installed face, never a stand-in: Skia falls back to another font for a name it does not know
    private static SKTypeface? Typeface(RenderedFont r)
    {
        var face = SKFontManager.Default.MatchFamily(r.Family.Trim(), r.Bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        if (face is not null && !string.Equals(face.FamilyName, r.Family.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            face.Dispose();
            return null;
        }
        return face;
    }

    // characters with no glyph in the face, or a glyph that leaves no ink
    private static List<string> Unprintable(SKTypeface face, IReadOnlyList<string> chars)
    {
        using var font = new SKFont(face, 100);
        return chars.Where(c => Glyphs(font, c).Any(g => g == 0) || Ink(font, c).IsEmpty).ToList();
    }

    private static ushort[] Glyphs(SKFont font, string text)
    {
        var g = new ushort[font.CountGlyphs(text)];
        font.GetGlyphs(text, g);
        return g;
    }

    private static float Advance(SKFont font, string text) => font.MeasureText(Glyphs(font, text));

    private static SKRect Ink(SKFont font, string text)
    {
        font.MeasureText(Glyphs(font, text), out var bounds);
        return bounds;
    }
}
