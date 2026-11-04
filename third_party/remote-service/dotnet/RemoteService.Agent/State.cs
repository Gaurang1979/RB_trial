using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sundaram.RemoteService;

/// <summary>
/// What the agent must remember across restarts (<see cref="RemoteServiceOptions.DataFolder"/>/rs-state.json): the newest
/// package version ever installed and the lowest version allowed (docs/COMPARABLE-PROJECTS.md item 1). Written whole, in one
/// step. Keep the folder where only the host's service account can write: whoever can edit this file can lower the floor.
/// </summary>
public sealed class AgentState
{
    public const string FileName = "rs-state.json";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly string? _path;
    readonly object _lock = new();

    [JsonPropertyName("highestVersion")] public string? HighestVersion { get; set; }
    [JsonPropertyName("floorVersion")] public string? FloorVersion { get; set; }

    AgentState(string? path) => _path = path;

    /// <summary>The state in <paramref name="folder"/>, or one in memory only (null folder: tests). A damaged file is an error,
    /// never an empty state: an empty state would let an old package back in.</summary>
    public static AgentState Load(string? folder)
    {
        if (folder is null) return new AgentState(null);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var s = new AgentState(path);
        if (File.Exists(path))
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(path)) ?? throw new InvalidDataException($"{FileName} is empty");
            s.HighestVersion = stored.HighestVersion;
            s.FloorVersion = stored.FloorVersion;
        }
        return s;
    }

    public bool Persistent => _path is not null;

    /// <summary>Records an installed version: the highest only goes up, the floor only goes up.</summary>
    public void Installed(string version, string? minVersion)
    {
        lock (_lock)
        {
            if (HighestVersion is null || VersionOrder.Compare(version, HighestVersion) > 0) HighestVersion = version;
            if (minVersion is { Length: > 0 } && (FloorVersion is null || VersionOrder.Compare(minVersion, FloorVersion) > 0)) FloorVersion = minVersion;
            Save();
        }
    }

    /// <summary>The file's content.</summary>
    sealed record Stored([property: JsonPropertyName("highestVersion")] string? HighestVersion, [property: JsonPropertyName("floorVersion")] string? FloorVersion);

    void Save()
    {
        if (_path is null) return;
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(new Stored(HighestVersion, FloorVersion), Json));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>Orders versions like "2.5.0", "v2.5", "2.5.0-rc1" (a pre-release comes before its release). Null when either
/// cannot be read as a version: then nothing is assumed about their order.</summary>
public static class VersionOrder
{
    public static int? Compare(string a, string b)
    {
        if (Parse(a) is not { } x || Parse(b) is not { } y) return null;
        var c = x.Number.CompareTo(y.Number);
        if (c != 0) return c;
        if (x.Pre.Length == 0 && y.Pre.Length == 0) return 0;
        if (x.Pre.Length == 0) return 1;    // a release comes after its pre-releases
        if (y.Pre.Length == 0) return -1;
        return Math.Sign(string.CompareOrdinal(x.Pre, y.Pre));
    }

    static (Version Number, string Pre)? Parse(string s)
    {
        s = s.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var (num, pre) = dash >= 0 ? (s[..dash], s[(dash + 1)..]) : (s, "");
        if (!num.Contains('.')) num += ".0";
        // padded to four parts: "1.0" and "1.0.0" are the same version
        return Version.TryParse(num, out var v) ? (new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)), pre) : null;
    }
}
