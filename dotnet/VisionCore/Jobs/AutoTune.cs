using System.Globalization;
using BarcodeReading;

namespace VisionCore;

/// <summary>One saved image of a product for auto-tune: a good part (must pass) or a bad part (must fail).</summary>
public sealed record TuneImage(string File, bool Good);

/// <summary>What the job gave on one tune image.</summary>
public sealed record TuneSample(TuneImage Image, JobResult Result)
{
    public string Name => Path.GetFileName(Image.File);
}

/// <summary>Which setting of a tool auto-tune moves.</summary>
public enum TuneKnobKind
{
    /// <summary>Find the part: the lowest match (<see cref="LocateToolConfig.MinScore"/>, 0..1).</summary>
    LocateMinScore,
    /// <summary>Check text: the strictness (<see cref="TextToolConfig.MinQuality"/>, 1..99), against PrintQuality.</summary>
    TextMinQuality,
    /// <summary>Read code: the lowest print grade (<see cref="CodeToolConfig.MinGrade"/>, 1 = D .. 4 = A), against the
    /// worst overall grade of the part's codes (requirements AS-03a).</summary>
    CodeMinGrade,
    /// <summary>Read code, Read/Check text: the time budget in ms (requirements AS-03b). Not a score: worked out
    /// from the time the tool took, never above now.</summary>
    TimeBudget,
}

/// <summary>
/// One tool setting auto-tune proposes. <see cref="LowestGood"/> is the lowest score of a good part the job can
/// pass; <see cref="HighestBad"/> the highest score of a bad part only this setting rejects (null = none). The
/// margins are the distances of <see cref="Proposed"/> from both.
/// </summary>
public sealed record TuneKnob(string Tool, TuneKnobKind Kind, float Current, float Proposed, float? LowestGood, float? HighestBad)
{
    public float? GoodMargin => LowestGood - Proposed;
    public float? BadMargin => Proposed - HighestBad;
    public bool Changed => Math.Abs(Proposed - Current) > 1e-4f;
    /// <summary>The setting accepts more parts. A shorter time budget never does: it only reads less.</summary>
    public bool Looser => Kind != TuneKnobKind.TimeBudget && Proposed < Current - 1e-4f;
    /// <summary>Time budget: the slowest time the budget must cover (ms), before the margin; null for the other kinds.</summary>
    public float? SlowestMs { get; init; }

    public string Setting => Kind switch
    {
        TuneKnobKind.LocateMinScore => "lowest match",
        TuneKnobKind.CodeMinGrade => "lowest print grade",
        TuneKnobKind.TimeBudget => "time budget",
        _ => "strictness",
    };

    public string Show(float v) => Kind switch
    {
        TuneKnobKind.LocateMinScore => v.ToString("0.00", CultureInfo.InvariantCulture),
        TuneKnobKind.CodeMinGrade => CodeQuality.Letter((int)MathF.Round(v)),
        TuneKnobKind.TimeBudget => v.ToString("0", CultureInfo.InvariantCulture) + " ms",
        _ => v.ToString("0", CultureInfo.InvariantCulture),
    };
}

/// <summary>Good and bad images counted on one run of the job.</summary>
public sealed record TuneCount(int GoodPass, int Good, int BadFail, int Bad, double MaxMs)
{
    public bool AllBadFail => BadFail == Bad;

    public static TuneCount Of(IReadOnlyList<TuneSample> run) => new(
        run.Count(s => s.Image.Good && s.Result.Pass), run.Count(s => s.Image.Good),
        run.Count(s => !s.Image.Good && !s.Result.Pass), run.Count(s => !s.Image.Good),
        run.Count == 0 ? 0 : run.Max(s => s.Result.TotalMs));

    /// <summary>Acceptable %: good parts that pass, 0..100; -1 when there are no good parts.</summary>
    public double GoodPassPct => Good == 0 ? -1 : 100.0 * GoodPass / Good;
    /// <summary>Not acceptable %: bad parts that fail, 0..100; -1 when there are no bad parts.</summary>
    public double BadFailPct => Bad == 0 ? -1 : 100.0 * BadFail / Bad;

    /// <summary>"38 of 40 good parts pass, all 12 bad parts fail, slowest 35 ms".</summary>
    public string Words()
    {
        string good = GoodPass == Good ? $"all {Good} good parts pass" : $"{GoodPass} of {Good} good parts pass";
        string bad = BadFail == Bad ? $"all {Bad} bad parts fail" : $"{Bad - BadFail} of {Bad} bad parts PASS";
        return $"{good}, {bad}, slowest {MaxMs:0} ms";
    }
}

