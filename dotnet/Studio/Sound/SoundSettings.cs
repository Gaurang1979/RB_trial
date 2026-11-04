namespace VisionSystem.Studio.Sound;

/// <summary>What sounds play, and how loud (requirements 17.15 NXT-04, NXT-05). Good parts are silent
/// by default; every other sound and spoken alarms are on.</summary>
public sealed record SoundSettings
{
    /// <summary>Master volume from 0 (silent) to 1.</summary>
    public double Volume { get; init; } = 0.7;

    /// <summary>The sounds turned off; a sound not listed is on, except the good-part tick (below).</summary>
    public IReadOnlySet<SoundKind> Off { get; init; } = new HashSet<SoundKind> { SoundKind.GoodPart };

    public bool SpokenAlarms { get; init; } = true;

    public bool IsOn(SoundKind kind) => !Off.Contains(kind);

    public SoundSettings With(SoundKind kind, bool on)
    {
        var off = new HashSet<SoundKind>(Off);
        if (on) off.Remove(kind);
        else off.Add(kind);
        return this with { Off = off };
    }

    public static SoundSettings Default { get; } = new();

    /// <summary>Nothing plays (headless tests).</summary>
    public static SoundSettings Silent { get; } = new() { Volume = 0, SpokenAlarms = false };
}
