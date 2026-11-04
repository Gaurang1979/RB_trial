using System.Runtime.InteropServices;
using System.Text;

namespace VisionCore;

/// <summary>Inkjet (TIJ): solid-looking strokes of fine grain, filled before reading; never chosen by Auto. On light
/// grain Solid can read better: pick the type that gives the highest lowest-character score on good parts.</summary>
public enum PrintType { Auto = 0, Solid = 1, DotMatrix = 2, Inkjet = 3 }

/// <summary>Where a checked line sits against the place learned from good parts (Allow shift, vs_shift_state).</summary>
public enum ShiftState { Off = 0, Ok = 1, Beyond = 2, Unlearned = 3 }

public enum TextPolarity { Auto = 0, DarkOnLight = 1, LightOnDark = 2 }

/// <summary>
/// Read text safety (vs_agree): which second reader must read the same text before a read line passes. Auto: every
/// independent one there is (the second AI model, the taught font with the AI or the AI with the taught font),
/// else the first reader on the line moved by half a step. Off is for the engine's own tests and benches only:
/// the Read text tool refuses it.
/// </summary>
public enum ReadAgreement { Off = 0, Auto = 1, ShiftOnly = 2 }

/// <summary>What the second readers said about a read line (vs_agree_state).</summary>
public enum AgreeState { NotChecked = 0, Same = 1, Different = 2 }

/// <summary>The readers that read a line again (vs_second_reader, as flags).</summary>
[Flags]
public enum SecondReaders { None = 0, SecondModel = 1, TaughtFont = 2, Ai = 4, Shifted = 8 }

/// <summary>What reads the characters: the AI line recogniser, or a font taught from sample print (classical OCR).</summary>
public enum OcrReader { Ai = 0, Font = 1 }

/// <summary>Unknown (taught font only): the character was never taught, or the print looks like no taught character.</summary>
public enum CharStatus { Ok = 0, Unsure = 1, Wrong = 2, Missing = 3, Extra = 4, Unknown = 5 }

/// <summary>Why a checked character scores low (vs_print_reason).</summary>
public enum PrintReason { Ok = 0, Unclear = 1, Faint = 2, Broken = 3, Smeared = 4, Position = 5 }

/// <summary>
/// Print quality of a checked character: the recogniser's score and the measured contrast (grey levels), ink
/// (pixels in its cell of the 32 px line image) and pitch (line pixels from the previous character), each
/// scored 0..100 against limits learned from good parts (100 until learned).
/// </summary>
public sealed record CharQuality(float Recognition, float Contrast, float Ink, float Pitch,
                                 float ContrastScore, float InkScore, float PitchScore, PrintReason Reason, bool Learned)
{
    /// <summary>Verification margin in nats (tool spec 7.3.3): ln(P(expected) / P(best rival character or nothing)).
    /// Reported only, the verdict does not use it: several nats is a clear print, near 0 a look-alike explains the
    /// image almost as well, below 0 the rival is more likely. 99 for a wildcard, -99 when not placed.</summary>
    public float Margin { get; init; }
}

public readonly record struct PointF(float X, float Y);

public sealed class OcrSettings
{
    public RoiBox? Roi { get; set; }
    public PrintType Print { get; set; } = PrintType.Auto;
    public TextPolarity Polarity { get; set; } = TextPolarity.Auto;
    /// <summary>Height of a capital letter in image pixels; 0 = estimate on every image (slower).</summary>
    public int CharHeightPx { get; set; }
    public int MaxLines { get; set; } = 8;
    /// <summary>Lowest character score (0..100) that passes a check.</summary>
    public float MinQuality { get; set; } = 60;
    public float MaxSkewDeg { get; set; } = 15;
    public int TimeBudgetMs { get; set; } = 100;
    public OcrReader Reader { get; set; } = OcrReader.Ai;
    /// <summary>Check text: each line within this many image pixels of where the learned good parts had it; 0 = not checked.
    /// Set with no place learned yet, every line is NOT OK until a good part is learned.</summary>
    public float AllowShiftPx { get; set; }
    /// <summary>Dot print: dot diameter in image pixels (<see cref="OcrEngine.MeasureDots"/>); 0 = from the character height.</summary>
    public float DotSizePx { get; set; }
    /// <summary>Dot print and inkjet: 0.5–3 times the normal joining of dots or grain; 0 = normal.</summary>
    public float JoinStrength { get; set; }
    /// <summary>Dot print spaced tight: the gap between characters is no wider than a gap between the dots of one
    /// character (vs_ocr_set_tight_spacing). Off for print with a dot column or more between characters.</summary>
    public bool TightSpacing { get; set; }
    /// <summary>Read text: the second reader that must read the same text (vs_ocr_set_agreement).</summary>
    public ReadAgreement Agreement { get; set; } = ReadAgreement.Auto;
}

