using System.Text;

namespace Sundaram.RemoteService;

/// <summary>
/// Paths inside a <see cref="FileRoot"/>, checked here and never trusted from the browser or the relay: a relative path of plain
/// names only; no "..", no drive or absolute path, no alternate data stream, no link or junction on the way.
/// </summary>
public static class SafePaths
{
    static readonly char[] Bad = ['\0', ':', '*', '?', '"', '<', '>', '|'];
    static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                                         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>The full path of <paramref name="relative"/> inside <paramref name="root"/>, or null when it is not allowed.
    /// "" is the root itself.</summary>
    public static string? Resolve(FileRoot root, string? relative)
    {
        var rel = (relative ?? "").Replace('\\', '/').Trim('/');
        var parts = rel.Length == 0 ? [] : rel.Split('/');
        foreach (var p in parts)
        {
            if (p.Length == 0 || p is "." or ".." || p.IndexOfAny(Bad) >= 0 || p.EndsWith('.') || p.EndsWith(' ') || p.Any(char.IsControl)
                || Reserved.Contains(Path.GetFileNameWithoutExtension(p).ToUpperInvariant()))
                return null;
        }
        var top = Path.GetFullPath(root.Folder);
        var full = Path.GetFullPath(Path.Combine([top, .. parts]));
        var prefix = top.EndsWith(Path.DirectorySeparatorChar) ? top : top + Path.DirectorySeparatorChar;
        if (full != top && !full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        // no link or junction anywhere below the root: it could point anywhere on the PC
        var walk = top;
        foreach (var p in parts)
        {
            walk = Path.Combine(walk, p);
            FileSystemInfo info = Directory.Exists(walk) ? new DirectoryInfo(walk) : new FileInfo(walk);
            if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))) return null;
        }
        return full;
    }

    /// <summary>The upload's extension is one the root takes (any, when it names none).</summary>
    public static bool ExtensionAllowed(FileRoot root, string path) =>
        root.Extensions is not { Count: > 0 } list || list.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public static class Csv
{
    /// <summary>A data view as CSV. A cell that a spreadsheet would run as a formula (=, +, -, @) is kept as text.</summary>
    public static string Write(DataTable t)
    {
        var sb = new StringBuilder();
        Line(sb, t.Columns);
        foreach (var r in t.Rows) Line(sb, r);
        return sb.ToString();
    }

    static void Line(StringBuilder sb, IEnumerable<string?> cells)
    {
        var first = true;
        foreach (var c in cells)
        {
            if (!first) sb.Append(',');
            first = false;
            var v = c ?? "";
            if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
            if (v.IndexOfAny([',', '"', '\n', '\r']) >= 0) v = "\"" + v.Replace("\"", "\"\"") + "\"";
            sb.Append(v);
        }
        sb.Append("\r\n");
    }
}
