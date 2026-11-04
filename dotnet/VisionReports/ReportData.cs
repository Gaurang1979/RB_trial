using VisionCore.Plc;

namespace VisionReports;

/// <summary>What to report on: a time range (shift, day, custom) and optionally one batch.</summary>
public sealed record ReportQuery(DateTime FromUtc, DateTime ToUtc, string? Batch, string Title)
{
    /// <summary>Report sections; all by default.</summary>
    public ReportSections Sections { get; init; } = ReportSections.All;
}

[Flags]
public enum ReportSections
{
    Summary = 1, Rejects = 2, Length = 4, Events = 8, Cameras = 16, PartList = 32,
    All = Summary | Rejects | Length | Events | Cameras | PartList,
}

public sealed record CountRow(string Name, int Count, double Share);
public sealed record HourRow(DateTime HourLocal, int Parts, int Rejected)
{
    public double RejectPct => Parts == 0 ? 0 : 100.0 * Rejected / Parts;
}
public sealed record CameraStat(int Camera, int Inspections, int Ok, int Ng, double MedianMs, double P95Ms, double MaxMs);
public sealed record Stats(int Count, double Mean, double Sd, double Min, double Max)
{
    public static Stats? Of(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return null;
        double m = v.Average();
        double sd = v.Count > 1 ? Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1)) : 0;
        return new Stats(v.Count, m, sd, v.Min(), v.Max());
    }
}
public sealed record Bin(double From, double To, int Count);
public sealed record Stoppage(string Kind, DateTime StartLocal, DateTime? EndLocal, double Seconds, string Detail);

/// <summary>Everything a report shows, worked out once and drawn by the PDF and Excel writers.</summary>
public sealed class ReportData
{
    public required ReportQuery Query { get; init; }
    public required string LineName { get; init; }
    public DateTime GeneratedLocal { get; init; } = DateTime.Now;
    public bool FromPlc { get; init; }
    public List<BatchRow> Batches { get; init; } = new();
    /// <summary>Approved remote corrections of these batches' values (REMOTE-SUPPORT.md 4b): shown with the original values.</summary>
    public List<ServiceLog.BatchCorrection> Corrections { get; set; } = new();
    public int Parts { get; init; }
    public int Good { get; init; }
    public int Rejected { get; init; }
    public double YieldPct => Parts == 0 ? 0 : 100.0 * Good / Parts;
    public double? PrevYieldPct { get; init; }
    public int PrevParts { get; init; }
    public double AvgPerMinute { get; init; }
    public double PeakPerMinute { get; init; }
    public double RunMinutes { get; init; }
    public List<CountRow> Reasons { get; init; } = new();
    public List<CountRow> FailingTools { get; init; } = new();
    public List<HourRow> Hours { get; init; } = new();
    public List<CameraStat> Cameras { get; init; } = new();
    public Stats? Length { get; init; }
    public double? NominalMm { get; init; }
    public int TolerancePct { get; init; }
    public double? Cpk { get; init; }
    public List<Bin> LengthBins { get; init; } = new();
    public Stats? Gap { get; init; }
    public int SmallGaps { get; init; }
    public int TouchingGroups { get; init; }
    public int TouchingProducts { get; init; }
    public int LengthOut { get; init; }
    public List<Stoppage> Stoppages { get; init; } = new();
    public List<CountRow> StoppageByKind { get; init; } = new();
    public double StoppedSeconds { get; init; }
    public List<PartRow> RejectList { get; init; } = new();
    public List<PartRow> AllParts { get; init; } = new();
    public List<InspectionRow> Inspections { get; init; } = new();
    public List<string> Insights { get; init; } = new();

    public static string Local(DateTime utc) => utc.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss");
    public static string Duration(double s) => s >= 3600 ? $"{(int)(s / 3600)} h {(int)(s % 3600 / 60)} min"
        : s >= 60 ? $"{(int)(s / 60)} min {(int)(s % 60)} s" : $"{s:0} s";
}

/// <summary>Builds <see cref="ReportData"/> from the results log.</summary>
public static class ReportBuilder
{
    /// <summary>Condition events that stop or hold the line, with their names in the report.</summary>
    public static readonly IReadOnlyDictionary<string, string> StopKinds = new Dictionary<string, string>
    {
        ["jam"] = "Line jam", ["estop"] = "E-stop", ["air"] = "Air pressure low", ["bin"] = "Reject bin full",
        ["drive"] = "Conveyor drive fault", ["drive_comm"] = "Drive not answering", ["vision_lost"] = "Vision link lost",
        ["reject_fail"] = "Reject not confirmed (line stopped)", ["consec_ng"] = "NOT OK in a row (line stopped)",
        ["tracking"] = "Tracking mismatch", ["encoder"] = "Encoder fault", ["trigger_stuck"] = "Trigger sensor blocked",
        ["line_stop"] = "Line stopped",
    };

