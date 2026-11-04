using System.Net;
using System.Text;
using System.Text.Json;
using VisionCore;
using VisionCore.Plc;
using Xunit;

// Check text expected text from the PLC's UserIn1..4 and the text block (PLC-INTERFACE.md section 3.7):
// a string that is missing, stale (not from this trigger) or unreadable never passes
public class PlcTextTests
{
    // a text block as the Modbus server holds it: register n = bytes 2n (low), 2n+1 (high)
    private static byte[] Block(ushort stamp, int register, string s, bool highFirst = true, bool s7 = false, int max = 0)
    {
        var b = new PlcBlock(PlcTextBlock.Size, false);
        b.SetU16(0, stamp);
        var chars = Encoding.Latin1.GetBytes(s).ToList();
        if (s7) chars.InsertRange(0, new[] { (byte)(max == 0 ? s.Length : max), (byte)s.Length });
        if (chars.Count % 2 == 1) chars.Add(0);
        int at = 2 * (register - PlcTextBlock.StartRegister);
        for (int i = 0; i < chars.Count; i += 2)
            b.SetU16(at + i, highFirst ? (ushort)(chars[i] << 8 | chars[i + 1]) : (ushort)(chars[i + 1] << 8 | chars[i]));
        return b.Bytes;
    }

    private static PlcTextSpec Spec(int register = 501, int length = 40, PlcTextEncoding enc = PlcTextEncoding.Ascii,
                                    PlcByteOrder order = PlcByteOrder.HighFirst) =>
        new() { Register = register, Length = length, Encoding = enc, ByteOrder = order };

    private static TextLineSpec TextLine(PlcTextSpec? spec) => new() { Source = ExpectedSource.Plc, SourceName = "Text", PlcText = spec };

    [Fact]
    public void StringsAreReadInEitherByteOrderAndAsS7Strings()
    {
        const string lot = "B.NO. AX2391 MFG 10/2026 EXP 09/2028 MRP 145.00";   // longer than PartID's 30
        var t = new PlcText(Block(7, 501, lot), false, 7, false);
        Assert.True(t.Fresh);
        Assert.Equal(lot, t.Get(Spec(length: 60), out _));
        // odd length, padding spaces trimmed, a NUL ends the string
        Assert.Equal("LOT A12", new PlcText(Block(7, 510, "LOT A12    "), false, 7, false).Get(Spec(510, 11), out _));
        Assert.Equal("LOT", new PlcText(Block(7, 510, "LOT\0XYZ"), false, 7, false).Get(Spec(510, 7), out _));
        // cut to the line's length
        Assert.Equal("B.NO.", t.Get(Spec(length: 5), out _));
        Assert.Equal("LOT A12", new PlcText(Block(7, 520, "LOT A12", highFirst: false), false, 7, false)
            .Get(Spec(520, 7, order: PlcByteOrder.LowFirst), out _));
        // the wrong byte order is not silently right
        Assert.NotEqual("LOT A12", new PlcText(Block(7, 520, "LOT A12", highFirst: false), false, 7, false).Get(Spec(520, 7), out _));
        var s7 = new PlcText(Block(7, 600, "EXP 09/2028", s7: true, max: 20), false, 7, false);
        Assert.Equal("EXP 09/2028", s7.Get(Spec(600, 20, PlcTextEncoding.S7String), out _));
        // an S7 length beyond its maximum or the line's length is unreadable
        Assert.Null(new PlcText(Block(7, 600, "EXP 09/2028", s7: true, max: 5), false, 7, false).Get(Spec(600, 20, PlcTextEncoding.S7String), out var why));
        Assert.Contains("S7 string", why);
        Assert.Null(s7.Get(Spec(600, 8, PlcTextEncoding.S7String), out _));
    }