/// <summary>
/// Settings auto-tune proposes for one camera of a job, worked out from a run with the tuned checks relaxed.
/// <see cref="Problem"/> is set (and <see cref="Knobs"/> empty) when no setting of these tools tells the good
/// images from the bad ones; the job is then left as it is.
/// </summary>
public sealed record TuneProposal(int Camera, IReadOnlyList<TuneKnob> Knobs, TuneCount Predicted, string? Problem)
{
    /// <summary>Good images that fail whatever these settings are (a wrong read, text not as expected).</summary>
    public IReadOnlyList<string> GoodAlwaysFail { get; init; } = Array.Empty<string>();
    /// <summary>Bad images that pass at the strictest setting that still passes every good image.</summary>
    public IReadOnlyList<string> BadNotSeparable { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The verdict on a proposal after the job was run again with it (<see cref="AutoTune.Verify"/>). It may be
/// applied only when <see cref="CanApply"/>: every bad image fails, and no good image that passed before fails.
/// </summary>
public sealed record TuneVerdict(TuneCount Before, TuneCount After, bool CanApply, string Words, IReadOnlyList<string> Lost);

/// <summary>
/// Auto-tune of a job on saved good and bad images (requirements AS-03): searches the thresholds of the tools
/// that decide on a score (Find the part's lowest match, Check text's strictness, Read code's lowest print grade)
/// for the widest margin between the good and the bad images, and never proposes a setting that lets a known bad
/// image pass (AS-12). It also proposes the lowest time budget of Read code and Read/Check text that keeps every
/// good image passing (<see cref="BudgetFor"/>, AS-03b).
///
/// How: the job is run once with those thresholds relaxed (<see cref="Relaxed"/>), which gives each tool's score
/// and whether it would pass on everything else. A tool then passes at threshold t when it passed relaxed and
/// its score is at least t, so the verdict of every setting is known without running again. The upper end of
/// each threshold is the lowest score of a good image the job can pass; each bad image the other checks do not
/// already fail is given to the tool that rejects it with most room, and the threshold is put half-way between
/// that tool's highest such bad score and its lowest good score. No setting is made less strict than now unless
/// now fails a good image (and then never below a floor). The job is then run again with the proposal (<see cref="Verify"/>)
/// and the proposal is offered only when the real run agrees: the scores are a model, the run is the proof.
/// Nothing is changed in the job until the user accepts (<see cref="Apply"/>) and the job is saved as a new
/// version.
/// </summary>
public static class AutoTune
{
    /// <summary>Find the part's lowest match during the relaxed run.</summary>
    public const float RelaxedMinScore = 0.30f;
    /// <summary>Check text's strictness during the relaxed run.</summary>
    public const float RelaxedMinQuality = 1f;
    /// <summary>The lowest match auto-tune proposes: below it a Find the part tool finds anything.</summary>
    public const float FloorMinScore = 0.40f;
    /// <summary>The lowest strictness auto-tune proposes.</summary>
    public const float FloorMinQuality = 20f;

    /// <summary>The highest lowest match proposed (the engine takes no more).</summary>
    public const float CeilingMinScore = 0.99f;

    /// <summary>A good part whose match is within this of Find the part's lowest match passed close to its limit.</summary>
    public const float NearMinScore = 0.10f;
    /// <summary>A good part whose print quality is within this of Check text's strictness passed close to its limit.</summary>
    public const float NearMinQuality = 10f;
    /// <summary>A good part whose worst code grade is within this of Read code's lowest print grade (graded at it) passed close to its limit.</summary>
    public const float NearMinGrade = 1f;

    /// <summary>The lowest print grade auto-tune proposes (D).</summary>
    public const float FloorMinGrade = 1f;
    /// <summary>The highest lowest print grade (A).</summary>
    public const float CeilingMinGrade = 4f;

    /// <summary>A proposed time budget is the slowest time it must cover times this, plus <see cref="BudgetExtraMs"/>.</summary>
    public const float BudgetFactor = 1.5f;
    /// <summary>Added to the slowest time for the timing noise of the line PC.</summary>
    public const float BudgetExtraMs = 10f;
    /// <summary>Time budgets are proposed in steps of this many ms.</summary>
    public const int BudgetStepMs = 5;

    /// <summary>
    /// Why a tool failed that a shorter time budget still fails: it found too little (no code, too few codes, no
    /// text, a missing line). Reading less finds no more, so such a bad image's time need not be covered. Any other
    /// failure (an extra code or text, a wrong one, a grade) could turn into a pass if the reader stopped before it
    /// saw the fault, so the budget covers its time.
    /// </summary>
    public static readonly IReadOnlySet<string> FoundTooLittle = new HashSet<string>
    {
        "explain.code.none", "explain.code.short", "explain.ocr.no_text", "explain.ocv.missing",
    };

    /// <summary>
    /// Whether a part of <paramref name="camera"/> that passed did so close to a tuned limit (owner, 2026-11-04: keep
    /// such good pictures for learning): a Find the part match or a Check text print quality just above its setting.
    /// Only the settings auto-tune moves are looked at, so a kept picture is one learning can use. False for a part
    /// that failed.
    /// </summary>
    public static bool NearLimit(Job job, int camera, JobResult r)
    {
        if (!r.Pass) return false;
        foreach (var t in Tunable(job, camera))
        {
            var res = r.Tools.FirstOrDefault(x => string.Equals(x.ToolName, t.Name, StringComparison.OrdinalIgnoreCase));
            if (res is null) continue;
            if (t is LocateToolConfig l && res.MatchScore is { } m && m - l.MinScore < NearMinScore) return true;
            if (t is TextToolConfig x && res.PrintQuality is { } q && q - x.MinQuality < NearMinQuality) return true;
            if (t is CodeToolConfig c && GradeOf(res) is { } g && g - c.MinGrade < NearMinGrade) return true;
        }
        return false;
    }

    /// <summary>
    /// The tuned tools of a camera, in job order. Check text is tuned only where its score does not depend on
    /// the strictness and the run needs nothing from the PLC: with learned print limits the engine scales ink,
    /// contrast and pitch by the strictness, so a relaxed run would not show the score at another setting.
    /// Read code is tuned only where a lowest print grade is set and every part is graded: a tool that grades one
    /// part in N does not judge the parts between, so a bad image may pass whatever its grade.
    /// </summary>
    public static IEnumerable<ToolConfig> Tunable(Job job, int camera) =>
        job.Tools.Where(t => t.Enabled && t.Camera == camera &&
                             (t is LocateToolConfig ||
                              t is TextToolConfig { Mode: TextMode.Check } x && string.IsNullOrEmpty(x.PrintReference) &&
                              x.Lines.All(l => l.Source != ExpectedSource.Plc) ||
                              t is CodeToolConfig { MinGrade: >= 1, GradeEvery: <= 1 }));

    /// <summary>The tools of a camera whose time budget auto-tune proposes: Read code and Read/Check text with a budget set.</summary>
    public static IEnumerable<ToolConfig> Budgeted(Job job, int camera) =>
        job.Tools.Where(t => t.Enabled && t.Camera == camera && BudgetOf(t) > 0);

    private static int BudgetOf(ToolConfig t) => t switch
    {
        CodeToolConfig c => c.TimeBudgetMs,
        TextToolConfig x => x.TimeBudgetMs,
        _ => 0,
    };

    /// <summary>The worst overall print grade of a Read code result's codes; null when a code was not graded or none was.</summary>
    private static float? GradeOf(ToolResult r) =>
        r.Values.ContainsKey("grade.ungraded") ? null : r.Values.TryGetValue("grade", out var g) ? (float)g : null;

    /// <summary>A copy of the job with the tuned thresholds of <paramref name="camera"/> relaxed.</summary>
    public static Job Relaxed(Job job, int camera)
    {
        var c = JobStore.Clone(job);
        foreach (var t in Tunable(c, camera).ToList())
        {
            if (t is LocateToolConfig l) l.MinScore = Math.Min(l.MinScore, RelaxedMinScore);
            if (t is TextToolConfig x) x.MinQuality = RelaxedMinQuality;
            // graded and recorded, never failed on the grade: the result's "grade" is the score
            if (t is CodeToolConfig k) { k.MinGrade = -1; k.RecordGrades = true; }
        }
        return c;
    }

    /// <summary>A copy of the job with the proposal's settings.</summary>
    public static Job Apply(Job job, TuneProposal p)
    {
        var c = JobStore.Clone(job);
        foreach (var k in p.Knobs)
        {
            var t = c.Tools.FirstOrDefault(t => t.Camera == p.Camera && string.Equals(t.Name, k.Tool, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{k.Tool} is no longer in the job");
            switch (t, k.Kind)
            {
                case (LocateToolConfig l, TuneKnobKind.LocateMinScore): l.MinScore = k.Proposed; break;
                case (TextToolConfig x, TuneKnobKind.TextMinQuality): x.MinQuality = k.Proposed; break;
                case (CodeToolConfig cd, TuneKnobKind.CodeMinGrade): cd.MinGrade = (int)MathF.Round(k.Proposed); break;
                case (CodeToolConfig cd, TuneKnobKind.TimeBudget): cd.TimeBudgetMs = (int)k.Proposed; break;
                case (TextToolConfig x, TuneKnobKind.TimeBudget): x.TimeBudgetMs = (int)k.Proposed; break;
                default: throw new InvalidOperationException($"{k.Tool} has changed kind since the tune");
            }
        }
        return c;
    }

    private static float? ScoreOf(ToolResult r, TuneKnobKind kind) => kind switch
    {
        TuneKnobKind.LocateMinScore => r.MatchScore,
        TuneKnobKind.CodeMinGrade => GradeOf(r),
        _ => r.PrintQuality,
    };

    private static TuneKnobKind KindOf(ToolConfig t) => t switch
    {
        LocateToolConfig => TuneKnobKind.LocateMinScore,
        CodeToolConfig => TuneKnobKind.CodeMinGrade,
        _ => TuneKnobKind.TextMinQuality,
    };

    private static float CurrentOf(ToolConfig t) => t switch
    {
        LocateToolConfig l => l.MinScore,
        CodeToolConfig c => c.MinGrade,
        _ => ((TextToolConfig)t).MinQuality,
    };

    private static (float Floor, float Ceiling, float Step, float Scale) Range(TuneKnobKind kind) => kind switch
    {
        TuneKnobKind.LocateMinScore => (FloorMinScore, CeilingMinScore, 0.01f, 1f),
        TuneKnobKind.CodeMinGrade => (FloorMinGrade, CeilingMinGrade, 1f, 4f),
        _ => (FloorMinQuality, 99f, 1f, 100f),
    };

    /// <summary>
    /// The time budget proposed for <paramref name="tool"/> from the relaxed run (requirements AS-03b), or null to
    /// leave it: the slowest time it must cover times <see cref="BudgetFactor"/> plus <see cref="BudgetExtraMs"/>,
    /// rounded up to <see cref="BudgetStepMs"/>, when that is at least one step below now. It covers every good image
    /// the tool passes and every bad image, except one the tool fails for finding too little (<see cref="FoundTooLittle"/>).
    /// A bad image the tool passes is covered too: in the relaxed run its fault may be one the run does not judge
    /// (a code's print grade), and reading less could leave that code unread. Null when no good image passes the
    /// tool (nothing shows how long a good part takes).
    /// </summary>
    public static TuneKnob? BudgetFor(ToolConfig tool, IReadOnlyList<TuneSample> relaxed)
    {
        int now = BudgetOf(tool);
        if (now <= 0) return null;
        double slowest = 0;
        bool anyGood = false;
        foreach (var s in relaxed)
        {
            var r = s.Result.Tools.FirstOrDefault(t => string.Equals(t.ToolName, tool.Name, StringComparison.OrdinalIgnoreCase));
            if (r is null) continue;
            if (s.Image.Good) { if (!r.Pass) continue; anyGood = true; }
            else if (!r.Pass && r.Explain is { } e && FoundTooLittle.Contains(e)) continue;
            slowest = Math.Max(slowest, r.TimeMs);
        }
        if (!anyGood) return null;
        int proposed = (int)Math.Ceiling((slowest * BudgetFactor + BudgetExtraMs) / BudgetStepMs) * BudgetStepMs;
        if (proposed > now - BudgetStepMs) return null;
        return new TuneKnob(tool.Name, TuneKnobKind.TimeBudget, now, proposed, null, null) { SlowestMs = (float)slowest };
    }

    /// <summary>Works out the settings from the relaxed run of <paramref name="job"/> (as it is now, not relaxed).</summary>
    public static TuneProposal Propose(Job job, int camera, IReadOnlyList<TuneSample> relaxed)
    {
        var tools = Tunable(job, camera).ToList();
        var kinds = tools.Select(KindOf).ToList();
        var current = tools.Select(CurrentOf).ToList();
        int n = tools.Count;
        var none = TuneCount.Of(Array.Empty<TuneSample>());
        if (n == 0 && !Budgeted(job, camera).Any())
            return new TuneProposal(camera, Array.Empty<TuneKnob>(), none, "This camera has no Find the part, Check text or Read code tool to tune");
        if (!relaxed.Any(s => s.Image.Good)) return new TuneProposal(camera, Array.Empty<TuneKnob>(), none, "Add images of good parts");
        if (!relaxed.Any(s => !s.Image.Good)) return new TuneProposal(camera, Array.Empty<TuneKnob>(), none, "Add images of bad parts");

        // per image: did the job pass relaxed, and each tuned tool's score (null = the tool gave none: it did not
        // run, or the scoring failed; such an image passes no threshold)
        var rows = relaxed.Select(s =>
        {
            var scores = new float?[n];
            for (int i = 0; i < n; i++)
            {
                var r = s.Result.Tools.FirstOrDefault(t => string.Equals(t.ToolName, tools[i].Name, StringComparison.OrdinalIgnoreCase));
                scores[i] = r is null || !r.Pass ? null : ScoreOf(r, kinds[i]);
            }
            return (Sample: s, Scores: scores, Passable: s.Result.Pass && scores.All(v => v is not null));
        }).ToList();

        var goods = rows.Where(r => r.Sample.Image.Good).ToList();
        var passableGoods = goods.Where(r => r.Passable).ToList();
        var alwaysFail = goods.Where(r => !r.Passable).Select(r => r.Sample.Name).ToList();

        // the strictest setting that still passes every good image the job can pass
        var upper = new float?[n];
        for (int i = 0; i < n; i++)
            upper[i] = passableGoods.Count == 0 ? null
                     : Math.Min(passableGoods.Min(r => r.Scores[i]!.Value), Range(kinds[i]).Ceiling);

        // each bad image the other checks do not fail must be failed by a tuned tool
        var assigned = new List<float>[n];
        for (int i = 0; i < n; i++) assigned[i] = new List<float>();
        var notSeparable = new List<string>();
        foreach (var b in rows.Where(r => !r.Sample.Image.Good && r.Passable))
        {
            int best = -1;
            float room = 0;
            for (int i = 0; i < n; i++)
            {
                if (upper[i] is not { } u || b.Scores[i]!.Value >= u) continue;
                float scale = Range(kinds[i]).Scale;
                float rm = (u - b.Scores[i]!.Value) / scale;
                if (best < 0 || rm > room) { best = i; room = rm; }
            }
            if (best < 0) notSeparable.Add(b.Sample.Name);
            else assigned[best].Add(b.Scores[best]!.Value);
        }
        if (passableGoods.Count == 0)
            return new TuneProposal(camera, Array.Empty<TuneKnob>(), none,
                "No good image passes the job on its other checks, so there is nothing to tune against") { GoodAlwaysFail = alwaysFail };
        if (notSeparable.Count > 0)
            return new TuneProposal(camera, Array.Empty<TuneKnob>(), none,
                $"{notSeparable.Count} bad image(s) score as well as the good ones on every tuned tool, so no setting fails them. " +
                "Check that they are really bad parts, or add a tool that sees their fault") { GoodAlwaysFail = alwaysFail, BadNotSeparable = notSeparable };

        // a setting is made stricter (to fail a bad image, half-way to the good ones) but never less strict than
        // now, unless now fails a good image the job can otherwise pass; then it is lowered half-way to the bad
        // ones, or to the floor, and never below the floor (that good image is then listed as failing)
        var knobs = new List<TuneKnob>(n);
        var belowFloor = new List<string>();
        for (int i = 0; i < n; i++)
        {
            float u = upper[i]!.Value, cur = current[i];
            float floor = Range(kinds[i]).Floor;
            float? hb = assigned[i].Count == 0 ? null : assigned[i].Max();
            float t;
            if (cur <= u) t = hb is { } h ? Math.Max(cur, Between(h, u, kinds[i])) : cur;
            else if (hb is { } h2) t = Between(h2, u, kinds[i]);
            else if (u >= floor) t = Math.Max(floor, Round(u - (u - floor) / 2, kinds[i], down: true));
            else
            {
                t = cur;
                int k = i;
                belowFloor.AddRange(passableGoods.Where(r => r.Scores[k]!.Value < cur).Select(r => r.Sample.Name));
            }
            knobs.Add(new TuneKnob(tools[i].Name, kinds[i], cur, t, u, hb));
        }

        // the verdict of every image at the proposed settings, from the scores (a time budget is proved by the run)
        var predicted = relaxed.Select((s, j) => s with
        {
            Result = s.Result with { Pass = rows[j].Passable && knobs.Select((k, i) => rows[j].Scores[i]!.Value >= k.Proposed).All(x => x) },
        }).ToList();
        foreach (var t in Budgeted(job, camera))
            if (BudgetFor(t, relaxed) is { } b) knobs.Add(b);
        return new TuneProposal(camera, knobs, TuneCount.Of(predicted), null) { GoodAlwaysFail = alwaysFail.Concat(belowFloor.Distinct()).ToList() };
    }

    // half-way between the highest bad score and the lowest good one, rounded to the setting's step when the
    // rounded value still fails that bad score and passes that good one
    private static float Between(float bad, float good, TuneKnobKind kind)
    {
        float mid = (bad + good) / 2;
        // a grade is whole: half-way up, so it still fails that bad grade and passes that good one
        if (kind == TuneKnobKind.CodeMinGrade) return Math.Clamp(MathF.Ceiling(mid), FloorMinGrade, CeilingMinGrade);
        float r = Round(mid, kind, down: false);
        if (r > bad && r <= good) mid = r;
        float lo = kind == TuneKnobKind.LocateMinScore ? 0.01f : 1f, hi = Range(kind).Ceiling;
        return Math.Clamp(mid, lo, hi);
    }

    private static float Round(float v, TuneKnobKind kind, bool down)
    {
        float step = Range(kind).Step;
        double q = v / step;
        return (float)((down ? Math.Floor(q) : Math.Round(q)) * step);
    }

    /// <summary>
    /// Compares the run with the proposal against the run of the job as it is. The proposal may be applied only
    /// when every bad image fails on a real check (not a tool error), every good image that passed before still
    /// passes, a less strict setting passes more good images than before, and the slowest image is no slower
    /// than before (a quarter or 10 ms of timing noise allowed).
    /// </summary>
    public static TuneVerdict Verify(TuneProposal p, IReadOnlyList<TuneSample> before, IReadOnlyList<TuneSample> after)
    {
        var b = TuneCount.Of(before);
        var a = TuneCount.Of(after);
        var passedBefore = before.Where(s => s.Image.Good && s.Result.Pass).Select(s => s.Image.File).ToHashSet();
        var lost = after.Where(s => s.Image.Good && !s.Result.Pass && passedBefore.Contains(s.Image.File)).Select(s => s.Name).ToList();
        var badPass = after.Where(s => !s.Image.Good && s.Result.Pass).Select(s => s.Name).ToList();
        // a bad image failed only by a tool error is in doubt: it may pass once the error is gone
        var badErr = after.Where(s => !s.Image.Good && !s.Result.Pass &&
                                      s.Result.Tools.Where(t => !t.Pass).All(t => t.Explain == "explain.tool.error")).Select(s => s.Name).ToList();
        bool looser = p.Knobs.Any(k => k.Looser);
        bool slower = a.MaxMs > Math.Max(b.MaxMs * 1.25, b.MaxMs + 10);
        bool ok = p.Problem is null && p.Knobs.Count > 0 && badPass.Count == 0 && badErr.Count == 0 && lost.Count == 0;
        string words = $"Now: {b.Words()}. With the new settings: {a.Words()}.";
        if (badPass.Count > 0) words += $" Not offered: {string.Join(", ", badPass)} would pass.";
        else if (badErr.Count > 0) words += $" Not offered: {string.Join(", ", badErr)} failed only on a tool error, so it is not known whether they would pass.";
        else if (lost.Count > 0) words += $" Not offered: {string.Join(", ", lost)} passed before and would fail.";
        else if (ok && looser && a.GoodPass <= b.GoodPass) { ok = false; words += " Not offered: a less strict setting that passes no more good parts."; }
        else if (ok && slower) { ok = false; words += " Not offered: slower than now."; }
        else if (ok && !p.Knobs.Any(k => k.Changed)) { ok = false; words += " The settings are already the best found."; }
        return new TuneVerdict(b, a, ok, words, lost);
    }

    /// <summary>Runs <paramref name="job"/> on the images of one camera (the runner is built once).</summary>
    public static List<TuneSample> Run(Job job, int camera, IReadOnlyList<TuneImage> images, ModelPaths models, BatchFields batch,
                                       CancellationToken token = default, Action<int>? progress = null)
    {
        var list = new List<TuneSample>(images.Count);
        using var runner = new JobRunner(job, models);
        foreach (var im in images)
        {
            token.ThrowIfCancellationRequested();
            list.Add(new TuneSample(im, runner.Run(camera, GrayImage.Load(im.File), batch)));
            progress?.Invoke(list.Count);
        }
        return list;
    }
}
