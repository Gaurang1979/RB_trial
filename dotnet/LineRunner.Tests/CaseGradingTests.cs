using BarcodeReading;
using Xunit;

// Grade sampling on aggregation lines (requirements 17.3 PQ-07, parent label PQ-08).
public class CaseGradingTests
{
    private static CodeQuality Q(int overall, params (string, int)[] grades) => new(70, 0.6f, 3f, 0.9f, 0f, overall) { Grades = grades };

    private static bool[] Samples(CaseGrading g, int n) => Enumerable.Range(0, n).Select(_ => g.NextCase()).ToArray();

    [Fact]
    public void OneCaseInNIsGraded()
    {
        Assert.Equal(new[] { true, false, false, true, false, false, true }, Samples(new CaseGrading(3, -1), 7));
        Assert.All(Samples(new CaseGrading(1, -1), 5), Assert.True);
    }

    [Fact]
    public void OffByDefaultAndALowestGradeAloneGradesEveryCase()
    {
        var off = new CaseGrading(0, -1);
        Assert.False(off.Enabled);
        Assert.All(Samples(off, 5), Assert.False);
        var min = new CaseGrading(0, 3);
        Assert.Equal(1, min.Every);
        Assert.All(Samples(min, 5), Assert.True);
    }

    [Fact]
    public void BadSettingsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new CaseGrading(-1, -1));
        Assert.Throws<ArgumentException>(() => new CaseGrading(10001, -1));
        Assert.Throws<ArgumentException>(() => new CaseGrading(1, 0));
        Assert.Throws<ArgumentException>(() => CaseGrading.ParseGrade("E"));
        Assert.Equal(new[] { 4, 3, 2, 1 }, new[] { "A", "b", "C", " d " }.Select(CaseGrading.ParseGrade));
    }

    [Fact]
    public void GradesGoToThePlantUnderTheChildTool()
    {
        var (v, why) = new CaseGrading(10, -1).Judge(new CodeQuality?[]
        {
            Q(3, ("Symbol contrast", 4), ("Modulation", 3)) with { SymbolContrast = 80, Modulation = 0.7f },
            Q(2, ("Symbol contrast", 3), ("Modulation", 2)) with { SymbolContrast = 65, Modulation = 0.5f },
        });
        Assert.Equal(2, v["child.grade"]);
        Assert.Equal(3, v["child.grade.symbol_contrast"]);
        Assert.Equal(2, v["child.grade.modulation"]);
        Assert.Equal(65, v["child.symbol_contrast_pct"]);
        Assert.Equal(0.5, v["child.modulation"], 3);
        Assert.False(v.ContainsKey("child.grade_min"));
        Assert.All(v.Keys, k => Assert.StartsWith("child.", k));
        Assert.Empty(why); // no lowest grade: recorded only, never NG
    }

    [Fact]
    public void BelowTheLowestGradeOrNotGradableIsNg()
    {
        var g = new CaseGrading(5, 3); // lowest grade B
        var (good, okWhy) = g.Judge(new CodeQuality?[] { Q(4), Q(3) });
        Assert.Empty(okWhy);
        Assert.Equal(3, good["child.grade_min"]);

        var (_, lowWhy) = g.Judge(new CodeQuality?[] { Q(4), Q(2, ("Modulation", 2)) });
        Assert.Equal(new[] { new PlantEvents.NgReason("child_codes", "grade_low") }, lowWhy);

        // a grade in doubt is NG, and it is still sent as ungraded for the trend
        var (v, doubtWhy) = g.Judge(new CodeQuality?[] { Q(4), null });
        Assert.Equal(new[] { new PlantEvents.NgReason("child_codes", "not_graded") }, doubtWhy);
        Assert.Equal(1, v["child.grade.ungraded"]);
    }

    [Fact]
    public void ParentGradesGoUnderTheParentToolWithParentReasons()
    {
        var (v, why) = new CaseGrading(5, 3, CaseGrading.Parent).Judge(new CodeQuality?[] { Q(2, ("Modulation", 2)) });
        Assert.Equal(2, v["parent.grade"]);
        Assert.Equal(3, v["parent.grade_min"]);
        Assert.All(v.Keys, k => Assert.StartsWith("parent.", k));
        Assert.Equal(new[] { new PlantEvents.NgReason("parent_code", "grade_low") }, why);
        Assert.Throws<ArgumentException>(() => new CaseGrading(5, -1, "side"));
    }

    [Fact]
    public void ASampledCaseCarriesChildAndParentGrades()
    {
        var child = new CaseGrading(3, 3);
        var parent = new CaseGrading(3, 3, CaseGrading.Parent);
        var (v, why) = CaseGrading.JudgeCase(child, true, new CodeQuality?[] { Q(4), Q(3) },
                                             parent, true, new CodeQuality?[] { Q(2) });
        Assert.Equal(3, v["child.grade"]);
        Assert.Equal(2, v["parent.grade"]);
        Assert.Equal(new[] { new PlantEvents.NgReason("parent_code", "grade_low") }, why);

        // a parent label that could not be graded is in doubt: NG
        var (_, doubt) = CaseGrading.JudgeCase(child, true, new CodeQuality?[] { Q(4) }, parent, true, new CodeQuality?[] { null });
        Assert.Equal(new[] { new PlantEvents.NgReason("parent_code", "not_graded") }, doubt);
    }

    [Fact]
    public void AnUnsampledCaseCarriesNoGrades()
    {
        var child = new CaseGrading(3, 3);
        var parent = new CaseGrading(3, 3, CaseGrading.Parent);
        // even with low codes: between samples a case is not judged on its grade
        var (v, why) = CaseGrading.JudgeCase(child, false, new CodeQuality?[] { Q(1) }, parent, false, new CodeQuality?[] { Q(0) });
        Assert.Empty(v);
        Assert.Empty(why);
        // an unread parent label carries no parent grade (it is NG as no_read already), and no parent camera none
        var (noRead, _) = CaseGrading.JudgeCase(child, true, new CodeQuality?[] { Q(4) }, parent, true, null);
        Assert.DoesNotContain(noRead.Keys, k => k.StartsWith("parent."));
        var (noCam, _) = CaseGrading.JudgeCase(child, true, new CodeQuality?[] { Q(4) }, null, true, new CodeQuality?[] { Q(4) });
        Assert.DoesNotContain(noCam.Keys, k => k.StartsWith("parent."));
    }

    [Fact]
    public void TheParentReaderIsSwitchedPerCaseInStepWithTheChild()
    {
        var childReader = new List<bool>();
        var parentReader = new List<bool>();
        var child = new CameraSampling(new CaseGrading(3, -1), childReader.Add);
        var parent = new CameraSampling(new CaseGrading(3, -1, CaseGrading.Parent), parentReader.Add);
        // both readers are set for the first case before any frame
        Assert.Equal(new[] { true }, childReader);
        Assert.Equal(new[] { true }, parentReader);

        var childGraded = Enumerable.Range(0, 7).Select(_ => child.Decoded()).ToArray();
        var parentGraded = Enumerable.Range(0, 7).Select(_ => parent.Decoded()).ToArray();
        Assert.Equal(new[] { true, false, false, true, false, false, true }, parentGraded);
        Assert.Equal(childGraded, parentGraded);
        // switched after every frame, to what the next case needs
        Assert.Equal(new[] { true, false, false, true, false, false, true, false }, parentReader);
        Assert.Equal(childReader, parentReader);
    }

    [Fact]
    public void MissedFramesKeepBothCamerasInStep()
    {
        var child = new CameraSampling(new CaseGrading(3, -1), _ => { });
        var parentReader = new List<bool>();
        var parent = new CameraSampling(new CaseGrading(3, -1, CaseGrading.Parent), parentReader.Add);
        // case 0 decoded on both; cases 1 and 2 never reached the PC from the parent camera; case 3 is sampled again
        Assert.True(child.Decoded());
        Assert.True(parent.Decoded());
        Assert.False(child.Decoded());
        Assert.False(child.Decoded());
        parent.Missed(2);
        Assert.True(parentReader[^1]); // set to grade case 3 before its frame
        Assert.True(child.Decoded());
        Assert.True(parent.Decoded());
        // and the other way round: the child misses, the parent decodes
        child.Missed(1);
        Assert.False(parent.Decoded());
        Assert.Equal(parent.Decoded(), child.Decoded());
    }

    [Fact]
    public void GradingOffNeverSwitchesAReaderOn()
    {
        var reader = new List<bool>();
        var off = new CameraSampling(new CaseGrading(0, -1, CaseGrading.Parent), reader.Add);
        for (int i = 0; i < 4; i++) Assert.False(off.Decoded());
        off.Missed(3);
        Assert.All(reader, Assert.False);
    }
}
