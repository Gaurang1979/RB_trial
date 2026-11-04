using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VisionCore.Plc;
using Xunit;

/// <summary>A PLC simulated over real Modbus TCP drives the handshake of PLC-INTERFACE.md section 4.1.</summary>
public class PlcTests
{
    private sealed class FakeHost : IPlcHost
    {
        public bool CanInspect { get; set; } = true;
        public ushort ActiveJobNumber { get; set; } = 7;
        public bool Simulation => true;
        public bool NextOk { get; set; } = true;
        public int Inspected;
        public ushort Loaded;

        public PlcInspection Inspect(PlcTrigger t)
        {
            Inspected++;
            Thread.Sleep(30);
            return new PlcInspection(NextOk, NextOk ? 0b11u : 0b01u, NextOk ? (ushort)0 : (ushort)2,
                "8901396152002", "₹ 46.00, HWN881", new[] { 1.5f, 2.5f });
        }

        public int CameraCount => 3;
        public int CameraInspected;

        public PlcInspection InspectCamera(int camera, PlcTrigger t)
        {
            Interlocked.Increment(ref CameraInspected);
            Thread.Sleep(40 + 20 * camera);
            bool ok = !(camera == 1 && t.TriggerId == 2);   // camera 2 finds part 2 bad
            return new PlcInspection(ok, 0, ok ? (ushort)0 : (ushort)(camera + 1), "", "", Array.Empty<float>());
        }

        public ushort LoadJob(ushort n)
        {
            if (n == 99) return PlcCodes.JobNotFound;
            Loaded = n;
            ActiveJobNumber = n;
            return 0;
        }
    }

    /// <summary>Minimal Modbus TCP client: what the PLC's MB_CLIENT block does.</summary>
    private sealed class Plc : IDisposable
    {
        private readonly TcpClient _c = new();
        private ushort _tid;
        public Plc(int port) => _c.Connect(IPAddress.Loopback, port);

