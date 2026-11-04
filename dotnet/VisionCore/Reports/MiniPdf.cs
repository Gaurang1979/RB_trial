using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace VisionCore.Reports;

/// <summary>
/// A small PDF 1.4 writer for reports: A4 pages, the standard Helvetica fonts (no font embedded),
/// text, lines, filled boxes and 8-bit grey images (compressed). Text is written in the PDF's
/// WinAnsi encoding: characters outside it print as "?". Coordinates are points from the page's
/// top-left corner (the writer turns them into PDF's bottom-left ones).
/// </summary>
public sealed class MiniPdf
{
    public const float PageWidth = 595.28f, PageHeight = 841.89f; // A4

    private readonly List<StringBuilder> _pages = new();
    private readonly List<(byte[] Data, int Width, int Height, bool Rgb)> _images = new();
    private StringBuilder Page => _pages.Count > 0 ? _pages[^1] : throw new InvalidOperationException("Add a page first");

    public int PageCount => _pages.Count;

    public void AddPage() => _pages.Add(new StringBuilder());

    /// <summary>Text at (x, y), y the baseline from the top; bold uses Helvetica-Bold.</summary>
    public void Text(float x, float y, string text, float size = 10, bool bold = false)
    {
        Page.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(PageHeight - y)} Td (")
            .Append(Escape(text)).Append(") Tj ET\n");
    }

    /// <summary>White text (on a dark box).</summary>
    public void TextWhite(float x, float y, string text, float size = 10, bool bold = false)
    {
        Page.Append(CultureInfo.InvariantCulture, $"1 g BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(PageHeight - y)} Td (")
            .Append(Escape(text)).Append(") Tj ET 0 g\n");
    }

    /// <summary>The width of text in points (Helvetica's widths for the common characters, an average otherwise).</summary>
    public static float TextWidth(string text, float size, bool bold = false) =>
        text.Sum(c => (bold ? BoldWidth(c) : Width(c)) / 1000f * size);

    public void Line(float x0, float y0, float x1, float y1, float width = 0.5f, float grey = 0f)
    {
        Page.Append(CultureInfo.InvariantCulture,
            $"{N(width)} w {N(grey)} G {N(x0)} {N(PageHeight - y0)} m {N(x1)} {N(PageHeight - y1)} l S\n");
    }

    /// <summary>A line through the points (a graph), in grey or colour (r, g, b 0..1).</summary>
    public void Polyline(IReadOnlyList<(float X, float Y)> points, float width = 0.7f, float r = 0, float g = 0, float b = 0)
    {
        if (points.Count < 2) return;
        var sb = Page.Append(CultureInfo.InvariantCulture, $"{N(width)} w {N(r)} {N(g)} {N(b)} RG {N(points[0].X)} {N(PageHeight - points[0].Y)} m");
        for (int i = 1; i < points.Count; i++) sb.Append(CultureInfo.InvariantCulture, $" {N(points[i].X)} {N(PageHeight - points[i].Y)} l");
        sb.Append(" S 0 G\n");
    }

    /// <summary>A circle (outline, or filled), in colour (r, g, b 0..1), drawn as a 16-sided polygon.</summary>
    public void Circle(float cx, float cy, float radius, float r, float g, float b, bool fill, float width = 0.5f)
    {
        var sb = Page.Append(CultureInfo.InvariantCulture, $"{N(width)} w {N(r)} {N(g)} {N(b)} RG {N(r)} {N(g)} {N(b)} rg");
        for (int i = 0; i <= 16; i++)
        {
            double a = i * Math.PI / 8;
            float x = cx + radius * (float)Math.Cos(a), y = PageHeight - (cy + radius * (float)Math.Sin(a));
            sb.Append(CultureInfo.InvariantCulture, $" {N(x)} {N(y)} {(i == 0 ? "m" : "l")}");
        }
        sb.Append(fill ? " f" : " S").Append(" 0 G 0 g\n");
    }

    /// <summary>A box filled with a colour (r, g, b 0..1), from its top-left corner.</summary>
    public void BoxRgb(float x, float y, float w, float h, float r, float g, float b)
    {
        Page.Append(CultureInfo.InvariantCulture, $"{N(r)} {N(g)} {N(b)} rg {N(x)} {N(PageHeight - y - h)} {N(w)} {N(h)} re f 0 g\n");
    }

    /// <summary>A box filled with grey (0 = black, 1 = white), from its top-left corner.</summary>
    public void Box(float x, float y, float w, float h, float grey)
    {
        Page.Append(CultureInfo.InvariantCulture, $"{N(grey)} g {N(x)} {N(PageHeight - y - h)} {N(w)} {N(h)} re f 0 g\n");
    }

