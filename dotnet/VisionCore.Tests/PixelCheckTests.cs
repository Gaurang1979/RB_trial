using System.Text.Json;
using BarcodeReading;
using VisionCore;
using Xunit;

namespace VisionCore.Tests;

// The pixel check per tool (requirements AS-14): what each tool needs, the factor missing, the three remedies
// and "Accept as is". Advice only: nothing here touches a tool's result.
public class PixelCheckTests
{
    static readonly VisionCore.PointF[] Box = [new(10, 10), new(60, 10), new(60, 60), new(10, 60)];

    static CodeRead Code(CodeFormat f, float modulePx) =>
        new(f, "ABC", Box, new CodeQuality(80, 0.7f, modulePx, 1, 0, 4));

    static ToolResult Read(params CodeRead[] codes) =>
        new("QR", true, "Read", [], 1, []) { Codes = codes };

    static OcrLine Line(float heightPx) => new("B.NO A1", "B.NO A1", true, true, 90, [], Box, 0, 0, heightPx);

    [Theory]
    [InlineData(2.5f, 2.1f, 1.2f)]    // 1.19 rounded up: the remedy is never short
    [InlineData(2.5f, 1.25f, 2.0f)]
    [InlineData(2.5f, 2.5f, 1.0f)]    // enough
    [InlineData(2.5f, 3.4f, 1.0f)]
    [InlineData(20f, 14f, 1.5f)]      // 1.43
    public void The_factor_is_need_over_found_rounded_up_to_a_tenth(float need, float found, float factor) =>
        Assert.Equal(factor, PixelCheck.Factor(need, found));

    [Fact]
    public void Each_tool_states_its_need_by_what_it_reads()
    {
        Assert.Equal(PixelMeasure.Module, PixelCheck.MeasureOf(CodeFormat.QrCode));
        Assert.Equal(PixelMeasure.Module, PixelCheck.MeasureOf(CodeFormat.DataMatrix));
        Assert.Equal(PixelMeasure.NarrowBar, PixelCheck.MeasureOf(CodeFormat.Ean13));
        Assert.Equal(PixelMeasure.NarrowBar, PixelCheck.MeasureOf(CodeFormat.Gs1_128));
        Assert.Equal(PixelMeasure.NarrowBar, PixelCheck.MeasureOf(CodeFormat.Pharmacode));
        Assert.Equal(PixelMeasure.NarrowBar, PixelCheck.MeasureOf(CodeFormat.Pdf417));
        Assert.Equal((2.5f, 1.8f), PixelCheck.NeedOf(PixelMeasure.Module));
        Assert.Equal((2.0f, 1.5f), PixelCheck.NeedOf(PixelMeasure.NarrowBar));
        Assert.Equal((20f, 12f), PixelCheck.NeedOf(PixelMeasure.LetterHeight));
    }

    [Fact]
    public void A_small_code_gets_the_factor_and_the_three_remedies_with_the_lens_in_mm()
    {
        var p = PixelCheck.Check(new CodeToolConfig { Name = "QR" }, Read(Code(CodeFormat.QrCode, 3.1f), Code(CodeFormat.QrCode, 2.1f)),
            new CameraConfig { LensFocalMm = 25 })!;
        Assert.Equal(2.1f, p.FoundPx);   // the worst code is the tool's
        Assert.Equal(2, p.Items.Count);
        Assert.Equal([false, true], p.Items.Select(i => i.Below));
        Assert.False(p.Enough);
        Assert.False(p.Unsure);
        Assert.True(p.TooFew);
        Assert.Equal(1.2f, p.Factor);
        Assert.Equal(1.5f, p.PixelsTimes);   // 1.44
        Assert.Equal(30f, p.LensAdvisedMm);
        Assert.Equal("Needs 1.2× more pixels: camera 1.2× nearer, a 30 mm lens instead of 25 mm, or a camera with 1.5× the pixels", p.Advice());
        Assert.Equal("2.1 px/module", PixelReport.SizeText(p.Measure, p.FoundPx));
    }

    [Fact]
    public void Without_the_lens_the_advice_gives_a_factor_of_the_focal_length_and_says_when_the_read_is_unsure()
    {
        var p = PixelCheck.Check(new CodeToolConfig(), Read(Code(CodeFormat.Ean13, 1.2f)))!;
        Assert.Equal(PixelMeasure.NarrowBar, p.Measure);
        Assert.True(p.Unsure);
        Assert.Null(p.LensAdvisedMm);
        Assert.Equal("Needs 1.7× more pixels: camera 1.7× nearer, a lens with 1.7× the focal length, or a camera with 2.9× the pixels. "
                     + "At this size the read is unsure", p.Advice());
    }

    [Fact]
    public void Text_is_checked_on_its_capital_height_and_enough_gives_no_advice()
    {
        var text = new TextToolConfig { Name = "Batch" };
        var small = PixelCheck.Check(text, new ToolResult("Batch", true, "", [], 1, []) { Lines = [Line(14), Line(22)] })!;
        Assert.Equal((PixelMeasure.LetterHeight, 14f, 1.5f), (small.Measure, small.FoundPx, small.Factor));
        Assert.Equal("14 px high", PixelReport.SizeText(small.Measure, small.FoundPx));
        var big = PixelCheck.Check(text, new ToolResult("Batch", true, "", [], 1, []) { Lines = [Line(26)] })!;
        Assert.True(big.Enough);
        Assert.False(big.TooFew);
        Assert.Equal(1f, big.Factor);
        Assert.Equal("", big.Advice());
    }

