using Google.Protobuf;
using Grpc.Core;
using SkiaSharp;
using VisionStudio.Remote.V1;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Runtime;

public sealed class SessionService(Sessions sessions) : V1.Session.SessionBase
{
    public override Task<SignInResponse> SignIn(SignInRequest request, ServerCallContext context) =>
        Task.FromResult(sessions.SignIn(request, context));

    public override Task<SignOutResponse> SignOut(SignOutRequest request, ServerCallContext context)
    {
        sessions.SignOut(context);
        return Task.FromResult(new SignOutResponse());
    }

    public override Task<ListUsersResponse> ListUsers(ListUsersRequest request, ServerCallContext context) =>
        Task.FromResult(sessions.ListUsers(context));

    public override Task<WhoAmIResponse> WhoAmI(WhoAmIRequest request, ServerCallContext context) =>
        Task.FromResult(sessions.WhoAmI(sessions.Read(context)));
}

public sealed class RuntimeService(LineRuntime line, JobDesigner designer, Sessions sessions, ReviewDesk reviews, Learning learning,
    LearningSettingsStore settings, VisionStudio.Services.OkSampler okSamples, VisionReports.ResultLog audit)
    : VisionRuntime.VisionRuntimeBase
{
    const int ChunkBytes = 1 << 20;

    public override Task<V1.Status> GetStatus(GetStatusRequest request, ServerCallContext context)
    {
        sessions.Read(context);
        return Task.FromResult(line.Status());
    }

    public override async Task WatchStatus(WatchStatusRequest request, IServerStreamWriter<V1.Status> responseStream, ServerCallContext context)
    {
        sessions.Read(context);
        await foreach (var s in line.WatchStatus(context.CancellationToken).ReadAllAsync(context.CancellationToken))
            await responseStream.WriteAsync(s, context.CancellationToken);
    }

    public override Task<ListJobsResponse> ListJobs(ListJobsRequest request, ServerCallContext context)
    {
        sessions.Read(context);
        return Task.FromResult(line.ListJobs());
    }

    public override Task<ChangeJobResponse> ChangeJob(ChangeJobRequest request, ServerCallContext context)
    {
        var c = sessions.Operate(context);
        return Task.FromResult(line.ChangeJob(request, c.User?.Name ?? ""));
    }

    public override Task<StartResponse> Start(StartRequest request, ServerCallContext context)
    {
        var c = sessions.Operate(context);
        return Task.FromResult(line.Start(request, c.User?.Name ?? ""));
    }

    public override Task<StopResponse> Stop(StopRequest request, ServerCallContext context)
    {
        sessions.Operate(context);
        return Task.FromResult(line.Stop());
    }

    public override Task<TriggerResponse> Trigger(TriggerRequest request, ServerCallContext context)
    {
        sessions.Operate(context);
        return line.TriggerAsync(request, context.CancellationToken);
    }

    /// <summary>The answer at the line PC to a remote service request (REMOTE-SUPPORT.md 4b).</summary>
    public override Task<AnswerRemoteRequestResponse> AnswerRemoteRequest(AnswerRemoteRequestRequest request, ServerCallContext context) =>
        Task.FromResult(context.GetHttpContext().RequestServices.GetRequiredService<RemoteRequests>().Answer(request, context, sessions));

    /// <summary>A second person accepts the changeover at the line PC while the plant server cannot be reached: a
    /// Supervisor, QA or Admin, never the signed-in operator nor the one who made the change.</summary>

    public override Task<AcceptChangeoverResponse> AcceptChangeover(AcceptChangeoverRequest request, ServerCallContext context)
    {
        var c = sessions.Operate(context);
        var (second, refused) = sessions.SecondPerson(request, context);
        if (refused is not null) return Task.FromResult(refused);
        if (!second!.Roles.Any(r => r is Role.Supervisor or Role.Qa or Role.Admin))
            return Task.FromResult(new AcceptChangeoverResponse { ErrorCode = 403, ErrorText = "Only a supervisor, QA or admin may accept a changeover.", ErrorKey = "runtime.changeover.notApprover" });
        if (c.User is { } op && string.Equals(op.Name, second.Name, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new AcceptChangeoverResponse { ErrorCode = 403, ErrorText = "A second person must accept, not the one signed in.", ErrorKey = "runtime.changeover.samePerson" });
        return Task.FromResult(line.AcceptChangeoverAtLine(request.Code, second.Name, second.FullName));
    }

    public override Task<SetInputResponse> SetInput(SetInputRequest request, ServerCallContext context)
    {
        var c = sessions.Operate(context);
        return Task.FromResult(line.SetInput(request, c.User?.Name ?? ""));
    }

    public override async Task StreamResults(StreamResultsRequest request, IServerStreamWriter<InspectionResult> responseStream, ServerCallContext context)
    {
        sessions.Read(context);
        await foreach (var r in line.WatchResults(context.CancellationToken).ReadAllAsync(context.CancellationToken))
        {
            if (request.NgOnly && r.Pass) continue;
            var send = r;
            if (!request.IncludeValues && r.Outputs.Count > 0) { send = r.Clone(); send.Outputs.Clear(); }
            await responseStream.WriteAsync(send, context.CancellationToken);
        }
    }

    public override async Task WatchFrames(WatchFramesRequest request, IServerStreamWriter<LiveFrame> responseStream, ServerCallContext context)
    {
        sessions.Read(context);
        var gap = TimeSpan.FromSeconds(1 / (request.MaxFps > 0 ? Math.Min(request.MaxFps, 30) : 5));
        var maxWidth = request.MaxWidth > 0 ? request.MaxWidth : 1280;
        var camera = request.Camera;
        var last = new Dictionary<string, DateTime>();   // per camera: one camera's frames never crowd out another's
        await foreach (var f in line.WatchFrames(context.CancellationToken).ReadAllAsync(context.CancellationToken))
        {
            if (!request.AllCameras)
            {
                if (camera.Length > 0 && f.Camera != camera) continue;
                if (camera.Length == 0 && line.Status().Cameras.FirstOrDefault() is { } first && f.Camera != first.Name) continue;
            }
            if (request.NgOnly && f.Pass) continue;
            var now = DateTime.UtcNow;
            // drop, never queue; a bad part's frame always goes (they are few, and the Run screen
            // shows every bad part's picture)
            if (f.Pass && now - last.GetValueOrDefault(f.Camera, DateTime.MinValue) < gap) continue;
            last[f.Camera] = now;
            await responseStream.WriteAsync(f.Width > maxWidth ? Smaller(f, maxWidth) : f, context.CancellationToken);
        }
    }

    // The live picture scaled down to the client's width, graphics with it.
    static LiveFrame Smaller(LiveFrame f, int maxWidth)
    {
        var s = (double)maxWidth / f.Width;
        int w = maxWidth, h = Math.Max(1, (int)Math.Round(f.Height * s));
        using var src = SKBitmap.Decode(f.Jpeg.ToByteArray());
        if (src is null) return f;
        using var small = src.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
        using var img = SKImage.FromBitmap(small);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 82);
        var r = f.Clone();
        r.Jpeg = ByteString.CopyFrom(data.ToArray());
        r.Width = w;
        r.Height = h;
        r.Scale = (float)(f.Scale * s);
        foreach (var g in r.Graphics)
            foreach (var p in g.Points) { p.X = (float)(p.X * s); p.Y = (float)(p.Y * s); }
        return r;
    }

    // --- job designer (Engineer, Admin) --------------------------------------------------------

    public override Task<GetJobResponse> GetJob(GetJobRequest request, ServerCallContext context)
    {
        sessions.Design(context);
        return Task.FromResult(designer.GetJob(request));
    }

    public override Task<SaveJobResponse> SaveJob(SaveJobRequest request, ServerCallContext context)
    {
        var c = sessions.Design(context);
        return Task.FromResult(designer.SaveJob(request, c.User?.Name ?? ""));
    }

    public override Task<ListPicturesResponse> ListPictures(ListPicturesRequest request, ServerCallContext context)
    {
        sessions.Design(context);
        return Task.FromResult(designer.ListPictures(request));
    }

    public override Task<TryJobResponse> TryJob(TryJobRequest request, ServerCallContext context)
    {
        sessions.Design(context);
        return Task.Run(() => designer.TryJob(request), context.CancellationToken);
    }

    public override Task<GrabPictureResponse> GrabPicture(GrabPictureRequest request, ServerCallContext context)
    {
        sessions.Design(context);
        return Task.Run(() => designer.GrabPicture(request), context.CancellationToken);
    }

    public override Task<FindToolsResponse> FindTools(FindToolsRequest request, ServerCallContext context)
    {
        sessions.Design(context);
        return Task.Run(() => designer.FindTools(request), context.CancellationToken);
    }

    public override Task<SuggestBlobToleranceResponse> SuggestBlobTolerance(SuggestBlobToleranceRequest request, ServerCallContext context)
    {
        sessions.Design(context);
        return Task.Run(() => designer.SuggestBlobTolerance(request), context.CancellationToken);
    }

    public override Task<PlcTestSignalResponse> PlcTestSignal(PlcTestSignalRequest request, ServerCallContext context)
    {
        sessions.TestPlc(context);
        return Task.FromResult(line.PlcTestSignal(request));
    }

    // --- NG review and learning (Supervisor, QA, Engineer, Admin; Support and OEM with today's code, AS-04h) ------
    // A Support or OEM session's every action goes into the audit trail (sessions.AuditSupport).

    public override Task<ReviewPartResponse> ReviewPart(ReviewPartRequest request, ServerCallContext context)
    {
        var c = sessions.Learn(context);
        var r = reviews.Review(request, c.User?.Name ?? "");
        var said = request.Decision == ReviewDecision.Accepted
            ? request.KeepAsRecord ? "accepted, record only" : request.ConfirmUnreadable ? "accepted after a second look" : "accepted"
            : request.Decision == ReviewDecision.ConfirmedNg ? "rejected" : "no decision";
        sessions.AuditSupport(c, $"Picture {request.ImageId} sorted: {said}: " + (!r.Ok ? "refused, " + r.ErrorText
            : r.LooksBad ? "asked to double-check (looks bad)" : r.Unreadable ? "asked for a second validation (unreadable)"
            : (r.Learned ? "learned" : "record only") + (r.PossibleEscape ? ", possible escape" : "")));
        return Task.FromResult(r);
    }

    public override Task<ReviewsSummary> GetReviews(GetReviewsRequest request, ServerCallContext context)
    {
        sessions.Learn(context);
        return Task.FromResult(reviews.Summary(request));
    }

    public override async Task<LearnResponse> LearnFromReviews(LearnRequest request, ServerCallContext context)
    {
        var c = sessions.Learn(context);
        sessions.AuditSupport(c, $"Learning started on camera {request.Camera}");
        try
        {
            var r = await Task.Run(() => learning.Learn(request, context.CancellationToken), context.CancellationToken);
            sessions.AuditSupport(c, "Learning " + (!r.Ok ? "refused: " + r.ErrorText
                : r.ProposalId.Length > 0 ? "done: a proposal waits for a supervisor to apply it"
                : "done, nothing to apply" + (r.Problem.Length > 0 ? ": " + r.Problem : "")));
            return r;
        }
        catch (OperationCanceledException)
        {
            sessions.AuditSupport(c, "Learning stopped");
            throw;
        }
    }

    public override Task<ApplyLearningResponse> ApplyLearning(ApplyLearningRequest request, ServerCallContext context)
    {
        var c = sessions.Learn(context);
        // Support and OEM learn; a customer Supervisor, QA, Engineer or Admin applies the proposal (AS-04h, AS-04f)
        if (!c.MayChangeSetup)
        {
            sessions.AuditSupport(c, "Apply learning refused (a supervisor applies it)");
            return Task.FromResult(new ApplyLearningResponse { ErrorCode = 403,
                ErrorText = "A supervisor applies what was learned. Ask your supervisor.", ErrorKey = "runtime.refused.supervisorApplies" });
        }
        var r = learning.Apply(request, c.User?.Name ?? "");
        sessions.AuditSupport(c, r.Ok ? $"Learning applied: {r.Job} version {r.Version}" : "Apply learning refused: " + r.ErrorText);
        return Task.FromResult(r);
    }

    // --- the learning session (owner, 2026-11-04; Supervisor, QA, Engineer, Admin; Support and OEM, AS-04h) ------

    public override Task<ListToSortResponse> ListToSort(ListToSortRequest request, ServerCallContext context)
    {
        var c = sessions.Learn(context);
        sessions.AuditSupport(c, $"Sorting opened{(request.Job.Length > 0 ? " for job " + request.Job : "")}");
        return Task.Run(() => reviews.ToSort(request), context.CancellationToken);
    }

    public override Task<LearningSettings> GetLearningSettings(GetLearningSettingsRequest request, ServerCallContext context)
    {
        sessions.Read(context);
        return Task.FromResult(settings.Get());
    }

    public override Task<SetLearningSettingsResponse> SetLearningSettings(LearningSettings request, ServerCallContext context)
    {
        var c = sessions.Learn(context);
        var user = c.User?.Name ?? "";
        var before = settings.Get();
        string? why;
        try { why = settings.Set(request.Enabled, (int)request.IdleMinutes, user); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = "The setting could not be saved: " + e.Message; }
        if (why is not null)
            return Task.FromResult(new SetLearningSettingsResponse { ErrorCode = 400, ErrorText = why, ErrorKey = "runtime.refused.idleMinutes", Settings = before });
        okSamples.Enabled = request.Enabled;
        try
        {
            audit.AddEvent(DateTime.UtcNow, "learning_settings", false,
                $"Learning session {(request.Enabled ? "on" : "off")}, idle {request.IdleMinutes} min (was {(before.Enabled ? "on" : "off")}, {before.IdleMinutes} min), by {c.Who}");
        }
        catch (Exception) { /* the setting is saved; the audit trail reports its own faults */ }
        return Task.FromResult(new SetLearningSettingsResponse { Ok = true, Settings = settings.Get() });
    }

    public override Task<Counters> GetCounters(GetCountersRequest request, ServerCallContext context)
    {
        sessions.Read(context);
        return Task.FromResult(line.Counters());
    }

    public override async Task GetImage(GetImageRequest request, IServerStreamWriter<ImageChunk> responseStream, ServerCallContext context)
    {
        // a tried picture may be an engineer's upload: only those who may try jobs fetch it
        if (JobDesigner.IsTried(request.ImageId)) sessions.Design(context);
        else sessions.Read(context);
        var img = designer.TriedImage(request.ImageId, request.Encoding) ?? line.Image(request.ImageId, request.Encoding, request.WithGraphics)
            ?? throw new RpcException(new Grpc.Core.Status(StatusCode.NotFound, "This image is no longer kept."));
        for (var at = 0; at == 0 || at < img.Data.Length; at += ChunkBytes)
        {
            var chunk = new ImageChunk { Data = ByteString.CopyFrom(img.Data, at, Math.Min(ChunkBytes, img.Data.Length - at)) };
            if (at == 0) { chunk.Width = img.Width; chunk.Height = img.Height; chunk.Format = img.Format; }
            await responseStream.WriteAsync(chunk, context.CancellationToken);
        }
    }
}
