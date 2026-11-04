using VisionReports;

namespace VisionStudio.Services;

/// <summary>
/// The results ledger check before production (Req 17.10 REQ-16 item 5, MRG-08): the whole chain of results.db and
/// its chain head are checked; a failure, or a check that cannot run, refuses the line start and raises alarm 801.
/// Line safety (requirements section 12): in doubt the line does not run. Without a ledger key nothing is chained,
/// so nothing can be checked and the line runs (MRG-04).
/// </summary>
public sealed class LedgerGate(ResultLog? log)
{
    /// <summary>The alarm the line is told: ALM-801, database tampering detected.</summary>
    public const uint AlarmCode = 801;

    /// <summary>Why the last check failed; null when it passed or has not run.</summary>
    public string? Failure { get; private set; }

    /// <summary>Checks the ledger now; returns why it failed, or null.</summary>
    public string? Check()
    {
        if (log is null) return Failure = null;
        try { log.VerifyLedger(); Failure = null; }
        catch (DatabaseTamperingException e) { Failure = e.Message; }
        catch (Exception e) { Failure = "The results ledger could not be checked: " + e.Message; }
        return Failure;
    }
}
