using Grpc.Core;
using VisionCore;
using VisionStudio.Remote;
using VisionStudio.Remote.V1;

// soakctl mkjob <jobsDir> <cam1Folder> <cam2Folder> <budgetMs>
//   the soak job SOAK2CAM: two simulated cameras, each with one code tool (QR, at least one code)
// soakctl local <jobsDir> <job> <models> <camera> <folder>
//   the job's tools of one camera on every image of a folder, with a fresh reader: the reference verdicts
// soakctl <start|stop|status|counters> <socket> [user pin] [job]
// soakctl watch <socket> <user> <pin> <out.csv>
//   one line per result until the runtime stops
var cmd = args[0];
if (cmd == "mkjob")
{
    var budget = int.Parse(args[4]);
    new JobStore(args[1]).Save(new Job
    {
        Name = "SOAK2CAM",
        Number = 230,
        Cameras = [new CameraConfig { Name = "Cam1", Driver = "sim", TestFolder = args[2] }, new CameraConfig { Name = "Cam2", Driver = "sim", TestFolder = args[3] }],
        Tools =
        [
            new CodeToolConfig { Name = "QR1", Camera = 0, Format = BarcodeReading.CodeFormat.QrCode, ExpectedCount = 0, TimeBudgetMs = budget },
            new CodeToolConfig { Name = "QR2", Camera = 1, Format = BarcodeReading.CodeFormat.QrCode, ExpectedCount = 0, TimeBudgetMs = budget },
        ],
    }, "soak");
    Console.WriteLine("job saved");
    return 0;
}
if (cmd == "crop")
{
    // soakctl crop <in> <out.bmp> <width> <height>: the centre of a frame as an 8-bit grey BMP,
    // the size of a line camera (the sample case photos are 20 MP; a 225/min camera is 2-3 MP)
    var src = GrayImage.Load(args[1]);
    int w = Math.Min(int.Parse(args[3]), src.Width), h = Math.Min(int.Parse(args[4]), src.Height);
    int x0 = (src.Width - w) / 2, y0 = (src.Height - h) / 2, stride = (w + 3) & ~3;
    using var o = new BinaryWriter(File.Create(args[2]));
    o.Write((byte)'B'); o.Write((byte)'M'); o.Write(54 + 1024 + stride * h); o.Write(0); o.Write(54 + 1024);
    o.Write(40); o.Write(w); o.Write(h); o.Write((short)1); o.Write((short)8); o.Write(0); o.Write(stride * h); o.Write(2835); o.Write(2835); o.Write(256); o.Write(0);
    for (int i = 0; i < 256; i++) o.Write(i * 0x010101);
    var row = new byte[stride];
    for (int y = h - 1; y >= 0; y--)   // bottom-up
    {
        Array.Copy(src.Pixels, (y0 + y) * src.Stride + x0, row, 0, w);
        o.Write(row);
    }
    return 0;
}
if (cmd == "loop")
{
    // soakctl loop <jobsDir> <job> <models> <cam1Folder> <cam2Folder> <seconds> <out.csv>
    //   the job's two cameras on two threads, frames loaded from file per part as the simulator does,
    //   with no runtime and no PLC: separates the engine's memory from the runtime's. Every 30 s a row;
    //   then 60 s idle, so vmmap and leaks can look at the process.
    var job = new JobStore(args[1]).Load(args[2])!;
    var secs = int.Parse(args[6]);
    var until = DateTime.UtcNow.AddSeconds(secs);
    long parts = 0;
    var workers = Enumerable.Range(0, 2).Select(cam => new Thread(() =>
    {
        using var runner = new JobRunner(job, new ModelPaths(args[3]));
        var files = FolderSource.List(args[4 + cam]);
        for (int i = 0; DateTime.UtcNow < until; i++)
        {
            runner.Run(cam, GrayImage.Load(files[i % files.Count]), new BatchFields());
            Interlocked.Increment(ref parts);
        }
    })).ToList();
    workers.ForEach(t => t.Start());
    using var w = new StreamWriter(args[7]) { AutoFlush = true };
    w.WriteLine("t_s,parts,working_set_mb,managed_heap_mb,gc_committed_mb,gen0,gen1,gen2");
    var proc = System.Diagnostics.Process.GetCurrentProcess();
    var t0 = DateTime.UtcNow;
    void Row()
    {
        proc.Refresh();
        var gc = GC.GetGCMemoryInfo();
        w.WriteLine($"{(DateTime.UtcNow - t0).TotalSeconds:0},{Interlocked.Read(ref parts)},{proc.WorkingSet64 / 1048576.0:0.0},{GC.GetTotalMemory(false) / 1048576.0:0.0}," +
                    $"{gc.TotalCommittedBytes / 1048576.0:0.0},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}");
    }
    while (workers.Any(t => t.IsAlive)) { Thread.Sleep(30000); Row(); }
    Console.WriteLine("idle");
    Thread.Sleep(60000);
    Row();
    return 0;
}
if (cmd == "local")
{
    var job = new JobStore(args[1]).Load(args[2])!;
    using var runner = new JobRunner(job, new ModelPaths(args[3]));
    var cam = int.Parse(args[4]);
    foreach (var f in FolderSource.List(args[5]))
    {
        var r = runner.Run(cam, GrayImage.Load(f), new BatchFields());
        Console.WriteLine($"{Path.GetFileName(f)},{r.Pass},{r.TotalMs:0.0}," + string.Join(" ", r.Tools.Select(t => $"{t.ToolName}:{(t.Pass ? 1 : 0)}:{t.Summary.Replace(",", ";")}")));
    }
    return 0;
}
using var ch = RuntimeEndpoint.Parse(args[1]).CreateChannel();
var rt = new VisionRuntime.VisionRuntimeClient(ch);
Metadata? auth = null;
if (args.Length >= 4)
{
    var s = await new Session.SessionClient(ch).SignInAsync(new SignInRequest { User = args[2], Pin = args[3], Client = ClientKind.Studio });
    if (!s.Ok) { Console.WriteLine("signin failed: " + s.ErrorText); return 1; }
    auth = new Metadata { { "authorization", "Bearer " + s.Token } };
}
switch (cmd)
{
    case "start":
        var r = await rt.StartAsync(new StartRequest { Job = args.Length > 4 ? args[4] : "", Source = Source.Camera }, auth);
        Console.WriteLine($"start ok={r.Ok} code={r.ErrorCode} {r.ErrorText}");
        return r.Ok ? 0 : 1;
    case "stop":
        var x = await rt.StopAsync(new StopRequest(), auth);
        Console.WriteLine($"stop ok={x.Ok} code={x.ErrorCode} {x.ErrorText}");
        break;
    case "counters":
        var c = await rt.GetCountersAsync(new GetCountersRequest(), auth);
        Console.WriteLine($"total={c.Total} ok={c.Ok} ng={c.Ng} median={c.MedianCycleMs} max={c.MaxCycleMs} reasons={string.Join(";", c.NgReasons.Select(kv => kv.Key + "=" + kv.Value))}");
        break;
    case "watch":
        using (var w = new StreamWriter(args[4]) { AutoFlush = true })
        {
            w.WriteLine("t,inspection,pass,cycle_ms,tools");
            var call = rt.StreamResults(new StreamResultsRequest(), auth);
            try
            {
                while (await call.ResponseStream.MoveNext(CancellationToken.None))
                {
                    var res = call.ResponseStream.Current;
                    w.WriteLine($"{DateTime.Now:HH:mm:ss.fff},{res.InspectionId},{res.Pass},{res.CycleMs:0.0},{string.Join(" ", res.Tools.Select(t => $"{t.Name}:{(t.Pass ? 1 : 0)}:{t.TimeMs:0.0}"))}");
                }
            }
            catch (RpcException) { }
        }
        break;
    case "status":
        // one line the sampler reads: key=value, then the active alarms
        var st = await rt.GetStatusAsync(new GetStatusRequest());
        var h = st.Health;
        Console.WriteLine($"state={st.State} job={st.ActiveJob} plc_connected={st.PlcConnected} insp={st.Inspections} ng={st.Ng} err={st.ErrorCode} " +
                          $"pressure={h?.Pressure} mem_used_pct={h?.MemoryUsedPct:0.0} restart_recommended={h?.RestartRecommended} frames_missed={h?.FramesMissed} " +
                          $"alarms={st.Alarms.Count}");
        foreach (var a in st.Alarms) Console.WriteLine($"  alarm {a.Level} {a.Code}: {a.Text}");
        break;
}
return 0;
