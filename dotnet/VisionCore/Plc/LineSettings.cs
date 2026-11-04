using System.Text.Json;

namespace VisionCore.Plc;

/// <summary>
/// Line settings the PLC works with, kept on the vision PC and edited on the Line page. Vision → PLC,
/// 64 bytes at Modbus holding registers 300-331 (PLC-INTERFACE.md section 3.4); the PLC reads them
/// with its Modbus TCP client and takes them over when <c>Version</c> changes.
/// </summary>
public sealed class LineSettings
{
    public const int Size = 64;
    public const ushort Layout = 1;

    /// <summary>Conveyor speed sent to the drive, Hz.</summary>
    public float ConveyorHz { get; set; } = 30;
    /// <summary>From the trigger sensor edge to light on, ms.</summary>
    public int LightDelayMs { get; set; } = 0;
    /// <summary>Light on time, ms (0 = on while the line runs).</summary>
    public int LightOnMs { get; set; } = 0;
    /// <summary>From the trigger sensor edge to the vision trigger, ms (lets the part reach the centre of the view).</summary>
    public int CameraDelayMs { get; set; } = 0;
    /// <summary>true = reject by encoder distance from the trigger sensor; false = reject sensor edge + delay time.</summary>
    public bool RejectByEncoder { get; set; } = true;
    /// <summary>Encoder pulses from the trigger sensor to the reject sensor (manual value; learned when LearnAuto).</summary>
    public int RejectDistance { get; set; } = 2000;
    /// <summary>Pulses from the reject sensor to the rejector's firing point (may be negative).</summary>
    public int RejectOffset { get; set; } = 0;
    /// <summary>Reject sensor edge to firing, ms (when not rejecting by encoder).</summary>
    public int RejectDelayMs { get; set; } = 30;
    public int RejectOnMs { get; set; } = 60;
    public int VerifyWindowMs { get; set; } = 400;
    public int JamTimeMs { get; set; } = 2000;
    /// <summary>After a jam clears, wait this long and restart the conveyor.</summary>
    public int ResumeDelayMs { get; set; } = 3000;
    /// <summary>Learn the reject distance on the first part of every auto start (else use RejectDistance).</summary>
    public bool LearnAuto { get; set; } = true;
    public int MaxConsecutiveNg { get; set; } = 5;
    public bool StopOnRejectFail { get; set; } = true;
    /// <summary>Instruments this installation does not have (admin only): the PLC ignores them.</summary>
    public Bypass Bypassed { get; set; }
    /// <summary>Cameras along the line (1-3), each with its own trigger sensor; one rejector for all.</summary>
    public int CameraCount { get; set; } = 1;
    /// <summary>Which camera's trigger sensor fires camera 2 / 3 (1-based). Its own number = its own
    /// sensor at its own place; a lower number = same place, e.g. front and back of the pack read
    /// together with the same part number.</summary>
    public int Camera2Sensor { get; set; } = 2;
    public int Camera3Sensor { get; set; } = 3;

    /// <summary>Trigger sensor (1-3) that fires camera c (0-based), limited to valid choices.</summary>
    public int SensorOf(int c) => c switch
    {
        0 => 1,
        1 => Math.Clamp(Camera2Sensor, 1, 2),
        _ => Math.Clamp(Camera3Sensor, 1, 3) == 3 ? 3 : Math.Clamp(Camera3Sensor, 1, 2),
    };

    /// <summary>True when trigger sensor s (1-3) is wired and used by some camera.</summary>
    public bool SensorUsed(int s) => Enumerable.Range(0, Math.Clamp(CameraCount, 1, 3)).Any(c => SensorOf(c) == s);
    /// <summary>Encoder pulses per metre of conveyor travel (0 = not calibrated: no lengths in mm).</summary>
    public int PulsesPerMetre { get; set; }
    /// <summary>Calibration helper (not sent): encoder pulses per revolution as counted by the PLC
    /// (x4 for A/B quadrature counting) and the measuring roller diameter.</summary>
    public int EncoderPpr { get; set; } = 1000;
    public double RollerDiameterMm { get; set; } = 50;
    public int CalculatedPulsesPerMetre => RollerDiameterMm > 0 ? (int)Math.Round(EncoderPpr * 1000 / (Math.PI * RollerDiameterMm)) : 0;
    /// <summary>Nominal product length along the conveyor, mm (0 = the PLC learns it from the first parts).</summary>
    public int ProductLengthMm { get; set; }
    /// <summary>A product this much longer or shorter than nominal is out of tolerance, %.</summary>
    public int LengthTolerancePct { get; set; } = 20;
    /// <summary>A gap below this between two products is reported (0 = off), mm.</summary>
    public int MinGapMm { get; set; }
    /// <summary>Reject products that came touching (no gap: one long sensor signal for several products).</summary>
    public bool RejectTouching { get; set; } = true;
    /// <summary>Reject products out of the length tolerance (fallen over, lying, broken, half pack).</summary>
    public bool RejectLengthOut { get; set; } = true;

