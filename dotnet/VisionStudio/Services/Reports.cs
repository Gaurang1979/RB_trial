using System.Text.Json;
using Microsoft.Extensions.Options;
using VisionReports;

namespace VisionStudio.Services;

public sealed class ShiftDef
{
    public string Name { get; set; } = "";
    /// <summary>Start time of day, "HH:mm"; a shift ends where the next one starts.</summary>
    public string Start { get; set; } = "06:00";
}

/// <summary>Report settings (data/reports.json), edited on the Reports page.</summary>
public sealed class ReportSettings
{
    public string LineName { get; set; } = "Line 1";
    public List<ShiftDef> Shifts { get; set; } = new()
    {
        new() { Name = "Shift A", Start = "06:00" }, new() { Name = "Shift B", Start = "14:00" }, new() { Name = "Shift C", Start = "22:00" },
    };
    /// <summary>At every shift end, write that shift's report to the reports folder.</summary>
    public bool AutoPdf { get; set; } = true;
    public bool AutoExcel { get; set; } = true;
    /// <summary>Also a report per batch when the next batch starts.</summary>
    public bool AutoBatch { get; set; } = true;
    /// <summary>Delete automatic reports older than this many days (0 = keep all).</summary>
    public int KeepDays { get; set; } = 365;
}

public sealed record ShiftSpan(string Name, DateTime FromLocal, DateTime ToLocal)
{
    public string Title => $"{Name} report — {FromLocal:dd-MM-yyyy}";
}

/// <summary>Builds reports from the results log and keeps the automatic ones in data/reports.</summary>
public sealed class ReportService
{
    private readonly ResultLog _log;
    private readonly PlcService _plc;
    private readonly string _settingsPath;

    public ReportService(ResultLog log, PlcService plc, IOptions<StudioOptions> opt)
    {
        _log = log;
        _plc = plc;
        Folder = Path.Combine(opt.Value.DataFolder, "reports");
        Directory.CreateDirectory(Folder);
        _settingsPath = Path.Combine(opt.Value.DataFolder, "reports.json");
        Settings = File.Exists(_settingsPath) ? JsonSerializer.Deserialize<ReportSettings>(File.ReadAllText(_settingsPath)) ?? new() : new();
    }

    public string Folder { get; }
    public ReportSettings Settings { get; private set; }
    public ResultLog Log => _log;

