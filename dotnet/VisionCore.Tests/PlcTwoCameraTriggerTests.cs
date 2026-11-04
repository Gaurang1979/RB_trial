using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using VisionCore.Plc;
using Xunit;

/// <summary>The two-camera trigger of plc/delta-dvp/VisionLineDVP.st (Req 17.10 MRG-05) against the real runtime
/// over Modbus TCP: a C# copy of the program's CamStation block runs the PLC side of each camera channel scan by
/// scan (PLC-INTERFACE.md 3.5). A part passes only when both cameras answered Done with its part number and OK;
/// no answer in time, a refused trigger or a camera still busy makes it NG, and no part the runtime did not
/// inspect OK on both cameras ever passes.</summary>
public class PlcTwoCameraTriggerTests
{
    /// <summary>CamStation of VisionLineDVP.st, line for line; Step is one PLC scan.</summary>
    sealed class CamStation
    {
        int _st;                    // 0 idle, 1 delay (0 here), 2 waiting for done, 3 waiting for done to drop
        readonly Stopwatch _t = new();
        public bool Trigger, Finished, Ok, TimedOut, Refused, NotReady;
        public ushort TriggerId;

        public void Step(bool start, ushort id, int status, ushort echo, int resultMs = 1000)
        {
            Finished = TimedOut = Refused = NotReady = false;
            if (start)
            {
                if (_st == 0 && (status & 0x01) != 0) { _st = 1; TriggerId = id; }
                else NotReady = true;
            }
            if (_st == 1) { Trigger = true; _st = 2; _t.Restart(); }
            else if (_st == 2)
            {
                if ((status & 0x04) != 0 && echo == TriggerId) { Ok = (status & 0x18) == 0x08; End(); }
                else if ((status & 0x20) != 0) { Ok = false; Refused = true; End(); }
                else if (_t.ElapsedMilliseconds >= resultMs) { Ok = false; TimedOut = true; End(); }
            }
            else if (_st == 3 && (status & 0x24) == 0) _st = 0;
        }

        void End() { Finished = true; Trigger = false; _st = 3; }
    }

    /// <summary>The vision PC side: each camera's inspection time and verdict per part number.</summary>
    sealed class Host(Func<int, ushort, (int Ms, bool Ok)> inspect) : IPlcHost
    {
        public readonly ConcurrentBag<PlcDelivery> Delivered = new();
        public bool CanInspect => true;
        public ushort ActiveJobNumber => 1;
        public bool Simulation => true;
        public int CameraCount => 2;
        public PlcInspection Inspect(PlcTrigger t) => new(true, 0, 0, "", "", Array.Empty<float>());
        public ushort LoadJob(ushort n) => 0;
        public PlcInspection InspectCamera(int camera, PlcTrigger t)
        {
            var (ms, ok) = inspect(camera, t.TriggerId);
            Thread.Sleep(ms);
            return new PlcInspection(ok, 0, ok ? (ushort)0 : (ushort)(camera + 1), "", "", Array.Empty<float>());
        }
        void IPlcHost.Delivered(PlcDelivery d) => Delivered.Add(d);
    }

    sealed record Part(ushort Id, bool[] Answered, bool[] CamOk, bool[] Refused, bool[] TimedOut)
    {
        public bool Good => Answered.All(a => a) && CamOk.All(o => o);
    }

    /// <summary>Runs <paramref name="parts"/> parts past camera 1 every <paramref name="pitchMs"/>; camera 2 sees
    /// each part <paramref name="cam2LagMs"/> later (0 = front and back at one place, one sensor).</summary>
    static List<Part> RunLine(Host host, int parts, int pitchMs, int cam2LagMs)
    {
        using var rt = new PlcRuntime(new ModbusServer(0, IPAddress.Loopback), host, new PlcOptions { WatchdogMs = 2000 });
        int port = int.Parse(rt.LinkName.Split("port ")[1]);
        using var plc = new PlcTestsAccess.Client(port);
        var inB = new PlcBlock(PlcInput.Size, false);
        inB.SetBit(0, 3, true);                                      // RunEnable
        ushort hb = 0;
        void Beat() { inB.SetU16(2, ++hb); plc.Write(inB.Bytes); }
        PlcBlock Out()
        {
            var o = new PlcBlock(PlcCameraChannels.OutSize, false);
            plc.Read(228, 16).CopyTo(o.Bytes, 0);
            return o;
        }
        var ready = Stopwatch.StartNew();
        while (!(PlcCameraChannels.Ready(Out(), 0) && PlcCameraChannels.Ready(Out(), 1)))
        {
            Assert.True(ready.ElapsedMilliseconds < 5000, "the camera channels never became ready");
            Beat(); Thread.Sleep(20);
        }

        var cams = new[] { new CamStation(), new CamStation() };
        var log = new List<Part>();
        var inspecting = new Part?[2];
        int[] lag = [0, cam2LagMs], reached = [0, 0];               // parts that reached camera c's sensor
        var camIn = new PlcBlock(PlcCameraChannels.InSize, false);
        var clock = Stopwatch.StartNew();
        long beat = 0;
        int lastDue = pitchMs * (parts - 1) + cam2LagMs;
        while (clock.ElapsedMilliseconds < lastDue + 2500)
        {
            long now = clock.ElapsedMilliseconds;
            if (now - beat >= 100) { Beat(); beat = now; }
            var o = Out();
            for (int c = 0; c < 2; c++)
            {
                // the sensor edge of camera c: the next part due at it (parts are numbered at camera 1)
                Part? at = null;
                int n = reached[c];
                if (n < parts && now >= (long)pitchMs * n + lag[c])
                {
                    if (c == 0) log.Add(new Part((ushort)(n + 1), new bool[2], new bool[2], new bool[2], new bool[2]));
                    at = log[n];
                    reached[c]++;
                }
                int status = o.Bytes[8 * c];
                cams[c].Step(at != null, at?.Id ?? 0, status, PlcCameraChannels.IdEcho(o, c));
                if (at != null && !cams[c].NotReady) inspecting[c] = at;      // not inspected: no answer = NOT OK
                if (cams[c].Finished && inspecting[c] is { } p && p.Id == cams[c].TriggerId)
                {
                    p.Refused[c] = cams[c].Refused;
                    p.TimedOut[c] = cams[c].TimedOut;
                    if (!cams[c].TimedOut && !cams[c].Refused) { p.Answered[c] = true; p.CamOk[c] = cams[c].Ok; }
                }
                camIn.SetBit(0, c, cams[c].Trigger);
                camIn.SetU16(2 + 2 * c, cams[c].TriggerId);
            }
            plc.WriteAt(32, camIn.Bytes);
            Thread.Sleep(3);
        }
        Assert.Equal(parts, log.Count);
        return log;
    }

