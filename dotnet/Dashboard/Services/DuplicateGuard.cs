using System.Text.Json;

namespace BarcodeReading.Dashboard.Services;

/// <summary>How many codes the duplicate check remembers (set on the Admin page).</summary>
public sealed record DuplicateSettings(int ChildCodes = 10_000, int CaseCodes = 1_000)
{
    public const int Max = 1_000_000;
}

/// <summary>What the duplicate check found on one case.</summary>
public sealed record DuplicateCheck(IReadOnlyList<string> Children, bool Parent)
{
    public static readonly DuplicateCheck None = new(Array.Empty<string>(), false);
    public bool Any => Parent || Children.Count > 0;
}

/// <summary>
/// Catches re-used or copied labels: a child (hologram) code or a parent (case) code that was
/// already on one of the last accepted cases. Only accepted cases are remembered, so a rejected
/// case run through again is not flagged. Rebuilt from the case log at start and after the
/// sizes change, so a restart forgets nothing.
/// </summary>
public sealed class DuplicateGuard
{
    private readonly CaseLog _log;
    private readonly string _path;
    private readonly object _lock = new();
    private readonly DuplicateMemory _children = new(0), _cases = new(0);
    private DuplicateSettings _settings;

    public DuplicateGuard(CaseLog log, string dataFolder)
    {
        _log = log;
        _path = Path.Combine(dataFolder, "duplicates.json");
        _settings = LoadSettings();
        Rebuild();
    }

    public DuplicateSettings Settings
    {
        get { lock (_lock) return _settings; }
    }

    public (int Children, int Cases) Remembered => (_children.Count, _cases.Count);

    public DuplicateCheck Check(IEnumerable<string> children, string? parent)
    {
        lock (_lock) return new(_children.Duplicates(children), parent is not null && _cases.Contains(parent));
    }

    /// <summary>Remembers the codes of an accepted case.</summary>
    public void Accept(IEnumerable<string> children, string? parent)
    {
        lock (_lock)
        {
            _children.Add(children);
            if (parent is not null) _cases.Add(parent);
        }
    }

    public void Update(DuplicateSettings s, string user)
    {
        if (s.ChildCodes < 0 || s.ChildCodes > DuplicateSettings.Max || s.CaseCodes < 0 || s.CaseCodes > DuplicateSettings.Max)
            throw new ArgumentException($"counts must be 0 to {DuplicateSettings.Max:N0}");
        DuplicateSettings old;
        lock (_lock)
        {
            old = _settings;
            _settings = s;
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, _path, overwrite: true);
        }
        Rebuild();
        _log.Audit(user, "duplicate-settings",
            $"child codes {old.ChildCodes:N0} -> {s.ChildCodes:N0}, case codes {old.CaseCodes:N0} -> {s.CaseCodes:N0}");
    }

    private void Rebuild()
    {
        var s = Settings;
        var children = _log.RecentAcceptedChildCodes(s.ChildCodes);
        var cases = _log.RecentAcceptedParentCodes(s.CaseCodes);
        lock (_lock)
        {
            _children.Clear();
            _children.Capacity = s.ChildCodes;
            _children.Add(children);
            _cases.Clear();
            _cases.Capacity = s.CaseCodes;
            _cases.Add(cases);
        }
    }

    private DuplicateSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<DuplicateSettings>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException)
        {
            // a damaged file falls back to the defaults; the next save rewrites it
        }
        return new();
    }
}
