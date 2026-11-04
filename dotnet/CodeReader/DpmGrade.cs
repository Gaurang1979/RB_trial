using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace BarcodeReading;

/// <summary>The lighting a direct part mark is graded under (ISO/IEC TR 29158), written after the grade.</summary>
public enum DpmLighting
{
    /// <summary>Diffuse on-axis light ("90").</summary>
    Diffuse90 = 0,
    /// <summary>30 degrees from four sides ("30Q").</summary>
    Low30Four = 1,
    /// <summary>30 degrees from two sides ("30T").</summary>
    Low30Two = 2,
    /// <summary>30 degrees from one side ("30S").</summary>
    Low30One = 3,
    /// <summary>Diffuse off-axis light, a dome ("D").</summary>
    Dome = 4,
}

/// <summary>
/// Direct part mark grading after ISO/IEC TR 29158 (CodeReader.SetDpm). Minimum reflectance needs the
/// image-based calibration: a calibration symbol of known light reflectance RCalPct imaged with its mean
/// light (DpmGrade.MeanLight, kept as MlCal) at system response SrCal (exposure x gain); each part's
/// response in SrTarget (0 = the same as SrCal).
/// </summary>
public sealed record DpmSettings
{
    public bool Enabled { get; init; }
    public DpmLighting Lighting { get; init; } = DpmLighting.Diffuse90;
    /// <summary>Synthetic aperture in % of the cell: 80, 50, or 0 = the better grade of the two.</summary>
    public int AperturePct { get; init; } = 80;
    /// <summary>Recorded with the grade; 0 = not recorded.</summary>
    public int WavelengthNm { get; init; }
    /// <summary>Dot peen: 0 = gaps between dots closed, sized from the image; -1 = off; above 0 = the closing disc in px.</summary>
    public int Closing { get; init; }
    public float RCalPct { get; init; }
    public float MlCal { get; init; }
    public float SrCal { get; init; }
    public float SrTarget { get; init; }

    internal Native.DpmParams ToNative() => new()
    {
        StructSize = Marshal.SizeOf<Native.DpmParams>(),
        Enabled = Enabled ? 1 : 0,
        Lighting = (int)Lighting,
        Aperture = AperturePct,
        WavelengthNm = WavelengthNm,
        Closing = Closing,
        RCal = RCalPct,
        MlCal = MlCal,
        SrCal = SrCal,
        SrTarget = SrTarget,
    };
}

/// <summary>
/// A direct part mark grade after ISO/IEC TR 29158. Grades 4 = A .. 0 = F, -1 not measured; contrast and
/// modulation 0..1; mean light in grey levels and in % of full scale (the standard wants 70..86 %).
/// </summary>
public sealed record DpmGrade(int OverallGrade, int AperturePct, DpmLighting Lighting, bool Calibrated)
{
    public int WavelengthNm { get; init; }
    public bool Inverted { get; init; }
    public float CellPx { get; init; }
    public float ClosingPx { get; init; }
    public float MeanLight { get; init; }
    public float MeanDark { get; init; }
    public float MeanLightPct { get; init; }
    /// <summary>Mean light within 70..86 % of full scale; otherwise set the exposure and grade again.</summary>
    public bool MeanLightOk { get; init; }
    public float CellContrast { get; init; }
    public float CellModulation { get; init; }
    /// <summary>Minimum reflectance in %; -1 without the reflectance calibration.</summary>
    public float MinReflectancePct { get; init; } = -1;
    public float FixedPatternDamage { get; init; }
    /// <summary>The mean grade of the fixed-pattern segments, 0..4.</summary>
    public float DistributedDamage { get; init; }
    public int Overall50 { get; init; } = -1;
    public int Overall80 { get; init; } = -1;
    /// <summary>Each graded parameter and its grade, in the standard's order; parameters not measured are left out.</summary>
    public IReadOnlyList<(string Parameter, int Grade)> Grades { get; init; } = Array.Empty<(string, int)>();

    /// <summary>The lighting as written after a grade: 90, 30Q, 30T, 30S or D.</summary>
    public static string LightingCode(DpmLighting l) => l switch
    {
        DpmLighting.Low30Four => "30Q",
        DpmLighting.Low30Two => "30T",
        DpmLighting.Low30One => "30S",
        DpmLighting.Dome => "D",
        _ => "90",
    };

    /// <summary>The grade as a verifier writes it: grade/aperture/wavelength/lighting, e.g. "3/80/660/30Q"
    /// (the aperture in % of the cell; the wavelength left out when not recorded).</summary>
    public string Notation => string.Join("/", new[]
    {
        OverallGrade.ToString(CultureInfo.InvariantCulture), AperturePct.ToString(CultureInfo.InvariantCulture),
        WavelengthNm > 0 ? WavelengthNm.ToString(CultureInfo.InvariantCulture) : "-", LightingCode(Lighting),
    });

    /// <summary>As a CodeQuality, so the Read code tool's lowest-grade check and reports take it like a 15415 grade.</summary>
    public CodeQuality ToQuality() => new(CellContrast * 100f, CellModulation, CellPx, 0f, 0f, OverallGrade)
    {
        Calibrated = Calibrated,
        Grades = Grades,
        Values = new Dictionary<string, float>
        {
            ["Cell contrast"] = CellContrast, ["Cell modulation"] = CellModulation, ["Minimum reflectance"] = MinReflectancePct,
            ["Fixed pattern damage"] = FixedPatternDamage, ["Distributed damage"] = DistributedDamage, ["Mean light"] = MeanLightPct,
        },
    };

    internal static DpmGrade FromNative(in Native.Dpm d, int wavelengthNm) =>
        new(d.Overall, d.Aperture, (DpmLighting)d.Lighting, d.Calibrated != 0)
        {
            WavelengthNm = wavelengthNm,
            Inverted = d.Inverted != 0,
            CellPx = d.CellPx,
            ClosingPx = d.ClosingPx,
            MeanLight = d.MeanLight,
            MeanDark = d.MeanDark,
            MeanLightPct = d.MeanLightPct,
            MeanLightOk = d.MeanLightOk != 0,
            CellContrast = d.CellContrast,
            CellModulation = d.CellModulation,
            MinReflectancePct = d.MinReflectance,
            FixedPatternDamage = d.FixedPatternDamage,
            DistributedDamage = d.DistributedDamage,
            Overall50 = d.Overall50,
            Overall80 = d.Overall80,
            Grades = new[]
            {
                ("Decode", d.GradeDecode), ("Cell contrast", d.GradeCc), ("Minimum reflectance", d.GradeMr),
                ("Cell modulation", d.GradeCm), ("Fixed pattern damage", d.GradeFpd), ("Axial non-uniformity", d.GradeAn),
                ("Grid non-uniformity", d.GradeGn), ("Unused error correction", d.GradeUec),
            }.Where(g => g.Item2 >= 0).ToList(),
        };
}
