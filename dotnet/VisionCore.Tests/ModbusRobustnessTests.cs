using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VisionCore.Plc;
using Xunit;

/// <summary>PLC-INTERFACE.md section 2.1: the Modbus TCP server survives anything on the PLC network
/// (broken frames, unknown functions, partial frames, slow senders, scans, many and idle clients, fast
/// polling). It answers with a Modbus exception or closes that connection only, keeps serving the PLC,
/// never writes a block from a bad frame, and a connection that never wrote never counts as a PLC loss.</summary>
public class ModbusRobustnessTests
{
    private static readonly (int Start, int Count)[] Areas =
    {
        (ModbusServer.InStart, ModbusServer.InCount), (ModbusServer.CamInStart, ModbusServer.CamInCount),
        (ModbusServer.DiagStart, ModbusServer.DiagCount), (ModbusServer.OutStart, ModbusServer.OutCount),
        (ModbusServer.CamOutStart, ModbusServer.CamOutCount), (ModbusServer.SetStart, ModbusServer.SetCount),
        (ModbusServer.PartStart, ModbusServer.PartCount), (ModbusServer.TextStart, ModbusServer.TextCount),
    };

    // the areas only the vision system writes; the PLC may only read them
    private static readonly (int Start, int Count)[] VisionAreas =
    {
        (ModbusServer.OutStart, ModbusServer.OutCount), (ModbusServer.CamOutStart, ModbusServer.CamOutCount),
        (ModbusServer.SetStart, ModbusServer.SetCount),
    };

    private static bool Mapped(int a) => Areas.Any(r => a >= r.Start && a < r.Start + r.Count);

    /// <summary>A raw TCP client that sends whatever bytes it is given.</summary>
    private sealed class Raw : IDisposable
    {
        private readonly TcpClient _c = new();
        public Raw(int port) { _c.Connect(IPAddress.Loopback, port); _c.NoDelay = true; S = _c.GetStream(); }
        public readonly NetworkStream S;
        public void Send(byte[] b) => S.Write(b);