    public static ReportData Build(ResultLog log, ReportQuery q, string lineName, LineSettings? line = null,
                                   int maxRows = 200_000)
    {
        var parts = log.Parts(q.FromUtc, q.ToUtc, q.Batch, maxRows);
        var insp = log.Inspections(q.FromUtc, q.ToUtc, q.Batch, maxRows);
        bool fromPlc = parts.Count > 0;

        // without a PLC part log, every inspection cycle is a part (folder / free-run / single-camera use)
        if (!fromPlc)
            parts = insp.Select(i => new PartRow(i.Id, i.TimeUtc, i.Batch, i.Job, i.PartId ?? 0, !i.Ok, i.Ok ? PartFlags.None : PartFlags.CameraNg,
                null, null, 1, 0, 1 << i.Camera, i.Ok ? 1 << i.Camera : 0,
                i.Ok ? PartReasons.Good : $"Camera {i.Camera + 1}: {i.FailTool ?? "NOT OK"}",
                i.Ok ? "OK" : $"Camera {i.Camera + 1} NOT OK" + (i.FailTool is { } t ? $" — {t}" : "") + (i.Reason is { Length: > 0 } r ? $": {r}" : ""))).ToList();

        int n = parts.Count, rej = parts.Count(p => p.Rejected);
        var reasons = parts.Where(p => p.Rejected).GroupBy(p => p.Category)
            .Select(g => new CountRow(g.Key, g.Count(), rej == 0 ? 0 : 100.0 * g.Count() / rej)).OrderByDescending(r => r.Count).ToList();
        int ngInsp = insp.Count(i => !i.Ok);
        var tools = insp.Where(i => !i.Ok).GroupBy(i => $"Camera {i.Camera + 1}: {i.FailTool ?? "no picture / error"}")
            .Select(g => new CountRow(g.Key, g.Count(), ngInsp == 0 ? 0 : 100.0 * g.Count() / ngInsp)).OrderByDescending(r => r.Count).ToList();

        var hours = parts.GroupBy(p => { var l = p.TimeUtc.ToLocalTime(); return new DateTime(l.Year, l.Month, l.Day, l.Hour, 0, 0); })
            .OrderBy(g => g.Key).Select(g => new HourRow(g.Key, g.Count(), g.Count(p => p.Rejected))).ToList();

        // throughput: minutes with parts
        var perMin = parts.GroupBy(p => p.TimeUtc.Ticks / TimeSpan.TicksPerMinute).Select(g => g.Count()).ToList();
        double runMin = perMin.Count;

        var cams = insp.GroupBy(i => i.Camera).OrderBy(g => g.Key).Select(g =>
        {
            var ms = g.Select(i => i.Ms).OrderBy(x => x).ToList();
            return new CameraStat(g.Key, ms.Count, g.Count(i => i.Ok), g.Count(i => !i.Ok), Pct(ms, 50), Pct(ms, 95), ms.Count > 0 ? ms[^1] : 0);
        }).ToList();

        // length and gap (good-sized single products only for the statistics; touching groups are split)
        // statistics over the products of normal size; out-of-tolerance ones are counted apart and shown in the histogram
        var allLengths = parts.Where(p => p.LengthMm is > 0).Select(p => p.LengthMm!.Value).ToList();
        var lengths = parts.Where(p => p.LengthMm is > 0 && !p.Flags.HasFlag(PartFlags.LengthOut)).Select(p => p.LengthMm!.Value).ToList();
        var gaps = parts.Where(p => p.GapMm is > 0 && p.GroupIndex == 0).Select(p => p.GapMm!.Value).ToList();
        double? nominal = line is { ProductLengthMm: > 0 } ? line.ProductLengthMm : null;
        var ls = Stats.Of(lengths);
        nominal ??= ls is null ? null : Math.Round(Median(lengths), 1);
        int tol = line?.LengthTolerancePct ?? 20;
        double? cpk = null;
        if (ls is { Sd: > 0 } && nominal is { } nom)
        {
            double usl = nom * (1 + tol / 100.0), lsl = nom * (1 - tol / 100.0);
            cpk = Math.Min(usl - ls.Mean, ls.Mean - lsl) / (3 * ls.Sd);
        }

        // stoppages from condition events (start/end pairs), clipped to the range
        var evs = log.Events(q.FromUtc, q.ToUtc);
        var stops = new List<Stoppage>();
        foreach (var kind in StopKinds.Keys)
        {
            DateTime? on = log.ActiveAt(kind, q.FromUtc) ? q.FromUtc : null;
            string detail = "";
            foreach (var e in evs.Where(e => e.Kind == kind))
            {
                if (e.Active && on is null) { on = e.TimeUtc; detail = e.Detail; }
                else if (!e.Active && on is not null)
                {
                    stops.Add(new Stoppage(StopKinds[kind], on.Value.ToLocalTime(), e.TimeUtc.ToLocalTime(), (e.TimeUtc - on.Value).TotalSeconds, detail));
                    on = null;
                }
            }
            if (on is not null)
            {
                var end = q.ToUtc < DateTime.UtcNow ? q.ToUtc : DateTime.UtcNow;
                stops.Add(new Stoppage(StopKinds[kind], on.Value.ToLocalTime(), null, Math.Max(0, (end - on.Value).TotalSeconds), detail + " (still on)"));
            }
        }
        stops.Sort((a, b) => a.StartLocal.CompareTo(b.StartLocal));
        // line_stop overlaps the others: total stopped time from line_stop when it exists
        var lineStops = stops.Where(s => s.Kind == StopKinds["line_stop"]).ToList();
        double stopped = lineStops.Count > 0 ? lineStops.Sum(s => s.Seconds) : stops.Sum(s => s.Seconds);
        var byKind = stops.Where(s => s.Kind != StopKinds["line_stop"]).GroupBy(s => s.Kind)
            .Select(g => new CountRow(g.Key, g.Count(), g.Sum(s => s.Seconds))).OrderByDescending(r => r.Share).ToList();

        // previous period of the same length, for comparison
        var span = q.ToUtc - q.FromUtc;
        var prevParts = q.Batch is null ? log.Parts(q.FromUtc - span, q.FromUtc, null, maxRows) : new List<PartRow>();
        int prevN = prevParts.Count;
        if (prevN == 0 && q.Batch is null)
        {
            var pi = log.Inspections(q.FromUtc - span, q.FromUtc, null, maxRows);
            prevN = pi.Count;
            prevParts = pi.Select(i => new PartRow(0, i.TimeUtc, "", "", 0, !i.Ok, PartFlags.None, null, null, 1, 0, 0, 0, "", "")).ToList();
        }
        double? prevYield = prevN == 0 ? null : 100.0 * prevParts.Count(p => !p.Rejected) / prevN;

        var d = new ReportData
        {
            Query = q, LineName = lineName, FromPlc = fromPlc,
            Batches = log.Batches(q.FromUtc, q.ToUtc).Where(b => q.Batch is null || b.Name == q.Batch).ToList(),
            Parts = n, Good = n - rej, Rejected = rej, PrevYieldPct = prevYield, PrevParts = prevN,
            AvgPerMinute = runMin == 0 ? 0 : n / runMin, PeakPerMinute = perMin.Count == 0 ? 0 : perMin.Max(), RunMinutes = runMin,
            Reasons = reasons, FailingTools = tools, Hours = hours, Cameras = cams,
            Length = ls, NominalMm = nominal, TolerancePct = tol, Cpk = cpk, LengthBins = Histogram(allLengths, 16),
            Gap = Stats.Of(gaps), SmallGaps = parts.Count(p => p.Flags.HasFlag(PartFlags.SmallGap)),
            TouchingGroups = parts.Count(p => p.Flags.HasFlag(PartFlags.Touching) && p.GroupIndex == 0),
            TouchingProducts = parts.Count(p => p.Flags.HasFlag(PartFlags.Touching)),
            LengthOut = parts.Count(p => p.Flags.HasFlag(PartFlags.LengthOut)),
            Stoppages = stops, StoppageByKind = byKind, StoppedSeconds = stopped,
            RejectList = parts.Where(p => p.Rejected).ToList(), AllParts = parts, Inspections = insp,
        };
        d.Insights.AddRange(Insights(d));
        return d;
    }

