using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using VisionCore;
using VisionCore.Plc;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using Xunit;

/// <summary>The runtime behind its own Modbus TCP server with a simulated PLC (PLC-INTERFACE 4.1, 5.6): a part
/// inspected OK whose result the PLC never took is NG 100 in the results, the counters and results.db; the part
/// id the PLC sent is kept as text; JobActive follows Start and Stop; a camera channel's part is counted when the
/// PLC took it, and one it got as NG 100 reaches the plant server as NG ("PLC", "link_lost") with its image kept as
/// an NG image; the test signal is an engineer's, in Teach only. The frames hold an EAN-13, so parts are OK.</summary>
public sealed class PlcLineTests : IAsyncLifetime
{
    RuntimeFixture _f = null!;
    Metadata _op = null!;
    readonly FakePlant _plant = new();

    public async Task InitializeAsync()
    {
        _f = await RuntimeFixture.StartAsync(f =>
        {
            File.WriteAllText(Path.Combine(f.Folder, "plant-token.txt"), "station-token");
            var folder = Path.Combine(f.Folder, "ean");
            Directory.CreateDirectory(folder);
            var (px, w, h) = Ean13Frame("4006381333931");
            BarcodeReading.BmpImage.SaveGray(Path.Combine(folder, "a.bmp"), px, w, h);
            (px, w, h) = Ean13Frame("5901234123457");          // the next part's frame differs
            BarcodeReading.BmpImage.SaveGray(Path.Combine(folder, "b.bmp"), px, w, h);
            new JobStore(Path.Combine(f.Folder, "jobs")).Save(new Job
            {
                Name = "Ean",
                Number = 11,
                Cameras = [new CameraConfig { Name = "Top", TestFolder = folder }],
                Tools = [new CodeToolConfig { Name = "Read code", Format = BarcodeReading.CodeFormat.Ean13, ExpectedCount = 1, TimeBudgetMs = 1000 }],
            }, "test");
            Directory.CreateDirectory(Path.Combine(f.Folder, "data"));
            File.WriteAllText(Path.Combine(f.Folder, "data", "plc.json"),
                """{ "Protocol": "ModbusTcp", "Port": 0, "WatchdogMs": 1000, "BindAddress": "127.0.0.1" }""");
        },
        $"--Plant:ServerUrl={_plant.Url}", "--Plant:AllowHttp=true", "--Plant:TokenFile=plant-token.txt",
        "--Plant:Plant=P", "--Plant:Line=L", "--Plant:Station=S1", "--Plant:Sending:HeartbeatEvery=00:00:00.200",
        "--Runtime:LineSpeedPpm=225");
        _op = await _f.SignInOperator();
    }

    public async Task DisposeAsync()
    {
        await _f.DisposeAsync();
        _plant.Dispose();
    }

    [Fact]
    public async Task A_part_whose_OK_the_PLC_never_took_is_recorded_as_NG_100()
    {
        var plcService = _f.App.Services.GetRequiredService<PlcService>();
        Assert.Null(plcService.LastError);
        var port = int.Parse(plcService.Runtime!.LinkName.Split("port ")[1]);
        using var plc = new Plc(port);
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Ean" }, _op)).Ok);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = _f.Runtime().StreamResults(new StreamResultsRequest(), cancellationToken: cts.Token);
        await Task.Delay(100);
        var start = await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op);
        Assert.True(start.Ok, start.ErrorText);

        plc.RunEnable();
        var o = plc.Wait(x => x.Ready && x.JobActive);
        Assert.Equal(11, o.ActiveJob);

        // part 1: OK, and the PLC takes it (drops its trigger)
        o = plc.Trigger(8001, "A12-0001");
        Assert.True(o.Ok, $"the EAN-13 frame was not read OK (NG reason {o.NgReason})");
        plc.Drop();
        var r1 = await Next(call, cts.Token);
        Assert.True(r1.Pass);
        Assert.Equal("A12-0001", r1.PartId);

