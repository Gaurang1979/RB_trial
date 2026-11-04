using System.Security.Cryptography;
using System.Text.Json;
using VisionCore;
using Xunit;

public class TemplateTests
{
    [Fact]
    public void TemplateFillsFixedFieldsAndKeepsClockFields()
    {
        var spec = new TextLineSpec { Template = "¥ {MFD}, # {EXP} {TIME:HH:mm+-3}" };
        var batch = new BatchFields();
        batch.Values["MFD"] = "10/26";
        batch.Values["EXP"] = "09/28";
        Assert.Equal("¥ 10/26, # 09/28 {TIME:HH:mm+-3}", TextTemplate.Pattern(spec, 0, "Text 1", batch));
    }

    [Fact]
    public void FieldValuesAreLiteral()
    {
        // a batch value with ? or { must be printed as typed, not read as a wildcard or field
        var spec = new TextLineSpec { Template = "LOT {BATCH}" };
        var batch = new BatchFields();
        batch.Values["BATCH"] = "A?{1}";
        Assert.Equal("LOT A\\?\\{1}", TextTemplate.Pattern(spec, 0, "t", batch));
    }

    [Fact]
    public void FullTextKeepsWildcards()
    {
        var spec = new TextLineSpec { Entry = LineEntry.FullText };
        var batch = new BatchFields();
        batch.Values[TextTemplate.FullTextKey("Date", 1)] = "EXP 09/28 ??:??";
        Assert.Equal("EXP 09/28 ??:??", TextTemplate.Pattern(spec, 1, "Date", batch));
    }

    [Fact]
    public void MissingFieldAsksForIt()
    {
        var spec = new TextLineSpec { Template = "₹ {MRP}" };
        var e = Assert.Throws<MissingFieldException>(() => TextTemplate.Pattern(spec, 0, "t", new BatchFields()));
        Assert.Contains("MRP", e.Message);
    }

    [Fact]
    public void FixedFieldsSkipClock()
    {
        Assert.Equal(new[] { "MRP", "BATCH" }, TextTemplate.FixedFields("₹ {MRP}, {BATCH} {TIME:HH:mm+-3} {DATE:MM/YY}"));
    }
}

public class JobTests
{
    private static Job Sample() => new()
    {
        Name = "Harpic 200ml",
        Cameras = { new CameraConfig { Name = "Top", Serial = "12345" }, new CameraConfig { Name = "Side" } },
        Tools =
        {
            new CodeToolConfig { Name = "EAN", Camera = 0, Format = BarcodeReading.CodeFormat.Ean13, Expected = "{GTIN}" },
            new TextToolConfig
            {
                Name = "Batch print", Camera = 1, Roi = new RoiBox(450, 700, 1100, 550), CharHeightPx = 38,
                Lines =
                {
                    new TextLineSpec { Template = "₹ {MRP}, {BATCH}" },
                    new TextLineSpec { Template = "₹ 0.23 per ml" },
                    new TextLineSpec { Entry = LineEntry.FullText },
                },
            },
        },
    };

