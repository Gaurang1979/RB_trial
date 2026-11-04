namespace VisionSystem.Studio.Runtime;

/// <summary>
/// One connection to the runtime service. Calls throw when the link drops; the
/// <see cref="RuntimeLink"/> then reconnects. The runtime decides what each user may do
/// (ARCHITECTURE.md 2.2); the app only asks and shows the answer.
/// </summary>
public interface IRuntimeClient : IAsyncDisposable
{
    Task<RuntimeStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Status now and after each change. Ends or throws when the link drops.</summary>
    IAsyncEnumerable<RuntimeStatus> WatchStatusAsync(CancellationToken ct);

    /// <summary>Live frames for display, at most about 10 per second, newest only.</summary>
    IAsyncEnumerable<LiveFrame> WatchLiveAsync(CancellationToken ct);

    /// <summary>Every inspected part's result, as it is made. Ends or throws when the link drops.</summary>
    IAsyncEnumerable<PartResult> WatchResultsAsync(CancellationToken ct);

    Task<IReadOnlyList<UserTile>> ListUsersAsync(CancellationToken ct);
    Task<SignInResult> SignInWithPinAsync(string userId, string pin, CancellationToken ct);
    /// <param name="code">Today's code that opens a Support or OEM role (AS-04h); null or empty for everyone else.</param>
    Task<SignInResult> SignInWithPasswordAsync(string userId, string password, string? code, CancellationToken ct);
    /// <summary>The number a badge reader typed; the runtime finds whose card it is.</summary>
    Task<SignInResult> SignInWithCardAsync(string card, CancellationToken ct);
    Task SignOutAsync(CancellationToken ct);
    /// <summary>The signed-in user of this app's session, or null for nobody (Operator).</summary>
    Task<SignedInUser?> WhoAmIAsync(CancellationToken ct);

    Task<IReadOnlyList<ProductInfo>> ListProductsAsync(CancellationToken ct);
    /// <summary>Yield and cycle time of the batch.</summary>
    Task<RunCounters> GetCountersAsync(CancellationToken ct);
    /// <summary>A full image by its id (an NG part, a product picture), or null when the runtime no longer has it.</summary>
    Task<FullImage?> GetImageAsync(string imageId, CancellationToken ct);
    Task<CommandResult> ChangeProductAsync(ProductInfo product, CancellationToken ct);
    Task<CommandResult> StartAsync(CancellationToken ct);
    Task<CommandResult> StopAsync(CancellationToken ct);
    /// <summary>The PLC test signal (PLC-INTERFACE.md section 7): OK or NG with Done for 2 s, only in
    /// Teach. The runtime refuses it for anyone but an Engineer or Admin, and while the line runs, the
    /// PLC sets RunEnable or a part is handled, and says why.</summary>
    Task<CommandResult> PlcTestSignalAsync(bool ok, CancellationToken ct);
}

/// <summary>Opens a connection to the runtime; throws when it cannot be reached.</summary>
public interface IRuntimeConnector
{
    Task<IRuntimeClient> ConnectAsync(CancellationToken ct);
}
