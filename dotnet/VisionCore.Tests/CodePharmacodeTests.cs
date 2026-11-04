using System.IO;
using System.Reflection;
using System.Text.Json;
using BarcodeReading;
using VisionCore;
using Xunit;

namespace VisionCore.Tests;

public class CodePharmacodeTests
{
    [Fact]
    public void TeachingLocksThePharmacodeBarCount()
    {
        var code = new CodeToolConfig { Format = CodeFormat.Ean13 };
        CodeTeach.Lock(code, new[] { (CodeFormat.Pharmacode, "12345"), (CodeFormat.Pharmacode, "12345") });
        Assert.Equal(CodeFormat.Pharmacode, code.Format);
        Assert.Equal(2, code.ExpectedCount);
        // 12345: 13 bars, as the engine counts them for the lock
        Assert.Equal((13, 13), (code.MinLength, code.MaxLength));
        Assert.Equal("12345", code.Expected);
        Assert.Null(CodeTool.SetupProblem(code));
        // two different values: no expected value is guessed, the tool asks for one
        var mixed = new CodeToolConfig { Format = CodeFormat.Ean13 };
        CodeTeach.Lock(mixed, new[] { (CodeFormat.Pharmacode, "12345"), (CodeFormat.Pharmacode, "12346") });
        Assert.Equal("", mixed.Expected);
        Assert.Contains("value it must read", CodeTool.SetupProblem(mixed));
    }

    [Fact]
    public void PharmacodeWithoutABarCountIsASetupError()
    {
        // no check digit: without the bar count a partly covered Pharmacode reads as another value
        Assert.Contains("number of bars", CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Pharmacode }));
        Assert.Contains("number of bars", CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.PharmacodeReverse }));
        Assert.Contains("number of bars", CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Pharmacode, MaxLength = 16, Expected = "12345" }));
        Assert.Contains("value it must read", CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Pharmacode, MinLength = 13, MaxLength = 13 }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Pharmacode, MinLength = 13, MaxLength = 13, Expected = "12345" }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.DotCode }));
    }

    [Fact]
    public void APharmacodeValueTheToolCanNeverReadIsASetupError()
    {
        // the expected value is compared with the decimal the engine reports: outside 3-131070, with a leading
        // zero or spaces, or with more or fewer bars than the lock lets through, no part can pass
        static CodeToolConfig Tool(string expected, int bars) =>
            new() { Name = "Pharma", Format = CodeFormat.Pharmacode, MinLength = bars, MaxLength = bars, Expected = expected };
        foreach (var bad in new[] { "2", "131071", "0", "ABC", "012345", "12345 ", "+12345", "1.5" })
            Assert.Contains("can never be read", CodeTool.SetupProblem(Tool(bad, 13)));
        Assert.Contains("12345 has 13 bars, but the tool reads 12-12 bars", CodeTool.SetupProblem(Tool("12345", 12)));
        Assert.Null(CodeTool.SetupProblem(Tool("12345", 13)));
        Assert.Null(CodeTool.SetupProblem(Tool("3", 2)));
        Assert.Null(CodeTool.SetupProblem(Tool("131070", 16)));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig
            { Format = CodeFormat.PharmacodeReverse, MinLength = 12, MaxLength = 14, Expected = "12345" }));
        // a batch field is filled at batch start and checked when read
        Assert.Null(CodeTool.SetupProblem(Tool("{PHARMA}", 13)));
        // refused when the job is saved
        var job = new Job { Name = "Case", Cameras = { new CameraConfig { Name = "Top" } }, Tools = { Tool("12345", 12) } };
        Assert.StartsWith("Pharma: Pharmacode 12345 has 13 bars", Assert.Throws<ArgumentException>(() => job.Validate()).Message);
    }

    [Fact]
    public void PharmacodeAndDotCodeTakeALowestGrade()
    {
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.DotCode, MinGrade = 2, RecordGrades = true }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Pharmacode, MinLength = 5, MaxLength = 5, Expected = "31", MinGrade = 2 }));
        // part mark grading stays Data Matrix and QR only
        Assert.NotNull(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.DotCode, MinGrade = 2, GradeStandard = GradeStandard.Iso29158 }));
    }

    [Fact]
    public void ReadingDirectionIsKeptInTheJobFileAndReachesTheReader()
    {
        // a job file from before the setting reads across, as it did
        var old = JsonSerializer.Deserialize<CodeToolConfig>("{\"Name\":\"Pharma\",\"Format\":\"Pharmacode\"}", JobStore.Json)!;
        Assert.Equal(PharmacodeDirection.Across, old.PharmacodeDirection);
        var cfg = new CodeToolConfig
            { Name = "Pharma", Format = CodeFormat.Pharmacode, MinLength = 13, MaxLength = 13, Expected = "12345", PharmacodeDirection = PharmacodeDirection.Down };
        var json = JsonSerializer.Serialize(cfg, JobStore.Json);
        Assert.Contains("\"PharmacodeDirection\": \"Down\"", json);
        Assert.Equal(PharmacodeDirection.Down, JsonSerializer.Deserialize<CodeToolConfig>(json, JobStore.Json)!.PharmacodeDirection);
        using var tool = new CodeTool(cfg, new ModelPaths(Path.GetTempPath()));
        var settings = (ReaderSettings)typeof(CodeTool).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tool)!;
        Assert.Equal(PharmacodeDirection.Down, settings.PharmacodeDirection);
    }
}
