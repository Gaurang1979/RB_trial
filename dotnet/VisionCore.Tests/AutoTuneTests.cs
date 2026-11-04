using VisionCore;
using Xunit;

public class AutoTuneTests
{
    private static Job TwoTools() => new()
    {
        Name = "Label",
        Tools =
        {
            new LocateToolConfig { Name = "Part", MinScore = 0.6f },
            new TextToolConfig { Name = "Date", Mode = TextMode.Check, MinQuality = 60, Lines = { new TextLineSpec { Template = "EXP" } } },
            new CodeToolConfig { Name = "EAN" },
        },
    };

    // 60 ms: slow enough that the 100 ms time budgets are not lowered (60 * 1.5 + 10 = 100); budgets are tested apart
    private static ToolResult Tool(string name, bool pass, float? match = null, float? quality = null, double ms = 60) =>
        new(name, pass, "", Array.Empty<string>(), ms, Array.Empty<Graphic>()) { MatchScore = match, PrintQuality = quality };

    // one image of the relaxed run: the Find score, the Check text quality, and whether the code passed
    private static TuneSample S(string file, bool good, float match, float quality, bool code = true, double ms = 20)
    {
        var tools = new[] { Tool("Part", true, match: match), Tool("Date", true, quality: quality), Tool("EAN", code) };
        return new TuneSample(new TuneImage(file, good), new JobResult(tools.All(t => t.Pass), tools, ms, file));
    }

    [Fact]
    public void ThresholdGoesHalfWayBetweenBadAndGood()
    {
        var run = new[]
        {
            S("g1", true, 0.92f, 88), S("g2", true, 0.90f, 80), S("g3", true, 0.95f, 84),
            S("smudged", false, 0.91f, 40),       // only the text sees it
            S("wrong label", false, 0.50f, 85),   // only Find the part sees it
        };
        var p = AutoTune.Propose(TwoTools(), 0, run);
        Assert.Null(p.Problem);
        var part = p.Knobs.Single(k => k.Tool == "Part");
        var date = p.Knobs.Single(k => k.Tool == "Date");
        Assert.Equal(0.70f, part.Proposed, 3);   // (0.50 + 0.90) / 2
        Assert.Equal(60f, date.Proposed);        // (40 + 80) / 2
        Assert.Equal(0.20f, part.GoodMargin!.Value, 3);
        Assert.Equal(0.20f, part.BadMargin!.Value, 3);
        Assert.Equal(new TuneCount(3, 3, 2, 2, 20), p.Predicted);
    }

    [Fact]
    public void BadFailedByOtherChecksLeavesToolAsSet()
    {
        // the bad part has a wrong code: the code check fails it, so the text keeps its strictness
        var run = new[] { S("g1", true, 0.9f, 75), S("g2", true, 0.9f, 70), S("bad code", false, 0.9f, 72, code: false) };
        var p = AutoTune.Propose(TwoTools(), 0, run);
        Assert.Null(p.Problem);
        Assert.All(p.Knobs, k => Assert.False(k.Changed));
        Assert.All(p.Knobs, k => Assert.Null(k.HighestBad));
        Assert.True(p.Predicted.AllBadFail);
    }

    [Fact]
    public void SettingThatFailsAGoodPartIsLowered()
    {
        // strictness 60 fails a good part at 55; nothing bad depends on the text
        var run = new[] { S("g1", true, 0.9f, 55), S("g2", true, 0.9f, 80), S("bad", false, 0.3f, 80) };
        var p = AutoTune.Propose(TwoTools(), 0, run);
        var date = p.Knobs.Single(k => k.Tool == "Date");
        Assert.True(date.Looser);
        Assert.True(date.Proposed <= 55 && date.Proposed >= AutoTune.FloorMinQuality);
        Assert.Equal(new TuneCount(2, 2, 1, 1, 20), p.Predicted);
    }

    [Fact]
    public void NeverLessStrictThanNowWithoutNeed()
    {
        // half-way would be 45, below today's 60, and no good part needs it: 60 stays
        var run = new[] { S("g1", true, 0.9f, 80), S("bad", false, 0.9f, 10) };
        var date = AutoTune.Propose(TwoTools(), 0, run).Knobs.Single(k => k.Tool == "Date");
        Assert.Equal(10f, date.HighestBad);
        Assert.Equal(60f, date.Proposed);
        Assert.False(date.Looser);
    }

