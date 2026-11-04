using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using StationGuard;
using VisionCore;
using VisionStudio.Remote;
using VisionStudio.Remote.V1;
using VisionSystem.Runtime;

/// <summary>A real vs-runtime on a Unix socket and on a localhost TCP port (the "network"), with
/// its own jobs and data in a temporary folder. Jobs replay generated BMP frames with no codes, so
/// every part is NG; cameras are the simulator, playing the same frames through the intake.</summary>
public sealed class RuntimeFixture : IAsyncDisposable
{
    public const string OperatorPin = "4711";
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "vsrt-" + Guid.NewGuid().ToString("N")[..8]);
    public RuntimeEndpoint Endpoint { get; }
    public int Port { get; }
    public WebApplication App { get; private set; } = null!;
    public GrpcChannel Local { get; private set; } = null!;
    public GrpcChannel Network { get; private set; } = null!;
    public string FramesFolder => Path.Combine(Folder, "frames");

    RuntimeFixture()
    {
        Endpoint = new RuntimeEndpoint(null, Path.Combine(Path.GetTempPath(), $"vsrt-{Guid.NewGuid():N}"[..14] + ".sock"));
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public static async Task<RuntimeFixture> StartAsync(Action<RuntimeFixture>? prepare = null, params string[] extra)
    {
        var f = new RuntimeFixture();
        Directory.CreateDirectory(f.FramesFolder);
        for (var i = 0; i < 3; i++)
        {
            var px = new byte[320 * 240];
            Array.Fill(px, (byte)(100 + i * 20));
            BarcodeReading.BmpImage.SaveGray(Path.Combine(f.FramesFolder, $"frame{i}.bmp"), px, 320, 240);
        }
        var jobs = new JobStore(Path.Combine(f.Folder, "jobs"));
        jobs.Save(new Job
        {
            Name = "Labels",
            Number = 7,
            Cameras = [new CameraConfig { Name = "Top", TestFolder = f.FramesFolder }],
            Tools = [new CodeToolConfig { Name = "Read code", Format = BarcodeReading.CodeFormat.QrCode, ExpectedCount = 1 }],
        }, "test");
        jobs.Save(new Job { Name = "Empty", Number = 8, Cameras = [new CameraConfig { Name = "Top", TestFolder = f.FramesFolder }] }, "test");
        var access = new AccessStore(Path.Combine(f.Folder, "data"));
        access.SaveUser("op", "Line Operator", [Role.Operator], null, OperatorPin);
        access.SaveUser("eng", "Vision Engineer", [Role.Engineer], "secret-pass", null);
        prepare?.Invoke(f);
        string[] args =
        [
            $"--Runtime:Endpoint={f.Endpoint.SocketPath}",
            $"--Runtime:NetworkPort={f.Port}",
            "--Runtime:AllowInsecureNetwork=true",
            "--Runtime:Source=replay",
            $"--Runtime:SimulatorFolder={f.FramesFolder}",
            $"--Studio:JobsFolder={Path.Combine(f.Folder, "jobs")}",
            $"--Studio:DataFolder={Path.Combine(f.Folder, "data")}",
            $"--Studio:ModelsFolder={Path.Combine(AppContext.BaseDirectory, "models")}",
            "--Studio:LicenceRequired=false",
            "--Studio:FolderIntervalMs=30",
            "--Logging:LogLevel:Default=Warning",
            .. extra,
        ];
        // the development disk may be nearly full: the guard must never delete a test's images for it (TST-HYG-02)
        f.App = RuntimeHost.Build(args, f.Folder,
            g => g with { DiskHighFreePct = 0, DiskCriticalFreePct = 0 },
            i => i with { TargetFreeDiskPct = 0 });
        await f.App.StartAsync();
        f.Local = f.Endpoint.CreateChannel();
        f.Network = GrpcChannel.ForAddress($"http://127.0.0.1:{f.Port}");
        return f;
    }

    public VisionRuntime.VisionRuntimeClient Runtime(bool network = false) => new(network ? Network : Local);
    public Session.SessionClient Sessions(bool network = false) => new(network ? Network : Local);
    public static Metadata Bearer(string token) => new() { { "authorization", $"Bearer {token}" } };

    public async Task<Metadata> SignInOperator(bool network = false)
    {
        var r = await Sessions(network).SignInAsync(new SignInRequest { User = "op", Pin = OperatorPin, Client = ClientKind.Studio });
        if (!r.Ok) throw new InvalidOperationException(r.ErrorText);
        return Bearer(r.Token);
    }

    public string AddToken(string name, bool operate) => new AccessStore(Path.Combine(Folder, "data")).AddToken(name, operate);

    public async ValueTask DisposeAsync()
    {
        Local.Dispose();
        Network.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        try { File.Delete(Endpoint.SocketPath!); } catch (IOException) { }
        try { Directory.Delete(Folder, true); } catch (IOException) { }
    }
}
