using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace VisionCore.Plc;

/// <summary>
/// The vision system as a Modbus TCP server (PLC-INTERFACE.md section 2): the PLC writes the input
/// block to holding registers 0-31 and reads the output block from holding registers 100-227
/// (also readable as input registers), and may write the line diagnostics block to registers 40-79
/// (PLC-INTERFACE.md section 3.3), and the text block to registers 500-627 (section 3.7).
/// Register n holds block bytes 2n (low) and 2n+1 (high).
/// Function codes 3, 4, 6 and 16. A read is limited to 125 registers by the protocol, so the output
/// block takes two reads (100-224, 225-227); the runtime only changes it between handshakes.
/// Anything else on the PLC network (an HMI, a scanner, a port scan, a broken frame) is answered with a
/// Modbus exception or has its own connection closed, never writes a block, and never keeps the PLC out
/// (PLC-INTERFACE.md section 2.1): <see cref="ModbusServerOptions"/> caps connections and idle time.
/// </summary>
public sealed class ModbusServer : IPlcLink
{
    public const int InStart = 0, InCount = PlcInput.Size / 2, OutStart = 100, OutCount = PlcOutput.Size / 2;
    public const int DiagStart = 40, DiagCount = LineDiagnostics.Size / 2;
    public const int SetStart = 300, SetCount = LineSettings.Size / 2;
    public const int CamInStart = 32, CamInCount = PlcCameraChannels.InSize / 2;
    public const int CamOutStart = 228, CamOutCount = PlcCameraChannels.OutSize / 2;
    public const int PartStart = 400, PartCount = PlcPartLog.Size / 2;
    public const int TextStart = PlcTextBlock.StartRegister, TextCount = PlcTextBlock.Size / 2;

    private readonly object _lock = new();
    private readonly PlcBlock _in = new(PlcInput.Size, bigEndian: false);
    private readonly PlcBlock _out = new(PlcOutput.Size, bigEndian: false);
    private readonly PlcBlock _diag = new(LineDiagnostics.Size, bigEndian: false);
    private readonly PlcBlock _set = new(LineSettings.Size, bigEndian: false);
    private readonly PlcBlock _camIn = new(PlcCameraChannels.InSize, bigEndian: false);
    private readonly PlcBlock _camOut = new(PlcCameraChannels.OutSize, bigEndian: false);
    private readonly PlcBlock _parts = new(PlcPartLog.Size, bigEndian: false);
    private readonly PlcBlock _text = new(PlcTextBlock.Size, bigEndian: false);
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ModbusServerOptions _opt;
    private readonly List<Conn> _conns = new();   // under _conns' lock
    private int _writers;
    private int _disconnects;
    private int _disconnectsAtWrite;

