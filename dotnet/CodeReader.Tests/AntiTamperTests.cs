using System;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

// Runtime anti-tamper public contract (requirements section 12, owner 2026-11-01). The deterministic
// end-to-end proof that a tamper forces NG and never a read lives in the engine's C++ test
// (engine/tests/engine_tests.cpp testAntiTamper), which can drive the latch directly; these managed
// tests, like the rest of CodeReader.Tests, do not load the native library. They lock the managed
// contract the line software relies on: the enum matches the engine's cr_tamper, and a tamper is a
// distinct, catchable fault that is still a fault (so the existing NG path keeps working).
public class AntiTamperTests
{
    [Fact]
    public void TamperStateMatchesEngineCrTamper()
    {
        // codereader.h: CR_TAMPER_NONE=0, CR_TAMPER_DEBUGGER=1, CR_TAMPER_CODE_PATCH=2
        Assert.Equal(0, (int)TamperState.None);
        Assert.Equal(1, (int)TamperState.Debugger);
        Assert.Equal(2, (int)TamperState.CodePatched);
    }

    [Fact]
    public void TamperExceptionCarriesStateAndMessage()
    {
        var ex = new TamperDetectedException(TamperState.CodePatched, "codereader: tampering detected");
        Assert.Equal(TamperState.CodePatched, ex.State);
        Assert.Contains("tamper", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TamperExceptionIsStillAFaultForTheExistingNgPath()
    {
        // A host that only catches the generic fault still treats a tamper as NG; one that wants a
        // tamper alarm catches the specific type. Both must work, so the specific type derives from
        // the generic one.
        var ex = new TamperDetectedException(TamperState.Debugger, "codereader: debugger attached");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }
}
