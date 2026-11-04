using System.Text.RegularExpressions;

namespace VisionCore.Reports;

/// <summary>Grade reports on disk: one folder under the data folder, plain file names only (the download route serves nothing else).</summary>
public static class ReportFiles
{
    private static readonly Regex Safe = new(@"^[A-Za-z0-9_.-]{1,120}\.pdf$", RegexOptions.Compiled);

    public static string Folder(string dataFolder) => Path.Combine(dataFolder, "reports");

    /// <summary>job_tool_yyyyMMdd_HHmmss_fff.pdf with every other character as "-".</summary>
    public static string Name(string job, string tool, DateTime when) =>
        $"{Clean(job)}_{Clean(tool)}_{when:yyyyMMdd_HHmmss_fff}.pdf";

    /// <summary>True for a name this class makes: no folders, no "..", nothing but letters, digits, _ . -</summary>
    public static bool IsSafe(string name) => Safe.IsMatch(name) && !name.Contains("..");

    private static string Clean(string s)
    {
        var c = Regex.Replace(s, "[^A-Za-z0-9]+", "-").Trim('-');
        return c.Length == 0 ? "x" : c.Length > 40 ? c[..40] : c;
    }
}