        private byte[] Call(byte[] pdu)
        {
            var f = new byte[7 + pdu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(f, ++_tid);
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), (ushort)(pdu.Length + 1));
            f[6] = 1;
            pdu.CopyTo(f, 7);
            var s = _c.GetStream();
            s.Write(f);
            var head = new byte[7];
            s.ReadExactly(head);
            var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4)) - 1];
            s.ReadExactly(body);
            return body;
        }

        public byte[] Read(int a, int n)
        {
            var r = Call(new byte[] { 3, (byte)(a >> 8), (byte)a, 0, (byte)n });
            Assert.Equal(3, r[0]);
            var block = new byte[2 * n];
            for (int i = 0; i < n; i++) { ushort v = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2 + 2 * i)); block[2 * i] = (byte)v; block[2 * i + 1] = (byte)(v >> 8); }
            return block;
        }

        public byte Write(byte[] block)   // whole input block, low byte first per register
        {
            int n = block.Length / 2;
            var pdu = new byte[6 + block.Length];
            pdu[0] = 16; pdu[1] = 0; pdu[2] = 0; pdu[3] = 0; pdu[4] = (byte)n; pdu[5] = (byte)(2 * n);
            for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + 2 * i), (ushort)(block[2 * i] | block[2 * i + 1] << 8));
            return Call(pdu)[0];
        }

        public byte WriteOutputArea() => Call(new byte[] { 6, 0, 100, 0, 1 })[0];

        public byte WriteAt(int a, byte[] block)
        {
            int n = block.Length / 2;
            var pdu = new byte[6 + block.Length];
            pdu[0] = 16; pdu[1] = (byte)(a >> 8); pdu[2] = (byte)a; pdu[3] = 0; pdu[4] = (byte)n; pdu[5] = (byte)(2 * n);
            for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + 2 * i), (ushort)(block[2 * i] | block[2 * i + 1] << 8));
            return Call(pdu)[0];
        }

        public void Dispose() => _c.Dispose();
    }

    private static PlcOutput Out(byte[] raw)
    {
        var b = new PlcBlock(PlcOutput.Size, false);
        raw.CopyTo(b.Bytes, 0);
        return new PlcOutput(b);
    }

    private static PlcOutput WaitFor(Plc plc, Func<PlcOutput, bool> cond, int ms = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var raw = new byte[PlcOutput.Size];
            plc.Read(100, 125).CopyTo(raw, 0);
            plc.Read(225, 3).CopyTo(raw, 250);
            var o = Out(raw);
            if (cond(o)) return o;
            Assert.True(sw.ElapsedMilliseconds < ms, "PLC output did not reach the expected state in time");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void HandshakeOverModbus()
    {
        var host = new FakeHost();
        using var rt = new PlcRuntime(new ModbusServer(0, IPAddress.Loopback), host, new PlcOptions { WatchdogMs = 1000 });
        int port = int.Parse(rt.LinkName.Split("port ")[1]);
        using var plc = new Plc(port);
        var inB = new PlcBlock(PlcInput.Size, false);
        ushort hb = 0;
        void Send() { inB.SetU16(2, ++hb); Assert.Equal(16, plc.Write(inB.Bytes)); }
        // the PLC keeps its heartbeat running while the test waits, as a real PLC does: on a busy machine a wait
        // longer than the watchdog would otherwise lose the link (error 100) in the middle of the handshake
        PlcOutput Until(Func<PlcOutput, bool> cond)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long beat = 0;
            while (true)
            {
                var now = WaitFor(plc, _ => true);
                if (cond(now)) return now;
                Assert.True(sw.ElapsedMilliseconds < 5000, "PLC output did not reach the expected state in time");
                if (sw.ElapsedMilliseconds - beat >= 100) { Send(); beat = sw.ElapsedMilliseconds; }
                Thread.Sleep(5);
            }
        }

        // not ready until the PLC heartbeat runs and RunEnable is set
        Assert.False(WaitFor(plc, _ => true).Ready);
        inB.SetBit(0, 3, true);                         // RunEnable
        for (int i = 0; i < 4; i++) { Send(); Thread.Sleep(20); }
        var o = Until(x => x.Ready);
        Assert.Equal(7, o.ActiveJob);
        Assert.True(o.JobActive);
        Assert.True(o.Simulation);
        Assert.Equal(PlcOutput.Layout, o.B.U16(32));

        // trigger -> Done + OK with results, then trigger off -> Done off
        inB.SetU16(8, 4242);                            // TriggerID
        inB.SetBit(0, 0, true);
        Send();
        o = Until(x => x.Done);
        Assert.True(o.Ok && !o.Ng && !o.Busy);
        Assert.Equal(4242, o.TriggerIdEcho);
        Assert.Equal(1u, o.InspectionCount);
        Assert.Equal(3u, o.ToolPass);
        Assert.Equal("8901396152002", o.B.Str(120));
        // the last heartbeat comes back (no heartbeat is sent while this waits)
        Assert.Equal(hb, WaitFor(plc, x => x.HeartbeatEcho == hb).HeartbeatEcho);
        inB.SetBit(0, 0, false);
        Send();
        Until(x => !x.Done && !x.Ok && x.Ready);

        // a NOT OK part reports the first failing tool
        host.NextOk = false;
        inB.SetBit(0, 0, true);
        Send();
        o = Until(x => x.Done);
        Assert.True(o.Ng && !o.Ok);
        Assert.Equal(2, o.NgReason);
        inB.SetBit(0, 0, false);
        Send();
        Until(x => !x.Done);

        // not ready (no job): trigger rejected with 111, cleared by ResetAlarm
        host.CanInspect = false;
        inB.SetBit(0, 0, true);
        Send();
        o = Until(x => x.TriggerRejected);
        Assert.Equal(PlcCodes.TriggerNotReady, o.ErrorCode);
        Assert.Equal(2, host.Inspected);
        inB.SetBit(0, 0, false);
        inB.SetBit(0, 2, true);                         // ResetAlarm
        Send();
        o = Until(x => !x.TriggerRejected && !x.Error);
        inB.SetBit(0, 2, false);
        host.CanInspect = true;

        // job change by number; an unknown number is error 200
        inB.SetU16(4, 12);
        inB.SetBit(0, 1, true);
        Send();
        o = Until(x => x.JobChangeDone && x.ActiveJob == 12);
        Assert.Equal(12, host.Loaded);
        inB.SetBit(0, 1, false);
        Send();
        Until(x => !x.JobChangeDone);
        inB.SetU16(4, 99);
        inB.SetBit(0, 1, true);
        Send();
        o = Until(x => x.JobChangeDone && x.Error);
        Assert.Equal(PlcCodes.JobNotFound, o.ErrorCode);

        // the output block is read-only for the PLC
        Assert.Equal(0x86, plc.WriteOutputArea());

        // heartbeat stops -> error 100, not ready
        o = WaitFor(plc, x => x.ErrorCode == PlcCodes.HeartbeatLost && !x.Ready, 5000);
    }
}

