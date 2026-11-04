using System.Globalization;

namespace VisionCore;

/// <summary>
/// Teaching a taught font (classical OCR) after the first line: the reader proposes what it reads and the
/// engineer types only the characters it is unsure of, and characters that failed in a run are taught one
/// by one ("Teach this character") without teaching the whole line again.
/// </summary>
public static class FontTeach
{
    /// <summary>One character of a line the font read: what it read, and whether it is sure (OK at the tool's strictness).</summary>
    public sealed record Ask(string Read, bool Sure);

    /// <summary>A character of a run that did not pass, at the point to teach it from.</summary>
    /// <param name="Line">Line index in the run (0 = top).</param>
    /// <param name="Should">The expected character of a checked line ("" when only read): shown, never filled in for the engineer.</param>
    public sealed record Candidate(int Line, OcrChar Char, PointF At, string Should);

    /// <summary>The characters of a read line, each sure or not. A character never taught is never sure.</summary>
    public static IReadOnlyList<Ask> Asks(OcrLine line) =>
        line.Chars.Select(c => new Ask(c.Found, c.Status == CharStatus.Ok && c.Found.Length > 0)).ToList();

    /// <summary>
    /// The cells as the engineer left them (each sure one starts as read, each unsure one empty) joined into the
    /// text to teach, or null with the first cell (0-based) that is empty or holds more than one character.
    /// </summary>
    public static string? Compose(IReadOnlyList<string?> cells, out int badCell)
    {
        badCell = -1;
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < cells.Count; i++)
        {
            var c = (cells[i] ?? "").Trim();
            if (new StringInfo(c).LengthInTextElements != 1) { badCell = i; return null; }
            text.Append(c);
        }
        return cells.Count == 0 ? null : text.ToString();
    }

    /// <summary>
    /// The characters of a run worth teaching: every character below OK that has a place on the image (a
    /// missing one has none), the ones never taught first.
    /// </summary>
    public static IReadOnlyList<Candidate> Candidates(IReadOnlyList<OcrLine> lines)
    {
        var list = new List<Candidate>();
        for (int k = 0; k < lines.Count; k++)
            foreach (var c in lines[k].Chars)
                if (c.Status is not (CharStatus.Ok or CharStatus.Missing) && c.Corners.Any(p => p.X != 0 || p.Y != 0))
                    list.Add(new Candidate(k, c, Centre(c), lines[k].Checked ? c.Expected : ""));
        return list.OrderBy(c => c.Char.Status == CharStatus.Unknown ? 0 : 1).ToList();
    }

    public static PointF Centre(OcrChar c) => new(c.Corners.Average(p => p.X), c.Corners.Average(p => p.Y));
}
