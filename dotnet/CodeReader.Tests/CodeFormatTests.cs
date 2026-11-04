using System;
using System.Linq;
using System.Runtime.InteropServices;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class CodeFormatTests
{
    [Fact]
    public void NativeParamsMatchTheEngineLayout()
    {
        // engine/src/c_api.cpp: static_assert(sizeof(cr_params) == 26 * 4), cr_code_ext 8 * 4 + a pointer
        // (the add-on text, at 8 * 4) + 4 * 4 (codeword yield and grid), cr_stats_ext 7 * 4, cr_scan 13 * 4
        Type T(string name) => typeof(CodeReader).Assembly.GetType("BarcodeReading.Native+" + name, throwOnError: true)!;
        Assert.Equal(26 * 4, Marshal.SizeOf(T("Params")));
        Assert.Equal(25 * 4, (int)Marshal.OffsetOf(T("Params"), "PharmaDirection"));
        Assert.Equal(8 * 4 + IntPtr.Size + 4 * 4, Marshal.SizeOf(T("CodeExt")));
        Assert.Equal(8 * 4, (int)Marshal.OffsetOf(T("CodeExt"), "AddOn"));
        Assert.Equal(8 * 4 + IntPtr.Size, (int)Marshal.OffsetOf(T("CodeExt"), "CodewordYield"));
        Assert.Equal(8 * 4 + IntPtr.Size + 3 * 4, (int)Marshal.OffsetOf(T("CodeExt"), "GridCols"));
        Assert.Equal(7 * 4, Marshal.SizeOf(T("StatsExt")));
        Assert.Equal(13 * 4, Marshal.SizeOf(T("Scan")));
        // struct_size first, so the engine finds it in any caller's struct
        Assert.Equal(0, (int)Marshal.OffsetOf(T("Params"), "StructSize"));
        Assert.Equal(4, (int)Marshal.OffsetOf(T("CodeExt"), "SymbologyId"));
    }

    [Fact]
    public void NativeDpmMatchesTheEngineLayout()
    {
        // engine/src/c_api.cpp: static_assert(sizeof(cr_dpm_params) == 10 * 4), cr_dpm 31 * 4
        Type T(string name) => typeof(CodeReader).Assembly.GetType("BarcodeReading.Native+" + name, throwOnError: true)!;
        Assert.Equal(10 * 4, Marshal.SizeOf(T("DpmParams")));
        Assert.Equal(31 * 4, Marshal.SizeOf(T("Dpm")));
        Assert.Equal(0, (int)Marshal.OffsetOf(T("DpmParams"), "StructSize"));
        Assert.Equal(0, (int)Marshal.OffsetOf(T("Dpm"), "StructSize"));
        Assert.Equal(28 * 4, (int)Marshal.OffsetOf(T("Dpm"), "Overall"));
    }

    [Fact]
    public void NativeParamsCarryTheTaggedStructSize()
    {
        // codereader.h: CR_PARAMS_SIZE = CR_PARAMS_TAG (0x43520000) | sizeof(cr_params); an untagged struct is refused
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        object n = typeof(ReaderSettings).GetMethod("ToNative", flags)!.Invoke(new ReaderSettings(), null)!;
        Assert.Equal(0x43520000 | 26 * 4, (int)n.GetType().GetField("StructSize")!.GetValue(n)!);
    }

    [Fact]
    public void FlagsMatchTheEngine()
    {
        // engine/include/codereader.h cr_format
        Assert.Equal(0x0FFC, (int)CodeFormat.AllLinear);
        Assert.Equal(0x7FFFF, (int)CodeFormat.Any);
        Assert.Equal(0x40000, (int)CodeFormat.RMqr);
        Assert.Equal((0x8000, 0x10000, 0x20000), ((int)CodeFormat.Pharmacode, (int)CodeFormat.PharmacodeReverse, (int)CodeFormat.DotCode));
        var single = CodeFormats.Single;
        Assert.Equal(19, single.Length);
        Assert.Equal(single.Length, single.Distinct().Count());
        // each a single bit, together every known bit
        Assert.All(single, f => Assert.True(((int)f & ((int)f - 1)) == 0, $"{f} is one bit"));
        Assert.Equal(0x7FFFF, single.Aggregate(0, (a, f) => a | (int)f));
    }

    [Fact]
    public void PharmacodeBarsFromItsValue()
    {
        // 3 = two narrow bars, 4 = narrow + wide, 131070 = 16 wide bars, 131069 = 16 bars, the last narrow
        Assert.Equal(2, CodeFormats.PharmacodeBars("3"));
        Assert.Equal(2, CodeFormats.PharmacodeBars("4"));
        Assert.Equal(2, CodeFormats.PharmacodeBars("6"));
        Assert.Equal(3, CodeFormats.PharmacodeBars("7"));
        Assert.Equal(16, CodeFormats.PharmacodeBars("131070"));
        Assert.Equal(16, CodeFormats.PharmacodeBars("65535"));
        Assert.Equal(15, CodeFormats.PharmacodeBars("65534"));
        Assert.Equal(0, CodeFormats.PharmacodeBars("2"));
        Assert.Equal(0, CodeFormats.PharmacodeBars("131071"));
        Assert.Equal(0, CodeFormats.PharmacodeBars("+12"));
        Assert.Equal(0, CodeFormats.PharmacodeBars("ABC"));
        Assert.True(CodeFormats.IsPharmacode(CodeFormat.PharmacodeReverse));
        Assert.False(CodeFormats.IsLinear(CodeFormat.Pharmacode)); // its lock counts bars, not characters
    }

    [Fact]
    public void LinearTypesAndFixedLengths()
    {
        Assert.True(CodeFormats.IsLinear(CodeFormat.Itf));
        Assert.True(CodeFormats.IsLinear(CodeFormat.Gs1_128));
        Assert.True(CodeFormats.IsLinear(CodeFormat.DataBar));
        Assert.False(CodeFormats.IsLinear(CodeFormat.Pdf417));
        Assert.False(CodeFormats.IsLinear(CodeFormat.QrCode | CodeFormat.Itf));
        Assert.False(CodeFormats.IsLinear(0));
        Assert.True(CodeFormats.HasFixedLength(CodeFormat.UpcE));
        Assert.False(CodeFormats.HasFixedLength(CodeFormat.Code39));
    }

    [Fact]
    public void EveryTypeHasAName()
    {
        var names = CodeFormats.Single.Select(CodeFormats.Name).ToList();
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal("UPC-A", CodeFormats.Name(CodeFormat.UpcA));
        Assert.Equal("ITF / ITF-14", CodeFormats.Name(CodeFormat.Itf));
    }

    [Fact]
    public void RulesReachTheNativeParams()
    {
        var s = new ReaderSettings { Format = CodeFormat.Itf, MinLength = 14, MaxLength = 14, CheckDigit = CheckDigitRule.Strip };
        // ToNative and Native.Params are internal: read them by reflection
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        object n = typeof(ReaderSettings).GetMethod("ToNative", flags)!.Invoke(s, null)!;
        int Field(string name) => (int)n.GetType().GetField(name)!.GetValue(n)!;
        Assert.Equal(0x200, Field("Format"));
        Assert.Equal((14, 14, 2), (Field("MinLength"), Field("MaxLength"), Field("CheckDigit")));
        Assert.Equal(0, Field("PharmaDirection")); // across: the reading before the setting existed
    }

    [Fact]
    public void PharmacodeDirectionReachesTheNativeParams()
    {
        // codereader.h CR_PHARMA_ACROSS = 0, CR_PHARMA_DOWN = 1
        Assert.Equal((0, 1), ((int)PharmacodeDirection.Across, (int)PharmacodeDirection.Down));
        Assert.Equal(PharmacodeDirection.Across, new ReaderSettings().PharmacodeDirection);
        var s = new ReaderSettings { Format = CodeFormat.PharmacodeReverse, PharmacodeDirection = PharmacodeDirection.Down };
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        object n = typeof(ReaderSettings).GetMethod("ToNative", flags)!.Invoke(s, null)!;
        Assert.Equal(1, (int)n.GetType().GetField("PharmaDirection")!.GetValue(n)!);
    }
}
