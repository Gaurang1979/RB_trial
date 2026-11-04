using System;
using System.IO;
using System.Text.Json;

namespace BarcodeReading;

/// <summary>
/// What the reader knows about its optics for verifier-style print grades (as NI Vision and DALSA
/// verification tools calibrate): a flat field from a white card, a two-patch reflectance card, and
/// the lens and scale from a chessboard at the case's top. Kept per product beside the station's
/// data (not in the product master, so a recalibration does not make a new product version).
/// Without it the grades stay camera-relative.
/// </summary>
public sealed class GradingCalibration
{
    /// <summary>The white-card picture (BMP beside the JSON file); null = no flat field.</summary>
    public string? WhiteCardFile { get; set; }
    public float GreyDark { get; set; }
    public float GreyLight { get; set; }
    /// <summary>The card's certified reflectances in %; ReflLightPct 0 = no reflectance calibration.</summary>
    public float ReflDarkPct { get; set; }
    public float ReflLightPct { get; set; }
    public LensCalibration? Lens { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public bool HasFlatField => WhiteCardFile is not null;
    public bool HasReflectance => ReflLightPct > 0;
    public bool HasScale => Lens is { MmPerPx: > 0 };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static GradingCalibration Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<GradingCalibration>(File.ReadAllText(path)) ?? new() : new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Sets what is calibrated on a reader; a missing or unusable white card is skipped. Returns null, or
    /// the engine's reason when the stored lens was refused (over the 0.5 px limit): the reader then has no lens
    /// and no scale, and the caller should say so.</summary>
    public string? ApplyTo(CodeReader reader, string folder)
    {
        if (WhiteCardFile is { } f && File.Exists(Path.Combine(folder, f)))
        {
            var (px, w, h) = BmpImage.LoadGray(Path.Combine(folder, f));
            try { reader.SetFlatField(px, w, h, w); } catch (InvalidOperationException) { }
        }
        if (HasReflectance) reader.SetReflectance(GreyDark, GreyLight, ReflDarkPct, ReflLightPct);
        if (Lens is { } l)
        {
            try { reader.SetLens(l); }
            catch (InvalidOperationException e) { return e.Message; }
        }
        return null;
    }
}
