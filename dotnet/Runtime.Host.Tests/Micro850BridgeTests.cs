using System.Net;
using System.Net.Sockets;
using VisionSystem.Runtime;
using Xunit;

/// <summary>Micro850 trial: frames of four PLC-fired cameras put together into products, the Modbus TCP client,
/// and the camera intake when the PLC fires the camera.</summary>
public sealed class Micro850BridgeTests
{
    // 225 products/min: one every 267 ms; cameras 1 and 2 on DO_00, 3 on DO_09 a little later, 4 on P1_DO_01
    static readonly double[] Delays = [0, 0, 5, 120];
    const double Pitch = 266.7;

    static ProductGrouper Grouper() => new(4, Delays, toleranceMs: 40);

    [Fact]
    public void Frames_of_each_product_go_together()
    {
        var g = Grouper();
        var products = new List<ProductGrouper.Product>();
        var rnd = new Random(7);
        for (var n = 0; n < 50; n++)
        {
            var t0 = 1000 + n * Pitch;
            // arrival jitter of each camera (transfer, PC) up to ±15 ms, cameras in any order
            foreach (var c in new[] { 3, 1, 0, 2 })
            {
                var p = g.Arrive(c, t0 + Delays[c] + rnd.Next(-15, 16));
                p.Ok[c] = true;
                if (!products.Contains(p)) products.Add(p);
            }
        }
        Assert.Equal(50, products.Count);
        Assert.All(products, p => { Assert.True(p.Complete); Assert.True(p.Pass); Assert.False(p.Ambiguous); });
    }

    [Fact]
    public void A_missing_camera_frame_never_passes()
    {
        var g = Grouper();
        var p = g.Arrive(0, 1000);
        p.Ok[0] = true;
        g.Arrive(1, 1002).Ok[1] = true;
        g.Arrive(2, 1006).Ok[2] = true;
        // camera 4 never sends its frame: not due before the deadline, then due and not complete
        Assert.Empty(g.Due(1150, 0, 200));
        var due = Assert.Single(g.Due(1201, 0, 200));
        Assert.Same(p, due);
        Assert.False(due.Complete && due.Pass);
    }

    [Fact]
    public void A_second_frame_of_one_camera_makes_both_products_fail()
    {
        var g = Grouper();
        var a = g.Arrive(0, 1000);
        // the camera fired twice for one product (trigger bounce): neither product can be trusted
        var b = g.Arrive(0, 1010);
        Assert.NotSame(a, b);
        Assert.True(a.Ambiguous);
        Assert.True(b.Ambiguous);
        Assert.False(a.Pass);
    }

    [Fact]
    public void A_complete_product_is_due_at_once_but_not_before_the_window_opens()
    {
        var g = Grouper();
        foreach (var c in Enumerable.Range(0, 4)) g.Arrive(c, 1000 + Delays[c]).Ok[c] = true;
        Assert.Empty(g.Due(1125, notBeforeMs: 150, deadlineMs: 230));
        var p = Assert.Single(g.Due(1150, notBeforeMs: 150, deadlineMs: 230));
        Assert.True(p.Pass);
    }

    [Fact]
    public void Two_camera_job_completes_with_two_results()
    {
        var g = Grouper();
        g.Cameras = 2;
        g.Arrive(0, 1000).Ok[0] = true;
        g.Arrive(1, 1001).Ok[1] = false;
        var p = Assert.Single(g.Due(1002, 0, 200));
        Assert.True(p.Complete);
        Assert.False(p.Pass);
    }

    [Fact]
    public async Task Modbus_client_reads_and_writes_holding_registers()
    {
        var regs = new ushort[50];
        regs[25] = 1234;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[260];
            for (var k = 0; k < 2; k++)
            {
                var n = 0;
                while (n < 12) n += await s.ReadAsync(buf.AsMemory(n, 12 - n));
                int fc = buf[7], addr = buf[8] << 8 | buf[9], val = buf[10] << 8 | buf[11];
                byte[] pdu;
                if (fc == 3)
                {
                    pdu = new byte[2 + 2 * val];
                    pdu[0] = 3; pdu[1] = (byte)(2 * val);
                    for (var i = 0; i < val; i++) { pdu[2 + 2 * i] = (byte)(regs[addr + i] >> 8); pdu[3 + 2 * i] = (byte)regs[addr + i]; }
                }
                else { regs[addr] = (ushort)val; pdu = buf[7..12]; }
                var head = new byte[] { buf[0], buf[1], 0, 0, (byte)((pdu.Length + 1) >> 8), (byte)(pdu.Length + 1), buf[6] };
                await s.WriteAsync(head.Concat(pdu).ToArray());
            }
        });
        using var m = new ModbusTcpClient("127.0.0.1", port, 1, 1000);
        m.Connect();
        Assert.Equal(new ushort[] { 1234, 0 }, m.Read(25, 2));
        m.Write(Micro850Bridge.RegPass, 1);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, regs[3]);
    }

    sealed class FiredSource : ITriggeredSource
    {
        public event Action<ulong, int, int, CopyFrame>? Frame;
        public int Triggers;
        public bool Hardware => true;
        public void Trigger() => Triggers++;
        public void Send(ulong n, byte v) => Frame?.Invoke(n, 4, 2, into => into.Fill(v));
        public void Dispose() { }
    }

    [Fact]
    public void Fired_camera_hands_over_frames_in_order_and_skips_lost_numbers()
    {
        var hw = new HardwareFrames();
        var seen = new List<ulong>();
        var opened = 0;
        hw.Arrived += (name, n, _) => { Assert.Equal("Camera 1", name); seen.Add(n); };
        hw.Opened += _ => opened++;
        var src = new FiredSource();
        using var cam = new IntakeCamera(src, "Camera 1", buffers: 4, hardware: hw);
        Assert.Equal(1, opened);
        src.Send(1, 10);
        src.Send(2, 20);
        src.Send(4, 40);   // frame 3 never reached the PC
        Assert.Equal(new ulong[] { 1, 2, 4 }, seen);
        // a frame waiting in the intake is this product's: nothing is thrown away as stale and nothing is triggered
        Assert.Equal(10, cam.Grab(100)!.Pixels[0]);
        Assert.Equal(20, cam.Grab(100)!.Pixels[0]);
        Assert.Equal(40, cam.Grab(100)!.Pixels[0]);
        Assert.Null(cam.Grab(50));
        Assert.Equal(0, src.Triggers);
    }
}
