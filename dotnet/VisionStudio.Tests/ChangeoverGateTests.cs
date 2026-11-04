using PlantEvents;
using StationAgent;
using VisionCore;
using VisionStudio.Services;
using Xunit;

// CHANGEOVER-VERIFY.md: Start on the cameras waits until a supervisor accepted exactly this changeover.
public sealed class ChangeoverGateTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "vs-co-" + Guid.NewGuid().ToString("N"));
    bool _up = true;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    ChangeoverGate Gate(bool configured = true) => new(_dir, configured, () => _up);

    static Job JobA(int version = 7) => new()
    {
        Name = "A", Version = version,
        Tools = { new TextToolConfig { Name = "Batch", Camera = 1, Mode = TextMode.Check, Lines = [new TextLineSpec { Template = "B.NO {LOT}" }] } },
    };

    static BatchFields Lot(string v) { var b = new BatchFields(); b.Values["LOT"] = v; return b; }

    static ChangeoverAnswer Answer(ChangeoverAsk a, string state, bool required = true) => new(required, a.Code, a.Ask, state, "Priya", 1, state == "refused" ? "wrong lot" : null);

    [Fact]
    public void BeforeAnyAnswerTheCheckIsOnAndStartWaits()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        Assert.Equal(ChangeoverState.Waiting, g.Status().State);
        Assert.NotNull(g.Refusal());
        _up = false;
        Assert.Equal(ChangeoverState.NoServer, g.Status().State);
        Assert.Contains("cannot be reached", g.Refusal());
    }

    [Fact]
    public void WithNoPlantServerSetUpThereIsNoCheck()
    {
        var g = Gate(configured: false);
        g.Update(JobA(), Lot("L1"));
        Assert.Null(g.Refusal());
    }

    [Fact]
    public void AcceptanceOpensOnlyItsOwnRecordAndAChangeNeedsANewOne()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        var first = g.Current!;
        g.OnAnswer(Answer(first, ChangeoverAnswer.Accepted));
        Assert.Null(g.Refusal());

        g.Update(JobA(), Lot("L2"));
        Assert.NotNull(g.Refusal());
        Assert.True(g.Current!.Ask > first.Ask);
        // A late answer about the earlier changeover never opens this one.
        g.OnAnswer(Answer(first, ChangeoverAnswer.Accepted));
        Assert.NotNull(g.Refusal());

        // Back to the first values is a new changeover too.
        g.OnAnswer(Answer(g.Current!, ChangeoverAnswer.Accepted));
        g.Update(JobA(), Lot("L1"));
        Assert.NotNull(g.Refusal());
    }

    [Fact]
    public void AJobFileCopiedByHandWithTheSameNameAndVersionNeedsANewCheck()
    {
        var g = Gate();
        var a = JobA(); a.Digest = new string('a', 64);
        g.Update(a, Lot("L1"));
        g.OnAnswer(Answer(g.Current!, ChangeoverAnswer.Accepted));
        Assert.Null(g.Refusal());
        g.Update(a, Lot("L1"));                                  // the same file again: still accepted
        Assert.Null(g.Refusal());
        var copy = JobA(); copy.Digest = new string('b', 64);    // same name, version and batch, other bytes
        g.Update(copy, Lot("L1"));
        Assert.NotNull(g.Refusal());
        Assert.Equal(copy.Digest, g.Current!.JobDigest);
    }

    [Fact]
    public void ANewJobVersionNeedsANewCheck()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        g.OnAnswer(Answer(g.Current!, ChangeoverAnswer.Accepted));
        g.Update(JobA(version: 8), Lot("L1"));
        Assert.NotNull(g.Refusal());
    }

    [Fact]
    public void AfterARestartTheStoredAcceptanceCountsOfflineAndIsConfirmedOnline()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        g.OnAnswer(Answer(g.Current!, ChangeoverAnswer.Accepted));
        var again = Gate();
        again.Update(JobA(), Lot("L1"));
        Assert.Equal(g.Current!.Ask, again.Current!.Ask);
        // Reachable server: an edited file opens nothing until the server says so again.
        Assert.Equal(ChangeoverState.Waiting, again.Status().State);
        _up = false;
        Assert.Null(again.Refusal());
        _up = true;
        again.OnAnswer(Answer(again.Current!, ChangeoverAnswer.Accepted));
        Assert.Equal(ChangeoverState.Accepted, again.Status().State);
    }

    [Fact]
    public void ThePlantServerTakenOutOfTheSettingsDoesNotEndACheckItAskedFor()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        g.OnAnswer(Answer(g.Current!, ChangeoverAnswer.Pending));
        var unplugged = Gate(configured: false);
        unplugged.Update(JobA(), Lot("L1"));
        Assert.Equal(ChangeoverState.NoServer, unplugged.Status().State);
        Assert.NotNull(unplugged.Refusal());
    }

    [Fact]
    public void ValuesTheServerWouldRefuseAreNotSentAndSayWhy()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1\0"));
        Assert.Null(g.Current);
        Assert.Contains("cannot be sent", g.Refusal());
        g.Update(JobA(), Lot(new string('x', 257)));
        Assert.Null(g.Current);
        g.Update(JobA(), Lot("L1"));
        Assert.NotNull(g.Current);
    }

    [Fact]
    public void TheOperatorIsKeptAndAskAgainNamesWhoAsked()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        Assert.Equal("", g.Current!.Operator);
        g.Update(JobA(), Lot("L1"), op: "ravi");          // the same values entered by a named person
        Assert.Equal("ravi", g.Current!.Operator);
        g.OnAnswer(Answer(g.Current!, ChangeoverAnswer.Refused));
        Assert.True(g.AskAgain("asha"));
        Assert.Equal("asha", g.Current!.Operator);
    }

    [Fact]
    public void ALostFileNeverReusesAnAskNumber()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        var ask = g.Current!.Ask;
        Directory.Delete(_dir, true);
        var fresh = Gate();
        fresh.Update(JobA(), Lot("L1"));
        Assert.True(fresh.Current!.Ask >= ask);
        Assert.True(fresh.Current.Ask > 1_000_000);   // seconds since 2026: never 1 again
    }

    [Fact]
    public void RefusedThenAskedAgainIsANewRequest()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        var first = g.Current!;
        Assert.False(g.AskAgain());   // nothing refused yet
        g.OnAnswer(Answer(first, ChangeoverAnswer.Refused));
        Assert.Equal(ChangeoverState.Refused, g.Status().State);
        Assert.Contains("wrong lot", g.Refusal());
        Assert.True(g.AskAgain());
        Assert.Equal(first.Code, g.Current!.Code);
        Assert.True(g.Current.Ask > first.Ask);
        Assert.Equal(ChangeoverState.Waiting, g.Status().State);
    }

    [Fact]
    public void TheCheckOffForTheLineAllowsStart()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        g.OnAnswer(Answer(g.Current!, ChangeoverAnswer.NotNeeded, required: false));
        Assert.Null(g.Refusal());
        // Turned on again: the same values now wait for a supervisor.
        g.OnAnswer(Answer(g.Current!, ChangeoverAnswer.Pending));
        Assert.NotNull(g.Refusal());
    }

    [Fact]
    public void TheRecordHasEveryBatchFieldAndAValidCode()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"), op: "ravi");
        var a = g.Current!;
        Assert.Equal("A", a.Job);
        Assert.Equal(7, a.JobVersion);
        Assert.Equal("ravi", a.Operator);
        Assert.Equal([new ChangeoverField("LOT", "LOT", "L1")], a.Batch);
        var p = new List<string>();
        a.CheckFields(p);
        Assert.Empty(p);
    }

    // Owner, 2026-11-01: while the plant server cannot be reached, a second person may accept at the line PC.
    [Fact]
    public void ASecondPersonAcceptsAtTheLineOnlyWhileTheServerIsDown()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"), op: "ravi");
        var code = g.Status().ShortCode;
        Assert.NotNull(g.AcceptAtLine(code, "asha", "Asha Rao"));          // server reachable: use the web page or the app
        _up = false;
        Assert.NotNull(g.AcceptAtLine("00000000", "asha", "Asha Rao"));    // not the values they were shown
        Assert.NotNull(g.AcceptAtLine(code, "RAVI", "Ravi"));              // not the one who made the change
        Assert.Null(g.AcceptAtLine(code, "asha", "Asha Rao"));
        var s = g.Status();
        Assert.Equal((ChangeoverState.Accepted, true, "Asha Rao"), (s.State, s.AtLine, s.By));
        Assert.Null(g.Refusal());
        Assert.Equal("asha", g.Current!.AcceptedAtLine!.By);              // sent until the server has it
        var p = new List<string>();
        g.Current.CheckFields(p);
        Assert.Empty(p);
    }

    [Fact]
    public void TheServerRecordingTheAcceptanceStopsSendingItAndARefusalThereWins()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        _up = false;
        Assert.Null(g.AcceptAtLine(g.Status().ShortCode, "asha", "Asha"));
        _up = true;
        g.OnAnswer(Answer(g.Current!, ChangeoverAnswer.Accepted));
        Assert.Null(g.Current!.AcceptedAtLine);
        Assert.Null(g.Refusal());

        var h = Gate();
        h.Update(JobA(), Lot("L2"));
        _up = false;
        Assert.Null(h.AcceptAtLine(h.Status().ShortCode, "asha", "Asha"));
        _up = true;
        h.OnAnswer(Answer(h.Current!, ChangeoverAnswer.Refused));        // refused on the server while only the line was cut off
        Assert.Equal(ChangeoverState.Refused, h.Status().State);
        Assert.NotNull(h.Refusal());
    }

    [Fact]
    public void ANewChangeoverEndsAnAcceptanceAtTheLine()
    {
        var g = Gate();
        g.Update(JobA(), Lot("L1"));
        _up = false;
        Assert.Null(g.AcceptAtLine(g.Status().ShortCode, "asha", "Asha"));
        g.Update(JobA(), Lot("L2"));
        Assert.Equal(ChangeoverState.NoServer, g.Status().State);
        Assert.Null(g.Current!.AcceptedAtLine);
    }
}
