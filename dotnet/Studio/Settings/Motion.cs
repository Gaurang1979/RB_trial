using System.Runtime.InteropServices;

namespace VisionSystem.Studio.Settings;

/// <summary>Reduce motion (requirements 17.15 NXT-06; UI-UX-SPEC.md 7): the setting or the operating
/// system's own switch turns off non-essential animation. Flashing never goes above 2 Hz and each part
/// has at most one short animation.</summary>
public static class Motion
{
    /// <summary>The fastest any flashing may go (UI-UX-SPEC.md 7).</summary>
    public const double MaxFlashHz = 2;

    /// <summary>The longest animation of a part (UI-UX-SPEC.md 7: under 300 ms).</summary>
    public static readonly TimeSpan MaxAnimation = TimeSpan.FromMilliseconds(300);

    /// <summary>The window class the styles use to drop transitions, glows and the press nudge.</summary>
    public const string ReduceClass = "reduce-motion";

    /// <summary>Whether animation is reduced: the setting, or the operating system asking for it.</summary>
    public static bool Reduced(bool setting, Func<bool>? osAsks = null) => setting || (osAsks ?? OsAsksReducedMotion)();

    /// <summary>Windows: "Show animations in Windows" off (SPI_GETCLIENTAREAANIMATION). Other platforms: no.</summary>
    public static bool OsAsksReducedMotion()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var on, 0) && !on;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);
}
