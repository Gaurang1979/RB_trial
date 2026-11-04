using System.Collections.Concurrent;
using StationAgent;
using StationGuard;

namespace VisionStudio.Services;

/// <summary>
/// The station agent's image source: finds a part's saved NG images by image id (<see cref="NgImageStore"/>)
/// and reduces each to a JPEG for the plant server (<see cref="JpegReducer"/>). Inspection comes first, so the
/// reduction runs on one thread of its own (below normal priority, one part at a time, never the shared pool),
/// at most <c>maxPartsPerMinute</c> parts a minute, and only when <c>busy</c> says the PC is free (not pressed,
/// no part being inspected); otherwise the answer is "not yet" and the server asks again. A reduction the agent
/// stopped waiting for is kept for its next ask instead of being done twice.
/// </summary>
public sealed class NgImageSource : IImageSource, IDisposable
{
    /// <summary>The most cameras one image id holds on the server.</summary>
    public const int MaxCameras = 8;
    const int MaxKept = 8;

    sealed record Work(NgImageLookup Found, TaskCompletionSource<ImageAnswer> Result);

    readonly NgImageStore _store;
    readonly Func<bool>? _busy;
    readonly int _perMinute;
    readonly TimeSpan _wait;
    readonly TimeProvider _time;
    readonly object _lock = new();
    readonly Dictionary<string, Task<ImageAnswer>> _jobs = new(StringComparer.Ordinal);
    readonly Queue<long> _starts = new();
    readonly BlockingCollection<Work> _work = new(1);
    readonly Thread _worker;

    /// <param name="busy">True while images must not be made (pressure, a part in inspection).</param>
    /// <param name="maxPartsPerMinute">The rate limit: parts whose images are reduced per minute.</param>
    /// <param name="wait">How long one ask waits for the reduction before answering "not yet" (the work goes on).</param>
    public NgImageSource(NgImageStore store, Func<bool>? busy = null, int maxPartsPerMinute = 20, TimeSpan? wait = null, TimeProvider? time = null)
    {
        _store = store; _busy = busy;
        _perMinute = Math.Max(1, maxPartsPerMinute);
        _wait = wait ?? TimeSpan.FromSeconds(2);
        _time = time ?? TimeProvider.System;
        _worker = new Thread(WorkLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "ng-image-reducer" };
        _worker.Start();
    }

    public async Task<ImageAnswer> GetAsync(string imageId, CancellationToken ct)
    {
        var found = _store.Find(imageId);
        if (found.State == NgImageState.Queued) return ImageAnswer.NotYet;
        if (found.State == NgImageState.Missing) return ImageAnswer.Missing;
        Task<ImageAnswer> job;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(imageId, out job!))
            {
                if (_busy?.Invoke() == true || RateLimited()) return ImageAnswer.NotYet;
                if (_jobs.Count >= MaxKept) foreach (var k in _jobs.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList()) _jobs.Remove(k);
                var tcs = new TaskCompletionSource<ImageAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_jobs.Count >= MaxKept || !_work.TryAdd(new Work(found, tcs))) return ImageAnswer.NotYet;   // the worker has one waiting already
                _starts.Enqueue(_time.GetTimestamp());
                _jobs[imageId] = job = tcs.Task;
            }
        }
        try
        {
            var answer = await job.WaitAsync(_wait, _time, ct);
            lock (_lock) _jobs.Remove(imageId);
            return answer;
        }
        catch (TimeoutException) { return ImageAnswer.NotYet; }   // still being made: the next ask gets it
    }

    // Under _lock.
    bool RateLimited()
    {
        while (_starts.TryPeek(out var t) && _time.GetElapsedTime(t) >= TimeSpan.FromMinutes(1)) _starts.Dequeue();
        return _starts.Count >= _perMinute;
    }

    void WorkLoop()
    {
        foreach (var w in _work.GetConsumingEnumerable())
        {
            try { w.Result.TrySetResult(Reduce(w.Found)); }
            // Out of memory, a failed allocation, a locked file: nothing is wrong with the image, so ask again later.
            catch (Exception) { w.Result.TrySetResult(ImageAnswer.NotYet); }
        }
    }

    static ImageAnswer Reduce(NgImageLookup found)
    {
        var images = new List<CameraImage>();
        foreach (var f in found.Files.Take(MaxCameras))
        {
            try { images.Add(new CameraImage(f.Camera, JpegReducer.ReduceBmp(f.Path))); }
            // Removed by the folder limit since the lookup, or damaged: that camera's image is gone. Any other
            // failure goes up, and the part is answered "not yet".
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or InvalidDataException) { }
        }
        return ImageAnswer.Found(images);
    }

    public void Dispose() => _work.CompleteAdding();
}
