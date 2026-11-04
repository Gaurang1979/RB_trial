using System.Text.Json;
using VisionCore;

namespace VisionStudio.Services;

/// <summary>
/// The Run page's NG image list links each saved NG image to the Teach page with that picture loaded
/// (<c>teach?ng=&lt;path under ng-images&gt;</c>). The link carries only a path under the NG image folder, and the
/// Teach page loads nothing else: no other file on the PC can be opened through it.
/// </summary>
public static class NgTeachLink
{
    /// <summary>The Teach page address for an NG image under <paramref name="root"/>; null for a file outside it.</summary>
    public static string? Href(string root, string path)
    {
        var rel = Relative(root, path);
        return rel is null ? null : "teach?ng=" + Uri.EscapeDataString(rel);
    }

    /// <summary>The image's path under <paramref name="root"/> with / between folders; null for a file outside it.</summary>
    public static string? Relative(string root, string path)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (rel == "." || Path.IsPathRooted(rel) || rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..")) return null;
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// The NG image a link names: a .bmp that exists under <paramref name="root"/>. Null for anything else
    /// (a path leaving the folder, a rooted path, another file type, a file since trimmed away).
    /// </summary>
    public static string? Resolve(string root, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains('\0')) return null;
        var parts = rel.Split('/', '\\');
        if (parts.Any(p => p is "" or "." or "..") || Path.IsPathRooted(rel)) return null;
        if (!rel.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) return null;
        var full = Path.GetFullPath(Path.Combine([Path.GetFullPath(root), .. parts]));
        if (Relative(root, full) is null || !File.Exists(full)) return null;
        return full;
    }

    /// <summary>The "job-camera" label the line saved with the image (its .json beside it); null when unreadable.</summary>
    public static string? Label(string bmpPath)
    {
        try
        {
            var meta = Path.ChangeExtension(bmpPath, ".json");
            if (!File.Exists(meta)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            return doc.RootElement.TryGetProperty("job", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// The job and camera an image was saved for: the line names it "&lt;job&gt;-&lt;camera&gt;" (Station), so the
    /// pair whose names join to the label exactly. Null when no job matches (renamed or deleted since).
    /// </summary>
    public static (Job Job, int Camera)? Owner(string? label, IEnumerable<Job> jobs)
    {
        if (string.IsNullOrEmpty(label)) return null;
        foreach (var j in jobs)
            for (int i = 0; i < j.Cameras.Count; i++)
                if ($"{j.Name}-{j.Cameras[i].Name}" == label) return (j, i);
        return null;
    }
}