        /// <summary>The next reply frame as (transaction id, pdu), or null when the server closed the connection.</summary>
        public (ushort Tid, byte[] Pdu)? Reply(int ms = 2000)
        {
            S.ReadTimeout = ms;
            var head = new byte[7];
            try
            {
                if (!Fill(head)) return null;
                var pdu = new byte[BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4)) - 1];
                if (!Fill(pdu)) return null;
                return (BinaryPrimitives.ReadUInt16BigEndian(head), pdu);
            }
            catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { return null; }
        }

        private bool Fill(byte[] b)
        {
            int got = 0;
            while (got < b.Length) { int n = S.Read(b, got, b.Length - got); if (n == 0) return false; got += n; }
            return true;
        }

        /// <summary>True once the server has closed this connection (within the wait).</summary>
        public bool Closed(int ms = 2000)
        {
            S.ReadTimeout = ms;
            try { return S.Read(new byte[1], 0, 1) == 0; }
            catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { return true; }
            catch (IOException) { return false; }   // timed out: still open
        }

        public void Dispose() => _c.Dispose();
    }

    private static byte[] Frame(ushort tid, byte[] pdu, ushort protocol = 0, int? length = null, byte unit = 1)
    {
        var f = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(f, tid);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), protocol);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), (ushort)(length ?? pdu.Length + 1));
        f[6] = unit;
        pdu.CopyTo(f, 7);
        return f;
    }

    private static byte[] Read(int fc, int a, int n) => new[] { (byte)fc, (byte)(a >> 8), (byte)a, (byte)(n >> 8), (byte)n };
    private static byte[] Write1(int a, ushort v) => new[] { (byte)6, (byte)(a >> 8), (byte)a, (byte)(v >> 8), (byte)v };
    private static byte[] WriteN(int a, int n, byte byteCount, int dataBytes, Random r)
    {
        var p = new byte[6 + dataBytes];
        p[0] = 16; p[1] = (byte)(a >> 8); p[2] = (byte)a; p[3] = (byte)(n >> 8); p[4] = (byte)n; p[5] = byteCount;
        r.NextBytes(p.AsSpan(6));
        return p;
    }

    /// <summary>Every block as the PLC reads it, register by register.</summary>
    private static ushort[] Dump(PlcTestsAccess.Client c)
    {
        var all = new List<ushort>();
        foreach (var (start, count) in Areas)
            for (int a = start; a < start + count; a += 125)
            {
                var b = c.Read(a, Math.Min(125, start + count - a));
                for (int i = 0; i < b.Length; i += 2) all.Add((ushort)(b[i] | b[i + 1] << 8));
            }
        return all.ToArray();
    }

    private static ModbusServer Server(ModbusServerOptions? o = null)
    {
        var s = new ModbusServer(0, IPAddress.Loopback, o);
        // the vision-written blocks hold a pattern a stray write would break
        s.WriteOutput(Enumerable.Range(0, PlcOutput.Size).Select(i => (byte)(i * 7 + 1)).ToArray());
        s.WriteSettings(Enumerable.Range(0, LineSettings.Size).Select(i => (byte)(i * 5 + 3)).ToArray());
        s.WriteCameraOut(Enumerable.Range(0, PlcCameraChannels.OutSize).Select(i => (byte)(i * 3 + 2)).ToArray());
        return s;
    }

    private static void Until(Func<bool> cond, int ms, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond()) { Assert.True(sw.ElapsedMilliseconds < ms, what); Thread.Sleep(5); }
    }

    private static void AssertException(byte[] reply, int fc, int code)
    {
        Assert.Equal(2, reply.Length);
        Assert.Equal(fc | 0x80, reply[0]);
        Assert.Equal(code, reply[1]);
    }

    private enum Bad { RandomBytes, UnknownFunction, Write1Length, WriteNCount, VisionArea, Unmapped, ReadCount, Protocol, Length, Truncated }

    [Fact]
    public void FuzzedFramesNeverWriteAndNeverStopThePlc()
    {
        using var server = Server();
        int changes = 0;
        server.InputChanged += () => Interlocked.Increment(ref changes);
        using var plc = new PlcTestsAccess.Client(server.Port);
        var input = new byte[PlcInput.Size];
        input[0] = 0x08;
        Assert.Equal(16, plc.Write(input));
        int plcWrites = 1;
        var before = Dump(plc);

        var r = new Random(20261031);
        Raw? conn = null;
        ushort tid = 0;
        for (int i = 0; i < 1500; i++)
        {
            conn ??= new Raw(server.Port);
            var kind = (Bad)r.Next(Enum.GetValues<Bad>().Length);
            byte[] pdu;
            int expectFc = -1, expectCode = -1;   // -1: the server closes the connection
            switch (kind)
            {
                case Bad.RandomBytes:
                {
                    var junk = new byte[r.Next(1, 300)];
                    r.NextBytes(junk);
                    conn.Send(junk);
                    conn.Dispose(); conn = null;   // the stream is out of step: whatever the server made of it, start again
                    continue;
                }
                case Bad.UnknownFunction:
                {
                    int[] known = { 3, 4, 6, 16 };
                    int fc;
                    do fc = r.Next(0, 256); while (known.Contains(fc));
                    pdu = new byte[r.Next(1, 253)];
                    r.NextBytes(pdu);
                    pdu[0] = (byte)fc;
                    expectFc = fc; expectCode = 1;
                    break;
                }
                case Bad.Write1Length:
                {
                    int len = r.Next(1, 40);
                    if (len == 5) len = 4;
                    pdu = new byte[len];
                    r.NextBytes(pdu);
                    pdu[0] = 6;
                    if (len >= 3) { pdu[1] = 0; pdu[2] = (byte)r.Next(ModbusServer.InCount); }   // aimed at the input block
                    expectFc = 6; expectCode = 3;
                    break;
                }
                case Bad.WriteNCount:
                {
                    int n = r.Next(1, 124), data = 2 * n;
                    byte count = (byte)(2 * n);
                    switch (r.Next(4))
                    {
                        case 0: count = (byte)(2 * n + r.Next(1, 20)); break;                 // byte count disagrees with n
                        case 1: data = Math.Max(0, 2 * n - r.Next(1, 2 * n + 1)); break;      // fewer data bytes than stated
                        case 2: data = 2 * n + r.Next(1, 4); if (6 + data > 253) data = 2 * n - 1; break;   // more than stated
                        default: n = r.Next(124, 0x10000); count = (byte)r.Next(256); data = r.Next(0, 200); break;   // too many registers
                    }
                    pdu = WriteN(r.Next(ModbusServer.InCount), n, count, data, r);
                    pdu[1] = 0;
                    expectFc = 16; expectCode = 3;
                    break;
                }
                case Bad.VisionArea:
                {
                    var (start, count) = VisionAreas[r.Next(VisionAreas.Length)];
                    int a = start + r.Next(count);
                    if (r.Next(2) == 0) pdu = Write1(a, (ushort)r.Next(0x10000));
                    else { int n = r.Next(1, Math.Min(123, start + count - a) + 1); pdu = WriteN(a, n, (byte)(2 * n), 2 * n, r); }
                    expectFc = pdu[0]; expectCode = 2;
                    break;
                }
                case Bad.Unmapped:
                {
                    int a;
                    do a = r.Next(2) == 0 ? r.Next(0, 700) : r.Next(0x10000); while (Mapped(a));
                    pdu = r.Next(3) switch { 0 => Write1(a, 0xFFFF), 1 => WriteN(a, 1, 2, 2, r), _ => Read(3 + r.Next(2), a, r.Next(1, 126)) };
                    expectFc = pdu[0]; expectCode = 2;
                    break;
                }
                case Bad.ReadCount:
                {
                    var (start, _) = Areas[r.Next(Areas.Length)];
                    pdu = Read(3 + r.Next(2), start, r.Next(2) == 0 ? 0 : r.Next(126, 0x10000));
                    if (r.Next(3) == 0) pdu = pdu.Concat(new byte[r.Next(1, 5)]).ToArray();   // a read with trailing bytes
                    expectFc = pdu[0]; expectCode = 3;
                    break;
                }
                case Bad.Protocol:
                    conn.Send(Frame(++tid, Write1(r.Next(ModbusServer.InCount), 0xBEEF), protocol: (ushort)r.Next(1, 0x10000)));
                    Assert.True(conn.Closed(), "a frame that is not Modbus TCP must close its connection");
                    conn.Dispose(); conn = null;
                    continue;
                case Bad.Length:
                {
                    int len = r.Next(3) switch { 0 => 0, 1 => 1, _ => r.Next(255, 0x10000) };
                    conn.Send(Frame(++tid, Write1(0, 0xBEEF), length: len));
                    Assert.True(conn.Closed(), $"MBAP length {len} must close its connection");
                    conn.Dispose(); conn = null;
                    continue;
                }
                default:   // Bad.Truncated: the header promises more than ever comes, then the sender goes away
                {
                    var f = Frame(++tid, WriteN(0, 4, 8, 8, r));
                    conn.Send(f[..r.Next(1, f.Length)]);
                    conn.Dispose(); conn = null;
                    continue;
                }
            }
            conn.Send(Frame(++tid, pdu, unit: (byte)r.Next(256)));
            var reply = conn.Reply();
            Assert.True(reply is not null, $"frame {i} ({kind}) closed the connection instead of an exception");
            Assert.Equal(tid, reply!.Value.Tid);
            AssertException(reply.Value.Pdu, expectFc, expectCode);

            if (i % 50 == 0)   // the PLC keeps being served throughout
            {
                input[2] = (byte)i;
                Assert.Equal(16, plc.Write(input));
                plcWrites++;
                Assert.Equal(input, server.ReadInput());
            }
        }
        conn?.Dispose();

        before[1] = (ushort)(input[2] | input[3] << 8);   // register 1 holds what the PLC itself last wrote
        Assert.Equal(before, Dump(plc));
        Until(() => Volatile.Read(ref changes) == plcWrites, 1000, "a bad frame raised InputChanged");
        Assert.Equal(plcWrites, Volatile.Read(ref changes));
        Assert.Equal(0, server.Disconnects);   // no fuzzing connection ever wrote, so none counts as a PLC loss
        Assert.True(server.Connected);
        Assert.Equal(16, plc.Write(input));
    }

    [Fact]
    public void UnknownFunctionsAndBadRequestsGetExceptionsAndTheConnectionStays()
    {
        using var server = Server();
        using var c = new Raw(server.Port);
        ushort tid = 100;
        foreach (int fc in new[] { 1, 2, 5, 15, 22, 23, 43, 0x7F, 0x83, 0xFF })
        {
            c.Send(Frame(++tid, new byte[] { (byte)fc, 0, 0, 0, 1 }));
            var r = c.Reply();
            Assert.NotNull(r);
            Assert.Equal(tid, r!.Value.Tid);
            AssertException(r.Value.Pdu, fc, 1);
        }
        (byte[] Pdu, int Code)[] bad =
        {
            (Read(3, 0, 0), 3), (Read(3, 0, 126), 3), (Read(4, 90, 10), 2),            // spans the diagnostics block's end
            (Read(3, 96, 1), 2), (Read(3, 0xFFFF, 1), 2), (new byte[] { 3, 0, 0, 0 }, 3),
            (new byte[] { 6, 0, 1, 0x12 }, 3), (new byte[] { 6, 0, 1, 0x12, 0x34, 0 }, 3),   // a write one byte short, one byte long
            (WriteN(0, 2, 3, 4, new Random(1)), 3), (WriteN(0, 2, 4, 5, new Random(1)), 3), (WriteN(0, 124, 248, 0, new Random(1)), 3),
            (WriteN(30, 4, 8, 8, new Random(1)), 2),                                        // spans the input block's end
        };
        foreach (var (pdu, code) in bad)
        {
            c.Send(Frame(++tid, pdu));
            var r = c.Reply();
            Assert.NotNull(r);
            AssertException(r!.Value.Pdu, pdu[0], code);
        }
        Assert.Equal(new byte[PlcInput.Size], server.ReadInput());
        Assert.Equal(new byte[LineDiagnostics.Size], server.ReadDiagnostics());

        c.Send(Frame(++tid, Read(3, 0, 2)));   // still served
        Assert.Equal(3, c.Reply()!.Value.Pdu[0]);
    }

    [Fact]
    public void WritesToTheVisionBlocksAreRefused()
    {
        using var server = Server();
        var outBefore = server.ReadOutput();
        using var plc = new PlcTestsAccess.Client(server.Port);
        var settingsBefore = plc.Read(ModbusServer.SetStart, ModbusServer.SetCount);
        var camBefore = plc.Read(ModbusServer.CamOutStart, ModbusServer.CamOutCount);
        foreach (var (start, count) in VisionAreas)
        {
            Assert.Equal(16 | 0x80, plc.WriteAt(start, new byte[2 * Math.Min(count, 123)]));
            Assert.Equal(16 | 0x80, plc.WriteAt(start + count - 1, new byte[2]));
        }
        Assert.Equal(outBefore, server.ReadOutput());
        Assert.Equal(settingsBefore, plc.Read(ModbusServer.SetStart, ModbusServer.SetCount));
        Assert.Equal(camBefore, plc.Read(ModbusServer.CamOutStart, ModbusServer.CamOutCount));
        Assert.False(server.Connected);   // refused writes do not make a connection the PLC
    }

    [Fact]
    public void NonModbusFramesCloseOnlyTheirConnection()
    {
        using var server = Server();
        using var plc = new PlcTestsAccess.Client(server.Port);
        Assert.Equal(16, plc.Write(new byte[PlcInput.Size]));
        foreach (var f in new[]
        {
            Frame(1, Write1(0, 0xBEEF), protocol: 1), Frame(2, Write1(0, 0xBEEF), length: 0),
            Frame(3, Write1(0, 0xBEEF), length: 1), Frame(4, Write1(0, 0xBEEF), length: 255),
            "GET / HTTP/1.1\r\nHost: plc\r\n\r\n"u8.ToArray(), new byte[] { 0x16, 0x03, 0x01, 0x02, 0x00, 0x01, 0x00 },   // a browser, a TLS hello
        })
        {
            using var c = new Raw(server.Port);
            c.Send(f);
            Assert.True(c.Closed(), "the server kept a connection that is not speaking Modbus TCP");
        }
        Assert.Equal(new byte[PlcInput.Size], server.ReadInput());
        Assert.Equal(16, plc.Write(new byte[PlcInput.Size]));
        Assert.Equal(0, server.Disconnects);
    }

    [Fact]
    public void SlowSendersAndPipelinedFramesAreServed()
    {
        using var server = Server(new ModbusServerOptions { FrameTimeoutMs = 2000 });
        using var c = new Raw(server.Port);
        var f = Frame(7, Write1(1, 0x1234));
        foreach (var b in f) { c.Send(new[] { b }); Thread.Sleep(15); }   // one byte at a time
        var r = c.Reply();
        Assert.NotNull(r);
        Assert.Equal(7, r!.Value.Tid);
        Assert.Equal(6, r.Value.Pdu[0]);
        Assert.Equal(new byte[] { 0x34, 0x12 }, server.ReadInput()[2..4]);

        c.Send(Frame(8, Read(3, 0, 2)).Concat(Frame(9, Read(4, 1, 1))).Concat(Frame(10, new byte[] { 0x2B, 0x0E, 1, 0 })).ToArray());
        Assert.Equal(8, c.Reply()!.Value.Tid);
        Assert.Equal(9, c.Reply()!.Value.Tid);
        var last = c.Reply()!.Value;
        Assert.Equal(10, last.Tid);
        AssertException(last.Pdu, 0x2B, 1);
    }

    [Fact]
    public void APartialFrameTimesOutAndWritesNothing()
    {
        using var server = Server(new ModbusServerOptions { FrameTimeoutMs = 300 });
        using var plc = new PlcTestsAccess.Client(server.Port);
        Assert.Equal(16, plc.Write(new byte[PlcInput.Size]));
        using var c = new Raw(server.Port);
        var f = Frame(1, WriteN(0, 4, 8, 8, new Random(3)));
        c.Send(f[..(f.Length - 1)]);              // the last byte never comes
        Assert.Equal(16, plc.Write(new byte[PlcInput.Size]));   // the PLC is served meanwhile
        Assert.True(c.Closed(3000), "a partial frame was held open past the frame time");
        Assert.Equal(new byte[PlcInput.Size], server.ReadInput());
        Assert.Equal(0, server.Disconnects);
    }

    [Fact]
    public void AClientThatNeverSendsIsClosedAfterTheIdleTime()
    {
        using var server = Server(new ModbusServerOptions { IdleTimeoutMs = 300 });
        using var c = new Raw(server.Port);
        Until(() => server.Connections == 1, 2000, "the server did not take the connection");
        Assert.False(server.Connected);         // a connection that has not written is not the PLC
        Assert.True(c.Closed(3000), "an idle connection was not closed");
        Until(() => server.Connections == 0, 2000, "the idle connection is still counted");
        Assert.Equal(0, server.Disconnects);
    }

    [Fact]
    public void ManyClientsAndScansNeverPushThePlcOut()
    {
        using var server = Server(new ModbusServerOptions { MaxConnections = 4 });
        using var plc = new PlcTestsAccess.Client(server.Port);
        Assert.Equal(16, plc.Write(new byte[PlcInput.Size]));
        Assert.True(server.Connected);
        var scans = new List<Raw>();
        try
        {
            for (int i = 0; i < 40; i++)   // a port scan, HMIs and laptops that connect and never send
            {
                scans.Add(new Raw(server.Port));
                Thread.Sleep(2);
                Assert.True(server.Connections <= 4, $"{server.Connections} connections open with a cap of 4");
            }
            for (int i = 0; i < 10; i++) Assert.Equal(16, plc.Write(new byte[PlcInput.Size]));   // the PLC, connected first, is still served
            Assert.True(server.Connected);
            Assert.Equal(0, server.Disconnects);

            // the newest client gets in: the cap closes the oldest that never wrote, not the newcomer
            using var hmi = new PlcTestsAccess.Client(server.Port);
            Assert.Equal(ModbusServer.InCount * 2, hmi.Read(0, ModbusServer.InCount).Length);
            Assert.True(scans[0].Closed(2000), "the oldest idle connection was not the one closed at the cap");
            Assert.False(scans[^1].Closed(200), "the cap closed the newest connection instead of the oldest idle one");
        }
        finally { scans.ForEach(s => s.Dispose()); }
    }

    [Fact]
    public void AReconnectingPlcGetsInWhenEverySlotIsTaken()
    {
        using var server = Server(new ModbusServerOptions { MaxConnections = 3 });
        var old = new PlcTestsAccess.Client(server.Port);
        Assert.Equal(16, old.Write(new byte[PlcInput.Size]));   // a PLC whose cable is about to be pulled: its socket lingers
        using var a = new Raw(server.Port);
        using var b = new Raw(server.Port);
        Until(() => server.Connections == 3, 2000, "the server did not take the connections");
        using var plc = new PlcTestsAccess.Client(server.Port);
        Assert.Equal(16, plc.Write(new byte[PlcInput.Size]));
        Assert.True(server.Connections <= 3);
        old.Dispose();
    }

    [Fact]
    public void AClientThatNeverReadsItsRepliesIsClosedAndThePlcIsServed()
    {
        using var server = Server(new ModbusServerOptions { FrameTimeoutMs = 500 });
        using var plc = new PlcTestsAccess.Client(server.Port);
        Assert.Equal(16, plc.Write(new byte[PlcInput.Size]));
        using var hog = new Raw(server.Port);
        var req = Frame(1, Read(3, ModbusServer.OutStart, 125));
        var burst = Enumerable.Repeat(req, 2000).SelectMany(x => x).ToArray();   // 2000 requests whose 520 KB of replies are never read
        var sender = Task.Run(() =>
        {
            hog.S.WriteTimeout = 5000;
            try { for (int i = 0; i < 200; i++) hog.S.Write(burst); } catch (IOException) { /* closed by the server */ }
        });
        for (int i = 0; i < 50; i++) { Assert.Equal(16, plc.Write(new byte[PlcInput.Size])); Thread.Sleep(10); }
        Assert.True(sender.Wait(15000), "the server never closed the client that stopped reading");
        Until(() => server.Connections == 1, 5000, "the stalled connection is still open");
        Assert.Equal(16, plc.Write(new byte[PlcInput.Size]));
        Assert.Equal(0, server.Disconnects);
    }

    [Fact]
    public void FastPollingReadersAreServedAndNeverCountAsAPlcLoss()
    {
        using var server = Server();
        using var plc = new PlcTestsAccess.Client(server.Port);
        var input = new byte[PlcInput.Size];
        Assert.Equal(16, plc.Write(input));
        var expectedOut = server.ReadOutput();
        var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            using var hmi = new PlcTestsAccess.Client(server.Port);
            for (int i = 0; i < 1500; i++)
            {
                var o = hmi.Read(ModbusServer.OutStart, 125);
                Assert.Equal(expectedOut[..250], o);
            }
        })).ToArray();
        ushort hb = 0;
        while (!readers.All(t => t.IsCompleted))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(2), ++hb);
            Assert.Equal(16, plc.Write(input));
            Assert.Equal(input, server.ReadInput());
        }
        Task.WaitAll(readers);
        Assert.Equal(0, server.Disconnects);   // six readers came and went
        Assert.True(server.Connected);
    }

    [Fact]
    public void TheRuntimeStaysReadyThroughAStormOnThePlcNetwork()
    {
        var server = new ModbusServer(0, IPAddress.Loopback);
        using var rt = new PlcRuntime(server, new PlcTestsAccess.Host(), new PlcOptions { WatchdogMs = 400 });
        using var plc = new PlcTestsAccess.Client(server.Port);
        var input = new PlcBlock(PlcInput.Size, false);
        input.SetBit(0, 3, true);   // RunEnable
        ushort hb = 0;
        void Beat() { input.SetU16(2, ++hb); Assert.Equal(16, plc.Write(input.Bytes)); }
        PlcOutput Output()
        {
            var raw = new byte[PlcOutput.Size];
            plc.Read(ModbusServer.OutStart, 125).CopyTo(raw, 0);
            plc.Read(ModbusServer.OutStart + 125, 3).CopyTo(raw, 250);
            var b = new PlcBlock(PlcOutput.Size, false);
            raw.CopyTo(b.Bytes, 0);
            return new PlcOutput(b);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!Output().Ready) { Beat(); Thread.Sleep(20); Assert.True(sw.ElapsedMilliseconds < 3000, "the runtime never became ready"); }

        using var stop = new CancellationTokenSource();
        var storm = Task.Run(() =>
        {
            var r = new Random(7);
            while (!stop.IsCancellationRequested)
                try
                {
                    using var c = new Raw(server.Port);
                    var junk = new byte[r.Next(1, 100)];
                    r.NextBytes(junk);
                    c.Send(r.Next(3) switch
                    {
                        0 => junk,
                        1 => Frame(1, Write1(ModbusServer.OutStart, 0)),     // a write to the output block
                        _ => Frame(2, Read(3, 0, 32)),                       // an HMI read, then gone
                    });
                    c.Reply(200);
                }
                catch (IOException) { }
                catch (SocketException) { }
        });
        sw.Restart();
        while (sw.ElapsedMilliseconds < 1500)
        {
            Beat();
            var o = Output();
            Assert.True(o.Ready, "the line lost Ready during the storm");
            Assert.Equal(0, o.ErrorCode);
            Thread.Sleep(20);
        }
        stop.Cancel();
        storm.Wait(5000);
        Assert.True(rt.Connected);
        Assert.Equal(0, server.Disconnects);
    }
}
