using SkiaSharp;

namespace VisionReports;

/// <summary>Writes a report as an A4 PDF (SkiaSharp's PDF backend: no extra licence).</summary>
public sealed class PdfReport : IDisposable
{
    private const float W = 595, H = 842, M = 36, Bottom = H - 40;
    private static readonly SKColor Ink = new(0x1f, 0x29, 0x37), Muted = new(0x6b, 0x72, 0x80), Line = new(0xd1, 0xd5, 0xdb),
        Accent = new(0x25, 0x63, 0xeb), Good = new(0x16, 0xa3, 0x4a), Bad = new(0xdc, 0x26, 0x26), Warn = new(0xd9, 0x77, 0x06),
        Band = new(0xf3, 0xf4, 0xf6);

    private readonly SKDocument _doc;
    private readonly ReportData _d;
    private readonly SKTypeface _face = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
    private readonly SKTypeface _bold = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default;
    private SKCanvas _c = null!;
    private float _y;
    private int _page;

    private PdfReport(Stream s, ReportData d)
    {
        _d = d;
        _doc = SKDocument.CreatePdf(s, new SKDocumentPdfMetadata
        {
            Title = d.Query.Title, Creator = "Vision Studio", Producer = "Vision Studio",
            Creation = DateTime.Now, Modified = DateTime.Now,
        });
    }

    public static byte[] Create(ReportData d)
    {
        using var ms = new MemoryStream();
        using (var r = new PdfReport(ms, d)) r.Write();
        return ms.ToArray();
    }

    public void Dispose()
    {
        _doc.Dispose();
    }

    // ---- page and text helpers

    private void NewPage()
    {
        if (_page > 0) _doc.EndPage();
        _c = _doc.BeginPage(W, H);
        _page++;
        _y = M;
        // header line and footer
        Text($"{_d.LineName} — {_d.Query.Title}", M, 22, 8, Muted);
        Text($"Page {_page}", W - M, 22, 8, Muted, right: true);
        Text($"Generated {_d.GeneratedLocal:dd-MM-yyyy HH:mm} by Vision Studio", M, H - 20, 7, Muted);
        Rule(28);
    }

    private void Need(float h)
    {
        if (_y + h > Bottom) NewPage();
    }

    private SKPaint Paint(float size, SKColor color, bool bold = false) => new()
    {
        Typeface = bold ? _bold : _face, TextSize = size, Color = color, IsAntialias = true,
    };

    private void Text(string s, float x, float y, float size, SKColor color, bool bold = false, bool right = false, bool centre = false)
    {
        using var p = Paint(size, color, bold);
        if (right) x -= p.MeasureText(s);
        else if (centre) x -= p.MeasureText(s) / 2;
        _c.DrawText(s, x, y, p);
    }

    private string Fit(string s, float width, float size, bool bold = false)
    {
        using var p = Paint(size, Ink, bold);
        if (p.MeasureText(s) <= width) return s;
        while (s.Length > 1 && p.MeasureText(s + "…") > width) s = s[..^1];
        return s + "…";
    }

