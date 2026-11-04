using System.Globalization;
using System.Text;

namespace VisionStudio.Services;

/// <summary>
/// The camera names sent to the plant server with a part's images: the server takes any printable name (Python's
/// <c>str.isprintable</c>) of 1 to 64 characters other than "." and "..", and an image id holds one image per name,
/// so two cameras must never share one.
/// </summary>
public static class CameraNames
{
    public const int MaxLength = 64;

    /// <summary>
    /// One name per camera of a job, in order: every character that is not printable replaced by "_" (control, format
    /// such as a zero-width space or a byte order mark, an unpaired surrogate, private use, unassigned, line and
    /// paragraph separators) and every space separator (a no-break or ideographic space) by an ordinary space, then
    /// trimmed and cut to 64 characters; an empty name, "." or ".." becomes "Camera {n}" (n counts from 1); a name already used by an earlier camera gets " (2)",
    /// " (3)" and so on (case ignored, since the images are saved as files named after them).
    /// </summary>
    public static string[] ForJob(IReadOnlyList<string?> names)
    {
        var result = new string[names.Count];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            var name = Clean(names[i]);
            if (name.Length == 0 || name is "." or "..") name = $"Camera {i + 1}";
            var candidate = name;
            for (var n = 2; !used.Add(candidate); n++)
            {
                var suffix = $" ({n})";
                candidate = Cut(name, MaxLength - suffix.Length).TrimEnd() + suffix;
            }
            result[i] = candidate;
        }
        return result;
    }

    static string Clean(string? s)
    {
        if (s is null) return "";
        s = s.Trim();   // outer white space first (tabs and line breaks included), so only inner ones become "_"
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var pair = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);   // one code point; a lone half is "Surrogate"
            switch (CharUnicodeInfo.GetUnicodeCategory(s, i))
            {
                case UnicodeCategory.SpaceSeparator: sb.Append(' '); break;
                case UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                    or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator:
                    sb.Append('_'); break;
                default:
                    sb.Append(s[i]);
                    if (pair) sb.Append(s[i + 1]);
                    break;
            }
            if (pair) i++;
        }
        return Cut(sb.ToString().Trim(), MaxLength).Trim();
    }

    // Never splits a surrogate pair.
    static string Cut(string s, int max) =>
        s.Length <= max ? s : char.IsHighSurrogate(s[max - 1]) ? s[..(max - 1)] : s[..max];
}