    [Fact]
    public void AStaleEmptyOrUnreadableStringIsMissing()
    {
        var block = Block(41, 501, "LOT A12");
        Assert.Null(new PlcText(block, false, 42, false).Get(Spec(), out var why));          // from the part before
        Assert.Contains("TriggerID 41", why);
        Assert.Null(new PlcText(block, false, 41, true).Get(Spec(), out why));                // TriggerID used again
        Assert.Contains("used before", why);
        Assert.Null(new PlcText(Block(0, 501, "LOT A12"), false, 0, false).Get(Spec(), out why));
        Assert.Contains("TriggerID 0", why);
        Assert.Null(new PlcText(Array.Empty<byte>(), false, 41, false).Get(Spec(), out why));  // protocol without one
        Assert.Contains("no text block", why);
        Assert.Null(new PlcText(Block(41, 501, ""), false, 41, false).Get(Spec(), out why));
        Assert.Contains("empty", why);
        Assert.Null(new PlcText(Block(41, 501, "    "), false, 41, false).Get(Spec(length: 4), out why));
        Assert.Contains("empty", why);
        Assert.Null(new PlcText(Block(41, 501, "LOTé12"), false, 41, false).Get(Spec(length: 6), out why));
        Assert.Contains("not printable", why);
        Assert.Null(new PlcText(Block(41, 501, "LOT\u000712"), false, 41, false).Get(Spec(length: 6), out why));
        // a place outside the block
        Assert.Null(new PlcText(block, false, 41, false).Get(Spec(620, 40), out why));
        Assert.Contains("registers 501 to 627", why);
    }

