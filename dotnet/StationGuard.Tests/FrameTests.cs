using StationGuard;
using Xunit;

/// <summary>
/// A free-running or hardware-triggered camera: frames come on its own thread at its own pace,
/// whether or not the inspection has finished the last one. Each frame's pixels are its number.
/// </summary>
sealed class FakeCamera(FrameIntake intake, int width = 64, int height = 48)
{
    public int Sent;

    public void Fire(ulong frameNumber)
    {
        Sent++;
        intake.Arrived(frameNumber, width, height, px => px.Fill((byte)frameNumber));
    }

    public Thread Run(int frames, TimeSpan every) => Start(() =>
    {
        for (ulong n = 1; n <= (ulong)frames; n++) { Fire(n); Thread.Sleep(every); }
    });

    static Thread Start(Action a) { var t = new Thread(() => a()) { IsBackground = true }; t.Start(); return t; }
}

public class FrameTests
{
    static readonly TimeSpan NoWait = TimeSpan.Zero;

    [Fact]
    public void WithEveryBufferInUseTheFrameIsDroppedAndItsPartIsMissed()
    {
        using var intake = new FrameIntake(buffers: 3);
        var cam = new FakeCamera(intake);
        var missed = new List<(ulong, string)>();
        intake.PartMissed += (n, why) => missed.Add((n, why));
        for (ulong n = 1; n <= 3; n++) cam.Fire(n);
        var inspecting = intake.TryTake(NoWait)!;      // frame 1 being inspected; 2 and 3 waiting
        cam.Fire(4);                                   // no buffer free: dropped, never written over 1
        Assert.Equal(1UL, inspecting.FrameNumber);
        Assert.All(inspecting.Pixels.ToArray(), b => Assert.Equal(1, b));
        Assert.Equal([(4UL, "no_free_buffer")], missed);
        inspecting.Dispose();                          // done with 1: its buffer is free again
        cam.Fire(5);
        var order = new List<CapturedFrame>();
        while (intake.TryTake(NoWait) is { } f) order.Add(f);
        Assert.Equal([2UL, 3, 4, 5], order.Select(f => f.FrameNumber));
        Assert.True(order[2].Missed);                  // part 4 is handed on as missed (NG), in its place
        Assert.Equal(0, order[2].Pixels.Length);
        Assert.All(order[3].Pixels.ToArray(), b => Assert.Equal(5, b));
        Assert.Equal(4, intake.Received);
        Assert.Equal(1, intake.Missed);
        Assert.Equal(3, intake.Pool.InUse);
        foreach (var f in order) f.Dispose();
        Assert.Equal(0, intake.Pool.InUse);
    }

    [Fact]
    public void FramesTheCameraNumberedButNeverDeliveredAreMissedInOrder()
    {
        using var intake = new FrameIntake();
        var cam = new FakeCamera(intake);
        cam.Fire(7);
        cam.Fire(10);
        var order = new List<CapturedFrame>();
        while (intake.TryTake(NoWait) is { } f) { order.Add(f); f.Dispose(); }
        Assert.Equal([7UL, 8, 9, 10], order.Select(f => f.FrameNumber));
        Assert.Equal(["frame_lost", "frame_lost"], order.Where(f => f.Missed).Select(f => f.MissedReason!));
    }

    [Fact]
    public void ACameraThatCountsAfreshIsNotAGap()
    {
        var gap = new FrameGap();
        Assert.Equal(0, gap.Next(65534));
        Assert.Equal(0, gap.Next(65535));
        Assert.Equal(0, gap.Next(1));   // the count wrapped or the camera restarted
        Assert.Equal(1, gap.Next(3));
        gap.Reset();
        Assert.Equal(0, gap.Next(100));
    }

    [Fact]
    public void ABufferIsNeverOverwrittenWhileInspectedWhenFramesComeFasterThanInspection()
    {
        // Frames every 1 ms, inspection 20 ms: inspection falls far behind.
        using var intake = new FrameIntake(buffers: 3);
        var cam = new FakeCamera(intake, 512, 512);
        const int frames = 150;
        var camThread = cam.Run(frames, TimeSpan.FromMilliseconds(1));
        var seen = new List<ulong>();
        int inspected = 0, missed = 0;
        while (seen.Count < frames && intake.TryTake(TimeSpan.FromSeconds(5)) is { } f)
        {
            using (f)
            {
                seen.Add(f.FrameNumber);
                if (f.Missed) { missed++; continue; }
                inspected++;
                Assert.True(intake.Pool.InUse <= 3);
                var want = (byte)f.FrameNumber;
                Assert.Equal(want, f.Pixels[0]);
                Thread.Sleep(20);                                       // inspecting
                Assert.All(f.Pixels.ToArray(), b => Assert.Equal(want, b)); // still this frame's pixels
            }
        }
        camThread.Join();
        // Every part is accounted for once, in order: inspected or missed (NG), never lost.
        Assert.Equal(Enumerable.Range(1, frames).Select(i => (ulong)i), seen);
        Assert.True(missed > 0, "inspection was slower, so some parts must be missed");
        Assert.Equal(intake.Missed, missed);
        Assert.Equal(intake.Received, inspected);
        Assert.Equal(frames, inspected + missed);
        Assert.Equal(0, intake.Pool.InUse);
    }

    [Fact]
    public void WhenInspectionKeepsUpNoPartIsMissed()
    {
        // At most three frames waiting or in inspection at any time: three buffers are enough.
        using var intake = new FrameIntake(buffers: 3);
        var cam = new FakeCamera(intake);
        var count = 0;
        for (ulong n = 1; n <= 30; n++)
        {
            cam.Fire(n);
            if (n % 3 != 0) continue;
            while (intake.TryTake(NoWait) is { } f) using (f) { Assert.False(f.Missed); count++; }
        }
        Assert.Equal(30, count);
        Assert.Equal(0, intake.Missed);
    }

    [Fact]
    public void ALargerFrameGetsALargerBuffer()
    {
        var pool = new FramePool(1);
        var a = pool.TryRent(10)!;
        Assert.Null(pool.TryRent(10));
        using var intake = new FrameIntake(1);
        new FakeCamera(intake, 4, 4).Fire(1);
        intake.TryTake(NoWait)!.Dispose();
        new FakeCamera(intake, 8, 8).Fire(2);
        using var f = intake.TryTake(NoWait)!;
        Assert.Equal(64, f.Pixels.Length);
        Assert.True(f.Buffer!.Length >= 64);
        Assert.NotNull(a);
    }
}
