using System.Text.Json;
using PlantEvents;
using Xunit;

public class PlantEventTests
{
    static T Where<T>(T e) where T : PlantEvent =>
        e with { Plant = "P1", Line = "L4", Station = "S1", Ts = 1_793_280_000_000, Seq = 7, QueueId = "q1", Job = "Carton-30", JobVersion = 7 };

    public static IEnumerable<object[]> EveryKind() => new PlantEvent[]
    {
        Where(new InspectionResult { Ok = false, PartId = "0108901234567890", Reasons = [new("caliper", "too_large")],
            Values = new Dictionary<string, double> { ["label_offset_mm"] = 0.56 }, CycleMs = 41.5,
            Models = new Dictionary<string, string> { ["print"] = "ocrnet-12" }, ImageId = "img-1", Camera = "cam2" }),
        Where(new LineState { State = LineStateValue.Changeover, Reason = "product_change" }),
        Where(new CounterSnapshot { Good = 10, Bad = 2, Total = 12, ShiftStart = 1_793_250_000_000 }),
        Where(new AlarmRaised { Code = "cam_dark", Priority = AlarmPriority.High }),
        Where(new AlarmCleared { Code = "cam_dark" }),
        Where(new AlarmAcknowledged { Code = "cam_dark", By = "ravi" }),
        Where(new JobChanged { From = "Carton-30", To = "Carton-24", By = "ravi" }),
        Where(new ModelDeployed { Tool = "print", ModelVersion = "ocrnet-13", By = "admin", Validation = "0 missed NG" }),
        Where(new HealthSample { FocusScore = 0.94, CpuPct = 31, DiskPct = 81, FrameDrops = 0 }),
        Where(new Heartbeat { AgentVersion = "1.0.0", QueueDepth = 3 }),
    }.Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void EveryKindRoundTripsAndIsValid(PlantEvent e)
    {
        Assert.Empty(e.Validate());
        var back = PlantEventJson.Parse(PlantEventJson.Serialize(e));
        Assert.Equal(e.GetType(), back.GetType());
        // records compare collections by reference, so compare the wire form
        Assert.Equal(PlantEventJson.Serialize(e), PlantEventJson.Serialize(back));
    }

    [Fact]
    public void EveryKnownKindIsCovered()
    {
        var tested = EveryKind().Select(o => ((PlantEvent)o[0]).Kind).ToHashSet();
        Assert.Equal(PlantEventJson.KnownKinds.OrderBy(k => k), tested.OrderBy(k => k));
    }

    [Fact]
    public void WireFormIsSnakeCaseWithKindFirst()
    {
        var json = PlantEventJson.Serialize(Where(new InspectionResult { Ok = true, CycleMs = 12, PartId = "A1" }));
        Assert.StartsWith("{\"kind\":\"inspection.result\",\"schema\":1,", json);
        using var d = JsonDocument.Parse(json);
        Assert.Equal(7, d.RootElement.GetProperty("job_version").GetInt32());
        Assert.Equal("A1", d.RootElement.GetProperty("part_id").GetString());
        Assert.Equal(12, d.RootElement.GetProperty("cycle_ms").GetDouble());
        Assert.False(d.RootElement.TryGetProperty("camera", out _)); // nulls are left out
    }

    [Fact]
    public void EnumsAreSnakeCaseStrings()
    {
        var json = PlantEventJson.Serialize(Where(new AlarmRaised { Code = "plc_lost", Priority = AlarmPriority.High }));
        Assert.Contains("\"priority\":\"high\"", json);
        Assert.Contains("\"state\":\"changeover\"", PlantEventJson.Serialize(Where(new LineState { State = LineStateValue.Changeover })));
    }

    [Fact]
    public void ValueNamesKeepTheirCase()
    {
        var json = PlantEventJson.Serialize(Where(new InspectionResult { Ok = true, Values = new Dictionary<string, double> { ["LabelOffset"] = 1 } }));
        Assert.Contains("\"LabelOffset\":1", json);
    }

    [Fact]
    public void KindMayComeAnywhereInTheObject()
    {
        var e = PlantEventJson.Parse("""{"schema":1,"plant":"P1","line":"L1","station":"S1","ts":5,"seq":1,"queue_id":"q","code":"x","kind":"alarm.raised"}""");
        Assert.IsType<AlarmRaised>(e);
        Assert.Equal("x", ((AlarmRaised)e).Code);
    }