    [Fact]
    public void UserInAndTheTextBlockAreExpectedTexts()
    {
        var plc = new PlcValues("", 0) { UserIn = new[] { 0, 2391, -5, 0 }, Text = new PlcText(Block(9, 501, "AX2391?"), false, 9, false) };
        var ctx = new RunContext(plc);
        var batch = new BatchFields();
        Assert.Equal("2391", TextTemplate.Pattern(new TextLineSpec { Source = ExpectedSource.Plc, SourceName = "userin2" }, 0, "T", batch, ctx));
        Assert.Equal("-5", TextTemplate.Pattern(new TextLineSpec { Source = ExpectedSource.Plc, SourceName = "UserIn3" }, 0, "T", batch, ctx));
        Assert.Equal(@"AX2391\?", TextTemplate.Pattern(TextLine(Spec(length: 7)), 0, "T", batch, ctx));
        // UserIn 0 is no value; a text line without its place, without a block, or stale fails
        Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(new TextLineSpec { Source = ExpectedSource.Plc, SourceName = "UserIn1" }, 0, "T", batch, ctx));
        Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(new TextLineSpec { Source = ExpectedSource.Plc, SourceName = "UserIn4" }, 0, "T", batch, new RunContext(new PlcValues("", 0))));
        Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(TextLine(null), 0, "T", batch, ctx));
        Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(TextLine(Spec()), 0, "T", batch, new RunContext(new PlcValues("X", 0))));
        Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(TextLine(Spec()), 0, "T", batch, null));
        var stale = new RunContext(new PlcValues("", 0) { Text = new PlcText(Block(8, 501, "AX2391"), false, 9, false) });
        var e = Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(TextLine(Spec()), 0, "T", batch, stale));
        Assert.Contains("PLC text", e.Message);
    }

    [Fact]
    public void ThePlaceIsCheckedAtSetupAndKeptInTheJob()
    {
        TextToolConfig Cfg(TextLineSpec line) => new() { Name = "Lot", Lines = { line } };
        Assert.Null(TextTool.SetupProblem(Cfg(TextLine(Spec()))));
        Assert.Null(TextTool.SetupProblem(Cfg(new TextLineSpec { Source = ExpectedSource.Plc, SourceName = "UserIn4" })));
        Assert.Contains("say where", TextTool.SetupProblem(Cfg(TextLine(null))));
        Assert.Contains("registers 501 to 627", TextTool.SetupProblem(Cfg(TextLine(Spec(500)))));
        Assert.Contains("registers 501 to 627", TextTool.SetupProblem(Cfg(TextLine(Spec(627, 3)))));
        Assert.Null(TextTool.SetupProblem(Cfg(TextLine(Spec(627, 2)))));
        Assert.Contains("length", TextTool.SetupProblem(Cfg(TextLine(Spec(length: 0)))));
        Assert.Contains("length", TextTool.SetupProblem(Cfg(TextLine(Spec(length: 254, enc: PlcTextEncoding.S7String)))));
        Assert.Contains("the PLC sends no UserIn5", TextTool.SetupProblem(Cfg(new TextLineSpec { Source = ExpectedSource.Plc, SourceName = "UserIn5" })));

        var job = new Job { Name = "Lot", Cameras = { new CameraConfig { Name = "Top" } },
                            Tools = { Cfg(TextLine(Spec(530, 24, PlcTextEncoding.S7String, PlcByteOrder.LowFirst))) } };
        var json = JsonSerializer.Serialize(job, JobStore.Json);
        var back = Assert.IsType<TextToolConfig>(JsonSerializer.Deserialize<Job>(json, JobStore.Json)!.Tools[0]).Lines[0].PlcText!;
        Assert.Equal((530, 24, PlcTextEncoding.S7String, PlcByteOrder.LowFirst), (back.Register, back.Length, back.Encoding, back.ByteOrder));
        // lines without a PLC text keep their old form
        Assert.DoesNotContain("PlcText", JsonSerializer.Serialize(new TextLineSpec(), JobStore.Json));
    }

    private sealed class Host : IPlcHost
    {
        public readonly List<PlcTrigger> Triggers = new();
        public bool CanInspect => true;
        public ushort ActiveJobNumber => 1;
        public bool Simulation => true;
        public int CameraCount => 1;
        public PlcInspection Inspect(PlcTrigger t)
        {
            lock (Triggers) Triggers.Add(t);
            return new PlcInspection(true, 0, 0, "", "", Array.Empty<float>());
        }
        public ushort LoadJob(ushort n) => 0;
        public PlcInspection InspectCamera(int camera, PlcTrigger t) => Inspect(t);
    }

    [Fact]
    public void TheTextBlockTravelsWithItsTriggerOverModbus()
    {
        var host = new Host();
        using var rt = new PlcRuntime(new ModbusServer(0, IPAddress.Loopback), host, new PlcOptions { WatchdogMs = 2000 });
        int port = int.Parse(rt.LinkName.Split("port ")[1]);
        using var plc = new PlcTestsAccess.Client(port);
        var inB = new PlcBlock(PlcInput.Size, false);
        ushort hb = 0;
        void Send() { inB.SetU16(2, ++hb); Assert.Equal(16, plc.Write(inB.Bytes)); }
        PlcOutput Out()
        {
            var b = new PlcBlock(PlcOutput.Size, false);
            plc.Read(100, 125).CopyTo(b.Bytes, 0);
            plc.Read(225, 3).CopyTo(b.Bytes, 250);
            return new PlcOutput(b);
        }
        void Wait(Func<PlcOutput, bool> cond)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond(Out())) { Assert.True(sw.ElapsedMilliseconds < 3000, "PLC output did not reach the expected state in time"); Thread.Sleep(5); }
        }
        PlcTrigger Trigger(ushort id)
        {
            int before;
            lock (host.Triggers) before = host.Triggers.Count;
            inB.SetU16(8, id);
            inB.SetBit(0, 0, true);
            Send();
            Wait(o => o.Done);
            inB.SetBit(0, 0, false);
            Send();
            Wait(o => !o.Done && o.Ready);
            lock (host.Triggers) { Assert.Equal(before + 1, host.Triggers.Count); return host.Triggers[^1]; }
        }

        inB.SetBit(0, 3, true);                         // RunEnable
        inB.SetU32(48 + 4, 2391);                       // UserIn2
        for (int i = 0; i < 4; i++) { Send(); Thread.Sleep(20); }
        Wait(o => o.Ready);

        // the PLC writes the string, then the stamp, then the trigger
        var spec = Spec(length: 40);
        const string lot = "B.NO. AX2391 MFG 10/2026 EXP 09/2028";
        Assert.Equal(16, plc.WriteAt(501, Block(0, 501, lot)[2..42]));
        Assert.Equal(16, plc.WriteAt(500, new byte[] { 11, 0 }));
        var t = Trigger(11);
        Assert.Equal(lot, PlcValues.Of(t).GetText(spec, out _));
        Assert.Equal("2391", PlcValues.Of(t).Get("UserIn2"));

        // the same TriggerID again: stale
        t = Trigger(11);
        Assert.Null(PlcValues.Of(t).GetText(spec, out var why));
        Assert.Contains("used before", why);
        // a new trigger without a new stamp: the string is the part before's
        t = Trigger(12);
        Assert.Null(PlcValues.Of(t).GetText(spec, out why));
        Assert.Contains("TriggerID 11", why);
        // stamped for the new part: read
        Assert.Equal(16, plc.WriteAt(500, new byte[] { 13, 0 }));
        Assert.Equal(lot, PlcValues.Of(Trigger(13)).GetText(spec, out _));
    }
}