public sealed record OcrChar(string Expected, string Found, float Score, CharStatus Status, PointF[] Corners)
{
    /// <summary>Checked characters only; null for free reads and extra print.</summary>
    public CharQuality? Quality { get; init; }
}

public sealed record OcrLine(string Text, string Expected, bool Checked, bool Pass, float Quality,
                             IReadOnlyList<OcrChar> Chars, PointF[] Corners, float AngleDeg, float SlantDeg, float CharHeightPx)
{
    /// <summary>Read text mode: the index of the format (<see cref="OcrEngine.SetFormats"/>) the line was read with; -1 = none.</summary>
    public int Format { get; init; } = -1;
    /// <summary>Check text with Allow shift: where the line sits against the learned place.</summary>
    public ShiftState Shift { get; init; }
    /// <summary>Distance from the learned place in image pixels; -1 = unknown.</summary>
    public float ShiftPx { get; init; } = -1;
    /// <summary>The text may go on past this line (cut by the area's edge, or letters in line just past it): it never passes.</summary>
    public bool CutShort { get; init; }
    /// <summary>The line of the layout (<see cref="OcrEngine.SetLayout"/>) this line was matched to; -1 = none. In Read text
    /// mode a matched line with no format is read with as many characters as its layout line allows (Checked).</summary>
    public int LayoutLine { get; init; } = -1;
    /// <summary>Checked lines: the lowest character margin (<see cref="CharQuality.Margin"/>), how far the print is from
    /// being read as another text; -99 when the line was not found, 0 when not checked.</summary>
    public float Margin { get; init; }
    /// <summary>Read text: whether the second readers read the same text; Different = the line is NOT OK.</summary>
    public AgreeState Agreement { get; init; }
    public SecondReaders SecondReaders { get; init; }
    /// <summary>Read text: how sure the reader is of the whole line, 0..100 (calibrated); Quality is at most this.</summary>
    public float LineConfidence { get; init; } = 100;
}

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, bool Pass, int ExpectedLines, float LocateMs, float TotalMs, bool TimedOut)
{
    /// <summary>Why the lines do not fit the layout (the part then fails); "" when they fit or no layout is set.</summary>
    public string LayoutReason { get; init; } = "";
}

/// <summary>
/// One OCR / OCV tool instance over the native vs_ocr engine. Not thread-safe: use one per camera.
/// </summary>
public sealed class OcrEngine : IDisposable
{
    private IntPtr _handle;

    public OcrEngine(OcrSettings settings)
    {
        var p = ToNative(settings);
        _handle = OcrNative.Create(ref p);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("vs_ocr_create failed");
        OcrNative.SetTightSpacing(_handle, settings.TightSpacing ? 1 : 0);
        if (OcrNative.SetAgreement(_handle, (int)settings.Agreement) != 0)
        {
            Dispose();
            throw new ArgumentException($"Unknown agreement {settings.Agreement}");
        }
    }

