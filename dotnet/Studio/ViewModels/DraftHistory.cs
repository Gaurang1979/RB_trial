using System.Text.Json.Nodes;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// The job designer's undo and redo (UI-UX-SPEC.md principle 9 and 5 "Undo / Redo", requirement AS-18): the
/// draft as it was after each step, as JSON, so any change of the draft, by a command or in the property table,
/// is a step without each edit having its own command. A state the same as the current one is no step.
/// Typing into one setting is one step while it goes on: a typed change of the same single value within
/// <see cref="TypingMs"/> of the last one replaces that step instead of adding one.
/// </summary>
public sealed class DraftHistory(Func<long>? clock = null)
{
    /// <summary>Steps kept; the oldest go first.</summary>
    public const int Kept = 1000;
    /// <summary>A pause in typing longer than this starts a new step.</summary>
    public const long TypingMs = 2000;

    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly LinkedList<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string? _current;
    /// <summary>The one value the last typed step changed, and when; null when the last step was not typing.</summary>
    private string? _typedPath;
    private long _typedAt;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int Steps => _undo.Count;

    /// <summary>A job opened or made: no steps, this state is where undo stops.</summary>
    public void Reset(string? state)
    {
        _undo.Clear();
        _redo.Clear();
        _current = state;
        _typedPath = null;
    }

    /// <summary>The draft now; a step when it differs from the last state. Typing: the change was typed into a
    /// setting (merged with the last step when it changed only the same value, soon enough). True when recorded.</summary>
    public bool Record(string state, bool typing = false)
    {
        if (state == _current) return false;
        if (_current is null) { Reset(state); return false; }
        var now = _clock();
        var path = typing ? OnlyChange(_current, state) : null;
        if (path is not null && path == _typedPath && now - _typedAt <= TypingMs && _undo.Count > 0)
        {
            _current = state;   // the same setting still being typed: one step
            _typedAt = now;
            _redo.Clear();
            return true;
        }
        _undo.AddLast(_current);
        if (_undo.Count > Kept) _undo.RemoveFirst();
        _current = state;
        _redo.Clear();
        (_typedPath, _typedAt) = (path, now);
        return true;
    }

    /// <summary>The state before the last step, now current; null when there is none.</summary>
    public string? Undo()
    {
        if (_undo.Last is not { } last || _current is null) return null;
        _undo.RemoveLast();
        _redo.Push(_current);
        _typedPath = null;
        return _current = last.Value;
    }

    /// <summary>The state of the step last undone, now current; null when there is none.</summary>
    public string? Redo()
    {
        if (_redo.Count == 0 || _current is null) return null;
        _undo.AddLast(_current);
        _typedPath = null;
        return _current = _redo.Pop();
    }

    /// <summary>The steps to undo (oldest first), the state now, and the steps to redo (next first), to keep across a
    /// restart (<see cref="DraftHistoryStore"/>).</summary>
    public (IReadOnlyList<string> Undo, string? Current, IReadOnlyList<string> Redo) Export() =>
        ([.. _undo], _current, [.. _redo]);

    /// <summary>Steps kept from before a restart: as <see cref="Export"/> gave them; typing starts a step of its own.</summary>
    public void Restore(IEnumerable<string> undo, string current, IEnumerable<string> redo)
    {
        Reset(current);
        foreach (var s in undo.TakeLast(Kept)) _undo.AddLast(s);
        foreach (var s in redo.Reverse()) _redo.Push(s);
    }

    /// <summary>Where two drafts differ when they differ in one value only ("Cameras/0/Tools/1/Name"); null when
    /// they differ in more, in shape, or are not JSON.</summary>
    public static string? OnlyChange(string a, string b)
    {
        try
        {
            var found = new List<string>();
            return Diff(JsonNode.Parse(a), JsonNode.Parse(b), "", found) && found.Count == 1 ? found[0] : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Gathers the paths of values that differ; false when the shapes differ or more than one value does.</summary>
    private static bool Diff(JsonNode? a, JsonNode? b, string path, List<string> found)
    {
        switch (a, b)
        {
            case (JsonObject x, JsonObject y):
                if (x.Count != y.Count) return false;
                foreach (var (key, value) in x)
                    if (!y.TryGetPropertyValue(key, out var other) || !Diff(value, other, path + "/" + key, found)) return false;
                return true;
            case (JsonArray x, JsonArray y):
                if (x.Count != y.Count) return false;
                for (var i = 0; i < x.Count; i++)
                    if (!Diff(x[i], y[i], path + "/" + i, found)) return false;
                return true;
            case (JsonObject or JsonArray, _) or (_, JsonObject or JsonArray):
                return false;
            default:
                if (a?.ToJsonString() == b?.ToJsonString()) return true;
                found.Add(path);
                return found.Count == 1;
        }
    }
}