    [Fact]
    public void JsonRoundTripKeepsCodeTypeAndOneDRules()
    {
        var job = new Job
        {
            Name = "Case label",
            Tools =
            {
                new CodeToolConfig
                {
                    Name = "ITF", Format = BarcodeReading.CodeFormat.Itf, MinLength = 14, MaxLength = 14,
                    CheckDigit = BarcodeReading.CheckDigitRule.Verify,
                },
                new CodeToolConfig { Name = "Two types", Format = BarcodeReading.CodeFormat.QrCode | BarcodeReading.CodeFormat.MicroQr },
            },
        };
        var back = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, JobStore.Json), JobStore.Json)!;
        var itf = Assert.IsType<CodeToolConfig>(back.Tools[0]);
        Assert.Equal(BarcodeReading.CodeFormat.Itf, itf.Format);
        Assert.Equal((14, 14, BarcodeReading.CheckDigitRule.Verify), (itf.MinLength, itf.MaxLength, itf.CheckDigit));
        Assert.Equal(BarcodeReading.CodeFormat.QrCode | BarcodeReading.CodeFormat.MicroQr, ((CodeToolConfig)back.Tools[1]).Format);
    }

    [Fact]
    public void JsonRoundTripKeepsToolKinds()
    {
        var text = JsonSerializer.Serialize(Sample(), JobStore.Json);
        Assert.Contains("\"kind\": \"text\"", text);
        Assert.Contains("₹", text);                       // readable, not ₹
        var back = JsonSerializer.Deserialize<Job>(text, JobStore.Json)!;
        Assert.IsType<CodeToolConfig>(back.Tools[0]);
        var t = Assert.IsType<TextToolConfig>(back.Tools[1]);
        Assert.Equal(1, t.Camera);
        Assert.Equal(3, t.Lines.Count);
        Assert.Equal(LineEntry.FullText, t.Lines[2].Entry);
    }

    [Fact]
    public void BatchFieldsListEveryFieldOnce()
    {
        var keys = Sample().BatchFields().Select(f => f.Key).ToList();
        Assert.Equal(new[] { "GTIN", "MRP", "BATCH", TextTemplate.FullTextKey("Batch print", 2) }, keys);
    }

    [Fact]
    public void StoreKeepsEveryVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-jobs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(dir);
            store.Save(Sample(), "a");
            var j = store.Save(Sample(), "b");
            Assert.Equal(2, j.Version);
            Assert.True(File.Exists(Path.Combine(dir, "history", "Harpic 200ml", "v1.json")));
            Assert.Equal("b", store.Load("Harpic 200ml")!.UpdatedBy);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ToolOnMissingCameraIsRefused()
    {
        var j = Sample();
        j.Tools[0].Camera = 5;
        Assert.Throws<ArgumentException>(() => j.Validate());
    }

    [Fact]
    public void TimeFromCameraFileName()
    {
        Assert.Equal(new DateTime(2026, 10, 23, 15, 42, 34), GrayImage.TimeFromName("20261023_154234728.bmp"));
        Assert.Null(GrayImage.TimeFromName("part.bmp"));
    }
}

public class FixtureTests
{
    [Fact]
    public void ShiftMovesAreas()
    {
        var f = new Fixture(100, 100, 130, 90, 0);
        // moved 30 right and 10 up; the part above the image edge is cut off
        Assert.Equal(new RoiBox(30, 0, 50, 40), f.Map(new RoiBox(0, 0, 50, 50), 1000, 1000));
        var p = f.Map(10, 20);
        Assert.Equal(40, p.X, 3);
        Assert.Equal(10, p.Y, 3);
    }

    [Fact]
    public void TurnIsCounterClockwiseOnScreen()
    {
        // a point right of the centre, turned 90 degrees counter-clockwise on screen (y down), ends above it
        var f = new Fixture(100, 100, 100, 100, 90);
        var p = f.Map(110, 100);
        Assert.Equal(100, p.X, 3);
        Assert.Equal(90, p.Y, 3);
    }