    private IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(OcrEngine));

    public void Apply(OcrSettings settings)
    {
        var p = ToNative(settings);
        if (OcrNative.SetParams(Handle, ref p) != 0) throw new InvalidOperationException(OcrNative.LastError(Handle));
        OcrNative.SetTightSpacing(Handle, settings.TightSpacing ? 1 : 0);
        if (OcrNative.SetAgreement(Handle, (int)settings.Agreement) != 0) throw new ArgumentException($"Unknown agreement {settings.Agreement}");
    }

    /// <summary>A second AI model, trained apart from the first, read as a second reader (Agreement Auto).
    /// Decrypted in memory when protected at rest (AES-256-GCM, content key from the licence; LICENSING.md 7.4).</summary>
    public void LoadSecondModel(string onnxPath, string charsetPath)
    {
        if (!TryReadModel(onnxPath, out byte[] bytes))
        {
            if (OcrNative.LoadSecondModel(Handle, onnxPath, charsetPath) != 0)
                throw new InvalidOperationException(OcrNative.LastError(Handle));
            return;
        }
        if (OcrNative.LoadSecondModelBuffer(Handle, bytes, (nuint)bytes.Length, charsetPath) != 0)
            throw new InvalidOperationException(OcrNative.LastError(Handle));
    }

    /// <summary>Loads the line recogniser. The model is decrypted in memory when protected at rest and loaded
    /// from the buffer, so no plaintext model is on the shipped disk; a missing key or tampered file throws,
    /// so the tool stays NG rather than reading without the recogniser (requirements section 12).</summary>
    public void LoadModel(string onnxPath, string charsetPath)
    {
        if (!TryReadModel(onnxPath, out byte[] bytes))
        {
            if (OcrNative.LoadModel(Handle, onnxPath, charsetPath) != 0)
                throw new InvalidOperationException(OcrNative.LastError(Handle));
            return;
        }
        if (OcrNative.LoadModelBuffer(Handle, bytes, (nuint)bytes.Length, charsetPath) != 0)
            throw new InvalidOperationException(OcrNative.LastError(Handle));
    }

    // Reads a model file, decrypting it if protected at rest. False = the file is missing, so the caller
    // falls back to the native path load (its usual "not found" error). A protected file with no key or a
    // bad tag throws BarcodeReading.Protection.ContentProtectionException.
    private static bool TryReadModel(string onnxPath, out byte[] bytes)
    {
        try { bytes = BarcodeReading.Protection.ContentProtection.ReadArtifact(onnxPath); return true; }
        catch (FileNotFoundException) { bytes = Array.Empty<byte>(); return false; }
        catch (DirectoryNotFoundException) { bytes = Array.Empty<byte>(); return false; }
    }

    /// <summary>
    /// Teaches the taught-font reader the characters of line <paramref name="line"/> (0 = top) of an image, from
    /// <paramref name="text"/>, what is printed there (spaces ignored). Returns the characters taught; throws with
    /// the reason when the line is not found or does not cut into that many characters.
    /// </summary>
    public unsafe int TeachFont(ReadOnlySpan<byte> gray, int width, int height, int stride, int line, string text)
    {
        fixed (byte* p = gray)
        {
            int n = OcrNative.TeachFont(Handle, p, width, height, stride, line, text);
            if (n < 0) throw new InvalidOperationException(OcrNative.LastError(Handle));
            return n;
        }
    }

    /// <summary>
    /// "Teach this character": adds the one character printed at image point <paramref name="at"/> (for example the
    /// centre of a character's box from a failed run) to the taught font as <paramref name="character"/>. Throws with
    /// the reason when nothing is printed there or it clearly looks like another taught character.
    /// </summary>
    public unsafe void TeachChar(ReadOnlySpan<byte> gray, int width, int height, int stride, PointF at, string character)
    {
        fixed (byte* p = gray)
        {
            if (OcrNative.TeachChar(Handle, p, width, height, stride, at.X, at.Y, character) < 0)
                throw new InvalidOperationException(OcrNative.LastError(Handle));
        }
    }

    public void ClearFont() => OcrNative.ClearFont(Handle);

    /// <summary>Distinct characters taught.</summary>
    public int FontGlyphs => OcrNative.FontGlyphs(Handle);

    /// <summary>The taught font as text, kept with the job.</summary>
    public unsafe string GetFont()
    {
        int need = OcrNative.GetFont(Handle, null, 0);
        if (need <= 1) return "";
        var buf = new byte[need];
        fixed (byte* b = buf) OcrNative.GetFont(Handle, b, need);
        return Encoding.UTF8.GetString(buf, 0, need - 1);
    }

    public void SetFont(string font)
    {
        if (OcrNative.SetFont(Handle, font) != 0) throw new InvalidOperationException(OcrNative.LastError(Handle));
    }

    /// <summary>
    /// Adds the last run's checked characters to the print quality reference, as one good part. Only a run that
    /// passed is learned (throws otherwise). Returns the good parts learned; limits apply from 10.
    /// </summary>
    /// <summary>
    /// Dot print: the median dot diameter and dot pitch (image pixels) in the region of this image, for
    /// <see cref="OcrSettings.DotSizePx"/>. Throws when the region shows no dot print.
    /// </summary>
    public unsafe (float SizePx, float PitchPx) MeasureDots(ReadOnlySpan<byte> gray, int width, int height, int stride)
    {
        float size, pitch;
        fixed (byte* p = gray) size = OcrNative.MeasureDots(Handle, p, width, height, stride, out pitch);
        if (size <= 0) throw new InvalidOperationException(OcrNative.LastError(Handle));
        return (size, pitch);
    }

    public int LearnPrint()
    {
        int n = OcrNative.LearnPrint(Handle);
        if (n < 0) throw new InvalidOperationException(OcrNative.LastError(Handle));
        return n;
    }

    public void ClearPrint() => OcrNative.ClearPrint(Handle);

    /// <summary>Good parts in the print quality reference.</summary>
    public int PrintParts => OcrNative.PrintParts(Handle);

    /// <summary>The print quality reference as text, kept with the job.</summary>
    public unsafe string GetPrint()
    {
        int need = OcrNative.GetPrint(Handle, null, 0);
        if (need <= 1) return "";
        var buf = new byte[need];
        fixed (byte* b = buf) OcrNative.GetPrint(Handle, b, need);
        return Encoding.UTF8.GetString(buf, 0, need - 1);
    }

    public void SetPrint(string reference)
    {
        if (OcrNative.SetPrint(Handle, reference) != 0) throw new InvalidOperationException(OcrNative.LastError(Handle));
    }

    /// <summary>Expected lines top to bottom, as engine patterns (see vs_ocr.h); empty = read only.</summary>
    public void SetExpected(IReadOnlyList<string> patterns)
    {
        OcrNative.ClearExpected(Handle);
        for (int i = 0; i < patterns.Count; i++)
            if (OcrNative.SetExpected(Handle, i, patterns[i]) != 0)
                throw new ArgumentException($"line {i + 1}: {OcrNative.LastError(Handle)}");
    }

    /// <summary>
    /// Read text mode formats in engine syntax (vs_ocr_set_format), by index; a null entry has none (that
    /// line is read freely). Each format's line is then read only with the characters the format allows.
    /// </summary>
    public void SetFormats(IReadOnlyList<string?> formats)
    {
        OcrNative.ClearFormats(Handle);
        for (int i = 0; i < formats.Count; i++)
            if (formats[i] is { } f && OcrNative.SetFormat(Handle, i, f) != 0)
                throw new ArgumentException($"format {i + 1}: {OcrNative.LastError(Handle)}");
    }

    /// <summary>
    /// The block's text-line layout (vs_ocr_set_layout): each line's groups of characters apart by word gaps, lines
    /// apart by "|", e.g. "2 2 2-4 | 6". The line finder joins pieces of one line by it; each line must fit it, or the
    /// part fails with <see cref="OcrResult.LayoutReason"/>. Null or empty clears it; throws when it does not parse.
    /// </summary>
    public void SetLayout(string? layout)
    {
        if (OcrNative.SetLayout(Handle, string.IsNullOrWhiteSpace(layout) ? null : layout) != 0)
            throw new ArgumentException(OcrNative.LastError(Handle));
    }

    /// <summary>The layout as the engine keeps it (normalised: "2 2 2-4 | 6"); "" = none.</summary>
    public unsafe string GetLayout()
    {
        int need = OcrNative.GetLayout(Handle, null, 0);
        if (need <= 1) return "";
        var buf = new byte[need];
        fixed (byte* b = buf) OcrNative.GetLayout(Handle, b, need);
        return Encoding.UTF8.GetString(buf, 0, need - 1);
    }

    /// <summary>Teach: sets the layout from the last run, which must have passed (a good part); returns it. Throws when it did not.</summary>
    public string LearnLayout()
    {
        if (OcrNative.LearnLayout(Handle) < 0) throw new InvalidOperationException(OcrNative.LastError(Handle));
        return GetLayout();
    }

    /// <summary>Reference time for {TIME} and {DATE}; null = the PC clock at each run.</summary>
    public void SetClock(DateTime? local)
    {
        long secs = local is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Local)).ToUnixTimeSeconds() : 0;
        OcrNative.SetClock(Handle, secs);
    }

    public unsafe OcrResult Run(ReadOnlySpan<byte> gray, int width, int height, int stride)
    {
        fixed (byte* p = gray)
        {
            if (OcrNative.Run(Handle, p, width, height, stride) != 0)
                throw new InvalidOperationException(OcrNative.LastError(Handle));
        }
        return Collect();
    }

    public unsafe OcrResult Run(IntPtr gray, int width, int height, int stride)
    {
        if (OcrNative.Run(Handle, (byte*)gray, width, height, stride) != 0)
            throw new InvalidOperationException(OcrNative.LastError(Handle));
        return Collect();
    }

    /// <summary>The straightened image of line <paramref name="index"/> from the last run (32 px high).</summary>
    public unsafe (byte[] Pixels, int Width, int Height)? LineImage(int index)
    {
        int need = OcrNative.GetLineImage(Handle, index, null, 0, out int w);
        if (need <= 0) return null;
        var buf = new byte[need];
        fixed (byte* b = buf) OcrNative.GetLineImage(Handle, index, b, need, out w);
        return (buf, w, need / w);
    }

    private unsafe OcrResult Collect()
    {
        OcrNative.GetStats(Handle, out var st);
        var chars = new List<OcrNative.Char>();
        var quals = new List<CharQuality?>();
        for (int i = 0, n = OcrNative.CharCount(Handle); i < n; i++)
        {
            OcrNative.GetChar(Handle, i, out var c);
            chars.Add(c);
            var q = new OcrNative.CharQuality { StructSize = sizeof(OcrNative.CharQuality) };
            bool has = OcrNative.GetCharQuality(Handle, i, ref q) == 0 && q.Recognition + q.Ink > 0;
            quals.Add(has ? new CharQuality(q.Recognition, q.Contrast, q.Ink, q.Pitch, q.ContrastScore, q.InkScore, q.PitchScore,
                                            (PrintReason)q.Reason, q.Learned != 0) { Margin = q.Margin } : null);
        }
        var lines = new List<OcrLine>();
        for (int i = 0, n = OcrNative.LineCount(Handle); i < n; i++)
        {
            OcrNative.GetLine(Handle, i, out var l);
            var ext = new OcrNative.LineExt { StructSize = sizeof(OcrNative.LineExt) };
            if (OcrNative.GetLineExt(Handle, i, ref ext) != 0) ext = new OcrNative.LineExt { ShiftPx = -1 };
            var cs = new List<OcrChar>();
            for (int k = l.FirstChar; k < l.FirstChar + l.CharCount && k < chars.Count; k++)
            {
                var c = chars[k];
                cs.Add(new OcrChar(Cp(c.Codepoint), Cp(c.Found), c.Score, (CharStatus)c.Status, Quad(c.X, c.Y)) { Quality = quals[k] });
            }
            lines.Add(new OcrLine(Marshal.PtrToStringUTF8(l.Text) ?? "", Marshal.PtrToStringUTF8(l.Expected) ?? "",
                                  l.Checked != 0, l.Pass != 0, l.Quality, cs, Quad(l.X, l.Y), l.AngleDeg, l.SlantDeg, l.CharHeightPx)
                      { Format = l.Format, Shift = (ShiftState)ext.ShiftState, ShiftPx = ext.ShiftPx, CutShort = ext.CutShort != 0, Margin = ext.Margin,
                        Agreement = (AgreeState)ext.Agreement, SecondReaders = (SecondReaders)ext.SecondReaders, LineConfidence = ext.LineConfidence,
                        LayoutLine = OcrNative.LineLayout(Handle, i) });
        }
        return new OcrResult(lines, st.Pass != 0, st.ExpectedLines, st.LocateMs, st.TotalMs, st.TimedOut != 0)
            { LayoutReason = OcrNative.LayoutReason(Handle) };
    }

    private static string Cp(int c) => c == 0 ? "" : char.ConvertFromUtf32(c);

    private static unsafe PointF[] Quad(float* x, float* y) =>
        new[] { new PointF(x[0], y[0]), new PointF(x[1], y[1]), new PointF(x[2], y[2]), new PointF(x[3], y[3]) };

    private static OcrNative.Params ToNative(OcrSettings s)
    {
        OcrNative.DefaultParams(out var p);
        if (s.Roi is { } r) { p.RoiX = r.X; p.RoiY = r.Y; p.RoiW = r.Width; p.RoiH = r.Height; }
        p.Print = (int)s.Print;
        p.Polarity = (int)s.Polarity;
        p.CharHeightPx = s.CharHeightPx;
        p.MaxLines = s.MaxLines;
        p.MinQuality = s.MinQuality;
        p.MaxSkewDeg = s.MaxSkewDeg;
        p.TimeBudgetMs = s.TimeBudgetMs;
        p.Reader = (int)s.Reader;
        p.AllowShiftPx = s.AllowShiftPx;
        p.DotSizePx = s.DotSizePx;
        p.JoinStrength = s.JoinStrength;
        return p;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) OcrNative.Destroy(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>Pattern text for a literal string: escapes the engine's special characters.</summary>
    public static string Literal(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '?' or '{' or '\\') b.Append('\\');
            b.Append(c);
        }
        return b.ToString();
    }
}
