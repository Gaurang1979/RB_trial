using System.Globalization;
using System.Text.Json;
using StationGuard;

namespace VisionStudio.Services;

/// <summary>One saved NG image for the Run page's list: its file name, local time, first NG reason, where it came
/// from (the "job-camera" label) and its path.</summary>
public sealed record NgImageEntry(string Name, DateTime At, string Reason, string Where, string Path);

/// <summary>
/// The newest saved NG images under the NG image folder, in both layouts: the naming pattern's
/// <c>&lt;root&gt;/L01/2026-10-31/J045/C1/NG/L01_C1_J045_261031-104512_000123.bmp</c> (owner, 2026-10-31) and the
/// older <c>&lt;root&gt;/2026-10-30/&lt;job-camera&gt;/&lt;HHmmss_fff&gt;_&lt;reason&gt;.bmp</c> or
/// <c>&lt;image_id&gt;_&lt;camera&gt;.bmp</c>, which stay where they are. Reads day folders newest first and stops
/// once it has enough; the .json beside an image is read only for the images listed.
/// </summary>
public static class NgImageList
{
    public static List<NgImageEntry> Newest(string root, int max)
    {
        var found = new List<NgImageEntry>();
        if (!Directory.Exists(root) || max <= 0) return found;
        static bool IsDay(string? name) => DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        var dayDirs = new List<(string Dir, string Day)>();
        foreach (var d in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(d);
            if (IsDay(name)) dayDirs.Add((d, name));
            else if (name is ['L', >= '0' and <= '9', >= '0' and <= '9'])
                foreach (var dd in Directory.EnumerateDirectories(d))
                    if (IsDay(Path.GetFileName(dd))) dayDirs.Add((dd, Path.GetFileName(dd)));
        }
        foreach (var day in dayDirs.GroupBy(d => d.Day).OrderByDescending(g => g.Key, StringComparer.Ordinal))
        {
            var date = DateTime.ParseExact(day.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var inDay = new List<(string Path, DateTime At, ParsedImageName P)>();
            foreach (var (dir, _) in day)
                foreach (var path in Directory.EnumerateFiles(dir, "*.bmp", SearchOption.AllDirectories))
                {
                    var p = ImageName.ParseAny(Path.GetFileNameWithoutExtension(path), date);
                    // the pattern's OK folders hold OK images (save mode All): not NG images
                    if (p.Form == ImageNameForm.Current && Path.GetFileName(Path.GetDirectoryName(path)) == "OK") continue;
                    inDay.Add((path, p.Time ?? date, p));
                }
            foreach (var (path, at, p) in inDay.OrderByDescending(x => x.At).Take(max - found.Count))
            {
                var (job, reason) = Meta(path);
                var where = p.Form == ImageNameForm.Current ? job ?? $"J{p.Name!.Value.Job:D3} {p.Camera}" : Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
                found.Add(new NgImageEntry(Path.GetFileName(path), at, p.Reason ?? reason ?? "", where, path));
            }
            if (found.Count >= max) break;
        }
        return found;
    }

    // The "job-camera" label and first NG reason the line wrote in the image's .json; none when it cannot be read.
    static (string? Job, string? Reason) Meta(string bmp)
    {
        try
        {
            var meta = Path.ChangeExtension(bmp, ".json");
            if (!File.Exists(meta)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            var job = root.TryGetProperty("job", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;
            var reason = root.TryGetProperty("reasons", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0
                && r[0].ValueKind == JsonValueKind.String ? r[0].GetString() : null;
            return (job, reason);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return (null, null); }
    }
}
