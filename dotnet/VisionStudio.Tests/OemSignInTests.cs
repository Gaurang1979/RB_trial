using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using VisionCore.Licensing;
using VisionStudio.Services;
using Xunit;

/// <summary>Service and OEM roles only with today's code (Req 17.10 MRG-09 item 4), and the commissioning command.</summary>
public class OemSignInTests
{
    // Test-only secrets; the real ones are set at commissioning, never in the repository.
    private const string Service = "test-only-secret-0123456789";
    private const string Factory = "test-only-factory-0123456789";
    private const string Lic = "VS-PRO-GJ04-98214";
    private const string Day = "2026-11-03";

    private static OemSignIn.Outcome Decide(string[] roles, string? otp, string? lic = Lic, string? service = Service, string? factory = Factory)
        => OemSignIn.Decide(roles, otp, lic, service, factory, Day);

    [Fact]
    public void ServiceRoleNeedsTodaysServiceCode()
    {
        var today = OemOtpVerifier.CalculateDailyOtp(Lic, Day, Service)!;
        var yesterday = OemOtpVerifier.CalculateDailyOtp(Lic, "2026-11-02", Service)!;
        var both = new[] { Roles.Engineer, Roles.Service };

        Assert.Equal(both, Decide(both, today).Roles);
        Assert.Equal([Roles.Service], Decide(both, today).Granted);
        foreach (var bad in new[] { null, "", yesterday, today == "000000" ? "111111" : "000000",
                                    OemOtpVerifier.CalculateFactoryOtp(Lic, Day, Factory) })
        {
            var o = Decide(both, bad);
            Assert.Equal([Roles.Engineer], o.Roles);
            Assert.Equal([Roles.Service], o.Refused);
        }
    }

    [Fact]
    public void OemRoleNeedsTodaysFactoryCode_NotTheServiceCode()
    {
        var service = OemOtpVerifier.CalculateDailyOtp(Lic, Day, Service)!;
        var factory = OemOtpVerifier.CalculateFactoryOtp(Lic, Day, Factory)!;
        Assert.Empty(Decide([Roles.OEM], service).Roles);
        Assert.Equal([Roles.OEM], Decide([Roles.OEM], factory).Roles);
        // one code opens its own tier only
        var o = Decide([Roles.Service, Roles.OEM], factory);
        Assert.Equal([Roles.OEM], o.Roles);
        Assert.Equal([Roles.Service], o.Refused);
    }

    [Fact]
    public void NoLicenceOrNoSecretOpensNothing()
    {
        var code = OemOtpVerifier.CalculateDailyOtp(Lic, Day, Service)!;
        Assert.Empty(Decide([Roles.Service], code, lic: "").Roles);
        Assert.Empty(Decide([Roles.Service], code, lic: null).Roles);
        Assert.Empty(Decide([Roles.Service], code, service: null).Roles);
        Assert.Empty(Decide([Roles.Service], code, service: "short").Roles);
    }

    [Fact]
    public void OtherRolesNeedNoCode()
    {
        var o = Decide([Roles.Operator, Roles.Supervisor, Roles.Engineer, Roles.QA], null);
        Assert.Equal(4, o.Roles.Count);
        Assert.Empty(o.Refused);
    }

    [Fact]
    public void FiveWrongCodesLockTheUsersCodesFor15Minutes()
    {
        var now = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        var a = OtpAttempts.InMemory(() => now);
        var right = OemOtpVerifier.CalculateDailyOtp(Lic, Day, Service)!;
        var wrong = right == "000000" ? "111111" : "000000";
        OemSignIn.Outcome Try(string user, string? otp) => a.Decide(user, [Roles.Service], otp, Lic, Service, Factory, Day);

        Assert.Empty(Try("svc", null).Roles);                  // no code given: not counted
        for (int i = 0; i < OtpAttempts.MaxFailures; i++) Assert.False(Try("svc", wrong).Locked);
        Assert.True(a.Locked("SVC"));
        var locked = Try("svc", right);                         // the right code is not even checked
        Assert.True(locked.Locked);
        Assert.Empty(locked.Roles);
        Assert.Equal([Roles.Service], Try("other", right).Roles);   // another user is not locked

        now += OtpAttempts.Window;
        Assert.False(a.Locked("svc"));
        Assert.Equal([Roles.Service], Try("svc", right).Roles);
        for (int i = 0; i < OtpAttempts.MaxFailures - 1; i++) Try("svc", wrong);
        Assert.Equal([Roles.Service], Try("svc", right).Roles);    // a right code clears the count
        Try("svc", wrong);
        Assert.False(a.Locked("svc"));
    }