        // the Run screen's Batch and PLC card and chart lines (UI-UX-SPEC.md 4.1)
        var status = await _f.Runtime().GetStatusAsync(new GetStatusRequest());
        Assert.Equal(225f, status.LineSpeedPpm);
        Assert.Equal(60000f / 225, status.CycleBudgetMs, 0.01f);
        Assert.True(status.Plc is { LinkUp: true, HeartbeatOk: true, RunEnable: true, LastTriggerId: 8001, LastResultPass: true });
        Assert.NotNull(status.Plc.LastResultTime);
        Assert.StartsWith("ModbusTcp", status.Plc.Link);

        // part 2: OK written, then the PLC's heartbeat stops while it still holds its trigger
        Assert.True(plc.Trigger(8002, "A12-0002").Ok);
        Thread.Sleep(2000);
        o = plc.Read();
        Assert.True(o.Ng && !o.Ok);
        Assert.Equal(PlcCodes.HeartbeatLost, o.NgReason);
        var r2 = await Next(call, cts.Token);
        Assert.False(r2.Pass);
        Assert.Equal("A12-0002", r2.PartId);
        Assert.Contains(r2.Tools, t => t.Name == "PLC link" && !t.Pass);
        status = await _f.Runtime().GetStatusAsync(new GetStatusRequest());
        Assert.True(status.Plc is { HeartbeatOk: false, LastTriggerId: 8002, LastResultPass: false }, status.Plc?.ToString());
        Assert.False(status.PlcConnected);

        // a trigger during the loss keeps error 100 (one heartbeat change each: still lost)
        plc.Drop();
        plc.Raise(8003, "A12-0003");
        o = plc.Poll(x => x.TriggerRejected);
        Assert.Equal(PlcCodes.HeartbeatLost, o.ErrorCode);

        var counters = await _f.Runtime().GetCountersAsync(new GetCountersRequest());
        Assert.Equal(2ul, counters.Total);
        Assert.Equal(1ul, counters.Ok);
        Assert.Equal(1ul, counters.Ng);
        Assert.Equal(1ul, counters.NgReasons["plc_link_lost"]);

        var log = _f.App.Services.GetRequiredService<VisionReports.ResultLog>();
        var rows = log.Inspections(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Ok);
        Assert.Equal("A12-0001", rows[0].PartText);
        Assert.False(rows[1].Ok);
        Assert.Equal("PLC link", rows[1].FailTool);
        Assert.Equal(Station.PlcLinkLostError, rows[1].Reason);
        Assert.Equal("A12-0002", rows[1].PartText);

        // the part's image was judged OK when taken: it is kept as an NG image with the PLC's reason
        var store = _f.App.Services.GetRequiredService<Station>().NgImages;
        Until(() => store.Counts.PerReason.TryGetValue("plc_link_lost", out var c) && c.Saved == 1, "the NG 100 part's image was not kept");

