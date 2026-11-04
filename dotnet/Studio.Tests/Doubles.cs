using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.Tests;

/// <summary>A runtime connection the test scripts: answers, refusals, failures and streams.</summary>
internal sealed class ScriptedClient : IRuntimeClient, IJobDesignClient, IReviewClient
{
    public RuntimeStatus Status { get; set; } = new() { Product = new ProductRef("Whisky 750 ml", "") };
    public Channel<RuntimeStatus> StatusStream { get; } = Channel.CreateUnbounded<RuntimeStatus>();
    public Channel<LiveFrame> LiveStream { get; } = Channel.CreateUnbounded<LiveFrame>();
    public Channel<PartResult> ResultStream { get; } = Channel.CreateUnbounded<PartResult>();
    /// <summary>StreamResults fails at once (a runtime without it).</summary>
    public bool NoResults { get; set; }
    public List<string> Calls { get; } = [];
    public Exception? FailWith { get; set; }
    public CommandResult StartResult { get; set; } = CommandResult.Done;
    public CommandResult ChangeResult { get; set; } = CommandResult.Done;
    public string GoodPin { get; set; } = "1234";
    public string GoodPassword { get; set; } = "secret-pass";
    /// <summary>Badge numbers and whose they are.</summary>
    public Dictionary<string, string> Cards { get; } = new() { ["0004211337"] = "supervisor" };
    /// <summary>Secrets the app sent, so a test can check none stays behind.</summary>
    public List<string> Secrets { get; } = [];
    /// <summary>Today's codes the app sent with a password (AS-04h).</summary>
    public List<string> Codes { get; } = [];
    /// <summary>Learn waits until it is cancelled (Stop learning).</summary>
    public bool LearnHangs { get; set; }
    public SignedInUser? Who { get; set; }
    public bool Disposed { get; private set; }

    public IReadOnlyList<UserTile> Users { get; set; } =
        [new("operator", "Anita", Role.Operator), new("supervisor", "Ramesh", Role.Supervisor),
         new("eng", "Suresh", Role.Engineer, HasPin: false, HasPassword: true)];

    public IReadOnlyList<ProductInfo> Products { get; set; } =
        [new("Whisky 750 ml", ""), new("Whisky 1 L", ""), new("Rum 375 ml", "")];

    private T Answer<T>(string call, T value)
    {
        Calls.Add(call);
        if (FailWith is { } e) throw e;
        return value;
    }

    /// <summary>GetStatus never answers (a runtime that hangs) until the call is cancelled.</summary>
    public bool HangStatus { get; set; }

    /// <summary>GetStatus answers (or fails) only after this long, deaf to cancellation (a slow machine).</summary>
    public TimeSpan StatusLatency { get; set; }

    public async Task<RuntimeStatus> GetStatusAsync(CancellationToken ct)
    {
        if (HangStatus) await Task.Delay(Timeout.Infinite, ct);
        if (StatusLatency > TimeSpan.Zero) await Task.Delay(StatusLatency);
        return Answer("GetStatus", Status);
    }

