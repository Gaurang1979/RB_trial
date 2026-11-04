namespace VisionCore.Plc;

/// <summary>An instrument on the line wired to the PLC (sensor, encoder, valve, lamp ...).</summary>
public enum Instrument
{
    EncoderA, EncoderB,
    TriggerSensor,          // product at camera 1 (first camera; parts get their ID here)
    Camera2Sensor,          // product at camera 2 (cameras at different places)
    Camera3Sensor,
    RejectSensor,           // product at the rejector
    RejectVerifySensor,     // product really left the line (in the reject chute)
    JamSensor,
    BinFullSensor,
    AirPressureOk,
    LineRunning,            // from the machine: conveyor running in automatic
    EStopOk,                // emergency-stop circuit healthy (NC contact)
    ResetButton,
    StartButton,            // line start push button, NO
    StopButton,             // line stop push button, NC (on = not pressed)
    VfdFault,               // conveyor drive fault relay or motor overload relay
    Rejector,               // valve / pusher
    VisionLight,            // light controller on/off input
    VisionReadyOut,         // to the machine: vision system ready
    LineStopOut,            // to the machine: stop the conveyor
    TowerGreen, TowerAmber, TowerRed, Buzzer,
    Vfd,                    // conveyor drive on the PLC's RS-485 port (run/stop, speed, status)
    MotorRun,               // KM1 main contactor (direct-on-line / star-delta) or VFD run (FWD) by terminal
    MotorDelta,             // KM2 delta contactor (star-delta)
    MotorStar,              // KM3 star contactor (star-delta)
}

public enum InstrumentKind { Input, CounterInput, Output, Serial }

/// <summary>How an instrument is connected.</summary>
public sealed record InstrumentInfo(Instrument Id, InstrumentKind Kind, string Name, string Device, string Wiring);

/// <summary>A PLC model: its terminals and where each instrument goes by default.</summary>
public sealed record PlcProfile(
    string Key, string Make, string Model, int Inputs, int Outputs, string OutputType, int CounterChannels,
    string Supply, string Software, IReadOnlyDictionary<Instrument, string> Terminals, string Notes)
{
    public string Title => $"{Make} {Model}";

    /// <summary>Every input terminal in order (DVP slim: CPU X0-X7 then the module from X20, octal).</summary>
    public IReadOnlyList<string> InputTerminals => Terminals.Values.Any(t => t.Contains('.'))
        ? Enumerable.Range(0, Inputs).Select(i => $"X0.{i}").ToList()
        : Enumerable.Range(0, 8).Select(i => $"X{i}").Concat(Enumerable.Range(0, Inputs - 8).Select(i => $"X{20 + i}")).ToList();

    /// <summary>Every output terminal in order (DVP slim: CPU outputs then the module from Y20).</summary>
    public IReadOnlyList<string> OutputTerminals => Terminals.Values.Any(t => t.Contains('.'))
        ? Enumerable.Range(0, Outputs).Select(i => $"Y0.{i}").ToList()
        : Enumerable.Range(0, Outputs - 8).Select(i => $"Y{i}").Concat(Enumerable.Range(0, 8).Select(i => $"Y{20 + i}")).ToList();
}