    [Theory]
    [InlineData("""{"kind":"camera.exploded","schema":1}""", "unknown kind")]
    [InlineData("""{"kind":"heartbeat","schema":2}""", "schema 2 is not supported")]
    [InlineData("""{"kind":"heartbeat"}""", "no schema")]
    [InlineData("""{"schema":1}""", "no kind")]
    [InlineData("""[1,2]""", "not a JSON object")]
    [InlineData("""not json""", "not JSON")]
    [InlineData("""{"kind":"line.state","schema":1,"state":"exploded"}""", "bad line.state")]
    [InlineData("""{"kind":"line.state","schema":1,"state":42}""", "bad line.state")]
    [InlineData("""{"kind":"line.state","schema":1}""", "bad line.state")]
    [InlineData("""{"kind":"inspection.result","schema":1,"cycle_ms":3}""", "bad inspection.result")]
    [InlineData("""{"kind":"counter.snapshot","schema":1,"good":1}""", "bad counter.snapshot")]
    public void RefusesWhatItCannotRead(string json, string why)
    {
        var ex = Assert.Throws<PlantEventException>(() => PlantEventJson.Parse(json));
        Assert.Contains(why, ex.Message);
    }

    [Fact]
    public void NullCollectionsAreProblemsNotCrashes()
    {
        var e = PlantEventJson.Parse("""{"kind":"inspection.result","schema":1,"plant":"P","line":"L","station":"S","ts":5,"seq":1,"queue_id":"q","ok":false,"reasons":null}""");
        Assert.Contains("reasons, values, scores and models may not be null", e.Validate());
        var f = PlantEventJson.Parse("""{"kind":"inspection.result","schema":1,"plant":"P","line":"L","station":"S","ts":5,"seq":1,"queue_id":"q","ok":false,"reasons":[null]}""");
        Assert.Contains("an NG reason is null", f.Validate());
    }

    [Fact]
    public void NumbersMustBeFinite()
    {
        var p = Where(new InspectionResult { Ok = true, CycleMs = double.PositiveInfinity, Values = new Dictionary<string, double> { ["w"] = double.NaN } }).Validate();
        Assert.Contains("cycle_ms is not a number of 0 or more", p);
        Assert.Contains("values[w] is not a finite number", p);
        Assert.Contains("temp_c is not a finite number", Where(new HealthSample { TempC = double.NaN }).Validate());
    }

    [Fact]
    public void UndefinedEnumValuesAreProblems()
    {
        Assert.Contains("state 42 is not a known value", Where(new LineState { State = (LineStateValue)42 }).Validate());
    }

    [Fact]
    public void AStoredEventNeedsItsQueueId()
    {
        Assert.Contains("a stored event has no queue_id", Where(new LineState()) with { QueueId = null } is var e ? e.Validate() : []);
    }

    [Fact]
    public void TimeMustBeInMilliseconds()
    {
        Assert.Contains("ts is not in milliseconds (past year 9999)", Where(new LineState()) with { Ts = 1_793_280_000_000_000 } is var e ? e.Validate() : []);
    }

    [Fact]
    public void AnNgMustSayWhy()
    {
        Assert.Contains("an NG result has no reason", Where(new InspectionResult { Ok = false }).Validate());
    }

    [Fact]
    public void AnOkCannotCarryNgReasons()
    {
        Assert.Contains("an OK result has NG reasons", Where(new InspectionResult { Ok = true, Reasons = [new("ocv", "wrong_char")] }).Validate());
    }

    [Fact]
    public void WhereAndWhenAreRequired()
    {
        var p = new LineState { State = LineStateValue.Running }.Validate();
        Assert.Contains("plant is empty", p);
        Assert.Contains("line is empty", p);
        Assert.Contains("station is empty", p);
        Assert.Contains("ts is not set", p);
        Assert.Contains("seq is not set", p);
    }

    [Fact]
    public void HeartbeatNeedsNoSequenceOfItsOwn()
    {
        Assert.Empty(Where(new Heartbeat { AgentVersion = "1" }).with0Seq().Validate());
    }

    [Fact]
    public void CountersMustAddUp()
    {
        Assert.Contains("total is not good + bad", Where(new CounterSnapshot { Good = 1, Bad = 1, Total = 3 }).Validate());
    }

    [Fact]
    public void PercentagesStayInRange()
    {
        Assert.Contains("disk_pct 101 is not 0-100", Where(new HealthSample { DiskPct = 101 }).Validate());
    }

