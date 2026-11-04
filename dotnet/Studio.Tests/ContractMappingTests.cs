using Google.Protobuf;
using VisionSystem.Studio.Runtime;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Studio.Tests;

public class ContractMappingTests
{
    [Fact]
    public void Status_maps_line_speed_budget_and_the_PLC_signals()
    {
        var at = DateTimeOffset.UtcNow;
        var s = new V1.Status { LineSpeedPpm = 225, CycleBudgetMs = 266.7f, PlcConnected = true };
        s.Plc = new V1.PlcState { LinkUp = true, HeartbeatOk = true, RunEnable = false, LastTriggerId = 48213,
            LastResultTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(at), LastResultPass = true, Link = "ModbusTcp :502" };
        var m = GrpcRuntimeClient.Map(s);
        Assert.Equal(225, m.LineSpeedPpm);
        Assert.Equal(266.7, m.CycleBudgetMs, 1);
        Assert.Equal(new PlcSignals(true, true, false, 48213, at, true, "ModbusTcp :502"), m.Plc);

        // an older runtime says nothing: no lines on the charts, no PLC card values
        var old = GrpcRuntimeClient.Map(new V1.Status { LineSpeedPpm = float.NaN, CycleBudgetMs = -5 });
        Assert.Equal((0.0, 0.0), (old.LineSpeedPpm, old.CycleBudgetMs));
        Assert.Null(old.Plc);
        Assert.Null(old.Learning);

        // the learning session (owner, 2026-11-04)
        s.Learning = new V1.LearningState { Enabled = true, IdleMinutes = 5, Idle = true, IdleSeconds = 320, ToSort = 37, MayLearn = true };
        Assert.Equal(new LearningInfo(true, 5, true, 320, 37, true), GrpcRuntimeClient.Map(s).Learning);
    }

    [Fact]
    public void Status_maps_state_product_counters_and_problems()
    {
        var s = new V1.Status
        {
            State = V1.Status.Types.State.Busy, ActiveJob = "Bottle", ActiveVariant = "Whisky 1 L",
            Inspections = 100, Ng = 3, PlcConnected = true, LicenceOk = true,
        };
        s.Cameras.Add(new V1.CameraState { Name = "Top", Connected = true });
        s.Cameras.Add(new V1.CameraState { Name = "Side", Connected = false });
        s.Alarms.Add(new V1.Alarm { Level = V1.Alarm.Types.Level.Warning, Text = "Getting darker." });

        var m = GrpcRuntimeClient.Map(s);
        Assert.Equal(LineState.Running, m.State);
        Assert.Equal("Bottle · Whisky 1 L", m.Product!.Name);
        Assert.Equal((97L, 3L), (m.Counters.Good, m.Counters.Bad));
        Assert.False(m.CameraOk);
        Assert.Equal("Getting darker.", m.Warning);
        Assert.Null(m.Alarm);
    }

    [Fact]
    public void Cameras_batch_and_each_frames_camera_come_through()
    {
        var s = new V1.Status { State = V1.Status.Types.State.Stopped, ActiveJob = "Tablets" };
        s.Cameras.Add(new V1.CameraState { Name = "Cap" });
        s.Cameras.Add(new V1.CameraState { Name = "Label" });
        s.Batch.Add(new V1.BatchField { Key = "BATCH", Label = "BATCH", Example = "A123", Value = "" });
        var m = GrpcRuntimeClient.Map(s);
        Assert.Equal(["Cap", "Label"], m.Cameras);
        Assert.Equal(new BatchEntry("BATCH", "BATCH", "A123", ""), Assert.Single(m.Batch));
        var f = GrpcRuntimeClient.Map(new V1.LiveFrame { InspectionId = 4, Camera = "Label", Pass = false });
        Assert.Equal("Label", f.Camera);
        Assert.Equal(Verdict.Ng, f.Result);
    }

    [Theory]
    [InlineData(V1.Status.Types.State.Ready, LineState.Running)]
    [InlineData(V1.Status.Types.State.Stopped, LineState.Stopped)]
    [InlineData(V1.Status.Types.State.Teach, LineState.Stopped)]
    [InlineData(V1.Status.Types.State.Unspecified, LineState.Stopped)]
    public void Only_ready_and_busy_count_as_running(V1.Status.Types.State state, LineState expected) =>
        Assert.Equal(expected, GrpcRuntimeClient.Map(new V1.Status { State = state }).State);