/// <summary>Instrument catalogue and PLC model profiles (docs/vision-system/PLC-DELTA-AS.md).</summary>
public static class LineProfiles
{
    public static readonly IReadOnlyList<InstrumentInfo> Instruments = new InstrumentInfo[]
    {
        new(Instrument.EncoderA, InstrumentKind.CounterInput, "Encoder A", "Incremental encoder, 24 V push-pull or PNP, 500–2000 ppr", "brown +24 V, blue 0 V, black A"),
        new(Instrument.EncoderB, InstrumentKind.CounterInput, "Encoder B", "Same encoder, channel B (direction)", "white B"),
        new(Instrument.TriggerSensor, InstrumentKind.Input, "Camera 1 trigger sensor", "Photoelectric, PNP, NO, at camera 1 (parts get their number here)", "brown +24 V, blue 0 V, black signal"),
        new(Instrument.Camera2Sensor, InstrumentKind.Input, "Camera 2 trigger sensor", "Photoelectric, PNP, NO, at camera 2", "brown +24 V, blue 0 V, black signal"),
        new(Instrument.Camera3Sensor, InstrumentKind.Input, "Camera 3 trigger sensor", "Photoelectric, PNP, NO, at camera 3", "brown +24 V, blue 0 V, black signal"),
        new(Instrument.RejectSensor, InstrumentKind.Input, "Reject position sensor", "Photoelectric, PNP, NO, just before the rejector", "brown +24 V, blue 0 V, black signal"),
        new(Instrument.RejectVerifySensor, InstrumentKind.Input, "Reject verification sensor", "Photoelectric, PNP, NO, across the reject chute", "brown +24 V, blue 0 V, black signal"),
        new(Instrument.JamSensor, InstrumentKind.Input, "Line jam sensor", "Photoelectric, PNP, NO, after the rejector (product backing up)", "brown +24 V, blue 0 V, black signal"),
        new(Instrument.BinFullSensor, InstrumentKind.Input, "Reject bin full", "Photoelectric or capacitive, PNP, NO, at the bin top", "brown +24 V, blue 0 V, black signal"),
        new(Instrument.AirPressureOk, InstrumentKind.Input, "Air pressure OK", "Pressure switch on the rejector air line, NO (closed = pressure OK)", "+24 V through the contact"),
        new(Instrument.LineRunning, InstrumentKind.Input, "Line running", "Dry contact from the machine: conveyor running in automatic", "+24 V through the contact"),
        new(Instrument.EStopOk, InstrumentKind.Input, "E-stop circuit OK", "Auxiliary NO contact of the safety relay (closed = healthy)", "+24 V through the contact"),
        new(Instrument.ResetButton, InstrumentKind.Input, "Alarm reset button", "Push button, NO", "+24 V through the contact"),
        new(Instrument.StartButton, InstrumentKind.Input, "Start push button", "Green push button, NO contact (3-4)", "+24 V through 3-4"),
        new(Instrument.StopButton, InstrumentKind.Input, "Stop push button", "Red push button, NC contact (1-2): on = not pressed, a broken wire stops the line", "+24 V through 1-2"),
        new(Instrument.VfdFault, InstrumentKind.Input, "Conveyor drive fault / overload", "VFD fault relay (RA-RC NO or RB-RC NC) or motor overload relay (95-96 NC or 97-98 NO); set NC / NO on the Line page", "+24 V through the contact"),
        new(Instrument.Rejector, InstrumentKind.Output, "Rejector valve", "24 V DC solenoid valve (air blast or pusher), with a flyback diode", "+24 V to the coil, coil to the output"),
        new(Instrument.VisionLight, InstrumentKind.Output, "Vision light on/off", "Light controller enable / trigger input (24 V)", "output to the controller input"),
        new(Instrument.VisionReadyOut, InstrumentKind.Output, "Vision ready (to machine)", "24 V relay, contact to the machine PLC", "+24 V to the coil, coil to the output"),
        new(Instrument.LineStopOut, InstrumentKind.Output, "Line stop (to machine)", "24 V relay, NC contact in the machine's stop circuit", "+24 V to the coil, coil to the output"),
        new(Instrument.TowerGreen, InstrumentKind.Output, "Tower lamp green", "24 V LED tower, green", "common +24 V, green to the output"),
        new(Instrument.TowerAmber, InstrumentKind.Output, "Tower lamp amber", "24 V LED tower, amber", "amber to the output"),
        new(Instrument.TowerRed, InstrumentKind.Output, "Tower lamp red", "24 V LED tower, red", "red to the output"),
        new(Instrument.Buzzer, InstrumentKind.Output, "Buzzer", "24 V buzzer in the tower", "buzzer to the output"),
        new(Instrument.MotorRun, InstrumentKind.Output, "Motor run (KM1 / VFD FWD)", "Main contactor KM1 coil through an interposing relay, or the VFD FWD input", "relay coil +24 V to the output; contact to KM1 A1 / VFD FWD"),
        new(Instrument.MotorDelta, InstrumentKind.Output, "Delta contactor KM2", "Star-delta only; electrically interlocked with KM3 (NC aux contact)", "relay coil +24 V to the output; contact to KM2 A1"),
        new(Instrument.MotorStar, InstrumentKind.Output, "Star contactor KM3", "Star-delta only; electrically interlocked with KM2 (NC aux contact)", "relay coil +24 V to the output; contact to KM3 A1"),
        new(Instrument.Vfd, InstrumentKind.Serial, "Conveyor drive (VFD)", "Delta VFD, Modbus RTU on RS-485: run / stop, speed, status", "PLC RS-485 + / - to VFD SG+ / SG-, shield at one end"),
    };

    public static InstrumentInfo Info(Instrument i) => Instruments.First(x => x.Id == i);

