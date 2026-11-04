using CodeFormat = BarcodeReading.CodeFormat;
using VisionCore;
using Xunit;
using Xunit.Abstractions;

/// <summary>Auto find on the whole image (requirements AS-01): codes with their type, text blocks with each line's field type.</summary>
public class AutoFindTests
{
    private readonly ITestOutputHelper _out;
    public AutoFindTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("MFG 10/2026", "production date")]
    [InlineData("EXP 09/2028", "expiry date")]
    [InlineData("12/10/2026", "production date")]
    [InlineData("B.NO. AB123", "lot / batch")]
    [InlineData("BATCH 4471", "lot / batch")]
    [InlineData("B.NOAB123", "lot / batch")]
    [InlineData("LOT:A12", "lot / batch")]
    [InlineData("MRP RS. 120.00", "price")]
    [InlineData("₹ 45", "price")]
    [InlineData("10:45", "time")]
    [InlineData("A1234567", "number / code")]
    [InlineData("MADE IN INDIA", "text")]
    public void EachLineGetsAFieldType(string read, string field)
    {
        Assert.Equal(field, AutoFind.LineField(read, TextFormat.Propose(read)));
    }

    [Fact]
    public void ALineWithAThirdDateIsADate()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MFD", "EXP" };
        Assert.Equal("date", AutoFind.LineField("PKG 01/2026", TextFormat.Propose("01/2026", used)));
        Assert.Equal("time", AutoFind.LineField("10:45", "{TIME:HH:mm+-3}"));
    }

    // a line read at (x, y), w wide, letters h high
    private static OcrLine Line(string text, float x, float y, float w, float h) =>
        new(text, "", false, true, 90, Array.Empty<OcrChar>(),
            new[] { new PointF(x, y), new PointF(x + w, y), new PointF(x + w, y + h), new PointF(x, y + h) }, 0, 0, h);

    [Fact]
    public void LinesCloseTogetherFormOneBlockAndFarOrDifferentPrintDoesNot()
    {
        var lines = new[]
        {
            Line("EXP 09/2028", 100, 140, 300, 40),   // second line of the block (given out of order)
            Line("MFG 10/2026", 100, 80, 300, 40),
            Line("B.NO. AB123", 110, 200, 280, 40),
            Line("MADE IN INDIA", 100, 700, 400, 40), // far below: its own block
            Line("NET WT 500G", 420, 90, 200, 12),    // next to the block but much smaller print
        };
        var blocks = AutoFind.Blocks(lines);
        Assert.Equal(3, blocks.Count);
        Assert.Equal(new[] { "MFG 10/2026", "EXP 09/2028", "B.NO. AB123" }, blocks[0].Select(l => l.Text));
        Assert.Equal("NET WT 500G", Assert.Single(blocks[1]).Text);
        Assert.Equal("MADE IN INDIA", Assert.Single(blocks[2]).Text);
        Assert.Empty(AutoFind.Blocks(Array.Empty<OcrLine>()));
    }

    [Fact]
    public void PadKeepsTheAreaInsideTheImage()
    {
        var pts = new[] { new PointF(10, 10), new PointF(110, 50) };
        Assert.Equal(new RoiBox(0, 0, 150, 90), AutoFind.Pad(pts, 0.25, 400, 300));
        Assert.Equal(new RoiBox(0, 0, 100, 60), AutoFind.Pad(pts, 0.25, 100, 60));
    }

    // a font every development and line PC has; the tests that draw are skipped on a PC with none of these
    private static readonly string? Family = new[] { "Arial", "Helvetica", "DejaVu Sans", "Liberation Sans" }
        .FirstOrDefault(f => FontRender.Families().Contains(f, StringComparer.OrdinalIgnoreCase));

    // needs the engine built (engine/build); skipped when the native libraries are not there
    private static bool EngineBuilt => File.Exists(Path.Combine(AppContext.BaseDirectory, "libvs_ocr.dylib"))
                                       || File.Exists(Path.Combine(AppContext.BaseDirectory, "vs_ocr.dll"));

    private static string? FindModels()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var m = Path.Combine(d.FullName, "ml", "models");
            if (File.Exists(Path.Combine(m, "ocrnet.onnx"))) return m;
        }
        return null;
    }

    // A label: Code 39 "*AB12*" at the top (narrow 4 px, wide 10 px, dark 30 on light 255) and three
    // printed lines below it, apart from the barcode, nothing drawn by the user.
    private static GrayImage Label(string family)
    {
        int w = 900, h = 640;
        var px = Enumerable.Repeat((byte)255, w * h).ToArray();
        var table = new Dictionary<char, string>
        {
            ['*'] = "nwnnwnwnn", ['A'] = "wnnnnwnnw", ['B'] = "nnwnnwnnw", ['1'] = "wnnwnnnnw", ['2'] = "nnwwnnnnw",
        };
        int x = 80;
        foreach (var ch in "*AB12*")
        {
            for (int k = 0; k < 9; k++)
            {
                int width = table[ch][k] == 'w' ? 10 : 4;
                if (k % 2 == 0)
                    for (int y = 40; y < 160; y++)
                        for (int i = x; i < x + width; i++) px[y * w + i] = 30;
                x += width;
            }
            x += 4;   // gap between characters
        }
        // the printed lines, 30 px capitals, about 1.9 letter heights apart (darker pixel wins where margins overlap)
        int top = 330;
        foreach (var s in new[] { "MFG 10/2026", "EXP 09/2028", "B.NO. AB123" })
        {
            var line = FontRender.Draw(family, false, 30, s, 0);
            for (int y = 0; y < line.Height && top + y < h; y++)
                for (int i = 0; i < line.Width && 60 + i < w; i++)
                {
                    int at = (top + y) * w + 60 + i;
                    px[at] = Math.Min(px[at], line.Pixels[y * line.Stride + i]);
                }
            top += 56;
        }
        return new GrayImage(px, w, h);
    }

    [Fact]
    public void TheWholeLabelGivesTheCodeAndTheTextFields()
    {
        if (Family is null || !EngineBuilt) return;
        var models = FindModels();
        if (models is null) return;
        var image = Label(Family);
        var found = AutoFind.Suggest(image, new ModelPaths(models));
        foreach (var s in found) _out.WriteLine($"{s.Kind} {s.Summary}: {string.Join(" | ", s.Read.Zip(s.Fields, (r, f) => $"{r} = {f}"))}");

        var code = Assert.Single(found, s => s.Kind == SuggestionKind.Code);
        var cfg = Assert.IsType<CodeToolConfig>(code.Tool);
        Assert.Equal((CodeFormat.Code39, 1, "AB12"), (cfg.Format, cfg.ExpectedCount, code.Read[0]));
        Assert.NotNull(cfg.Roi);

        var text = found.Where(s => s.Kind == SuggestionKind.Text).ToList();
        Assert.NotEmpty(text);
        var fields = text.SelectMany(s => s.Fields).ToList();
        Assert.Contains("production date", fields);
        Assert.Contains("expiry date", fields);
        Assert.Contains("lot / batch", fields);
        // each text suggestion is a Read text tool with a format per line and its area set
        Assert.All(text, s =>
        {
            var t = Assert.IsType<TextToolConfig>(s.Tool);
            Assert.Equal(TextMode.Read, t.Mode);
            Assert.Equal(s.Read.Count, t.Lines.Count);
            Assert.NotNull(t.Roi);
        });
        // the code's own text is not offered as a text field
        Assert.DoesNotContain(text.SelectMany(s => s.Read), r => r.Replace(" ", "") == "AB12");
    }

    [Fact]
    public void ADraggedWindowOffersOnlyWhatLiesInIt()
    {
        if (Family is null || !EngineBuilt) return;
        var models = FindModels();
        if (models is null) return;
        var image = Label(Family);
        // round the barcode only: the code, no text
        var code = AutoFind.Suggest(image, new ModelPaths(models), area: new RoiBox(40, 20, 400, 170));
        Assert.Equal(SuggestionKind.Code, Assert.Single(code).Kind);
        Assert.Equal("AB12", code[0].Read[0]);
        // round the printed lines only: text, no code
        var text = AutoFind.Suggest(image, new ModelPaths(models), area: new RoiBox(30, 300, 600, 200));
        Assert.NotEmpty(text);
        Assert.All(text, s => Assert.Equal(SuggestionKind.Text, s.Kind));
        // a window off the picture is the whole picture
        Assert.Contains(AutoFind.Suggest(image, new ModelPaths(models), area: new RoiBox(5000, 5000, 100, 100)), s => s.Kind == SuggestionKind.Code);
    }

    [Fact]
    public void ABlankImageSuggestsNothing()
    {
        if (!EngineBuilt) return;
        var models = FindModels();
        if (models is null) return;
        var image = new GrayImage(Enumerable.Repeat((byte)200, 400 * 300).ToArray(), 400, 300);
        Assert.Empty(AutoFind.Suggest(image, new ModelPaths(models)));
    }

    // Sample images (customer data, kept outside the repository): set VS_IMAGES to see what is found on each
    [Fact]
    public void SampleImagesPrintWhatIsFound()
    {
        var dir = Environment.GetEnvironmentVariable("VS_IMAGES");
        if (dir is null || !Directory.Exists(dir) || !EngineBuilt) return;
        var models = FindModels();
        if (models is null) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.bmp", SearchOption.AllDirectories).Order())
        {
            GrayImage image;
            try { image = GrayImage.Load(f); }
            catch (InvalidDataException) { continue; }   // a PNG saved with a .bmp name: not this test's concern
            var found = AutoFind.Suggest(image, new ModelPaths(models));
            _out.WriteLine($"{Path.GetFileName(f)}: {string.Join("; ", found.Select(s => s.Summary))}");
        }
    }
}
