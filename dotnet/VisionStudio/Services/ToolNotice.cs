using VisionCore;

namespace VisionStudio.Services;

/// <summary>What the Teach page and the result tables say about whether a tool decides the part's OK/NG.</summary>
public static class ToolNotice
{
    /// <summary>The explain key the Teach page shows for the tool: a Read text tool with a line that would pass any
    /// text (no format, or one such as {?}), or one set to report only; null for every other tool.</summary>
    public static string? Of(ToolConfig tool) => tool switch
    {
        TextToolConfig { Mode: TextMode.Read, ReportOnly: true } => TextTool.ReportOnlyExplain,
        TextToolConfig { Mode: TextMode.Read } t when t.Lines.Count == 0 || t.Lines.Any(l => TextTool.OpenFormat(l.Template)) => TextTool.NoRuleExplain,
        _ => null,
    };

    /// <summary>The mark of a tool's row: ⓘ for a report-only result (never OK), else ✓ or ✗.</summary>
    public static string Mark(ToolResult r) => r.ReportOnly ? "ⓘ" : r.Pass ? "✓" : "✗";

    /// <summary>The row's style: "info" for a report-only result, else "ok" or "ng".</summary>
    public static string Row(ToolResult r) => r.ReportOnly ? "info" : r.Pass ? "ok" : "ng";
}