    public async IAsyncEnumerable<RuntimeStatus> WatchStatusAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var s in StatusStream.Reader.ReadAllAsync(ct)) yield return s;
    }

    public async IAsyncEnumerable<LiveFrame> WatchLiveAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var f in LiveStream.Reader.ReadAllAsync(ct)) yield return f;
    }

    public async IAsyncEnumerable<PartResult> WatchResultsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (NoResults) throw new InvalidOperationException("StreamResults is not implemented");
        await foreach (var r in ResultStream.Reader.ReadAllAsync(ct)) yield return r;
    }

    public Task<IReadOnlyList<UserTile>> ListUsersAsync(CancellationToken ct) => Task.FromResult(Answer("ListUsers", Users));

    public Task<SignInResult> SignInWithPinAsync(string userId, string pin, CancellationToken ct)
    {
        var tile = Users.First(u => u.Id == userId);
        return Task.FromResult(Answer($"SignIn {userId}", pin == GoodPin
            ? new SignInResult(SignInOutcome.Ok, new SignedInUser(userId, tile.DisplayName, tile.Role, true))
            : new SignInResult(SignInOutcome.Wrong)));
    }

    public Task<SignInResult> SignInWithPasswordAsync(string userId, string password, string? code, CancellationToken ct)
    {
        Secrets.Add(password);
        if (!string.IsNullOrEmpty(code)) { Secrets.Add(code); Codes.Add(code); }
        var tile = Users.FirstOrDefault(u => u.Id == userId);
        return Task.FromResult(Answer($"SignIn password {userId}", tile is { HasPassword: true } && password == GoodPassword
            ? new SignInResult(SignInOutcome.Ok, new SignedInUser(userId, tile.DisplayName, tile.Role, true))
            : new SignInResult(SignInOutcome.Wrong)));
    }

    public Task<SignInResult> SignInWithCardAsync(string card, CancellationToken ct)
    {
        Secrets.Add(card);
        var tile = Cards.TryGetValue(card, out var id) ? Users.First(u => u.Id == id) : null;
        return Task.FromResult(Answer("SignIn card", tile is not null
            ? new SignInResult(SignInOutcome.Ok, new SignedInUser(tile.Id, tile.DisplayName, tile.Role, true))
            : new SignInResult(SignInOutcome.Wrong)));
    }

    public Task SignOutAsync(CancellationToken ct) => Task.FromResult(Answer("SignOut", 0));
    public Task<SignedInUser?> WhoAmIAsync(CancellationToken ct) => Task.FromResult(Answer("WhoAmI", Who));
    public Task<IReadOnlyList<ProductInfo>> ListProductsAsync(CancellationToken ct) => Task.FromResult(Answer("ListProducts", Products));

    public RunCounters Counters { get; set; } = new(0, 0, 0, 0, 0, 0);
    /// <summary>Full images by id; an id not here answers null (no longer kept).</summary>
    public Dictionary<string, FullImage> Images { get; } = [];

    public Task<RunCounters> GetCountersAsync(CancellationToken ct) => Task.FromResult(Answer("GetCounters", Counters));

    public Task<FullImage?> GetImageAsync(string imageId, CancellationToken ct) =>
        Task.FromResult(Answer($"GetImage {imageId}", Images.GetValueOrDefault(imageId)));

    public Task<CommandResult> ChangeProductAsync(ProductInfo product, CancellationToken ct) =>
        Task.FromResult(Answer($"Change {product.Name}", ChangeResult));

    public Task<CommandResult> StartAsync(CancellationToken ct) => Task.FromResult(Answer("Start", StartResult));
    public Task<CommandResult> StopAsync(CancellationToken ct) => Task.FromResult(Answer("Stop", CommandResult.Done));
    public CommandResult PlcTestResult { get; set; } = CommandResult.Done;
    public Task<CommandResult> PlcTestSignalAsync(bool ok, CancellationToken ct) =>
        Task.FromResult(Answer(ok ? "PlcTest OK" : "PlcTest NG", PlcTestResult));

    // --- job designer and Start batch -----------------------------------------------------------

    /// <summary>Job files by name; GetJob answers with them, SaveJob stores into them.</summary>
    public Dictionary<string, string> JobFiles { get; } = [];
    public IReadOnlyList<string> PictureNames { get; set; } = ["good1.bmp", "good2.bmp", "bad1.bmp"];
    /// <summary>What a try answers: by picture name, else <see cref="TryDefault"/>.</summary>
    public Dictionary<string, TryOutcome> TryAnswers { get; } = [];
    public TryOutcome TryDefault { get; set; } = new(true, true, 12, [1, 2, 3], 640, 480, 0.5, 1280, 960,
        [new ToolTried("Read code 1", "barcode", true, "Read: ABC", 9)], [], [], []);
    /// <summary>What a try answers when this gives one (a teach that depends on the tool's area); else as above.</summary>
    public Func<TryRequest, TryOutcome?>? TryWith { get; set; }
    public SaveOutcome? SaveAnswer { get; set; }
    public List<TryRequest> Tries { get; } = [];
    public List<string> Saved { get; } = [];
    public Dictionary<string, string> BatchSet { get; } = [];
    public CommandResult BatchResult { get; set; } = CommandResult.Done;

    public Task<JobFile> GetJobAsync(string name, CancellationToken ct) =>
        Task.FromResult(Answer($"GetJob {name}", JobFiles.TryGetValue(name, out var j) ? new JobFile(true, j, 3) : new JobFile(false, Refused: new CommandResult(false, null, "No such job."))));

    public Task<SaveOutcome> SaveJobAsync(string json, CancellationToken ct)
    {
        Saved.Add(json);
        var name = System.Text.Json.Nodes.JsonNode.Parse(json)!["Name"]!.ToString();
        if (SaveAnswer is { Ok: false } no) return Task.FromResult(Answer("SaveJob", no));
        JobFiles[name] = json;
        return Task.FromResult(Answer("SaveJob", SaveAnswer ?? new SaveOutcome(true, name, 4)));
    }

    public Task<PictureList> ListPicturesAsync(string json, int camera, CancellationToken ct) =>
        Task.FromResult(Answer($"ListPictures {camera}", new PictureList(true, PictureNames)));

    public Task<TryOutcome> TryJobAsync(TryRequest request, CancellationToken ct)
    {
        Tries.Add(request);
        if (TryWith?.Invoke(request) is { } scripted) return Task.FromResult(Answer($"Try {request.Stored}", scripted));
        return Task.FromResult(Answer($"Try {request.Stored}", request.Stored is { } s && TryAnswers.TryGetValue(s, out var a) ? a : TryDefault));
    }

    public Task<CommandResult> AskChangeoverAgainAsync(CancellationToken ct) => Task.FromResult(Answer("AskChangeoverAgain", BatchResult));

    /// <summary>What the app sent to accept a changeover at the line PC: user, kind, secret, code.</summary>
    public List<(string User, string Kind, string Secret, string Code)> Accepts { get; } = [];
    public CommandResult AcceptResult { get; set; } = CommandResult.Done;

    public Task<CommandResult> AcceptChangeoverAtLineAsync(string user, string secretKind, string secret, string code, CancellationToken ct)
    {
        Accepts.Add((user, secretKind, secret, code));
        return Task.FromResult(Answer("AcceptChangeover", AcceptResult));
    }

    /// <summary>What the app answered to remote service requests: id, yes or no, user, kind, secret.</summary>
    public List<(string Id, bool Approve, string User, string Kind, string Secret)> RemoteAnswers { get; } = [];
    public CommandResult RemoteAnswerResult { get; set; } = CommandResult.Done;

    public Task<CommandResult> AnswerRemoteAsync(string id, bool approve, string user, string secretKind, string secret, CancellationToken ct)
    {
        RemoteAnswers.Add((id, approve, user, secretKind, secret));
        return Task.FromResult(Answer("AnswerRemote", RemoteAnswerResult));
    }

    /// <summary>What Grab answers; its picture is added to <see cref="PictureNames"/> when it is a yes.</summary>
    public GrabOutcome GrabAnswer { get; set; } = new(true, "grab-1.bmp");
    /// <summary>What Find tools answers.</summary>
    public FindOutcome FindAnswer { get; set; } = new(true, []);
    public List<(string Stored, (int X, int Y, int Width, int Height)? Area)> Finds { get; } = [];

    public Task<GrabOutcome> GrabPictureAsync(string json, int camera, CancellationToken ct)
    {
        if (GrabAnswer.Ok) PictureNames = [.. PictureNames, GrabAnswer.Name];
        return Task.FromResult(Answer($"Grab {camera}", GrabAnswer.Json.Length > 0 ? GrabAnswer : GrabAnswer with { Json = json }));
    }

    public Task<FindOutcome> FindToolsAsync(string json, int camera, string stored, (int X, int Y, int Width, int Height)? area, CancellationToken ct)
    {
        Finds.Add((stored, area));
        return Task.FromResult(Answer($"Find {stored}", FindAnswer));
    }

    /// <summary>What Auto tolerance answers, and what it was asked: tool and pictures.</summary>
    public BlobToleranceOutcome ToleranceAnswer { get; set; } = new(true, true, 0.9f, 1.2f, "px", 3, 0.4f, 0.6f);
    public List<(string Tool, IReadOnlyList<string> Pictures)> Tolerances { get; } = [];

    public Task<BlobToleranceOutcome> SuggestBlobToleranceAsync(string json, int camera, string tool, IReadOnlyList<string> pictures, CancellationToken ct)
    {
        Tolerances.Add((tool, pictures.ToArray()));
        return Task.FromResult(Answer($"Tolerance {tool}", ToleranceAnswer));
    }

    public Task<CommandResult> SetBatchAsync(string key, string value, CancellationToken ct)
    {
        if (BatchResult.Ok) BatchSet[key] = value;
        return Task.FromResult(Answer($"SetBatch {key}={value}", BatchResult));
    }

    // --- NG review and learning ---------------------------------------------------------------

    /// <summary>Image ids the runtime finds clearly bad (an Accepted is answered LooksBad unless confirmed).</summary>
    public HashSet<string> LooksBad { get; } = [];
    public List<(string Id, ReviewDecision Decision, bool Confirm)> Reviews { get; } = [];
    public LearnOutcome LearnAnswer { get; set; } = new(CommandResult.Done, "p1",
        [new TunedSettingInfo("Batch", "strictness", "60", "52", true)], "1 of 3 good parts pass, all 5 bad parts fail",
        "3 of 3 good parts pass, all 5 bad parts fail", true, "", [], 3, 5);
    public ApplyOutcome ApplyAnswer { get; set; } = new(CommandResult.Done, "Whisky 750 ml", 4);

    /// <summary>Image ids the runtime could not read (an Accepted is answered Unreadable unless validated again).</summary>
    public HashSet<string> Unreadable { get; } = [];
    /// <summary>Image ids the software passed (a rejection is a possible escape).</summary>
    public HashSet<string> Passed { get; } = [];
    public List<(string Id, ReviewDecision Decision, ReviewConfirm Confirm)> Decisions { get; } = [];

    public Task<ReviewOutcome> ReviewPartAsync(string imageId, ReviewDecision decision, ReviewConfirm confirm, string note, CancellationToken ct)
    {
        var confirmLooksBad = confirm.HasFlag(ReviewConfirm.LooksBad);
        if (decision == ReviewDecision.Accepted && LooksBad.Contains(imageId) && !confirmLooksBad)
            return Task.FromResult(Answer($"Review {imageId} asked", new ReviewOutcome(new CommandResult(false, "runtime.review.looksBad"), true,
                ["Batch: a character is wrong"], false, [])));
        if (decision == ReviewDecision.Accepted && Unreadable.Contains(imageId) && (confirm & (ReviewConfirm.Unreadable | ReviewConfirm.KeepAsRecord)) == 0)
            return Task.FromResult(Answer($"Review {imageId} unreadable", new ReviewOutcome(new CommandResult(false, "runtime.review.unreadable"), false,
                ["Read code: explain.code.none"], false, []) { Unreadable = true }));
        Reviews.Add((imageId, decision, confirmLooksBad));
        // as the runtime lists reviewed pictures: the newest decision first
        if (ReviewedItems.FindIndex(p => p.ImageId == imageId) is var at and >= 0)
        {
            var changed = ReviewedItems[at];
            ReviewedItems.RemoveAt(at);
            ReviewedItems.Insert(0, changed with { Reviewed = changed.Reviewed! with { Decision = decision } });
        }
        Decisions.Add((imageId, decision, confirm));
        // as the runtime: a clearly bad part accepted, or one kept as a record, is a record only
        var learned = decision == ReviewDecision.ConfirmedNg || (!LooksBad.Contains(imageId) && !confirm.HasFlag(ReviewConfirm.KeepAsRecord));
        int ok = Decisions.Count(r => r.Decision == ReviewDecision.Accepted && !LooksBad.Contains(r.Id) && !r.Confirm.HasFlag(ReviewConfirm.KeepAsRecord));
        int ng = Decisions.Count(r => r.Decision == ReviewDecision.ConfirmedNg);
        return Task.FromResult(Answer($"Review {imageId} {decision}", new ReviewOutcome(CommandResult.Done, false, [], learned,
            [new CameraReviewCounts(0, "", ok, ng, Decisions.Count - ok - ng)]) { PossibleEscape = decision == ReviewDecision.ConfirmedNg && Passed.Contains(imageId) }));
    }

    public List<SortPicture> ToSort { get; } = [];

    public Task<SortList> ListToSortAsync(int camera, CancellationToken ct) =>
        Task.FromResult(Answer($"ListToSort {camera}", new SortList(CommandResult.Done,
            ToSort.Where(p => !Decisions.Any(d => d.Id == p.ImageId)).ToList(), ToSort.Count(p => !Decisions.Any(d => d.Id == p.ImageId)))));

    /// <summary>The reviewed list (AS-04j): a removal takes the picture out.</summary>
    public List<SortPicture> ReviewedItems { get; } = [];
    public List<WasOkMark> WasOk { get; } = [];
    public List<string> Removed { get; } = [];
    /// <summary>The answer to FlagWasOk (refused: as for an operator not signed in).</summary>
    public CommandResult FlagResult { get; set; } = CommandResult.Done;

    public Task<ReviewedList> ListReviewedAsync(int camera, CancellationToken ct) =>
        Task.FromResult(Answer($"ListReviewed {camera}", new ReviewedList(CommandResult.Done, ReviewedItems.ToList(), ReviewedItems.Count, WasOk.ToList())));

    public Task<ReviewOutcome> RemoveReviewAsync(string imageId, CancellationToken ct)
    {
        if (ReviewedItems.RemoveAll(p => p.ImageId == imageId) == 0)
            return Task.FromResult(Answer($"RemoveReview {imageId} refused", new ReviewOutcome(new CommandResult(false, "runtime.refused.notReviewed"), false, [], false, [])));
        Removed.Add(imageId);
        return Task.FromResult(Answer($"RemoveReview {imageId}", new ReviewOutcome(CommandResult.Done, false, [], false, [new CameraReviewCounts(0, "", 0, 0, 0)])));
    }

    public Task<CommandResult> FlagWasOkAsync(string imageId, string note, CancellationToken ct)
    {
        if (FlagResult.Ok) WasOk.Add(new WasOkMark(imageId, 0, "op", DateTimeOffset.UtcNow));
        return Task.FromResult(Answer($"FlagWasOk {imageId}", FlagResult));
    }

    public LearningSettingsInfo LearningSettings { get; set; } = new(true, 5, "", null);

    public Task<LearningSettingsInfo?> GetLearningSettingsAsync(CancellationToken ct) =>
        Task.FromResult<LearningSettingsInfo?>(Answer("GetLearningSettings", LearningSettings));

    public Task<CommandResult> SetLearningSettingsAsync(bool enabled, int idleMinutes, CancellationToken ct)
    {
        if (idleMinutes is < 2 or > 60) return Task.FromResult(Answer("SetLearningSettings refused", new CommandResult(false, "runtime.refused.idleMinutes")));
        LearningSettings = new LearningSettingsInfo(enabled, idleMinutes, "sup", DateTimeOffset.UtcNow);
        return Task.FromResult(Answer($"SetLearningSettings {enabled} {idleMinutes}", CommandResult.Done));
    }

    public IReadOnlyList<CameraReviewCounts> ReviewCameras { get; set; } = [new CameraReviewCounts(0, "", 0, 0, 0)];

    public Task<IReadOnlyList<CameraReviewCounts>> GetReviewsAsync(CancellationToken ct) =>
        Task.FromResult(Answer("GetReviews", ReviewCameras));

    public async Task<LearnOutcome> LearnAsync(int camera, CancellationToken ct)
    {
        var answer = Answer($"Learn {camera}", LearnAnswer);
        if (LearnHangs) await Task.Delay(Timeout.Infinite, ct);
        return answer;
    }

    public Task<ApplyOutcome> ApplyLearningAsync(string proposalId, CancellationToken ct) =>
        Task.FromResult(Answer($"Apply {proposalId}", ApplyAnswer));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        StatusStream.Writer.TryComplete();
        LiveStream.Writer.TryComplete();
        ResultStream.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A link the test sets by hand, for view-model tests.</summary>
