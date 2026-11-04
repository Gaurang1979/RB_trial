using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace BarcodeReading.Dashboard.Services;

/// <summary>A retraining report from ml/retrain.py (models/candidates/report-*.json).</summary>
public sealed record CandidateReport(
    string Stamp, string Decision, string? Candidate, int HardCases, int Learnable,
    int GoldenLabelled, int GoldenOld, int GoldenNew, int HardLabelled, int HardOld, int HardNew, bool Passed)
{
    public string? CandidateFile => Candidate is null ? null : Path.GetFileName(Candidate);
}

public sealed record ModelHistoryEntry(DateTime Time, string Promoted, string ApprovedBy);

/// <summary>
/// The AI models in use, the retraining agent's candidates, and promotion with an approver.
/// Mirrors `retrain.py --promote`: the old model is kept, and every promotion is appended to
/// models/history.jsonl. A promoted model is used from the next station start.
/// </summary>
public sealed class ModelRegistry
{
    private readonly string _dir;

    public ModelRegistry(IOptions<StationOptions> opt) => _dir = opt.Value.ModelsFolder;

    public (DateTime? Locator, DateTime? Enhancer) Current()
    {
        static DateTime? Stamp(string p) => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        return (Stamp(Path.Combine(_dir, "codenet.onnx")), Stamp(Path.Combine(_dir, "enhancer.onnx")));
    }

    public IReadOnlyList<CandidateReport> Reports()
    {
        string cand = Path.Combine(_dir, "candidates");
        if (!Directory.Exists(cand)) return Array.Empty<CandidateReport>();
        var list = new List<CandidateReport>();
        foreach (var f in Directory.GetFiles(cand, "report-*.json"))
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(f))!;
                int G(string a, string b) => j[a]?[b]?.GetValue<int>() ?? 0;
                string decision = j["decision"]?.GetValue<string>() ?? "";
                list.Add(new CandidateReport(
                    j["time"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(f),
                    decision, j["candidate"]?.GetValue<string>(),
                    j["hard_cases"]?.GetValue<int>() ?? 0, j["learnable"]?.GetValue<int>() ?? 0,
                    G("golden", "labelled"), G("golden", "old"), G("golden", "new"),
                    G("hard", "labelled"), G("hard", "old"), G("hard", "new"),
                    decision.StartsWith("candidate passed", StringComparison.Ordinal)));
            }
            catch (JsonException)
            {
                // a half-written report: skip it
            }
        }
        return list.OrderByDescending(r => r.Stamp, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<ModelHistoryEntry> History()
    {
        string path = Path.Combine(_dir, "history.jsonl");
        if (!File.Exists(path)) return Array.Empty<ModelHistoryEntry>();
        var list = new List<ModelHistoryEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var j = JsonNode.Parse(line)!;
            list.Add(new ModelHistoryEntry(
                DateTime.TryParse(j["time"]?.GetValue<string>(), out var t) ? t : DateTime.MinValue,
                j["promoted"]?.GetValue<string>() ?? "", j["approved_by"]?.GetValue<string>() ?? ""));
        }
        return list.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>Makes a passed candidate the line's locator model. Only passed candidates qualify.</summary>
    public void Promote(string candidateFile, string approvedBy)
    {
        if (string.IsNullOrWhiteSpace(approvedBy)) throw new ArgumentException("an approver is required");
        if (candidateFile != Path.GetFileName(candidateFile) || !candidateFile.EndsWith(".onnx", StringComparison.Ordinal))
            throw new ArgumentException("bad candidate name");
        var report = Reports().FirstOrDefault(r => r.CandidateFile == candidateFile)
                     ?? throw new InvalidOperationException("no retraining report for this candidate");
        if (!report.Passed) throw new InvalidOperationException("this candidate did not pass the no-regression test");

        string cand = Path.Combine(_dir, "candidates");
        string src = Path.Combine(cand, candidateFile);
        if (!File.Exists(src)) throw new FileNotFoundException("candidate model not found", candidateFile);
        string dst = Path.Combine(_dir, "codenet.onnx");
        if (File.Exists(dst)) File.Copy(dst, Path.Combine(cand, $"previous-{DateTime.Now:yyyyMMdd-HHmmss}.onnx"));
        File.Copy(src, dst, overwrite: true);
        string pt = Path.ChangeExtension(src, ".pt");
        if (File.Exists(pt)) File.Copy(pt, Path.Combine(_dir, "codenet.pt"), overwrite: true);
        File.AppendAllText(Path.Combine(_dir, "history.jsonl"), JsonSerializer.Serialize(new
        {
            time = DateTime.Now.ToString("s"), promoted = src, approved_by = approvedBy,
        }) + "\n");
    }
}
