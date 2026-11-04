using System.Collections.Concurrent;
using System.Net;
using VisionCore.Plc;
using Xunit;

/// <summary>PLC-INTERFACE.md section 5.6: the runtime never sets OK for a part whose trigger came
/// before a link or heartbeat loss. A part held at the gate is OK when inspected; the PLC must still get NG.</summary>
public class PlcLinkLossTests
{
    private sealed class GatedHost : IPlcHost
    {
        public readonly ManualResetEventSlim Gate = new(false);
        public readonly SemaphoreSlim Entered = new(0);
        public readonly BlockingCollection<PlcDelivery> Deliveries = new();
        public volatile bool Running = true;
        public bool CanInspect => Running;
        public ushort ActiveJobNumber => 1;
        public bool Simulation => true;
        public int CameraCount => 1;
        public ushort LoadJob(ushort n) => 0;

        private PlcInspection Held()
        {
            Entered.Release();
            Assert.True(Gate.Wait(5000), "the test never released the inspection");
            return new PlcInspection(true, 1, 0, "", "", Array.Empty<float>());
        }

        public PlcInspection Inspect(PlcTrigger t) => Held();
        public PlcInspection InspectCamera(int camera, PlcTrigger t) => Held();
        public void Delivered(PlcDelivery d) => Deliveries.Add(d);

        public PlcDelivery Next()
        {
            Assert.True(Deliveries.TryTake(out var d, 3000), "no part was handed to the station");
            return d;
        }
    }

    private sealed class Line : IDisposable
    {
        public readonly GatedHost Host = new();
        public readonly PlcRuntime Rt;
        public readonly ModbusServer Server;
        public readonly int Port;
        public readonly PlcBlock In = new(PlcInput.Size, false);
        public PlcTestsAccess.Client Plc;
        private ushort _hb;

        public Line(int watchdogMs)
        {
            Server = new ModbusServer(0, IPAddress.Loopback);
            Rt = new PlcRuntime(Server, Host, new PlcOptions { WatchdogMs = watchdogMs });
            Port = int.Parse(Rt.LinkName.Split("port ")[1]);
            Plc = new PlcTestsAccess.Client(Port);
            In.SetBit(0, 3, true);                       // RunEnable
            Beat(5);
            Wait(o => o.Ready);
        }

        public void Send() { In.SetU16(2, ++_hb); Plc.Write(In.Bytes); }
        public void Beat(int n) { for (int i = 0; i < n; i++) { Send(); Thread.Sleep(20); } }

        // the PLC's connection ends and a new one opens; returns once the server has seen the end
        public void Reconnect()
        {
            int before = Server.Disconnects;
            Plc.Dispose();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Server.Disconnects == before)
            {
                Assert.True(sw.ElapsedMilliseconds < 3000, "the server did not see the connection end");
                Thread.Sleep(2);
            }
            Plc = new PlcTestsAccess.Client(Port);
        }

        public PlcBlock CameraOut()
        {
            var o = new PlcBlock(PlcCameraChannels.OutSize, false);
            Plc.Read(228, 16).CopyTo(o.Bytes, 0);
            return o;
        }

