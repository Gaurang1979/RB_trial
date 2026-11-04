namespace StationAgent;

/// <summary>
/// The connection to the plant server (in production a TLS link; a fake in
/// tests). The station agent is its only caller.
/// </summary>
public interface IPlantLink
{
    /// <summary>
    /// Sends a batch of stored events, in sequence order, as their JSON.
    /// Returns the highest sequence number N in this batch such that N and
    /// every earlier event of the batch are stored by the server (the
    /// server keys them by station, queue_id and seq, so a resend is
    /// harmless). Below the batch's first number means none was stored. Never
    /// above the batch's last: the agent treats that as a broken server and
    /// marks nothing sent. Throws when the server cannot be reached.
    /// </summary>
    Task<long> SendAsync(IReadOnlyList<QueuedEvent> batch, CancellationToken ct);

    /// <summary>
    /// Sends a heartbeat's JSON; it is not stored and not retried. Returns the image ids the server
    /// asks for (its answer's "image_requests", up to 10) and the state of the station's changeover
    /// check; an answer without them, or that is not JSON (an older server), gives an empty list and
    /// no changeover answer, and never fails the heartbeat.
    /// </summary>
    Task<HeartbeatAnswer> SendHeartbeatAsync(string json, CancellationToken ct);

    /// <summary>
    /// Sends one camera's reduced JPEG of a part (at most 300 KB) the server asked for; sent again, it
    /// replaces the earlier one. Throws when the server refuses or cannot be reached; a refusal is an
    /// <see cref="HttpRequestException"/> whose <c>StatusCode</c> is set (400 or 413: this image can never
    /// be accepted, the agent does not retry it), while an unreachable server has no status.
    /// </summary>
    Task UploadImageAsync(string imageId, string camera, byte[] jpeg, CancellationToken ct);

    /// <summary>Tells the server every image it can have of this part has been sent (same exceptions as <see cref="UploadImageAsync"/>).</summary>
    Task ImageDoneAsync(string imageId, CancellationToken ct);

    /// <summary>Tells the server this station no longer has the part's images, so it stops asking.</summary>
    Task ImageMissingAsync(string imageId, CancellationToken ct);
}

/// <summary>What the plant server answered to a heartbeat.</summary>
public sealed record HeartbeatAnswer(IReadOnlyList<string> ImageRequests, ChangeoverAnswer? Changeover = null)
{
    public static HeartbeatAnswer Empty { get; } = new([]);
}

/// <summary>
/// The plant server's state of a changeover check (CHANGEOVER-VERIFY.md section 2): whether the check is on
/// for the line, the request it is about (code and ask), and "pending", "accepted", "refused" or "not_needed".
/// </summary>
public sealed record ChangeoverAnswer(bool Required, string Code, int Ask, string State, string? By = null, long? At = null, string? Note = null)
{
    public const string Pending = "pending", Accepted = "accepted", Refused = "refused", NotNeeded = "not_needed";
}

/// <summary>What an <see cref="IImageSource"/> knows about a part's images.</summary>
public enum ImageState { NotYet, Missing, Found }

/// <summary>One camera's JPEG, already reduced to the size the server takes.</summary>
public sealed record CameraImage(string Camera, byte[] Jpeg);

/// <summary>The answer of <see cref="IImageSource.GetAsync"/>; use <see cref="NotYet"/>, <see cref="Missing"/> or <see cref="Found"/>.</summary>
public sealed record ImageAnswer(ImageState State, IReadOnlyList<CameraImage> Images)
{
    /// <summary>Still waiting for the disk writer: the agent skips it now, the server asks again.</summary>
    public static ImageAnswer NotYet { get; } = new(ImageState.NotYet, []);
    /// <summary>Not on this PC (never kept, or removed by the folder limit).</summary>
    public static ImageAnswer Missing { get; } = new(ImageState.Missing, []);
    public static ImageAnswer Found(IReadOnlyList<CameraImage> images) => images.Count > 0 ? new(ImageState.Found, images) : Missing;
}

/// <summary>
/// Where the agent gets a part's images when the server asks (the line application's saved NG
/// images and its encoder). Implementations must not throw for a missing image.
/// </summary>
public interface IImageSource
{
    Task<ImageAnswer> GetAsync(string imageId, CancellationToken ct);
}