    public void Save(ReportSettings s)
    {
        s.Shifts = s.Shifts.Where(x => TimeSpan.TryParse(x.Start, out _)).OrderBy(x => TimeSpan.Parse(x.Start)).ToList();
        Settings = s;
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Where approved corrections of batch values come from (the runtime's service log when remote service is on); null:
    /// none. Given the report's batch (null: every batch).</summary>
    public Func<string?, IReadOnlyList<ServiceLog.BatchCorrection>>? Corrections { get; set; }

    public ReportData Build(ReportQuery q)
    {
        var d = ReportBuilder.Build(_log, q, Settings.LineName, _plc.Line);
        if (Corrections is { } source)
        {
            // a report is always made: corrections that cannot be read are left out, not a reason to fail it
            try { d.Corrections = source(q.Batch).Where(c => d.Batches.Any(b => b.Name == c.Batch)).ToList(); }
            catch (Exception) { }
        }
        return d;
    }

    public byte[] Pdf(ReportQuery q) => PdfReport.Create(Build(q));
    public byte[] Excel(ReportQuery q) => ExcelReport.Create(Build(q));

    /// <summary>The shift that contains a local time.</summary>
    public ShiftSpan ShiftAt(DateTime local)
    {
        var shifts = Settings.Shifts.Count > 0 ? Settings.Shifts : new() { new() { Name = "Day", Start = "00:00" } };
        var starts = new List<(DateTime At, string Name)>();
        foreach (var day in new[] { local.Date.AddDays(-1), local.Date, local.Date.AddDays(1) })
            foreach (var s in shifts) starts.Add((day + TimeSpan.Parse(s.Start), s.Name));
        starts.Sort((a, b) => a.At.CompareTo(b.At));
        int i = starts.FindLastIndex(x => x.At <= local);
        return new ShiftSpan(starts[i].Name, starts[i].At, starts[i + 1].At);
    }

    public ShiftSpan CurrentShift() => ShiftAt(DateTime.Now);
    public ShiftSpan PreviousShift() => ShiftAt(CurrentShift().FromLocal.AddMinutes(-1));

    /// <summary>Writes the PDF and / or Excel report to the reports folder; returns the file names.</summary>
    public List<string> WriteFiles(ReportQuery q, string baseName, bool pdf, bool excel)
    {
        var names = new List<string>();
        var safe = string.Concat(baseName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var d = Build(q);
        if (pdf) { File.WriteAllBytes(Path.Combine(Folder, safe + ".pdf"), PdfReport.Create(d)); names.Add(safe + ".pdf"); }
        if (excel) { File.WriteAllBytes(Path.Combine(Folder, safe + ".xlsx"), ExcelReport.Create(d)); names.Add(safe + ".xlsx"); }
        return names;
    }

    public IEnumerable<FileInfo> Files() =>
        new DirectoryInfo(Folder).EnumerateFiles().Where(f => f.Extension is ".pdf" or ".xlsx").OrderByDescending(f => f.LastWriteTimeUtc);

    /// <summary>A file of the reports folder by name (no paths).</summary>
    public string? FilePath(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..")) return null;
        var p = Path.Combine(Folder, name);
        return File.Exists(p) ? p : null;
    }

    public void Prune()
    {
        if (Settings.KeepDays <= 0) return;
        foreach (var f in Files().Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-Settings.KeepDays)))
            try { f.Delete(); } catch { }
    }
}

/// <summary>Writes each shift's report when the shift ends, and a batch report when the next batch starts.</summary>
public sealed class ShiftReporter : BackgroundService
{
    private readonly ReportService _reports;
    private readonly Station _station;
    private readonly ILogger<ShiftReporter> _logger;
    private readonly string _statePath;
    private BatchRow? _batch;

    public ShiftReporter(ReportService reports, Station station, ILogger<ShiftReporter> logger, IOptions<StudioOptions> opt)
    {
        _reports = reports;
        _station = station;
        _logger = logger;
        _statePath = Path.Combine(opt.Value.DataFolder, "reports", "last-shift.txt");
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        _batch = _reports.Log.Batches(limit: 1).FirstOrDefault();
        while (!token.IsCancellationRequested)
        {
            try { Tick(); } catch (Exception e) { _logger.LogWarning(e, "Automatic report failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), token); } catch (OperationCanceledException) { return; }
        }
    }

    private void Tick()
    {
        var s = _reports.Settings;
        // shift: the previous shift once it has ended, once (also after a restart)
        var prev = _reports.PreviousShift();
        string key = $"{prev.FromLocal:yyyy-MM-dd HH:mm}";
        string done = File.Exists(_statePath) ? File.ReadAllText(_statePath).Trim() : "";
        if (done != key && (s.AutoPdf || s.AutoExcel))
        {
            var q = new ReportQuery(prev.FromLocal.ToUniversalTime(), prev.ToLocal.ToUniversalTime(), null, prev.Title);
            var names = _reports.WriteFiles(q, $"{prev.FromLocal:yyyy-MM-dd HHmm} {prev.Name}", s.AutoPdf, s.AutoExcel);
            File.WriteAllText(_statePath, key);
            _logger.LogInformation("Shift report written: {Files}", string.Join(", ", names));
            _reports.Prune();
        }
        // batch: when a new batch starts, report the one that ended
        var latest = _reports.Log.Batches(limit: 2);
        if (latest.FirstOrDefault() is { } now && (_batch is null || now.Id != _batch.Id))
        {
            if (_batch is not null && s.AutoBatch && latest.FirstOrDefault(b => b.Id == _batch.Id) is { EndedUtc: { } end } ended)
            {
                var q = new ReportQuery(ended.StartedUtc, end, ended.Name, $"Batch {ended.Name} report");
                _reports.WriteFiles(q, $"{ended.StartedUtc.ToLocalTime():yyyy-MM-dd HHmm} Batch {ended.Name}", s.AutoPdf, s.AutoExcel);
            }
            _batch = now;
        }
    }
}
