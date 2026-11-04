using System;
using System.Runtime.InteropServices;

namespace BarcodeReading;

// cr_dpm_params and cr_dpm (codereader.h): ISO/IEC TR 29158 grading. Both grow at their end:
// StructSize tells the engine how much it may read or write.
internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DpmParams
    {
        public int StructSize;
        public int Enabled, Lighting, Aperture, WavelengthNm, Closing;
        public float RCal, MlCal, SrCal, SrTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Dpm
    {
        public int StructSize;
        public int Measured, Calibrated, Inverted, Lighting, Aperture;
        public float CellPx, ClosingPx, MeanLight, MeanDark, MeanLightPct;
        public int MeanLightOk;
        public float CellContrast, CellModulation, MinReflectance, FixedPatternDamage, DistributedDamage;
        public float AxialNonuniformity, GridNonuniformity, UnusedErrorCorrection;
        public int GradeDecode, GradeCc, GradeCm, GradeMr, GradeFpd, GradeAn, GradeGn, GradeUec;
        public int Overall, Overall50, Overall80;
    }

    [DllImport(Lib, EntryPoint = "cr_set_dpm")]
    internal static extern int SetDpm(IntPtr r, ref DpmParams p);

    [DllImport(Lib, EntryPoint = "cr_get_dpm")]
    internal static extern int GetDpm(IntPtr r, int index, ref Dpm d);
}
