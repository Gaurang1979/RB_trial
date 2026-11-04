using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using VisionStudio.Remote;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Studio.Runtime;

/// <summary>
/// Connects to the runtime service over the local endpoint of the Hybrid 0 contract (named
/// pipe on Windows, $TMPDIR/vs-runtime.sock on macOS, VS_RUNTIME_ENDPOINT to override). One
/// channel serves every connection: after a drop the next call opens a new pipe or socket.
/// The session token survives reconnects; WhoAmI tells whether the runtime still knows it.
/// </summary>
public sealed class GrpcRuntimeConnector(GrpcChannel channel, Func<string> language) : IRuntimeConnector
{
    private readonly SessionToken _token = new();

    public static GrpcRuntimeConnector ForDefaultEndpoint(Func<string> language) =>
        new(RuntimeEndpoint.Default().CreateChannel(), language);

    public Task<IRuntimeClient> ConnectAsync(CancellationToken ct) =>
        Task.FromResult<IRuntimeClient>(new GrpcRuntimeClient(channel, _token, language));

    internal sealed class SessionToken
    {
        private string? _value;

        public string? Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }

        /// <summary>Forgets the token only if it is still the one a refused call sent, so a refusal of
        /// an old token never wipes a session signed in meanwhile.</summary>
        public void ClearIf(string? sent)
        {
            if (sent is not null) Interlocked.CompareExchange(ref _value, null, sent);
        }
    }
}

