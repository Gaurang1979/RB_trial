using BarcodeReading;
using VisionCore;
using Xunit;

/// <summary>A job set up so that no part can pass is refused when it is saved (Job.Validate), not NG on every part.</summary>
public class JobSetupTests : IDisposable
{
    // the AI recogniser's characters, as ml/models/ocr_charset.txt (no ₹ here, to test a missing one)
    private const string AiChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz .,:/-#%()+&*'¥";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs_setup_" + Guid.NewGuid().ToString("N"));
    private readonly ModelPaths _models;

    public JobSetupTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Path.Combine(_dir, "ocr_charset.txt"), AiChars.Select(c => c.ToString()));
        _models = new ModelPaths(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Font(string chars) => "VSFONT1\n" + string.Concat(chars.Select(c => $"{(int)c} 16 00\n"));

    private static TextToolConfig Read(string format, string? font = null) => new()
    {
        Name = "Date", Mode = TextMode.Read, Reader = font is null ? OcrReader.Ai : OcrReader.Font, Font = font ?? "",
        Lines = { new TextLineSpec { Entry = LineEntry.Format, Template = format } },
    };

    private static TextToolConfig Report(TextToolConfig t)
    {
        t.ReportOnly = true;
        return t;
    }

    private static Job JobWith(ToolConfig tool) => new() { Name = "Case", Cameras = { new CameraConfig { Name = "Top" } }, Tools = { tool } };

    [Fact]
    public void AFormatCharacterTheTaughtFontLacksIsRefused()
    {
        var digits = "0123456789";
        Assert.Contains("The taught font cannot read L O T", TextTool.SetupProblem(Read("LOT {D:5}", Font(digits))));
        // either case of a taught letter reads it, as the format ignores case
        Assert.Null(TextTool.SetupProblem(Read("lot {D:5}", Font(digits + "LOT"))));
        var e = Assert.Throws<ArgumentException>(() => JobWith(Read("LOT {D:5}", Font(digits))).Validate());
        Assert.StartsWith("Date: Line 1: The taught font cannot read", e.Message);
    }

    [Fact]
    public void ADigitFieldNeedsEveryDigit()
    {
        // a lot number or a date may hold any digit: a font without the 7 fails every part that prints one
        Assert.Contains("has no 7", TextTool.SetupProblem(Read("{D:5}", Font("012345689"))));
        Assert.Contains("has no 7", TextTool.SetupProblem(Read("{EXP:MM/YY}", Font("012345689/"))));
        Assert.Contains("reads no letters", TextTool.SetupProblem(Read("{D:5}{A:1}", Font("0123456789"))));
        Assert.Null(TextTool.SetupProblem(Read("{D:5}{X:2}", Font("0123456789"))));
    }

    [Fact]
    public void EveryMonthNeedsOneSpellingTheReaderReads()
    {
        // a font taught on a print of JAN cannot read the other months
        var problem = TextTool.SetupProblem(Read("{EXP:MMM YY}", Font("0123456789JAN")));
        Assert.Contains("cannot read the month FEB", problem);
        // the AI reader has no accents, but every French month has a spelling without them
        var fr = Read("{EXP:MMM YY}");
        fr.MonthLanguages = new() { "fr" };
        Assert.Null(TextTool.SetupProblem(fr, _models));
    }

    [Fact]
    public void TheAiReadersCharactersAreCheckedWhenTheModelsAreKnown()
    {
        var mrp = Read("MRP ₹{D:3}");
        Assert.Null(TextTool.SetupProblem(mrp));          // without the models the AI's characters are not known
        Assert.Contains("The AI reader cannot read ₹", TextTool.SetupProblem(mrp, _models));
        Assert.Null(TextTool.SetupProblem(Read("MRP ¥{D:3}"), _models));
        Assert.Throws<ArgumentException>(() => JobWith(mrp).Validate(_models));
        // the store saves with the models it was given
        var jobs = Path.Combine(_dir, "jobs");
        Assert.Throws<ArgumentException>(() => new JobStore(jobs, _models).Save(JobWith(mrp), "test"));
        Assert.Equal(1, new JobStore(jobs).Save(JobWith(mrp), "test").Version);
    }

    [Fact]
    public void AnAiFormatItCanNeverReadFailsAtSetupWithItsReason()
    {
        // a job saved without the models (an older job, a store without them) reaches the line unchecked:
        // the tool then fails every part with the reason, as a setup error, never reading under the format
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "libvs_ocr.dylib")) && !File.Exists(Path.Combine(AppContext.BaseDirectory, "vs_ocr.dll"))) return;
        string? models = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && models is null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ml", "models", "ocrnet.onnx"))) models = Path.Combine(d.FullName, "ml", "models");
        if (models is null) return;
        var image = new GrayImage(Enumerable.Repeat((byte)200, 320 * 120).ToArray(), 320, 120);
        using (var tool = new TextTool(Read("PRICE €{D:3}"), new ModelPaths(models)))
        {
            var r = tool.Run(image, new BatchFields());
            Assert.False(r.Pass);
            Assert.Equal("explain.format.setup", r.Explain);
            Assert.Contains("The AI reader cannot read €", r.Summary);
        }
        var check = new TextToolConfig { Name = "Lot", Lines = { new TextLineSpec { Template = "LOT@{BATCH}" } } };
        using (var tool = new TextTool(check, new ModelPaths(models)))
            Assert.Contains("Line 1: The AI reader cannot read @", tool.Run(image, new BatchFields()).Summary);
        // a format it can read is not a setup error (nothing printed here: NG for that reason)
        using (var tool = new TextTool(Read("LOT {D:5}"), new ModelPaths(models)))
            Assert.NotEqual("explain.format.setup", tool.Run(image, new BatchFields()).Explain);
    }

    [Fact]
    public void AFormatTooBigForTheDecoderIsRefused()
    {
        // each {?} place is a node per character of the charset; a 2048 px line (512 columns) times the
        // nodes must stay within the decoder's 16e6 cells
        // (report only: a format of {?} alone does not decide OK/NG)
        Assert.Null(TextTool.SetupProblem(Report(Read("{?:64} {?:64}")), _models));
        Assert.Null(TextTool.SetupProblem(Report(Read(string.Join(" ", Enumerable.Repeat("{?:64}", 6)))), _models));
        Assert.Contains("too many characters", TextTool.SetupProblem(Report(Read(string.Join(" ", Enumerable.Repeat("{?:64}", 7)))), _models));
        // and at most 1024 places, even of digits
        Assert.Contains("too many characters", TextTool.SetupProblem(Read(string.Join("", Enumerable.Repeat("{D:64}", 17))), _models));
        Assert.Null(TextTool.SetupProblem(Read(string.Join("", Enumerable.Repeat("{D:64}", 15))), _models));
    }

    [Fact]
    public void CheckTextFixedCharactersTheReaderLacksAreRefused()
    {
        var tool = new TextToolConfig
        {
            Name = "Lot", Mode = TextMode.Check, Reader = OcrReader.Font, Font = Font("0123456789LOT"),
            Lines = { new TextLineSpec { Template = "LOT {BATCH}" } },
        };
        Assert.Null(TextTool.SetupProblem(tool));         // the batch value is checked when read
        tool.Lines.Add(new TextLineSpec { Template = "Mfd {DATE:MM/YY}" });
        Assert.Contains("Line 2: The taught font cannot read M f d", TextTool.SetupProblem(tool));
        tool.Lines[1].Template = " ";
        Assert.Contains("Line 2: the text is empty", TextTool.SetupProblem(tool));
    }

    [Fact]
    public void StrictnessOutsideOneTo99IsRefused()
    {
        var t = Read("{D:5}");
        foreach (var q in new[] { 0f, 100f, -5f, float.NaN })
        {
            t.MinQuality = q;
            Assert.Equal("Strictness must be 1 to 99", TextTool.SetupProblem(t));
        }
        t.MinQuality = 99;
        Assert.Null(TextTool.SetupProblem(t));
    }

    [Fact]
    public void ACodeLengthLockThatReadsNothingIsRefused()
    {
        var c = new CodeToolConfig { Name = "Box", Format = CodeFormat.Gs1_128, MinLength = 14, MaxLength = 12 };
        Assert.Contains("Fewest characters (14) is more than most characters (12)", CodeTool.SetupProblem(c));
        c.MaxLength = 0;                                  // 0 = any
        Assert.Null(CodeTool.SetupProblem(c));
        c.MinLength = -1;
        Assert.NotNull(CodeTool.SetupProblem(c));
        Assert.Equal("Pick the code type to read", CodeTool.SetupProblem(new CodeToolConfig { Name = "None", Format = 0 }));
        Assert.Throws<ArgumentException>(() => JobWith(new CodeToolConfig { Name = "None", Format = 0 }).Validate());
    }

    [Fact]
    public void AFindThePartMatchOutsideZeroToOneIsRefused()
    {
        foreach (var s in new[] { 0f, 1.5f, float.NaN })
            Assert.Contains("lowest match", Assert.Throws<ArgumentException>(() => JobWith(new LocateToolConfig { Name = "Find", MinScore = s }).Validate()).Message);
        JobWith(new LocateToolConfig { Name = "Find", MinScore = 1 }).Validate();
    }
}
