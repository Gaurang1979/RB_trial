using VisionSystem.Runtime;
using Xunit;

/// <summary>The browser dashboard's grades kept across a runtime restart (requirements 6.4 WEB-06): read back
/// within the count and age limits of WEB-05, a torn line is skipped and doubt is never read as a good grade.</summary>
public sealed class GradeTrailFileTests : IDisposable
{
    static readonly DateTime Now = new(2026, 11, 3, 12, 30, 0, DateTimeKind.Utc);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "vs-grades-" + Guid.NewGuid().ToString("N"));
    string File_ => Path.Combine(_dir, "grades.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void After_a_restart_the_trend_and_drift_show_the_earlier_samples()
    {
        using (var before = new GradeTrail(File_, Now))
        {
            for (var i = 0; i < 20; i++) before.Add("QR", Now.AddMinutes(-20 + i), i % 2 == 0 ? 2.4 : null, 3);
            before.Add("DM", Now.AddMinutes(-1), 4);
        }
        using var after = new GradeTrail(File_, Now);
        Assert.Equal(["DM", "QR"], after.Tools());
        var (buckets, drift) = after.Of("QR", Now.AddHours(-8), Now)!.Value;
        Assert.Equal(20, buckets.Sum(b => b.Samples));
        Assert.Equal(10, buckets.Sum(b => b.Ungraded));
        Assert.Equal(("drift", 20, 10, 3.0, "tool"), (drift.State, drift.Samples, drift.Ungraded, drift.Lowest, drift.LowestFrom));
        Assert.Equal(1.2, drift.Mean);
    }

    [Fact]
    public void Read_back_keeps_the_newest_per_tool_and_drops_samples_older_than_a_week()
    {
        using (var before = new GradeTrail(File_, Now))
        {
            before.Add("OLD", Now.AddHours(-GradeTrail.KeepHours - 1), 1);
            for (var i = 0; i < GradeTrail.Keep + 50; i++) before.Add("QR", Now.AddSeconds(-GradeTrail.Keep - 50 + i), i < 50 ? 0 : 4);
        }
        using var after = new GradeTrail(File_, Now);
        var (b, _) = after.Of("QR", Now.AddDays(-30), Now)!.Value;
        Assert.Equal(GradeTrail.Keep, b.Sum(x => x.Samples));
        Assert.Equal(4.0, b.Min(x => x.Worst));      // the 50 oldest are gone
        Assert.Equal(["QR"], after.Tools());          // and the tool whose only sample is over a week old
        // the file was rewritten to what is kept
        Assert.Equal(GradeTrail.Keep, File.ReadLines(File_).Count());
    }

    [Fact]
    public void A_torn_or_bad_line_is_skipped_and_a_bad_grade_reads_as_ungraded()
    {
        Directory.CreateDirectory(_dir);
        var at = Now.AddMinutes(-5).ToString("O");
        File.WriteAllLines(File_,
        [
            $$"""{"T":"QR","At":"{{at}}","G":3.5,"L":null}""",
            $$"""{"T":"QR","At":"{{at}}","G":9,"L":7}""",        // not a grade: ungraded, no lowest grade
            "not json",
            $$"""{"T":"","At":"{{at}}","G":4}""",
            $$"""{"T":"QR","At":"{{at}}","G":4""",                // torn by a power cut
        ]);
        using var t = new GradeTrail(File_, Now);
        var (b, d) = t.Of("QR", Now.AddHours(-1), Now)!.Value;
        Assert.Equal((2, 1), (b.Single().Samples, b.Single().Ungraded));
        Assert.Equal("default", d.LowestFrom);
        Assert.Equal(["QR"], t.Tools());
    }

    [Fact]
    public void A_file_that_cannot_be_written_never_stops_the_grades()
    {
        Directory.CreateDirectory(File_);   // a folder where the file should be: every write fails
        using var t = new GradeTrail(File_, Now);
        t.Add("QR", Now, 4);
        t.Flush();
        Assert.Equal(1, t.Of("QR", Now.AddHours(-1), Now)!.Value.Buckets.Single().Samples);
    }

    [Fact]
    public void At_most_the_tool_limit_is_kept()
    {
        using var t = new GradeTrail();
        for (var i = 0; i < GradeTrail.MaxTools + 5; i++) t.Add($"T{i}", Now, 4);
        Assert.Equal(GradeTrail.MaxTools, t.Tools().Count);
    }
}