public class CameraChannelTests
{
    [Fact]
    public void CamerasAtDifferentPlacesTriggerIndependently()
    {
        var host = new PlcTestsAccess.Host();
        using var rt = new PlcRuntime(new ModbusServer(0, IPAddress.Loopback), host, new PlcOptions { WatchdogMs = 2000 });
        int port = int.Parse(rt.LinkName.Split("port ")[1]);
        using var plc = new PlcTestsAccess.Client(port);
        var inB = new PlcBlock(PlcInput.Size, false);
        inB.SetBit(0, 3, true);
        ushort hb = 0;
        void Beat() { inB.SetU16(2, ++hb); plc.Write(inB.Bytes); }
        for (int i = 0; i < 4; i++) { Beat(); Thread.Sleep(15); }
        var cam = new PlcBlock(PlcCameraChannels.InSize, false);
        PlcBlock Out()
        {
            var o = new PlcBlock(PlcCameraChannels.OutSize, false);
            plc.Read(228, 16).CopyTo(o.Bytes, 0);
            return o;
        }
        PlcBlock Wait(Func<PlcBlock, bool> f)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long beat = 0;
            // the heartbeat keeps running while the test waits, as a real PLC's does, so a busy machine never trips the watchdog
            while (true)
            {
                var o = Out();
                if (f(o)) return o;
                Assert.True(sw.ElapsedMilliseconds < 5000, "camera channel timeout");
                if (sw.ElapsedMilliseconds - beat >= 100) { Beat(); beat = sw.ElapsedMilliseconds; }
                Thread.Sleep(3);
            }
        }
        Wait(o => PlcCameraChannels.Ready(o, 0) && PlcCameraChannels.Ready(o, 1));
        void Fire(int c, ushort id, bool on) { cam.SetBit(0, c, on); if (on) cam.SetU16(2 + 2 * c, id); plc.WriteAt(32, cam.Bytes); }

        Fire(0, 1, true);                                           // part 1 at camera 1
        var o = Wait(x => PlcCameraChannels.Done(x, 0));
        Assert.True(PlcCameraChannels.Ok(o, 0));
        Assert.Equal(1, PlcCameraChannels.IdEcho(o, 0));
        Fire(0, 0, false);
        Fire(1, 1, true);                                           // part 1 at camera 2 ...
        Fire(0, 2, true);                                           // ... while part 2 is at camera 1
        o = Wait(x => PlcCameraChannels.Done(x, 0) && PlcCameraChannels.Done(x, 1));
        Assert.Equal(2, PlcCameraChannels.IdEcho(o, 0));
        Assert.Equal(1, PlcCameraChannels.IdEcho(o, 1));
        Assert.True(PlcCameraChannels.Ok(o, 0) && PlcCameraChannels.Ok(o, 1));
        Fire(0, 0, false); Fire(1, 0, false);
        Wait(x => !PlcCameraChannels.Done(x, 1));
        Fire(1, 2, true);                                           // part 2 at camera 2: NOT OK
        o = Wait(x => PlcCameraChannels.Done(x, 1));
        Assert.False(PlcCameraChannels.Ok(o, 1));
        Assert.True(o.Bit(8, 4));                                   // NG bit of camera 2
        Assert.Equal(2, o.U16(8 + 4));                              // first failing tool reported by camera 2
        Assert.Equal(4, host.CameraInspected);
    }
}