    private static double Pct(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static List<Bin> Histogram(List<double> v, int bins)
    {
        if (v.Count == 0) return new();
        double lo = v.Min(), hi = v.Max();
        if (hi - lo < 1e-9) return new() { new Bin(lo, hi, v.Count) };
        double w = (hi - lo) / bins;
        var c = new int[bins];
        foreach (var x in v) c[Math.Min(bins - 1, (int)((x - lo) / w))]++;
        return Enumerable.Range(0, bins).Select(i => new Bin(lo + i * w, lo + (i + 1) * w, c[i])).ToList();
    }

    /// <summary>Plain-language findings: what stands out and what to check.</summary>
    public static IEnumerable<string> Insights(ReportData d)
    {
        if (d.Parts == 0) { yield return "No products were inspected in this period."; yield break; }
        string y = $"Yield {d.YieldPct:0.0} %: {d.Good:N0} good, {d.Rejected:N0} rejected of {d.Parts:N0}";
        if (d.PrevYieldPct is { } py && d.PrevParts > 0)
        {
            double diff = d.YieldPct - py;
            y += Math.Abs(diff) < 0.1 ? $" (same as the period before)." : $" ({(diff > 0 ? "up" : "down")} {Math.Abs(diff):0.0} points on the period before, {py:0.0} %).";
        }
        else y += ".";
        yield return y;

        if (d.Reasons.FirstOrDefault() is { } top && d.Rejected >= 3)
            yield return $"Main reject reason: {top.Name} — {top.Count:N0} of {d.Rejected:N0} rejects ({top.Share:0} %).{Advice(top.Name)}";

        var hot = d.Hours.Where(h => h.Parts >= 20).OrderByDescending(h => h.RejectPct).FirstOrDefault();
        double avgPct = d.Parts == 0 ? 0 : 100.0 * d.Rejected / d.Parts;
        if (hot is not null && hot.RejectPct > Math.Max(2 * avgPct, avgPct + 2))
            yield return $"Rejects peaked at {hot.HourLocal:HH:mm}–{hot.HourLocal.AddHours(1):HH:mm}: {hot.RejectPct:0.0} % against {avgPct:0.0} % over the period. Check what changed then (batch, operator, reel, speed).";

        if (d.TouchingGroups > 0)
            yield return $"Products came without a gap {d.TouchingGroups:N0} times ({d.TouchingProducts:N0} products; the ones hidden behind another could not be inspected). Check the infeed spacing: star wheel / timing screw, or run this conveyor faster than the one before it.";
        if (d.SmallGaps > 0)
            yield return $"{d.SmallGaps:N0} products had less than the minimum gap to the one before: close to touching.";
        if (d.LengthOut > 0)
            yield return $"{d.LengthOut:N0} products were out of length tolerance: fallen or lying products, broken or half packs, or a different pack size on the line.";
        if (d.Cpk is { } cpk && d.Length is { Count: >= 30 })
            yield return cpk < 1.0
                ? $"Product length varies a lot (Cpk {cpk:0.00} against ±{d.TolerancePct} %): guides, product orientation or the encoder wheel slipping."
                : $"Product length is stable: mean {d.Length.Mean:0.0} mm, spread ±{3 * d.Length.Sd:0.0} mm (Cpk {cpk:0.00}).";

        int notInsp = d.AllParts.Count(p => p.Flags.HasFlag(PartFlags.NotInspected) && !p.Flags.HasFlag(PartFlags.Touching));
        if (notInsp > 0)
            yield return $"{notInsp:N0} products were not inspected (camera busy, not ready or no answer): the line may be faster than the inspection time, or the vision PC was not running.";
        foreach (var c in d.Cameras.Where(c => c.P95Ms > 100))
            yield return $"Camera {c.Camera + 1}: inspection time {c.P95Ms:0} ms for 95 % of parts (above 100 ms). Narrow the search areas or lower the image size.";
        if (d.StoppedSeconds > 0)
        {
            var big = d.StoppageByKind.FirstOrDefault();
            yield return $"Line stopped {ReportData.Duration(d.StoppedSeconds)} in total" +
                (big is null ? "." : $"; most from {big.Name} ({big.Count}×, {ReportData.Duration(big.Share)}).");
        }
        if (d.Hours.Count >= 2 && d.RunMinutes > 0)
            yield return $"Speed: {d.AvgPerMinute:0} products a minute on average while running, {d.PeakPerMinute:0} at the peak.";
    }

    private static string Advice(string reason) => reason switch
    {
        PartReasons.TouchingCat => " Products are bunching at the infeed.",
        PartReasons.LengthCat => " Products are fallen, lying or of another size.",
        PartReasons.NotInspectedCat => " The cameras did not answer in time.",
        PartReasons.MissedCat => " A camera trigger sensor is missing products: clean or realign it.",
        _ when reason.Contains("Camera") => " Look at the images of these rejects (print quality, label position, lighting).",
        _ => "",
    };
}
