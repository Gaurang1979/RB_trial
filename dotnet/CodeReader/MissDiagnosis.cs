using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BarcodeReading;

/// <summary>
/// Why a found region did not read (engine cr_miss_reason): the first cause the engine measured,
/// in this order. After the expired verifier patents US9552506 / US8027802.
/// </summary>
public enum MissReason
{
    /// <summary>Nothing measured stands out, or the region was not diagnosed (time cap).</summary>
    Unknown = 0,
    /// <summary>The code runs off the image edge.</summary>
    CutOff = 1,
    /// <summary>Too much of the code is saturated: a reflection.</summary>
    Glare = 2,
    /// <summary>Light and dark too close: light level, exposure or print.</summary>
    LowContrast = 3,
    /// <summary>No code texture where the finder saw a code: covered, folded or blank.</summary>
    Covered = 4,
    /// <summary>Under about 2 px per module.</summary>
    TooSmall = 5,
    /// <summary>Edges soft against the contrast: focus, motion, bent label.</summary>
    Blur = 6,
    /// <summary>Code texture, but no QR finder patterns / Data Matrix L.</summary>
    NoFinder = 7,
}

/// <summary>
/// The engine's diagnosis of one unread region: the reason and the numbers behind it, beside the
/// same numbers on this image's read codes. Grey levels are camera values (0..255).
/// </summary>
public sealed record MissDiagnosis(
    Region Box,
    MissReason Reason,
    bool Measured,
    float Sharpness,
    float Contrast,
    float Light,
    float Dark,
    float Saturated,
    float Texture,
    float ModulePx,
    float LearnedModulePx,
    bool? FinderFound,
    bool? QuietZoneClear,
    float RefSharpness,
    float RefContrast,
    int SlotRow = 0,
    int SlotCol = 0)
{
    internal static MissDiagnosis FromNative(Native.MissInfo d) => new(
        new Region(d.X, d.Y, d.W, d.H),
        Enum.IsDefined(typeof(MissReason), d.Reason) ? (MissReason)d.Reason : MissReason.Unknown,
        d.Measured != 0, d.Sharpness, d.Contrast, d.Light, d.Dark, d.Saturated, d.Texture, d.ModulePx, d.LearnedModulePx,
        d.Finder < 0 ? null : d.Finder != 0, d.QuietZone < 0 ? null : d.QuietZone != 0, d.RefSharpness, d.RefContrast,
        d.SlotRow, d.SlotCol);

    /// <summary>The case slot (row, column from 1 at the top left) when the case's codes sit on a
    /// lattice; null otherwise (shrink-wrapped cans).</summary>
    public (int Row, int Col)? Slot => SlotRow > 0 && SlotCol > 0 ? (SlotRow, SlotCol) : null;

    /// <summary>The short tag drawn beside the red box.</summary>
    public string Tag => (Slot is { } s ? $"R{s.Row} C{s.Col} · " : "") + (Measured ? TagOf(Reason) : "?");

    /// <summary>The tag of a reason, in capitals for the live picture.</summary>
    public static string TagOf(MissReason r) => r switch
    {
        MissReason.CutOff => "CUT OFF",
        MissReason.Glare => "GLARE",
        MissReason.LowContrast => "LOW CONTRAST",
        MissReason.Covered => "COVERED",
        MissReason.TooSmall => "TOO SMALL",
        MissReason.Blur => "BLUR",
        MissReason.NoFinder => "NO FINDER",
        _ => "?",
    };

    /// <summary>What the line can do about a reason.</summary>
    public static string FixOf(MissReason r) => r switch
    {
        MissReason.CutOff => "The code runs off the image: check where the case stops and where the label sits, or widen the field of view.",
        MissReason.Glare => "A reflection washes out the code: tilt the camera or light about 15 degrees, use diffuse or polarised light, or lower the exposure.",
        MissReason.LowContrast => "The code is too faint: more light or exposure, or check the print (ribbon, ink, label colour).",
        MissReason.Covered => "No code where one was found: label covered, folded, torn or not printed.",
        MissReason.TooSmall => "Modules under 2 pixels: move the camera closer, use a longer lens or a higher-resolution camera.",
        MissReason.Blur => "Edges are soft: check the focus, shorten the exposure if the case moves, or a bent or curved label.",
        MissReason.NoFinder => "Finder patterns damaged or hidden: print quality, or a part of the code covered.",
        _ => "Nothing measured stands out.",
    };

    /// <summary>Tooltip text: the reason, what to do, and the numbers.</summary>
    public string Tooltip
    {
        get
        {
            if (!Measured) return "Not diagnosed: the time for diagnosis ran out on this case.";
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(TagOf(Reason)).Append(": ").AppendLine(FixOf(Reason));
            sb.Append(c, $"sharpness {Sharpness:F2}");
            if (RefSharpness > 0) sb.Append(c, $" (read codes {RefSharpness:F2})");
            sb.AppendLine();
            sb.Append(c, $"contrast {Contrast:F0} grey levels");
            if (RefContrast > 0) sb.Append(c, $" (read codes {RefContrast:F0})");
            sb.AppendLine(c, $", light {Light:F0} / dark {Dark:F0}");
            sb.AppendLine(c, $"saturated {Saturated * 100:F0}%, code texture {Texture * 100:F0}%");
            if (ModulePx > 0)
            {
                sb.Append(c, $"module {ModulePx:F1} px");
                if (LearnedModulePx > 0) sb.Append(c, $" (product {LearnedModulePx:F1} px)");
                sb.AppendLine();
            }
            sb.Append("finder ").Append(FinderFound switch { true => "found", false => "not found", null => "not checked" });
            sb.Append(", quiet zone ").Append(QuietZoneClear switch { true => "clear", false => "marks in it", null => "not checked" });
            return sb.ToString();
        }
    }
}