        // heartbeat back, then Stop: the job stays loaded but is no longer active
        plc.Drop();
        plc.Wait(x => x.ErrorCode == 0);
        await _f.Runtime().StopAsync(new StopRequest(), _op);
        o = plc.Wait(x => !x.JobActive);
        Assert.Equal(11, o.ActiveJob);
        Assert.False(o.Ready);
    }

    [Fact]
    public async Task A_camera_fault_alarm_ends_with_Stop()
    {
        var station = _f.App.Services.GetRequiredService<Station>();
        new JobStore(Path.Combine(_f.Folder, "jobs")).Save(new Job
        {
            Name = "NoSide",
            Number = 12,
            Cameras = [new CameraConfig { Name = "Top", TestFolder = _f.FramesFolder }, new CameraConfig { Name = "Side", TestFolder = Path.Combine(_f.Folder, "empty") }],
            Tools =
            [
                new CodeToolConfig { Name = "Top code", Camera = 0, Format = BarcodeReading.CodeFormat.QrCode, ExpectedCount = 1 },
                new CodeToolConfig { Name = "Side code", Camera = 1, Format = BarcodeReading.CodeFormat.QrCode, ExpectedCount = 1 },
            ],
        }, "test");
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "NoSide" }, _op)).Ok);
        station.Start(RunMode.PlcSimulation);
        Assert.False(station.InspectCamera(1, new PlcTrigger(1, "P1", 0, 2, false)).Ok);   // no picture: NG
        var status = await _f.Runtime().GetStatusAsync(new GetStatusRequest());
        Assert.Contains(status.Alarms, a => a.Code == PlcCodes.CameraNotFound);
        await _f.Runtime().StopAsync(new StopRequest(), _op);
        status = await _f.Runtime().GetStatusAsync(new GetStatusRequest());
        Assert.DoesNotContain(status.Alarms, a => a.Code == PlcCodes.CameraNotFound);
    }

    [Fact]
    public async Task A_camera_channel_part_is_counted_when_the_PLC_took_it_and_an_NG_100_reaches_the_plant_with_its_image()
    {
        var plcService = _f.App.Services.GetRequiredService<PlcService>();
        var port = int.Parse(plcService.Runtime!.LinkName.Split("port ")[1]);
        using var plc = new Plc(port);
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Ean" }, _op)).Ok);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = _f.Runtime().StreamResults(new StreamResultsRequest(), cancellationToken: cts.Token);
        await Task.Delay(100);
        // the plant server (FakePlant) has the changeover check off for this line: wait for the heartbeat
        // to carry that before the production start, so Start is not gated (ChangeoverTests covers the gate)
        Until(() => _f.Runtime().GetStatusAsync(new GetStatusRequest()).GetAwaiter().GetResult()
            .Changeover.State == Changeover.Types.State.NotNeeded, "the changeover check never cleared");
        // the simulated camera plays the job camera's frames through the intake (reused frame buffers)
        var start = await _f.Runtime().StartAsync(new StartRequest { Source = Source.Camera }, _op);
        Assert.True(start.Ok, start.ErrorText);
        plc.RunEnable();
        plc.Wait(x => x.Ready && x.JobActive);

        // part 1 on camera channel 1: OK, but not counted until the PLC drops its trigger (took the result)
        plc.PartId("C-0001");
        var c = plc.FireCamera(0, 41);
        Assert.True(PlcCameraChannels.Ok(c, 0), "the EAN-13 frame was not read OK");
        plc.Wait(_ => false, 300, quietly: true);           // the heartbeat goes on while the part is held
        Assert.Equal(0ul, (await _f.Runtime().GetCountersAsync(new GetCountersRequest())).Total);
        plc.DropCamera(0);
        var r1 = await Next(call, cts.Token);
        Assert.True(r1.Pass);
        Assert.Equal("C-0001", r1.PartId);

        // part 2: OK written, then the PLC's heartbeat stops while it still holds the camera trigger
        plc.Wait(_ => plcService.Runtime!.Connected);       // a slow Mac may have missed a beat meanwhile
        plc.PartId("C-0002");
        Assert.True(PlcCameraChannels.Ok(plc.FireCamera(0, 42), 0));
        Thread.Sleep(2000);
        c = plc.ReadCamera();
        Assert.True(PlcCameraChannels.Done(c, 0) && !PlcCameraChannels.Ok(c, 0));
        var r2 = await Next(call, cts.Token);
        Assert.False(r2.Pass);
        Assert.Equal("C-0002", r2.PartId);
        Assert.Contains(r2.Tools, t => t.Name == "PLC link" && !t.Pass);
        var counters = await _f.Runtime().GetCountersAsync(new GetCountersRequest());
        Assert.Equal((2ul, 1ul, 1ul), (counters.Total, counters.Ok, counters.Ng));
        Assert.Equal(1ul, counters.NgReasons["plc_link_lost"]);

        // the plant server: OK, then NG with the PLC's reason, the part id and the kept image
        var sent = _plant.Results(2);
        Assert.True(sent[0].GetProperty("ok").GetBoolean());
        Assert.Equal("C-0001", sent[0].GetProperty("part_id").GetString());
        var ng = sent[1];
        Assert.False(ng.GetProperty("ok").GetBoolean());
        Assert.Equal("C-0002", ng.GetProperty("part_id").GetString());
        var why = Assert.Single(ng.GetProperty("reasons").EnumerateArray());
        Assert.Equal(("PLC", "link_lost"), (why.GetProperty("tool").GetString(), why.GetProperty("reason").GetString()));
        var imageId = ng.GetProperty("image_id").GetString();
        Assert.False(string.IsNullOrEmpty(imageId));
        var store = _f.App.Services.GetRequiredService<Station>().NgImages;
        Until(() => store.Find(imageId!).State == StationGuard.NgImageState.Found, "the NG 100 part's image was not saved");
        var file = Assert.Single(store.Find(imageId!).Files);
        // part 2's own frame (the folder's second), not part 1's, whose buffer the camera reused for it
        var saved = GrayImage.Load(file.Path);
        var (px, w, h) = Ean13Frame("5901234123457");
        Assert.Equal((w, h), (saved.Width, saved.Height));
        Assert.Equal(px, saved.Pixels);

        plc.Drop();
        plc.Wait(x => x.ErrorCode == 0);
        await _f.Runtime().StopAsync(new StopRequest(), _op);
    }

    [Fact]
    public async Task The_test_signal_is_an_engineers_in_Teach_only_and_a_two_second_pulse()
    {
        var plcService = _f.App.Services.GetRequiredService<PlcService>();
        var port = int.Parse(plcService.Runtime!.LinkName.Split("port ")[1]);
        using var plc = new Plc(port);
        plc.Wait(_ => plcService.Runtime!.Connected);
        var signIn = await _f.Sessions().SignInAsync(new SignInRequest { User = "eng", Password = "secret-pass", Client = ClientKind.Studio });
        Assert.True(signIn.Ok, signIn.ErrorText);
        var eng = RuntimeFixture.Bearer(signIn.Token);

        // an operator may not; nobody signed in may not
        var e = await Assert.ThrowsAsync<RpcException>(async () => await _f.Runtime().PlcTestSignalAsync(new PlcTestSignalRequest { Ok = true }, _op));
        Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
        e = await Assert.ThrowsAsync<RpcException>(async () => await _f.Runtime().PlcTestSignalAsync(new PlcTestSignalRequest { Ok = true }));
        Assert.Equal(StatusCode.Unauthenticated, e.StatusCode);

        // the line runs: Teach only
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Ean" }, _op)).Ok);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        var r = await _f.Runtime().PlcTestSignalAsync(new PlcTestSignalRequest { Ok = true }, eng);
        Assert.Equal((false, "runtime.refused.stopFirst"), (r.Ok, r.ErrorKey));
        await _f.Runtime().StopAsync(new StopRequest(), _op);

        // the PLC sets RunEnable: refused (the calls above sent no heartbeat: wait until the link is up again)
        plc.RunEnable();
        plc.Wait(_ => plcService.Runtime!.Connected);
        r = await _f.Runtime().PlcTestSignalAsync(new PlcTestSignalRequest { Ok = true }, eng);
        Assert.Equal((false, "runtime.refused.plcRunEnable"), (r.Ok, r.ErrorKey));
        Assert.False(plc.Read().Done);

        // RunEnable off, line stopped: OK with Done for 2 s, then back to 0
        plc.RunEnable(false);
        plc.Wait(_ => plcService.Runtime!.Connected);
        var sw = System.Diagnostics.Stopwatch.StartNew();   // before the call: the pulse starts during it
        r = await _f.Runtime().PlcTestSignalAsync(new PlcTestSignalRequest { Ok = true }, eng);
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal((uint)PlcRuntime.TestPulseMs, r.PulseMs);
        Assert.True(plc.Wait(x => x.Done).Ok);
        plc.Wait(x => !x.Done, PlcRuntime.TestPulseMs + 5000);
        Assert.True(sw.ElapsedMilliseconds >= PlcRuntime.TestPulseMs, "the test signal ended early");

        // NG, then OK while the NG is on: refused
        plc.Wait(_ => plcService.Runtime!.Connected);
        Assert.True((await _f.Runtime().PlcTestSignalAsync(new PlcTestSignalRequest { Ok = false }, eng)).Ok);
        Assert.True(plc.Wait(x => x.Done).Ng);
        r = await _f.Runtime().PlcTestSignalAsync(new PlcTestSignalRequest { Ok = true }, eng);
        Assert.Equal((false, "runtime.refused.plcLatchedNg"), (r.Ok, r.ErrorKey));
        Assert.False(plc.Read().Ok);
    }

    [Fact]
    public async Task WhoAmI_says_who_may_test_and_the_PLC_page_test_signal_is_no_part()
    {
        var plcService = _f.App.Services.GetRequiredService<PlcService>();
        var signIn = await _f.Sessions().SignInAsync(new SignInRequest { User = "eng", Password = "secret-pass", Client = ClientKind.Studio });
        Assert.True(signIn.Ok, signIn.ErrorText);
        var eng = RuntimeFixture.Bearer(signIn.Token);
        Assert.True((await _f.Sessions().WhoAmIAsync(new WhoAmIRequest(), eng)).MayTestPlc);
        Assert.False((await _f.Sessions().WhoAmIAsync(new WhoAmIRequest(), _op)).MayTestPlc);
        Assert.False((await _f.Sessions().WhoAmIAsync(new WhoAmIRequest())).MayTestPlc);

        // no PLC connected yet: refused, nothing written
        Assert.Equal("plc.refused.plc_off", plcService.TestSignal(true));

        var port = int.Parse(plcService.Runtime!.LinkName.Split("port ")[1]);
        using var plc = new Plc(port);
        plc.Wait(_ => plcService.Runtime!.Connected);

        // the line runs: Teach only
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Ean" }, _op)).Ok);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        Assert.Equal("plc.refused.stop_first", plcService.TestSignal(true));
        await _f.Runtime().StopAsync(new StopRequest(), _op);
        plc.Wait(_ => plcService.Runtime!.Connected);

        plc.RunEnable();
        plc.Wait(_ => plcService.Runtime!.Connected);
        Assert.Equal("plc.refused.run_enable", plcService.TestSignal(true));
        plc.RunEnable(false);
        plc.Wait(_ => plcService.Runtime!.Connected);

        // sent: NG with Done reaches the PLC, but no part is counted, logged or sent to the plant
        var before = await _f.Runtime().GetCountersAsync(new GetCountersRequest());
        var parts = 0;
        void OnPart(PartRecord _) => Interlocked.Increment(ref parts);
        plcService.PartLeft += OnPart;
        try
        {
            Assert.Null(plcService.TestSignal(false));
            Assert.True(plc.Wait(x => x.Done).Ng);
            Assert.Equal("plc.refused.latched_ng", plcService.TestSignal(true));
            plc.Wait(x => !x.Done, PlcRuntime.TestPulseMs + 5000);
        }
        finally { plcService.PartLeft -= OnPart; }
        var after = await _f.Runtime().GetCountersAsync(new GetCountersRequest());
        Assert.Equal((before.Total, before.Ng), (after.Total, after.Ng));
        Assert.Equal(0, parts);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" 192.168.10.5 ", true)]
    [InlineData("::1", true)]
    [InlineData("plc-card", false)]
    [InlineData("192.168.10.300", false)]
    public void The_PLC_network_card_address_is_checked_before_saving(string address, bool good) =>
        Assert.Equal(good, PlcService.BindAddressError(address) is null);

    static void Until(Func<bool> cond, string message, int ms = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            Assert.True(sw.ElapsedMilliseconds < ms, message);
            Thread.Sleep(20);
        }
    }

    static async Task<InspectionResult> Next(AsyncServerStreamingCall<InspectionResult> call, CancellationToken ct)
    {
        Assert.True(await call.ResponseStream.MoveNext(ct), "no result came");
        return call.ResponseStream.Current;
    }

    // EAN-13 modules (1 = bar), as LinearReportTests draws them: 3 px a module, a wide quiet zone
    static (byte[] Px, int W, int H) Ean13Frame(string digits)
    {
        string[] lCodes = { "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011" };
        string[] parity = { "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL" };
        string R(int d) => new(lCodes[d].Select(c => c == '0' ? '1' : '0').ToArray());
        string G(int d) => new(R(d).Reverse().ToArray());
        var sb = new StringBuilder("101");
        for (int i = 1; i <= 6; i++)
        {
            int d = digits[i] - '0';
            sb.Append(parity[digits[0] - '0'][i - 1] == 'L' ? lCodes[d] : G(d));
        }
        sb.Append("01010");
        for (int i = 7; i <= 12; i++) sb.Append(R(digits[i] - '0'));
        sb.Append("101");
        const int x = 3, left = 60, top = 40, barH = 90;
        var m = sb.ToString();
        int w = left * 2 + m.Length * x, h = top * 2 + barH;
        var px = Enumerable.Repeat((byte)220, w * h).ToArray();
        for (int k = 0; k < m.Length; k++)
            if (m[k] == '1')
                for (int y = top; y < top + barH; y++)
                    for (int c = left + k * x; c < left + (k + 1) * x; c++) px[y * w + c] = 30;
        return (px, w, h);
    }

    /// <summary>The PLC side: raw Modbus TCP (FC3 / FC16), the input block at register 0, the output block at 100.</summary>
    sealed class Plc : IDisposable
    {
        readonly TcpClient _c = new();
        readonly PlcBlock _in = new(PlcInput.Size, false);
        ushort _tid, _hb;

        public Plc(int port) => _c.Connect(IPAddress.Loopback, port);

        byte[] Call(byte[] pdu)
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

        byte[] ReadRegs(int a, int n)
        {
            var r = Call([3, (byte)(a >> 8), (byte)a, 0, (byte)n]);
            var block = new byte[2 * n];
            for (int i = 0; i < n; i++) { ushort v = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2 + 2 * i)); block[2 * i] = (byte)v; block[2 * i + 1] = (byte)(v >> 8); }
            return block;
        }

        void WriteRegs(int a, byte[] b)
        {
            int n = b.Length / 2;
            var pdu = new byte[6 + b.Length];
            pdu[0] = 16; pdu[1] = (byte)(a >> 8); pdu[2] = (byte)a; pdu[3] = 0; pdu[4] = (byte)n; pdu[5] = (byte)(2 * n);
            for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + 2 * i), (ushort)(b[2 * i] | b[2 * i + 1] << 8));
            Assert.Equal(16, Call(pdu)[0]);
        }

        // the input block with the next heartbeat
        void Send()
        {
            _in.SetU16(2, ++_hb);
            WriteRegs(0, _in.Bytes);
        }

        // camera channels (PLC-INTERFACE 3.5): triggers at register 32, results at 228
        readonly PlcBlock _cam = new(PlcCameraChannels.InSize, false);

        public PlcBlock ReadCamera()
        {
            var b = new PlcBlock(PlcCameraChannels.OutSize, false);
            ReadRegs(228, PlcCameraChannels.OutSize / 2).CopyTo(b.Bytes, 0);
            return b;
        }

        public PlcBlock FireCamera(int cam, ushort id)
        {
            _cam.SetU16(2 + 2 * cam, id);
            _cam.SetBit(0, cam, true);
            WriteRegs(32, _cam.Bytes);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var o = ReadCamera();
                if (PlcCameraChannels.Done(o, cam) && PlcCameraChannels.IdEcho(o, cam) == id) return o;
                Assert.True(sw.ElapsedMilliseconds < 10000, "the camera channel gave no result in time");
                Send();
                Thread.Sleep(20);
            }
        }

        public void DropCamera(int cam) { _cam.SetBit(0, cam, false); WriteRegs(32, _cam.Bytes); Send(); }

        public void PartId(string id) { _in.SetStr(16, 32, id); Send(); }

        public PlcOutput Read()
        {
            var b = new PlcBlock(PlcOutput.Size, false);
            ReadRegs(100, 125).CopyTo(b.Bytes, 0);
            ReadRegs(225, 3).CopyTo(b.Bytes, 250);
            return new PlcOutput(b);
        }

        // the heartbeat keeps going while waiting
        // quietly: at the end of the time return the output as it is (a pause with the heartbeat going on)
        public PlcOutput Wait(Func<PlcOutput, bool> cond, int ms = 5000, bool quietly = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var o = Read();
                if (cond(o) || quietly && sw.ElapsedMilliseconds >= ms) return o;
                Assert.True(sw.ElapsedMilliseconds < ms, "the PLC output did not reach the expected state in time");
                Send();
                Thread.Sleep(20);
            }
        }

        // no heartbeat while waiting: a lost one stays lost
        public PlcOutput Poll(Func<PlcOutput, bool> cond)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var o = Read();
                if (cond(o)) return o;
                Assert.True(sw.ElapsedMilliseconds < 3000, "the PLC output did not reach the expected state in time");
                Thread.Sleep(10);
            }
        }

        public void RunEnable(bool on = true) { _in.SetBit(0, 3, on); Send(); }

        public void Raise(ushort id, string partId)
        {
            _in.SetU16(8, id);
            _in.SetStr(16, 32, partId);
            _in.SetBit(0, 0, true);
            Send();
        }

        public PlcOutput Trigger(ushort id, string partId)
        {
            Raise(id, partId);
            return Wait(x => x.Done && x.TriggerIdEcho == id, 10000);
        }

        public void Drop() { _in.SetBit(0, 0, false); Send(); }

        public void Dispose() => _c.Dispose();
    }

    /// <summary>The plant server's ingest, as the station agent sees it: takes every event, answers with the
    /// highest sequence number stored, and keeps the events for the test.</summary>
    sealed class FakePlant : IDisposable
    {
        readonly HttpListener _l = new();
        readonly ConcurrentQueue<JsonElement> _events = new();
        public string Url { get; }

        public FakePlant()
        {
            using (var t = new TcpListener(IPAddress.Loopback, 0))
            {
                t.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)t.LocalEndpoint).Port}/";
            }
            _l.Prefixes.Add(Url);
            _l.Start();
            _ = Task.Run(Serve);
        }

        async Task Serve()
        {
            while (true)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
                var answer = "{}";
                if (c.Request.Url!.AbsolutePath.EndsWith("/events"))
                {
                    using var doc = JsonDocument.Parse(body);
                    long top = 0;
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        _events.Enqueue(e.Clone());
                        top = Math.Max(top, e.GetProperty("seq").GetInt64());
                    }
                    answer = $"{{\"stored_up_to\":{top}}}";
                }
                else if (c.Request.Url!.AbsolutePath.EndsWith("/heartbeat"))
                {
                    // these tests do not exercise the changeover check: this plant has it off for the line,
                    // so Start is not gated (the check itself is covered by ChangeoverTests).
                    answer = "{\"changeover\":{\"required\":false,\"state\":\"not_needed\"}}";
                }
                var bytes = Encoding.UTF8.GetBytes(answer);
                c.Response.ContentType = "application/json";
                await c.Response.OutputStream.WriteAsync(bytes);
                c.Response.Close();
            }
        }

        /// <summary>The first n inspection results, in the order sent.</summary>
        public JsonElement[] Results(int n)
        {
            JsonElement[] Now() => _events.Where(e => e.GetProperty("kind").GetString() == "inspection.result").ToArray();
            Until(() => Now().Length >= n, "the plant server did not get the results in time", 15000);
            return Now().Take(n).ToArray();
        }

        public void Dispose() => _l.Close();
    }
}