    private static Dictionary<Instrument, string> Full16x12() => new()
    {
        [Instrument.EncoderA] = "X0.0", [Instrument.EncoderB] = "X0.1",
        [Instrument.TriggerSensor] = "X0.2", [Instrument.RejectSensor] = "X0.3", [Instrument.RejectVerifySensor] = "X0.4",
        [Instrument.JamSensor] = "X0.5", [Instrument.BinFullSensor] = "X0.6", [Instrument.AirPressureOk] = "X0.7",
        [Instrument.LineRunning] = "X0.8", [Instrument.EStopOk] = "X0.9", [Instrument.ResetButton] = "X0.10",
        [Instrument.Camera2Sensor] = "X0.11", [Instrument.Camera3Sensor] = "X0.12", [Instrument.VfdFault] = "X0.13",
        [Instrument.StartButton] = "X0.14", [Instrument.StopButton] = "X0.15",
        [Instrument.MotorRun] = "Y0.8", [Instrument.MotorDelta] = "Y0.9", [Instrument.MotorStar] = "Y0.10",
        [Instrument.Vfd] = "COM1",
        [Instrument.Rejector] = "Y0.0", [Instrument.VisionLight] = "Y0.1", [Instrument.VisionReadyOut] = "Y0.2",
        [Instrument.LineStopOut] = "Y0.3", [Instrument.TowerGreen] = "Y0.4", [Instrument.TowerAmber] = "Y0.5",
        [Instrument.TowerRed] = "Y0.6", [Instrument.Buzzer] = "Y0.7",
    };

    // Delta AS series figures: Delta AS-series catalogue, "AS200 CPUs" and "Product Specifications" pages.
    // DVP slim: CPU I/O X0-X7 / Y0-..; the first right-side digital module counts from X20 / Y20 (check the
    // I/O numbering in WPLSoft/ISPSoft for your module order). Counter C251 = A/B phase on X0 / X1 (check).
    private static Dictionary<Instrument, string> DvpSlim16() => new()
    {
        [Instrument.EncoderA] = "X0", [Instrument.EncoderB] = "X1",
        [Instrument.TriggerSensor] = "X2", [Instrument.RejectSensor] = "X3", [Instrument.RejectVerifySensor] = "X4",
        [Instrument.JamSensor] = "X5", [Instrument.AirPressureOk] = "X6", [Instrument.EStopOk] = "X7",
        [Instrument.LineRunning] = "X20", [Instrument.BinFullSensor] = "X21", [Instrument.ResetButton] = "X22", [Instrument.VfdFault] = "X23",
        [Instrument.Camera2Sensor] = "X24", [Instrument.Camera3Sensor] = "X25",
        [Instrument.StartButton] = "X26", [Instrument.StopButton] = "X27",
        [Instrument.Rejector] = "Y0", [Instrument.VisionLight] = "Y1", [Instrument.VisionReadyOut] = "Y2", [Instrument.LineStopOut] = "Y3",
        [Instrument.TowerGreen] = "Y20", [Instrument.TowerAmber] = "Y21", [Instrument.TowerRed] = "Y22", [Instrument.Buzzer] = "Y23",
        [Instrument.MotorRun] = "Y24", [Instrument.MotorDelta] = "Y25", [Instrument.MotorStar] = "Y26",
        [Instrument.Vfd] = "COM2",
    };