    [Fact]
    public void A_user_with_both_roles_cannot_clear_guesses_at_one_code_with_the_other()
    {
        var now = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        var a = OtpAttempts.InMemory(() => now);
        var service = OemOtpVerifier.CalculateDailyOtp(Lic, Day, Service)!;
        var factory = OemOtpVerifier.CalculateFactoryOtp(Lic, Day, Factory)!;
        var wrong = service == "000000" || factory == "000000" ? "111111" : "000000";
        OemSignIn.Outcome Try(string? otp) => a.Decide("both", [Roles.Service, Roles.OEM], otp, Lic, Service, Factory, Day);

        for (int i = 0; i < OtpAttempts.MaxFailures - 1; i++) Try(wrong);
        Assert.Equal([Roles.Service], Try(service).Granted);   // the known code still opens its role ...
        Assert.False(a.Locked("both"));
        Try(wrong);                                              // ... but the guesses before it still count
        Assert.True(a.Locked("both"));
        Assert.True(Try(factory).Locked);
    }

    /// <summary>Like DPAPI: the blob cannot be read as text, and an edited blob throws.</summary>
    private sealed class SealProtector : ISecretProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public bool FailProtect;
        public byte[] Protect(byte[] plain)
        {
            if (FailProtect) throw new CryptographicException("test: cannot protect");
            var body = plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
            return [.. HMACSHA256.HashData(_key, body), .. body];
        }
        public byte[] Unprotect(byte[] blob)
        {
            if (blob.Length < 32 || !HMACSHA256.HashData(_key, blob[32..]).AsSpan().SequenceEqual(blob.AsSpan(0, 32)))
                throw new CryptographicException("test: damaged");
            return blob[32..].Select(b => (byte)(b ^ 0x5A)).ToArray();
        }
    }

    /// <summary>A data folder with a settled attempts file (first start's hold passed) and a movable clock.</summary>
    private sealed class Station : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "vs-otp-attempts-" + Guid.NewGuid().ToString("N"));
        public readonly SealProtector Protector = new();
        public DateTime Now = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        public readonly string Right = OemOtpVerifier.CalculateDailyOtp(Lic, Day, Service)!;
        public string Wrong => Right == "000000" ? "111111" : "000000";
        public string FilePath => Path.Combine(Dir, OtpAttempts.FileName);
        public OtpAttempts Start() => new(Dir, Protector, () => Now);
        public Station()
        {
            Assert.NotNull(Start().Hold);   // first start: no file yet, so codes are held once
            Now += OtpAttempts.Window;
        }
        public static OemSignIn.Outcome Try(OtpAttempts a, string user, string? otp, params string[] roles)
            => a.Decide(user, roles.Length == 0 ? [Roles.Service] : roles, otp, Lic, Service, Factory, Day);
        public void Dispose() { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
    }

    [Fact]
    public void ARestartKeepsTheLock()
    {
        using var st = new Station();
        var a = st.Start();
        Assert.Null(a.Hold);
        for (int i = 0; i < OtpAttempts.MaxFailures; i++) Station.Try(a, "svc", st.Wrong);
        Assert.True(a.Locked("svc"));

        var b = st.Start();                                         // Vision Studio restarted
        Assert.Null(b.Hold);
        Assert.True(b.Locked("svc"));
        Assert.True(Station.Try(b, "svc", st.Right).Locked);
        Assert.Equal([Roles.Service], Station.Try(b, "other", st.Right).Roles);
        var bytes = File.ReadAllBytes(st.FilePath);                 // neither user names nor times readable
        Assert.Equal(-1, bytes.AsSpan().IndexOf("svc"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("2026"u8));

        st.Now += OtpAttempts.Window;
        var c = st.Start();
        Assert.False(c.Locked("svc"));
        Assert.Equal([Roles.Service], Station.Try(c, "svc", st.Right).Roles);
    }

    [Fact]
    public void ARightCodeClearsTheCountOnDisk()
    {
        using var st = new Station();
        var a = st.Start();
        for (int i = 0; i < OtpAttempts.MaxFailures - 1; i++) Station.Try(a, "svc", st.Wrong);
        Assert.Equal([Roles.Service], Station.Try(a, "svc", st.Right).Roles);
        var b = st.Start();
        Station.Try(b, "svc", st.Wrong);
        Assert.False(b.Locked("svc"));
    }

    [Fact]
    public void ADeletedFileHoldsEveryCodeAcrossARestart()
    {
        using var st = new Station();
        var a = st.Start();
        for (int i = 0; i < OtpAttempts.MaxFailures; i++) Station.Try(a, "svc", st.Wrong);
        File.Delete(st.FilePath);

        var b = st.Start();
        Assert.Contains("missing", b.Hold);
        Assert.True(Station.Try(b, "svc", st.Right).Locked);
        Assert.True(Station.Try(b, "other", st.Right).Locked);      // every user's codes, not only the locked one
        var mixed = Station.Try(b, "other", st.Right, Roles.Engineer, Roles.Service);
        Assert.Equal([Roles.Engineer], mixed.Roles);                // the other roles still sign in

        st.Now += TimeSpan.FromMinutes(10);
        Assert.True(st.Start().Locked("other"));                    // the hold was written: a restart does not end it
        st.Now += TimeSpan.FromMinutes(5);
        var c = st.Start();
        Assert.Null(c.Hold);
        Assert.Equal([Roles.Service], Station.Try(c, "svc", st.Right).Roles);
    }

    [Fact]
    public void AnEditedFileHoldsEveryCodeAndIsKeptForInspection()
    {
        using var st = new Station();
        Station.Try(st.Start(), "svc", st.Wrong);
        var bytes = File.ReadAllBytes(st.FilePath);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(st.FilePath, bytes);

        var b = st.Start();
        Assert.Contains("cannot be opened", b.Hold);
        Assert.True(Station.Try(b, "other", st.Right).Locked);
        Assert.True(File.Exists(st.FilePath + ".bad"));
        Assert.True(st.Start().Locked("other"));                    // rewritten with the hold
    }

    [Fact]
    public void TheRuntimesRemoteCountAndVisionStudiosShareAFolderWithoutHoldingEachOther()
    {
        // Vision Studio and the runtime run side by side on one data folder (REQ-11): each keeps its own file
        using var st = new Station();
        var studio = st.Start();
        var remote = new OtpAttempts(st.Dir, st.Protector, () => st.Now, fileName: OtpAttempts.RemoteFileName);
        Assert.NotNull(remote.Hold);                       // its own first start holds once, like Vision Studio's
        st.Now += OtpAttempts.Window;
        remote = new OtpAttempts(st.Dir, st.Protector, () => st.Now, fileName: OtpAttempts.RemoteFileName);
        Assert.Null(remote.Hold);
        Assert.True(File.Exists(Path.Combine(st.Dir, OtpAttempts.RemoteFileName)));

        Assert.Empty(Station.Try(remote, "remote-support", st.Wrong).Granted);
        Assert.Equal([Roles.Service], Station.Try(studio, "svc", st.Right).Granted);   // a remote try never holds a local code
        Assert.Empty(Station.Try(studio, "svc2", st.Wrong).Granted);
        Assert.Equal([Roles.Service], Station.Try(remote, "remote-support", st.Right).Granted);   // nor a local try a remote one
        Assert.Null(studio.Hold);
        Assert.Null(remote.Hold);
    }

    [Fact]
    public void AFileFromAnotherPcHoldsEveryCode()
    {
        using var st = new Station();
        Station.Try(st.Start(), "svc", st.Wrong);
        var other = new OtpAttempts(st.Dir, new SealProtector(), () => st.Now);   // another PC's key
        Assert.Contains("cannot be opened", other.Hold);
        Assert.True(other.Locked("anyone"));
    }

    [Fact]
    public void DeletingOrPuttingBackTheFileWhileRunningHoldsEveryCode()
    {
        using var st = new Station();
        var a = st.Start();
        File.Delete(st.FilePath);
        Assert.True(Station.Try(a, "svc", st.Right).Locked);
        Assert.Contains("deleted or changed", a.Hold);

        st.Now += OtpAttempts.Window;
        var b = st.Start();
        var clean = File.ReadAllBytes(st.FilePath);
        for (int i = 0; i < OtpAttempts.MaxFailures; i++) Station.Try(b, "svc", st.Wrong);
        File.WriteAllBytes(st.FilePath, clean);                     // an older copy put back to clear the count
        Assert.True(Station.Try(b, "svc", st.Right).Locked);
        Assert.True(Station.Try(b, "other", st.Right).Locked);
        Assert.True(st.Start().Locked("other"));
    }

    [Fact]
    public void AFileThatCannotBeWrittenHoldsEveryCode()
    {
        using var st = new Station();
        var a = st.Start();
        st.Protector.FailProtect = true;
        Assert.Empty(Station.Try(a, "svc", st.Wrong).Roles);        // the wrong code could not be stored
        Assert.Contains("cannot be written", a.Hold);
        Assert.True(Station.Try(a, "other", st.Right).Locked);
        st.Protector.FailProtect = false;
        st.Now += OtpAttempts.Window;
        Assert.Equal([Roles.Service], Station.Try(a, "other", st.Right).Roles);
    }

    [Fact]
    public void VisionStudioGetsTheStoreInItsDataFolder()
    {
        using var st = new Station();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<StudioOptions>(o => o.DataFolder = st.Dir);
        services.AddSingleton<OtpAttempts>();                      // as Program.cs
        using var sp = services.BuildServiceProvider();
        var a = sp.GetRequiredService<OtpAttempts>();
        // DPAPI on Windows keeps signin.dat; without it (this bench PC) the count is in memory only
        Assert.Equal(OperatingSystem.IsWindows(), a.Hold is not null);
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] blob) => Protect(blob);
    }

    [Fact]
    public void SecretsCommandReadsTheValueFromStandardInputAndNeverPrintsIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-secrets-cmd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CommissioningSecrets(dir, new XorProtector(), _ => null);
            int Run(string input, params string[] args)
            {
                var o = new StringWriter(); var e = new StringWriter();
                var rc = SecretsCommand.Run(args, store, new StringReader(input), o, e);
                Assert.DoesNotContain(Service, o + e.ToString());
                if (store.LedgerKeyBase64 is { } k) Assert.DoesNotContain(k, o + e.ToString());
                return rc;
            }
            Assert.Equal(0, Run(Service + "\n", "set", CommissioningSecrets.ServiceOtpSecret));
            Assert.Equal(Service, store.ServiceOtp);
            Assert.Equal(1, Run("short\n", "set", CommissioningSecrets.FactoryOtpSecret));
            Assert.Equal(0, Run("", "new-ledger-key"));
            Assert.Equal(1, Run("", "new-ledger-key"));
            Assert.Equal(0, Run("", "new-ledger-key", "--replace"));
            Assert.Equal(0, Run("", "status"));
            Assert.Equal(2, Run("", "set"));
            Assert.Equal(2, Run(""));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
