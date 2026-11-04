using System;
using System.Collections.Generic;
using System.Linq;
using BarcodeReading;
using PlantEvents;
using VisionCore;

/// <summary>
/// Grade sampling of one camera's codes on an aggregation line (requirements 17.3 PQ-07, PQ-08), the way Vision
/// Studio's Read code tool does it (<see cref="CodeTool.IsSample"/>, <see cref="CodeTool.GradeValues"/>): one case
/// in N is graded, its grades go to the plant server in the result's values map under the tool name ("child" for
/// the child codes, "parent" for the case label), and with a lowest grade a sampled code below it, or not gradable,
/// rejects the case. Cases between samples are not judged on their grade. Grades 4 = A .. 1 = D, 0 = F.
/// Each camera has its own: it counts that camera's frames, so the k-th child and k-th parent frame (one case)
/// are sampled alike.
/// </summary>
public sealed class CaseGrading
{
    /// <summary>The tool name of the child codes' grades ("child.grade", "child.grade.modulation", ...).</summary>
    public const string Child = "child";
    /// <summary>The tool name of the parent (case) label's grades ("parent.grade", ...).</summary>
    public const string Parent = "parent";

    private long _cases;

    /// <param name="every">Grade one case in this many (1 = every case); 0 = only with a lowest grade, then every case.</param>
    /// <param name="minGrade">Lowest grade a code may have (4 = A .. 1 = D); -1 = not judged on its grade.</param>
    /// <param name="tool">The tool name the grades are sent under: <see cref="Child"/> or <see cref="Parent"/>.</param>
    public CaseGrading(int every, int minGrade, string tool = Child)
    {
        if (every is < 0 or > 10000) throw new ArgumentException("--grade-every: 1 (every case) to 10000");
        if (minGrade is not (-1 or 1 or 2 or 3 or 4)) throw new ArgumentException("--min-grade must be A, B, C or D");
        if (tool is not (Child or Parent)) throw new ArgumentException("tool must be child or parent");
        MinGrade = minGrade;
        Every = every == 0 && minGrade >= 0 ? 1 : every;
        Tool = tool;
    }

    public string Tool { get; }
    public int Every { get; }
    public int MinGrade { get; }
    /// <summary>Grading is on: a sampling rate or a lowest grade was given.</summary>
    public bool Enabled => Every > 0;

    /// <summary>Whether the next case is graded: the first, then every Nth. Called once per decoded case, before
    /// its decode, so the reader grades only the sampled ones (grading costs time on fast lines).</summary>
    public bool NextCase() { bool sampled = Sampled; _cases++; return sampled; }

    /// <summary>Whether the camera's next frame is a sampled case (what its reader should be set to now).</summary>
    public bool Sampled => Enabled && CodeTool.IsSample(_cases, Every);

    /// <summary>Frames the camera took but the PC never got: each was a case, so the count moves past them and
    /// stays in step with the other camera.</summary>
    public void Skip(int frames) => _cases += Math.Max(0, frames);

    /// <summary>A graded case's grades for the plant result, and why it is NG on its grade (empty when it is not).
    /// <paramref name="qualities"/>: each child code's quality (null = could not be graded).</summary>
    public (Dictionary<string, double> Values, List<NgReason> Reasons) Judge(IReadOnlyList<CodeQuality?> qualities)
    {
        var values = CodeTool.GradeValues(qualities, MinGrade).ToDictionary(kv => $"{Tool}.{kv.Key}", kv => kv.Value);
        var reasons = new List<NgReason>();
        if (MinGrade >= 0)
        {
            string part = Tool == Parent ? "parent_code" : "child_codes";
            // a grade in doubt is NG (line safety): a code that could not be graded fails like a low one
            if (qualities.Any(q => q is null)) reasons.Add(new NgReason(part, "not_graded"));
            if (qualities.Any(q => q is not null && q.OverallGrade < MinGrade)) reasons.Add(new NgReason(part, "grade_low"));
        }
        return (values, reasons);
    }

    /// <summary>One case's grades for its plant result, and why it is NG on them: the child codes when the child
    /// frame was graded, the parent label when the parent frame was graded and its label read (an unread label is
    /// already NG as no_read). A case neither camera graded carries none.
    /// <paramref name="parentCodes"/>: the parent label's quality (the one code the case is named by); null or
    /// empty = not read.</summary>
    public static (Dictionary<string, double> Values, List<NgReason> Reasons) JudgeCase(
        CaseGrading child, bool childGraded, IReadOnlyList<CodeQuality?> childCodes,
        CaseGrading? parent, bool parentGraded, IReadOnlyList<CodeQuality?>? parentCodes)
    {
        var values = new Dictionary<string, double>();
        var reasons = new List<NgReason>();
        if (childGraded)
        {
            var (v, why) = child.Judge(childCodes);
            foreach (var kv in v) values[kv.Key] = kv.Value;
            reasons.AddRange(why);
        }
        if (parent is not null && parentGraded && parentCodes is { Count: > 0 })
        {
            var (v, why) = parent.Judge(parentCodes);
            foreach (var kv in v) values[kv.Key] = kv.Value;
            reasons.AddRange(why);
        }
        return (values, reasons);
    }

    /// <summary>"A".."D" (case-insensitive) to the grade number 4..1.</summary>
    public static int ParseGrade(string s) => s.Trim().ToUpperInvariant() switch
    {
        "A" => 4, "B" => 3, "C" => 2, "D" => 1,
        _ => throw new ArgumentException("--min-grade must be A, B, C or D"),
    };
}

/// <summary>
/// One camera's reader switched per case (requirements 17.3 PQ-08): before each decode the reader is set to grade
/// or not, from that camera's own case count, so grading costs time only on the sampled cases. Called on the
/// camera's own thread (its decoded and missed frame events), which is the thread that decodes next, so the switch
/// always lands before the next frame. Each frame reports whether it was really decoded with grading on.
/// </summary>
public sealed class CameraSampling
{
    private readonly CaseGrading _grading;
    private readonly Action<bool> _switchReader;
    private bool _on;

    /// <param name="switchReader">Sets the camera's reader to grade (true) or not; called now and after every frame.</param>
    public CameraSampling(CaseGrading grading, Action<bool> switchReader)
    {
        _grading = grading;
        _switchReader = switchReader;
        Switch();
    }

    public CaseGrading Grading => _grading;

    /// <summary>A frame was decoded: whether it was graded; the reader is then set for the next frame.</summary>
    public bool Decoded()
    {
        bool graded = _on;
        _grading.NextCase();
        Switch();
        return graded;
    }

    /// <summary>Frames that never reached the PC: the count moves past them and the reader is set for the next.</summary>
    public void Missed(int frames)
    {
        _grading.Skip(frames);
        Switch();
    }

    private void Switch()
    {
        _on = _grading.Sampled;
        _switchReader(_on);
    }
}
