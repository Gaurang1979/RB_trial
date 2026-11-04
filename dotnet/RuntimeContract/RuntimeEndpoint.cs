using System.IO.Pipes;
using System.Net.Sockets;
using Grpc.Net.Client;

namespace VisionStudio.Remote;

/// <summary>
/// Where the runtime listens on this PC: a named pipe on Windows, a Unix socket elsewhere
/// (ARCHITECTURE.md sections 2.1 and 2.4). No TCP port is opened for local clients.
/// </summary>
public sealed record RuntimeEndpoint(string? PipeName, string? SocketPath)
{
    public const string DefaultPipeName = "vs-runtime";
    public const string DefaultSocketName = "vs-runtime.sock";

    /// <summary>Set to a pipe name ("pipe:vs-runtime") or a socket path to override the default.</summary>
    public const string EnvironmentVariable = "VS_RUNTIME_ENDPOINT";

    public static RuntimeEndpoint Default()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env)) return Parse(env);
        return OperatingSystem.IsWindows()
            ? new RuntimeEndpoint(DefaultPipeName, null)
            : new RuntimeEndpoint(null, Path.Combine(Path.GetTempPath(), DefaultSocketName));
    }

    /// <summary>"pipe:name" for a named pipe, anything else is a Unix socket path.</summary>
    public static RuntimeEndpoint Parse(string text) =>
        text.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase)
            ? new RuntimeEndpoint(text[5..], null)
            : new RuntimeEndpoint(null, text);

    public override string ToString() => PipeName is not null ? $"pipe:{PipeName}" : SocketPath!;

    /// <summary>A gRPC channel to the runtime. The connection is opened on the first call and
    /// re-opened by the next call after the runtime restarts, so a client only has to retry.</summary>
    public GrpcChannel CreateChannel()
    {
        var handler = new SocketsHttpHandler { ConnectCallback = ConnectAsync };
        // the host name is not used for a pipe or socket, only the scheme (h2c)
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = 64 << 20,   // full-resolution images through GetImage
        });
    }

    async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext _, CancellationToken ct)
    {
        if (PipeName is not null)
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(ct).ConfigureAwait(false); return pipe; }
            catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath!), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