internal sealed class ManualLink : IRuntimeLink
{
    public LinkState State { get; private set; } = LinkState.Connecting;
    public IRuntimeClient? Client { get; private set; }
    public int Broken { get; private set; }
    public event Action<LinkState>? StateChanged;
    public event Action<RuntimeStatus>? StatusReceived;
    public event Action<LiveFrame>? FrameReceived;
    public event Action<PartResult>? ResultReceived;

    public void Connect(IRuntimeClient client, RuntimeStatus status)
    {
        Client = client;
        State = LinkState.Connected;
        StateChanged?.Invoke(State);
        StatusReceived?.Invoke(status);
    }

    public void Drop()
    {
        Client = null;
        State = LinkState.NotConnected;
        StateChanged?.Invoke(State);
    }

    public void Push(RuntimeStatus status) => StatusReceived?.Invoke(status);
    public void Push(LiveFrame frame) => FrameReceived?.Invoke(frame);
    public void Push(PartResult result) => ResultReceived?.Invoke(result);
    public void ReportBroken(IRuntimeClient client) => Broken++;
}

/// <summary>A clock the test moves.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Stands in for a decoded JPEG in view-model tests (no rendering platform needed).</summary>
internal sealed class FakeImage : IImage
{
    public Size Size => new(10, 10);
    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) { }
}