    public ModbusServer(int port = 502, IPAddress? bind = null, ModbusServerOptions? options = null)
    {
        _opt = options ?? new ModbusServerOptions();
        _listener = new TcpListener(bind ?? IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop(_cts.Token);
    }

    public int Port { get; }
    public string Name => $"Modbus TCP server, port {Port}";
    /// <summary>A connection that wrote to the vision blocks (the PLC) is open; one that only reads
    /// (an HMI, a poll tool, a port scan) does not make the link look connected.</summary>
    public bool Connected => Volatile.Read(ref _writers) > 0;
    /// <summary>Open connections of any kind, at most <see cref="ModbusServerOptions.MaxConnections"/>.</summary>
    public int Connections { get { lock (_conns) return _conns.Count; } }
    public int Disconnects => Volatile.Read(ref _disconnects);
    public int DisconnectsAtInputWrite => Volatile.Read(ref _disconnectsAtWrite);
    public event Action? InputChanged;

    public byte[] ReadInput()
    {
        lock (_lock) return (byte[])_in.Bytes.Clone();
    }

    public void WriteOutput(byte[] block)
    {
        lock (_lock) Array.Copy(block, _out.Bytes, Math.Min(block.Length, _out.Bytes.Length));
    }

    public byte[] ReadOutput()
    {
        lock (_lock) return (byte[])_out.Bytes.Clone();
    }

    public bool BigEndian => false;

    public byte[] ReadCameraIn()
    {
        lock (_lock) return (byte[])_camIn.Bytes.Clone();
    }

    public void WriteCameraOut(byte[] block)
    {
        lock (_lock) Array.Copy(block, _camOut.Bytes, Math.Min(block.Length, _camOut.Bytes.Length));
    }

    public void WriteSettings(byte[] block)
    {
        lock (_lock) Array.Copy(block, _set.Bytes, Math.Min(block.Length, _set.Bytes.Length));
    }

    public byte[] ReadPartLog()
    {
        lock (_lock) return (byte[])_parts.Bytes.Clone();
    }

    public byte[] ReadDiagnostics()
    {
        lock (_lock) return (byte[])_diag.Bytes.Clone();
    }

    public byte[] ReadText()
    {
        lock (_lock) return (byte[])_text.Bytes.Clone();
    }

    private sealed class Conn(TcpClient client, CancellationToken server)
    {
        public readonly TcpClient Client = client;
        public readonly CancellationTokenSource Cts = CancellationTokenSource.CreateLinkedTokenSource(server);
        public long LastFrame = Environment.TickCount64;
        public bool Wrote;
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(token); }
            catch when (token.IsCancellationRequested) { return; }
            catch { await Task.Delay(50, CancellationToken.None); continue; }   // a client reset before accept, or no sockets left: keep listening
            _ = Serve(c, token);
        }
    }

    // at the cap, the newcomer is let in and the connection idle longest is closed, one that never wrote
    // first: a PLC reconnecting after a cable pull always gets in, and a flood of scans never pushes it out
    private void Admit(Conn conn)
    {
        Conn? victim = null;
        lock (_conns)
        {
            if (_conns.Count >= Math.Max(1, _opt.MaxConnections))
                victim = _conns.Where(c => !c.Wrote).MinBy(c => c.LastFrame) ?? _conns.MinBy(c => c.LastFrame);
            if (victim is not null) _conns.Remove(victim);
            _conns.Add(conn);
        }
        if (victim is null) return;
        try { victim.Cts.Cancel(); } catch (ObjectDisposedException) { /* it ended by itself meanwhile */ }
        victim.Client.Dispose();
    }

    private async Task Serve(TcpClient client, CancellationToken server)
    {
        var conn = new Conn(client, server);
        Admit(conn);
        var token = conn.Cts.Token;
        try
        {
            using var _ = client;
            client.NoDelay = true;
            var s = client.GetStream();
            var head = new byte[7];
            while (!token.IsCancellationRequested)
            {
                // a frame's first byte may take up to the idle time; the rest of it must follow within the frame time
                if (!await ReadExactly(s, head.AsMemory(0, 1), _opt.IdleTimeoutMs, token)) return;
                if (!await ReadExactly(s, head.AsMemory(1), _opt.FrameTimeoutMs, token)) return;
                int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
                // not Modbus TCP (protocol id 0), or a length no Modbus frame has: the stream cannot be trusted, close it
                if (head[2] != 0 || head[3] != 0 || len < 2 || len > 254) return;
                var pdu = new byte[len - 1];
                if (!await ReadExactly(s, pdu, _opt.FrameTimeoutMs, token)) return;
                var reply = Handle(pdu);
                if (pdu[0] is 6 or 16 && reply[0] == pdu[0] && !conn.Wrote) { conn.Wrote = true; Interlocked.Increment(ref _writers); }
                var frame = new byte[7 + reply.Length];
                head.AsSpan(0, 4).CopyTo(frame);                          // transaction, protocol
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(reply.Length + 1));
                frame[6] = head[6];                                        // unit
                reply.CopyTo(frame, 7);
                using (var w = CancellationTokenSource.CreateLinkedTokenSource(token))   // a client that never reads its replies
                {
                    w.CancelAfter(Math.Max(1, _opt.FrameTimeoutMs));
                    await s.WriteAsync(frame, w.Token);
                }
                Volatile.Write(ref conn.LastFrame, Environment.TickCount64);
            }
        }
        catch { /* client gone, timed out or closed for another */ }
        finally
        {
            lock (_conns) _conns.Remove(conn);
            conn.Cts.Dispose();
            if (conn.Wrote) { Interlocked.Increment(ref _disconnects); Interlocked.Decrement(ref _writers); }
        }
    }

    private static async Task<bool> ReadExactly(NetworkStream s, Memory<byte> buf, int timeoutMs, CancellationToken token)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(token);
        t.CancelAfter(Math.Max(1, timeoutMs));
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReadAsync(buf[got..], t.Token);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    private static byte[] Exception(byte fc, byte code) => new[] { (byte)(fc | 0x80), code };

    // which block a register range lies in (null = none, or it spans two blocks)
    private (PlcBlock Block, int Start)? Area(int a, int n)
    {
        if (a >= InStart && a + n <= InStart + InCount) return (_in, InStart);
        if (a >= DiagStart && a + n <= DiagStart + DiagCount) return (_diag, DiagStart);
        if (a >= OutStart && a + n <= OutStart + OutCount) return (_out, OutStart);
        if (a >= SetStart && a + n <= SetStart + SetCount) return (_set, SetStart);
        if (a >= CamInStart && a + n <= CamInStart + CamInCount) return (_camIn, CamInStart);
        if (a >= CamOutStart && a + n <= CamOutStart + CamOutCount) return (_camOut, CamOutStart);
        if (a >= PartStart && a + n <= PartStart + PartCount) return (_parts, PartStart);
        if (a >= TextStart && a + n <= TextStart + TextCount) return (_text, TextStart);
        return null;
    }

    private byte[] Handle(byte[] pdu)
    {
        byte fc = pdu[0];
        switch (fc)
        {
            case 3:
            case 4:
            {
                if (pdu.Length != 5) return Exception(fc, 3);
                int a = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1)), n = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                if (n < 1 || n > 125) return Exception(fc, 3);
                if (Area(a, n) is not { } area) return Exception(fc, 2);
                var r = new byte[2 + 2 * n];
                r[0] = fc;
                r[1] = (byte)(2 * n);
                lock (_lock)
                    for (int i = 0; i < n; i++)
                    {
                        int b = 2 * (a + i - area.Start);
                        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2 + 2 * i), (ushort)(area.Block.Bytes[b] | area.Block.Bytes[b + 1] << 8));
                    }
                return r;
            }
            case 6:
            {
                if (pdu.Length != 5) return Exception(fc, 3);                 // exact length: a frame with more or fewer bytes writes nothing
                int a = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
                if (Area(a, 1) is not { } area || area.Block == _out || area.Block == _set || area.Block == _camOut) return Exception(fc, 2);   // vision-written areas are read-only to the PLC
                ushort v = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                lock (_lock) { int b = 2 * (a - area.Start); area.Block.Bytes[b] = (byte)v; area.Block.Bytes[b + 1] = (byte)(v >> 8); }
                Volatile.Write(ref _disconnectsAtWrite, Volatile.Read(ref _disconnects));
                InputChanged?.Invoke();
                return pdu[..5];
            }
            case 16:
            {
                if (pdu.Length < 6) return Exception(fc, 3);
                int a = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1)), n = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                if (n < 1 || n > 123 || pdu.Length != 6 + 2 * n || pdu[5] != 2 * n) return Exception(fc, 3);
                if (Area(a, n) is not { } area || area.Block == _out || area.Block == _set || area.Block == _camOut) return Exception(fc, 2);
                lock (_lock)
                    for (int i = 0; i < n; i++)
                    {
                        ushort v = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + 2 * i));
                        int b = 2 * (a - area.Start + i);
                        area.Block.Bytes[b] = (byte)v;
                        area.Block.Bytes[b + 1] = (byte)(v >> 8);
                    }
                Volatile.Write(ref _disconnectsAtWrite, Volatile.Read(ref _disconnects));
                InputChanged?.Invoke();
                return pdu[..5];
            }
            default:
                return Exception(fc, 1);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