    [Fact]
    public void NeverBelowTheFloor()
    {
        // a good part matching 0.35 cannot be passed above the floor 0.40: the lowest match stays, the part is named
        var run = new[] { S("g1", true, 0.35f, 80), S("g2", true, 0.9f, 80), S("bad", false, 0.9f, 80, code: false) };
        var p = AutoTune.Propose(TwoTools(), 0, run);
        Assert.Equal(0.6f, p.Knobs.Single(k => k.Tool == "Part").Proposed);
        Assert.Equal(new[] { "g1" }, p.GoodAlwaysFail);
        Assert.Equal(new TuneCount(1, 2, 1, 1, 20), p.Predicted);
    }

    [Fact]
    public void LearnedPrintAndPlcTextAreNotTuned()
    {
        var job = TwoTools();
        ((TextToolConfig)job.Tools[1]).PrintReference = "learned";
        Assert.Equal(new[] { "Part" }, AutoTune.Tunable(job, 0).Select(t => t.Name));
        job = TwoTools();
        ((TextToolConfig)job.Tools[1]).Lines[0].Source = ExpectedSource.Plc;
        Assert.Equal(new[] { "Part" }, AutoTune.Tunable(job, 0).Select(t => t.Name));
    }

    [Fact]
    public void BadThatLooksGoodIsRefused()
    {
        var run = new[] { S("g1", true, 0.9f, 80), S("g2", true, 0.85f, 70), S("twin", false, 0.95f, 90) };
        var p = AutoTune.Propose(TwoTools(), 0, run);
        Assert.NotNull(p.Problem);
        Assert.Empty(p.Knobs);
        Assert.Equal(new[] { "twin" }, p.BadNotSeparable);
    }

    [Fact]
    public void BadGoesToTheToolWithMoreRoom()
    {
        // both tools could fail it; the text has more room (90 -> 40 against 0.90 -> 0.85)
        var run = new[] { S("g1", true, 0.90f, 90), S("bad", false, 0.85f, 40) };
        var p = AutoTune.Propose(TwoTools(), 0, run);
        Assert.Equal(65f, p.Knobs.Single(k => k.Tool == "Date").Proposed);
        Assert.Null(p.Knobs.Single(k => k.Tool == "Part").HighestBad);
    }

    [Fact]
    public void GoodThatFailsOtherChecksIsListedNotTunedFor()
    {
        var run = new[] { S("g1", true, 0.9f, 80), S("g bad code", true, 0.5f, 20, code: false), S("bad", false, 0.9f, 40) };
        var p = AutoTune.Propose(TwoTools(), 0, run);
        Assert.Equal(new[] { "g bad code" }, p.GoodAlwaysFail);
        Assert.Equal(60f, p.Knobs.Single(k => k.Tool == "Date").Proposed);
        Assert.Equal(new TuneCount(1, 2, 1, 1, 20), p.Predicted);
    }

    [Fact]
    public void NeedsGoodAndBadImagesAndATunedTool()
    {
        Assert.NotNull(AutoTune.Propose(TwoTools(), 0, new[] { S("g", true, 0.9f, 80) }).Problem);
        Assert.NotNull(AutoTune.Propose(TwoTools(), 0, new[] { S("b", false, 0.9f, 80) }).Problem);
        var codeOnly = new Job { Name = "C", Tools = { new CodeToolConfig { Name = "EAN" } } };
        Assert.NotNull(AutoTune.Propose(codeOnly, 0, new[] { S("g", true, 0.9f, 80), S("b", false, 0.9f, 80) }).Problem);
    }

    [Fact]
    public void RelaxedAndApplyLeaveTheJobAlone()
    {
        var job = TwoTools();
        var relaxed = AutoTune.Relaxed(job, 0);
        Assert.Equal(AutoTune.RelaxedMinScore, relaxed.Tools.OfType<LocateToolConfig>().Single().MinScore);
        Assert.Equal(AutoTune.RelaxedMinQuality, relaxed.Tools.OfType<TextToolConfig>().Single().MinQuality);
        Assert.Equal(0.6f, job.Tools.OfType<LocateToolConfig>().Single().MinScore);

        var p = new TuneProposal(0, new[]
        {
            new TuneKnob("Part", TuneKnobKind.LocateMinScore, 0.6f, 0.7f, 0.9f, 0.5f),
            new TuneKnob("Date", TuneKnobKind.TextMinQuality, 60, 45, 80, 10),
        }, new TuneCount(1, 1, 1, 1, 1), null);
        var tuned = AutoTune.Apply(job, p);
        Assert.Equal(0.7f, tuned.Tools.OfType<LocateToolConfig>().Single().MinScore);
        Assert.Equal(45f, tuned.Tools.OfType<TextToolConfig>().Single().MinQuality);
        Assert.Equal(60f, job.Tools.OfType<TextToolConfig>().Single().MinQuality);
        tuned.Validate();
    }