/// <summary>Opens the PLC test helpers to other test classes.</summary>
public static class PlcTestsAccess
{
    public sealed class Host : IPlcHost
    {
        public bool CanInspect => true;
        public ushort ActiveJobNumber => 1;
        public bool Simulation => true;
        public int CameraCount => 3;
        public int CameraInspected;
        public PlcInspection Inspect(PlcTrigger t) => new(true, 0, 0, "", "", Array.Empty<float>());
        public ushort LoadJob(ushort n) => 0;
        public PlcInspection InspectCamera(int camera, PlcTrigger t)
        {
            Interlocked.Increment(ref CameraInspected);
            Thread.Sleep(40 + 20 * camera);
            bool ok = !(camera == 1 && t.TriggerId == 2);
            return new PlcInspection(ok, 0, ok ? (ushort)0 : (ushort)(camera + 1), "", "", Array.Empty<float>());
        }
    }

    public sealed class Client : IDisposable
    {
        private readonly TcpClient _c = new();
        private ushort _tid;
        public Client(int port) => _c.Connect(IPAddress.Loopback, port);
        private byte[] Call(byte[] pdu)
        {
            var f = new byte[7 + pdu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(f, ++_tid);
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), (ushort)(pdu.Length + 1));
            f[6] = 1;
            pdu.CopyTo(f, 7);
            var s = _c.GetStream();
            s.Write(f);
            var head = new byte[7];
            s.ReadExactly(head);
            var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4)) - 1];
            s.ReadExactly(body);
            return body;
        }
        public byte[] Read(int a, int n)
        {
            var r = Call(new byte[] { 3, (byte)(a >> 8), (byte)a, 0, (byte)n });
            var block = new byte[2 * n];
            for (int i = 0; i < n; i++) { ushort v = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2 + 2 * i)); block[2 * i] = (byte)v; block[2 * i + 1] = (byte)(v >> 8); }
            return block;
        }
        public byte Write(byte[] block) => WriteAt(0, block);
        public byte WriteAt(int a, byte[] block)
        {
            int n = block.Length / 2;
            var pdu = new byte[6 + block.Length];
            pdu[0] = 16; pdu[1] = (byte)(a >> 8); pdu[2] = (byte)a; pdu[3] = 0; pdu[4] = (byte)n; pdu[5] = (byte)(2 * n);
            for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + 2 * i), (ushort)(block[2 * i] | block[2 * i + 1] << 8));
            return Call(pdu)[0];
        }
        public void Dispose() => _c.Dispose();
    }
}

public class LineHealthTests
{
    private static LineDiagnostics Diag(Action<PlcBlock> fill)
    {
        var b = new PlcBlock(LineDiagnostics.Size, false);
        b.SetU16(0, LineDiagnostics.Layout);
        fill(b);
        return new LineDiagnostics(b);
    }

    private static InstrumentHealth Of(IReadOnlyList<InstrumentHealth> l, Instrument i) => l.First(x => x.Id == i);

    [Fact]
    public void RejectsNotSeenInTheChuteAreMissesSometimes()
    {
        var d = Diag(b => { b.SetU32(36, 200); b.SetU32(40, 197); b.SetU32(44, 3); });
        var h = LineHealth.Evaluate(LineProfiles.Profile("delta-as228t"), d);
        Assert.Equal(Health.Sometimes, Of(h, Instrument.Rejector).Health);
        Assert.Contains("3 rejects not confirmed in 200", Of(h, Instrument.Rejector).Detail);
    }

    [Fact]
    public void BypassedInstrumentsAreNotJudged()
    {
        var d = Diag(b => { b.SetU32(36, 25); b.SetU32(44, 25); });   // would be a fault if fitted
        var set = new LineSettings { Bypassed = LineSettings.Bypass.RejectVerify | LineSettings.Bypass.JamSensor };
        var h = LineHealth.Evaluate(LineProfiles.Profile("delta-dvp12se-16sp"), d, set);
        Assert.Equal(Health.Bypassed, Of(h, Instrument.RejectVerifySensor).Health);
        Assert.Equal(Health.Bypassed, Of(h, Instrument.JamSensor).Health);
    }