    [Fact]
    public void An_error_state_is_an_alarm_even_without_text()
    {
        var m = GrpcRuntimeClient.Map(new V1.Status { State = V1.Status.Types.State.Error, PlcConnected = true, LicenceOk = true });
        Assert.Equal("", m.Alarm);
        Assert.Equal("status.alarm", VisionSystem.Studio.ViewModels.PillState.From(LinkState.Connected, m).TextKey);
    }

    [Fact]
    public void Frame_verdict_is_NG_unless_pass_and_duplicate_only_when_nothing_else_failed()
    {
        V1.LiveFrame Frame(bool pass, params string[] colours)
        {
            var f = new V1.LiveFrame { InspectionId = 7, Pass = pass, Width = 640, Height = 480, Jpeg = ByteString.CopyFrom(1, 2) };
            foreach (var c in colours)
            {
                var g = new V1.Graphic { Kind = V1.Graphic.Types.Kind.Polygon, Colour = c };
                g.Points.Add(new V1.Point { X = 1, Y = 2 });
                f.Graphics.Add(g);
            }
            return f;
        }

        Assert.Equal(Verdict.Ok, GrpcRuntimeClient.Map(Frame(true, "status.ok")).Result);
        Assert.Equal(Verdict.Duplicate, GrpcRuntimeClient.Map(Frame(false, "status.ok", "status.duplicate")).Result);
        Assert.Equal(Verdict.Ng, GrpcRuntimeClient.Map(Frame(false, "status.duplicate", "status.ng")).Result);
        Assert.Equal(Verdict.Ng, GrpcRuntimeClient.Map(Frame(false)).Result);   // fail without graphics is still NG
    }

    [Fact]
    public void A_preview_frame_has_no_verdict()
    {
        var preview = new V1.LiveFrame { InspectionId = 0, Pass = false, Width = 640, Height = 480 };
        Assert.Equal(Verdict.None, GrpcRuntimeClient.Map(preview).Result);
    }

    [Theory]
    [InlineData(Grpc.Core.StatusCode.Unavailable, true)]
    [InlineData(Grpc.Core.StatusCode.Internal, true)]
    [InlineData(Grpc.Core.StatusCode.DeadlineExceeded, false)]
    [InlineData(Grpc.Core.StatusCode.FailedPrecondition, false)]
    [InlineData(Grpc.Core.StatusCode.Unimplemented, false)]
    public void Only_transport_failures_break_the_link(Grpc.Core.StatusCode code, bool transport) =>
        Assert.Equal(transport, GrpcRuntimeClient.IsTransport(code));

    [Fact]
    public void A_refusal_of_an_old_token_never_clears_a_newer_one()
    {
        var token = new GrpcRuntimeConnector.SessionToken { Value = "old" };
        token.Value = "new";            // signed in again meanwhile
        token.ClearIf("old");
        Assert.Equal("new", token.Value);
        token.ClearIf("new");
        Assert.Null(token.Value);
    }

    [Fact]
    public void Graphics_keep_their_token_colour_and_rects_become_four_corners()
    {
        var rect = new V1.Graphic { Kind = V1.Graphic.Types.Kind.Rect, Colour = "status.warning", Label = "Size" };
        rect.Points.Add(new V1.Point { X = 10, Y = 20 });
        rect.Points.Add(new V1.Point { X = 30, Y = 50 });
        var g = GrpcRuntimeClient.Map(rect)!;
        Assert.Equal(GraphicKind.Polygon, g.Kind);
        Assert.Equal(Verdict.Warn, g.Verdict);
        Assert.Equal("Size", g.Label);
        Assert.Equal([new(10, 20), new(30, 20), new(30, 50), new(10, 50)], g.Points);

        Assert.Null(GrpcRuntimeClient.Map(new V1.Graphic { Kind = V1.Graphic.Types.Kind.Line }));   // no points
        var unknown = new V1.Graphic { Kind = V1.Graphic.Types.Kind.Cross, Colour = "teal" };
        unknown.Points.Add(new V1.Point());
        Assert.Equal((GraphicKind.Cross, Verdict.Info), (GrpcRuntimeClient.Map(unknown)!.Kind, GrpcRuntimeClient.Map(unknown)!.Verdict));
    }
}