    [Fact]
    public void OtherCameraIsNotTuned()
    {
        var job = TwoTools();
        job.Cameras.Add(new CameraConfig { Name = "Side" });
        job.Tools.Add(new TextToolConfig { Name = "Side text", Camera = 1, Mode = TextMode.Check, MinQuality = 70 });
        Assert.Equal(70f, AutoTune.Relaxed(job, 0).Tools.OfType<TextToolConfig>().Single(t => t.Camera == 1).MinQuality);
        Assert.Equal(new[] { "Part", "Date" }, AutoTune.Tunable(job, 0).Select(t => t.Name));
    }

    // the real engine: Check text with a rendered font, good prints, a wrong character and a smudged one; the
    // tune never lets either bad print pass, also while the strictness is relaxed for measuring
    private static readonly string? Family = new[] { "Arial", "Helvetica", "DejaVu Sans", "Liberation Sans" }
        .FirstOrDefault(f => FontRender.Families().Contains(f, StringComparer.OrdinalIgnoreCase));

    private static bool EngineBuilt => File.Exists(Path.Combine(AppContext.BaseDirectory, "libvs_ocr.dylib"))
                                       || File.Exists(Path.Combine(AppContext.BaseDirectory, "vs_ocr.dll"));

    private static string Print(string folder, string name, string text, byte ink = 20, Func<int, int, bool>? erase = null)
    {
        var line = FontRender.Draw(Family!, false, 30, text, gapEm: 0);
        int w = line.Width + 80, h = line.Height + 60;
        var px = Enumerable.Repeat((byte)230, w * h).ToArray();
        for (int y = 0; y < line.Height; y++)
            for (int x = 0; x < line.Width; x++)
                if (erase?.Invoke(x, y) != true)
                    px[(y + 30) * w + x + 40] = (byte)Math.Max(ink, line.Pixels[y * line.Width + x] * 230 / 255);
        var file = Path.Combine(folder, name + ".bmp");
        BarcodeReading.BmpImage.SaveGray(file, px, w, h);
        return file;
    }