    /// <summary>How the PLC runs the conveyor motor.</summary>
    public DriveKind Drive { get; set; } = DriveKind.VfdRs485;
    /// <summary>Star-delta: time in star before changing to delta, ms.</summary>
    public int StarDeltaMs { get; set; } = 5000;
    /// <summary>Star-delta: gap between star off and delta on (both contactors open), ms.</summary>
    public int ChangeoverMs { get; set; } = 100;
    /// <summary>Drive fault / overload input is a normally closed contact (on = healthy, a broken wire trips):
    /// overload relay 95-96 or VFD RB-RC. Off = normally open (closes on a fault): VFD RA-RC, overload 97-98.</summary>
    public bool DriveFaultNc { get; set; }

    public enum DriveKind : ushort
    {
        VfdRs485 = 0,       // Delta VFD: run / stop / speed / status over RS-485 Modbus RTU
        VfdTerminal = 1,    // VFD run by a PLC output (FWD terminal), speed set on the drive
        DirectOnLine = 2,   // one contactor KM1 and an overload relay
        StarDelta = 3,      // KM1 main, KM3 star, KM2 delta, overload relay
    }

    public int PulsesOf(double mm) => (int)Math.Round(mm * PulsesPerMetre / 1000.0);
    public double? MmOf(double pulses) => PulsesPerMetre > 0 ? pulses * 1000.0 / PulsesPerMetre : null;

    /// <summary>Learn again on the next part (one shot, cleared after sending).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool RelearnNow { get; set; }

    public void Write(PlcBlock b, ushort version)
    {
        b.SetU16(0, Layout);
        b.SetU16(2, (ushort)Math.Clamp((int)Math.Round(ConveyorHz * 100), 0, 60000));
        b.SetU16(4, (ushort)Math.Clamp(LightDelayMs, 0, 60000));
        b.SetU16(6, (ushort)Math.Clamp(LightOnMs, 0, 60000));
        b.SetU16(8, (ushort)Math.Clamp(CameraDelayMs, 0, 60000));
        b.SetU16(10, (ushort)(RejectByEncoder ? 0 : 1));
        b.SetU32(12, unchecked((uint)RejectDistance));
        b.SetU16(16, unchecked((ushort)(short)Math.Clamp(RejectOffset, short.MinValue, short.MaxValue)));
        b.SetU16(18, (ushort)Math.Clamp(RejectDelayMs, 0, 60000));
        b.SetU16(20, (ushort)Math.Clamp(RejectOnMs, 5, 5000));
        b.SetU16(22, (ushort)Math.Clamp(VerifyWindowMs, 10, 10000));
        b.SetU16(24, (ushort)Math.Clamp(JamTimeMs, 100, 60000));
        b.SetU16(26, (ushort)Math.Clamp(ResumeDelayMs, 0, 60000));
        b.SetU16(28, (ushort)(LearnAuto ? 1 : 0));
        b.SetU16(30, (ushort)Math.Clamp(MaxConsecutiveNg, 1, 1000));
        b.SetU16(32, (ushort)(StopOnRejectFail ? 1 : 0));
        b.SetU16(34, (ushort)(RelearnNow ? 1 : 0));
        b.SetU16(36, version);
        b.SetU16(38, (ushort)Bypassed);
        b.SetU16(40, (ushort)Math.Clamp(CameraCount, 1, 3));
        b.Bytes[42] = (byte)SensorOf(1);
        b.Bytes[43] = (byte)SensorOf(2);
        b.SetU32(44, (uint)Math.Max(0, PulsesPerMetre));
        b.SetU16(48, (ushort)Math.Clamp(ProductLengthMm, 0, 65535));
        b.SetU16(50, (ushort)Math.Clamp(LengthTolerancePct, 1, 90));
        b.SetU16(52, (ushort)Math.Clamp(MinGapMm, 0, 65535));
        b.SetU16(54, (ushort)((RejectTouching ? 1 : 0) | (RejectLengthOut ? 2 : 0) | (DriveFaultNc ? 4 : 0)));
        b.SetU16(56, (ushort)Drive);
        b.SetU16(58, (ushort)Math.Clamp(StarDeltaMs, 500, 30000));
        b.SetU16(60, (ushort)Math.Clamp(ChangeoverMs, 30, 1000));
    }

    [Flags]
    public enum Bypass : ushort
    {
        None = 0,
        RejectVerify = 1 << 0,     // no reject verification sensor: rejects are not checked
        JamSensor = 1 << 1,        // no jam sensor
        BinFull = 1 << 2,
        AirPressure = 1 << 3,
        RejectSensor = 1 << 4,     // no reject position sensor: no learning or tracking check (manual distance)
        EStop = 1 << 5,            // E-stop status not wired to the PLC
        StartStop = 1 << 6,        // no start / stop push buttons: the line runs with the auto input only
        AutoInput = 1 << 7,        // no "line running / auto" input: the start / stop buttons run the line
    }

    public static Instrument? InstrumentOf(Bypass b) => b switch
    {
        Bypass.RejectVerify => Instrument.RejectVerifySensor, Bypass.JamSensor => Instrument.JamSensor,
        Bypass.BinFull => Instrument.BinFullSensor, Bypass.AirPressure => Instrument.AirPressureOk,
        Bypass.RejectSensor => Instrument.RejectSensor, Bypass.EStop => Instrument.EStopOk,
        Bypass.StartStop => Instrument.StartButton, Bypass.AutoInput => Instrument.LineRunning, _ => null,
    };

    public static LineSettings Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<LineSettings>(File.ReadAllText(path)) ?? new() : new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
