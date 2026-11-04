namespace VisionStudio.Services;

/// <summary>One entry of the Run screen's NG filmstrip: when, why, and whether it is the newest.</summary>
public sealed record FilmstripItem(string Time, string Reason, bool IsActive);

/// <summary>
/// What the Run screen may show from live data only (Req 6.1 RUN-LIVE-01): the NG filmstrip from the inspections
/// seen, and the extra tabs only when the runtime supplies their data. Never sample text, numbers or temperatures.
/// </summary>
public static class RunLive
{
    public const int FilmstripKeep = 4;

    /// <summary>The newest NG inspections first (at most <see cref="FilmstripKeep"/>); the newest is the active one. None gives none.</summary>
    public static FilmstripItem[] Filmstrip(IEnumerable<(string Time, string Reason)> newestFirst) =>
        newestFirst.Take(FilmstripKeep).Select((n, i) => new FilmstripItem(n.Time, n.Reason, i == 0)).ToArray();

    /// <summary>The first failing tool's name, "Error" for a camera or link error, "NG" when no tool says why.</summary>
    public static string ReasonOf(string? error, string? firstFailingTool) =>
        error is not null ? "Error" : firstFailingTool ?? "NG";

    /// <summary>The Run screen's tabs: the cockpit always; SplitWipe, Tooling Matrix and Process Stability Map only
    /// with their live feed (an NG/golden image pair, sealing-zone temperatures, latent-space points).</summary>
    public static string[] Tabs(bool imagePair, bool zoneTemperatures, bool latentPoints)
    {
        var tabs = new List<string> { "cockpit" };
        if (imagePair) tabs.Add("splitwipe");
        if (zoneTemperatures) tabs.Add("tooling");
        if (latentPoints) tabs.Add("haystack");
        return tabs.ToArray();
    }
}
