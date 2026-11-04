using VisionStudio.Remote;
using VisionStudio.Remote.Stub;

// vs-runtime-stub [--endpoint <socket path | pipe:name>] [--frames <folder of .jpg>] [--interval-ms 700]
//                 [--start] [--drop-every-s <seconds>]
// While it runs, type "d" and Enter to drop every connection (the endpoint is gone for 3 s, as
// when the service restarts), so a client's reconnect can be tried by hand; --drop-every-s does
// the same on a timer. The made-up line keeps its state across drops. "a" raises a sample alarm and
// "w" a sample warning, both with "What do I do?" steps; "c" clears them.
string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var endpoint = Arg("--endpoint") is { } e ? RuntimeEndpoint.Parse(e) : RuntimeEndpoint.Default();
using var runtime = new StubRuntime(Arg("--frames"), seed: Environment.TickCount)
{
    Interval = TimeSpan.FromMilliseconds(int.TryParse(Arg("--interval-ms"), out var ms) ? ms : 700),
};
if (args.Contains("--start")) runtime.Start(new VisionStudio.Remote.V1.StartRequest());
var dropEvery = int.TryParse(Arg("--drop-every-s"), out var s) && s > 0 ? TimeSpan.FromSeconds(s) : Timeout.InfiniteTimeSpan;

using var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; quit.Cancel(); };
// kill (SIGTERM) too, when it runs in the background
using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; quit.Cancel(); });
var drop = new SemaphoreSlim(0);
_ = Task.Run(() =>
{
    while (Console.ReadLine() is { } line)
        switch (line.Trim())
        {
            case "d": drop.Release(); break;
            case "a": runtime.RaiseAlarm(StubRuntime.SampleAlarm()); break;
            case "w": runtime.RaiseAlarm(StubRuntime.SampleWarning()); break;
            case "c": runtime.ClearAlarms(); break;
        }
});

Console.WriteLine($"stub runtime on {endpoint}, {runtime.FrameCount} frames; users: operator (PIN 1234), supervisor, engineer, admin; plant tokens {StubRuntime.PlantOperateToken} / {StubRuntime.PlantReadToken}; type d to drop connections, a / w for an alarm / warning, c to clear");
while (!quit.IsCancellationRequested)
{
    var app = StubServer.Build(runtime, endpoint);
    await app.StartAsync(quit.Token);
    try { await drop.WaitAsync(dropEvery, quit.Token); }
    catch (OperationCanceledException) { }
    await app.StopAsync(TimeSpan.Zero);
    await app.DisposeAsync();
    if (quit.IsCancellationRequested) break;
    Console.WriteLine("connections dropped; back in 3 s");
    try { await Task.Delay(3000, quit.Token); } catch (OperationCanceledException) { }
}
