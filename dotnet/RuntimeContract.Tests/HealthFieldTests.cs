using Google.Protobuf;
using VisionStudio.Remote.V1;
using Xunit;

/// <summary>Status.health (Hybrid 1) is an added field: older clients and the stub are unaffected.</summary>
public sealed class HealthFieldTests
{
    [Fact]
    public void Status_without_health_reads_as_before_and_with_health_round_trips()
    {
        var old = new Status { State = Status.Types.State.Busy, ActiveJob = "Labels", Inspections = 3 };
        var back = Status.Parser.ParseFrom(old.ToByteArray());
        Assert.Null(back.Health);
        Assert.Equal("Labels", back.ActiveJob);

        var s = old.Clone();
        s.Health = new PcHealth { CpuPct = 42, Pressure = PcHealth.Types.Pressure.High, FramesMissed = 2, Limits = new GuardLimits { HighPct = 85 } };
        var bytes = s.ToByteArray();
        var again = Status.Parser.ParseFrom(bytes);
        Assert.Equal(42, again.Health.CpuPct);
        Assert.Equal(85, again.Health.Limits.HighPct);
        Assert.Equal(2ul, again.Health.FramesMissed);
        // the fields of before keep their numbers: a reader that knows only them skips field 16
        Assert.Equal(old.ToByteArray(), bytes[..old.CalculateSize()]);
    }
}
