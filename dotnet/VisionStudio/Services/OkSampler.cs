using StationGuard;
using VisionCore;

namespace VisionStudio.Services;

/// <summary>
/// Good pictures kept for learning (owner, 2026-11-04: "learn from good image and bad image both"): one OK part
/// in <see cref="EveryNth"/> of each camera, and OK parts that passed close to a tuned limit
/// (<see cref="AutoTune.NearLimit"/>), at most <see cref="NearLimitPerHour"/> of those per camera an hour so a
/// drifting line does not fill the disk. They go to their own image store (data/learning-ok), never into the NG
/// images or their counts. Off while <see cref="Enabled"/> is false (the Supervisor's setting). Like the NG image
/// store it never throws into the inspection and never waits for the disk; under memory or disk pressure the
/// store keeps no OK picture at all.
/// </summary>
public sealed class OkSampler(NgImageStore store, TimeProvider? time = null) : IDisposable
{
    /// <summary>One OK part in this many is kept (owner, 2026-11-04: "1 in 50 will remain same").</summary>
    public const int EveryNth = 50;
    /// <summary>Near-limit OK parts kept per camera an hour, besides the one in <see cref="EveryNth"/>.</summary>
    public const int NearLimitPerHour = 30;

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly object _lock = new();
    readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    readonly Dictionary<string, (long Hour, int Count)> _near = new(StringComparer.Ordinal);

    public NgImageStore Store => store;

    /// <summary>Whether good pictures are kept at all.</summary>
    public volatile bool Enabled = true;

    /// <summary>Whether this OK picture is to be kept, and why: counts the part either way.</summary>
    public (bool Keep, bool NearLimit) Decide(string jobCamera, bool nearLimit)
    {
        lock (_lock)
        {
            var n = _seen.GetValueOrDefault(jobCamera);
            _seen[jobCamera] = n + 1;
            if (n % EveryNth == EveryNth - 1) return (true, nearLimit);
            if (!nearLimit) return (false, false);
            var hour = _time.GetUtcNow().ToUnixTimeSeconds() / 3600;
            var (h, c) = _near.GetValueOrDefault(jobCamera);
            if (h != hour) c = 0;
            if (c >= NearLimitPerHour) return (false, true);
            _near[jobCamera] = (hour, c + 1);
            return (true, true);
        }
    }

    /// <summary>Writes the pictures still waiting and the counts (the runtime disposes it at shutdown).</summary>
    public void Dispose() => store.Dispose();

    /// <summary>Offers one camera's picture of a part that passed. True when it will be kept. Never throws.</summary>
    public bool Offer(Job job, int camera, string cameraName, GrayImage img, JobResult r, ImageName name, IReadOnlyDictionary<string, object?> meta)
    {
        if (!Enabled || !r.Pass) return false;
        try
        {
            var jobCamera = $"{job.Name}-{job.Cameras[camera].Name}";
            var (keep, near) = Decide(jobCamera, AutoTune.NearLimit(job, camera, r));
            if (!keep) return false;
            var m = new Dictionary<string, object?>(meta) { ["near_limit"] = near, ["sample"] = "learning" };
            return store.KeepImage(jobCamera, true, [], img.Pixels, img.Width, img.Height, img.Stride, null, cameraName, m, name);
        }
        catch (Exception) { return false; }
    }
}