internal static class Make
{
    public static readonly Strings Strings = Strings.Instance;

    public static RuntimeStatus Running(long good = 0, long bad = 0) => new()
    {
        State = LineState.Running,
        Product = new ProductRef("Whisky 750 ml", ""),
        Counters = new Counters(good, bad),
    };

    public static RuntimeStatus Stopped() => Running() with { State = LineState.Stopped };

    /// <summary>A batch as the Start batch screen leaves it: dates and MRP entered.</summary>
    public static readonly IReadOnlyList<BatchEntry> Batch =
        [new("BATCH", "B.NO", "B2410-17", "B2410-17"), new("MFG", "MFG", "10/2026", "10/2026"),
         new("EXP", "EXP", "09/2028", "09/2028"), new("MRP", "MRP", "145.00", "RS.145.00")];

    /// <summary>A live PLC link whose last part, just now, was OK.</summary>
    public static PlcSignals Plc => new(true, true, true, 48213, DateTimeOffset.UtcNow, true, "ModbusTcp :502");

    /// <summary>Six tools, one failed: fills "This picture".</summary>
    public static readonly IReadOnlyList<ToolOutcome> SixTools =
        [new("Locate carton", true), new("Read code 1", true), new("Read code 2", false), new("Check text 1", true, 78),
         new("Size", true), new("Same code", true)];

    public static LiveFrame Frame(Verdict verdict, ulong seq = 1) =>
        new(seq, DateTimeOffset.Now, [1, 2, 3], 800, 600, verdict,
            [new ResultGraphic(GraphicKind.Polygon, [new(10, 10), new(90, 10), new(90, 90), new(10, 90)], verdict, "Code")]);

    public static PartResult Part(ulong id, Verdict verdict = Verdict.Ok, string reason = "") =>
        new(id, new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero).AddSeconds(id), "Whisky 750 ml",
            $"CASE{id:000}", verdict, 41, reason);

    /// <summary>The repository root (holds design/tokens), found from the test's folder.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "design", "tokens"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }
}
