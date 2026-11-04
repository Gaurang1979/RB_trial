using System.Runtime.InteropServices;

namespace VisionCore;

// P/Invoke declarations matching engine/include/vs_ocr.h. Keep the struct layouts in step with the
// header: field order and sizes must match exactly.
internal static class OcrNative
{
    private const string Lib = "vs_ocr";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Params
    {
        public int StructSize;
        public int RoiX, RoiY, RoiW, RoiH;
        public int Print;
        public int Polarity;
        public int CharHeightPx;
        public int MaxLines;
        public float MinQuality;
        public float MaxSkewDeg;
        public int TimeBudgetMs;
        public int Reader;
        public float AllowShiftPx;
        public float DotSizePx;
        public float JoinStrength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Line
    {
        public IntPtr Text;
        public IntPtr Expected;
        public int Checked;
        public int Pass;
        public float Quality;
        public int FirstChar;
        public int CharCount;
        public fixed float X[4];
        public fixed float Y[4];
        public float AngleDeg;
        public float SlantDeg;
        public float CharHeightPx;
        public int Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Char
    {
        public int Codepoint;
        public int Found;
        public float Score;
        public int Status;
        public fixed float X[4];
        public fixed float Y[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CharQuality
    {
        public int StructSize;
        public float Recognition;
        public float Contrast;
        public float Ink;
        public float Pitch;
        public float ContrastScore;
        public float InkScore;
        public float PitchScore;
        public int Reason;
        public int Learned;
        public float Margin;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LineExt
    {
        public int StructSize;
        public int ShiftState;
        public float ShiftPx;
        public float ShiftX, ShiftY;
        public float PosX, PosY;
        public int CutShort;
        public float Margin;
        public int Agreement;
        public int SecondReaders;
        public float LineConfidence;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Stats
    {
        public int Lines;
        public int ExpectedLines;
        public int Pass;
        public float LocateMs;
        public float ReadMs;
        public float TotalMs;
        public int TimedOut;
    }

    [DllImport(Lib, EntryPoint = "vs_ocr_default_params")] internal static extern void DefaultParams(out Params p);
    [DllImport(Lib, EntryPoint = "vs_ocr_create")] internal static extern IntPtr Create(ref Params p);
    [DllImport(Lib, EntryPoint = "vs_ocr_destroy")] internal static extern void Destroy(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_set_params")] internal static extern int SetParams(IntPtr t, ref Params p);
    [DllImport(Lib, EntryPoint = "vs_ocr_load_model")]
    internal static extern int LoadModel(IntPtr t, [MarshalAs(UnmanagedType.LPUTF8Str)] string onnx, [MarshalAs(UnmanagedType.LPUTF8Str)] string charset);
    // Load the recogniser from decrypted ONNX bytes in memory (no plaintext model on the shipped disk);
    // the charset stays a path (it is not a secret). `size` is size_t -> nuint.
    [DllImport(Lib, EntryPoint = "vs_ocr_load_model_buffer")]
    internal static extern int LoadModelBuffer(IntPtr t, byte[] onnx, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string charset);
    [DllImport(Lib, EntryPoint = "vs_ocr_load_second_model_buffer")]
    internal static extern int LoadSecondModelBuffer(IntPtr t, byte[]? onnx, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? charset);
    [DllImport(Lib, EntryPoint = "vs_ocr_set_agreement")] internal static extern int SetAgreement(IntPtr t, int mode);
    [DllImport(Lib, EntryPoint = "vs_ocr_load_second_model")]
    internal static extern int LoadSecondModel(IntPtr t, [MarshalAs(UnmanagedType.LPUTF8Str)] string? onnx, [MarshalAs(UnmanagedType.LPUTF8Str)] string? charset);
    [DllImport(Lib, EntryPoint = "vs_ocr_set_expected")]
    internal static extern int SetExpected(IntPtr t, int line, [MarshalAs(UnmanagedType.LPUTF8Str)] string? pattern);
    [DllImport(Lib, EntryPoint = "vs_ocr_clear_expected")] internal static extern void ClearExpected(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_set_format")]
    internal static extern int SetFormat(IntPtr t, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string? format);
    [DllImport(Lib, EntryPoint = "vs_ocr_clear_formats")] internal static extern void ClearFormats(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_set_layout")]
    internal static extern int SetLayout(IntPtr t, [MarshalAs(UnmanagedType.LPUTF8Str)] string? layout);
    [DllImport(Lib, EntryPoint = "vs_ocr_get_layout")] internal static extern unsafe int GetLayout(IntPtr t, byte* buf, int cap);
    [DllImport(Lib, EntryPoint = "vs_ocr_learn_layout")] internal static extern int LearnLayout(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_layout_reason")] private static extern IntPtr LayoutReasonPtr(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_line_layout")] internal static extern int LineLayout(IntPtr t, int index);
    [DllImport(Lib, EntryPoint = "vs_ocr_set_tight_spacing")] internal static extern void SetTightSpacing(IntPtr t, int tight);
    [DllImport(Lib, EntryPoint = "vs_ocr_set_clock")] internal static extern void SetClock(IntPtr t, long localUnixSeconds);
    [DllImport(Lib, EntryPoint = "vs_ocr_run")] internal static extern unsafe int Run(IntPtr t, byte* pixels, int width, int height, int stride);
    [DllImport(Lib, EntryPoint = "vs_ocr_line_count")] internal static extern int LineCount(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_get_line")] internal static extern int GetLine(IntPtr t, int index, out Line line);
    [DllImport(Lib, EntryPoint = "vs_ocr_char_count")] internal static extern int CharCount(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_get_char")] internal static extern int GetChar(IntPtr t, int index, out Char c);
    [DllImport(Lib, EntryPoint = "vs_ocr_get_stats")] internal static extern void GetStats(IntPtr t, out Stats s);
    [DllImport(Lib, EntryPoint = "vs_ocr_get_line_image")]
    internal static extern unsafe int GetLineImage(IntPtr t, int index, byte* buf, int cap, out int width);
    [DllImport(Lib, EntryPoint = "vs_ocr_teach_font")]
    internal static extern unsafe int TeachFont(IntPtr t, byte* pixels, int width, int height, int stride, int line,
                                                [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Lib, EntryPoint = "vs_ocr_teach_char")]
    internal static extern unsafe int TeachChar(IntPtr t, byte* pixels, int width, int height, int stride, float x, float y,
                                                [MarshalAs(UnmanagedType.LPUTF8Str)] string character);
    [DllImport(Lib, EntryPoint = "vs_ocr_clear_font")] internal static extern void ClearFont(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_font_glyphs")] internal static extern int FontGlyphs(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_get_font")] internal static extern unsafe int GetFont(IntPtr t, byte* buf, int cap);
    [DllImport(Lib, EntryPoint = "vs_ocr_set_font")]
    internal static extern int SetFont(IntPtr t, [MarshalAs(UnmanagedType.LPUTF8Str)] string font);
    [DllImport(Lib, EntryPoint = "vs_ocr_get_char_quality")] internal static extern int GetCharQuality(IntPtr t, int index, ref CharQuality q);
    [DllImport(Lib, EntryPoint = "vs_ocr_learn_print")] internal static extern int LearnPrint(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_clear_print")] internal static extern void ClearPrint(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_print_parts")] internal static extern int PrintParts(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_ocr_get_print")] internal static extern unsafe int GetPrint(IntPtr t, byte* buf, int cap);
    [DllImport(Lib, EntryPoint = "vs_ocr_set_print")]
    internal static extern int SetPrint(IntPtr t, [MarshalAs(UnmanagedType.LPUTF8Str)] string reference);
    [DllImport(Lib, EntryPoint = "vs_ocr_get_line_ext")] internal static extern int GetLineExt(IntPtr t, int index, ref LineExt e);
    [DllImport(Lib, EntryPoint = "vs_ocr_measure_dots")]
    internal static extern unsafe float MeasureDots(IntPtr t, byte* pixels, int width, int height, int stride, out float pitchPx);
    [DllImport(Lib, EntryPoint = "vs_ocr_last_error")] private static extern IntPtr LastErrorPtr(IntPtr t);

    internal static string LastError(IntPtr t) => Marshal.PtrToStringUTF8(LastErrorPtr(t)) ?? "";
    internal static string LayoutReason(IntPtr t) => Marshal.PtrToStringUTF8(LayoutReasonPtr(t)) ?? "";
}
