namespace VisionSystem.Studio.Runtime;

/// <summary>
/// Wait before reconnect attempt n (0-based): First, doubling up to Max, with up to
/// Jitter of the wait added at random so many studios do not retry in step. Never gives up.
/// </summary>
public sealed class Backoff(TimeSpan first, TimeSpan max, double jitter = 0.2, Random? random = null)
{
    public static Backoff Default() => new(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));

    private readonly Random _random = random ?? Random.Shared;

    public TimeSpan First { get; } = first;
    public TimeSpan Max { get; } = max;

    public TimeSpan Delay(int attempt)
    {
        var ms = First.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempt, 0, 30));
        ms = Math.Min(ms, Max.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms + ms * jitter * _random.NextDouble());
    }
}