internal sealed class GrpcRuntimeClient(GrpcChannel channel, GrpcRuntimeConnector.SessionToken token, Func<string> language)
    : IRuntimeClient, IJobDesignClient, IReviewClient
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Start, Stop and a product change may load models or move the line: they get longer.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly V1.VisionRuntime.VisionRuntimeClient _runtime = new(channel);
    private readonly V1.Session.SessionClient _session = new(channel);
    private readonly V1.NgReviews.NgReviewsClient _reviews = new(channel);

    private CallOptions Options(CancellationToken ct, bool stream = false) => Options(ct, out _, stream);

    private CallOptions Options(CancellationToken ct, out string? sent, bool stream = false, TimeSpan? timeout = null)
    {
        var headers = new Metadata { { "accept-language", language() } };
        sent = token.Value;
        if (sent is not null) headers.Add("authorization", "Bearer " + sent);
        return new CallOptions(headers, stream ? null : DateTime.UtcNow + (timeout ?? CallTimeout), ct);
    }

    /// <summary>
    /// Reads need no token on the local endpoint, but an unknown token is refused: after a runtime
    /// restart the old token is dropped and the read asked again as nobody (Operator).
    /// </summary>
    public async Task<RuntimeStatus> GetStatusAsync(CancellationToken ct)
    {
        string? sent = null;
        try
        {
            return Map(await _runtime.GetStatusAsync(new V1.GetStatusRequest(), Options(ct, out sent)));
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Unauthenticated && sent is not null)
        {
            token.ClearIf(sent);
            return Map(await _runtime.GetStatusAsync(new V1.GetStatusRequest(), Options(ct)));
        }
    }

    public async IAsyncEnumerable<RuntimeStatus> WatchStatusAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var call = _runtime.WatchStatus(new V1.WatchStatusRequest(), Options(ct, stream: true));
        await foreach (var status in call.ResponseStream.ReadAllAsync(ct)) yield return Map(status);
    }

    public async IAsyncEnumerable<LiveFrame> WatchLiveAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // every camera of the job: the Run screen shows the one chosen and every camera's bad parts
        var request = new V1.WatchFramesRequest { MaxWidth = 1280, MaxFps = 10, AllCameras = true };
        using var call = _runtime.WatchFrames(request, Options(ct, stream: true));
        await foreach (var frame in call.ResponseStream.ReadAllAsync(ct)) yield return Map(frame);
    }

    public async IAsyncEnumerable<PartResult> WatchResultsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var call = _runtime.StreamResults(new V1.StreamResultsRequest(), Options(ct, stream: true));
        await foreach (var r in call.ResponseStream.ReadAllAsync(ct)) yield return Map(r);
    }

    public async Task<IReadOnlyList<UserTile>> ListUsersAsync(CancellationToken ct)
    {
        var response = await _session.ListUsersAsync(new V1.ListUsersRequest(), Options(ct));
        return response.Users.Where(u => u.HasPin || u.HasPassword)
            .Select(u => new UserTile(u.Name, DisplayName(u), Highest(u.Roles), u.HasPin, u.HasPassword))
            .ToArray();
    }

    public Task<SignInResult> SignInWithPinAsync(string userId, string pin, CancellationToken ct) =>
        SignInAsync(new V1.SignInRequest { User = userId, Pin = pin, Client = V1.ClientKind.Studio }, ct);

    public Task<SignInResult> SignInWithPasswordAsync(string userId, string password, string? code, CancellationToken ct) =>
        SignInAsync(new V1.SignInRequest { User = userId, Password = password, Code = code?.Trim() ?? "", Client = V1.ClientKind.Studio }, ct);

    public Task<SignInResult> SignInWithCardAsync(string card, CancellationToken ct) =>
        SignInAsync(new V1.SignInRequest { Card = card, Client = V1.ClientKind.Studio }, ct);

    private async Task<SignInResult> SignInAsync(V1.SignInRequest request, CancellationToken ct)
    {
        var response = await _session.SignInAsync(request, Options(ct));
        if (!response.Ok)
        {
            return response.ErrorKey is "runtime.signIn.wrong" or "runtime.signIn.cardUnknown"
                ? new SignInResult(SignInOutcome.Wrong)
                : new SignInResult(SignInOutcome.Refused, Text: response.ErrorText);
        }
        var old = token.Value;
        token.Value = response.Token;
        // a card replacing the signed-in user: end that user's session too, not only forget it
        if (old is not null && old != response.Token)
        {
            var headers = new Metadata { { "accept-language", language() }, { "authorization", "Bearer " + old } };
            try { await _session.SignOutAsync(new V1.SignOutRequest(), new CallOptions(headers, DateTime.UtcNow + CallTimeout, ct)); }
            catch (RpcException) { }   // it ends by itself after the runtime's idle time
        }
        var who = await WhoAmIAsync(ct);
        return new SignInResult(SignInOutcome.Ok,
            who ?? new SignedInUser(response.User.Name, DisplayName(response.User), Highest(response.User.Roles), true));
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        if (token.Value is null) return;
        string? sent = null;
        try
        {
            await _session.SignOutAsync(new V1.SignOutRequest(), Options(ct, out sent));
        }
        finally
        {
            token.ClearIf(sent);   // a sign-in meanwhile keeps its new token
        }
    }

    public async Task<SignedInUser?> WhoAmIAsync(CancellationToken ct)
    {
        if (token.Value is null) return null;
        string? sent = null;
        try
        {
            var who = await _session.WhoAmIAsync(new V1.WhoAmIRequest(), Options(ct, out sent));
            if (who.User is null || who.User.Name.Length == 0) return null;
            return new SignedInUser(who.User.Name, DisplayName(who.User), Highest(who.User.Roles), who.MayOperate,
                who.IdleSignOutSeconds > 0 ? TimeSpan.FromSeconds(who.IdleSignOutSeconds) : null, who.MayDesignJobs, who.MayTestPlc,
                // not said: an operator, or a runtime from before AS-04h, whose review rights follow the role
                who.MayLearn ? true : null);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Unauthenticated)
        {
            token.ClearIf(sent);   // the runtime restarted or the session ended
            return null;
        }
    }

    public async Task<IReadOnlyList<ProductInfo>> ListProductsAsync(CancellationToken ct)
    {
        var response = await _runtime.ListJobsAsync(new V1.ListJobsRequest(), Options(ct));
        return response.Jobs
            .SelectMany(j => j.Variants.Count == 0
                ? [new ProductInfo(j.Name, "", j.PictureId)]
                : j.Variants.Select(v => new ProductInfo(j.Name, v, j.PictureId)))
            .ToArray();
    }

    public async Task<RunCounters> GetCountersAsync(CancellationToken ct) =>
        Map(await _runtime.GetCountersAsync(new V1.GetCountersRequest(), Options(ct)));

    public async Task<FullImage?> GetImageAsync(string imageId, CancellationToken ct)
    {
        var request = new V1.GetImageRequest { ImageId = imageId, Encoding = V1.GetImageRequest.Types.Encoding.Png };
        try
        {
            using var call = _runtime.GetImage(request, Options(ct, out _, timeout: ImageTimeout));
            using var data = new MemoryStream();
            int width = 0, height = 0;
            await foreach (var chunk in call.ResponseStream.ReadAllAsync(ct))
            {
                if (width == 0) (width, height) = (chunk.Width, chunk.Height);
                chunk.Data.WriteTo(data);
            }
            return data.Length == 0 ? null : new FullImage(data.ToArray(), width, height);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            return null;   // kept only for a while; an old NG image may be gone
        }
        catch (Exception e) when (e is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled } && ct.IsCancellationRequested)
        {
            // the caller's time ran out first (a big picture on a slow link): no answer in time, the link is fine
            throw new RpcException(new Grpc.Core.Status(StatusCode.DeadlineExceeded, "The picture did not arrive in time."));
        }
    }

    public Task<CommandResult> ChangeProductAsync(ProductInfo product, CancellationToken ct) =>
        Command(async () =>
        {
            var r = await _runtime.ChangeJobAsync(new V1.ChangeJobRequest { Name = product.Job, Variant = product.Variant }, Command(ct));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });

    public Task<CommandResult> StartAsync(CancellationToken ct) =>
        Command(async () =>
        {
            var r = await _runtime.StartAsync(new V1.StartRequest(), Command(ct));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });

    public Task<CommandResult> StopAsync(CancellationToken ct) =>
        Command(async () =>
        {
            var r = await _runtime.StopAsync(new V1.StopRequest(), Command(ct));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });

    public Task<CommandResult> PlcTestSignalAsync(bool ok, CancellationToken ct) =>
        Command(async () =>
        {
            var r = await _runtime.PlcTestSignalAsync(new V1.PlcTestSignalRequest { Ok = ok }, Command(ct));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });

    // --- job designer and Start batch ------------------------------------------------------------

    /// <summary>Trying a picture builds the tools (AI models) and reads a full picture: it gets longer.</summary>
    private static readonly TimeSpan TryTimeout = TimeSpan.FromSeconds(60);
    /// <summary>A full picture (20 MP is some 15 MB of PNG) over plant Wi-Fi or a VPN; the caller's own limit, if shorter, ends it first.</summary>
    public static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A call whose answer carries more than ok: the answer when it is a yes, and the refusal.</summary>
    private async Task<(T? Answer, CommandResult Result)> Ask<T>(Func<Task<T>> call, Func<T, (bool Ok, string Key, string Text)> verdict)
        where T : class
    {
        T? answer = null;
        var result = await Command(async () => verdict(answer = await call()));
        return (result.Ok ? answer : null, result);
    }

    public async Task<JobFile> GetJobAsync(string name, CancellationToken ct)
    {
        var (j, result) = await Ask(async () => await _runtime.GetJobAsync(new V1.GetJobRequest { Name = name }, Command(ct)),
            r => (r.Ok, r.ErrorKey, r.ErrorText));
        return j is not null ? new JobFile(true, j.Json, j.Version, j.Open) : new JobFile(false, Refused: result);
    }

    public async Task<SaveOutcome> SaveJobAsync(string json, CancellationToken ct)
    {
        var (s, result) = await Ask(async () => await _runtime.SaveJobAsync(new V1.SaveJobRequest { Json = json }, Command(ct)),
            r => (r.Ok, r.ErrorKey, r.ErrorText));
        return s is not null ? new SaveOutcome(true, s.Name, s.Version) : new SaveOutcome(false, Refused: result);
    }

    public async Task<PictureList> ListPicturesAsync(string json, int camera, CancellationToken ct)
    {
        var (p, result) = await Ask(async () => await _runtime.ListPicturesAsync(new V1.ListPicturesRequest { JobJson = json, Camera = camera }, Command(ct)),
            r => (r.Ok, r.ErrorKey, r.ErrorText));
        return p is not null ? new PictureList(true, p.Names.ToArray()) : new PictureList(false, [], result);
    }

    public async Task<TryOutcome> TryJobAsync(TryRequest request, CancellationToken ct)
    {
        var req = new V1.TryJobRequest { JobJson = request.Json, Camera = request.Camera, MaxWidth = 1280, TeachTool = request.TeachTool ?? "",
            SkipPictureCheck = request.SkipPictureCheck, NotShown = request.NotShown, TeachSpot = request.TeachSpot };
        if (request.Stored is { } stored) req.Stored = stored;
        else if (request.ImageId is { } id) req.ImageId = id;
        foreach (var (k, v) in request.Batch ?? new Dictionary<string, string>()) req.Batch[k] = v;
        foreach (var (k, v) in request.PlcText ?? new Dictionary<int, string>()) req.PlcText[k] = v;
        var (t, result) = await Ask(async () =>
        {
            var options = Options(ct, out var sent, timeout: TryTimeout);
            _commandToken = sent;
            return await _runtime.TryJobAsync(req, options);
        }, r => (r.Ok, r.ErrorKey, r.ErrorText));
        return t is null ? new TryOutcome(false, Refused: result) : Map(t);
    }

    internal static TryOutcome Map(V1.TryJobResponse t) =>
        new(true, t.Pass, t.TimeMs, t.Jpeg.ToByteArray(), t.Width, t.Height, t.Scale, t.ImageWidth, t.ImageHeight,
            t.Tools.Select(x => new ToolTried(x.Name, x.Kind, x.Pass, x.ReasonText, x.TimeMs, x.HasPrintQuality ? x.PrintQuality : null,
                x.ReportOnly, x.HasCodeGrade ? x.CodeGrade : null, x.Pixels is { } p ? Map(p) : null)).ToArray(),
            t.Codes.Select(c => new CodeOutcome(c.Text, c.Format, c.AddOn, c.Corners.Select(p => new Point2(p.X, p.Y)).ToArray(),
                c.Gs1.ToDictionary(kv => kv.Key, kv => kv.Value), c.Field, c.Tool)).ToArray(),
            t.Graphics.Select(Map).Where(g => g is not null).Select(g => g!).ToArray(),
            t.Lines.Select(l => new TextLineRead(l.Tool, l.Text, l.Expected, l.Pass, l.Quality, l.Format, l.Field)).ToArray(),
            t.TaughtJson, t.TaughtText, ImageId: t.ImageId, PictureCheck: t.PictureCheck is { } pc ? Map(pc) : null);

    internal static PictureCheckOutcome Map(V1.PictureCheck c) => new(
        c.Zones.Select(z => new PictureZoneCheck(z.Zone, z.X, z.Y, z.Width, z.Height, z.Sharpness >= 0 ? z.Sharpness : null, z.Brightness, z.State)).ToArray(),
        c.Advice.Select(a => new PictureAdviceLine(a.Kind, a.Zones.ToArray())).ToArray());

    public async Task<GrabOutcome> GrabPictureAsync(string json, int camera, CancellationToken ct)
    {
        var (g, result) = await Ask(async () =>
        {
            var options = Options(ct, out var sent, timeout: TryTimeout);
            _commandToken = sent;
            return await _runtime.GrabPictureAsync(new V1.GrabPictureRequest { JobJson = json, Camera = camera }, options);
        }, r => (r.Ok, r.ErrorKey, r.ErrorText));
        return g is not null ? new GrabOutcome(true, g.Name, g.JobJson) : new GrabOutcome(false, Refused: result);
    }

    public async Task<FindOutcome> FindToolsAsync(string json, int camera, string stored, (int X, int Y, int Width, int Height)? area, CancellationToken ct)
    {
        var req = new V1.FindToolsRequest { JobJson = json, Camera = camera, Stored = stored };
        if (area is { } a) req.Area = new V1.Area { X = a.X, Y = a.Y, Width = a.Width, Height = a.Height };
        var (f, result) = await Ask(async () =>
        {
            var options = Options(ct, out var sent, timeout: TryTimeout);
            _commandToken = sent;
            return await _runtime.FindToolsAsync(req, options);
        }, r => (r.Ok, r.ErrorKey, r.ErrorText));
        if (f is null) return new FindOutcome(false, Refused: result);
        return new FindOutcome(true, f.Suggestions.Select(s => new ToolSuggestion(s.Kind, s.Summary, s.Read.ToArray(), s.Fields.ToArray(),
            s.Outline.Select(p => new Point2(p.X, p.Y)).ToArray(), s.ToolJson)).ToArray(), f.TimeMs,
            f.Jpeg.ToByteArray(), f.Width, f.Height, f.Scale, f.ImageWidth, f.ImageHeight);
    }

    public async Task<BlobToleranceOutcome> SuggestBlobToleranceAsync(string json, int camera, string tool, IReadOnlyList<string> pictures, CancellationToken ct)
    {
        var req = new V1.SuggestBlobToleranceRequest { JobJson = json, Camera = camera, Tool = tool };
        req.Pictures.AddRange(pictures);
        var (s, result) = await Ask(async () =>
        {
            var options = Options(ct, out var sent, timeout: TryTimeout);
            _commandToken = sent;
            return await _runtime.SuggestBlobToleranceAsync(req, options);
        }, r => (r.Ok, r.ErrorKey, r.ErrorText));
        return s is null ? new BlobToleranceOutcome(false, Refused: result) : Map(s);
    }

    internal static BlobToleranceOutcome Map(V1.SuggestBlobToleranceResponse s) => new(true, s.Found, s.MaxDeviation, s.MaxDentDepth,
        s.Unit.Length > 0 ? s.Unit : "px", s.Pictures, s.WorstDeviation, s.WorstDent, s.ReasonText,
        s.Calibration is { } c ? new LensCalibrationInfo(c.Fx, c.Fy, c.Cx, c.Cy, c.K1, c.K2, c.P1, c.P2, c.K3, c.MmPerPx, c.RmsPx) : null);

    public Task<CommandResult> AskChangeoverAgainAsync(CancellationToken ct) =>
        Command(async () =>
        {
            var r = await _runtime.SetInputAsync(new V1.SetInputRequest { Name = "changeover.ask" }, Command(ct));
            return (r.Ok, r.Ok ? "" : "run.reason.refused", "");
        });

    public Task<CommandResult> AcceptChangeoverAtLineAsync(string user, string secretKind, string secret, string code, CancellationToken ct) =>
        Command(async () =>
        {
            var request = new V1.AcceptChangeoverRequest { User = user, Code = code };
            switch (secretKind)
            {
                case "pin": request.Pin = secret; break;
                case "password": request.Password = secret; break;
                case "card": request.Card = secret; break;
                default: return (false, "run.reason.refused", "");
            }
            var r = await _runtime.AcceptChangeoverAsync(request, Command(ct));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });

    public Task<CommandResult> AnswerRemoteAsync(string id, bool approve, string user, string secretKind, string secret, CancellationToken ct) =>
        Command(async () =>
        {
            var request = new V1.AnswerRemoteRequestRequest { Id = id, Approve = approve, User = user };
            switch (secretKind)
            {
                case "pin": request.Pin = secret; break;
                case "password": request.Password = secret; break;
                case "card": request.Card = secret; break;
                case "": break;   // a confirmation, or No
                default: return (false, "run.reason.refused", "");
            }
            var r = await _runtime.AnswerRemoteRequestAsync(request, Command(ct));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });

    public Task<CommandResult> SetBatchAsync(string key, string value, CancellationToken ct) =>
        Command(async () =>
        {
            var r = await _runtime.SetInputAsync(new V1.SetInputRequest { Name = "batch." + key, Value = value }, Command(ct));
            // SetInput answers with a code only: 203 (PLC-INTERFACE JobChangeWhileBusy) is "the line runs"
            return (r.Ok, r.Ok ? "" : r.ErrorCode == 203 ? "batch.reason.running" : "batch.reason.refused", "");
        });

    // --- NG review and learning (owner, 2026-10-31) ---------------------------------------------

    /// <summary>Learning runs the job three times over every reviewed picture of a camera.</summary>
    public static readonly TimeSpan LearnTimeout = TimeSpan.FromMinutes(10);

    private static IReadOnlyList<CameraReviewCounts> Counts(V1.ReviewsSummary? s) =>
        s is null ? [] : s.Cameras.Select(c => new CameraReviewCounts(c.Camera, c.Name, (int)c.Accepted, (int)c.ConfirmedNg, (int)c.AcceptedRecordOnly)).ToList();

    public async Task<ReviewOutcome> ReviewPartAsync(string imageId, ReviewDecision decision, ReviewConfirm confirm, string note, CancellationToken ct)
    {
        V1.ReviewPartResponse? r = null;
        var result = await Command(async () =>
        {
            r = await _runtime.ReviewPartAsync(new V1.ReviewPartRequest
            {
                ImageId = imageId,
                Decision = decision == ReviewDecision.Accepted ? V1.ReviewDecision.Accepted : V1.ReviewDecision.ConfirmedNg,
                ConfirmLooksBad = confirm.HasFlag(ReviewConfirm.LooksBad),
                ConfirmUnreadable = confirm.HasFlag(ReviewConfirm.Unreadable),
                KeepAsRecord = confirm.HasFlag(ReviewConfirm.KeepAsRecord),
                Note = note,
            }, Command(ct));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });
        return new ReviewOutcome(result, r?.LooksBad ?? false, r?.Faults.ToArray() ?? [], r?.Learned ?? false, Counts(r?.Summary))
        {
            Unreadable = r?.Unreadable ?? false,
            PossibleEscape = r?.PossibleEscape ?? false,
        };
    }

    public async Task<IReadOnlyList<CameraReviewCounts>> GetReviewsAsync(CancellationToken ct) =>
        Counts(await _runtime.GetReviewsAsync(new V1.GetReviewsRequest(), Options(ct, out _)));

    public async Task<LearnOutcome> LearnAsync(int camera, CancellationToken ct)
    {
        V1.LearnResponse? r = null;
        var result = await Command(async () =>
        {
            r = await _runtime.LearnFromReviewsAsync(new V1.LearnRequest { Camera = camera }, Options(ct, out _, timeout: LearnTimeout));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });
        if (r is null || !result.Ok) return LearnOutcome.Refused(result);
        return new LearnOutcome(result, r.ProposalId,
            r.Settings.Select(s => new TunedSettingInfo(s.Tool, s.Setting, s.Current, s.Proposed, s.Looser)).ToList(),
            r.Before, r.After, r.CanApply, r.Problem, r.StillRejected.ToArray(), (int)r.Accepted, (int)r.ConfirmedNg)
        {
            GoodPassBefore = r.GoodPassPctBefore, BadFailBefore = r.BadFailPctBefore,
            GoodPassAfter = r.GoodPassPctAfter, BadFailAfter = r.BadFailPctAfter,
        };
    }

    public async Task<SortList> ListToSortAsync(int camera, CancellationToken ct)
    {
        var (a, result) = await Ask(async () => await _runtime.ListToSortAsync(new V1.ListToSortRequest { Camera = camera }, Options(ct, out _)),
            _ => (true, "", ""));
        return new SortList(result, a?.Items.Select(i => new SortPicture(i.ImageId, i.Camera, i.CameraName, i.Ok, i.Faults.ToArray(),
            i.Time?.ToDateTimeOffset() ?? DateTimeOffset.MinValue, i.NearLimit)
        {
            FlaggedBy = i.FlaggedOkBy, FlaggedAt = i.FlaggedOkAt?.ToDateTimeOffset(),
        }).ToList() ?? [], (int)(a?.Total ?? 0));
    }

    public async Task<ReviewedList> ListReviewedAsync(int camera, CancellationToken ct)
    {
        var (a, result) = await Ask(async () => await _reviews.ListReviewedAsync(new V1.ListReviewedRequest { Camera = camera }, Options(ct, out _)),
            _ => (true, "", ""));
        var items = a?.Items.Select(i =>
        {
            var at = i.Time?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
            return new SortPicture(i.ImageId, i.Camera, i.CameraName, i.SoftwareOk, i.Faults.ToArray(), at, false)
            {
                Reviewed = new ReviewedInfo(i.Decision == V1.ReviewDecision.ConfirmedNg ? ReviewDecision.ConfirmedNg : ReviewDecision.Accepted,
                    i.Learned, i.User, at),
            };
        }).ToList() ?? [];
        var flags = a?.Flags.Select(f => new WasOkMark(f.ImageId, f.Camera, f.User, f.Time?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)).ToList() ?? [];
        return new ReviewedList(result, items, (int)(a?.Total ?? 0), flags);
    }

    public async Task<ReviewOutcome> RemoveReviewAsync(string imageId, CancellationToken ct)
    {
        V1.RemoveReviewResponse? r = null;
        var result = await Command(async () =>
        {
            r = await _reviews.RemoveReviewAsync(new V1.RemoveReviewRequest { ImageId = imageId }, Command(ct));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });
        return new ReviewOutcome(result, false, [], false, Counts(r?.Summary));
    }

    public Task<CommandResult> FlagWasOkAsync(string imageId, string note, CancellationToken ct) =>
        Command(async () =>
        {
            var r = await _reviews.FlagWasOkAsync(new V1.FlagWasOkRequest { ImageId = imageId, Note = note }, Command(ct));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });

    public async Task<LearningSettingsInfo?> GetLearningSettingsAsync(CancellationToken ct)
    {
        var s = await _runtime.GetLearningSettingsAsync(new V1.GetLearningSettingsRequest(), Options(ct, out _));
        return new LearningSettingsInfo(s.Enabled, (int)s.IdleMinutes, s.ChangedBy, s.ChangedAt?.ToDateTimeOffset());
    }

    public Task<CommandResult> SetLearningSettingsAsync(bool enabled, int idleMinutes, CancellationToken ct) =>
        Command(async () =>
        {
            var r = await _runtime.SetLearningSettingsAsync(new V1.LearningSettings { Enabled = enabled, IdleMinutes = (uint)Math.Max(0, idleMinutes) }, Command(ct));
            return (r.Ok, r.ErrorKey, r.ErrorText);
        });

    public async Task<ApplyOutcome> ApplyLearningAsync(string proposalId, CancellationToken ct)
    {
        // saving the job reloads it on the line (AI models): it may take longer than a command
        var (a, result) = await Ask(async () => await _runtime.ApplyLearningAsync(new V1.ApplyLearningRequest { ProposalId = proposalId },
            Options(ct, out _, timeout: TimeSpan.FromMinutes(2))),
            r => (r.Ok, r.ErrorKey, r.ErrorText));
        return new ApplyOutcome(result, a?.Job ?? "", a?.Version ?? 0);
    }

    private string? _commandToken;

    private CallOptions Command(CancellationToken ct)
    {
        var options = Options(ct, out var sent, timeout: CommandTimeout);
        _commandToken = sent;
        return options;
    }

    /// <summary>The link is broken only when the runtime cannot be reached; any other answer is an answer.</summary>
    internal static bool IsTransport(StatusCode code) => code is StatusCode.Unavailable or StatusCode.Unknown
        or StatusCode.Internal or StatusCode.Cancelled;

    /// <summary>A refusal is an answer, not a broken link; a transport failure is rethrown so the link reconnects.</summary>
    private async Task<CommandResult> Command(Func<Task<(bool Ok, string Key, string Text)>> call)
    {
        try
        {
            var (ok, key, text) = await call();
            return ok ? CommandResult.Done : new CommandResult(false, key.Length > 0 ? key : null, text.Length > 0 ? text : null);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.PermissionDenied)
        {
            return new CommandResult(false, "run.reason.no_rights");
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Unauthenticated)
        {
            token.ClearIf(_commandToken);   // nobody signed in, or the runtime no longer knows this session
            return new CommandResult(false, "run.reason.sign_in");
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Unimplemented)
        {
            return new CommandResult(false, "run.reason.runtime_old");   // a call an older runtime does not have
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.DeadlineExceeded)
        {
            // The runtime may still be doing it: say so, and let the status show what happened.
            return new CommandResult(false, "run.reason.no_answer");
        }
        catch (RpcException e) when (!IsTransport(e.StatusCode))
        {
            return new CommandResult(false, null, e.Status.Detail);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;   // the channel is shared

    internal static RuntimeStatus Map(V1.Status s)
    {
        var top = s.Alarms.FirstOrDefault(a => a.Level == V1.Alarm.Types.Level.Alarm);
        var warning = s.Alarms.FirstOrDefault(a => a.Level == V1.Alarm.Types.Level.Warning);
        var alarm = top?.Text;
        if (alarm is null && s.State == V1.Status.Types.State.Error)
        {
            alarm = s.ErrorText.Length > 0 ? s.ErrorText : "";
        }
        var inspections = (long)s.Inspections;
        var ng = (long)s.Ng;
        return new RuntimeStatus
        {
            State = s.State is V1.Status.Types.State.Ready or V1.Status.Types.State.Busy ? LineState.Running : LineState.Stopped,
            Product = s.ActiveJob.Length > 0 ? new ProductRef(s.ActiveJob, s.ActiveVariant) : null,
            Counters = new Counters(Math.Max(0, inspections - ng), ng),
            CameraOk = s.Cameras.All(c => c.Connected),
            PlcOk = s.PlcConnected,
            LicenceOk = s.LicenceOk,
            Alarm = alarm,
            AlarmHelp = top?.Help is { Length: > 0 } help ? help : null,
            Warning = warning?.Text,
            WarningHelp = warning?.Help is { Length: > 0 } warningHelp ? warningHelp : null,
            Cameras = s.Cameras.Select(c => c.Name).ToArray(),
            Batch = s.Batch.Select(b => new BatchEntry(b.Key, b.Label, b.Example, b.Value)).ToArray(),
            LineSpeedPpm = float.IsFinite(s.LineSpeedPpm) ? Math.Max(0, s.LineSpeedPpm) : 0,
            CycleBudgetMs = float.IsFinite(s.CycleBudgetMs) ? Math.Max(0, s.CycleBudgetMs) : 0,
            Plc = s.Plc is { } p ? new PlcSignals(p.LinkUp, p.HeartbeatOk, p.RunEnable, p.LastTriggerId,
                p.LastResultTime?.ToDateTimeOffset(), p.LastResultPass, p.Link) : null,
            SetupCheck = s.SetupCheck is { } c ? Map(c) : null,
            Remote = s.RemoteService is { } rs ? new RemoteServiceInfo(rs.Active, rs.Requests.Select(q => new RemoteAsk(q.Id,
                q.Kind == V1.RemoteRequest.Types.Kind.Approve, q.Title, q.What,
                q.Changes.Select(c => new RemoteFieldChange(c.Field, c.OldValue, c.NewValue)).ToList(), q.Reason,
                q.Expires?.ToDateTimeOffset())).ToList()) : null,
            Learning = s.Learning is { } l ? new LearningInfo(l.Enabled, (int)l.IdleMinutes, l.Idle, (int)l.IdleSeconds, (int)l.ToSort, l.MayLearn)
            {
                BadToReview = (int)l.BadToReview, FlaggedOk = (int)l.FlaggedOk,
            } : null,
            Changeover = s.Changeover is { } co ? new ChangeoverInfo(co.State switch
            {
                V1.Changeover.Types.State.NotNeeded => ChangeoverCheck.NotNeeded,
                V1.Changeover.Types.State.Accepted => ChangeoverCheck.Accepted,
                V1.Changeover.Types.State.Refused => ChangeoverCheck.Refused,
                V1.Changeover.Types.State.NoServer => ChangeoverCheck.NoServer,
                // Unknown or unset from a newer runtime: waiting, never a pass.
                _ => ChangeoverCheck.Waiting,
            }, co.Code, co.By, co.At?.ToDateTimeOffset(), co.Note, co.AtLine) : null,
        };
    }

    internal static SetupCheckOutcome? Map(V1.SetupCheck c) => c.State switch
    {
        V1.SetupCheck.Types.State.Checking => new(SetupCheckState.Checking, c.Job, []),
        V1.SetupCheck.Types.State.Stopped => new(SetupCheckState.Stopped, c.Job, []),
        V1.SetupCheck.Types.State.Done => new(SetupCheckState.Done, c.Job, c.Cameras.Select(x => new CameraSetupOutcome(
            x.Camera, x.Pictures, x.Source, x.Error, x.PictureCheck is { } pc ? Map(pc) : null,
            x.Tools.Where(t => t.Pixels is not null).Select(t => new ToolPixels(t.Name, Map(t.Pixels))).ToArray(),
            x.Unmeasured.ToArray())).ToArray()),
        _ => null,   // a state a newer runtime knows and this app does not
    };

    internal static RunCounters Map(V1.Counters c) =>
        new((long)c.Total, (long)c.Ok, (long)c.Ng, c.YieldPercent, c.MedianCycleMs, c.MaxCycleMs);

    internal static LiveFrame Map(V1.LiveFrame f)
    {
        var graphics = f.Graphics.Select(Map).Where(g => g is not null).Select(g => g!).ToArray();
        var verdict = f.InspectionId == 0 ? Verdict.None   // a preview frame: no part was checked
            : f.Pass ? Verdict.Ok
            : graphics.Any(g => g.Verdict == Verdict.Duplicate) && graphics.All(g => g.Verdict != Verdict.Ng) ? Verdict.Duplicate
            : Verdict.Ng;
        return new LiveFrame(f.InspectionId, DateTimeOffset.Now, f.Jpeg.ToByteArray(), f.Width, f.Height, verdict, graphics,
            f.ImageId.Length > 0 ? f.ImageId : null,
            f.Tools.Select(t => new ToolOutcome(t.Name, t.Pass, t.HasPrintQuality ? t.PrintQuality : null, t.ReportOnly)).ToArray(),
            f.Codes.Select(c => new CodeOutcome(c.Text, c.Format, c.AddOn)).ToArray(), f.Camera);
    }

    internal static PartResult Map(V1.InspectionResult r)
    {
        var read = r.Codes.Count(c => c.State == V1.CodeRead.Types.State.Read);
        var missed = r.Codes.Count(c => c.State == V1.CodeRead.Types.State.Missed);
        var duplicate = r.Codes.Count(c => c.State == V1.CodeRead.Types.State.Duplicate);
        var failedTool = r.Tools.FirstOrDefault(t => !t.Pass);
        // Same rule as the live frame: only a part whose sole fault is a code seen before is "same code".
        var verdict = r.Pass ? Verdict.Ok
            : duplicate > 0 && missed == 0 && failedTool is null ? Verdict.Duplicate
            : Verdict.Ng;
        var reason = r.Pass ? ""
            : failedTool is not null ? (failedTool.ReasonText.Length > 0 ? failedTool.ReasonText : failedTool.Name)
            : r.Codes.FirstOrDefault(c => c.State == V1.CodeRead.Types.State.Missed) is { } miss ? miss.Reason
            : "";
        var product = r.Variant.Length > 0 ? $"{r.Job} · {r.Variant}" : r.Job;
        var time = r.Time is null ? DateTimeOffset.Now : r.Time.ToDateTimeOffset();
        return new PartResult(r.InspectionId, time, product, r.PartId, verdict, r.CycleMs, reason, read, missed, duplicate,
            r.ImageIds.FirstOrDefault(i => i.Length > 0));
    }

    internal static PixelCheckTried Map(V1.PixelCheck p) =>
        new(p.Measure, p.NeedPx, p.UnsurePx, p.FoundPx, p.Factor, p.PixelsTimes, p.LensMm, p.LensAdvisedMm, p.Accepted, p.AcceptedBy,
            p.AcceptedAt?.ToDateTimeOffset(),
            p.Items.Select(i => new PixelItemTried(i.Name, i.Measure, i.SizePx, i.Below, i.Corners.Select(c => new Point2(c.X, c.Y)).ToArray())).ToArray());

    internal static ResultGraphic? Map(V1.Graphic g) =>
        Shape(g) is { } shape ? shape with { Tool = g.Tool.Length > 0 ? g.Tool : null } : null;

    private static ResultGraphic? Shape(V1.Graphic g)
    {
        var points = g.Points.Select(p => new Point2(p.X, p.Y)).ToList();
        if (points.Count == 0) return null;
        var verdict = g.Colour switch
        {
            "status.ok" => Verdict.Ok,
            "status.ng" => Verdict.Ng,
            "status.warning" => Verdict.Warn,
            "status.duplicate" => Verdict.Duplicate,
            _ => Verdict.Info,
        };
        var label = g.Label.Length > 0 ? g.Label : null;
        var tip = g.Tooltip.Length > 0 ? g.Tooltip : null;
        switch (g.Kind)
        {
            case V1.Graphic.Types.Kind.Rect when points.Count >= 2:
                var (a, b) = (points[0], points[1]);
                return new ResultGraphic(GraphicKind.Polygon, [a, new Point2(b.X, a.Y), b, new Point2(a.X, b.Y)], verdict, label, tip);
            case V1.Graphic.Types.Kind.Line:
                return new ResultGraphic(GraphicKind.Polyline, points, verdict, label, tip);
            case V1.Graphic.Types.Kind.Cross:
                return new ResultGraphic(GraphicKind.Cross, points, verdict, label, tip);
            case V1.Graphic.Types.Kind.Text:
                return new ResultGraphic(GraphicKind.Text, points, verdict, label, tip);
            default:
                return new ResultGraphic(GraphicKind.Polygon, points, verdict, label, tip);
        }
    }

    private static string DisplayName(V1.UserInfo u) => u.FullName.Length > 0 ? u.FullName : u.Name;

    private static Role Highest(IEnumerable<V1.Role> roles) =>
        roles.Select(r => r switch
        {
            V1.Role.Admin => Role.Admin,
            V1.Role.Engineer => Role.Engineer,
            V1.Role.Supervisor => Role.Supervisor,
            V1.Role.Qa => Role.Qa,
            _ => Role.Operator,
        }).DefaultIfEmpty(Role.Operator).Max();
}
