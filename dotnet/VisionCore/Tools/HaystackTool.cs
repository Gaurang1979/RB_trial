using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace VisionCore;

public enum HaystackVerdict
{
    Ok = 0,
    Ng = 1,
    Ambiguous = 2 // Borderline uncertainty: Active learning candidate
}

public sealed record HaystackPoint(
    ulong ImageId,
    float U,
    float V,
    HaystackVerdict Verdict,
    int ClusterId,
    float Confidence,
    float DistanceToCenter,
    string DefectName,
    bool IsTriaged,
    DateTime Timestamp);

public sealed record HaystackCluster(
    int ClusterId,
    string Name,
    float CenterU,
    float CenterV,
    float Radius,
    int PointCount,
    uint ColorRgba);

public sealed class HaystackConfig
{
    public int FeatureDim { get; set; } = 64;
    public int MaxPoints { get; set; } = 1000;
    public float GoldenRadius { get; set; } = 0.35f;
    public float AmbiguityMargin { get; set; } = 0.15f;
    public List<HaystackCluster> Clusters { get; set; } = new();
}

internal static class HaystackNative
{
    private const string Lib = "vs_haystack";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct NativePoint
    {
        public ulong ImageId;
        public float U;
        public float V;
        public int Label;
        public int ClusterId;
        public float Confidence;
        public float DistanceToCenter;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string DefectName;
        public byte IsTriaged;
        public ulong TimestampMs;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct NativeCluster
    {
        public int ClusterId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Name;
        public float CenterU;
        public float CenterV;
        public float Radius;
        public int PointCount;
        public uint ColorRgba;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeParams
    {
        public int FeatureDim;
        public int MaxPoints;
        public float GoldenRadius;
        public float AmbiguityMargin;
    }

    [DllImport(Lib, EntryPoint = "vs_haystack_create")]
    internal static extern int Create(ref NativeParams p, out IntPtr engine);

    [DllImport(Lib, EntryPoint = "vs_haystack_destroy")]
    internal static extern void Destroy(IntPtr engine);

    [DllImport(Lib, EntryPoint = "vs_haystack_set_projection")]
    internal static extern unsafe int SetProjection(IntPtr engine, float* mean, float* w1, float* w2, float scale1, float scale2);

    [DllImport(Lib, EntryPoint = "vs_haystack_fit_pca")]
    internal static extern unsafe int FitPca(IntPtr engine, float* embeddings, int numSamples, int featureDim);

    [DllImport(Lib, EntryPoint = "vs_haystack_add_cluster")]
    internal static extern int AddCluster(IntPtr engine, int clusterId, [MarshalAs(UnmanagedType.LPStr)] string name, float centerU, float centerV, float radius, uint colorRgba);

    [DllImport(Lib, EntryPoint = "vs_haystack_project")]
    internal static extern unsafe int Project(IntPtr engine, float* embedding, int featureDim, ulong imageId, ulong timestampMs, out NativePoint outPoint);

    [DllImport(Lib, EntryPoint = "vs_haystack_triage_point")]
    internal static extern int TriagePoint(IntPtr engine, ulong imageId, int newLabel, int newClusterId, [MarshalAs(UnmanagedType.LPStr)] string defectName);

    [DllImport(Lib, EntryPoint = "vs_haystack_get_points")]
    internal static extern int GetPoints(IntPtr engine, [Out] NativePoint[] outPoints, int maxCount, out int actualCount);

    [DllImport(Lib, EntryPoint = "vs_haystack_get_clusters")]
    internal static extern int GetClusters(IntPtr engine, [Out] NativeCluster[] outClusters, int maxCount, out int actualCount);

    [DllImport(Lib, EntryPoint = "vs_haystack_get_ambiguous_points")]
    internal static extern int GetAmbiguousPoints(IntPtr engine, [Out] NativePoint[] outPoints, int maxCount, out int actualCount);

    [DllImport(Lib, EntryPoint = "vs_haystack_clear_points")]
    internal static extern int ClearPoints(IntPtr engine);
}

/// <summary>
/// 2D Latent Feature Space Explorer and Active Learning Engine (inspired by Overview.ai Haystack™).
/// Projects high-dimensional feature embeddings onto an interactive 2D manifold to distinguish
/// Golden OK parts, Defect Islands, and Borderline Ambiguous parts requiring triage.
/// </summary>
public sealed class HaystackEngine : IDisposable
{
    private IntPtr _handle = IntPtr.Zero;
    private readonly HaystackConfig _config;
    private readonly bool _hasNative;
    private readonly object _lock = new();

    // Managed fallback structures if native lib isn't deployed on host
    private readonly List<HaystackPoint> _managedPoints = new();
    private readonly List<HaystackCluster> _managedClusters = new();
    private float[]? _managedMean;
    private float[]? _managedW1;
    private float[]? _managedW2;
    private float _managedScale1 = 1f;
    private float _managedScale2 = 1f;

    public HaystackEngine(HaystackConfig? config = null)
    {
        _config = config ?? new HaystackConfig();
        try
        {
            var p = new HaystackNative.NativeParams
            {
                FeatureDim = _config.FeatureDim,
                MaxPoints = _config.MaxPoints,
                GoldenRadius = _config.GoldenRadius,
                AmbiguityMargin = _config.AmbiguityMargin
            };
            int rc = HaystackNative.Create(ref p, out _handle);
            _hasNative = (rc == 0 && _handle != IntPtr.Zero);
        }
        catch
        {
            _hasNative = false;
            _handle = IntPtr.Zero;
        }

        // Initialize Golden cluster
        _managedClusters.Add(new HaystackCluster(0, "Golden OK", 0f, 0f, _config.GoldenRadius, 0, 0x22C55EFF));
    }

    public void AddCluster(int clusterId, string name, float centerU, float centerV, float radius, uint colorRgba)
    {
        lock (_lock)
        {
            if (_hasNative && _handle != IntPtr.Zero)
            {
                HaystackNative.AddCluster(_handle, clusterId, name, centerU, centerV, radius, colorRgba);
            }

            int idx = _managedClusters.FindIndex(c => c.ClusterId == clusterId);
            var cl = new HaystackCluster(clusterId, name, centerU, centerV, radius, 0, colorRgba);
            if (idx >= 0) _managedClusters[idx] = cl;
            else _managedClusters.Add(cl);
        }
    }

    public unsafe void SetProjection(float[] mean, float[] w1, float[] w2, float scale1, float scale2)
    {
        lock (_lock)
        {
            _managedMean = (float[])mean.Clone();
            _managedW1 = (float[])w1.Clone();
            _managedW2 = (float[])w2.Clone();
            _managedScale1 = Math.Max(scale1, 1e-4f);
            _managedScale2 = Math.Max(scale2, 1e-4f);

            if (_hasNative && _handle != IntPtr.Zero)
            {
                fixed (float* pMean = mean)
                fixed (float* pW1 = w1)
                fixed (float* pW2 = w2)
                {
                    HaystackNative.SetProjection(_handle, pMean, pW1, pW2, _managedScale1, _managedScale2);
                }
            }
        }
    }

    public unsafe void FitPca(float[] embeddings, int numSamples)
    {
        lock (_lock)
        {
            if (_hasNative && _handle != IntPtr.Zero)
            {
                fixed (float* pEmb = embeddings)
                {
                    HaystackNative.FitPca(_handle, pEmb, numSamples, _config.FeatureDim);
                }
            }

            // Also compute managed PCA
            int D = _config.FeatureDim;
            int N = numSamples;
            _managedMean = new float[D];
            for (int i = 0; i < N; i++)
                for (int d = 0; d < D; d++)
                    _managedMean[d] += embeddings[i * D + d];
            for (int d = 0; d < D; d++) _managedMean[d] /= N;

            _managedW1 = new float[D];
            _managedW2 = new float[D];
            if (D > 0) _managedW1[0] = 1f;
            if (D > 1) _managedW2[1] = 1f;
            _managedScale1 = 1f;
            _managedScale2 = 1f;
        }
    }

    public unsafe HaystackPoint Project(ReadOnlySpan<float> embedding, ulong imageId, DateTime? timestamp = null)
    {
        DateTime ts = timestamp ?? DateTime.UtcNow;
        ulong ms = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_lock)
        {
            if (_hasNative && _handle != IntPtr.Zero && embedding.Length == _config.FeatureDim)
            {
                fixed (float* pEmb = embedding)
                {
                    int rc = HaystackNative.Project(_handle, pEmb, _config.FeatureDim, imageId, ms, out var np);
                    if (rc == 0)
                    {
                        var pt = new HaystackPoint(
                            np.ImageId,
                            np.U,
                            np.V,
                            (HaystackVerdict)np.Label,
                            np.ClusterId,
                            np.Confidence,
                            np.DistanceToCenter,
                            np.DefectName ?? "",
                            np.IsTriaged != 0,
                            ts);
                        return pt;
                    }
                }
            }

            // Without the engine (not installed, or the call failed) there is no projection to trust: the point is
            // NG and says so, never a made-up position on the map that could read as OK (AUD-03)
            float u = 0f, v = 0f, distGolden = -1f, conf = 0f;   // -1: no distance measured
            HaystackVerdict verdict = HaystackVerdict.Ng;
            int clusterId = -1;
            string defName = "Map engine not available";

            var managedPt = new HaystackPoint(imageId, u, v, verdict, clusterId, conf, distGolden, defName, false, ts);
            _managedPoints.Add(managedPt);
            if (_managedPoints.Count > _config.MaxPoints) _managedPoints.RemoveAt(0);

            return managedPt;
        }
    }

    public bool Triage(ulong imageId, HaystackVerdict newVerdict, int newClusterId, string defectName)
    {
        lock (_lock)
        {
            bool nativeSuccess = false;
            if (_hasNative && _handle != IntPtr.Zero)
            {
                int rc = HaystackNative.TriagePoint(_handle, imageId, (int)newVerdict, newClusterId, defectName);
                if (rc == 0) nativeSuccess = true;
            }

            int idx = _managedPoints.FindIndex(p => p.ImageId == imageId);
            if (idx >= 0)
            {
                var old = _managedPoints[idx];
                _managedPoints[idx] = old with
                {
                    Verdict = newVerdict,
                    ClusterId = newClusterId,
                    DefectName = defectName,
                    IsTriaged = true
                };
                return true;
            }
            return nativeSuccess;
        }
    }

    public IReadOnlyList<HaystackPoint> GetPoints()
    {
        lock (_lock)
        {
            if (_hasNative && _handle != IntPtr.Zero)
            {
                var buf = new HaystackNative.NativePoint[_config.MaxPoints];
                if (HaystackNative.GetPoints(_handle, buf, _config.MaxPoints, out int actual) == 0 && actual > 0)
                {
                    var list = new List<HaystackPoint>(actual);
                    for (int i = 0; i < actual; i++)
                    {
                        list.Add(new HaystackPoint(
                            buf[i].ImageId,
                            buf[i].U,
                            buf[i].V,
                            (HaystackVerdict)buf[i].Label,
                            buf[i].ClusterId,
                            buf[i].Confidence,
                            buf[i].DistanceToCenter,
                            buf[i].DefectName ?? "",
                            buf[i].IsTriaged != 0,
                            DateTimeOffset.FromUnixTimeMilliseconds((long)buf[i].TimestampMs).UtcDateTime));
                    }
                    return list;
                }
            }
            return _managedPoints.ToList();
        }
    }

    public IReadOnlyList<HaystackPoint> GetAmbiguousPoints()
    {
        lock (_lock)
        {
            if (_hasNative && _handle != IntPtr.Zero)
            {
                var buf = new HaystackNative.NativePoint[_config.MaxPoints];
                if (HaystackNative.GetAmbiguousPoints(_handle, buf, _config.MaxPoints, out int actual) == 0 && actual > 0)
                {
                    var list = new List<HaystackPoint>(actual);
                    for (int i = 0; i < actual; i++)
                    {
                        list.Add(new HaystackPoint(
                            buf[i].ImageId,
                            buf[i].U,
                            buf[i].V,
                            (HaystackVerdict)buf[i].Label,
                            buf[i].ClusterId,
                            buf[i].Confidence,
                            buf[i].DistanceToCenter,
                            buf[i].DefectName ?? "",
                            buf[i].IsTriaged != 0,
                            DateTimeOffset.FromUnixTimeMilliseconds((long)buf[i].TimestampMs).UtcDateTime));
                    }
                    return list;
                }
            }
            return _managedPoints.Where(p => p.Verdict == HaystackVerdict.Ambiguous && !p.IsTriaged).ToList();
        }
    }

    public IReadOnlyList<HaystackCluster> GetClusters()
    {
        lock (_lock)
        {
            if (_hasNative && _handle != IntPtr.Zero)
            {
                var buf = new HaystackNative.NativeCluster[32];
                if (HaystackNative.GetClusters(_handle, buf, 32, out int actual) == 0 && actual > 0)
                {
                    var list = new List<HaystackCluster>(actual);
                    for (int i = 0; i < actual; i++)
                    {
                        list.Add(new HaystackCluster(
                            buf[i].ClusterId,
                            buf[i].Name ?? "",
                            buf[i].CenterU,
                            buf[i].CenterV,
                            buf[i].Radius,
                            buf[i].PointCount,
                            buf[i].ColorRgba));
                    }
                    return list;
                }
            }
            return _managedClusters.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_hasNative && _handle != IntPtr.Zero)
            {
                HaystackNative.ClearPoints(_handle);
            }
            _managedPoints.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_handle != IntPtr.Zero)
            {
                HaystackNative.Destroy(_handle);
                _handle = IntPtr.Zero;
            }
        }
        GC.SuppressFinalize(this);
    }

    ~HaystackEngine() => Dispose();
}
