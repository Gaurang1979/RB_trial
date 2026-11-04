using VisionStudio.Remote.V1;

namespace VisionStudio.Remote;

/// <summary>
/// Who may change what (ARCHITECTURE.md section 2.2, owner 2026-10-30). The runtime applies these
/// rules to every call; clients only use them to grey out buttons.
/// </summary>
public static class RuntimeAccess
{
    /// <summary>The shortest badge number: what `vs-runtime add-user --card` enrols and what Vision
    /// Studio takes for a card reader's burst are the same.</summary>
    public const int MinCardLength = 6;

    /// <summary>A badge number as stored and compared: trimmed, and in capitals, since a reader that
    /// types hex changes case with Caps Lock.</summary>
    public static string NormalizeCard(string card) => card.Trim().ToUpperInvariant();

    /// <summary>Start, stop, change job or product, trigger, inputs.</summary>
    public static bool MayOperate(ClientKind client, IReadOnlyCollection<Role> roles, bool operateToken = false) =>
        client switch
        {
            // every customer role runs the line; Support and OEM only sort and learn (AS-04h)
            ClientKind.Studio => roles.Any(r => !IsSupport(r)),
            ClientKind.PlantSoftware => operateToken,
            _ => false,                                      // the browser never changes anything
        };

    /// <summary>Jobs, products, tool settings, users and settings: only in Vision Studio.</summary>
    public static bool MayChangeSetup(ClientKind client, IReadOnlyCollection<Role> roles) =>
        client == ClientKind.Studio
        && roles.Any(r => r is Role.Supervisor or Role.Engineer or Role.Admin or Role.Qa);

    /// <summary>The learning session (AS-04c, AS-04h): list and sort the saved pictures, see the reviews, start and stop
    /// learning and its setting. Supervisor, QA, Engineer, Admin, and Support or OEM (opened at sign-in with today's
    /// code), in Vision Studio. Applying a proposal needs <see cref="MayChangeSetup"/>.</summary>
    public static bool MayLearn(ClientKind client, IReadOnlyCollection<Role> roles) =>
        MayChangeSetup(client, roles) || (client == ClientKind.Studio && roles.Any(IsSupport));

    /// <summary>Sundaram's roles (Support, OEM): opened only with today's code (Req 17.10 MRG-09, AS-04h).</summary>
    public static bool IsSupport(Role role) => role is Role.Service or Role.Oem;

    /// <summary>The job designer (UI-UX-SPEC.md 4.7): read, try and save whole jobs. Engineer and
    /// Admin in Vision Studio.</summary>
    public static bool MayDesignJobs(ClientKind client, IReadOnlyCollection<Role> roles) =>
        client == ClientKind.Studio && roles.Any(r => r is Role.Engineer or Role.Admin);

    /// <summary>The PLC screen's test signal (PLC-INTERFACE.md section 7): Engineer and Admin in
    /// Vision Studio, as for the job designer.</summary>
    public static bool MayTestPlc(ClientKind client, IReadOnlyCollection<Role> roles) =>
        client == ClientKind.Studio && roles.Any(r => r is Role.Engineer or Role.Admin);
}