    public static readonly IReadOnlyList<PlcProfile> Profiles = new PlcProfile[]
    {
        new("delta-dvp12se-16sp", "Delta", "DVP12SE11T + DVP16SP11T", 16, 12, "transistor NPN (sinking)", 0,
            "24 V DC from the SMPS (CPU, module, sensors, outputs)", "ISPSoft / WPLSoft",
            DvpSlim16(),
            "DVP12SE11T (8 DI / 4 DO, built-in Ethernet) + DVP16SP11T (8 DI / 8 DO) on the right. Ethernet: Modbus TCP data exchange to the vision PC. COM2 RS-485 to the conveyor VFD. Spare: Y27."),
        new("delta-dvp14ss2-16sp", "Delta", "DVP14SS211T + DVP16SP11T", 16, 14, "transistor NPN (sinking)", 0,
            "24 V DC from the SMPS (CPU, module, sensors, outputs)", "ISPSoft / WPLSoft",
            DvpSlim16(),
            "DVP14SS211T (8 DI / 6 DO) + DVP16SP11T. The SS2 takes no Ethernet module (DVP-EN01/EN02-SL list SA2, SE, SV2, SX2 only): the vision link must be RS-485 Modbus RTU (USB-RS485 on the vision PC), not yet in Vision Studio. Then the VFD needs the other RS-485 port. Spare: Y4, Y5, Y27."),
        new("delta-dvp12se-16sp-r", "Delta", "DVP12SE11R + DVP16SP11R", 16, 12, "relay, 2 A per point", 0,
            "24 V DC from the SMPS (CPU, module, sensors); relay commons from +24 V (or 230 V AC for AC coils, never mixed in one group)", "ISPSoft / WPLSoft",
            DvpSlim16(),
            "Relay outputs: each common (C0, C1 ...) feeds its group of outputs; the load goes between the output and 0 V. Not for the encoder-timed rejector valve if it fires faster than about 20 times a second (relay life): use the transistor (T) models then. Spare: Y27."),
        new("delta-dvp14ss2r-16sp", "Delta", "DVP14SS211R + DVP16SP11R", 16, 14, "relay, 2 A per point", 0,
            "24 V DC from the SMPS (CPU, module, sensors); relay commons C0 → Y0, C1 → Y1, C2 → Y2–Y5 (check the label on your CPU)", "ISPSoft / WPLSoft",
            DvpSlim16(),
            "Relay outputs as in the usual SS2 panel: C0 / C1 / C2 from +24 V, load between the output and 0 V. The SS2 takes no Ethernet module: the vision link must be RS-485 Modbus RTU (not yet in Vision Studio). Spare: Y4, Y5, Y27."),
        new("delta-as228t", "Delta", "AS228T-A", 16, 12, "transistor NPN (sinking), 0.5 A", 4, "CPU supply: see the model instruction sheet; sensors and outputs: separate 24 V DC", "ISPSoft 3.0",
            Full16x12(),
            "16 DI / 12 DO, 4 high-speed counters 200 kHz, Modbus TCP client/server. COM1 RS-485 to the conveyor VFD (check the port on your CPU). Spare: Y0.11."),
        new("delta-as332t", "Delta", "AS332T-A", 16, 16, "transistor NPN (sinking), 0.5 A", 6, "AS-PS02A power module (100–240 V AC in); sensors and outputs: separate 24 V DC", "ISPSoft 3.0",
            Full16x12(),
            "16 DI / 16 DO, 6 high-speed counters 200 kHz. COM1 RS-485 to the conveyor VFD (check the port on your CPU). Spare: Y0.11–Y0.15."),
        new("delta-as218tx", "Delta", "AS218TX-A", 8, 6, "transistor NPN (sinking), 0.5 A", 4, "CPU supply: see the model instruction sheet; sensors and outputs: separate 24 V DC", "ISPSoft 3.0",
            new Dictionary<Instrument, string>
            {
                [Instrument.EncoderA] = "X0.0", [Instrument.EncoderB] = "X0.1",
                [Instrument.TriggerSensor] = "X0.2", [Instrument.RejectSensor] = "X0.3", [Instrument.RejectVerifySensor] = "X0.4",
                [Instrument.JamSensor] = "X0.5", [Instrument.AirPressureOk] = "X0.6", [Instrument.LineRunning] = "X0.7",
                [Instrument.Rejector] = "Y0.0", [Instrument.VisionLight] = "Y0.1", [Instrument.VisionReadyOut] = "Y0.2",
                [Instrument.LineStopOut] = "Y0.3", [Instrument.TowerGreen] = "Y0.4", [Instrument.TowerRed] = "Y0.5",
            },
            "8 DI / 6 DO (+2 AI / 2 AO). Not fitted for lack of points: bin full, E-stop status (wire the E-stop in series with Line running), reset button (reset from the HMI or vision PC), amber lamp, buzzer."),
    };

    public static PlcProfile Profile(string key) => Profiles.FirstOrDefault(p => p.Key == key) ?? Profiles[0];

    /// <summary>Terminal number inside its group: X23 -> 3 (input bits of the diagnostics block are X0-X7 then X20-X27).</summary>
    public static int DiagBit(string term)
    {
        // DVP: X0..X7 -> bits 0..7, X20..X27 -> bits 8..15 (octal numbering); AS: X0.n -> bit n
        if (term.Contains('.')) return int.Parse(term.Split('.')[1]);
        int n = int.Parse(term[1..]);
        return n < 20 ? n : 8 + (n - 20);
    }
}