    [Fact]
    public void LocatePatternIsStoredBesideTheJob()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-jobs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(dir);
            var loc = new LocateToolConfig { Name = "Find label", Roi = new RoiBox(10, 10, 20, 16), Pattern = new byte[20 * 16], PatternWidth = 20, PatternHeight = 16, OriginX = 20, OriginY = 18 };
            loc.Pattern[5] = 200;
            store.Save(new Job { Name = "J", Tools = { loc } }, "a");
            var back = Assert.IsType<LocateToolConfig>(store.Load("J")!.Tools[0]);
            Assert.Equal(20, back.PatternWidth);
            Assert.Equal(200, back.Pattern![5]);
            Assert.Equal(20, back.OriginX);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class CodeTeachTests
{
    [Fact]
    public void LocksTypeCountAndPrintedLength()
    {
        var code = new CodeToolConfig { Name = "Case", CheckDigit = BarcodeReading.CheckDigitRule.Strip };
        var read = new List<(BarcodeReading.CodeFormat, string)>
        {
            (BarcodeReading.CodeFormat.Code39, "VS-39AV"), (BarcodeReading.CodeFormat.Code39, "VS-40AW"),
            (BarcodeReading.CodeFormat.Ean13, "4006381333931"),
        };
        var mine = CodeTeach.Lock(code, read);
        Assert.Equal(2, mine.Count);
        Assert.Equal((BarcodeReading.CodeFormat.Code39, 2), (code.Format, code.ExpectedCount));
        // as printed, check digit included, whatever the strip setting (the engine counts it the same way)
        Assert.Equal((7, 7), (code.MinLength, code.MaxLength));
    }

    [Fact]
    public void FixedLengthTypesGetNoLockAndItfGetsItsCheckDigit()
    {
        var code = new CodeToolConfig { MinLength = 18, MaxLength = 18 };
        CodeTeach.Lock(code, new List<(BarcodeReading.CodeFormat, string)> { (BarcodeReading.CodeFormat.Ean8, "96385074") });
        Assert.Equal((0, 0), (code.MinLength, code.MaxLength));
        var itf = new CodeToolConfig();
        CodeTeach.Lock(itf, new List<(BarcodeReading.CodeFormat, string)> { (BarcodeReading.CodeFormat.Itf, "15400141288763") });
        Assert.Equal((14, 14, BarcodeReading.CheckDigitRule.Verify), (itf.MinLength, itf.MaxLength, itf.CheckDigit));
        var noCheck = new CodeToolConfig();
        CodeTeach.Lock(noCheck, new List<(BarcodeReading.CodeFormat, string)> { (BarcodeReading.CodeFormat.Itf, "123456") });
        Assert.Equal(BarcodeReading.CheckDigitRule.AsRead, noCheck.CheckDigit);
        Assert.Empty(CodeTeach.Lock(new CodeToolConfig(), new List<(BarcodeReading.CodeFormat, string)>()));
    }

    [Fact]
    public void CharsAreCodePoints() => Assert.Equal(3, CodeTeach.Chars("AB\U0001F600"));

    [Fact]
    public void ItfWithoutProtectionIsRefused()
    {
        Assert.NotNull(CodeTool.SetupProblem(new CodeToolConfig { Format = BarcodeReading.CodeFormat.Itf }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = BarcodeReading.CodeFormat.Itf, MinLength = 14 }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = BarcodeReading.CodeFormat.Itf, CheckDigit = BarcodeReading.CheckDigitRule.Verify }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = BarcodeReading.CodeFormat.Itf, Expected = "{GTIN14}" }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = BarcodeReading.CodeFormat.Code39 }));
    }
}

// Job bundle round trip: export on one line PC, import on another, load to run (Req 6.1 RUN-PAR-02; from the closed PR #239).
// Signed with the plant key, checked before anything is written, golden image per version (Req 17.11 AUD-06).
public sealed class JobStoreBundleTests : IDisposable
{
    private readonly string _dir1 = Path.Combine(Path.GetTempPath(), $"jobstore_src_{Guid.NewGuid():N}");
    private readonly string _dir2 = Path.Combine(Path.GetTempPath(), $"jobstore_dst_{Guid.NewGuid():N}");
    private readonly string _bundle = Path.Combine(Path.GetTempPath(), $"bundle_{Guid.NewGuid():N}.vsrecipe");
    private readonly ECDsa _plant = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly JobStore _store1, _store2;

    public JobStoreBundleTests()
    {
        _store1 = new JobStore(_dir1);
        _store2 = new JobStore(_dir2);
    }

    public void Dispose()
    {
        _plant.Dispose();
        if (File.Exists(_bundle)) File.Delete(_bundle);
        if (Directory.Exists(_dir1)) Directory.Delete(_dir1, true);
        if (Directory.Exists(_dir2)) Directory.Delete(_dir2, true);
    }

    private string Pem => _plant.ExportSubjectPublicKeyInfoPem();

    private static Job Sample(string? referenceImage = null) => new()
    {
        Name = "BlisterPouchLine",
        Number = 42,
        Description = "High speed blister sealing line",
        Cameras = [new CameraConfig { Name = "MainCam", Serial = "HIK123", ReferenceImage = referenceImage }],
        Tools =
        {
            new CodeToolConfig { Name = "DataMatrixCode", Format = BarcodeReading.CodeFormat.DataMatrix }
        }
    };

