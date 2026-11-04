namespace VisionCore.Plc;

/// <summary>
/// Line diagnostics block, PLC → vision, 112 bytes at Modbus holding registers 40-95
/// (PLC-INTERFACE.md section 3.3). Counters run from PLC power-up; the vision side works with the
/// change since it last looked, so a PLC restart only resets the baseline.
/// </summary>
public sealed class LineDiagnostics
{
    public const int Size = 112;
    public const ushort Layout = 3;

    [Flags]
    public enum State : ushort
    {
        Running = 1 << 0, Jam = 1 << 1, BinFull = 1 << 2, AirLow = 1 << 3, EStop = 1 << 4, LineStopOut = 1 << 5,
        RejectFailAlarm = 1 << 6, TrackingMismatch = 1 << 7, EncoderFault = 1 << 8, TriggerSensorStuck = 1 << 9,
        VisionLost = 1 << 10, VisionNotReady = 1 << 11, VfdFault = 1 << 12, VfdCommLost = 1 << 13, Learning = 1 << 14, Resuming = 1 << 15,
    }

    public LineDiagnostics(PlcBlock b) => B = b;
    public PlcBlock B { get; }

    public ushort LayoutVersion => B.U16(0);
    public ushort PlcModel => B.U16(2);
    public ushort Inputs => B.U16(4);
    public ushort Outputs => B.U16(6);
    public State States => (State)B.U16(8);
    public ushort PartsPerMinute => B.U16(10);
    public int EncoderCount => B.I32(12);
    public uint TriggerSensorCount => B.U32(16);
    public uint TriggersSent => B.U32(20);
    public uint ResultsReceived => B.U32(24);
    public uint TriggersMissed => B.U32(28);
    public uint ResultTimeouts => B.U32(32);
    public uint RejectsCommanded => B.U32(36);
    public uint RejectsConfirmed => B.U32(40);
    public uint RejectsUnconfirmed => B.U32(44);
    public uint RejectSensorCount => B.U32(48);
    public uint TrackingMismatches => B.U32(52);
    public uint JamEvents => B.U32(56);
    public ushort PulsesPerPartMin => B.U16(60);
    public ushort PulsesPerPartMax => B.U16(62);
    public ushort TriggerSensorOnMsMax => B.U16(64);
    public ushort ConsecutiveNg => B.U16(66);
    public uint EncoderFaults => B.U32(68);
    public uint VisionLossCount => B.U32(72);
    /// <summary>Encoder pulses from the trigger sensor to the reject sensor, learned on the first part (or entered).</summary>
    public int LearnedDistance => B.I32(76);
    /// <summary>Parts seen by the trigger sensor of camera 2 / 3 (camera 1: TriggerSensorCount).</summary>
    public uint CameraSensorCount(int cam) => cam == 0 ? TriggerSensorCount : B.U32(80 + 4 * (cam - 1));
    /// <summary>Parts rejected because a camera did not inspect them (vision not ready, no result).</summary>
    public uint NotInspected => B.U32(88);
    /// <summary>Bypass bits and camera count the PLC is working with (echo of the settings).</summary>
    public ushort BypassEcho => B.U16(92);
    public ushort CameraCountEcho => B.U16(94);
    /// <summary>Product length the PLC works with, pulses (manual or learned; 0 = not known yet).</summary>
    public int ProductLength => B.I32(96);
    /// <summary>Times several products came touching (one sensor signal for 2 or more products).</summary>
    public uint TouchingGroups => B.U32(100);
    public uint LengthOutOfTolerance => B.U32(104);
    public uint SmallGaps => B.U32(108);

    public bool Input(int bit) => (Inputs >> bit & 1) != 0;
    public bool Output(int bit) => (Outputs >> bit & 1) != 0;
}

public enum Health { NotFitted, NoData, Ok, Sometimes, Fault, Bypassed }

public sealed record InstrumentHealth(Instrument Id, string Name, string? Terminal, bool? Signal, Health Health, string Detail);

/// <summary>
/// Turns the diagnostics block into a condition per instrument: OK, misses sometimes (x of y), fault,
/// not fitted. The rules compare counters that should move together (sensor counts against
/// triggers, rejects commanded against rejects confirmed ...).
/// </summary>
public static class LineHealth
{
    private static (Health, string) Ratio(uint bad, uint total, string what, double faultShare = 0.5)
    {
        if (total == 0) return (Health.NoData, "no parts yet");
        if (bad == 0) return (Health.Ok, $"0 {what} in {total}");
        double share = (double)bad / total;
        return share >= faultShare
            ? (Health.Fault, $"{bad} {what} in {total}")
            : (Health.Sometimes, $"{bad} {what} in {total} ({share:P1})");
    }

