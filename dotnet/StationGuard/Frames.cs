namespace StationGuard;

/// <summary>
/// Finds frames the camera took but the PC never received, from the camera's own frame
/// numbers: a jump from 7 to 10 means frames 8 and 9 were lost (the camera's or the driver's
/// buffers were full). Each lost frame is a part that was not inspected.
/// </summary>
public sealed class FrameGap
{
    ulong? _last;

    /// <summary>Frames lost before this one. A number that does not go up (the camera restarted
    /// its count, or gives none) starts counting afresh and reports none.</summary>
    public int Next(ulong frameNumber)
    {
        var last = _last;
        _last = frameNumber;
        if (last is not { } l || frameNumber <= l) return 0;
        return (int)Math.Min(frameNumber - l - 1, int.MaxValue);
    }

    /// <summary>Forget the last number (call when grabbing starts again).</summary>
    public void Reset() => _last = null;
}

/// <summary>
/// A camera frame copied into a pooled buffer, or a part whose frame was missed. Dispose it
/// when the inspection is done: that hands the buffer back for a later frame.
/// </summary>
public sealed class CapturedFrame : IDisposable
{
    readonly FramePool? _pool;
    byte[]? _pixels;

    internal CapturedFrame(FramePool pool, byte[] pixels, int width, int height, ulong frameNumber, DateTimeOffset at)
    {
        _pool = pool;
        _pixels = pixels;
        Width = width;
        Height = height;
        FrameNumber = frameNumber;
        At = at;
    }

    internal CapturedFrame(ulong frameNumber, DateTimeOffset at, string reason)
    {
        FrameNumber = frameNumber;
        At = at;
        MissedReason = reason;
    }

    /// <summary>Width × Height bytes of 8-bit grey; empty for a missed part.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels is { } p ? p.AsSpan(0, Width * Height) :
        MissedReason is null ? throw new ObjectDisposedException(nameof(CapturedFrame)) : default;
    /// <summary>The pooled buffer itself (may be longer than Width × Height), for code that needs an array.</summary>
    public byte[]? Buffer => _pixels;
    public int Width { get; }
    public int Height { get; }
    public ulong FrameNumber { get; }
    public DateTimeOffset At { get; }

    /// <summary>
    /// Why this part has no frame ("no_free_buffer": inspection was behind and every buffer was
    /// in use; "frame_lost": the camera's frame never reached the PC). A missed part is NG.
    /// </summary>
    public string? MissedReason { get; }
    public bool Missed => MissedReason is not null;

    public void Dispose()
    {
        var p = Interlocked.Exchange(ref _pixels, null);
        if (p is not null) _pool!.Return(p);
    }
}

/// <summary>
/// A fixed set of reused frame buffers (a 20 MP frame is 20 MB on the large-object heap; a new
/// one per frame forces full collections). A buffer is handed out only when it is free: a
/// buffer still being inspected is never written to. When none is free the caller drops the
/// frame; it never waits on the camera thread and never takes more memory.
/// </summary>
public sealed class FramePool
{
    readonly object _lock = new();
    readonly Stack<byte[]> _free = new();
    int _created;

    public FramePool(int buffers = 3)
    {
        if (buffers < 1) throw new ArgumentOutOfRangeException(nameof(buffers));
        Buffers = buffers;
    }

    public int Buffers { get; }
    /// <summary>Buffers handed out and not yet returned.</summary>
    public int InUse { get { lock (_lock) return _created - _free.Count; } }

    /// <summary>A free buffer of at least <paramref name="size"/> bytes, or null when all are in use.</summary>
    public byte[]? TryRent(int size)
    {
        lock (_lock)
        {
            if (_free.TryPop(out var b)) return b.Length >= size ? b : new byte[size]; // a larger image: replace it
            if (_created >= Buffers) return null;
            _created++;
            return new byte[size];
        }
    }

    internal void Return(byte[] b)
    {
        lock (_lock) _free.Push(b);
    }
}

/// <summary>
/// Between a free-running or hardware-triggered camera and the inspection. The camera thread
/// calls <see cref="Arrived"/> for every frame; the inspection takes frames with
/// <see cref="TryTake"/> in the order the parts came. When inspection falls behind and every
/// buffer is in use, the frame is dropped and its part is handed on as missed, so the part is
/// NG and the line is told; a buffer being inspected is never overwritten. Frames the camera
/// numbered but never delivered are handed on as missed too.
/// </summary>
public sealed class FrameIntake : IDisposable
{
    readonly FramePool _pool;
    readonly TimeProvider _time;
    readonly FrameGap _gap = new();
    readonly Queue<CapturedFrame> _queue = new();
    readonly object _lock = new();
    readonly SemaphoreSlim _ready = new(0);
    long _received, _missed;

    public FrameIntake(int buffers = 3, TimeProvider? time = null)
    {
        _pool = new FramePool(buffers);
        _time = time ?? TimeProvider.System;
    }

    public FramePool Pool => _pool;
    /// <summary>Frames copied for inspection.</summary>
    public long Received => Interlocked.Read(ref _received);
    /// <summary>Parts missed (no free buffer, or the frame never reached the PC); each one is NG.</summary>
    public long Missed => Interlocked.Read(ref _missed);

    /// <summary>Raised on the camera thread for each missed part: frame number and reason.</summary>
    public event Action<ulong, string>? PartMissed;

    /// <summary>
    /// A frame from the camera (on the camera thread). <paramref name="copy"/> fills the buffer
    /// (Width × Height bytes) from the driver's memory; it is not called when the frame is dropped.
    /// Returns false when the frame was dropped.
    /// </summary>
    public bool Arrived(ulong frameNumber, int width, int height, Action<Span<byte>> copy)
    {
        var at = _time.GetUtcNow();
        var lost = _gap.Next(frameNumber);
        for (var i = lost; i > 0; i--) Miss(frameNumber - (ulong)i, at, "frame_lost");
        var size = checked(width * height);
        var buf = _pool.TryRent(size);
        if (buf is null)
        {
            Miss(frameNumber, at, "no_free_buffer");
            return false;
        }
        CapturedFrame f;
        try
        {
            copy(buf.AsSpan(0, size));
            f = new CapturedFrame(_pool, buf, width, height, frameNumber, at);
        }
        catch
        {
            _pool.Return(buf);
            Miss(frameNumber, at, "copy_failed");
            throw;
        }
        Interlocked.Increment(ref _received);
        Enqueue(f);
        return true;
    }

    /// <summary>Grabbing starts again: frame numbers count afresh.</summary>
    public void Restart() => _gap.Reset();

    /// <summary>The next part in order (a frame, or a missed part), or null when none came in time.</summary>
    public CapturedFrame? TryTake(TimeSpan timeout)
    {
        if (!_ready.Wait(timeout)) return null;
        lock (_lock) return _queue.Dequeue();
    }

    void Miss(ulong frameNumber, DateTimeOffset at, string reason)
    {
        Interlocked.Increment(ref _missed);
        Enqueue(new CapturedFrame(frameNumber, at, reason));
        PartMissed?.Invoke(frameNumber, reason);
    }

    void Enqueue(CapturedFrame f)
    {
        lock (_lock) _queue.Enqueue(f);
        _ready.Release();
    }

    public void Dispose()
    {
        lock (_lock)
            while (_queue.TryDequeue(out var f)) f.Dispose();
        _ready.Dispose();
    }
}