    [Fact]
    public void TuneOnTheEngineNeverPassesABadPrint()
    {
        if (Family is null || !EngineBuilt) return;
        var dir = Directory.CreateTempSubdirectory("autotune").FullName;
        try
        {
            var cfg = new TextToolConfig
            {
                Name = "Lot", Mode = TextMode.Check, Reader = OcrReader.Font, Print = PrintType.Solid, MinQuality = 60,
                Rendered = new RenderedFont { Family = Family, SizePx = 30, Characters = RenderedFont.DefaultCharacters },
                Lines = { new TextLineSpec { Entry = LineEntry.Template, Template = "LOT {LOT}" } },
            };
            var job = new Job { Name = "Case", Cameras = { new CameraConfig { Name = "Top" } }, Tools = { cfg } };
            job.Validate();
            var batch = new BatchFields { Values = { ["LOT"] = "A123" } };
            int cut = FontRender.Draw(Family, false, 30, "LOT A12", gapEm: 0).Width - 30, half = FontRender.Draw(Family, false, 30, "LOT A123", gapEm: 0).Height / 2;
            var images = new List<TuneImage>
            {
                new(Print(dir, "g1", "LOT A123"), true),
                new(Print(dir, "g2", "LOT A123", ink: 50), true),
                new(Print(dir, "g3", "LOT A123", ink: 35), true),
                new(Print(dir, "wrong", "LOT A128"), false),
                // the top of the last character gone: a smudge, a blocked nozzle
                new(Print(dir, "smudge", "LOT A123", erase: (x, y) => x > cut && y < half), false),
            };
            var models = new ModelPaths(dir);
            var before = AutoTune.Run(job, 0, images, models, batch);
            var relaxed = AutoTune.Run(AutoTune.Relaxed(job, 0), 0, images, models, batch);
            Assert.All(relaxed.Where(s => s.Name == "wrong.bmp"), s => Assert.False(s.Result.Pass));
            var p = AutoTune.Propose(job, 0, relaxed);
            Assert.Null(p.Problem);
            var after = AutoTune.Run(AutoTune.Apply(job, p), 0, images, models, batch);
            Assert.All(after.Where(s => !s.Image.Good), s => Assert.False(s.Result.Pass, s.Name));
            Assert.All(after.Where(s => s.Image.Good), s => Assert.True(s.Result.Pass, s.Name + ": " + s.Result.FirstProblem));
            Assert.Equal(p.Predicted with { MaxMs = 0 }, TuneCount.Of(after) with { MaxMs = 0 });
            var v = AutoTune.Verify(p, before, after);
            Assert.Empty(v.Lost);
            Assert.Contains("all 2 bad parts fail", v.Words.Split("With the new settings")[1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void VerifyRefusesABadPassOrALostGood()
    {
        var p = new TuneProposal(0, new[] { new TuneKnob("Date", TuneKnobKind.TextMinQuality, 60, 70, 80, 30) }, new TuneCount(2, 2, 1, 1, 20), null);
        var before = new[] { S("g1", true, 0.9f, 80), S("g2", true, 0.9f, 80), S("b", false, 0.9f, 30, code: false) };

        var fine = AutoTune.Verify(p, before, before);
        Assert.True(fine.CanApply);
        Assert.Contains("all 2 good parts pass, all 1 bad parts fail", fine.Words);

        var badPasses = AutoTune.Verify(p, before, new[] { before[0], before[1], S("b", false, 0.9f, 80) });
        Assert.False(badPasses.CanApply);
        Assert.Contains("b would pass", badPasses.Words);

        var lost = AutoTune.Verify(p, before, new[] { before[0], S("g2", true, 0.9f, 80, code: false), before[2] });
        Assert.False(lost.CanApply);
        Assert.Equal(new[] { "g2" }, lost.Lost);

        // less strict without passing more good parts; a bad part failed only by a tool error; slower
        var looser = p with { Knobs = new[] { new TuneKnob("Date", TuneKnobKind.TextMinQuality, 60, 50, 80, 30) } };
        Assert.False(AutoTune.Verify(looser, before, before).CanApply);
        var err = new TuneSample(new TuneImage("e", false), new JobResult(false,
            new[] { new ToolResult("EAN", false, "", Array.Empty<string>(), 1, Array.Empty<Graphic>()) { Explain = "explain.tool.error" } }, 20, "e"));
        Assert.False(AutoTune.Verify(p, before.Append(err).ToList(), before.Append(err).ToList()).CanApply);
        Assert.False(AutoTune.Verify(p, before, new[] { before[0], before[1], S("b", false, 0.9f, 30, code: false, ms: 60) }).CanApply);

        var same = new TuneProposal(0, new[] { new TuneKnob("Date", TuneKnobKind.TextMinQuality, 60, 60, 80, null) }, p.Predicted, null);
        Assert.False(AutoTune.Verify(same, before, before).CanApply);
    }

    [Fact]
    public void A_good_part_close_to_a_tuned_limit_is_near_the_limit_and_a_bad_or_easy_one_is_not()
    {
        var job = TwoTools();
        JobResult R(float match, float quality, bool pass = true)
        {
            var tools = new[] { Tool("Part", pass, match: match), Tool("Date", true, quality: quality), Tool("EAN", true) };
            return new JobResult(tools.All(t => t.Pass), tools, 10, null);
        }
        Assert.False(AutoTune.NearLimit(job, 0, R(0.95f, 95)));
        Assert.True(AutoTune.NearLimit(job, 0, R(0.65f, 95)));    // match 0.65 over a lowest match of 0.60
        Assert.True(AutoTune.NearLimit(job, 0, R(0.95f, 64)));    // print quality 64 over a strictness of 60
        Assert.False(AutoTune.NearLimit(job, 0, R(0.65f, 64, pass: false)));   // a part that failed is not a good picture
        Assert.False(AutoTune.NearLimit(job, 1, R(0.65f, 64)));   // no tuned tool on that camera
    }

    [Fact]
    public void The_two_percentages_count_good_parts_passing_and_bad_parts_failing()
    {
        var c = new TuneCount(39, 40, 12, 12, 30);
        Assert.Equal(97.5, c.GoodPassPct);
        Assert.Equal(100, c.BadFailPct);
        Assert.Equal(-1, new TuneCount(0, 0, 0, 0, 0).GoodPassPct);
    }

    // Read code's lowest print grade (AS-03a) and the time budgets (AS-03b)

    private static Job Graded(int minGrade = 2, int every = 1) => new()
    {
        Name = "Case",
        Tools =
        {
            new LocateToolConfig { Name = "Part", MinScore = 0.6f },
            new CodeToolConfig { Name = "EAN", MinGrade = minGrade, GradeEvery = every, TimeBudgetMs = 100 },
        },
    };

    // one image of the relaxed run of Graded(): the worst code grade (null = a code not graded), the code tool's
    // time, whether it passed and why not
    private static TuneSample G(string file, bool good, int? grade, double ms = 60, bool code = true, string? explain = null, float match = 0.9f)
    {
        var values = new Dictionary<string, double>();
        if (grade is { } g) values["grade"] = g; else values["grade.ungraded"] = 1;
        var tools = new[]
        {
            Tool("Part", true, match: match),
            new ToolResult("EAN", code, "", Array.Empty<string>(), ms, Array.Empty<Graphic>()) { Values = values, Explain = explain },
        };
        return new TuneSample(new TuneImage(file, good), new JobResult(tools.All(t => t.Pass), tools, ms + 5, file));
    }

    [Fact]
    public void Lowest_print_grade_goes_half_way_up_to_fail_a_badly_printed_part()
    {
        // good parts graded A and B, a bad one C that only the grade sees: B fails it and passes both good ones
        var run = new[] { G("g1", true, 4), G("g2", true, 3), G("faint", false, 2) };
        var p = AutoTune.Propose(Graded(minGrade: 2), 0, run);
        Assert.Null(p.Problem);
        var grade = p.Knobs.Single(k => k.Kind == TuneKnobKind.CodeMinGrade);
        Assert.Equal(3f, grade.Proposed);
        Assert.Equal("B", grade.Show(grade.Proposed));
        Assert.Equal("lowest print grade", grade.Setting);
        Assert.Equal(new TuneCount(2, 2, 1, 1, 65), p.Predicted);
        Assert.Equal(3, AutoTune.Apply(Graded(minGrade: 2), p).Tools.OfType<CodeToolConfig>().Single().MinGrade);
    }

    [Fact]
    public void A_bad_part_graded_as_well_as_the_good_ones_is_never_passed_by_the_grade()
    {
        var run = new[] { G("g1", true, 3), G("g2", true, 3), G("twin", false, 3) };
        var p = AutoTune.Propose(Graded(), 0, run);
        Assert.NotNull(p.Problem);
        Assert.Empty(p.Knobs);
        Assert.Equal(new[] { "twin" }, p.BadNotSeparable);
    }

    [Fact]
    public void A_good_code_that_cannot_be_graded_fails_at_every_grade()
    {
        var run = new[] { G("g1", true, 4), G("ungraded", true, null), G("bad", false, 1) };
        var p = AutoTune.Propose(Graded(minGrade: 1), 0, run);
        Assert.Null(p.Problem);
        Assert.Equal(new[] { "ungraded" }, p.GoodAlwaysFail);
        Assert.Equal(3f, p.Knobs.Single(k => k.Kind == TuneKnobKind.CodeMinGrade).Proposed);   // ceil((1 + 4) / 2)
    }

    [Fact]
    public void Lowest_print_grade_that_fails_a_good_part_is_lowered_never_below_D()
    {
        // B fails a good part graded C; the bad part is a wrong label (Find the part fails it)
        var run = new[] { G("g1", true, 2), G("g2", true, 4), G("wrong label", false, 4, match: 0.3f) };
        var p = AutoTune.Propose(Graded(minGrade: 3), 0, run);
        var grade = p.Knobs.Single(k => k.Kind == TuneKnobKind.CodeMinGrade);
        Assert.True(grade.Looser);
        Assert.InRange(grade.Proposed, AutoTune.FloorMinGrade, 2f);
        Assert.Equal(new TuneCount(2, 2, 1, 1, 65), p.Predicted);
    }

    [Fact]
    public void Grade_is_tuned_only_where_every_part_is_graded_against_a_lowest_grade()
    {
        Assert.Contains("EAN", AutoTune.Tunable(Graded(), 0).Select(t => t.Name));
        Assert.DoesNotContain("EAN", AutoTune.Tunable(Graded(minGrade: -1), 0).Select(t => t.Name));
        Assert.DoesNotContain("EAN", AutoTune.Tunable(Graded(every: 10), 0).Select(t => t.Name));

        var job = Graded(minGrade: 3);
        var code = AutoTune.Relaxed(job, 0).Tools.OfType<CodeToolConfig>().Single();
        Assert.Equal(-1, code.MinGrade);   // graded and recorded, never failed on the grade
        Assert.True(code.RecordGrades);
        Assert.Equal(3, job.Tools.OfType<CodeToolConfig>().Single().MinGrade);
    }

    [Fact]
    public void A_code_graded_at_its_lowest_grade_is_near_the_limit()
    {
        var job = Graded(minGrade: 3);
        Assert.True(AutoTune.NearLimit(job, 0, G("b", true, 3).Result));
        Assert.False(AutoTune.NearLimit(job, 0, G("a", true, 4).Result));
    }

    [Fact]
    public void Time_budget_is_the_slowest_good_part_with_a_margin()
    {
        // the slowest good read takes 30 ms: 30 * 1.5 + 10 = 55 ms. The bad part with no code ran to the budget,
        // but reading less still finds no code
        var run = new[]
        {
            G("g1", true, 4, ms: 20), G("g2", true, 4, ms: 30),
            G("no code", false, null, ms: 100, code: false, explain: "explain.code.none"),
        };
        var p = AutoTune.Propose(Graded(minGrade: -1), 0, run);
        Assert.Null(p.Problem);
        var budget = p.Knobs.Single(k => k.Kind == TuneKnobKind.TimeBudget);
        Assert.Equal(100f, budget.Current);
        Assert.Equal(55f, budget.Proposed);
        Assert.Equal(30f, budget.SlowestMs);
        Assert.False(budget.Looser);
        Assert.Equal("55 ms", budget.Show(budget.Proposed));
        var tuned = AutoTune.Apply(Graded(minGrade: -1), p);
        Assert.Equal(55, tuned.Tools.OfType<CodeToolConfig>().Single().TimeBudgetMs);
        tuned.Validate();
    }

    [Fact]
    public void Time_budget_covers_a_bad_part_that_reading_less_could_pass()
    {
        // an extra pack's code read at 50 ms: a budget that stopped before it would read the expected count only
        var run = new[]
        {
            G("g1", true, 4, ms: 20),
            G("extra pack", false, 4, ms: 50, code: false, explain: "explain.code.extra"),
        };
        var budget = AutoTune.BudgetFor(Graded(minGrade: -1).Tools[1], run);
        Assert.NotNull(budget);
        Assert.Equal(85f, budget!.Proposed);   // 50 * 1.5 + 10, not 20 * 1.5 + 10
        // slower still: the budget stays as it is
        run[1] = G("extra pack", false, 4, ms: 80, code: false, explain: "explain.code.extra");
        Assert.Null(AutoTune.BudgetFor(Graded(minGrade: -1).Tools[1], run));
    }

    [Fact]
    public void Time_budget_is_left_without_a_good_part_the_tool_passes_or_a_whole_step_to_save()
    {
        var tool = Graded(minGrade: -1).Tools[1];
        Assert.Null(AutoTune.BudgetFor(tool, new[] { G("g", true, 4, ms: 5, code: false), G("b", false, 4, ms: 5, code: false, explain: "explain.code.none") }));
        Assert.Null(AutoTune.BudgetFor(tool, new[] { G("g", true, 4, ms: 58) }));   // 97 rounds up to 100, no step below now
        // a bad part the tool passes in the relaxed run is covered: its fault may be a grade the run does not judge
        Assert.Equal(70f, AutoTune.BudgetFor(tool, new[] { G("g", true, 4, ms: 20), G("b", false, 1, ms: 40) })!.Proposed);
        Assert.Null(AutoTune.BudgetFor(tool, new[] { G("g", true, 4, ms: 20), G("b", false, 4, ms: 99, match: 0.3f) }));
    }

    [Fact]
    public void Grade_and_budget_together_never_pass_a_bad_part_by_the_scores()
    {
        var run = new[]
        {
            G("g1", true, 4, ms: 20), G("g2", true, 3, ms: 25),
            G("faint", false, 2, ms: 22), G("no code", false, null, ms: 100, code: false, explain: "explain.code.none"),
        };
        var p = AutoTune.Propose(Graded(minGrade: 1), 0, run);
        Assert.Null(p.Problem);
        Assert.True(p.Predicted.AllBadFail);
        Assert.Equal(new[] { TuneKnobKind.LocateMinScore, TuneKnobKind.CodeMinGrade, TuneKnobKind.TimeBudget }, p.Knobs.Select(k => k.Kind));
        Assert.Equal(3f, p.Knobs[1].Proposed);
        Assert.Equal(50f, p.Knobs[2].Proposed);   // the slowest is 25 (g2), not 22 (faint): 25 * 1.5 + 10 = 47.5, up to 50
    }
}
