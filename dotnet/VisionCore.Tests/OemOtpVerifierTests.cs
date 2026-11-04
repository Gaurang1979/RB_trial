using VisionCore.Licensing;
using Xunit;

public class OemOtpVerifierTests
{
    // Test-only secrets. The real ones are set on the line PC and in ERPNext's site_config.json, never in the repository.
    private const string Service = "test-only-secret-0123456789";
    private const string Factory = "test-only-factory-0123456789";
    private const string Lic = "VS-PRO-GJ04-98214";
    private const string Day = "2026-11-03";

    [Fact]
    public void MatchesTheVectorsErpNextMakes()
    {
        // The same vectors are in erpnext/vision_licence/tests/test_otp.py: both sides must give these codes.
        Assert.Equal("131100", OemOtpVerifier.CalculateDailyOtp(Lic, Day, Service));
        Assert.Equal("387853", OemOtpVerifier.CalculateDailyOtp(Lic, "2026-11-04", Service));
        Assert.Equal("206240", OemOtpVerifier.CalculateFactoryOtp(Lic, Day, Service));
    }

    [Fact]
    public void VerifiesOnlyTheRightCode()
    {
        var otp = OemOtpVerifier.CalculateDailyOtp(Lic, Day, Service)!;
        Assert.True(OemOtpVerifier.VerifyDailyOtp(Lic, otp, Day, Service));
        Assert.True(OemOtpVerifier.VerifyDailyOtp(Lic, $" {otp} ", Day, Service));
        Assert.False(OemOtpVerifier.VerifyDailyOtp(Lic, otp == "000000" ? "111111" : "000000", Day, Service));
        Assert.False(OemOtpVerifier.VerifyDailyOtp(Lic, "", Day, Service));
        Assert.False(OemOtpVerifier.VerifyDailyOtp(Lic, null, Day, Service));
        Assert.False(OemOtpVerifier.VerifyDailyOtp(Lic, otp, "2026-11-04", Service));
        Assert.NotEqual(otp, OemOtpVerifier.CalculateDailyOtp("VS-PRO-TN01-54321", Day, Service));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void FailsClosedWithoutAUsableSecret(string? secret)
    {
        Assert.Null(OemOtpVerifier.CalculateDailyOtp(Lic, Day, secret));
        Assert.Null(OemOtpVerifier.CalculateFactoryOtp(Lic, Day, secret));
        Assert.False(OemOtpVerifier.VerifyDailyOtp(Lic, "000000", Day, secret));
        Assert.False(OemOtpVerifier.VerifyFactoryOtp(Lic, "000000", Day, secret));
    }

    [Fact]
    public void RefusesABadLicenceOrDate()
    {
        Assert.Null(OemOtpVerifier.CalculateDailyOtp("", Day, Service));
        Assert.Null(OemOtpVerifier.CalculateDailyOtp(Lic, "03-11-2026", Service));
    }

    [Fact]
    public void FactoryCodeIsItsOwn()
    {
        var serviceOtp = OemOtpVerifier.CalculateDailyOtp(Lic, Day, Service)!;
        var factoryOtp = OemOtpVerifier.CalculateFactoryOtp(Lic, Day, Factory)!;
        Assert.NotEqual(serviceOtp, factoryOtp);
        Assert.True(OemOtpVerifier.VerifyFactoryOtp(Lic, factoryOtp, Day, Factory));
        Assert.False(OemOtpVerifier.VerifyFactoryOtp(Lic, serviceOtp, Day, Factory));
    }

    [Fact]
    public void AuditWarningNoticeMatchesRequirement()
    {
        Assert.Contains("Do not share OTP with other users", OemOtpVerifier.AuditWarningNotice);
        Assert.Contains("It will be audited in your name - Risky", OemOtpVerifier.AuditWarningNotice);
    }
}