    /// <summary>Zero false accepts: a part passes only when the runtime inspected it OK on both cameras and the
    /// PLC took both results (no delivery of it is NG, not taken or lost).</summary>
    static void NoBadPartPasses(Host host, List<Part> line, Func<int, ushort, bool> truth)
    {
        foreach (var p in line.Where(p => p.Good))
        {
            Assert.True(truth(0, p.Id) && truth(1, p.Id), $"part {p.Id} passed but a camera's verdict was NG");
            for (int c = 0; c < 2; c++)
            {
                var d = host.Delivered.Where(x => x.Camera == c && x.Trigger.TriggerId == p.Id).ToList();
                Assert.True(d.Count == 1 && d[0].Result.Ok && !d[0].NotTaken && !d[0].LinkLossNg,
                    $"part {p.Id} passed but camera {c + 1}'s result was not an OK the PLC took");
            }
        }
    }

    [Fact]
    public void FrontAndBackCamerasOnOneSensorAreMatchedByPartNumber()
    {
        bool Truth(int c, ushort id) => !(c == 1 && id == 4);       // part 4's back is NOT OK
        var host = new Host((c, id) => (c == 0 ? 20 : 45, Truth(c, id)));
        var line = RunLine(host, parts: 8, pitchMs: 250, cam2LagMs: 0);
        Assert.Equal([4], line.Where(p => !p.Good).Select(p => (int)p.Id));
        var four = line[3];
        Assert.True(four.Answered[1] && !four.CamOk[1], "part 4's NOT OK at camera 2 was not taken as its answer");
        Assert.DoesNotContain(line, p => p.TimedOut.Any(t => t) || p.Refused.Any(r => r));
        NoBadPartPasses(host, line, Truth);
    }

    [Fact]
    public void ACameraTooSlowForThePartIsNgAndTheNextPartsAreNotPassedUninspected()
    {
        // camera 2 stands 80 ms after camera 1; on part 3 it takes 1.6 s, longer than the PLC's 1 s result
        // timeout: part 3 is NG (timed out); the parts reaching camera 2 while it is still busy are NG with no
        // answer (the station waits, or the channel is not Ready, so they are not triggered); the line
        // recovers after it
        var host = new Host((c, id) => (c == 1 && id == 3 ? 1600 : 25, true));
        var line = RunLine(host, parts: 12, pitchMs: 250, cam2LagMs: 80);
        var three = line[2];
        Assert.True(three.TimedOut[1] && !three.Good, "part 3 had no camera 2 result in time but was not NG");
        var ng = line.Where(p => !p.Good).ToList();
        Assert.True(ng.Count >= 2, "a part reached camera 2 while it was busy, yet only part 3 was NG");
        Assert.Equal(Enumerable.Range(3, ng.Count), ng.Select(p => (int)p.Id));   // 3 and the parts right after it
        Assert.All(ng, p => Assert.False(p.Answered[1], $"part {p.Id} had an answer from the busy camera 2"));
        Assert.True(line[0].Good && line[1].Good, "the parts before the slow one should pass");
        Assert.True(line[^1].Good && line[^2].Good, "the line did not recover after the slow inspection");
        // the late result of part 3 never counts as OK: handed over as not taken (the PLC made it NG)
        var late = Assert.Single(host.Delivered, d => d.Camera == 1 && d.Trigger.TriggerId == 3);
        Assert.True(late.NotTaken && !late.Result.Ok);
        NoBadPartPasses(host, line, (_, _) => true);
    }
}
