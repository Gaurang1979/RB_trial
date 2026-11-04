using System.Text.Json;
using Microsoft.Extensions.Options;
using VisionCore;
using VisionCore.Plc;

namespace VisionStudio.Services;

/// <summary>
/// The PLC link of this PC: settings in data/plc.json (changed on the PLC page), the protocol link and
/// the handshake runtime over the station. Restarted whenever the settings change.
/// </summary>
public sealed class PlcService : IDisposable
{
    private readonly Station _station;
    private readonly JobStore _jobs;
    private readonly string _path;
    private readonly string _linePath;
    private readonly object _lock = new();

    public PlcService(Station station, JobStore jobs, IOptions<StudioOptions> opt)
    {
        _station = station;
        _jobs = jobs;
        _path = Path.Combine(opt.Value.DataFolder, "plc.json");
        _linePath = Path.Combine(opt.Value.DataFolder, "line.json");
        Line = LineSettings.Load(_linePath);
        Options = File.Exists(_path) ? JsonSerializer.Deserialize<PlcOptions>(File.ReadAllText(_path)) ?? new() : new();
        try { Restart(); } catch (Exception e) { LastError = e.Message; }
    }

    public PlcOptions Options { get; private set; }
    /// <summary>The Modbus TCP server's limits on a shared network (PLC-INTERFACE.md 2.1), shown on the PLC page.</summary>
    public ModbusServerOptions ModbusLimits { get; } = new();
    /// <summary>Line settings the PLC works with (Line page).</summary>
    public LineSettings Line { get; private set; }

    public void SaveLine(LineSettings s)
    {
        s.Save(_linePath);
        Line = s;
        Runtime?.PublishSettings(s);
        s.RelearnNow = false;
        Changed?.Invoke();
    }
    public PlcRuntime? Runtime { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;
    /// <summary>A product left the line (part log from the PLC), whichever runtime is active.</summary>
    public event Action<PartRecord>? PartLeft;

    public void Save(PlcOptions o)
    {
        Options = o;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        Restart();
    }

    public void Restart()
    {
        lock (_lock)
        {
            LastError = null;
            if (Runtime is { } old) { old.Changed -= OnChanged; old.PartLeft -= OnPart; old.Dispose(); }
            Runtime = null;
            try
            {
                IPlcLink? link = Options.Protocol switch
                {
                    "ModbusTcp" => new ModbusServer(Options.Port, Options.BindIp(), ModbusLimits),
                    _ => null,
                };
                if (link is not null)
                {
                    Runtime = new PlcRuntime(link, _station, Options);
                    Runtime.Changed += OnChanged;
                    Runtime.PartLeft += OnPart;
                    Runtime.PublishSettings(Line);
                }
            }
            catch (Exception e)
            {
                LastError = e.Message.Contains("ermission") || e.Message.Contains("in use")
                    ? $"Port {Options.Port} cannot be opened: {e.Message}"
                    : e.Message;
            }
        }
        Changed?.Invoke();
    }

    /// <summary>The PLC page's test signal (PLC-INTERFACE.md section 7): OK or NG with Done for
    /// <see cref="PlcRuntime.TestPulseMs"/>, only in Teach and with the PLC connected; the PLC runtime
    /// refuses it while RunEnable is 1, while a part is handled and OK over a latched NG. A test signal
    /// is no part: nothing is counted or logged. Returns null when it was sent, else the text key of
    /// why not. The caller is an engineer (the page).</summary>
    public string? TestSignal(bool ok)
    {
        if (_station.Running) return "plc.refused.stop_first";
        if (Runtime is not { } rt || !rt.Connected) return "plc.refused.plc_off";
        return rt.TestResult(ok) switch
        {
            PlcTestRefusal.None => null,
            PlcTestRefusal.LineRunning => "plc.refused.stop_first",
            PlcTestRefusal.RunEnable => "plc.refused.run_enable",
            PlcTestRefusal.Busy => "plc.refused.busy",
            _ => "plc.refused.latched_ng",
        };
    }

    /// <summary>Checks a PLC network card address before it is saved (empty: every card).</summary>
    public static string? BindAddressError(string address)
    {
        try { new PlcOptions { BindAddress = address }.BindIp(); return null; }
        catch (FormatException e) { return e.Message; }
    }

    private void OnChanged() => Changed?.Invoke();
    private void OnPart(PartRecord p) => PartLeft?.Invoke(p);

    public void Dispose() => Runtime?.Dispose();
}
