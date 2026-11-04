using Grpc.Core;
using VisionStudio.Remote.V1;

namespace VisionStudio.Remote.Stub;

/// <summary>Reads the session from the `authorization: Bearer` header and applies the access
/// rules, as the real runtime will.</summary>
static class Gate
{
    public static string? Token(ServerCallContext ctx)
    {
        var h = ctx.RequestHeaders.GetValue("authorization");
        return h is not null && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
    }

    /// <summary>Nobody signed in at the line PC's own screen: reads only. The stub listens only on
    /// the local endpoint; the real runtime gives this to local callers only.</summary>
    public static readonly StubRuntime.SessionInfo LocalAnonymous = new(ClientKind.Studio, null, false);

    public static StubRuntime.SessionInfo Session(StubRuntime rt, ServerCallContext ctx)
    {
        var token = Token(ctx);
        if (token is null) return LocalAnonymous;
        return rt.Find(token) ?? throw new RpcException(new Grpc.Core.Status(StatusCode.Unauthenticated, "Please sign in again."));
    }

    public static void Operate(StubRuntime rt, ServerCallContext ctx)
    {
        var s = Session(rt, ctx);
        if (ReferenceEquals(s, LocalAnonymous))
            throw new RpcException(new Grpc.Core.Status(StatusCode.Unauthenticated, "Sign in first."));
        if (!RuntimeAccess.MayOperate(s.Client, s.Roles, s.OperateToken))
            throw new RpcException(new Grpc.Core.Status(StatusCode.PermissionDenied, s.Client == ClientKind.Browser
                ? "The browser dashboard only shows the line. Open Vision Studio on the line PC to change this."
                : "You may not change this. Ask your supervisor."));
    }
}

public sealed class StubSessionService(StubRuntime rt) : V1.Session.SessionBase
{
    public override Task<SignInResponse> SignIn(SignInRequest request, ServerCallContext context) =>
        Task.FromResult(rt.SignIn(request));

    public override Task<SignOutResponse> SignOut(SignOutRequest request, ServerCallContext context)
    {
        rt.SignOut(Gate.Token(context));
        return Task.FromResult(new SignOutResponse());
    }

    public override Task<ListUsersResponse> ListUsers(ListUsersRequest request, ServerCallContext context) =>
        Task.FromResult(rt.ListUsers());   // the stub only listens locally

    public override Task<WhoAmIResponse> WhoAmI(WhoAmIRequest request, ServerCallContext context) =>
        Task.FromResult(rt.WhoAmI(Gate.Session(rt, context)));
}

public sealed class StubRuntimeService(StubRuntime rt) : VisionRuntime.VisionRuntimeBase
{
    public override Task<V1.Status> GetStatus(GetStatusRequest request, ServerCallContext context)
    {
        Gate.Session(rt, context);
        return Task.FromResult(rt.Status());
    }

    public override async Task WatchStatus(WatchStatusRequest request, IServerStreamWriter<V1.Status> responseStream, ServerCallContext context)
    {
        Gate.Session(rt, context);
        await Pump(rt.WatchStatus(context.CancellationToken), responseStream, context.CancellationToken);
    }

    public override Task<ListJobsResponse> ListJobs(ListJobsRequest request, ServerCallContext context)
    {
        Gate.Session(rt, context);
        return Task.FromResult(rt.ListJobs());
    }

    public override Task<ChangeJobResponse> ChangeJob(ChangeJobRequest request, ServerCallContext context)
    {
        Gate.Operate(rt, context);
        return Task.FromResult(rt.ChangeJob(request));
    }

    public override Task<StartResponse> Start(StartRequest request, ServerCallContext context)
    {
        Gate.Operate(rt, context);
        return Task.FromResult(rt.Start(request));
    }

    public override Task<StopResponse> Stop(StopRequest request, ServerCallContext context)
    {
        Gate.Operate(rt, context);
        return Task.FromResult(rt.Stop());
    }

    public override Task<TriggerResponse> Trigger(TriggerRequest request, ServerCallContext context)
    {
        Gate.Operate(rt, context);
        var r = rt.Step(request.PartId);
        return Task.FromResult(new TriggerResponse { TriggerId = r.TriggerId, Result = request.WaitForResult ? r : null });
    }

    public override Task<SetInputResponse> SetInput(SetInputRequest request, ServerCallContext context)
    {
        Gate.Operate(rt, context);
        return Task.FromResult(new SetInputResponse { Ok = true });
    }

    public override async Task StreamResults(StreamResultsRequest request, IServerStreamWriter<InspectionResult> responseStream, ServerCallContext context)
    {
        Gate.Session(rt, context);
        await foreach (var r in rt.WatchResults(context.CancellationToken).ReadAllAsync(context.CancellationToken))
            if (!request.NgOnly || !r.Pass) await responseStream.WriteAsync(r);
    }

    public override async Task WatchFrames(WatchFramesRequest request, IServerStreamWriter<LiveFrame> responseStream, ServerCallContext context)
    {
        Gate.Session(rt, context);
        var gap = TimeSpan.FromSeconds(1 / (request.MaxFps > 0 ? request.MaxFps : 5));
        var last = DateTime.MinValue;
        await foreach (var f in rt.WatchFrames(context.CancellationToken).ReadAllAsync(context.CancellationToken))
        {
            if (request.NgOnly && f.Pass) continue;
            if (DateTime.UtcNow - last < gap) continue;     // drop, never queue
            last = DateTime.UtcNow;
            await responseStream.WriteAsync(f);
        }
    }

    public override Task<Counters> GetCounters(GetCountersRequest request, ServerCallContext context)
    {
        Gate.Session(rt, context);
        return Task.FromResult(rt.Counters());
    }

    /// <summary>Kept inspection images and product pictures, as the runtime has them (the requested
    /// encoding is not converted); in chunks of at most 1 MB.</summary>
    public override async Task GetImage(GetImageRequest request, IServerStreamWriter<ImageChunk> responseStream, ServerCallContext context)
    {
        Gate.Session(rt, context);
        var image = rt.Image(request.ImageId)
            ?? throw new RpcException(new Grpc.Core.Status(StatusCode.NotFound, "This image is no longer kept."));
        const int chunk = 1 << 20;
        for (var at = 0; at < image.Data.Length; at += chunk)
        {
            await responseStream.WriteAsync(new ImageChunk
            {
                Data = Google.Protobuf.ByteString.CopyFrom(image.Data, at, Math.Min(chunk, image.Data.Length - at)),
                Width = image.Width,
                Height = image.Height,
                Format = image.Format,
            }, context.CancellationToken);
        }
    }

    static async Task Pump<T>(System.Threading.Channels.ChannelReader<T> reader, IServerStreamWriter<T> w, CancellationToken ct)
    {
        await foreach (var item in reader.ReadAllAsync(ct)) await w.WriteAsync(item, ct);
    }
}