    // nothing at all written into the second store: no job, no history, no assets
    private void AssertStore2Untouched()
    {
        Assert.Empty(_store2.Names());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_dir2, "history")));
        Assert.False(Directory.Exists(Path.Combine(_dir2, "assets")));
    }

    [Fact]
    public void JobStore_ExportAndImportBundle_RoundtripsSuccessfully()
    {
        _store1.Save(Sample(), "EngineerA");

        string exported = _store1.ExportBundle("BlisterPouchLine", _plant, _bundle);
        Assert.True(File.Exists(exported));

        var imported = _store2.ImportBundle(exported, Pem, "EngineerB");

        Assert.Equal("BlisterPouchLine", imported.Name);
        Assert.Equal(42, imported.Number);
        Assert.Equal("EngineerB", imported.UpdatedBy);
        Assert.Single(imported.Tools);
        Assert.Equal("DataMatrixCode", imported.Tools[0].Name);

        var loaded = _store2.Load("BlisterPouchLine");
        Assert.NotNull(loaded);
        Assert.Equal(42, loaded.Number);
    }

    [Fact]
    public void ImportBundle_KeepsAGoldenImagePerVersion()
    {
        var png = Path.Combine(_dir1, "teach.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);
        _store1.Save(Sample(png), "EngineerA");
        _store1.ExportBundle("BlisterPouchLine", _plant, _bundle);

        var v1 = _store2.ImportBundle(_bundle, Pem, "EngineerB");
        var v2 = _store2.ImportBundle(_bundle, Pem, "EngineerC");

        Assert.Equal(Path.Combine(Path.GetFullPath(_dir2), "assets", "BlisterPouchLine", "golden_master.v1.png"), v1.Cameras[0].ReferenceImage);
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir2), "assets", "BlisterPouchLine", "golden_master.v2.png"), v2.Cameras[0].ReferenceImage);
        Assert.True(File.Exists(v1.Cameras[0].ReferenceImage));
        Assert.Equal(File.ReadAllBytes(png), File.ReadAllBytes(v2.Cameras[0].ReferenceImage!));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void ImportBundle_JobNameOutsideTheStore_IsRefusedBeforeWriting(string name)
    {
        var png = Path.Combine(_dir1, "teach.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);
        _store1.Save(Sample(png), "EngineerA");
        var json = File.ReadAllText(Path.Combine(_dir1, "BlisterPouchLine.json"))
            .Replace("\"BlisterPouchLine\"", System.Text.Json.JsonSerializer.Serialize(name));
        // signed with the right key: the name itself must be refused
        RecipeBundle.ExportToFile(_bundle, _plant, new RecipeManifest { Name = name }, json, File.ReadAllBytes(png));

        Assert.ThrowsAny<Exception>(() => _store2.ImportBundle(_bundle, Pem, "EngineerB"));
        AssertStore2Untouched();
        Assert.False(File.Exists(Path.Combine(_dir2, "escape.json")));
        Assert.False(Directory.Exists(Path.Combine(_dir2, "escape")));
    }

    [Fact]
    public void ImportBundle_UnsignedTamperedOrOtherKey_IsRefusedBeforeWriting()
    {
        _store1.Save(Sample(), "EngineerA");
        _store1.ExportBundle("BlisterPouchLine", _plant, _bundle);

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => _store2.ImportBundle(_bundle, other.ExportSubjectPublicKeyInfoPem(), "EngineerB"));
        AssertStore2Untouched();

        using (var zip = System.IO.Compression.ZipFile.Open(_bundle, System.IO.Compression.ZipArchiveMode.Update))
        {
            zip.GetEntry(RecipeBundle.ToolsEntry)!.Delete();
            using var s = zip.CreateEntry(RecipeBundle.ToolsEntry).Open();
            s.Write("{}"u8);
        }
        Assert.Throws<CryptographicException>(() => _store2.ImportBundle(_bundle, Pem, "EngineerB"));
        AssertStore2Untouched();

        using (var zip = System.IO.Compression.ZipFile.Open(_bundle, System.IO.Compression.ZipArchiveMode.Update))
            zip.GetEntry(RecipeBundle.SignatureEntry)!.Delete();
        Assert.Throws<CryptographicException>(() => _store2.ImportBundle(_bundle, Pem, "EngineerB"));
        AssertStore2Untouched();
    }

    [Fact]
    public void ImportBundle_NeedsTheUser()
    {
        _store1.Save(Sample(), "EngineerA");
        _store1.ExportBundle("BlisterPouchLine", _plant, _bundle);
        Assert.Throws<ArgumentException>(() => _store2.ImportBundle(_bundle, Pem, " "));
        AssertStore2Untouched();
    }

    // a taught pattern and a taught reference, with pixels that differ from place to place
    private static Job Taught(bool locateEnabled = true)
    {
        var job = Sample();
        var pattern = new byte[24 * 20];
        for (int i = 0; i < pattern.Length; i++) pattern[i] = (byte)(i * 7);
        var reference = new byte[32 * 16];
        for (int i = 0; i < reference.Length; i++) reference[i] = (byte)(255 - i);
        job.Tools.Insert(0, new LocateToolConfig { Name = "Find", Enabled = locateEnabled, Pattern = pattern, PatternWidth = 24, PatternHeight = 20 });
        job.Tools.Add(new PhaseToolConfig { Name = "Align", Roi = new RoiBox(10, 10, 32, 16), Reference = reference, ReferenceWidth = 32, ReferenceHeight = 16 });
        return job;
    }

    // tools.json as the first PC saved it, signed into a bundle with the pictures given
    private void SignedBundle(Dictionary<int, byte[]>? patterns, Dictionary<int, byte[]>? references)
    {
        var json = File.ReadAllText(Path.Combine(_dir1, "BlisterPouchLine.json"));
        RecipeBundle.ExportToFile(_bundle, _plant, new RecipeManifest { Name = "BlisterPouchLine" }, json, [1, 2, 3],
            patterns: patterns, references: references);
    }

    private byte[] Entry(string name)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(_bundle);
        using var s = zip.GetEntry(name)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void ImportBundle_CarriesTaughtPatternAndReference()
    {
        var job = Taught();
        _store1.Save(job, "EngineerA");
        _store1.ExportBundle("BlisterPouchLine", _plant, _bundle);

        var imported = _store2.ImportBundle(_bundle, Pem, "EngineerB");
        var locate = imported.Tools.OfType<LocateToolConfig>().Single();
        var align = imported.Tools.OfType<PhaseToolConfig>().Single();
        // saved as this PC's own files, not the other PC's names
        Assert.True(File.Exists(Path.Combine(_dir2, "assets", locate.PatternFile!)));
        Assert.True(File.Exists(Path.Combine(_dir2, "assets", align.ReferenceFile!)));

        // the job runs on this PC with the very pixels taught on the first one
        var loaded = _store2.Load("BlisterPouchLine")!;
        var l = loaded.Tools.OfType<LocateToolConfig>().Single();
        var r = loaded.Tools.OfType<PhaseToolConfig>().Single();
        Assert.Equal((24, 20), (l.PatternWidth, l.PatternHeight));
        Assert.Equal(((LocateToolConfig)job.Tools[0]).Pattern, l.Pattern);
        Assert.Equal((32, 16), (r.ReferenceWidth, r.ReferenceHeight));
        Assert.Equal(((PhaseToolConfig)job.Tools[^1]).Reference, r.Reference);
    }

    [Fact]
    public void ImportBundle_TamperedPattern_IsRefusedBeforeWriting()
    {
        _store1.Save(Taught(), "EngineerA");
        _store1.ExportBundle("BlisterPouchLine", _plant, _bundle);
        var bmp = Entry(RecipeBundle.PatternEntry(0));
        bmp[^1] ^= 0xFF;   // one pixel changed
        using (var zip = System.IO.Compression.ZipFile.Open(_bundle, System.IO.Compression.ZipArchiveMode.Update))
        {
            zip.GetEntry(RecipeBundle.PatternEntry(0))!.Delete();
            using var s = zip.CreateEntry(RecipeBundle.PatternEntry(0)).Open();
            s.Write(bmp);
        }
        var e = Assert.Throws<CryptographicException>(() => _store2.ImportBundle(_bundle, Pem, "EngineerB"));
        Assert.Contains(RecipeBundle.PatternEntry(0), e.Message);
        AssertStore2Untouched();
    }

    [Fact]
    public void ImportBundle_MissingPatternOrReference_IsRefusedBeforeWriting()
    {
        var job = Taught();
        _store1.Save(job, "EngineerA");
        var pattern = BarcodeReading.BmpImage.EncodeGray(((LocateToolConfig)job.Tools[0]).Pattern, 24, 20);
        var reference = BarcodeReading.BmpImage.EncodeGray(((PhaseToolConfig)job.Tools[^1]).Reference, 32, 16);
        int align = job.Tools.Count - 1;

        // a bundle made before the pictures were carried: it reads, but the job cannot be checked, so it never runs
        SignedBundle(null, null);
        var e = Assert.Throws<InvalidDataException>(() => _store2.ImportBundle(_bundle, Pem, "EngineerB"));
        Assert.Contains("Find", e.Message);
        AssertStore2Untouched();

        SignedBundle(new() { [0] = pattern }, null);
        e = Assert.Throws<InvalidDataException>(() => _store2.ImportBundle(_bundle, Pem, "EngineerB"));
        Assert.Contains("Align", e.Message);
        AssertStore2Untouched();

        SignedBundle(new() { [0] = pattern }, new() { [align] = reference });
        Assert.NotNull(_store2.ImportBundle(_bundle, Pem, "EngineerB"));
    }

    [Fact]
    public void ImportBundle_PicturesThatDoNotMatchTheJob_AreRefusedBeforeWriting()
    {
        var job = Taught();
        _store1.Save(job, "EngineerA");
        var pattern = BarcodeReading.BmpImage.EncodeGray(((LocateToolConfig)job.Tools[0]).Pattern, 24, 20);
        var reference = BarcodeReading.BmpImage.EncodeGray(((PhaseToolConfig)job.Tools[^1]).Reference, 32, 16);
        int align = job.Tools.Count - 1;

        // a picture for a tool that is not of its kind (the code tool), signed and all
        SignedBundle(new() { [0] = pattern, [1] = pattern }, new() { [align] = reference });
        Assert.Contains("matches no taught tool", Assert.Throws<InvalidDataException>(() => _store2.ImportBundle(_bundle, Pem, "EngineerB")).Message);
        AssertStore2Untouched();

        // a reference that is not the size of its area
        SignedBundle(new() { [0] = pattern }, new() { [align] = BarcodeReading.BmpImage.EncodeGray(new byte[16 * 16], 16, 16) });
        Assert.Throws<ArgumentException>(() => _store2.ImportBundle(_bundle, Pem, "EngineerB"));
        AssertStore2Untouched();

        // not a picture, or a header claiming more pixels than the bytes hold
        SignedBundle(new() { [0] = [1, 2, 3, 4] }, new() { [align] = reference });
        Assert.Contains("not a readable picture", Assert.Throws<InvalidDataException>(() => _store2.ImportBundle(_bundle, Pem, "EngineerB")).Message);
        var huge = (byte[])pattern.Clone();
        BitConverter.GetBytes(1 << 20).CopyTo(huge, 18);
        SignedBundle(new() { [0] = huge }, new() { [align] = reference });
        Assert.Contains("not a readable picture", Assert.Throws<InvalidDataException>(() => _store2.ImportBundle(_bundle, Pem, "EngineerB")).Message);
        AssertStore2Untouched();
    }

    [Fact]
    public void ImportBundle_DisabledToolWithoutItsPicture_ImportsUntaught()
    {
        var job = Taught(locateEnabled: false);
        _store1.Save(job, "EngineerA");
        var reference = BarcodeReading.BmpImage.EncodeGray(((PhaseToolConfig)job.Tools[^1]).Reference, 32, 16);
        SignedBundle(null, new() { [job.Tools.Count - 1] = reference });

        var imported = _store2.ImportBundle(_bundle, Pem, "EngineerB");
        Assert.Null(imported.Tools.OfType<LocateToolConfig>().Single().PatternFile);
        Assert.NotNull(_store2.Load("BlisterPouchLine"));
    }
}