/// <summary>
/// Counts of miss reasons over a run of the line (a shift), so the line can fix the cause: focus,
/// light, label placement. Thread safe.
/// </summary>
public sealed class MissReasonTally
{
    private readonly object _lock = new();
    private readonly int[] _counts = new int[Enum.GetValues(typeof(MissReason)).Length];
    private int _notDiagnosed, _cases;

    /// <summary>Adds the diagnosed misses of one decoded case (only short cases carry any).</summary>
    public void Add(DecodeResult result)
    {
        if (result.MissInfo.Count == 0) return;
        lock (_lock)
        {
            _cases++;
            foreach (var d in result.MissInfo)
            {
                if (d.Measured) _counts[(int)d.Reason]++;
                else _notDiagnosed++;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_counts);
            _notDiagnosed = 0;
            _cases = 0;
        }
    }

    /// <summary>Short cases with diagnosed misses.</summary>
    public int Cases
    {
        get { lock (_lock) return _cases; }
    }

    /// <summary>Unread regions the time cap left undiagnosed.</summary>
    public int NotDiagnosed
    {
        get { lock (_lock) return _notDiagnosed; }
    }

    /// <summary>Reasons seen, most frequent first (Unknown last among equals).</summary>
    public IReadOnlyList<(MissReason Reason, int Count)> Counts()
    {
        lock (_lock)
        {
            return Enumerable.Range(0, _counts.Length)
                .Where(i => _counts[i] > 0)
                .Select(i => ((MissReason)i, _counts[i]))
                .OrderByDescending(t => t.Item2)
                .ThenBy(t => t.Item1 == MissReason.Unknown)
                .ThenBy(t => t.Item1)
                .ToList();
        }
    }

    /// <summary>The most frequent known reason, or null when none is known yet.</summary>
    public MissReason? Top()
    {
        var known = Counts().Where(t => t.Reason != MissReason.Unknown).ToList();
        return known.Count == 0 ? null : known[0].Reason;
    }
}