    /// <summary>An 8-bit grey image (rows top to bottom, no padding) drawn in a box of w x h points.</summary>
    public void Image(byte[] gray, int width, int height, float x, float y, float w, float h) => Image(gray, width, height, false, x, y, w, h);

    /// <summary>An 8-bit RGB image (3 bytes a pixel, rows top to bottom) drawn in a box of w x h points.</summary>
    public void ImageRgb(byte[] rgb, int width, int height, float x, float y, float w, float h) => Image(rgb, width, height, true, x, y, w, h);

    private void Image(byte[] data, int width, int height, bool rgb, float x, float y, float w, float h)
    {
        if (width <= 0 || height <= 0 || data.Length < width * height * (rgb ? 3 : 1)) throw new ArgumentException("image data shorter than its size");
        _images.Add((data, width, height, rgb));
        Page.Append(CultureInfo.InvariantCulture,
            $"q {N(w)} 0 0 {N(h)} {N(x)} {N(PageHeight - y - h)} cm /Im{_images.Count} Do Q\n");
    }

    public byte[] ToBytes()
    {
        if (_pages.Count == 0) AddPage();
        using var ms = new MemoryStream();
        var offsets = new List<long>();
        void Raw(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(string body) { offsets.Add(ms.Position); Raw($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }
        void Stream(string dict, byte[] data)
        {
            offsets.Add(ms.Position);
            Raw($"{offsets.Count} 0 obj\n<< {dict} /Length {data.Length} >>\nstream\n");
            ms.Write(data, 0, data.Length);
            Raw("\nendstream\nendobj\n");
        }

        Raw("%PDF-1.4\n%âãÏÓ\n");
        // objects: 1 catalog, 2 pages, 3-4 fonts, then images, then per page its content and page object
        int firstImage = 5, firstPage = firstImage + _images.Count;
        string kids = string.Join(" ", Enumerable.Range(0, _pages.Count).Select(i => $"{firstPage + 2 * i + 1} 0 R"));
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj($"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        foreach (var (data, w, h, rgb) in _images)
            Stream($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /{(rgb ? "DeviceRGB" : "DeviceGray")} /BitsPerComponent 8 /Filter /FlateDecode",
                   Deflate(data.AsSpan(0, w * h * (rgb ? 3 : 1))));
        string xobjects = _images.Count == 0 ? "" :
            " /XObject << " + string.Join(" ", Enumerable.Range(0, _images.Count).Select(i => $"/Im{i + 1} {firstImage + i} 0 R")) + " >>";
        foreach (var page in _pages)
        {
            Stream("/Filter /FlateDecode", Deflate(Encoding.Latin1.GetBytes(page.ToString())));
            Obj(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >>{xobjects} >> /Contents {offsets.Count} 0 R >>"));
        }
        long xref = ms.Position;
        var sb = new StringBuilder($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Raw(sb.ToString());
        return ms.ToArray();
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // PDF string: backslash, brackets escaped; WinAnsi characters kept (Latin-1 range), others "?"
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '\\' or '(' or ')') sb.Append('\\').Append(c);
            else if (c < ' ' || c > '\u00ff' || c is >= '\u007f' and < '\u00a0') sb.Append('?'); // controls and C1: not printable
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // Helvetica glyph widths (1/1000 em) for ASCII; an average for the rest
    private static readonly short[] Widths = BuildWidths(
        "278 278 355 556 556 889 667 191 333 333 389 584 278 333 278 278 556 556 556 556 556 556 556 556 556 556 278 278 584 584 584 556 " +
        "1015 667 667 722 722 667 611 778 722 278 500 667 556 833 722 778 667 778 722 667 611 722 667 944 667 667 611 278 278 278 469 556 " +
        "333 556 556 500 556 556 278 556 556 222 222 500 222 833 556 556 556 556 333 500 278 556 500 722 500 500 500 334 260 334 584");
    private static readonly short[] Bold = BuildWidths(
        "278 333 474 556 556 889 722 238 333 333 389 584 278 333 278 278 556 556 556 556 556 556 556 556 556 556 333 333 584 584 584 611 " +
        "975 722 722 722 722 667 611 778 722 278 556 722 611 833 722 778 667 778 722 667 611 722 667 944 667 667 611 333 278 333 584 556 " +
        "333 556 611 556 611 556 333 611 611 278 278 556 278 889 611 611 611 611 389 556 333 611 556 778 556 556 500 389 280 389 584");

    private static short[] BuildWidths(string list) => list.Split(' ').Select(short.Parse).ToArray();
    private static float Width(char c) => c is >= ' ' and <= '~' ? Widths[c - ' '] : 556;
    private static float BoldWidth(char c) => c is >= ' ' and <= '~' ? Bold[c - ' '] : 556;
}