    [Fact]
    public void CameraSensorsFollowTheCameraCount()
    {
        // camera 1 counted 1000 parts, camera 2 1000, camera 3 only 950
        var d = Diag(b => { b.SetU32(16, 1000); b.SetU32(80, 1000); b.SetU32(84, 950); });
        var p = LineProfiles.Profile("delta-dvp12se-16sp");
        var one = LineHealth.Evaluate(p, d, new LineSettings { CameraCount = 1 });
        Assert.Equal(Health.NotFitted, Of(one, Instrument.Camera2Sensor).Health);
        var three = LineHealth.Evaluate(p, d, new LineSettings { CameraCount = 3 });
        Assert.Equal(Health.Ok, Of(three, Instrument.Camera2Sensor).Health);
        Assert.NotEqual(Health.Ok, Of(three, Instrument.Camera3Sensor).Health);
    }

    [Fact]
    public void SettingsCarryBypassAndCameras()
    {
        var b = new PlcBlock(LineSettings.Size, false);
        new LineSettings { Bypassed = LineSettings.Bypass.RejectVerify | LineSettings.Bypass.EStop, CameraCount = 3 }.Write(b, 7);
        Assert.Equal(64, LineSettings.Size);
        Assert.Equal(0x21, b.U16(38));
        Assert.Equal(3, b.U16(40));
        Assert.Equal(2, b.Bytes[42]);
        Assert.Equal(3, b.Bytes[43]);
    }

    [Fact]
    public void FrontAndBackCamerasShareOneSensor()
    {
        // cameras 1 and 2 read front and back at the same place; camera 3 further down the line
        var set = new LineSettings { CameraCount = 3, Camera2Sensor = 1, Camera3Sensor = 3 };
        var b = new PlcBlock(LineSettings.Size, false);
        set.Write(b, 1);
        Assert.Equal(1, b.Bytes[42]);
        Assert.False(set.SensorUsed(2));
        Assert.True(set.SensorUsed(3));
        var d = Diag(x => { x.SetU32(16, 1000); x.SetU32(84, 1000); });
        var h = LineHealth.Evaluate(LineProfiles.Profile("delta-dvp12se-16sp"), d, set);
        Assert.Equal(Health.NotFitted, Of(h, Instrument.Camera2Sensor).Health);
        Assert.Equal(Health.Ok, Of(h, Instrument.Camera3Sensor).Health);
    }

    [Fact]
    public void VerifySensorThatNeverSeesARejectIsAFault()
    {
        var d = Diag(b => { b.SetU32(36, 25); b.SetU32(44, 25); });
        Assert.Equal(Health.Fault, Of(LineHealth.Evaluate(LineProfiles.Profile("delta-as228t"), d), Instrument.RejectVerifySensor).Health);
    }

    [Fact]
    public void PartsInTransitAreNotMisses()
    {
        var d = Diag(b => { b.SetU32(16, 1000); b.SetU32(48, 996); });
        Assert.Equal(Health.Ok, Of(LineHealth.Evaluate(LineProfiles.Profile("delta-as228t"), d), Instrument.TriggerSensor).Health);
        var d2 = Diag(b => { b.SetU32(16, 1000); b.SetU32(48, 980); });
        Assert.Equal(Health.Sometimes, Of(LineHealth.Evaluate(LineProfiles.Profile("delta-as228t"), d2), Instrument.TriggerSensor).Health);
    }

    [Fact]
    public void JamStateAndSignalsAndSmallPlc()
    {
        var d = Diag(b => { b.SetU16(8, (ushort)LineDiagnostics.State.Jam); b.SetU16(4, 1 << 2); b.SetU16(6, 1 << 0); });
        var h = LineHealth.Evaluate(LineProfiles.Profile("delta-as228t"), d);
        Assert.Equal(Health.Fault, Of(h, Instrument.JamSensor).Health);
        Assert.True(Of(h, Instrument.TriggerSensor).Signal);     // X0.2 on
        Assert.True(Of(h, Instrument.Rejector).Signal);          // Y0.0 on
        var small = LineHealth.Evaluate(LineProfiles.Profile("delta-as218tx"), d);
        Assert.Equal(Health.NotFitted, Of(small, Instrument.BinFullSensor).Health);
        Assert.Equal(Health.NoData, Of(LineHealth.Evaluate(LineProfiles.Profile("delta-as228t"), null), Instrument.Rejector).Health);
    }

