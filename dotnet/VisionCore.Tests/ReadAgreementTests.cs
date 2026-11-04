using System.Text.Json;
using VisionCore;
using Xunit;

/// <summary>Read text safety: a second reader must read the same text, and the line confidence (vs_agree).</summary>
public class ReadAgreementTests
{
    private static readonly string? Family = new[] { "Arial", "Helvetica", "DejaVu Sans", "Liberation Sans" }
        .FirstOrDefault(f => FontRender.Families().Contains(f, StringComparer.OrdinalIgnoreCase));

    // needs the engine built (engine/build); skipped when the native library is not there
    private static bool EngineBuilt => File.Exists(Path.Combine(AppContext.BaseDirectory, "libvs_ocr.dylib"))
                                       || File.Exists(Path.Combine(AppContext.BaseDirectory, "vs_ocr.dll"));

    private static OcrChar Ch(string c, float score, CharStatus status = CharStatus.Ok) => new(c, c, score, status, new VisionCore.PointF[4]);

    [Fact]
    public void ReadTextNeedsASecondReader()
    {
        var read = new TextToolConfig { Name = "Lot", Mode = TextMode.Read, Lines = { new TextLineSpec { Entry = LineEntry.Format, Template = "LOT A{D:5}" } } };
        Assert.Equal(ReadAgreement.Auto, read.Agreement);
        Assert.Null(TextTool.SetupProblem(read));
        read.Agreement = ReadAgreement.ShiftOnly;
        Assert.Null(TextTool.SetupProblem(read));
        read.Agreement = ReadAgreement.Off;
        Assert.Contains("second reader", TextTool.SetupProblem(read));
        read.Agreement = (ReadAgreement)7;
        Assert.Contains("second reader", TextTool.SetupProblem(read));
        Assert.Equal(ReadAgreement.Auto, read.ToSettings().Agreement);   // the engine never gets an unknown mode
        // Check text compares with the expected text: the setting is not used there
        var check = new TextToolConfig { Name = "Lot", Mode = TextMode.Check, Agreement = ReadAgreement.Off, Lines = { new TextLineSpec { Template = "LOT {LOT}" } } };
        Assert.Null(TextTool.SetupProblem(check));
    }