    [Fact]
    public void Nothing_measured_or_another_tool_gives_no_check()
    {
        // a code tool measures only with the quality measure on (job setup): no size, no check
        Assert.Null(PixelCheck.Check(new CodeToolConfig(), Read(new CodeRead(CodeFormat.QrCode, "A", Box, null))));
        Assert.Null(PixelCheck.Check(new CodeToolConfig(), Read()));
        Assert.Null(PixelCheck.Check(new TextToolConfig(), new ToolResult("T", false, "", [], 1, []) { Lines = [Line(0)] }));
        Assert.Null(PixelCheck.Check(new BlobToolConfig(), Read(Code(CodeFormat.QrCode, 1f))));
    }

    [Fact]
    public void In_setup_a_code_tool_that_does_not_grade_measures_its_codes_and_judges_them_the_same()
    {
        var cfg = new CodeToolConfig { Name = "Lot", Format = CodeFormat.Code39, Expected = "AB12", TimeBudgetMs = 10_000 };
        using var tool = new CodeTool(cfg, new ModelPaths(Path.GetTempPath()));
        var img = LineGradingTests.Code39();   // narrow bars and spaces 4 px
        var line = tool.Run(img, new BatchFields());
        Assert.True(line.Pass, line.Summary);
        Assert.Null(line.Codes![0].Quality);   // the line never measures
        Assert.Null(PixelCheck.Check(cfg, line));

        tool.MeasureSize = true;
        var setup = tool.Run(img, new BatchFields());
        Assert.Equal((line.Pass, line.Summary), (setup.Pass, setup.Summary));
        Assert.Empty(setup.Values);   // not graded: no grades kept
        var p = PixelCheck.Check(cfg, setup)!;
        Assert.Equal(PixelMeasure.NarrowBar, p.Measure);
        Assert.InRange(p.FoundPx, 3.5f, 4.5f);
        Assert.True(p.Enough);
    }

    [Fact]
    public void Accept_as_is_holds_for_its_size_and_asks_again_for_a_clearly_smaller_one()
    {
        var tool = new CodeToolConfig { PixelsAccepted = new PixelAcceptance("Asha", DateTimeOffset.Now, 2.1f) };
        var same = PixelCheck.Check(tool, Read(Code(CodeFormat.QrCode, 2.05f)))!;
        Assert.NotNull(same.Accepted);
        Assert.False(same.TooFew);
        Assert.Equal(1.3f, same.Factor);   // the advice stays; only the tree's mark goes
        var smaller = PixelCheck.Check(tool, Read(Code(CodeFormat.QrCode, 1.9f)))!;
        Assert.Null(smaller.Accepted);
        Assert.True(smaller.TooFew);
    }

    [Fact]
    public void Saving_records_a_new_acceptance_as_the_users_now_and_keeps_an_old_one_as_it_was()
    {
        var then = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero);
        Job Make(PixelAcceptance? a) => new() { Name = "J", Tools = [new CodeToolConfig { Name = "QR", PixelsAccepted = a }, new TextToolConfig { Name = "T" }] };

        var fresh = Make(new PixelAcceptance("typed by the client", then, 2.1f));
        PixelCheck.Stamp(fresh, null, "eng", now);
        Assert.Equal(new PixelAcceptance("eng", now, 2.1f), fresh.Tools[0].PixelsAccepted);
        Assert.Null(fresh.Tools[1].PixelsAccepted);

        var saved = Make(new PixelAcceptance("ravi", then, 2.1f));
        var unchanged = Make(new PixelAcceptance("someone else", now, 2.1f));
        PixelCheck.Stamp(unchanged, saved, "eng", now);
        Assert.Equal(new PixelAcceptance("ravi", then, 2.1f), unchanged.Tools[0].PixelsAccepted);   // the client cannot rewrite who

        var other = Make(new PixelAcceptance("ravi", then, 1.9f));   // accepted again for another size
        PixelCheck.Stamp(other, saved, "eng", now);
        Assert.Equal(new PixelAcceptance("eng", now, 1.9f), other.Tools[0].PixelsAccepted);
    }

    [Fact]
    public void The_acceptance_and_the_lens_are_kept_in_the_job_file()
    {
        var job = new Job
        {
            Name = "J",
            Cameras = [new CameraConfig { Name = "C", LensFocalMm = 16 }],
            Tools = [new CodeToolConfig { Name = "QR", PixelsAccepted = new PixelAcceptance("eng", new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), 2.2f) }],
        };
        var json = JsonSerializer.Serialize(job, JobStore.Json);
        var back = JsonSerializer.Deserialize<Job>(json, JobStore.Json)!;
        Assert.Equal(16f, back.Cameras[0].LensFocalMm);
        Assert.Equal(job.Tools[0].PixelsAccepted, back.Tools[0].PixelsAccepted);
        // not set: not written, so older job files read the same
        Assert.DoesNotContain("PixelsAccepted", JsonSerializer.Serialize(new Job { Name = "K", Tools = [new CodeToolConfig { Name = "Q" }] }, JobStore.Json));
    }
}
