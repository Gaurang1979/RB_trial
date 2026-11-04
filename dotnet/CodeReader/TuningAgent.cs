using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BarcodeReading;

/// <summary>
/// Fast-loop tuner: learns, per product, which reader settings read the most codes in the least
/// time, while the line runs. Each setting is an independent Thompson-sampling bandit over a
/// small list of allowed values. Most frames use the best known values; a few explore.
/// A Locked product is never explored: the agent then only reports.
/// </summary>
public sealed class TuningAgent
{
    /// <summary>One tunable setting and the values the dashboard allows for it.</summary>
    public sealed record Knob(string Name, IReadOnlyList<float> Values, Action<ReaderSettings, float> Apply);

    /// <summary>Default knobs and ranges. The dashboard can narrow or widen each list.</summary>
    public static IReadOnlyList<Knob> DefaultKnobs { get; } = new[]
    {
        new Knob("Gamma", new[] { 0.35f, 0.5f, 0.7f, 1.0f }, (s, v) => s.Gamma = v),
        new Knob("LocatorDensity", new[] { 0.08f, 0.12f, 0.18f }, (s, v) => s.LocatorDensity = v),
        new Knob("AiThreshold", new[] { 0.25f, 0.4f, 0.55f }, (s, v) => s.AiThreshold = v),
        new Knob("MaxUpscale", new[] { 2f, 3f }, (s, v) => s.MaxUpscale = (int)v),
        new Knob("Enhancer", new[] { (float)EnhancerMode.Last, (float)EnhancerMode.Early, (float)EnhancerMode.SecondPass },
            (s, v) => s.Enhancer = (EnhancerMode)(int)v),
    };

    // Beta posterior per (knob, value) over "frame reward" in 0..1
    private sealed class Arm
    {
        public double Alpha { get; set; } = 1;
        public double Beta { get; set; } = 1;
        public int Pulls { get; set; }
        public double Mean => Alpha / (Alpha + Beta);
    }

    private readonly IReadOnlyList<Knob> _knobs;
    private readonly Dictionary<string, Arm[]> _arms;
    private readonly Random _rng;
    private int[] _lastChoice = Array.Empty<int>();

    /// <summary>Share of frames that explore (the rest use the best values so far).</summary>
    public double ExploreRate { get; set; } = 0.1;
    /// <summary>Weight of time in the reward: reward = read share - TimeWeight x (time / budget) / 2.</summary>
    public double TimeWeight { get; set; } = 0.2;
    public int Frames { get; private set; }

    public TuningAgent(IReadOnlyList<Knob>? knobs = null, int seed = 0)
    {
        _knobs = knobs ?? DefaultKnobs;
        _arms = _knobs.ToDictionary(k => k.Name, k => k.Values.Select(_ => new Arm()).ToArray());
        _rng = seed == 0 ? new Random() : new Random(seed);
    }

    /// <summary>
    /// Settings for the next frame. Locked products get their stored settings unchanged.
    /// Call <see cref="Observe"/> with the result of the frame decoded with these settings.
    /// </summary>
    public ReaderSettings Next(ProductMaster product)
    {
        var s = product.ChildReaderSettings();
        if (product.Mode == LearningMode.Locked)
        {
            _lastChoice = Array.Empty<int>();
            return s;
        }
        bool explore = _rng.NextDouble() < ExploreRate;
        _lastChoice = new int[_knobs.Count];
        for (int k = 0; k < _knobs.Count; k++)
        {
            if (!product.Reader.AutoKnobs.Contains(_knobs[k].Name))
            {
                _lastChoice[k] = -1; // Manual: keep the product's value, learn nothing
                continue;
            }
            var arms = _arms[_knobs[k].Name];
            int pick = explore ? ArgMax(arms, a => SampleBeta(a.Alpha, a.Beta)) : ArgMax(arms, a => a.Mean);
            _lastChoice[k] = pick;
            _knobs[k].Apply(s, _knobs[k].Values[pick]);
        }
        return s;
    }

    /// <summary>Feeds back one frame's outcome. Ignored for Locked products.</summary>
    public void Observe(DecodeStats stats, int timeBudgetMs)
    {
        if (_lastChoice.Length == 0) return;
        double reward = Reward(stats, timeBudgetMs, TimeWeight);
        for (int k = 0; k < _knobs.Count; k++)
        {
            if (_lastChoice[k] < 0) continue;
            var arm = _arms[_knobs[k].Name][_lastChoice[k]];
            arm.Alpha += reward;
            arm.Beta += 1 - reward;
            arm.Pulls++;
        }
        Frames++;
    }