    private List<string> Wrap(string s, float width, float size)
    {
        using var p = Paint(size, Ink);
        var lines = new List<string>();
        var cur = "";
        foreach (var w in s.Split(' '))
        {
            var t = cur.Length == 0 ? w : cur + " " + w;
            if (p.MeasureText(t) > width && cur.Length > 0) { lines.Add(cur); cur = w; }
            else cur = t;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    private void Rule(float y)
    {
        using var p = new SKPaint { Color = Line, StrokeWidth = 0.6f };
        _c.DrawLine(M, y, W - M, y, p);
    }

    private void Heading(string s)
    {
        Need(40);
        _y += 14;
        Text(s, M, _y + 12, 13, Accent, bold: true);
        _y += 20;
    }

    private void Rect(float x, float y, float w, float h, SKColor fill)
    {
        using var p = new SKPaint { Color = fill, IsAntialias = true };
        _c.DrawRoundRect(x, y, w, h, 4, 4, p);
    }

    /// <summary>A table that continues on the next page with its header.</summary>
    private void Table(string[] head, float[] widths, IEnumerable<string[]> rows, Func<int, SKColor?>? rowColour = null, int? maxRows = null)
    {
        const float rh = 14, size = 7.5f;
        float total = widths.Sum(), scale = (W - 2 * M) / total;
        var ws = widths.Select(w => w * scale).ToArray();
        void Head()
        {
            Need(rh * 2);
            Rect(M, _y, W - 2 * M, rh, Band);
            float x = M + 3;
            for (int i = 0; i < head.Length; i++) { Text(Fit(head[i], ws[i] - 4, size, true), x, _y + 10, size, Ink, bold: true); x += ws[i]; }
            _y += rh;
        }
        Head();
        int n = 0;
        foreach (var r in rows)
        {
            if (maxRows is { } m && n >= m) { Text($"… and more: the full list is in the Excel report.", M, _y + 10, size, Muted); _y += rh; break; }
            if (_y + rh > Bottom) { NewPage(); Head(); }
            var col = rowColour?.Invoke(n) ?? Ink;
            float x = M + 3;
            for (int i = 0; i < r.Length && i < ws.Length; i++) { Text(Fit(r[i], ws[i] - 4, size), x, _y + 10, size, col); x += ws[i]; }
            using var p = new SKPaint { Color = Band, StrokeWidth = 0.5f };
            _c.DrawLine(M, _y + rh, W - M, _y + rh, p);
            _y += rh;
            n++;
        }
        _y += 6;
    }

    private void Bars(IReadOnlyList<CountRow> rows, string unit, SKColor colour, int max = 10)
    {
        const float rh = 15;
        var list = rows.Take(max).ToList();
        if (list.Count == 0) { Text("None.", M, _y + 10, 9, Muted); _y += 16; return; }
        Need(list.Count * rh + 6);
        float labelW = 230, barW = W - 2 * M - labelW - 70;
        double top = list.Max(r => (double)r.Count);
        foreach (var r in list)
        {
            Text(Fit(r.Name, labelW - 6, 8), M, _y + 10, 8, Ink);
            float bw = (float)(top == 0 ? 0 : r.Count / top * barW);
            Rect(M + labelW, _y + 2, Math.Max(1, bw), rh - 5, colour);
            Text($"{r.Count:N0}{unit}" + (r.Share > 0 && unit.Length == 0 ? $"  ({r.Share:0} %)" : ""), M + labelW + bw + 4, _y + 10, 8, Ink);
            _y += rh;
        }
        _y += 6;
    }

    // ---- sections

    private void Write()
    {
        NewPage();
        var d = _d;
        // title block
        Text(d.Query.Title, M, _y + 22, 18, Ink, bold: true);
        Text($"{d.Query.FromUtc.ToLocalTime():dd-MM-yyyy HH:mm} to {d.Query.ToUtc.ToLocalTime():dd-MM-yyyy HH:mm}" +
             (d.Query.Batch is { } b ? $"   ·   Batch {b}" : ""), M, _y + 40, 9.5f, Muted);
        _y += 52;
        if (d.Batches.Count > 0)
        {
            var bl = string.Join("   ", d.Batches.Take(4).Select(x => $"{x.Name} ({x.Job}, {x.StartedUtc.ToLocalTime():dd-MM HH:mm}{(x.EndedUtc is { } e ? $"–{e.ToLocalTime():HH:mm}" : "–")}, {x.User})"));
            foreach (var l in Wrap("Batches: " + bl, W - 2 * M, 8)) { Text(l, M, _y + 8, 8, Muted); _y += 11; }
            _y += 4;
        }
        // approved remote corrections of batch values: the corrected value with the recorded one, who, who approved, why
        foreach (var c in d.Corrections)
            foreach (var (field, (old, now)) in c.Changes)
            {
                var line = $"Corrected: batch {c.Batch}, {field} \"{now}\" (recorded \"{old}\"), by {c.By}, approved by {c.ApprovedBy}, " +
                           $"{c.TimeUtc.ToLocalTime():dd-MM-yyyy HH:mm}: {c.Reason}";
                foreach (var l in Wrap(line, W - 2 * M, 8)) { Text(l, M, _y + 8, 8, Warn); _y += 11; }
            }
        if (d.Corrections.Count > 0) _y += 4;

        if (d.Query.Sections.HasFlag(ReportSections.Summary))
        {
            // KPI tiles
            var tiles = new (string Label, string Value, SKColor Colour)[]
            {
                ("Inspected", d.Parts.ToString("N0"), Ink), ("Good", d.Good.ToString("N0"), Good),
                ("Rejected", d.Rejected.ToString("N0"), d.Rejected > 0 ? Bad : Ink), ("Yield", $"{d.YieldPct:0.0} %", d.YieldPct >= 99 ? Good : d.YieldPct >= 95 ? Warn : Bad),
                ("Per minute", $"{d.AvgPerMinute:0}", Ink), ("Stopped", ReportData.Duration(d.StoppedSeconds), d.StoppedSeconds > 0 ? Warn : Ink),
            };
            float tw = (W - 2 * M - 5 * 6) / 6;
            for (int i = 0; i < tiles.Length; i++)
            {
                float x = M + i * (tw + 6);
                Rect(x, _y, tw, 46, Band);
                Text(tiles[i].Label, x + 6, _y + 13, 7.5f, Muted);
                Text(tiles[i].Value, x + 6, _y + 35, 15, tiles[i].Colour, bold: true);
            }
            _y += 58;

            Heading("Findings");
            foreach (var s in d.Insights)
            {
                var lines = Wrap(s, W - 2 * M - 14, 9);
                Need(lines.Count * 12 + 4);
                Text("•", M + 2, _y + 9, 9, Accent, bold: true);
                foreach (var l in lines) { Text(l, M + 14, _y + 9, 9, Ink); _y += 12; }
                _y += 3;
            }

            if (d.Hours.Count > 0)
            {
                Heading("Products and rejects per hour");
                HourChart();
            }
        }

        if (d.Query.Sections.HasFlag(ReportSections.Rejects))
        {
            Heading("Reject reasons");
            Bars(d.Reasons, "", Bad);
            if (d.FailingTools.Count > 0)
            {
                Heading("Failing tools (camera inspections)");
                Bars(d.FailingTools, "", Warn);
            }
        }

        if (d.Query.Sections.HasFlag(ReportSections.Cameras) && d.Cameras.Count > 0)
        {
            Heading("Cameras");
            Table(new[] { "Camera", "Inspections", "OK", "NOT OK", "NOT OK %", "Median ms", "95 % ms", "Max ms" },
                new float[] { 1, 1.2f, 1, 1, 1, 1, 1, 1 },
                d.Cameras.Select(c => new[] { $"Camera {c.Camera + 1}", c.Inspections.ToString("N0"), c.Ok.ToString("N0"), c.Ng.ToString("N0"),
                    c.Inspections == 0 ? "–" : $"{100.0 * c.Ng / c.Inspections:0.00}", $"{c.MedianMs:0.0}", $"{c.P95Ms:0.0}", $"{c.MaxMs:0.0}" }));
        }

        if (d.Query.Sections.HasFlag(ReportSections.Length) && (d.Length is not null || d.Gap is not null || d.TouchingGroups > 0))
        {
            Heading("Product length and gap (encoder)");
            var rows = new List<string[]>();
            if (d.Length is { } l)
                rows.Add(new[] { "Length, mm", $"{l.Count:N0}", $"{l.Mean:0.0}", $"{l.Sd:0.00}", $"{l.Min:0.0}", $"{l.Max:0.0}",
                    d.NominalMm is { } nom ? $"{nom:0.#} ±{d.TolerancePct} %" : "–", d.Cpk is { } cpk ? $"{cpk:0.00}" : "–" });
            if (d.Gap is { } g)
                rows.Add(new[] { "Gap, mm", $"{g.Count:N0}", $"{g.Mean:0.0}", $"{g.Sd:0.00}", $"{g.Min:0.0}", $"{g.Max:0.0}", "", "" });
            Table(new[] { "", "Products", "Mean", "Std dev", "Min", "Max", "Nominal", "Cpk" }, new float[] { 1.3f, 1, 1, 1, 1, 1, 1.3f, 0.8f }, rows);
            Text($"Touching groups: {d.TouchingGroups:N0} ({d.TouchingProducts:N0} products)    Out of length tolerance: {d.LengthOut:N0}    Small gaps: {d.SmallGaps:N0}",
                M, _y + 8, 8.5f, Ink);
            _y += 16;
            if (d.LengthBins.Count > 1) Histogram();
        }

        if (d.Query.Sections.HasFlag(ReportSections.Events))
        {
            Heading("Stoppages and alarms");
            if (d.Stoppages.Count == 0) { Text("None.", M, _y + 10, 9, Muted); _y += 16; }
            else
            {
                Bars(d.StoppageByKind.Select(k => new CountRow($"{k.Name} ({k.Count}×)", (int)Math.Round(k.Share), 0)).ToList(), " s", Warn);
                Table(new[] { "Start", "End", "Duration", "What", "Detail" }, new float[] { 1.3f, 1.3f, 1, 2, 2.5f },
                    d.Stoppages.Select(s => new[] { s.StartLocal.ToString("dd-MM HH:mm:ss"), s.EndLocal?.ToString("dd-MM HH:mm:ss") ?? "–",
                        ReportData.Duration(s.Seconds), s.Kind, s.Detail }), maxRows: 60);
            }
        }

        if (d.Query.Sections.HasFlag(ReportSections.PartList))
        {
            Heading($"Rejected products ({d.RejectList.Count:N0})");
            if (d.RejectList.Count == 0) { Text("None.", M, _y + 10, 9, Muted); _y += 16; }
            else
                Table(new[] { "Time", "Part", "Batch", "Length mm", "Reason" }, new float[] { 1.3f, 0.6f, 1, 0.8f, 5.5f },
                    d.RejectList.Select(p => new[] { p.TimeUtc.ToLocalTime().ToString("dd-MM HH:mm:ss"), p.PartId.ToString(), p.Batch,
                        p.LengthMm is { } lm ? $"{lm:0.0}" : "–", p.Reason }), maxRows: 300);
        }

        _doc.EndPage();
        _doc.Close();
    }

    private void HourChart()
    {
        const float h = 120;
        Need(h + 30);
        var hours = _d.Hours;
        float x0 = M + 30, x1 = W - M - 30, y0 = _y + h, w = (x1 - x0) / Math.Max(1, hours.Count);
        int top = Math.Max(1, hours.Max(r => r.Parts));
        double topPct = Math.Max(1, hours.Max(r => r.RejectPct));
        using var axis = new SKPaint { Color = Line, StrokeWidth = 0.6f };
        _c.DrawLine(x0, y0, x1, y0, axis);
        Text($"{top:N0}", x0 - 4, _y + 8, 7, Muted, right: true);
        Text($"{topPct:0.#} %", x1 + 4, _y + 8, 7, Bad);
        var path = new SKPath();
        for (int i = 0; i < hours.Count; i++)
        {
            var r = hours[i];
            float bh = (float)r.Parts / top * (h - 10);
            Rect(x0 + i * w + w * 0.15f, y0 - bh, w * 0.7f, bh, new SKColor(0x93, 0xc5, 0xfd));
            float py = y0 - (float)(r.RejectPct / topPct) * (h - 10), px = x0 + i * w + w / 2;
            if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
            if (hours.Count <= 24 || i % 2 == 0) Text(r.HourLocal.ToString("HH"), px, y0 + 10, 7, Muted, centre: true);
        }
        using var lp = new SKPaint { Color = Bad, StrokeWidth = 1.4f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        _c.DrawPath(path, lp);
        Text("bars: products per hour   line: reject %", x0, y0 + 22, 7, Muted);
        _y += h + 30;
    }

    private void Histogram()
    {
        const float h = 90;
        Need(h + 30);
        var bins = _d.LengthBins;
        float x0 = M + 30, x1 = W - M - 30, y0 = _y + h, w = (x1 - x0) / bins.Count;
        int top = Math.Max(1, bins.Max(b => b.Count));
        double? lo = _d.NominalMm * (1 - _d.TolerancePct / 100.0), hi = _d.NominalMm * (1 + _d.TolerancePct / 100.0);
        for (int i = 0; i < bins.Count; i++)
        {
            var b = bins[i];
            float bh = (float)b.Count / top * (h - 10);
            bool outTol = lo is not null && (b.To < lo || b.From > hi);
            Rect(x0 + i * w + 1, y0 - bh, w - 2, bh, outTol ? Bad : Accent);
            if (i % 3 == 0) Text($"{b.From:0.#}", x0 + i * w, y0 + 10, 7, Muted);
        }
        Text($"{bins[^1].To:0.#} mm", x1, y0 + 10, 7, Muted, right: true);
        Text("length distribution (red: outside the tolerance)", x0, y0 + 22, 7, Muted);
        _y += h + 30;
    }
}