    [Fact]
    public void EveryProfileFitsItsTerminals()
    {
        foreach (var p in LineProfiles.Profiles)
        {
            var terms = p.Terminals.Values.ToList();
            Assert.Equal(terms.Count, terms.Distinct().Count());
            // diagnostics bits: inputs and outputs each fit one 16-bit word
            Assert.All(terms.Where(t => t[0] is 'X' or 'Y'), t => Assert.InRange(LineProfiles.DiagBit(t), 0, 15));
            Assert.True(terms.Count(t => t[0] == 'X') <= p.Inputs, p.Model);
            Assert.True(terms.Count(t => t[0] == 'Y') <= p.Outputs, p.Model);
        }
        Assert.Equal(11, LineProfiles.DiagBit("X23"));
        Assert.Equal(5, LineProfiles.DiagBit("X0.5"));
    }

    [Fact]
    public void SettingsBlockRoundTrip()
    {
        var s = new LineSettings { ConveyorHz = 37.5f, LightDelayMs = 20, LightOnMs = 80, RejectDistance = 123456, RejectOffset = -40, LearnAuto = true };
        var b = new PlcBlock(LineSettings.Size, false);
        s.Write(b, version: 9);
        Assert.Equal(1, b.U16(0));
        Assert.Equal(3750, b.U16(2));
        Assert.Equal(123456, b.I32(12));
        Assert.Equal(unchecked((ushort)-40), b.U16(16));
        Assert.Equal(9, b.U16(36));
    }
}

public class DriveTests
{
    [Fact]
    public void EveryProfileHasDistinctTerminals()
    {
        foreach (var p in LineProfiles.Profiles)
            Assert.Equal(p.Terminals.Count, p.Terminals.Values.Distinct().Count());
    }

    [Fact]
    public void DriveSettingsAreSent()
    {
        var s = new LineSettings { Drive = LineSettings.DriveKind.StarDelta, StarDeltaMs = 6000, ChangeoverMs = 120, DriveFaultNc = true,
                                   Bypassed = LineSettings.Bypass.AutoInput };
        var b = new PlcBlock(LineSettings.Size, false);
        s.Write(b, 1);
        Assert.Equal(64, LineSettings.Size);
        Assert.Equal(3, b.U16(56));
        Assert.Equal(6000, b.U16(58));
        Assert.Equal(120, b.U16(60));
        Assert.Equal(4 | 1 | 2, b.U16(54));
        Assert.Equal(0x80, b.U16(38));
    }

    [Fact]
    public void ContactorsShowOnlyForTheirDrive()
    {
        var p = LineProfiles.Profile("delta-dvp12se-16sp");
        var vfd = LineHealth.Evaluate(p, null, new LineSettings { Drive = LineSettings.DriveKind.VfdRs485 });
        Assert.Equal(Health.NotFitted, vfd.First(h => h.Id == Instrument.MotorStar).Health);
        Assert.NotEqual(Health.NotFitted, vfd.First(h => h.Id == Instrument.Vfd).Health);
        var sd = LineHealth.Evaluate(p, null, new LineSettings { Drive = LineSettings.DriveKind.StarDelta });
        Assert.NotEqual(Health.NotFitted, sd.First(h => h.Id == Instrument.MotorStar).Health);
        Assert.Equal(Health.NotFitted, sd.First(h => h.Id == Instrument.Vfd).Health);
        var bypass = LineHealth.Evaluate(p, null, new LineSettings { Bypassed = LineSettings.Bypass.StartStop });
        Assert.Equal(Health.Bypassed, bypass.First(h => h.Id == Instrument.StopButton).Health);
    }
}