    /// <summary>
    /// Frame reward in 0..1: share of codes read, minus a small time penalty (half the time weight
    /// at the full budget). No bonus for speed: with a generous budget a bonus pushed every frame
    /// to the top of the range, and a missed code no longer counted against a setting.
    /// </summary>
    public static double Reward(DecodeStats s, int timeBudgetMs, double timeWeight)
    {
        double read = s.Expected > 0 ? Math.Min(1.0, (double)s.Codes / s.Expected) : (s.Codes > 0 ? 1 : 0);
        double time = Math.Min(2.0, s.TotalMs / Math.Max(1, timeBudgetMs));
        return Math.Clamp(read - timeWeight * time / 2, 0, 1);
    }

    /// <summary>Best value per knob so far, with its mean reward and number of frames.</summary>
    public IReadOnlyList<(string Knob, float Value, double MeanReward, int Frames)> Best()
    {
        var list = new List<(string, float, double, int)>();
        foreach (var k in _knobs)
        {
            var arms = _arms[k.Name];
            int i = ArgMax(arms, a => a.Mean);
            list.Add((k.Name, k.Values[i], arms[i].Mean, arms[i].Pulls));
        }
        return list;
    }

    /// <summary>
    /// Copies the learned best values into the product's reader tuning, as a proposal to save
    /// (a new product master version; a Locked product needs approval to change).
    /// </summary>
    public ProductMaster Propose(ProductMaster product, int minFrames = 50)
    {
        var p = JsonSerializer.Deserialize<ProductMaster>(JsonSerializer.Serialize(product))!;
        if (Frames < minFrames) return p;
        // start from the product's own values so Manual settings stay as they are
        var s = p.ChildReaderSettings();
        foreach (var (knob, value, _, pulls) in Best())
            if (pulls > 0 && p.Reader.AutoKnobs.Contains(knob))
                _knobs.First(k => k.Name == knob).Apply(s, value);
        p.Reader.Gamma = s.Gamma;
        p.Reader.LocatorDensity = s.LocatorDensity;
        p.Reader.AiThreshold = s.AiThreshold;
        p.Reader.MaxUpscale = s.MaxUpscale;
        p.Reader.Enhancer = s.Enhancer;
        return p;
    }

    /// <summary>Saves the learned statistics, so learning survives a restart.</summary>
    public void Save(string path)
    {
        var state = new Dictionary<string, object>
        {
            ["frames"] = Frames,
            ["arms"] = _arms.ToDictionary(kv => kv.Key, kv => kv.Value.Select(a => new[] { a.Alpha, a.Beta, a.Pulls }).ToArray()),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(state));
    }

    public void Load(string path)
    {
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Frames = doc.RootElement.GetProperty("frames").GetInt32();
        foreach (var knob in doc.RootElement.GetProperty("arms").EnumerateObject())
        {
            if (!_arms.TryGetValue(knob.Name, out var arms)) continue;
            int i = 0;
            foreach (var a in knob.Value.EnumerateArray())
            {
                if (i >= arms.Length) break;
                var v = a.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                arms[i++] = new Arm { Alpha = v[0], Beta = v[1], Pulls = (int)v[2] };
            }
        }
    }

    private static int ArgMax(Arm[] arms, Func<Arm, double> score)
    {
        int best = 0;
        double bestScore = double.MinValue;
        for (int i = 0; i < arms.Length; i++)
        {
            double sc = score(arms[i]);
            if (sc > bestScore) { bestScore = sc; best = i; }
        }
        return best;
    }

    // Beta(a, b) sample via two Gamma samples (Marsaglia-Tsang)
    private double SampleBeta(double a, double b)
    {
        double x = SampleGamma(a), y = SampleGamma(b);
        return x / (x + y);
    }

    private double SampleGamma(double shape)
    {
        if (shape < 1) return SampleGamma(shape + 1) * Math.Pow(_rng.NextDouble(), 1 / shape);
        double d = shape - 1.0 / 3, c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double x, v;
            do
            {
                x = Normal();
                v = 1 + c * x;
            } while (v <= 0);
            v = v * v * v;
            double u = _rng.NextDouble();
            if (u < 1 - 0.0331 * x * x * x * x || Math.Log(u) < 0.5 * x * x + d * (1 - v + Math.Log(v))) return d * v;
        }
    }

    private double Normal()
    {
        double u1 = 1 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
