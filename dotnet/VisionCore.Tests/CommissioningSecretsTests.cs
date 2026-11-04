using System.Security.Cryptography;
using System.Text;
using VisionCore.Licensing;
using Xunit;

/// <summary>The commissioning secrets store (Req 17.10 MRG-09), with a test double in place of DPAPI.</summary>
public class CommissioningSecretsTests : IDisposable
{
    // Test-only values. The real ones are set at commissioning on the line PC, never in the repository.
    private const string Service = "test-only-secret-0123456789";
    private const string Factory = "test-only-factory-0123456789";
    private static readonly string Ledger = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs-secrets-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _env = new();

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>AES-GCM with a key of its own, like DPAPI bound to "this PC": another instance cannot unwrap.</summary>
    private sealed class TestProtector : ISecretProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(byte[] plain)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[plain.Length];
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, plain, cipher, tag);
            return [.. nonce, .. tag, .. cipher];
        }
        public byte[] Unprotect(byte[] blob)
        {
            if (blob.Length < 28) throw new CryptographicException("short");
            var plain = new byte[blob.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(blob[..12], blob[28..], blob[12..28], plain);
            return plain;
        }
    }

    private CommissioningSecrets Store(ISecretProtector? p) => new(_dir, p, n => _env.GetValueOrDefault(n));

    [Fact]
    public void ValuesRoundTripAndTheFileHoldsNoNameOrValue()
    {
        var p = new TestProtector();
        var s = Store(p);
        s.Set(CommissioningSecrets.ServiceOtpSecret, Service);
        s.Set(CommissioningSecrets.FactoryOtpSecret, " " + Factory + "\n");
        s.Set(CommissioningSecrets.LedgerKey, Ledger);

        var again = Store(p);
        Assert.Equal(Service, again.ServiceOtp);
        Assert.Equal(Factory, again.FactoryOtp);
        Assert.Equal(Ledger, again.LedgerKeyBase64);
        Assert.Equal("store", again.SourceOf(CommissioningSecrets.ServiceOtpSecret));
        Assert.Null(again.Error);

        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(again.FilePath));
        foreach (var plain in new[] { Service, Factory, Ledger, "VS_", "OTP", "LEDGER" })
            Assert.DoesNotContain(plain, raw);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void ShortOrMalformedValuesAreRefused()
    {
        var s = Store(new TestProtector());
        Assert.Throws<ArgumentException>(() => s.Set(CommissioningSecrets.ServiceOtpSecret, "short"));
        Assert.Throws<ArgumentException>(() => s.Set(CommissioningSecrets.FactoryOtpSecret, ""));
        Assert.Throws<ArgumentException>(() => s.Set(CommissioningSecrets.LedgerKey, Convert.ToBase64String(new byte[31])));
        Assert.Throws<ArgumentException>(() => s.Set(CommissioningSecrets.LedgerKey, "not base64 !!"));
        Assert.Throws<ArgumentException>(() => s.Set("VS_OTHER", Service));
        Assert.False(File.Exists(s.FilePath));
    }

    [Fact]
    public void TheStoreWinsOverTheEnvironment_EnvironmentFillsWhatIsNotStored()
    {
        _env[CommissioningSecrets.ServiceOtpSecret] = "environment-secret-0123456789";
        _env[CommissioningSecrets.FactoryOtpSecret] = "environment-factory-0123456789";
        var s = Store(new TestProtector());
        Assert.Equal("environment", s.SourceOf(CommissioningSecrets.ServiceOtpSecret));
        s.Set(CommissioningSecrets.ServiceOtpSecret, Service);
        Assert.Equal(Service, s.ServiceOtp);
        Assert.Equal("environment-factory-0123456789", s.FactoryOtp);
        Assert.Null(s.LedgerKeyBase64);
        Assert.Null(s.SourceOf(CommissioningSecrets.LedgerKey));
    }

    [Fact]
    public void AStoreThatCannotBeOpenedGivesNoValue_NotEvenTheEnvironments()
    {
        Store(new TestProtector()).Set(CommissioningSecrets.ServiceOtpSecret, Service);
        _env[CommissioningSecrets.ServiceOtpSecret] = "environment-secret-0123456789";

        var otherPc = Store(new TestProtector());           // copied to another PC
        Assert.Null(otherPc.ServiceOtp);
        Assert.NotNull(otherPc.Error);
        Assert.DoesNotContain(Service, otherPc.Error);
        Assert.ThrowsAny<CryptographicException>(() => otherPc.Set(CommissioningSecrets.FactoryOtpSecret, Factory));

        var noProtection = Store(null);                     // a store on a PC with no protection
        Assert.Null(noProtection.ServiceOtp);
        Assert.NotNull(noProtection.Error);

        File.WriteAllBytes(otherPc.FilePath, [1, 2, 3]);    // damaged
        Assert.Null(Store(new TestProtector()).ServiceOtp);
    }

    [Fact]
    public void WithoutProtectionNothingIsStored_TheEnvironmentStillServes()
    {
        _env[CommissioningSecrets.LedgerKey] = Ledger;
        var s = Store(null);
        Assert.False(s.CanStore);
        Assert.Throws<InvalidOperationException>(() => s.Set(CommissioningSecrets.ServiceOtpSecret, Service));
        Assert.False(File.Exists(s.FilePath));
        Assert.Equal(Ledger, s.LedgerKeyBase64);
        Assert.Null(s.Error);
    }

    [Fact]
    public void TheLedgerKeyIsReplacedOnlyWhenAsked()
    {
        var s = Store(new TestProtector());
        s.NewLedgerKey();
        var first = s.LedgerKeyBase64!;
        Assert.Equal(CommissioningSecrets.MinLedgerKeyBytes, Convert.FromBase64String(first).Length);
        Assert.Throws<InvalidOperationException>(() => s.NewLedgerKey());
        s.Set(CommissioningSecrets.LedgerKey, first);       // the same key again is fine
        Assert.Equal(first, s.LedgerKeyBase64);
        s.NewLedgerKey(replace: true);
        Assert.NotEqual(first, s.LedgerKeyBase64);
        s.Set(CommissioningSecrets.ServiceOtpSecret, Service);
        s.Set(CommissioningSecrets.ServiceOtpSecret, Service + "-new");   // OTP secrets rotate freely
        Assert.Equal(Service + "-new", s.ServiceOtp);
    }

    [Fact]
    public void TheSupportLinkTokenIsStoredAndAShortOneIsRefused()
    {
        var s = Store(new TestProtector());
        Assert.Throws<ArgumentException>(() => s.Set(CommissioningSecrets.SupportLinkToken, "too-short"));
        var token = new string('t', CommissioningSecrets.MinSupportLinkTokenChars);
        s.Set(CommissioningSecrets.SupportLinkToken, token);
        Assert.Equal(token, s.SupportLink);
        Assert.Null(s.ServiceOtp);   // a store of its own name, not another secret's
    }

    [Fact]
    public void ForThisPcHasProtectionOnlyOnWindows()
    {
        Assert.Equal(OperatingSystem.IsWindows(), CommissioningSecrets.ForThisPc(_dir).CanStore);
    }
}
