namespace VisionCore.Security;

/// <summary>
/// Intellectual Property Protection: Camouflage mapping for external UI, Taskbar, and Windows Task Manager.
/// Replaces internal algorithmic and optical physics jargon with unassuming factory production terms
/// so competitors, line operators, and visitors cannot deduce our internal technology stack.
/// </summary>
public static class CamouflageLabels
{
    private static readonly HashSet<string> OpenSourcePreserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "OpenCV",
        "ZXing",
        "SkiaSharp",
        "SQLite",
        "Suricata",
        "Zeek",
        "WireGuard",
        "ClamAV",
        "Hikrobot",
        "Delta DVP",
        "Modbus"
    };

    private static readonly Dictionary<string, string> ToolNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Optical physics and computational imaging
        ["vs_photometric"] = "Surface Texture Check",
        ["photometric"] = "Surface Texture Check",
        ["vs_polar"] = "Anti-Glare Filter",
        ["polarimetric"] = "Anti-Glare Filter",
        ["vs_wavelet"] = "Surface Filter",
        ["wavelet"] = "Surface Filter",
        ["vs_deform"] = "Pouch Alignment",
        ["non_rigid"] = "Pouch Alignment",
        ["vs_infrared"] = "Liquid Level & Moisture",
        ["infrared"] = "Liquid Level & Moisture",
        ["vs_rectify"] = "Conveyor Alignment",
        ["rolling_shutter"] = "Conveyor Alignment",

        // AI & Machine Learning Backbones
        ["vs_anomaly"] = "Pattern Match",
        ["patchcore"] = "Pattern Match",
        ["vs_haystack"] = "Process Stability Map",
        ["haystack"] = "Process Stability Map",
        ["active_learning"] = "Adaptive Tolerance",
        ["vs_drift"] = "Lighting Drift Monitor",

        // Tooling & Thermal
        ["vs_thermal"] = "Seal Quality Check",
        ["thermal_seal"] = "Seal Quality Check",
        ["golden_template"] = "Reference Check",
        ["metamorphic"] = "Self-Verification"
    };

    /// <summary>
    /// Translates an internal engineering/algorithm name to an unassuming competitor-proof factory label.
    /// </summary>
    public static string GetSafeToolLabel(string internalName)
    {
        if (string.IsNullOrWhiteSpace(internalName)) return "Inspection Tool";

        // Open Source / Standard Attribution Compliance: never change required third-party names
        foreach (var os in OpenSourcePreserved)
        {
            if (internalName.Contains(os, StringComparison.OrdinalIgnoreCase))
                return os;
        }

        foreach (var kv in ToolNameMap)
        {
            if (internalName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value;
            }
        }
        return internalName;
    }

    /// <summary>
    /// Generic Process / Window Title for Taskbar and Task Manager display.
    /// </summary>
    public static string GetSafeProcessTitle(string? lineOrCustomer = null)
    {
        return string.IsNullOrWhiteSpace(lineOrCustomer)
            ? "Machine Control System"
            : $"Machine Control System — {lineOrCustomer}";
    }

    /// <summary>
    /// Generic OS / Service Description for Windows Task Manager details tab.
    /// </summary>
    public const string SafeServiceDescription = "Factory Edge Automation Runtime Host";
    public const string SafeCompanyName = "Industrial Automation Solutions";
    public const string SafeProductName = "Line Control System";
}
