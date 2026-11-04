using System.Collections.Concurrent;
using System.Text.Json;
using VisionCore;
using VisionCore.Plc;
using VisionReports;

namespace VisionStudio.Services;

/// <summary>
/// Writes everything the reports need to the results log: every camera inspection, every product
/// leaving the line (PLC part log, with its reason note), line events (jams, stops, alarms from the
/// PLC's line state) and batch starts.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly ResultLog _log;
    private readonly Station _station;
    private readonly PlcService _plc;
    // what each camera said about each part number, kept until the part leaves the line
    private readonly ConcurrentDictionary<(int Camera, ushort Part), (DateTime Time, CameraVerdict Verdict, string? ImageId)> _verdicts = new();
    private readonly Timer _timer;
    private LineDiagnostics.State? _lastState;

    private static readonly (LineDiagnostics.State Bit, string Kind, string Detail)[] Conditions =
    {
        (LineDiagnostics.State.Jam, "jam", "jam sensor on longer than the jam time"),
        (LineDiagnostics.State.EStop, "estop", "E-stop pressed"),
        (LineDiagnostics.State.AirLow, "air", "air pressure low"),
        (LineDiagnostics.State.BinFull, "bin", "reject bin full"),
        (LineDiagnostics.State.VfdFault, "drive", "conveyor drive fault relay"),
        (LineDiagnostics.State.VfdCommLost, "drive_comm", "no RS-485 answer from the drive"),
        (LineDiagnostics.State.VisionLost, "vision_lost", "vision heartbeat lost"),
        (LineDiagnostics.State.RejectFailAlarm, "reject_fail", "a reject was not seen in the chute"),
        (LineDiagnostics.State.TrackingMismatch, "tracking", "part not at the reject sensor where expected"),
        (LineDiagnostics.State.EncoderFault, "encoder", "no encoder pulses while running"),
        (LineDiagnostics.State.TriggerSensorStuck, "trigger_stuck", "camera 1 sensor on for more than 3 s"),
        (LineDiagnostics.State.LineStopOut, "line_stop", "line stop output on"),
    };

    public Recorder(ResultLog log, Station station, PlcService plc)
    {
        _log = log;
        _station = station;
        _plc = plc;
        station.Reported += OnInspected;
        station.BatchStarted += OnBatch;
        plc.PartLeft += OnPart;
        _timer = new Timer(_ => WatchLine(), null, 1000, 500);
    }

    public ResultLog Log => _log;
    /// <summary>Who started the next batch (set by the page before SetBatch).</summary>
    public string User { get; set; } = "";

    private string Batch => BatchName(_station.Batch);
    private string JobName => _station.Job?.Name ?? "";

    public static string BatchName(BatchFields b) =>
        b.Get("BATCH") ?? b.Values.FirstOrDefault(kv => kv.Key.Contains("BATCH", StringComparison.OrdinalIgnoreCase)).Value
        ?? b.Values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private void OnInspected(CameraReport c)
    {
        var (camera, part, r, error) = (c.Camera, c.Part, c.Result, c.Error);
        try
        {
            var fail = r?.Tools.FirstOrDefault(t => !t.Pass);
            string? reason = error ?? fail?.Summary;   // the specific text, e.g. "expected HWN937, found HWN881"
            string? text1 = r?.Tools.FirstOrDefault(t => t.Pass && t.Lines is null && t.Summary.Length > 0)?.Summary;
            string? text2 = r?.Tools.Select(t => t.Lines?.FirstOrDefault(l => l.Checked)?.Text ?? t.Lines?.FirstOrDefault()?.Text).FirstOrDefault(x => x is not null);
            bool ok = r?.Pass == true && error is null;
            // a part the PLC got as NG (e.g. NG 100) after an OK inspection is logged as that NG (PLC-INTERFACE 5.6)
            string? failTool = fail?.ToolName ?? (error is null ? null : c.ByPlc ? "PLC link" : "No picture");
            // the job and batch it was inspected with: a PLC part is logged when the PLC took it
            var batch = c.Batch is { } b ? BatchName(b) : Batch;
            _log.AddInspection(new InspectionRow(0, DateTime.UtcNow, batch, c.JobName ?? JobName, camera, part, ok,
                failTool, reason, text1, text2, r?.TotalMs ?? 0, string.IsNullOrWhiteSpace(c.PartId) ? null : c.PartId.Trim(), c.ImageId));
            if (part is { } p)
            {
                _verdicts[(camera, p)] = (DateTime.UtcNow, new CameraVerdict(camera, ok, failTool, reason), c.ImageId);
                if (_verdicts.Count > 500)
                    foreach (var k in _verdicts.Where(kv => kv.Value.Time < DateTime.UtcNow.AddMinutes(-5)).Select(kv => kv.Key).ToList())
                        _verdicts.TryRemove(k, out _);
            }
        }
        catch { /* logging must never stop inspection */ }
    }

    private void OnPart(PartRecord rec)
    {
        try
        {
            var line = _plc.Line;
            int cameras = Math.Max(1, line.CameraCount);
            var verdicts = new List<CameraVerdict>();
            string? imageId = null;   // the first camera's image name: any camera's name finds the part's images
            for (int c = 0; c < cameras; c++)
                if (_verdicts.TryRemove((c, rec.PartId), out var v)) { verdicts.Add(v.Verdict); imageId ??= v.ImageId; }
            double? len = rec.LengthPulses > 0 ? line.MmOf(rec.LengthPulses) : null;
            double? gap = rec.GapPulses > 0 ? line.MmOf(rec.GapPulses) : null;
            double? nominal = line.ProductLengthMm > 0 ? line.ProductLengthMm
                : _plc.Runtime?.Diagnostics() is { ProductLength: > 0 } d ? line.MmOf(d.ProductLength) : null;
            var (cat, reason) = PartReasons.Explain(rec, cameras, verdicts, len, nominal, line.LengthTolerancePct, gap,
                line.MinGapMm > 0 ? line.MinGapMm : null);
            _log.AddPart(new PartRow(0, DateTime.UtcNow, Batch, JobName, rec.PartId, rec.Rejected, rec.Flags, len, gap,
                rec.GroupSize, rec.GroupIndex, rec.CamAnswered, rec.CamOk, cat, reason, imageId));
        }
        catch { }
    }

    private void OnBatch(BatchFields b)
    {
        try
        {
            _log.StartBatch(BatchName(b), JobName, User, JsonSerializer.Serialize(b.Values), DateTime.UtcNow);
            _log.AddEvent(DateTime.UtcNow, "batch", false, $"Batch {BatchName(b)} started ({JobName})");
        }
        catch { }
    }

    /// <summary>Settings changes worth a line in the reports (who changed what).</summary>
    public void Note(string kind, string detail)
    {
        try { _log.AddEvent(DateTime.UtcNow, kind, false, detail); } catch { }
    }

    private void WatchLine()
    {
        try
        {
            if (_plc.Runtime?.Diagnostics() is not { } d) return;
            var now = d.States;
            var was = _lastState;
            _lastState = now;
            if (was is null) return;   // first look: no edges yet
            foreach (var (bit, kind, detail) in Conditions)
            {
                bool on = now.HasFlag(bit), before = was.Value.HasFlag(bit);
                if (on != before) _log.AddEvent(DateTime.UtcNow, kind, on, on ? detail : "cleared");
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _station.Reported -= OnInspected;
        _station.BatchStarted -= OnBatch;
        _plc.PartLeft -= OnPart;
    }
}
