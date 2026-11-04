using ClosedXML.Excel;
using VisionCore.Plc;

namespace VisionReports;

/// <summary>Writes a report as an Excel workbook: a summary sheet and one sheet per table, with filters.</summary>
public static class ExcelReport
{
    public static byte[] Create(ReportData d)
    {
        using var wb = new XLWorkbook();
        wb.Properties.Title = d.Query.Title;
        wb.Properties.Author = "Vision Studio";

        var s = wb.AddWorksheet("Summary");
        s.Cell(1, 1).Value = d.Query.Title;
        s.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(16);
        s.Cell(2, 1).Value = $"{d.LineName}   {d.Query.FromUtc.ToLocalTime():dd-MM-yyyy HH:mm} to {d.Query.ToUtc.ToLocalTime():dd-MM-yyyy HH:mm}" +
                             (d.Query.Batch is { } b ? $"   Batch {b}" : "");
        s.Cell(3, 1).Value = $"Generated {d.GeneratedLocal:dd-MM-yyyy HH:mm} by Vision Studio";
        int r = 5;
        void Kpi(string name, XLCellValue v, string? fmt = null)
        {
            s.Cell(r, 1).Value = name;
            s.Cell(r, 2).Value = v;
            if (fmt is not null) s.Cell(r, 2).Style.NumberFormat.Format = fmt;
            r++;
        }
        Kpi("Inspected", d.Parts, "#,##0");
        Kpi("Good", d.Good, "#,##0");
        Kpi("Rejected", d.Rejected, "#,##0");
        Kpi("Yield %", Math.Round(d.YieldPct, 2), "0.00");
        if (d.PrevYieldPct is { } py) Kpi("Yield % (period before)", Math.Round(py, 2), "0.00");
        Kpi("Products per minute (average while running)", Math.Round(d.AvgPerMinute, 1), "0.0");
        Kpi("Products per minute (peak)", d.PeakPerMinute, "0");
        Kpi("Stopped (minutes)", Math.Round(d.StoppedSeconds / 60, 1), "0.0");
        Kpi("Touching groups", d.TouchingGroups);
        Kpi("Products in touching groups", d.TouchingProducts);
        Kpi("Out of length tolerance", d.LengthOut);
        Kpi("Small gaps", d.SmallGaps);
        if (d.Length is { } l)
        {
            Kpi("Length mean mm", Math.Round(l.Mean, 2), "0.00");
            Kpi("Length std dev mm", Math.Round(l.Sd, 3), "0.000");
            if (d.NominalMm is { } nom) Kpi($"Length nominal mm (±{d.TolerancePct} %)", nom, "0.0");
            if (d.Cpk is { } cpk) Kpi("Length Cpk", Math.Round(cpk, 2), "0.00");
        }
        s.Range(5, 1, r - 1, 1).Style.Font.SetBold();
        r++;
        s.Cell(r++, 1).Value = "Findings";
        s.Cell(r - 1, 1).Style.Font.SetBold().Font.SetFontSize(12);
        foreach (var f in d.Insights) { s.Cell(r, 1).Value = f; s.Range(r, 1, r, 8).Merge().Style.Alignment.WrapText = true; s.Row(r).Height = 30; r++; }
        if (d.Batches.Count > 0)
        {
            r++;
            s.Cell(r++, 1).Value = "Batches";
            s.Cell(r - 1, 1).Style.Font.SetBold().Font.SetFontSize(12);
            Header(s, r++, "Batch", "Job", "Started", "Ended", "User", "Fields");
            foreach (var x in d.Batches)
                Row(s, r++, x.Name, x.Job, x.StartedUtc.ToLocalTime(), x.EndedUtc?.ToLocalTime(), x.User, x.Fields);
        }
        if (d.Corrections.Count > 0)
        {
            // the values entered stay as they were above; each approved correction is listed with both values
            r++;
            s.Cell(r++, 1).Value = "Corrected batch values";
            s.Cell(r - 1, 1).Style.Font.SetBold().Font.SetFontSize(12);
            Header(s, r++, "Batch", "Field", "Recorded", "Corrected", "Corrected by", "Approved by", "Reason", "Time");
            foreach (var c in d.Corrections)
                foreach (var (field, (old, now)) in c.Changes)
                    Row(s, r++, c.Batch, field, old, now, c.By, c.ApprovedBy, c.Reason, c.TimeUtc.ToLocalTime());
        }
        s.Column(1).Width = 44;
        s.Columns(2, 8).Width = 16;

        Sheet(wb, "Reject reasons", new[] { "Reason", "Rejects", "Share %" },
            d.Reasons.Select(x => new object?[] { x.Name, x.Count, Math.Round(x.Share, 1) }));
        Sheet(wb, "Failing tools", new[] { "Camera: tool", "NOT OK", "Share %" },
            d.FailingTools.Select(x => new object?[] { x.Name, x.Count, Math.Round(x.Share, 1) }));
        Sheet(wb, "Per hour", new[] { "Hour", "Products", "Rejected", "Reject %" },
            d.Hours.Select(h => new object?[] { h.HourLocal, h.Parts, h.Rejected, Math.Round(h.RejectPct, 2) }));
        Sheet(wb, "Cameras", new[] { "Camera", "Inspections", "OK", "NOT OK", "Median ms", "95 % ms", "Max ms" },
            d.Cameras.Select(c => new object?[] { c.Camera + 1, c.Inspections, c.Ok, c.Ng, Math.Round(c.MedianMs, 1), Math.Round(c.P95Ms, 1), Math.Round(c.MaxMs, 1) }));
        Sheet(wb, "Length", new[] { "From mm", "To mm", "Products" },
            d.LengthBins.Select(x => new object?[] { Math.Round(x.From, 2), Math.Round(x.To, 2), x.Count }));
        Sheet(wb, "Stoppages", new[] { "Start", "End", "Seconds", "What", "Detail" },
            d.Stoppages.Select(x => new object?[] { x.StartLocal, x.EndLocal, Math.Round(x.Seconds), x.Kind, x.Detail }));
        Sheet(wb, "Rejected", new[] { "Time", "Part", "Batch", "Job", "Reason group", "Reason", "Length mm", "Gap mm", "Group", "Flags", "Image" },
            d.RejectList.Select(PartCells));
        Sheet(wb, "All products", new[] { "Time", "Part", "Batch", "Job", "Result", "Reason", "Length mm", "Gap mm", "Group", "Flags", "Image" },
            d.AllParts.Select(p => { var c = PartCells(p); c[4] = p.Rejected ? "Rejected" : "Good"; return c; }));
        Sheet(wb, "Inspections", new[] { "Time", "Camera", "Part", "Batch", "Job", "Result", "Failing tool", "Reason", "Code", "Text", "ms", "Image" },
            d.Inspections.Select(i => new object?[] { i.TimeUtc.ToLocalTime(), i.Camera + 1, i.PartText ?? (object?)i.PartId, i.Batch, i.Job, i.Ok ? "OK" : "NOT OK",
                i.FailTool, i.Reason, i.Text1, i.Text2, Math.Round(i.Ms, 1), i.ImageId }));

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static object?[] PartCells(PartRow p) => new object?[]
    {
        p.TimeUtc.ToLocalTime(), p.PartId, p.Batch, p.Job, p.Category, p.Reason,
        p.LengthMm is { } l ? Math.Round(l, 1) : null, p.GapMm is { } g ? Math.Round(g, 1) : null,
        p.GroupSize > 1 ? $"{p.GroupIndex + 1} of {p.GroupSize}" : "", p.Flags == PartFlags.None ? "" : p.Flags.ToString(), p.ImageId,
    };

    private static void Header(IXLWorksheet s, int r, params string[] names)
    {
        for (int i = 0; i < names.Length; i++) s.Cell(r, i + 1).Value = names[i];
        s.Range(r, 1, r, names.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#E5E7EB"));
    }

    private static void Row(IXLWorksheet s, int r, params object?[] v)
    {
        for (int i = 0; i < v.Length; i++)
        {
            var c = s.Cell(r, i + 1);
            switch (v[i])
            {
                case null: break;
                case DateTime t: c.Value = t; c.Style.NumberFormat.Format = "dd-mm-yyyy hh:mm:ss"; break;
                case int n: c.Value = n; break;
                case long n: c.Value = n; break;
                case double x: c.Value = x; break;
                default: c.Value = v[i]!.ToString(); break;
            }
        }
    }

    private static void Sheet(XLWorkbook wb, string name, string[] head, IEnumerable<object?[]> rows)
    {
        var s = wb.AddWorksheet(name);
        Header(s, 1, head);
        int r = 2;
        foreach (var row in rows) Row(s, r++, row);
        if (r > 2) s.Range(1, 1, r - 1, head.Length).SetAutoFilter();
        s.SheetView.FreezeRows(1);
        s.Columns(1, head.Length).AdjustToContents(1, Math.Min(r - 1, 500));
        foreach (var col in s.Columns(1, head.Length)) if (col.Width > 70) col.Width = 70;
    }
}
