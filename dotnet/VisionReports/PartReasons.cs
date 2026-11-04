using VisionCore.Plc;

namespace VisionReports;

/// <summary>What a camera said about a part: the first failing tool and its plain-language reason.</summary>
public sealed record CameraVerdict(int Camera, bool Ok, string? FailTool, string? Reason);

/// <summary>
/// Turns a part record from the PLC into a reason note and a category for the reports. The category
/// is the main reason (used for the Pareto); the note lists every reason with the measured values.
/// </summary>
public static class PartReasons
{
    public const string Good = "Good";
    public const string TouchingCat = "Touching products (no gap)";
    public const string LengthCat = "Length out of tolerance";
    public const string NotInspectedCat = "Not inspected";
    public const string MissedCat = "Missed at a camera sensor";

    public static (string Category, string Reason) Explain(PartRecord p, int cameras, IReadOnlyList<CameraVerdict> verdicts,
                                                          double? lengthMm, double? nominalMm, int tolerancePct, double? gapMm, double? minGapMm)
    {
        var notes = new List<string>();
        string? cat = null;
        void Add(string category, string note) { cat ??= category; notes.Add(note); }

        if (p.Flags.HasFlag(PartFlags.Touching))
            Add(TouchingCat, p.GroupIndex == 0
                ? $"Touching products: {p.GroupSize} products came without a gap (one sensor signal); this is product 1 of {p.GroupSize}"
                : $"Touching products: hidden behind the product before it (product {p.GroupIndex + 1} of {p.GroupSize}), not inspected");
        if (p.Flags.HasFlag(PartFlags.LengthOut))
        {
            string len = lengthMm is { } l ? $"{l:0.#} mm" : $"{p.LengthPulses} pulses";
            string nom = nominalMm is { } n ? $" (nominal {n:0.#} mm ±{tolerancePct} %)" : "";
            bool shortOne = lengthMm is { } l2 && nominalMm is { } n2 && l2 < n2;
            Add(LengthCat, $"Length {len}{nom}: {(shortOne ? "too short — broken, half pack or debris" : "too long — lying, fallen or tilted product")}");
        }
        for (int c = 0; c < cameras; c++)
        {
            if (p.GroupIndex > 0) break;   // hidden touching products: no camera saw them
            var v = verdicts.FirstOrDefault(x => x.Camera == c);
            if (!p.CameraAnswered(c))
                Add(NotInspectedCat, $"Camera {c + 1}: not inspected (no answer, busy or not ready)");
            else if (!p.CameraOk(c))
                Add($"Camera {c + 1}: {v?.FailTool ?? "NOT OK"}", $"Camera {c + 1} NOT OK" +
                    (v?.FailTool is { } t ? $" — {t}" : "") + (v?.Reason is { Length: > 0 } r ? $": {r}" : ""));
        }
        if (p.Flags.HasFlag(PartFlags.MissedAtSensor))
            Add(MissedCat, "Passed a camera's trigger sensor unseen (sensor missed it)");
        if (p.Flags.HasFlag(PartFlags.SmallGap))
            notes.Add($"Gap to the product before: {(gapMm is { } g ? $"{g:0.#} mm" : $"{p.GapPulses} pulses")}" +
                      (minGapMm is { } m ? $" (minimum {m:0.#} mm)" : ""));
        if (p.Flags.HasFlag(PartFlags.Learning))
            notes.Add("First part after the start: distances learned on it");
        if (p.Rejected && cat is null) cat = "Rejected (reason not reported)";
        return (p.Rejected ? cat! : Good, notes.Count == 0 ? (p.Rejected ? "Rejected" : "OK") : string.Join("; ", notes));
    }
}