    [Fact]
    public void TheAgreementTravelsWithTheJob()
    {
        var job = new Job
        {
            Name = "Dates",
            Cameras = { new CameraConfig { Name = "Top" } },
            Tools = { new TextToolConfig { Name = "Lot", Mode = TextMode.Read, Agreement = ReadAgreement.ShiftOnly } },
        };
        var back = Assert.IsType<TextToolConfig>(JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, JobStore.Json), JobStore.Json)!.Tools[0]);
        Assert.Equal(ReadAgreement.ShiftOnly, back.Agreement);
        // a job saved before the setting: two readers must agree
        var old = JsonSerializer.Deserialize<TextToolConfig>("{\"Name\":\"Lot\",\"Mode\":\"Read\"}", JobStore.Json)!;
        Assert.Equal(ReadAgreement.Auto, old.Agreement);
    }

    [Fact]
    public void TheOperatorIsToldWhichCharacterTheReadersDisagreeOn()
    {
        var corners = new VisionCore.PointF[4];
        var differ = new OcrLine("LOT A12345", "", false, false, 59, new[] { Ch("L", 99), Ch("O", 99), Ch("T", 99), Ch("A", 99), Ch("1", 99),
                                 Ch("2", 99), Ch("3", 99), Ch("4", 99), Ch("5", 59, CharStatus.Unsure) }, corners, 0, 0, 20)
                     { Agreement = AgreeState.Different };
        var (text, explain) = TextTool.DescribeRead(differ);
        Assert.Equal("explain.ocr.unsure", explain);
        Assert.Contains("Character 9 of \"LOT A12345\" is unclear (read as 5, 59): the second reader reads it otherwise", text);
        // every character sure, the line not (a dropped mark the line confidence sees)
        var doubtful = new OcrLine("EXP", "", false, false, 41, new[] { Ch("E", 99), Ch("X", 99), Ch("P", 99) }, corners, 0, 0, 20) { LineConfidence = 41 };
        Assert.Equal(("\"EXP\" is unclear (41). Check the print", "explain.ocr.unsure"), TextTool.DescribeRead(doubtful));
        Assert.Equal("explain.ocr.cut_short", TextTool.DescribeRead(doubtful with { CutShort = true }).Explain);
    }

    [Fact]
    public void ReadTextPassesOnlyWhatBothReadersRead()
    {
        if (Family is null || !EngineBuilt) return;
        var models = FindModels();
        if (models is null) return;
        var cfg = new TextToolConfig
        {
            Name = "Lot", Mode = TextMode.Read, CharHeightPx = 30, Print = PrintType.Solid, TimeBudgetMs = 3000,
            Lines = { new TextLineSpec { Entry = LineEntry.Format, Template = "LOT A{D:5}" } },
        };
        using (var tool = new TextTool(cfg, new ModelPaths(models)))
        {
            var r = tool.Run(Print("LOT A12345"), new BatchFields());
            Assert.True(r.Pass, r.Summary);
            var line = Assert.Single(r.Lines!);
            Assert.True(line.Pass);
            Assert.Equal(AgreeState.Same, line.Agreement);
            Assert.True(line.SecondReaders != SecondReaders.None);
            Assert.InRange(line.Quality, cfg.MinQuality, line.LineConfidence);
            // nothing printed: NOT OK, the format's line is not there
            var blank = tool.Run(new GrayImage(Enumerable.Repeat((byte)230, 400 * 120).ToArray(), 400, 120), new BatchFields());
            Assert.False(blank.Pass);
            Assert.Equal("explain.ocv.mismatch", blank.Explain);
        }
        // a second model installed beside the first is read as the second reader
        var two = Directory.CreateTempSubdirectory("vs-agree-");
        try
        {
            foreach (var f in new[] { "ocrnet.onnx", "ocr_charset.txt" }) File.Copy(Path.Combine(models, f), Path.Combine(two.FullName, f));
            File.Copy(Path.Combine(models, "ocrnet.onnx"), Path.Combine(two.FullName, "ocrnet_b.onnx"));
            using var tool = new TextTool(cfg, new ModelPaths(two.FullName));
            var r = tool.Run(Print("LOT A12345"), new BatchFields());
            Assert.True(r.Pass, r.Summary);
            Assert.Equal(SecondReaders.SecondModel, r.Lines![0].SecondReaders);
            // a second model that cannot be loaded fails every part, never a quiet fall-back
            File.WriteAllText(Path.Combine(two.FullName, "ocrnet_b.onnx"), "not a model");
            using var broken = new TextTool(cfg, new ModelPaths(two.FullName));
            var b = broken.Run(Print("LOT A12345"), new BatchFields());
            Assert.False(b.Pass);
            Assert.Equal("explain.format.setup", b.Explain);
        }
        finally { two.Delete(true); }
    }

    private static GrayImage Print(string text)
    {
        var line = FontRender.Draw(Family!, false, 30, text, gapEm: 0);
        int w = line.Width + 80, h = line.Height + 60;
        var px = Enumerable.Repeat((byte)230, w * h).ToArray();
        for (int y = 0; y < line.Height; y++)
            for (int x = 0; x < line.Width; x++)
                px[(y + 30) * w + x + 40] = (byte)Math.Max(20, line.Pixels[y * line.Width + x] * 230 / 255);
        return new GrayImage(px, w, h);
    }

    private static string? FindModels()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var m = Path.Combine(d.FullName, "ml", "models");
            if (File.Exists(Path.Combine(m, "ocrnet.onnx"))) return m;
        }
        return null;
    }
}