        public PlcBlock WaitCamera(Func<PlcBlock, bool> cond)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var o = CameraOut();
                if (cond(o)) return o;
                Assert.True(sw.ElapsedMilliseconds < 3000, "camera channel timeout");
                Send();
                Thread.Sleep(10);
            }
        }

        public void FireCamera(ushort id)
        {
            var cam = new PlcBlock(PlcCameraChannels.InSize, false);
            cam.SetBit(0, 0, true);
            cam.SetU16(2, id);
            Plc.WriteAt(32, cam.Bytes);
            Assert.True(Host.Entered.Wait(3000), "the camera trigger did not start an inspection");
        }

        public PlcOutput Read()
        {
            var b = new PlcBlock(PlcOutput.Size, false);
            Plc.Read(100, 125).CopyTo(b.Bytes, 0);
            Plc.Read(225, 3).CopyTo(b.Bytes, 250);
            return new PlcOutput(b);
        }

        public PlcOutput Wait(Func<PlcOutput, bool> cond, int ms = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var o = Read();
                if (cond(o)) return o;
                Assert.True(sw.ElapsedMilliseconds < ms, "PLC output did not reach the expected state in time");
                Send();
                Thread.Sleep(10);
            }
        }

        public void Trigger(ushort id)
        {
            In.SetU16(8, id);
            In.SetBit(0, 0, true);
            Send();
            Assert.True(Host.Entered.Wait(3000), "the trigger did not start an inspection");
        }

        public void Drop() { In.SetBit(0, 0, false); Send(); }

        // reads only: no heartbeat is sent while waiting (a lost heartbeat stays lost)
        public T Poll<T>(Func<T> read, Func<T, bool> cond)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var o = read();
                if (cond(o)) return o;
                Assert.True(sw.ElapsedMilliseconds < 3000, "PLC output did not reach the expected state in time");
                Thread.Sleep(10);
            }
        }

        public void DropCamera() => Plc.WriteAt(32, new byte[PlcCameraChannels.InSize]);

        public void Dispose()
        {
            Host.Gate.Set();
            Plc.Dispose();
            Rt.Dispose();
        }
    }

    [Fact]
    public void WithoutALossTheHeldPartIsOk()
    {
        using var line = new Line(1000);
        line.Trigger(8000);
        line.Host.Gate.Set();
        var o = line.Wait(x => x.Done);
        Assert.True(o.Ok && !o.Ng);
        Assert.Equal(8000, o.TriggerIdEcho);
    }

    [Fact]
    public void AConnectionDroppedDuringThePartGivesNgNotOk()
    {
        using var line = new Line(1000);
        line.Trigger(8000);
        line.Reconnect();                              // gone for a moment, back well inside the watchdog
        line.Beat(3);
        line.Host.Gate.Set();
        var o = line.Wait(x => x.Done);
        Assert.False(o.Ok);
        Assert.True(o.Ng);
        Assert.Equal(PlcCodes.HeartbeatLost, o.NgReason);
        Assert.Equal(PlcCodes.HeartbeatLost, o.ErrorCode);
        Assert.Equal(8000, o.TriggerIdEcho);
    }

    [Fact]
    public void AHeartbeatLossDuringThePartGivesNgEvenAfterTheHeartbeatIsBack()
    {
        using var line = new Line(300);
        line.Trigger(8001);
        Thread.Sleep(600);                             // the PLC's heartbeat stops: error 100
        Assert.Equal(PlcCodes.HeartbeatLost, line.Read().ErrorCode);
        line.Beat(5);                                  // back, and seen for 3 periods
        line.Host.Gate.Set();
        var o = line.Wait(x => x.Done);
        Assert.False(o.Ok);
        Assert.True(o.Ng);
        Assert.Equal(PlcCodes.HeartbeatLost, o.NgReason);
    }

    [Fact]
    public void ACameraChannelPartAcrossADroppedConnectionIsNg()
    {
        using var line = new Line(1000);
        line.FireCamera(7);
        line.Reconnect();
        line.Beat(3);
        line.Host.Gate.Set();
        var o = line.WaitCamera(x => PlcCameraChannels.Done(x, 0));
        Assert.False(PlcCameraChannels.Ok(o, 0));
        Assert.True(o.Bit(0, 4));                          // camera 1's NG bit
        Assert.Equal(7, PlcCameraChannels.IdEcho(o, 0));
        Assert.Equal(PlcCodes.HeartbeatLost, o.U16(4));    // camera 1's NG reason
    }

    [Fact]
    public void AnOkAlreadyWrittenIsTakenBackWhenTheHeartbeatIsLost()
    {
        // a pulled cable sends no close: the loss shows only when the watchdog runs out, after the result
        using var line = new Line(300);
        line.Trigger(8002);
        line.Host.Gate.Set();
        Assert.True(line.Wait(x => x.Done).Ok);
        Thread.Sleep(700);                             // no heartbeat from the PLC
        var o = line.Read();
        Assert.True(o.Done);
        Assert.False(o.Ok);
        Assert.True(o.Ng);
        Assert.Equal(PlcCodes.HeartbeatLost, o.NgReason);
        Assert.Equal(PlcCodes.HeartbeatLost, o.ErrorCode);
    }

    [Fact]
    public void ACameraChannelOkAlreadyWrittenIsTakenBackWhenTheHeartbeatIsLost()
    {
        using var line = new Line(300);
        line.FireCamera(9);
        line.Host.Gate.Set();
        Assert.True(PlcCameraChannels.Ok(line.WaitCamera(x => PlcCameraChannels.Done(x, 0)), 0));
        Thread.Sleep(700);
        var o = line.CameraOut();
        Assert.True(PlcCameraChannels.Done(o, 0));
        Assert.False(PlcCameraChannels.Ok(o, 0));
        Assert.True(o.Bit(0, 4));
        Assert.Equal(PlcCodes.HeartbeatLost, o.U16(4));
    }

    [Fact]
    public void AReadingClientComingAndGoingLeavesThePartOk()
    {
        // an HMI or a Modbus poll tool reads the blocks and disconnects: not the PLC's link
        using var line = new Line(1000);
        line.Trigger(8003);
        using (var hmi = new PlcTestsAccess.Client(line.Port)) hmi.Read(100, 10);
        Thread.Sleep(50);
        Assert.Equal(0, line.Server.Disconnects);
        line.Host.Gate.Set();
        var o = line.Wait(x => x.Done);
        Assert.True(o.Ok && !o.Ng);
    }

    // ---- the station hears the verdict the PLC got (traceability, requirements section 12)

    [Fact]
    public void APartThePlcTookIsHandedOverOkWhenItDropsItsTrigger()
    {
        using var line = new Line(1000);
        line.Trigger(8010);
        line.Host.Gate.Set();
        Assert.True(line.Wait(x => x.Done).Ok);
        Assert.Equal(0, line.Host.Deliveries.Count);      // not before the PLC took it
        line.Drop();
        var d = line.Host.Next();
        Assert.Equal(-1, d.Camera);
        Assert.Equal(8010, d.Trigger.TriggerId);
        Assert.True(d.Trigger.Held);
        Assert.True(d.Result.Ok);
        Assert.False(d.LinkLossNg);
    }

    [Fact]
    public void APartAcrossADroppedConnectionIsHandedOverAsNg100()
    {
        using var line = new Line(1000);
        line.Trigger(8011);
        line.Reconnect();
        line.Beat(3);
        line.Host.Gate.Set();
        Assert.True(line.Wait(x => x.Done).Ng);
        line.Drop();
        var d = line.Host.Next();
        Assert.True(d.LinkLossNg);
        Assert.False(d.Result.Ok);
        Assert.Equal(PlcCodes.HeartbeatLost, d.Result.NgReason);
    }

    [Fact]
    public void AnOkTakenBackAtAHeartbeatLossIsHandedOverAsNg100()
    {
        using var line = new Line(300);
        line.Trigger(8012);
        line.Host.Gate.Set();
        Assert.True(line.Wait(x => x.Done).Ok);
        var d = line.Host.Next();                          // the watchdog runs out while the PLC holds its trigger
        Assert.Equal(8012, d.Trigger.TriggerId);
        Assert.True(d.LinkLossNg);
        Assert.Equal(PlcCodes.HeartbeatLost, d.Result.NgReason);
        Assert.True(line.Read().Ng);
    }

    [Fact]
    public void ACameraChannelOkTakenBackIsHandedOverAsNg100()
    {
        using var line = new Line(300);
        line.FireCamera(21);
        line.Host.Gate.Set();
        Assert.True(PlcCameraChannels.Ok(line.WaitCamera(x => PlcCameraChannels.Done(x, 0)), 0));
        var d = line.Host.Next();
        Assert.Equal(0, d.Camera);
        Assert.Equal(21, d.Trigger.TriggerId);
        Assert.True(d.LinkLossNg);
    }

    [Fact]
    public void ACameraChannelPartIsHandedOverWhenThePlcDropsItsTrigger()
    {
        using var line = new Line(1000);
        line.FireCamera(22);
        line.Host.Gate.Set();
        line.WaitCamera(x => PlcCameraChannels.Done(x, 0));
        line.DropCamera();
        line.Send();
        var d = line.Host.Next();
        Assert.Equal(0, d.Camera);
        Assert.True(d.Result.Ok);
        Assert.False(d.LinkLossNg);
    }

    [Fact]
    public void APartHeldWhenTheRuntimeClosesIsHandedOverAsNg100()
    {
        var line = new Line(1000);
        line.Trigger(8013);
        line.Host.Gate.Set();
        Assert.True(line.Wait(x => x.Done).Ok);
        line.Dispose();                                    // the link closes: the PLC makes the part NG
        var d = line.Host.Next();
        Assert.True(d.LinkLossNg);
    }

    [Fact]
    public void APartWhoseTriggerDroppedBeforeDoneIsHandedOverAsNg()
    {
        using var line = new Line(1000);
        line.Trigger(8014);
        line.Drop();                                       // the PLC's result timeout: it made the part NG
        line.Beat(3);
        line.Host.Gate.Set();                              // the inspection then says OK
        var d = line.Host.Next();
        Assert.Equal(8014, d.Trigger.TriggerId);
        Assert.True(d.NotTaken);
        Assert.False(d.Result.Ok);
    }

    [Fact]
    public void ACameraChannelPartWhoseTriggerDroppedBeforeDoneIsHandedOverAsNg()
    {
        using var line = new Line(1000);
        line.FireCamera(23);
        line.DropCamera();
        line.Beat(3);
        line.Host.Gate.Set();
        var d = line.Host.Next();
        Assert.Equal(0, d.Camera);
        Assert.True(d.NotTaken);
        Assert.False(d.Result.Ok);
    }

    [Fact]
    public void APartStillInspectedWhenTheRuntimeClosesIsHandedOverAsNg100()
    {
        using var line = new Line(1000);
        line.Trigger(8015);
        line.Rt.Dispose();                                 // waits for the inspection, then closes without it
        line.Host.Gate.Set();                              // it ends OK after the link closed
        var d = line.Host.Next();
        Assert.Equal(8015, d.Trigger.TriggerId);
        Assert.True(d.LinkLossNg);
        Assert.False(d.Result.Ok);
    }

    [Fact]
    public void ACameraChannelPartStillInspectedWhenTheRuntimeClosesIsHandedOverAsNg100()
    {
        using var line = new Line(1000);
        line.FireCamera(24);
        line.Rt.Dispose();
        line.Host.Gate.Set();
        var d = line.Host.Next();
        Assert.Equal(0, d.Camera);
        Assert.True(d.LinkLossNg);
    }

    // ---- error codes, JobActive and the test signal

    [Fact]
    public void ATriggerDuringAHeartbeatLossKeepsError100()
    {
        using var line = new Line(300);
        Thread.Sleep(600);                                 // no heartbeat: error 100
        Assert.Equal(PlcCodes.HeartbeatLost, line.Read().ErrorCode);
        line.In.SetU16(8, 8020);
        line.In.SetBit(0, 0, true);
        line.Send();                                       // one heartbeat change: still lost
        var o = line.Poll(line.Read, x => x.TriggerRejected);
        Assert.Equal(PlcCodes.HeartbeatLost, o.ErrorCode);
        Assert.False(o.Busy);
        // a camera channel trigger as well
        var cam = new PlcBlock(PlcCameraChannels.InSize, false);
        cam.SetBit(0, 0, true);
        cam.SetU16(2, 31);
        line.Plc.WriteAt(32, cam.Bytes);
        line.Poll(line.CameraOut, x => x.Bit(0, 5));       // camera 1 refused
        Assert.Equal(PlcCodes.HeartbeatLost, line.Read().ErrorCode);
        Assert.Equal(0, line.Host.Entered.CurrentCount);
    }

    [Fact]
    public void ATriggerWhileStoppedStillGivesError111()
    {
        using var line = new Line(1000);
        line.Host.Running = false;
        line.In.SetU16(8, 8021);
        line.In.SetBit(0, 0, true);
        line.Send();
        var o = line.Wait(x => x.TriggerRejected);
        Assert.Equal(PlcCodes.TriggerNotReady, o.ErrorCode);
    }

    [Fact]
    public void JobActiveIsOneOnlyWhileTheLineRuns()
    {
        using var line = new Line(1000);
        var o = line.Wait(x => x.JobActive);
        Assert.Equal(1, o.ActiveJob);
        line.Host.Running = false;                         // stopped: the job stays loaded, not active
        o = line.Wait(x => !x.JobActive);
        Assert.Equal(1, o.ActiveJob);
        Assert.False(o.Ready);
        line.Host.Running = true;
        Assert.True(line.Wait(x => x.JobActive).Ready);
    }

    // the line stopped (Teach) and the PLC's RunEnable off: what the test signal needs (section 7)
    private static void Teach(Line line)
    {
        line.Host.Running = false;
        line.In.SetBit(0, 3, false);
        line.Send();
    }

    [Fact]
    public void TheTestSignalNeverTurnsALatchedNgIntoOk()
    {
        using var line = new Line(1000);
        line.Trigger(8030);
        line.Reconnect();
        line.Beat(3);
        line.Host.Gate.Set();
        Assert.True(line.Wait(x => x.Done).Ng);
        Assert.Equal(PlcTestRefusal.LineRunning, line.Rt.TestResult(true));
        Teach(line);                                       // the PLC still holds its trigger: the part is not taken
        Assert.Equal(PlcTestRefusal.Busy, line.Rt.TestResult(true));
        var o = line.Read();
        Assert.True(o.Done && o.Ng);
        Assert.False(o.Ok);
        Assert.Equal(PlcCodes.HeartbeatLost, o.NgReason);
    }

    [Fact]
    public void TheTestSignalIsAPulseOfTwoSecondsInTeachOnly()
    {
        using var line = new Line(1000);
        Assert.Equal(PlcTestRefusal.LineRunning, line.Rt.TestResult(true));   // the line runs
        line.Host.Running = false;
        Assert.Equal(PlcTestRefusal.RunEnable, line.Rt.TestResult(true));     // the PLC sets RunEnable
        Assert.False(line.Read().Done);
        Teach(line);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(PlcTestRefusal.None, line.Rt.TestResult(true));
        Assert.True(line.Wait(x => x.Done && x.Ok).Ok);
        line.Wait(x => !x.Done, PlcRuntime.TestPulseMs + 1500);                // it does not latch
        Assert.InRange(sw.ElapsedMilliseconds, PlcRuntime.TestPulseMs - 100, PlcRuntime.TestPulseMs + 1500);
        var o = line.Read();
        Assert.False(o.Ok || o.Ng);
    }

    [Fact]
    public void TheTestSignalEndsAtOnceWhenThePlcSetsRunEnable()
    {
        using var line = new Line(1000);
        Teach(line);
        Assert.Equal(PlcTestRefusal.None, line.Rt.TestResult(true));
        line.Wait(x => x.Done && x.Ok);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        line.In.SetBit(0, 3, true);                        // RunEnable = 1: a test OK must never stand
        line.Send();
        var o = line.Wait(x => !x.Done);
        Assert.True(sw.ElapsedMilliseconds < 1000, "the test OK stood while RunEnable was 1");
        Assert.False(o.Ok);
    }

    [Fact]
    public void TheTestSignalWaitsForAPartBeingInspected()
    {
        using var line = new Line(1000);
        line.Trigger(8031);                                // held at the gate: busy
        Teach(line);
        Assert.Equal(PlcTestRefusal.Busy, line.Rt.TestResult(true));
        line.Host.Gate.Set();
        Assert.True(line.Wait(x => x.Done && !x.Busy).Done);
        Assert.Equal(PlcTestRefusal.Busy, line.Rt.TestResult(false));          // its result is not taken yet
        line.Drop();
        line.Wait(x => !x.Done);
        Assert.Equal(PlcTestRefusal.None, line.Rt.TestResult(false));
    }

    [Fact]
    public void TheOkTestNeverReplacesATestNg()
    {
        using var line = new Line(1000);
        Teach(line);
        Assert.Equal(PlcTestRefusal.None, line.Rt.TestResult(false));
        Assert.True(line.Wait(x => x.Done && x.Ng).Ng);
        Assert.Equal(PlcTestRefusal.LatchedNg, line.Rt.TestResult(true));
        Assert.True(line.Read().Ng);
        Assert.Equal(PlcTestRefusal.None, line.Rt.TestResult(false));          // NG over NG: allowed
    }

    // ---- the Modbus server's network card

    [Fact]
    public void TheServerListensOnTheChosenNetworkCard()
    {
        Assert.Equal(IPAddress.Any, new PlcOptions().BindIp());
        Assert.Equal(IPAddress.Loopback, new PlcOptions { BindAddress = " 127.0.0.1 " }.BindIp());
        Assert.Throws<FormatException>(() => new PlcOptions { BindAddress = "plc-lan" }.BindIp());
        using var server = new ModbusServer(0, new PlcOptions { BindAddress = "127.0.0.1" }.BindIp());
        using (var c = new PlcTestsAccess.Client(server.Port)) Assert.Equal(20, c.Read(0, 10).Length);
        // not on another address of this PC
        var other = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (other is null) return;
        using var tcp = new System.Net.Sockets.TcpClient();
        Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => tcp.Connect(other, server.Port));
    }
}