    [Fact]
    public void AnAcknowledgementNamesTheUser()
    {
        Assert.Contains("an acknowledgement has no user", Where(new AlarmAcknowledged { Code = "x" }).Validate());
    }
}

static class TestExt
{
    public static PlantEvent with0Seq(this Heartbeat h) => h with { Seq = 0 };
}

// CHANGEOVER-VERIFY.md section 2: the plant server works out the same code from the same record.
public class ChangeoverAskTests
{
    [Fact]
    public void CodeMatchesTheDocumentsExample() =>
        Assert.Equal("70e5b0358694252eab8049bde500d878b553fc253f21668b732f45a25fb04c7c",
            ChangeoverAsk.CodeOf("A", 7, "", [new ChangeoverField("lot", "Lot", "L1")]));

    [Fact]
    public void HeartbeatCarriesTheChangeoverInSnakeCaseAndChecksIt()
    {
        var a = ChangeoverAsk.Of("A", 7, "", [new ChangeoverField("lot", "Lot", "L1")], 3, "ravi", 5);
        var hb = new Heartbeat { Plant = "P1", Line = "L1", Station = "S1", Ts = 1_793_280_000_000, Changeover = a };
        Assert.Empty(hb.Validate());
        var json = PlantEventJson.Serialize(hb);
        Assert.Contains("\"changeover\":{\"code\":\"70e5b035", json);
        Assert.Contains("\"job_version\":7", json);
        Assert.Contains("\"changed_at\":5", json);
        Assert.Contains("changeover.code does not match its record", (hb with { Changeover = a with { Job = "B" } }).Validate());
        Assert.Contains("changeover.ask is below 1", (hb with { Changeover = a with { Ask = 0 } }).Validate());
    }
}

public class ChangeoverDigestTests
{
    static readonly ChangeoverField[] Lot = [new ChangeoverField("lot", "Lot", "L1")];
    const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void NoDigestKeepsTheOldCodeAndADigestChangesIt()
    {
        Assert.Equal(ChangeoverAsk.CodeOf("A", 7, "", Lot), ChangeoverAsk.CodeOf("A", 7, "", Lot, ""));
        var withDigest = ChangeoverAsk.CodeOf("A", 7, "", Lot, A);
        Assert.NotEqual(ChangeoverAsk.CodeOf("A", 7, "", Lot), withDigest);
        Assert.NotEqual(withDigest, ChangeoverAsk.CodeOf("A", 7, "", Lot, new string('b', 64)));
        // the same bytes the plant server makes: the canonical form, then 64: and the digest
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("1:A1:70:3:lot3:Lot2:L1" + "64:" + A))), withDigest);
    }

    [Fact]
    public void HeartbeatCarriesTheDigestAndRefusesABadOne()
    {
        var a = ChangeoverAsk.Of("A", 7, "", Lot, 3, "ravi", 5, A);
        var hb = new Heartbeat { Plant = "P1", Line = "L1", Station = "S1", Ts = 1_793_280_000_000, Changeover = a };
        Assert.Empty(hb.Validate());
        Assert.Contains($"\"job_digest\":\"{A}\"", PlantEventJson.Serialize(hb));
        Assert.Contains("changeover.code does not match its record", (hb with { Changeover = a with { JobDigest = new string('b', 64) } }).Validate());
        var bad = ChangeoverAsk.Of("A", 7, "", Lot, 3, "ravi", 5, "XYZ");
        Assert.Contains("changeover.job_digest is not empty or 64 lower-case hex", (hb with { Changeover = bad }).Validate());
    }
}

public class ChangeoverLineAcceptTests
{
    [Fact]
    public void TheLineAcceptanceIsSentInSnakeCaseAndIsNotPartOfTheCode()
    {
        var a = ChangeoverAsk.Of("A", 7, "", [new ChangeoverField("lot", "Lot", "L1")], 3, "ravi", 5) with { AcceptedAtLine = new("asha", "Asha Rao", 9) };
        var hb = new Heartbeat { Plant = "P1", Line = "L1", Station = "S1", Ts = 1_793_280_000_000, Changeover = a };
        Assert.Empty(hb.Validate());
        Assert.Contains("\"accepted_at_line\":{\"by\":\"asha\",\"name\":\"Asha Rao\",\"at\":9}", PlantEventJson.Serialize(hb));
        Assert.NotEmpty((hb with { Changeover = a with { AcceptedAtLine = new("", "x", 9) } }).Validate());
    }
}
