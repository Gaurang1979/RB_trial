using System.Security.Cryptography;
using System.Text.Json;
using VisionCore.Licensing;
using Xunit;

namespace VisionCore.Tests;

public class LicenceTests
{
    // signed by erpnext/vision_licence/core.py with a throw-away test key (not the real licence key)
    private static (string Public, SignedLicence Signed) Vector()
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "licence-vector.json")));
        return (doc.RootElement.GetProperty("public").GetString()!, doc.RootElement.GetProperty("signed").Deserialize<SignedLicence>()!);
    }

    [Fact]
    public void AnswerSignedByTheServerVerifies()
    {
        var (pub, signed) = Vector();
        var p = signed.Verify(pub);
        Assert.NotNull(p);
        Assert.True(p!.Active);
        Assert.Equal("ACME Foods", p.Customer);
        Assert.Equal("N0NCE", p.Nonce);
        Assert.Equal(new DateTime(2027, 3, 31, 23, 59, 59, DateTimeKind.Utc), p.ValidUntil!.Value.ToUniversalTime());
        Assert.True(p.Devices.Single(d => d.Serial == "CAM-0001").Ok);
        Assert.False(p.Devices.Single(d => d.Serial == "MKT-1").Ok);
        var st = LicenceStatus.Evaluate(p, DateTime.UtcNow, DateTime.UtcNow, false, new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(st.ToolLicensed("ocr"));
        Assert.False(st.ToolLicensed("locate"));
    }

    [Fact]
    public void EditedAnswerOrOtherKeyIsRejected()
    {
        var (pub, signed) = Vector();
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(signed.Payload)).Replace("\"ok\":false", "\"ok\":true");
        var edited = new SignedLicence { Payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)), Signature = signed.Signature };
        Assert.Null(edited.Verify(pub));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(signed.Verify(other.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void OfflineGraceThenExpired()
    {
        var p = new LicencePayload { Status = "active", IssuedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), NextCheckHours = 24, GraceDays = 7 };
        var good = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(LicenceState.Active, LicenceStatus.Evaluate(p, good, good, false, good.AddHours(5)).State);
        Assert.Equal(LicenceState.OfflineGrace, LicenceStatus.Evaluate(p, good, good.AddDays(3), true, good.AddDays(3)).State);
        Assert.Equal(LicenceState.Expired, LicenceStatus.Evaluate(p, good, good.AddDays(8), true, good.AddDays(8)).State);
        Assert.Equal(LicenceState.Invalid, LicenceStatus.Evaluate(p, good, good, false, good.AddDays(-3)).State);   // clock set back
        Assert.Equal(LicenceState.Rejected, LicenceStatus.Evaluate(new LicencePayload { Status = "suspended" }, good, good, false, good).State);
        Assert.Equal(LicenceState.NotActivated, LicenceStatus.Evaluate(null, null, null, false, good).State);
    }
}