    public static IReadOnlyList<InstrumentHealth> Evaluate(PlcProfile profile, LineDiagnostics? d, LineSettings? settings = null)
    {
        var list = new List<InstrumentHealth>();
        var bypassed = Enum.GetValues<LineSettings.Bypass>().Where(b => b != 0 && settings is not null && settings.Bypassed.HasFlag(b))
            .Select(LineSettings.InstrumentOf).OfType<Instrument>().ToHashSet();
        if (bypassed.Contains(Instrument.StartButton)) bypassed.Add(Instrument.StopButton);
        var drive = settings?.Drive ?? LineSettings.DriveKind.VfdRs485;
        int cameras = settings?.CameraCount ?? 1;
        foreach (var info in LineProfiles.Instruments)
        {
            profile.Terminals.TryGetValue(info.Id, out var term);
            if (term is null) { list.Add(new(info.Id, info.Name, null, null, Health.NotFitted, "not fitted on this PLC")); continue; }
            var set = settings ?? new LineSettings();
            if (info.Id == Instrument.Camera2Sensor && !set.SensorUsed(2) || info.Id == Instrument.Camera3Sensor && !set.SensorUsed(3))
            { list.Add(new(info.Id, info.Name, term, null, Health.NotFitted, cameras > 1 ? "camera shares another camera's sensor" : "the line has fewer cameras")); continue; }
            if (!UsedBy(info.Id, drive))
            { list.Add(new(info.Id, info.Name, term, null, Health.NotFitted, "not used with this conveyor drive type")); continue; }
            if (bypassed.Contains(info.Id)) { list.Add(new(info.Id, info.Name, term, null, Health.Bypassed, "bypassed in the admin settings: the PLC ignores it")); continue; }
            bool? signal = null;
            if (d is not null && term[0] is 'X' or 'Y')
            {
                int bit = LineProfiles.DiagBit(term);
                if (bit is >= 0 and < 16) signal = term.StartsWith('X') ? d.Input(bit) : d.Output(bit);
            }
            if (d is null || d.LayoutVersion != LineDiagnostics.Layout)
            {
                list.Add(new(info.Id, info.Name, term, signal, Health.NoData, d is null ? "PLC not connected" : "PLC program sends no diagnostics"));
                continue;
            }
            var s = d.States;
            (Health h, string detail) = info.Id switch
            {
                Instrument.EncoderA or Instrument.EncoderB =>
                    s.HasFlag(LineDiagnostics.State.EncoderFault) ? (Health.Fault, "no pulses while the line runs")
                    : d.EncoderFaults > 0 ? (Health.Sometimes, $"{d.EncoderFaults} stops without pulses")
                    : d.PulsesPerPartMax > 0 && d.PulsesPerPartMin > 0 && d.PulsesPerPartMax > 1.3 * d.PulsesPerPartMin
                        ? (Health.Sometimes, $"pulses per part {d.PulsesPerPartMin}–{d.PulsesPerPartMax}: slipping or parts not evenly spaced")
                    : (Health.Ok, $"count {d.EncoderCount}, {d.PulsesPerPartMin}–{d.PulsesPerPartMax} pulses per part"),
                Instrument.TriggerSensor =>
                    s.HasFlag(LineDiagnostics.State.TriggerSensorStuck) ? (Health.Fault, $"on for {d.TriggerSensorOnMsMax} ms: blocked or dirty")
                    : Ratio(InTransitAside(d.TriggerSensorCount, d.RejectSensorCount), d.TriggerSensorCount, "parts counted differently from the reject sensor"),
                Instrument.Camera2Sensor => Ratio(InTransitAside(d.CameraSensorCount(0), d.CameraSensorCount(1)), d.CameraSensorCount(0), "parts counted differently from camera 1"),
                Instrument.Camera3Sensor => Ratio(InTransitAside(d.CameraSensorCount(0), d.CameraSensorCount(2)), d.CameraSensorCount(0), "parts counted differently from camera 1"),
                Instrument.RejectSensor =>
                    s.HasFlag(LineDiagnostics.State.TrackingMismatch) ? (Health.Fault, "part at the rejector does not match the tracked part")
                    : Ratio(d.TrackingMismatches, d.RejectSensorCount, "tracking mismatches"),
                Instrument.RejectVerifySensor =>
                    d.RejectsCommanded >= 10 && d.RejectsConfirmed == 0 ? (Health.Fault, $"{d.RejectsCommanded} rejects, none seen in the chute: sensor or rejector dead")
                    : Ratio(d.RejectsUnconfirmed, d.RejectsCommanded, "rejects not seen in the chute"),
                Instrument.Rejector =>
                    s.HasFlag(LineDiagnostics.State.RejectFailAlarm) ? (Health.Fault, "a rejected part was not removed")
                    : Ratio(d.RejectsUnconfirmed, d.RejectsCommanded, "rejects not confirmed"),
                Instrument.JamSensor =>
                    s.HasFlag(LineDiagnostics.State.Jam) ? (Health.Fault, "line jammed now")
                    : d.JamEvents > 0 ? (Health.Sometimes, $"{d.JamEvents} jams since start") : (Health.Ok, "no jams"),
                Instrument.BinFullSensor => s.HasFlag(LineDiagnostics.State.BinFull) ? (Health.Fault, "reject bin full: empty it") : (Health.Ok, "bin not full"),
                Instrument.AirPressureOk => s.HasFlag(LineDiagnostics.State.AirLow) ? (Health.Fault, "air pressure low: rejector cannot work") : (Health.Ok, "pressure OK"),
                Instrument.EStopOk => s.HasFlag(LineDiagnostics.State.EStop) ? (Health.Fault, "emergency stop pressed") : (Health.Ok, "circuit healthy"),
                Instrument.LineRunning => s.HasFlag(LineDiagnostics.State.Running) ? (Health.Ok, $"running, {d.PartsPerMinute} parts/min") : (Health.Ok, "stopped"),
                Instrument.VisionReadyOut => s.HasFlag(LineDiagnostics.State.VisionLost) ? (Health.Fault, "PLC lost the vision heartbeat")
                    : d.VisionLossCount > 0 ? (Health.Sometimes, $"{d.VisionLossCount} heartbeat losses since start")
                    : Ratio(d.TriggersMissed, d.TriggersSent + d.TriggersMissed, "triggers while vision not ready"),
                Instrument.LineStopOut => s.HasFlag(LineDiagnostics.State.LineStopOut) ? (Health.Fault, "line stop active") : (Health.Ok, "not stopping"),
                Instrument.VisionLight => Ratio(d.ResultTimeouts, d.TriggersSent, "inspections without a result"),
                Instrument.VfdFault => s.HasFlag(LineDiagnostics.State.VfdFault)
                    ? (Health.Fault, drive is LineSettings.DriveKind.DirectOnLine or LineSettings.DriveKind.StarDelta
                        ? "motor overload relay tripped: find the cause, then reset the relay" : "conveyor drive tripped: see the drive display")
                    : (Health.Ok, drive is LineSettings.DriveKind.DirectOnLine or LineSettings.DriveKind.StarDelta ? "overload relay OK" : "drive OK"),
                Instrument.StartButton => (Health.Ok, signal == true ? "pressed" : "released"),
                Instrument.StopButton => signal == false ? (Health.Ok, "pressed (or wire open): line held stopped") : (Health.Ok, "released"),
                Instrument.MotorRun => (Health.Ok, signal == true ? "motor on" : "motor off"),
                Instrument.MotorStar => (Health.Ok, signal == true ? "starting in star" : "off"),
                Instrument.MotorDelta => (Health.Ok, signal == true ? "running in delta" : "off"),
                Instrument.Vfd => s.HasFlag(LineDiagnostics.State.VfdCommLost) ? (Health.Fault, "no answer from the drive on RS-485")
                    : s.HasFlag(LineDiagnostics.State.Running) ? (Health.Ok, $"running, {d.PartsPerMinute} parts/min") : (Health.Ok, "stopped"),
                _ => (Health.Ok, ""),
            };
            list.Add(new(info.Id, info.Name, term, signal, h, detail));
        }
        return list;
    }

    /// <summary>Instruments that belong to one conveyor drive type only.</summary>
    public static bool UsedBy(Instrument i, LineSettings.DriveKind drive) => i switch
    {
        Instrument.Vfd => drive == LineSettings.DriveKind.VfdRs485,
        Instrument.MotorRun => drive != LineSettings.DriveKind.VfdRs485,
        Instrument.MotorDelta or Instrument.MotorStar => drive == LineSettings.DriveKind.StarDelta,
        _ => true,
    };

    // parts between the camera and the rejector are counted by one sensor and not yet by the other
    private const uint InTransit = 5;
    private static uint InTransitAside(uint a, uint b) { uint d = a > b ? a - b : b - a; return d > InTransit ? d - InTransit : 0; }

}
