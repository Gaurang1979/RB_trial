using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>The Overview charts' numbers, worked out from the parts list (UI-UX-SPEC.md 4.1).</summary>
public class OverviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 30, 40, TimeSpan.Zero);

    private static PartResult Part(ulong id, DateTimeOffset at, Verdict result = Verdict.Ok, double ms = 50, string reason = "") =>
        new(id, at, "Whisky", $"P{id}", result, ms, reason);

    private static OverviewData From(PartLog log) => OverviewData.From(log, Now, "Same code twice", "No reason given", "Other reasons");

    [Fact]
    public void An_empty_list_gives_no_charts()
    {
        var data = From(new PartLog());
        Assert.False(data.HasParts);
        Assert.Empty(data.Minutes);
        Assert.Empty(data.Cycle);
    }

    [Fact]
    public void Parts_fall_into_their_minute_of_the_last_hour_and_older_ones_are_left_out()
    {
        var log = new PartLog();
        log.Add(Part(1, Now.AddMinutes(-90)));                     // older than the hour
        log.Add(Part(2, Now.AddMinutes(-59).AddSeconds(-40)));     // first minute shown, 08:31
        log.Add(Part(3, Now.AddSeconds(-30)));                     // 09:30, this minute
        log.Add(Part(4, Now.AddSeconds(-20), Verdict.Ng, reason: "Code not read"));
        log.Add(Part(5, Now.AddSeconds(-10), Verdict.Duplicate));

        var data = From(log);
        Assert.Equal(OverviewData.MinutesShown, data.Minutes.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 8, 31, 0, TimeSpan.Zero), data.Minutes[0].Start);
        Assert.Equal((1, 0), (data.Minutes[0].Good, data.Minutes[0].Bad));
        Assert.Equal((1, 2), (data.Minutes[^1].Good, data.Minutes[^1].Bad));
        Assert.Equal(4, data.Minutes.Sum(m => m.Total));
        Assert.Null(data.GoodPercentByMinute[1]);
        Assert.Equal(100.0 / 3, data.GoodPercentByMinute[^1]!.Value, 6);
    }

    [Fact]
    public void The_hour_is_complete_at_line_speed_although_the_list_keeps_only_the_newest_parts()
    {
        var log = new PartLog();
        ulong id = 0;
        for (var minute = 14; minute >= 0; minute--)
            for (var i = 0; i < 225; i++)
                log.Add(Part(++id, Now.AddMinutes(-minute).AddSeconds(-40 + i * 0.17), i == 0 ? Verdict.Ng : Verdict.Ok, reason: "Code not read"));

        Assert.Equal(PartLog.Capacity, log.Entries.Count);
        var data = From(log);
        Assert.Equal(15 * 225, data.Minutes.Sum(m => m.Total));
        Assert.All(data.Minutes.TakeLast(15), m => Assert.Equal((224, 1), (m.Good, m.Bad)));
        Assert.Equal(15, Assert.Single(data.Reasons).Count);
    }

    [Fact]
    public void Reasons_are_largest_first_and_the_smallest_fold_into_other()
    {
        var log = new PartLog();
        ulong id = 0;
        void Bad(string reason, int n, Verdict v = Verdict.Ng)
        {
            for (var i = 0; i < n; i++) log.Add(Part(++id, Now.AddSeconds(-1), v, reason: reason));
        }
        Bad("Code not read", 9);
        Bad("Line 4 cut short", 6);
        Bad("1D code missing", 4);
        Bad("", 3, Verdict.Duplicate);
        Bad("", 2);
        Bad("Camera 2 dark", 1);
        log.Add(Part(++id, Now.AddSeconds(-1)));

        var reasons = From(log).Reasons;
        Assert.Equal(OverviewData.ReasonsShown, reasons.Count);
        Assert.Equal(["Code not read", "Line 4 cut short", "1D code missing", "Same code twice", "Other reasons"], reasons.Select(r => r.Reason));
        Assert.Equal([9, 6, 4, 3, 3], reasons.Select(r => r.Count));
    }

    [Fact]
    public void Cycle_times_spread_over_round_bands_with_median_p95_and_slowest()
    {
        var log = new PartLog();
        for (ulong i = 1; i <= 100; i++) log.Add(Part(i, Now.AddSeconds(-1), ms: i));   // 1..100 ms
        log.Add(Part(101, Now.AddSeconds(-1), ms: 0));                                  // no time reported

        var data = From(log);
        Assert.Equal(100, data.CycleParts);
        Assert.Equal((50, 95, 100), (data.MedianMs, data.P95Ms, data.SlowestMs));
        Assert.All(data.Cycle, b => Assert.Equal(10, b.ToMs - b.FromMs));
        Assert.Equal(100, data.Cycle.Sum(b => b.Count));
        Assert.True(data.Cycle.Count <= OverviewData.MaxBins);
        Assert.True(data.Cycle[^1].ToMs > data.SlowestMs);
    }

    [Fact]
    public void Only_the_last_300_cycle_times_count()
    {
        var log = new PartLog();
        for (ulong i = 1; i <= 400; i++) log.Add(Part(i, Now.AddSeconds(-1), ms: i <= 100 ? 1000 : 50));
        var data = From(log);
        Assert.Equal(OverviewData.CyclePartsUsed, data.CycleParts);
        Assert.Equal(50, data.SlowestMs);
    }

    [Fact]
    public void One_part_with_a_wrong_clock_does_not_empty_the_hour()
    {
        var log = new PartLog();
        log.Add(Part(1, Now.AddDays(1)));                       // a runtime clock a day ahead, then corrected
        for (ulong i = 2; i <= 11; i++) log.Add(Part(i, Now.AddSeconds(-1)));
        Assert.Equal(10, From(log).Minutes.Sum(m => m.Total));
    }

    [Fact]
    public void An_infinite_or_missing_cycle_time_is_left_out()
    {
        var log = new PartLog();
        log.Add(Part(1, Now, ms: double.PositiveInfinity));
        log.Add(Part(2, Now, ms: double.NaN));
        log.Add(Part(3, Now, ms: 40));
        var data = From(log);
        Assert.Equal((1, 40.0), (data.CycleParts, data.SlowestMs));
        Assert.All(data.Cycle, b => Assert.True(double.IsFinite(b.ToMs)));
    }

    [Fact]
    public void Reasons_sent_as_keys_are_shown_in_words_and_free_texts_stop_at_the_limit()
    {
        var log = new PartLog();
        ulong id = 0;
        log.Add(Part(++id, Now, Verdict.Ng, reason: "explain.code.short"));
        for (var i = 0; i < PartLog.MaxReasons + 20; i++) log.Add(Part(++id, Now, Verdict.Ng, reason: $"error {i}"));
        Assert.Equal(PartLog.MaxReasons + 1, log.Reasons.Count);   // and the one "no reason" entry they fold into
        var data = OverviewData.From(log, Now, "Same code twice", "No reason given", "Other reasons",
            r => r == "explain.code.short" ? "Fewer codes than expected" : r);
        Assert.Contains(data.Reasons, r => r.Reason == "No reason given" && r.Count == 21);
        Assert.DoesNotContain(data.Reasons, r => r.Reason == "explain.code.short");
    }

    [Theory]
    [InlineData(0.3, 0.5)]
    [InlineData(6.25, 10)]
    [InlineData(1.2, 2)]
    [InlineData(17, 20)]
    [InlineData(22, 25)]
    [InlineData(250, 250)]
    [InlineData(260, 500)]
    public void Nice_steps_are_1_2_2_5_or_5_times_a_power_of_ten(double atLeast, double step) =>
        Assert.Equal(step, OverviewData.NiceStep(atLeast), 9);
}
