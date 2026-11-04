using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace VisionStudio.Remote.Stub;

/// <summary>Hosts a <see cref="StubRuntime"/> on a local endpoint (named pipe or Unix socket),
/// as the real runtime service will.</summary>
public static class StubServer
{
    public static WebApplication Build(StubRuntime runtime, RuntimeEndpoint endpoint, string[]? args = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (endpoint.PipeName is not null)
                k.ListenNamedPipe(endpoint.PipeName, l => l.Protocols = HttpProtocols.Http2);
            else
            {
                if (File.Exists(endpoint.SocketPath)) File.Delete(endpoint.SocketPath);   // left over from a crash
                k.ListenUnixSocket(endpoint.SocketPath!, l => l.Protocols = HttpProtocols.Http2);
            }
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(runtime);
        var app = builder.Build();
        app.MapGrpcService<StubSessionService>();
        app.MapGrpcService<StubRuntimeService>();
        return app;
    }
}