/// <summary>Limits of the Modbus TCP server against anything else on the PLC network
/// (PLC-INTERFACE.md section 2.1). The defaults suit a line: the PLC polls every 10-20 ms.</summary>
public sealed class ModbusServerOptions
{
    /// <summary>Open connections at most (the PLC, an HMI or two, a service laptop). At the cap the
    /// connection idle longest is closed, one that never wrote first, so the PLC always gets in.</summary>
    public int MaxConnections { get; init; } = 8;
    /// <summary>A connection that sends no frame for this long is closed (one that connects and never sends).</summary>
    public int IdleTimeoutMs { get; init; } = 60_000;
    /// <summary>A frame once started must arrive in full, and its reply be taken, within this time
    /// (a partial frame, a slow sender, a client that never reads its replies).</summary>
    public int FrameTimeoutMs { get; init; } = 2_000;
}

/// <summary>A PLC connection carrying the input and output blocks, whatever the protocol.</summary>
public interface IPlcLink : IDisposable
{
    string Name { get; }
    bool Connected { get; }
    /// <summary>PLC connections ended since the link started (connections that wrote; a reading HMI does
    /// not count). A part whose trigger came before one ended is NG (PLC-INTERFACE.md section 5.6),
    /// however briefly the link was down.</summary>
    int Disconnects => 0;
    /// <summary><see cref="Disconnects"/> as it was when the PLC's last write landed: the mark of a
    /// trigger, so a connection that wrote the trigger and ended before the runtime saw it still counts.</summary>
    int DisconnectsAtInputWrite => Disconnects;
    bool BigEndian { get; }
    /// <summary>Raised when the PLC wrote (server protocols); client protocols poll instead.</summary>
    event Action? InputChanged;
    byte[] ReadInput();
    void WriteOutput(byte[] block);
    /// <summary>The line diagnostics block (empty array when the protocol has none).</summary>
    byte[] ReadDiagnostics();
    /// <summary>The line settings block the PLC reads.</summary>
    void WriteSettings(byte[] block);
    /// <summary>Camera channels (section 3.5): per-camera triggers from the PLC, results to it.</summary>
    byte[] ReadCameraIn();
    void WriteCameraOut(byte[] block);
    /// <summary>Part log (section 3.6): a record per product leaving the line (empty when none).</summary>
    byte[] ReadPartLog();
    /// <summary>Text block (section 3.7): strings for expected texts (empty when the protocol has none).</summary>
    byte[] ReadText() => Array.Empty<byte>();
}
